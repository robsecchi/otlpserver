using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;

namespace OtlpServer.Tests.Infrastructure;

/// <summary>OTLP/HTTP request/response helpers for the tests.</summary>
public static class OtlpHttpClientExtensions
{
    public const string Protobuf = "application/x-protobuf";

    /// <summary>POST /v1/logs with a serialized request.</summary>
    public static Task<HttpResponseMessage> PostLogsAsync(this HttpClient http, ExportLogsServiceRequest request) =>
        http.PostLogsAsync(request.ToByteArray());

    /// <summary>POST /v1/logs with exact body bytes and Content-Type (null = no header).</summary>
    public static Task<HttpResponseMessage> PostLogsAsync(this HttpClient http, byte[] body, string? contentType = Protobuf)
    {
        var content = new ByteArrayContent(body);
        if (contentType is not null)
        {
            // Without validation, so tests can send exactly what a sloppy client would.
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        return http.PostAsync("/v1/logs", content);
    }

    /// <summary>POST /v1/logs with a prepared body (e.g. gzip, or with no Content-Length).</summary>
    public static Task<HttpResponseMessage> PostLogsAsync(this HttpClient http, HttpContent content) =>
        http.PostAsync("/v1/logs", content);

    /// <summary>A protobuf body, optionally with a Content-Encoding header.</summary>
    public static HttpContent ProtobufContent(byte[] body, string? contentEncoding = null, bool chunked = false)
    {
        HttpContent content = chunked ? new UnknownLengthContent(body) : new ByteArrayContent(body);
        content.Headers.TryAddWithoutValidation("Content-Type", Protobuf);
        if (contentEncoding is not null)
        {
            content.Headers.TryAddWithoutValidation("Content-Encoding", contentEncoding);
        }
        return content;
    }

    public static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Optimal))
        {
            gzip.Write(data);
        }
        return output.ToArray();
    }

    /// <summary>
    /// A body whose length isn't known up front, so no Content-Length header is sent (on a
    /// real connection: Transfer-Encoding: chunked). The server can't check the size before
    /// reading; it has to enforce the limit while reading.
    /// </summary>
    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    public const string Json = "application/json";

    private static readonly JsonParser JsonResponseParser = new(JsonParser.Settings.Default
        .WithTypeRegistry(Google.Protobuf.Reflection.TypeRegistry.FromMessages(
            Google.Rpc.BadRequest.Descriptor, Google.Rpc.RetryInfo.Descriptor)));

    /// <summary>POST /v1/logs with an OTLP/JSON body (optionally gzipped).</summary>
    public static Task<HttpResponseMessage> PostLogsJsonAsync(this HttpClient http, string json, bool gzip = false)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var content = new ByteArrayContent(gzip ? Gzip(bytes) : bytes);
        content.Headers.TryAddWithoutValidation("Content-Type", Json);
        if (gzip) content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
        return http.PostAsync("/v1/logs", content);
    }

    public static async Task<ExportLogsServiceResponse> ReadJsonExportResponseAsync(this HttpResponseMessage response)
    {
        Assert.Equal(Json, response.Content.Headers.ContentType?.MediaType);
        return JsonResponseParser.Parse<ExportLogsServiceResponse>(await response.Content.ReadAsStringAsync());
    }

    public static async Task<Google.Rpc.Status> ReadJsonStatusAsync(this HttpResponseMessage response)
    {
        Assert.Equal(Json, response.Content.Headers.ContentType?.MediaType);
        return JsonResponseParser.Parse<Google.Rpc.Status>(await response.Content.ReadAsStringAsync());
    }

    public static async Task<ExportLogsServiceResponse> ReadExportResponseAsync(this HttpResponseMessage response)
    {
        Assert.Equal(Protobuf, response.Content.Headers.ContentType?.MediaType);
        return ExportLogsServiceResponse.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>Every OTLP/HTTP 4xx / 5xx body MUST be a protobuf google.rpc.Status.</summary>
    public static async Task<Google.Rpc.Status> ReadStatusAsync(this HttpResponseMessage response)
    {
        Assert.Equal(Protobuf, response.Content.Headers.ContentType?.MediaType);
        return Google.Rpc.Status.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync());
    }
}
