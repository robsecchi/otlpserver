using System.Globalization;
using OtlpServer.Logs.Failures;

namespace OtlpServer.Transport.Http;

/// <summary>
/// Writes a transport-neutral <see cref="OtlpExportException"/> as an OTLP/HTTP error response.
///
/// <para>
/// Over HTTP the client decides whether to retry from the STATUS CODE alone (the body is for
/// developers):
/// </para>
/// <code>
///  retryable:      429 Too Many Requests, 502 Bad Gateway, 503 Service Unavailable, 504 Gateway Timeout
///  non-retryable:  every other 4xx / 5xx  → the client MUST drop the data
/// </code>
/// <para>So each failure kind gets a code on the right side of that line:</para>
/// <code>
///  Kind         HTTP                          body (google.rpc.Status)
///  -----------  ----------------------------  ---------------------------------
///  InvalidData  400 Bad Request               INVALID_ARGUMENT (+ BadRequest)
///  TooLarge     413 Content Too Large         RESOURCE_EXHAUSTED (no RetryInfo)
///  Throttled    503 + Retry-After: seconds    UNAVAILABLE      (+ RetryInfo)
///  QuotaExceeded 429 + Retry-After: seconds   RESOURCE_EXHAUSTED (+ RetryInfo)
///  Internal     500 Internal Server Error     INTERNAL
/// </code>
/// <para>
/// Both 503 and 429 are retryable and the spec allows either for throttling. We use 503 for
/// "the server is overloaded" and 429 for "this client is over its own share".
/// </para>
/// </summary>
internal static class HttpExportFailureMapper
{
    public static int StatusCodeFor(ExportFailureKind kind) => kind switch
    {
        ExportFailureKind.InvalidData => StatusCodes.Status400BadRequest,
        ExportFailureKind.TooLarge => StatusCodes.Status413PayloadTooLarge,
        ExportFailureKind.Throttled => StatusCodes.Status503ServiceUnavailable,
        ExportFailureKind.QuotaExceeded => StatusCodes.Status429TooManyRequests,
        ExportFailureKind.Internal => StatusCodes.Status500InternalServerError,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped failure kind"),
    };

    public static Task WriteAsync(HttpResponse response, OtlpExportException failure, OtlpHttp.Encoding encoding)
    {
        if (failure.RetryAfter is { } delay)
        {
            // Retry-After takes whole seconds. Round UP, so the client never comes back
            // earlier than the server asked.
            var seconds = Math.Max(0, (int)Math.Ceiling(delay.TotalSeconds));
            response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        return OtlpHttp.WriteMessageAsync(response, StatusCodeFor(failure.Kind), ExportFailureStatus.From(failure), encoding);
    }
}
