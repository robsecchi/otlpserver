using Google.Protobuf;

namespace OtlpServer.Transport.Http;

/// <summary>
/// Wire-level pieces of OTLP/HTTP shared by every signal endpoint: content types, writing
/// protobuf bodies, and turning ASP.NET Core's own errors (404, 405) into OTLP error bodies.
///
/// <para>
/// OTLP/HTTP carries the same messages as OTLP/gRPC, one request per HTTP POST:
/// </para>
/// <code>
///  POST /v1/logs                      Content-Type: application/x-protobuf
///  body: ExportLogsServiceRequest     (binary protobuf, the same bytes as inside a gRPC frame)
///
///  200 OK                             Content-Type: application/x-protobuf
///  body: ExportLogsServiceResponse    (maybe with partial_success)
///
///  4xx / 5xx                          Content-Type: application/x-protobuf
///  body: google.rpc.Status            ("MUST" for every 4xx and 5xx response)
/// </code>
/// </summary>
public static class OtlpHttp
{
    public const string ProtobufContentType = "application/x-protobuf";
    public const string JsonContentType = "application/json";

    /// <summary>Writes a protobuf message as the whole response body.</summary>
    public static async Task WriteMessageAsync(HttpResponse response, int statusCode, IMessage message)
    {
        var bytes = message.ToByteArray(); // responses are tiny: an empty message or a short Status
        response.StatusCode = statusCode;
        response.ContentType = ProtobufContentType;
        response.ContentLength = bytes.Length;
        await response.Body.WriteAsync(bytes);
    }

    /// <summary>
    /// Gives a <c>google.rpc.Status</c> body to error responses that ASP.NET Core produces
    /// before any OTLP code runs, so they follow the same rule as ours. In practice that is
    /// 404: no endpoint for this path (e.g. /v1/traces: this server only takes logs).
    /// Like every 4xx except 429, it is non-retryable for OTLP clients.
    /// It runs only for responses with no body yet, so our own error bodies are left alone.
    /// (405 is answered by the signal endpoints themselves; see <see cref="MapPostOnly"/>.)
    /// </summary>
    public static IApplicationBuilder UseOtlpHttpErrorBodies(this IApplicationBuilder app) =>
        app.UseStatusCodePages(async context =>
        {
            var http = context.HttpContext;

            // gRPC has its own error channel (trailers); never touch its responses.
            if (http.Request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true)
            {
                return;
            }

            var statusCode = http.Response.StatusCode;
            var (code, message) = statusCode == StatusCodes.Status404NotFound
                ? (Google.Rpc.Code.Unimplemented,
                   $"No OTLP endpoint at '{http.Request.Path}'. This server accepts logs only: POST /v1/logs.")
                : (Google.Rpc.Code.Unknown, $"HTTP {statusCode}.");

            await WriteMessageAsync(http.Response, statusCode, new Google.Rpc.Status { Code = (int)code, Message = message });
        });

    /// <summary>
    /// Maps <paramref name="handler"/> for POST on <paramref name="path"/>, and answers every
    /// other method with 405 + <c>Allow: POST</c> + a Status body.
    ///
    /// <para>
    /// Why not rely on ASP.NET Core's built-in 405? grpc-dotnet registers a catch-all route
    /// <c>{unimplementedService}/{unimplementedMethod}</c> (to answer unknown gRPC services
    /// with UNIMPLEMENTED) that accepts ANY method. "/v1/logs" has two segments, so it
    /// matches that pattern. Routing only produces 405 when every endpoint matching the path
    /// rejects the method, and that one doesn't. Its constraint (gRPC requests only) then
    /// rejects the request, nothing is left, and the client gets 404. So we answer 405
    /// ourselves.
    /// </para>
    /// </summary>
    public static IEndpointConventionBuilder MapPostOnly(this IEndpointRouteBuilder endpoints, string path, RequestDelegate handler)
    {
        // Any method on this path. Order 1 loses to the POST endpoint (order 0) for POST, so
        // only the other methods end up here.
        endpoints.Map(path, async http =>
        {
            http.Response.Headers.Allow = "POST";
            await WriteMessageAsync(http.Response, StatusCodes.Status405MethodNotAllowed, new Google.Rpc.Status
            {
                Code = (int)Google.Rpc.Code.Unimplemented,
                Message = $"Method {http.Request.Method} is not allowed on {path}. OTLP/HTTP exports use POST.",
            });
        }).WithOrder(1).WithDisplayName($"OTLP/HTTP 405 for {path}");

        return endpoints.MapPost(path, handler);
    }
}
