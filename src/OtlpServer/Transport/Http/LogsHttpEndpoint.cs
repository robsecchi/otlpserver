using System.Buffers;
using Microsoft.Extensions.Options;
using Microsoft.IO;
using OtlpServer.Diagnostics;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OtlpServer.Logs;
using OtlpServer.Logs.Failures;

namespace OtlpServer.Transport.Http;

/// <summary>
/// OTLP/HTTP adapter for logs: <c>POST /v1/logs</c>. The HTTP twin of LogsGrpcService; both
/// call the same <see cref="ILogsExportHandler"/>. Every response (success or error) uses
/// the request's encoding: binary protobuf or OTLP/JSON.
///
/// <para>How each result is reported over HTTP:</para>
/// <code>
///  Content-Type not x-protobuf / json     ──► 415
///  Content-Encoding not gzip / identity   ──► 415
///  body over a size limit (wire or gzip)  ──► 413 (TooLarge)
///  corrupt gzip, invalid protobuf / JSON  ──► 400 (InvalidData)
///  handler returns a response             ──► 200 + ExportLogsServiceResponse (maybe with partial_success)
///  handler throws OtlpExportException     ──► 400 / 503 + Retry-After / 500 (HttpExportFailureMapper)
///  client disconnected                    ──► nothing (no one is there to read it)
///  anything else (a bug)                  ──► 500, with a generic message
/// </code>
/// </summary>
public static class LogsHttpEndpoint
{
    /// <summary>The spec's default path for logs. Traces and metrics would be /v1/traces and /v1/metrics.</summary>
    public const string Path = "/v1/logs";

    public static IEndpointConventionBuilder MapOtlpHttpLogs(this IEndpointRouteBuilder endpoints) =>
        // A plain RequestDelegate: we read the body ourselves, so minimal APIs' parameter
        // binding (which would try to bind a body) stays out of the way.
        // Other methods on the same path get 405 (see OtlpHttp.MapPostOnly for why).
        endpoints.MapPostOnly(Path, HandleAsync);

    private static async Task HandleAsync(HttpContext http)
    {
        var handler = http.RequestServices.GetRequiredService<ILogsExportHandler>();
        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(LogsHttpEndpoint));
        var cancellationToken = http.RequestAborted;

        // Content-Type picks the decoder, and the response is encoded the same way.
        if (OtlpHttp.EncodingOf(http.Request) is not { } encoding)
        {
            // UNIMPLEMENTED in the body mirrors gRPC, which uses it for an unsupported grpc-encoding.
            await WriteUnsupportedAsync(http,
                $"Unsupported Content-Type '{http.Request.ContentType ?? "(none)"}'. " +
                $"Send {OtlpHttp.ProtobufContentType} (binary protobuf) or {OtlpHttp.JsonContentType} (OTLP/JSON).");
            return;
        }

        if (!HttpRequestBodyReader.TryGetContentCoding(http.Request, out var coding, out var unsupportedCoding))
        {
            await WriteUnsupportedAsync(http, unsupportedCoding);
            return;
        }

        try
        {
            // Read the whole body (decompressed, size-checked), then decode it.
            var options = http.RequestServices.GetRequiredService<IOptions<HttpTransportOptions>>().Value;
            var buffers = http.RequestServices.GetRequiredService<RecyclableMemoryStreamManager>();
            ExportLogsServiceRequest request;
            var warnings = http.RequestServices.GetRequiredService<AggregatedWarnings>();
            await using (var body = await HttpRequestBodyReader.ReadAsync(http, coding, options, buffers, warnings, cancellationToken))
            {
                // Decoded straight from the pooled blocks (no copy). The parsed message owns its
                // own data, so the blocks go back to the pool right here, before validation.
                var bytes = body.GetReadOnlySequence();
                request = encoding == OtlpHttp.Encoding.Json
                    ? OtlpJsonCodec.ParseLogsRequest(bytes)
                    : OtlpProtobufDecoder.ParseLogsRequest(bytes);
            }

            var response = await handler.ExportAsync(request, ExportContextFactory.From(http), cancellationToken);

            // Full or partial success: always 200. A partial success is NOT an error.
            await OtlpHttp.WriteMessageAsync(http.Response, StatusCodes.Status200OK, response, encoding);
        }
        catch (OtlpExportException failure)
        {
            await HttpExportFailureMapper.WriteAsync(http.Response, failure, encoding);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client went away; writing a response would fail anyway.
        }
        catch (Exception ex) when (ex is not BadHttpRequestException)
        {
            // BadHttpRequestException (a malformed HTTP request, e.g. broken chunked framing)
            // is left to Kestrel, which answers with the status code it carries.
            // Anything else is a bug: log it, never send its details to the client.
            logger.LogError(ex, "Unexpected error while processing OTLP/HTTP logs export");
            await HttpExportFailureMapper.WriteAsync(http.Response,
                OtlpExportException.Internal("Internal server error while processing the export", ex), encoding);
        }
    }

    private static Task WriteUnsupportedAsync(HttpContext http, string message) =>
        OtlpHttp.WriteMessageAsync(http.Response, StatusCodes.Status415UnsupportedMediaType,
            new Google.Rpc.Status { Code = (int)Google.Rpc.Code.Unimplemented, Message = message },
            OtlpHttp.ErrorEncodingFor(http.Request));
}
