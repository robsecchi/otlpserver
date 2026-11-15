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
