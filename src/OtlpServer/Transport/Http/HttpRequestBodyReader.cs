using System.IO.Compression;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.IO;
using OtlpServer.Diagnostics;
using OtlpServer.Logs.Failures;

namespace OtlpServer.Transport.Http;

/// <summary>
/// Reads an OTLP/HTTP request body into memory, decompressing it if needed and enforcing both
/// size limits from <see cref="HttpTransportOptions"/>. Shared by every encoding (protobuf
/// now, JSON later) and every signal.
///
/// <code>
///  Request.Body ──► LimitedReadStream (wire limit) ──┬──────────────────────────────────────────► pooled buffer
///                                                    └─ gzip: GZipStream ──► LimitedReadStream ──┘
///                                                                            (decompressed limit)
/// </code>
/// <para>
/// A declared Content-Length over the wire limit is refused before reading a single byte.
/// Without one (chunked body), or if the client lies, the limit is still enforced while
/// reading.
/// </para>
/// </summary>
internal static class HttpRequestBodyReader
{
    public enum ContentCoding { Identity, Gzip }

    /// <summary>
    /// Reads <c>Content-Encoding</c>. OTLP clients send <c>gzip</c> or nothing. Any other coding
    /// (br, deflate, zstd, several stacked ones) is refused: HTTP says 415 is the answer to a
    /// content coding the server doesn't support.
    /// </summary>
    public static bool TryGetContentCoding(HttpRequest request, out ContentCoding coding, out string unsupportedMessage)
    {
        // The header may come as one comma-separated value or repeated; flatten both forms.
        var codings = request.Headers.ContentEncoding
            .SelectMany(v => (v ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToArray();

        coding = ContentCoding.Identity;
        unsupportedMessage = "";

        switch (codings)
        {
            case []:
                return true;
            case [var only] when only.Equals("identity", StringComparison.OrdinalIgnoreCase):
                return true;
            // "x-gzip" is an old alias that HTTP says recipients SHOULD treat as "gzip".
            case [var only] when only.Equals("gzip", StringComparison.OrdinalIgnoreCase)
                              || only.Equals("x-gzip", StringComparison.OrdinalIgnoreCase):
                coding = ContentCoding.Gzip;
                return true;
            default:
                unsupportedMessage = $"Unsupported Content-Encoding '{string.Join(", ", codings)}'. " +
                                     "Send the body uncompressed or with Content-Encoding: gzip.";
                return false;
        }
    }

    /// <exception cref="OtlpExportException">
    /// TooLarge (413) over either limit; InvalidData (400) if the gzip data is corrupt.
    /// </exception>
    /// <returns>
    /// The body, in POOLED memory: dispose it as soon as it has been decoded, which returns its
    /// blocks to the pool. Read it with <c>GetReadOnlySequence()</c>: no copy.
    /// </returns>
    public static async Task<RecyclableMemoryStream> ReadAsync(
        HttpContext http, ContentCoding coding, HttpTransportOptions options, RecyclableMemoryStreamManager buffers,
        AggregatedWarnings warnings, CancellationToken cancellationToken)
    {
        var maxWire = options.MaxRequestBodySize;

        if (http.Request.ContentLength > maxWire)
        {
            throw WireTooLarge(maxWire);
        }

        // Kestrel has its own body limit (30 MB by default) that would answer a bare 413
        // with no Status body. Ours replaces it on this endpoint, so there is one limit, one
        // place that enforces it, and one response format.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } kestrelLimit)
        {
            kestrelLimit.MaxRequestBodySize = null;
        }

        // Not disposed: it would dispose Request.Body, which belongs to the server.
        var wire = new LimitedReadStream(http.Request.Body, maxWire, _ => WireTooLarge(maxWire));

        // Pooled blocks instead of one growing array: a MemoryStream doubles (and copies) as it
        // grows, and anything over 85 KB lands on the large object heap, which only gen-2 GCs
        // clean up. The S2.1 load test measured the result on this path: 33% of time in GC and
        // ~1,050 gen-2 GCs in 15 s. With a known Content-Length, the blocks are reserved up front.
        var body = buffers.GetStream("otlp-http-body",
            coding == ContentCoding.Identity && http.Request.ContentLength is { } length ? length : 0);
        try
        {
            if (coding == ContentCoding.Gzip)
            {
                var maxDecompressed = options.EffectiveMaxDecompressedBodySize;
                await using var decompressed = new LimitedReadStream(
                    new GZipStream(wire, CompressionMode.Decompress, leaveOpen: true),
                    maxDecompressed,
                    bytesSoFar =>
                    {
                        // Same warning as the gRPC provider: probably a bomb or a misconfigured client.
                        warnings.Report("Rejected gzip body (HTTP): decompressed size over the limit", "HTTP",
                            $"decompressed size exceeded {maxDecompressed} bytes (stopped after {bytesSoFar} bytes)");
                        return OtlpExportException.TooLarge(
                            $"Decompressed request body exceeds the maximum allowed size of {maxDecompressed} bytes. " +
                            "Send smaller batches (e.g. lower the exporter's max batch size).");
                    });
                await decompressed.CopyToAsync(body, cancellationToken);
            }
            else
            {
                await wire.CopyToAsync(body, cancellationToken);
            }

            return body;
        }
        catch (InvalidDataException ex)
        {
            await body.DisposeAsync();
            // Thrown by GZipStream: not gzip at all, corrupt, or truncated.
            throw OtlpExportException.InvalidData(
                $"Request body is not valid gzip data (Content-Encoding: gzip): {ex.Message}", violations: []);
        }
        catch
        {
            await body.DisposeAsync();
            throw;
        }
    }

    private static OtlpExportException WireTooLarge(long max) =>
        OtlpExportException.TooLarge(
            $"Request body exceeds the maximum allowed size of {max} bytes. " +
            "Send smaller batches (e.g. lower the exporter's max batch size).");
}
