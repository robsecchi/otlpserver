using System.Net;
using Google.Protobuf;
using Google.Rpc;
using OtlpServer.Tests.Infrastructure;
using static OtlpServer.Tests.Infrastructure.Requests;

namespace OtlpServer.Tests.Http;

/// <summary>
/// HTTP-level rules: Content-Type, path, method. All of these are non-retryable (4xx other
/// than 429), and every 4xx body MUST be a google.rpc.Status.
/// </summary>
public sealed class HttpProtocolTests
{
    private static readonly byte[] ValidBody = WithRecords(ValidRecord()).ToByteArray();

    [Theory]
    [InlineData("application/x-protobuf")]
    [InlineData("Application/X-Protobuf")]           // media types are case-insensitive
    [InlineData("application/x-protobuf; charset=x")] // parameters are allowed
    public async Task Protobuf_content_type_variants_are_accepted(string contentType)
    {
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsAsync(ValidBody, contentType);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/plain")]
    [InlineData("application/protobuf")] // close, but not what OTLP specifies
    [InlineData("not a media type")]
    public async Task Other_content_types_are_415(string? contentType)
    {
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostLogsAsync(ValidBody, contentType);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        var status = await response.ReadStatusAsync();
        Assert.Contains("application/x-protobuf", status.Message);
    }

    [Theory]
    [InlineData("/v1/traces")]  // another signal: not served here
    [InlineData("/v1/metrics")]
    [InlineData("/logs")]
    public async Task Unknown_path_is_404_with_a_status_body(string path)
    {
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.PostAsync(path, new ByteArrayContent(ValidBody));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var status = await response.ReadStatusAsync();
        Assert.Equal((int)Code.Unimplemented, status.Code);
        Assert.Contains("POST /v1/logs", status.Message);
    }

    [Fact]
    public async Task Get_on_the_logs_path_is_405_with_a_status_body()
    {
        await using var server = OtlpTestServer.Start();

        using var response = await server.Http.GetAsync("/v1/logs");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(["POST"], response.Content.Headers.Allow); // required by HTTP on a 405
        Assert.Contains("POST", (await response.ReadStatusAsync()).Message);
    }
}
