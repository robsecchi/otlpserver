using Grpc.Core;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OtlpServer.Logs;
using OtlpServer.Logs.Failures;

namespace OtlpServer.Transport.Grpc;

/// <summary>
/// OTLP/gRPC adapter for the unary RPC
/// <c>opentelemetry.proto.collector.logs.v1.LogsService/Export</c>.
///
/// <para>How each result is reported over gRPC:</para>
/// <code>
///  handler returns a response      ──► status OK + ExportLogsServiceResponse (maybe with partial_success)
///  handler throws OtlpExportException ─► mapped status + details (GrpcExportFailureMapper)
///  client cancelled / deadline hit  ──► CANCELLED (the client has gone; no one reads it)
///  anything else (a bug)            ──► INTERNAL, non-retryable, with a generic message
/// </code>
/// <para>
/// Some failures never get here, because grpc-dotnet handles them before the method runs:
/// </para>
/// <list type="bullet">
///   <item>Message larger than <c>MaxReceiveMessageSize</c>: RESOURCE_EXHAUSTED with no RetryInfo,
///         so clients treat it as non-retryable. The spec asks for exactly that, because
///         sending the same oversized payload again can't succeed.</item>
///   <item>gzip message whose DECOMPRESSED size goes over <c>MaxDecompressedMessageSize</c>:
///         RESOURCE_EXHAUSTED, raised while decompressing by
///         <see cref="Compression.BoundedGzipCompressionProvider"/> (decompression bomb protection).</item>
///   <item>grpc-encoding other than gzip / identity: UNIMPLEMENTED (non-retryable).</item>
///   <item>Bytes that aren't valid protobuf (truncated, bad tags, nesting too deep):
///         INVALID_ARGUMENT (non-retryable), raised by the request marshaller in
///         <see cref="LogsServiceBase"/>.</item>
/// </list>
/// </summary>
public sealed class LogsGrpcService(ILogsExportHandler handler, ILogger<LogsGrpcService> logger) : LogsServiceBase
{
    public override async Task<ExportLogsServiceResponse> Export(ExportLogsServiceRequest request, ServerCallContext context)
    {
        try
        {
            return await handler.ExportAsync(request, ExportContextFactory.From(context.GetHttpContext()), context.CancellationToken);
        }
        catch (OtlpExportException failure)
        {
            throw GrpcExportFailureMapper.ToRpcException(failure);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // The client disconnected or its deadline passed. CANCELLED is retryable, but
            // the client already chose to give up on this call.
            throw new RpcException(new global::Grpc.Core.Status(StatusCode.Cancelled, "Export cancelled by the client"));
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            // Never send exception details (stack traces, internals) to the client. Log them
            // here and return a generic non-retryable error.
            logger.LogError(ex, "Unexpected error while processing logs export");
            throw GrpcExportFailureMapper.ToRpcException(
                OtlpExportException.Internal("Internal server error while processing the export", ex));
        }
    }
}
