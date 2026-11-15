using Google.Protobuf;
using OtlpServer.Logs.Failures;
using OtlpServer.Transport;
using OtlpServer.Transport.Http;

namespace OtlpServer.Security;

/// <summary>
/// The OTLP-shaped answers for requests refused by authentication or authorization, i.e.
/// BEFORE any OTLP endpoint runs. Shared by every authentication method.
/// <code>
///  case                         gRPC                         HTTP                                retryable?
///  ---------------------------  ---------------------------  ----------------------------------  ----------
///  no / bad credentials         UNAUTHENTICATED (16)         401 + WWW-Authenticate: Bearer       no
///  valid, but not allowed       PERMISSION_DENIED (7)        403                                  no
///  can't check (STS down)       UNAVAILABLE (14) + RetryInfo 503 + Retry-After                    yes
/// </code>
/// <para>
/// gRPC: a "trailers-only" response. The status goes in the response HEADERS (grpc-status,
/// grpc-message, grpc-status-details-bin) with no body, which is how gRPC reports an error
/// that happens before any message is exchanged.
/// </para>
/// <para>HTTP: a google.rpc.Status body in the request's encoding, as for every other OTLP error.</para>
/// </summary>
internal static class AuthenticationResponses
{
    public static Task UnauthenticatedAsync(HttpContext http, string message)
    {
        // RFC 6750: a 401 for a bearer-protected resource says which scheme to use.
        http.Response.Headers.WWWAuthenticate = "Bearer";
        return WriteAsync(http, StatusCodes.Status401Unauthorized,
            new Google.Rpc.Status { Code = (int)Google.Rpc.Code.Unauthenticated, Message = message }, retryAfter: null);
    }

    public static Task PermissionDeniedAsync(HttpContext http, string message) =>
        WriteAsync(http, StatusCodes.Status403Forbidden,
            new Google.Rpc.Status { Code = (int)Google.Rpc.Code.PermissionDenied, Message = message }, retryAfter: null);

    /// <summary>Retryable: the credentials couldn't be CHECKED, which is our problem, not the client's.</summary>
    public static Task UnavailableAsync(HttpContext http, string message, TimeSpan retryAfter) =>
        WriteAsync(http, StatusCodes.Status503ServiceUnavailable,
            ExportFailureStatus.From(OtlpExportException.Throttled(message, retryAfter)), retryAfter);

    private static async Task WriteAsync(HttpContext http, int httpStatus, Google.Rpc.Status status, TimeSpan? retryAfter)
    {
        var response = http.Response;

        if (http.Request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true)
        {
            response.StatusCode = StatusCodes.Status200OK; // gRPC errors ride on HTTP 200 + grpc-status
            response.ContentType = "application/grpc";
            response.Headers["grpc-status"] = status.Code.ToString(System.Globalization.CultureInfo.InvariantCulture);
            response.Headers["grpc-message"] = Uri.EscapeDataString(status.Message); // gRPC requires percent-encoding
            if (status.Details.Count > 0)
            {
                // "-bin" headers carry base64 binary: the full google.rpc.Status, e.g. with RetryInfo.
                response.Headers["grpc-status-details-bin"] = Convert.ToBase64String(status.ToByteArray());
            }
            return;
        }

        if (retryAfter is { } delay)
        {
            response.Headers.RetryAfter = Math.Max(0, (int)Math.Ceiling(delay.TotalSeconds))
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        await OtlpHttp.WriteMessageAsync(response, httpStatus, status, OtlpHttp.ErrorEncodingFor(http.Request));
    }
}
