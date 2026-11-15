using System.Net;
using Google.Protobuf;
using Google.Rpc;
using OtlpServer.Tests.Infrastructure;
using static OtlpServer.Tests.Infrastructure.OtlpHttpClientExtensions;
using static OtlpServer.Tests.Infrastructure.Requests;

namespace OtlpServer.Tests.Http;

/// <summary>
/// Spec: OTLP/HTTP compression (Content-Encoding: gzip). Plus the two size limits
/// (Otlp:Http:MaxRequestBodySize / MaxDecompressedBodySize), which answer 413: non-retryable,
/// like RESOURCE_EXHAUSTED without RetryInfo on gRPC.
/// </summary>
public sealed class HttpSizeAndCompressionTests
{
    private const int Limit = 16 * 1024;

    private static readonly Dictionary<string, string?> SmallLimits = new()
    {
        ["Otlp:Http:MaxRequestBodySize"] = Limit.ToString(),
        ["Otlp:Http:MaxDecompressedBodySize"] = Limit.ToString(),
    };

    /// <summary>~1 MiB of protobuf that gzip shrinks to ~1 KiB.</summary>
    private static byte[] HighlyCompressibleBody(int size = 1024 * 1024) =>
        WithRecords(ValidRecord(new string('a', size))).ToByteArray();

    // ---- Compression ---------------------------------------------------------------------

    [Theory]
    [InlineData("gzip")]
    [InlineData("GZIP")]   // codings are case-insensitive
    [InlineData("x-gzip")] // old alias HTTP says to treat as gzip
    public async Task Gzip_body_is_accepted(string encoding)
    {
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsAsync(
            ProtobufContent(Gzip(WithRecords(ValidRecord(), RecordWithDuplicateKey()).ToByteArray()), encoding));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Decoded and processed normally: the duplicate-key record is seen and rejected.
        Assert.Equal(1, (await response.ReadExportResponseAsync()).PartialSuccess.RejectedLogRecords);
    }

    [Fact]
    public async Task Explicit_identity_encoding_is_accepted()
    {
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsAsync(
            ProtobufContent(WithRecords(ValidRecord()).ToByteArray(), "identity"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("br")]
    [InlineData("deflate")]
    [InlineData("zstd")]
    [InlineData("gzip, gzip")] // stacked codings
    public async Task Other_encodings_are_415(string encoding)
    {
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsAsync(
            ProtobufContent(WithRecords(ValidRecord()).ToByteArray(), encoding));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        var status = await response.ReadStatusAsync();
        Assert.Equal((int)Code.Unimplemented, status.Code);
        Assert.Contains("Content-Encoding", status.Message);
    }

    [Fact]
    public async Task Corrupt_gzip_is_400()
    {
        await using var server = OtlpTestServer.Start();

        // Plain protobuf, but labelled as gzip.
        using var response = await server.Http.PostLogsAsync(
            ProtobufContent(WithRecords(ValidRecord()).ToByteArray(), "gzip"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var status = await response.ReadStatusAsync();
        Assert.Equal((int)Code.InvalidArgument, status.Code);
        Assert.Contains("not valid gzip", status.Message);
    }

    // ---- Size limits ---------------------------------------------------------------------

    [Theory]
    [InlineData(false)] // Content-Length declared: refused before reading
    [InlineData(true)]  // no Content-Length (chunked): refused while reading
    public async Task Body_over_the_wire_limit_is_413_without_retry_info(bool chunked)
    {
        await using var server = OtlpTestServer.Start(SmallLimits);

        using var response = await server.Http.PostLogsAsync(
            ProtobufContent(WithRecords(ValidRecord(new string('a', 4 * Limit))).ToByteArray(), chunked: chunked));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var status = await response.ReadStatusAsync();
        Assert.Equal((int)Code.ResourceExhausted, status.Code);
        Assert.Contains($"maximum allowed size of {Limit} bytes", status.Message);
        Assert.Null(status.GetDetail<RetryInfo>());
    }

    [Fact]
    public async Task Gzip_bomb_is_413_even_though_its_wire_size_is_small()
    {
        await using var server = OtlpTestServer.Start(SmallLimits);

        var bomb = Gzip(HighlyCompressibleBody());
        Assert.True(bomb.Length < Limit, "the compressed body should fit the wire limit");

        using var response = await server.Http.PostLogsAsync(ProtobufContent(bomb, "gzip"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var status = await response.ReadStatusAsync();
        Assert.Contains("Decompressed request body exceeds", status.Message);
    }

    [Fact]
    public async Task The_two_limits_are_independent()
    {
        // Small on the wire, roomy once decompressed: a large but legitimate gzip batch passes.
        await using var server = OtlpTestServer.Start(new Dictionary<string, string?>
        {
            ["Otlp:Http:MaxRequestBodySize"] = Limit.ToString(),
            ["Otlp:Http:MaxDecompressedBodySize"] = (2 * 1024 * 1024).ToString(),
        });

        using var response = await server.Http.PostLogsAsync(ProtobufContent(Gzip(HighlyCompressibleBody()), "gzip"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Default_limit_is_4_MiB_like_grpc()
    {
        await using var server = OtlpTestServer.Start();

        using var over = await server.Http.PostLogsAsync(
            ProtobufContent(HighlyCompressibleBody(4 * 1024 * 1024 + 1)));
        using var under = await server.Http.PostLogsAsync(
            ProtobufContent(HighlyCompressibleBody(4 * 1024 * 1024 - 100)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        Assert.Equal(HttpStatusCode.OK, under.StatusCode);
    }

    [Theory]
    [InlineData("Otlp:Http:MaxRequestBodySize", "0")]
    [InlineData("Otlp:Http:MaxDecompressedBodySize", "-1")]
    [InlineData("Otlp:Http:MaxRequestBodySize", "3000000000")] // over what one array can hold
    public void Invalid_limits_fail_at_startup(string key, string value)
    {
        var ex = Assert.ThrowsAny<Exception>(() => OtlpTestServer.Start(new Dictionary<string, string?> { [key] = value }));

        Assert.Contains(key.Split(':')[^1], ex.ToString());
    }
}
