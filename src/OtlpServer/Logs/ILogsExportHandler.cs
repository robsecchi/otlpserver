using OpenTelemetry.Proto.Collector.Logs.V1;

namespace OtlpServer.Logs;

/// <summary>
/// Transport-agnostic entry point for an OTLP logs export.
/// <para>
/// OTLP/gRPC and OTLP/HTTP carry the same <see cref="ExportLogsServiceRequest"/> /
/// <see cref="ExportLogsServiceResponse"/> messages; only the framing, status codes and
/// encodings differ. Each transport adapter decodes the request, calls this handler,
/// and maps the result (or exceptions) onto its own wire semantics.
/// </para>
/// </summary>
public interface ILogsExportHandler
{
    Task<ExportLogsServiceResponse> ExportAsync(ExportLogsServiceRequest request, CancellationToken cancellationToken);
}
