using System.Globalization;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using OtlpServer.Logs.Failures;
using OtlpServer.Transport.Http;

namespace OtlpServer.Transport;

/// <summary>
/// Writes an OTLP error for a request refused BEFORE its endpoint runs (by authentication,
/// authorization or admission), whichever transport it came on:
/// <list type="bullet">
///   <item>gRPC: a "trailers-only" response. The status goes in the response HEADERS
///         (grpc-status, grpc-message, grpc-status-details-bin) with no body, which is how gRPC
///         reports an error before any message has been exchanged. The request body is never
///         read.</item>
///   <item>HTTP: the status code, Retry-After when given, and a google.rpc.Status body in the
///         request's encoding, as for every other OTLP/HTTP error.</item>
/// </list>
/// </summary>
public static class EarlyResponses
{
    public static bool IsGrpc(HttpRequest request) =>
        request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>For a failure kind the mappers already know (Throttled, QuotaExceeded, ...).</summary>
    public static Task WriteAsync(HttpContext http, OtlpExportException failure) =>
        WriteAsync(http, HttpExportFailureMapper.StatusCodeFor(failure.Kind), ExportFailureStatus.From(failure), failure.RetryAfter);

    public static async Task WriteAsync(HttpContext http, int httpStatus, Google.Rpc.Status status, TimeSpan? retryAfter)
    {
        var response = http.Response;

        if (IsGrpc(http.Request))
        {
            response.StatusCode = StatusCodes.Status200OK; // gRPC errors ride on HTTP 200 + grpc-status
            response.ContentType = "application/grpc";
            response.Headers["grpc-status"] = status.Code.ToString(CultureInfo.InvariantCulture);
            response.Headers["grpc-message"] = Uri.EscapeDataString(status.Message); // gRPC requires percent-encoding
            if (status.Details.Count > 0)
            {
                // "-bin" headers carry base64 binary: the full google.rpc.Status, e.g. with RetryInfo.
                response.Headers["grpc-status-details-bin"] = Convert.ToBase64String(status.ToByteArray());
            }
        }
        else
        {
            if (retryAfter is { } delay)
            {
                // Whole seconds, rounded UP so the client never comes back earlier than asked.
                response.Headers.RetryAfter = Math.Max(0, (int)Math.Ceiling(delay.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            }
            await OtlpHttp.WriteMessageAsync(response, httpStatus, status, OtlpHttp.ErrorEncodingFor(http.Request));
        }

        // Send the answer now, so the client learns the outcome without waiting for the drain.
        await response.CompleteAsync();
        await DrainRequestBodyAsync(http);
    }

    // ---- Draining the unread body -------------------------------------------------------------
    //
    // An early refusal is answered while the client may still be uploading the body. If the
    // request then ends with its body unread, Kestrel RESETS the HTTP/2 stream (RST_STREAM).
    // Under load that means thousands of resets per second on one connection, which is exactly
    // what an HTTP/2 "rapid reset" attack (CVE-2023-44487) looks like. Kestrel's protection then
    // answers ENHANCE_YOUR_CALM and closes the connection: the client sees RESOURCE_EXHAUSTED,
    // INTERNAL and hung calls, and its only connection to us dies. (Found by the S2.1 load test.)
    //
    // So the rest of the body is read and thrown away, and the stream ends normally. That costs
    // network bandwidth (the client is sending those bytes anyway), but no decoding and no
    // allocation (one pooled buffer). It is bounded, so a client can't keep us reading forever:
    // at most the largest body we'd accept, within DrainTimeout. Beyond either, the request is
    // aborted: a reset, but a rare one.

    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private static async Task DrainRequestBodyAsync(HttpContext http)
    {
        var limit = MaxDrainBytes(http);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        timeout.CancelAfter(DrainTimeout);

        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            long total = 0;
            int read;
            while ((read = await http.Request.Body.ReadAsync(buffer, timeout.Token)) > 0)
            {
                total += read;
                if (total > limit)
                {
                    http.Abort(); // more than any acceptable export: stop reading
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            http.Abort(); // too slow, or the client went away
        }
        catch (Exception ex) when (ex is IOException or BadHttpRequestException)
        {
            // The client reset its side or the request was malformed: nothing left to drain.
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The largest body any OTLP endpoint would accept (plus gRPC's 5-byte frame header).</summary>
    private static long MaxDrainBytes(HttpContext http)
    {
        var services = http.RequestServices;
        var grpc = services.GetService<IOptions<Grpc.GrpcTransportOptions>>()?.Value.MaxReceiveMessageSize ?? 4 * 1024 * 1024;
        var httpMax = services.GetService<IOptions<HttpTransportOptions>>()?.Value.MaxRequestBodySize ?? 4 * 1024 * 1024;
        return Math.Max(grpc + 5L, httpMax);
    }
}
