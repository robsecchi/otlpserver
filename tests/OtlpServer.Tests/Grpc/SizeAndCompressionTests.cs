using System.IO.Compression;
using Google.Rpc;
using Grpc.Core;
using Grpc.Net.Compression;
using OtlpServer.Tests.Infrastructure;

namespace OtlpServer.Tests.Grpc;

/// <summary>
/// Message size limits and compression. These failures happen inside grpc-dotnet, before
/// LogsGrpcService runs.
/// </summary>
public sealed class SizeAndCompressionTests
{
    private const int Limit = 16 * 1024;

    [Fact]
    public async Task Gzip_request_within_limits_is_accepted()
    {
        await using var server = OtlpTestServer.Start();

        var response = await server.Client.ExportAsync(
            Requests.WithRecords(Requests.ValidRecord(new string('a', 100_000))),
            Requests.Compressed());

        Assert.Null(response.PartialSuccess);
    }

    [Fact]
    public async Task Message_larger_than_max_receive_size_is_resource_exhausted_without_retry_info()
    {
        await using var server = OtlpTestServer.Start(new Dictionary<string, string?>
        {
            ["Otlp:Grpc:MaxReceiveMessageSize"] = Limit.ToString(),
        });

        var ex = await Assert.ThrowsAsync<RpcException>(() => server.Client.ExportAsync(
            Requests.WithRecords(Requests.ValidRecord(new string('a', 4 * Limit)))).ResponseAsync);

        // RESOURCE_EXHAUSTED is retryable ONLY with RetryInfo. Without it the client drops
        // the data, which is right: the same payload would be too big again.
        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
        Assert.Null(ex.GetRpcStatus()?.GetDetail<RetryInfo>());
    }

    [Fact]
    public async Task Gzip_bomb_is_resource_exhausted_even_though_its_wire_size_is_small()
    {
        await using var server = OtlpTestServer.Start(new Dictionary<string, string?>
        {
            ["Otlp:Grpc:MaxReceiveMessageSize"] = Limit.ToString(),
            ["Otlp:Grpc:MaxDecompressedMessageSize"] = Limit.ToString(),
        });

        // ~1 MiB of 'a' compresses to about 1 KiB: well under the wire limit, far over the
        // decompressed limit.
        var ex = await Assert.ThrowsAsync<RpcException>(() => server.Client.ExportAsync(
            Requests.WithRecords(Requests.ValidRecord(new string('a', 1024 * 1024))),
            Requests.Compressed()).ResponseAsync);

        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
        Assert.Contains("Decompressed message exceeds", ex.Status.Detail);
        Assert.Null(ex.GetRpcStatus()?.GetDetail<RetryInfo>());
    }

    [Fact]
    public async Task Unsupported_encoding_is_unimplemented()
    {
        // grpc-dotnet would accept deflate by default; this server registers gzip only.
        await using var server = OtlpTestServer.Start(
            clientCompressionProviders: [new DeflateProvider()]);

        var ex = await Assert.ThrowsAsync<RpcException>(() => server.Client.ExportAsync(
            Requests.WithRecords(Requests.ValidRecord()),
            Requests.Compressed("deflate")).ResponseAsync);

        Assert.Equal(StatusCode.Unimplemented, ex.StatusCode);
    }

    /// <summary>Lets the test client send deflate-compressed requests.</summary>
    private sealed class DeflateProvider : ICompressionProvider
    {
        public string EncodingName => "deflate";

        public Stream CreateCompressionStream(Stream stream, CompressionLevel? compressionLevel) =>
            new DeflateStream(stream, compressionLevel ?? CompressionLevel.Fastest, leaveOpen: true);

        public Stream CreateDecompressionStream(Stream stream) =>
            new DeflateStream(stream, CompressionMode.Decompress);
    }
}
