using OpenTelemetry.Proto.Logs.V1;

namespace OtlpServer.Logs.Validation;

/// <summary>
/// Interface defining the log record validation policy,
/// </summary>
public interface ILogRecordValidator
{
    /// <summary>
    /// Validates a log record, returning a list of issues if any.
    /// </summary>
    /// <param name="record">Log record to validate.</param>
    /// <returns>A list of validation issues.</returns>
    public IReadOnlyList<LogRecordIssue> Validate(LogRecord record);
}