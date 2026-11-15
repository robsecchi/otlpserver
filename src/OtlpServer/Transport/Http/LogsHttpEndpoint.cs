using System.Buffers;
using Microsoft.Net.Http.Headers;
using OtlpServer.Logs;
using OtlpServer.Logs.Failures;

namespace OtlpServer.Transport.Http;

/// <summary>
/// OTLP/HTTP adapter for logs: <c>POST /v1/logs</c>. The HTTP twin of LogsGrpcService; both
/// call the same <see cref="ILogsExportHandler"/>.
///
/// <para>How each result is reported over HTTP:</para>
/// <code>
///  Content-Type not application/x-protobuf ──► 415 (JSON comes in a later step)
///  body isn't a valid protobuf message    ──► 400 (InvalidData)
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

        if (!IsProtobuf(http.Request.ContentType, out var unsupportedMessage))
        {
            // UNIMPLEMENTED in the body mirrors gRPC, which uses it for an unsupported grpc-encoding.
            await OtlpHttp.WriteMessageAsync(http.Response, StatusCodes.Status415UnsupportedMediaType,
                new Google.Rpc.Status { Code = (int)Google.Rpc.Code.Unimplemented, Message = unsupportedMessage });
            return;
        }

        try
        {
            // Read the whole body, then decode. No size limit of our own yet (next step);
            // Kestrel's MaxRequestBodySize (30 MB by default) answers 413 beyond that.
            using var body = new MemoryStream();
            await http.Request.Body.CopyToAsync(body, cancellationToken);
            var request = OtlpProtobufDecoder.ParseLogsRequest(
                new ReadOnlySequence<byte>(body.GetBuffer(), 0, (int)body.Length));

            var response = await handler.ExportAsync(request, cancellationToken);

            // Full or partial success: always 200. A partial success is NOT an error.
            await OtlpHttp.WriteMessageAsync(http.Response, StatusCodes.Status200OK, response);
        }
        catch (OtlpExportException failure)
        {
            await HttpExportFailureMapper.WriteAsync(http.Response, failure);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client went away; writing a response would fail anyway.
        }
        catch (Exception ex) when (ex is not BadHttpRequestException)
        {
            // BadHttpRequestException (e.g. body over Kestrel's limit) is left to Kestrel,
            // which answers with the status code it carries (413).
            // Anything else is a bug: log it, never send its details to the client.
            logger.LogError(ex, "Unexpected error while processing OTLP/HTTP logs export");
            await HttpExportFailureMapper.WriteAsync(http.Response,
                OtlpExportException.Internal("Internal server error while processing the export", ex));
        }
    }

    /// <summary>
    /// Accepts <c>application/x-protobuf</c>, case-insensitive and with any parameters
    /// (e.g. "; charset=..."), as media types are defined to work.
    /// </summary>
    private static bool IsProtobuf(string? contentType, out string unsupportedMessage)
    {
        if (MediaTypeHeaderValue.TryParse(contentType, out var mediaType))
        {
            if (mediaType.MediaType.Equals(OtlpHttp.ProtobufContentType, StringComparison.OrdinalIgnoreCase))
            {
                unsupportedMessage = "";
                return true;
            }

            if (mediaType.MediaType.Equals(OtlpHttp.JsonContentType, StringComparison.OrdinalIgnoreCase))
            {
                unsupportedMessage = $"OTLP/JSON ({OtlpHttp.JsonContentType}) is not supported yet. " +
                                     $"Send binary protobuf with Content-Type: {OtlpHttp.ProtobufContentType}.";
                return false;
            }
        }

        unsupportedMessage = $"Unsupported Content-Type '{contentType ?? "(none)"}'. " +
                             $"Send binary protobuf with Content-Type: {OtlpHttp.ProtobufContentType}.";
        return false;
    }
}
