namespace OtlpServer.Logs.Validation;

/// <summary>
/// How serious a problem found on a single <c>LogRecord</c> is.
/// </summary>
public enum IssueSeverity
{
    /// <summary>
    /// The record is still accepted. It does NOT count towards
    /// <c>rejected_log_records</c>, but it is reported in <c>error_message</c> so the
    /// sender can fix it. (Spec: servers MAY use partial_success for warnings.)
    /// </summary>
    Warning,

    /// <summary>
    /// The record is dropped and counts towards <c>rejected_log_records</c>.
    /// </summary>
    Rejection,
}

/// <param name="Severity">Whether the record is rejected or just warned about.</param>
/// <param name="Rule">Stable short code, used to group identical problems in the response message.</param>
/// <param name="Detail">Human-readable explanation for this specific occurrence.</param>
public readonly record struct LogRecordIssue(IssueSeverity Severity, string Rule, string Detail);
