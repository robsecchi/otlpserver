using Google.Protobuf;
using Grpc.Core;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OtlpServer.Logs.Failures;

namespace OtlpServer.Transport.Grpc;

/// <summary>
/// Hand-written replacement for the generated <c>LogsService.LogsServiceBase</c>. It declares
/// the same RPC, but lets us choose how request bytes are decoded.
///
/// <para>Why not use the generated base:</para>
/// <code>
///  bytes ──► request marshaller (deserializer) ──► interceptors ──► LogsGrpcService.Export
///            ▲
///            └─ a parse error thrown here escapes before any of our code runs. With the
///               generated marshaller, grpc-dotnet reports it as UNKNOWN
///               "Exception was thrown by handler."
/// </code>
/// <para>
/// The spec says a non-retryable error SHOULD use INVALID_ARGUMENT, and bad bytes are
/// exactly that: sending the same bytes again fails the same way. UNKNOWN is non-retryable
/// too, so clients already drop the data, but the status says nothing about the cause.
/// </para>
/// <para>How grpc-dotnet wires this class (the same pattern the generated code uses):</para>
/// <list type="number">
///   <item><c>MapGrpcService&lt;LogsGrpcService&gt;()</c> walks up from LogsGrpcService and finds
///         <see cref="BindServiceMethodAttribute"/> here.</item>
///   <item>It calls <see cref="BindService"/> with a null service, just to learn which
///         methods exist and their marshallers.</item>
///   <item>For each method it looks up, by name, a public override of a method declared on
///         THIS class (here <see cref="Export"/>) and calls it on a new LogsGrpcService
///         instance per request. That's why the binding and the abstract method must live
///         on the same class.</item>
/// </list>
/// </summary>
[BindServiceMethod(typeof(LogsServiceBase), nameof(BindService))]
public abstract class LogsServiceBase
{
    // The RPC's name comes from the .proto descriptor, so it can't drift from what clients call:
    // /opentelemetry.proto.collector.logs.v1.LogsService/Export
    private static readonly Method<ExportLogsServiceRequest, ExportLogsServiceResponse> ExportMethod = new(
        MethodType.Unary,
        LogsService.Descriptor.FullName,
        "Export",
        Marshallers.Create(SerializeMessage, DeserializeRequest),
        Marshallers.Create(SerializeMessage, DeserializeResponse));

    /// <summary>rpc Export(ExportLogsServiceRequest) returns (ExportLogsServiceResponse)</summary>
    public abstract Task<ExportLogsServiceResponse> Export(ExportLogsServiceRequest request, ServerCallContext context);

    /// <summary>Called by grpc-dotnet at startup (see the class remarks).</summary>
    public static void BindService(ServiceBinderBase binder, LogsServiceBase? service) =>
        binder.AddMethod(ExportMethod, service is null ? null : service.Export);

    private static ExportLogsServiceRequest DeserializeRequest(DeserializationContext context)
    {
        try
        {
            // The payload is already decompressed (and size-checked) at this point.
            return OtlpProtobufDecoder.ParseLogsRequest(context.PayloadAsReadOnlySequence());
        }
        catch (OtlpExportException failure)
        {
            // InvalidData → INVALID_ARGUMENT. grpc-dotnet sends an RpcException thrown here
            // as the call's status, unchanged.
            throw GrpcExportFailureMapper.ToRpcException(failure);
        }
    }

    // Not used by the server (it never receives responses), but a Method needs both marshallers.
    private static ExportLogsServiceResponse DeserializeResponse(DeserializationContext context) =>
        ExportLogsServiceResponse.Parser.ParseFrom(context.PayloadAsReadOnlySequence());

    /// <summary>Writes the message straight into grpc-dotnet's buffer, as the generated code does.</summary>
    private static void SerializeMessage<T>(T message, SerializationContext context) where T : IMessage<T>
    {
        context.SetPayloadLength(message.CalculateSize());
        message.WriteTo(context.GetBufferWriter());
        context.Complete();
    }
}
