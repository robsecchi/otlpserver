using OtlpServer.Transport;

namespace OtlpServer.Tests.Unit;

public sealed class LimitedReadStreamTests
{
    private sealed class LimitExceeded(long bytes) : Exception
    {
        public long Bytes { get; } = bytes;
    }

    private static LimitedReadStream Over(int length, int max) =>
        new(new MemoryStream(new byte[length]), max, bytes => new LimitExceeded(bytes));

    [Fact]
    public async Task Exactly_the_limit_is_accepted()
    {
        await using var stream = Over(length: 1000, max: 1000);

        await stream.CopyToAsync(Stream.Null);

        Assert.Equal(1000, stream.Position);
    }

    [Fact]
    public async Task One_byte_over_the_limit_throws_the_callers_exception()
    {
        await using var stream = Over(length: 1001, max: 1000);

        var ex = await Assert.ThrowsAsync<LimitExceeded>(() => stream.CopyToAsync(Stream.Null));
        Assert.True(ex.Bytes > 1000);
    }

    [Fact]
    public void Sync_and_single_byte_reads_are_counted_too()
    {
        using var stream = Over(length: 3, max: 2);

        Assert.Equal(2, stream.Read(new byte[2], 0, 2));
        Assert.Throws<LimitExceeded>(() => stream.ReadByte());
    }
}
