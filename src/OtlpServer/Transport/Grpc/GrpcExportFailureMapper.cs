using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using OtlpServer.Logs.Failures;

namespace OtlpServer.Transport.Grpc;

/// <summary>
/// Turns a transport-neutral <see cref="OtlpExportException"/> into the
/// <see cref="RpcException"/> that OTLP/gRPC requires.
///
/// <para>How a rich gRPC error reaches the client:</para>
/// <code>
///  HTTP/2 trailers
///    grpc-status:             14                     ← the code, all a basic client looks at
///    grpc-message:            "Server is busy..."
///    grpc-status-details-bin: base64(google.rpc.Status {   ← optional, structured
///                               code: 14, message: "...",
///                               details: [ Any(google.rpc.RetryInfo { retry_delay: 1s }) ] })
/// </code>
/// <para>
/// OTel SDK and Collector exporters decode <c>grpc-status-details-bin</c>. When there is a
/// <c>RetryInfo</c>, they wait <c>retry_delay</c> instead of their usual exponential backoff.
/// </para>
/// <para>Which codes clients retry (from the spec):</para>
/// <code>
///  retryable:      CANCELLED, DEADLINE_EXCEEDED, ABORTED, OUT_OF_RANGE, UNAVAILABLE, DATA_LOSS
///  non-retryable:  UNKNOWN, INVALID_ARGUMENT, NOT_FOUND, ALREADY_EXISTS, PERMISSION_DENIED,
///                  UNAUTHENTICATED, FAILED_PRECONDITION, UNIMPLEMENTED, INTERNAL
///  conditional:    RESOURCE_EXHAUSTED: retryable only if the status carries RetryInfo
/// </code>
/// </summary>
internal static class GrpcExportFailureMapper
{
    public static RpcException ToRpcException(OtlpExportException failure)
    {
        var status = failure.Kind switch
        {
            // Non-retryable: the client MUST drop the data. BadRequest tells the developer
            // what to fix.
            ExportFailureKind.InvalidData => new Google.Rpc.Status
            {
                Code = (int)Code.InvalidArgument,
                Message = failure.Message,
                Details = { Any.Pack(ToBadRequest(failure.Violations)) },
            },

            // Retryable backpressure. The spec says the server SHOULD use UNAVAILABLE and
            // MAY add RetryInfo; the client MUST throttle itself.
            // (RESOURCE_EXHAUSTED + RetryInfo would also be retryable, but UNAVAILABLE is
            // what the spec recommends for throttling.)
            ExportFailureKind.Throttled => new Google.Rpc.Status
            {
                Code = (int)Code.Unavailable,
                Message = failure.Message,
                Details =
                {
                    Any.Pack(new RetryInfo
                    {
                        RetryDelay = Duration.FromTimeSpan(failure.RetryAfter ?? TimeSpan.FromSeconds(1)),
                    }),
                },
            },

            // Non-retryable server bug.
            ExportFailureKind.Internal => new Google.Rpc.Status
            {
                Code = (int)Code.Internal,
                Message = failure.Message,
            },

            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure.Kind, "Unmapped failure kind"),
        };

        // Grpc.StatusProto: sets grpc-status / grpc-message and serializes the
        // google.rpc.Status into the grpc-status-details-bin trailer.
        return status.ToRpcException();
    }

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
