using System.IO.Compression;
using Grpc.Core;
using Grpc.Net.Compression;
using OtlpServer.Logs.Failures;

namespace OtlpServer.Transport.Grpc.Compression;

/// <summary>
/// gzip compression provider for grpc-dotnet that limits how large a message may get when
/// decompressed. Protects against decompression bombs.
///
/// <para>Where it fits in a request:</para>
/// <code>
///  1. Client sends header  grpc-encoding: gzip  and messages with "compressed flag" = 1.
///  2. grpc-dotnet finds the provider whose EncodingName == "gzip" (this class).
///       not found → UNIMPLEMENTED "Unsupported grpc-encoding value ..." (non-retryable)
///  3. grpc-dotnet checks the WIRE length against MaxReceiveMessageSize.
///  4. It reads the decompressed bytes from CreateDecompressionStream() into a buffer:
///       GZipStream ──► LimitedReadStream ──► buffer
///       more than the limit → RpcException(RESOURCE_EXHAUSTED), which grpc-dotnet passes on
///       unchanged as the call's status. Our service method never runs.
///  5. The buffer is parsed into ExportLogsServiceRequest and handed to LogsGrpcService.
/// </code>
/// <para>
/// Why RESOURCE_EXHAUSTED with no RetryInfo: the spec says clients may retry this code ONLY if
/// the server includes RetryInfo. Without it the client drops the data, which is correct,
/// because resending the same oversized payload can't succeed.
/// </para>
/// <para>
/// Registering this as the ONLY provider also makes the server gzip-only. OTLP names gzip
/// as its compression (OTEL_EXPORTER_OTLP_COMPRESSION=gzip|none); grpc-dotnet's built-in
/// list would also accept deflate.
/// </para>
/// </summary>
public sealed class BoundedGzipCompressionProvider : ICompressionProvider
{
    private readonly long _maxDecompressedBytes;
    private readonly ILogger _logger;

    public BoundedGzipCompressionProvider(long maxDecompressedBytes, ILogger<BoundedGzipCompressionProvider> logger)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDecompressedBytes);
        _maxDecompressedBytes = maxDecompressedBytes;
        _logger = logger;
    }

    /// <summary>Matched against the request's <c>grpc-encoding</c> header.</summary>
    public string EncodingName => "gzip";

    /// <summary>
    /// Only used for compressing responses. Response compression is off (no
    /// <c>ResponseCompressionAlgorithm</c> configured), because OTLP responses are tiny.
    /// Implemented anyway, so the provider is complete if that ever changes.
    /// </summary>
    public Stream CreateCompressionStream(Stream stream, CompressionLevel? compressionLevel)
        => new GZipStream(stream, compressionLevel ?? CompressionLevel.Fastest, leaveOpen: true);

    /// <summary>
    /// Called once per compressed request message. A new stream is created each time, so
    /// the byte count starts at zero for every message.
    /// </summary>
    public Stream CreateDecompressionStream(Stream stream)
        => new LimitedReadStream(
            new GZipStream(stream, CompressionMode.Decompress),
            _maxDecompressedBytes,
            OnLimitExceeded);

    private Exception OnLimitExceeded(long bytesSoFar)
    {
        // Probably hostile, or a misconfigured client sending huge batches; either way worth
        // knowing about.
        // TODO: replace (or complement) with a metric. Under attack, one log line per refusal
        // could itself flood the logs.
        _logger.LogWarning(
            "Rejected gzip message: decompressed size exceeded {Limit} bytes (stopped after {BytesSoFar} bytes)",
            _maxDecompressedBytes, bytesSoFar);

        // TooLarge → RESOURCE_EXHAUSTED without RetryInfo (non-retryable), the same failure
        // kind OTLP/HTTP reports as 413.
        return GrpcExportFailureMapper.ToRpcException(OtlpExportException.TooLarge(
            $"Decompressed message exceeds the maximum allowed size of {_maxDecompressedBytes} bytes. " +
            "Send smaller batches (e.g. lower the exporter's max batch size)."));
    }
}
