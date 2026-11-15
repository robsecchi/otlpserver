using OtlpServer.Logs.Failures;
using OtlpServer.Transport;

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
/// <para>The wire format (gRPC trailers-only / HTTP Status body) is <see cref="EarlyResponses"/>.</para>
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

    private static Task WriteAsync(HttpContext http, int httpStatus, Google.Rpc.Status status, TimeSpan? retryAfter) =>
        EarlyResponses.WriteAsync(http, httpStatus, status, retryAfter);
}
