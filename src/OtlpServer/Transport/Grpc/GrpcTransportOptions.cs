namespace OtlpServer.Transport.Grpc;

/// <summary>
/// Size limits for the OTLP/gRPC transport, bound from the "Otlp:Grpc" configuration section.
///
/// <para>There are two limits because a compressed request has two sizes:</para>
/// <code>
///                      ┌── MaxReceiveMessageSize ──┐   ┌── MaxDecompressedMessageSize ──┐
///  client ──► wire:    gzip bytes (e.g. 98 KiB)    ──► decompressed protobuf (e.g. 10 MiB)
/// </code>
/// <para>
/// grpc-dotnet checks <see cref="MaxReceiveMessageSize"/> against the bytes on the wire only.
/// Without the second limit, a small gzip payload can expand to gigabytes in memory (a
/// "decompression bomb"). For an uncompressed request the two sizes are equal, so with the
/// default (both limits equal) the effective cap is the same whether or not the client
/// compresses.
/// </para>
/// </summary>
public sealed class GrpcTransportOptions
{
    public const string SectionName = "Otlp:Grpc";

    private const int DefaultMessageSize = 4 * 1024 * 1024; // the gRPC default, 4 MiB

    /// <summary>
    /// Largest message accepted, measured on the wire (compressed bytes if the client
    /// compresses). grpc-dotnet enforces it and answers RESOURCE_EXHAUSTED (no RetryInfo, so
    /// non-retryable) when it is exceeded.
    /// </summary>
    public int MaxReceiveMessageSize { get; set; } = DefaultMessageSize;

    /// <summary>
    /// Largest message accepted after decompression. Enforced by
    /// <see cref="Compression.BoundedGzipCompressionProvider"/> WHILE decompressing, so memory
    /// never goes much past this value. Leave it unset to use
    /// <see cref="MaxReceiveMessageSize"/>.
    /// </summary>
    public int? MaxDecompressedMessageSize { get; set; }

    public int EffectiveMaxDecompressedMessageSize => MaxDecompressedMessageSize ?? MaxReceiveMessageSize;
}
