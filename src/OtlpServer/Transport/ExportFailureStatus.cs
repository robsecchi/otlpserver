using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using OtlpServer.Logs.Failures;

namespace OtlpServer.Transport;

/// <summary>
/// Builds the <c>google.rpc.Status</c> message that describes a failed export. Both
/// transports send this same message; only where it goes differs:
/// <code>
///  OTLP/gRPC:  grpc-status-details-bin trailer (plus grpc-status / grpc-message)
///  OTLP/HTTP:  the response BODY of every 4xx / 5xx, encoded like the request
/// </code>
/// <para>
/// <c>code</c> is always the gRPC code (google.rpc.Code), also over HTTP: Status is a gRPC
/// type. HTTP clients decide on the HTTP status code; the body is for the developer.
/// </para>
/// </summary>
public static class ExportFailureStatus
{
    public static Google.Rpc.Status From(OtlpExportException failure)
    {
        var status = new Google.Rpc.Status
        {
            Code = (int)GrpcCodeFor(failure.Kind),
            Message = failure.Message,
        };

        switch (failure.Kind)
        {
            // Tells the developer what to fix. Decoding errors have no per-field violations,
            // and an empty BadRequest would say nothing, so it's left out then.
            case ExportFailureKind.InvalidData when failure.Violations.Count > 0:
                status.Details.Add(Any.Pack(ToBadRequest(failure.Violations)));
                break;

            // gRPC clients wait retry_delay instead of their own backoff. HTTP clients use the
            // Retry-After header instead; keeping RetryInfo in the body costs nothing.
            case ExportFailureKind.Throttled or ExportFailureKind.QuotaExceeded:
                status.Details.Add(Any.Pack(new RetryInfo
                {
                    RetryDelay = Duration.FromTimeSpan(failure.RetryAfter ?? TimeSpan.FromSeconds(1)),
                }));
                break;
        }

        return status;
    }

    /// <summary>
    /// <list type="bullet">
    ///   <item>InvalidData → INVALID_ARGUMENT: non-retryable.</item>
    ///   <item>TooLarge → RESOURCE_EXHAUSTED with NO RetryInfo: clients retry this code only
    ///         when RetryInfo is present, so leaving it out makes it non-retryable.</item>
    ///   <item>Throttled → UNAVAILABLE: retryable. The spec says the server SHOULD use it for
    ///         throttling. RESOURCE_EXHAUSTED + RetryInfo would also be retryable, but
    ///         UNAVAILABLE is the recommended one.</item>
    ///   <item>Internal → INTERNAL: non-retryable server bug.</item>
    /// </list>
    /// </summary>
    public static Code GrpcCodeFor(ExportFailureKind kind) => kind switch
    {
        ExportFailureKind.InvalidData => Code.InvalidArgument,
        ExportFailureKind.TooLarge => Code.ResourceExhausted,
        ExportFailureKind.Throttled => Code.Unavailable,
        // Per-client quota: RESOURCE_EXHAUSTED is retryable here BECAUSE RetryInfo is attached.
        ExportFailureKind.QuotaExceeded => Code.ResourceExhausted,
        ExportFailureKind.Internal => Code.Internal,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped failure kind"),
    };

    private static BadRequest ToBadRequest(IReadOnlyList<ExportFieldViolation> violations)
    {
        var badRequest = new BadRequest();
        foreach (var v in violations)
        {
            badRequest.FieldViolations.Add(new BadRequest.Types.FieldViolation
            {
                Field = v.Field,
                Description = v.Description,
            });
        }
        return badRequest;
    }
}
