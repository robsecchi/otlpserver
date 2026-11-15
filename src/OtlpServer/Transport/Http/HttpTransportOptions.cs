namespace OtlpServer.Transport.Http;

/// <summary>
/// Size limits for the OTLP/HTTP transport, bound from the "Otlp:Http" configuration section.
/// The twin of <see cref="Grpc.GrpcTransportOptions"/>, with the same defaults, so a batch
/// accepted on one transport is accepted on the other.
///
/// <para>Two limits, because a gzip body has two sizes:</para>
/// <code>
///                      ┌── MaxRequestBodySize ──┐   ┌── MaxDecompressedBodySize ──┐
///  client ──► wire:    gzip bytes (e.g. 98 KiB)  ──► decompressed protobuf (e.g. 10 MiB)
/// </code>
/// <para>
/// Both are enforced WHILE reading (with <see cref="LimitedReadStream"/>), so memory never
/// goes much past the limit, even when the client sends no Content-Length (chunked body)
/// or a gzip bomb. Going over either one answers 413 (non-retryable).
/// </para>
/// </summary>
public sealed class HttpTransportOptions
{
    public const string SectionName = "Otlp:Http";

    private const long DefaultBodySize = 4 * 1024 * 1024; // same as the gRPC default, 4 MiB

    /// <summary>
    /// Largest body accepted, measured on the wire (compressed bytes if the client gzips).
    /// Replaces Kestrel's generic MaxRequestBodySize (30 MB) on the OTLP endpoints.
    /// </summary>
    public long MaxRequestBodySize { get; set; } = DefaultBodySize;

    /// <summary>
    /// Largest body accepted after gzip decompression. Leave it unset to use
    /// <see cref="MaxRequestBodySize"/>.
    /// </summary>
    public long? MaxDecompressedBodySize { get; set; }

    public long EffectiveMaxDecompressedBodySize => MaxDecompressedBodySize ?? MaxRequestBodySize;
}
