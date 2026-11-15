namespace OtlpServer.Transport;

/// <summary>
/// Read-only wrapper that counts the bytes read through it and throws as soon as the total
/// goes over a limit.
///
/// <para>
/// Put on the OUTPUT side of a decompression stream, it bounds how many decompressed bytes
/// the reader can ever get, whatever the compression ratio:
/// </para>
/// <code>
///  wire bytes ──► GZipStream ──► LimitedReadStream ──► reader (grpc-dotnet)
///                                 total += bytes read
///                                 total > limit → throw (the reader stops; nothing more is decompressed)
/// </code>
/// <para>
/// Memory stays bounded because the reader pulls data chunk by chunk: at most
/// <c>limit + one read buffer</c> has been produced when the exception is thrown.
/// </para>
/// <para>
/// Nothing here depends on a transport; the caller decides which exception to throw. Used by
/// OTLP/gRPC (decompressed size) and OTLP/HTTP (wire size and decompressed size).
/// </para>
/// </summary>
public sealed class LimitedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maxBytes;
    private readonly Func<long, Exception> _onLimitExceeded;
    private long _totalRead;

    /// <param name="inner">The stream to read from (here: the decompression stream). Owned and disposed by this wrapper.</param>
    /// <param name="maxBytes">Largest total allowed. Exactly <paramref name="maxBytes"/> bytes is still accepted.</param>
    /// <param name="onLimitExceeded">Creates the exception to throw; receives the number of bytes read so far.</param>
    public LimitedReadStream(Stream inner, long maxBytes, Func<long, Exception> onLimitExceeded)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentNullException.ThrowIfNull(onLimitExceeded);

        _inner = inner;
        _maxBytes = maxBytes;
        _onLimitExceeded = onLimitExceeded;
    }

    // ---- Reads: every overload goes through Track() -----------------------------------
    // Stream's default implementations route some overloads through others, but which one
    // goes through which depends on the stream type. Overriding them all means no overload
    // can bypass the count.

    public override int Read(byte[] buffer, int offset, int count) => Track(_inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Track(_inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => Track(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int ReadByte()
    {
        var b = _inner.ReadByte();
        if (b >= 0) Track(1);
        return b;
    }

    private int Track(int bytesRead)
    {
        _totalRead += bytesRead;
        if (_totalRead > _maxBytes)
        {
            throw _onLimitExceeded(_totalRead);
        }
        return bytesRead;
    }

    // ---- The rest: read-only, forward-only -----------------------------------------------

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => _totalRead;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
