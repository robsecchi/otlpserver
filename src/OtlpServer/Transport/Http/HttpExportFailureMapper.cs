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
///  Internal     500 Internal Server Error     INTERNAL
/// </code>
/// <para>
/// Throttling could also be 429. The spec allows both; 503 says "the server is overloaded",
/// which is what our limit means. 429 fits better for a per-client quota.
/// </para>
/// </summary>
internal static class HttpExportFailureMapper
{
    public static int StatusCodeFor(ExportFailureKind kind) => kind switch
    {
        ExportFailureKind.InvalidData => StatusCodes.Status400BadRequest,
        ExportFailureKind.TooLarge => StatusCodes.Status413PayloadTooLarge,
        ExportFailureKind.Throttled => StatusCodes.Status503ServiceUnavailable,
        ExportFailureKind.Internal => StatusCodes.Status500InternalServerError,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped failure kind"),
    };

    public static Task WriteAsync(HttpResponse response, OtlpExportException failure, OtlpHttp.Encoding encoding)
    {
        if (failure is { Kind: ExportFailureKind.Throttled, RetryAfter: { } delay })
        {
            // Retry-After takes whole seconds. Round UP, so the client never comes back
            // earlier than the server asked.
            var seconds = Math.Max(0, (int)Math.Ceiling(delay.TotalSeconds));
            response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        return OtlpHttp.WriteMessageAsync(response, StatusCodeFor(failure.Kind), ExportFailureStatus.From(failure), encoding);
    }
}
