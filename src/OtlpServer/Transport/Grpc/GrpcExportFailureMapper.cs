using Grpc.Core;
using OtlpServer.Logs.Failures;

namespace OtlpServer.Transport.Grpc;

/// <summary>
/// Turns a transport-neutral <see cref="OtlpExportException"/> into the
/// <see cref="RpcException"/> that OTLP/gRPC requires. The Status itself (code, message,
/// details) is built by <see cref="ExportFailureStatus"/>, shared with OTLP/HTTP.
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
    // Grpc.StatusProto: sets grpc-status / grpc-message and serializes the google.rpc.Status
    // into the grpc-status-details-bin trailer.
    public static RpcException ToRpcException(OtlpExportException failure) =>
        ExportFailureStatus.From(failure).ToRpcException();
}
