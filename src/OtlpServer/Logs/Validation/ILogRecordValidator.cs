using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;

namespace OtlpServer.Logs.Validation;

/// <summary>
/// The validation policy for log records and the resource / scope that contain them.
/// <para>
/// A Rejection on a container drops every record inside it (see LogsExportHandler).
/// </para>
/// </summary>
public interface ILogRecordValidator
{
    /// <summary>Validates one log record.</summary>
    IReadOnlyList<LogRecordIssue> Validate(LogRecord record);

    /// <summary>Validates the resource shared by a ResourceLogs (null if the sender left it unset).</summary>
    IReadOnlyList<LogRecordIssue> ValidateResource(Resource? resource);

    /// <summary>Validates the instrumentation scope shared by a ScopeLogs (null if unset).</summary>
    IReadOnlyList<LogRecordIssue> ValidateScope(InstrumentationScope? scope);
}
