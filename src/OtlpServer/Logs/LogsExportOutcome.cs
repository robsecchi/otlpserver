using System.Text;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OtlpServer.Logs.Failures;
using OtlpServer.Logs.Validation;

namespace OtlpServer.Logs;

/// <summary>
/// Collects the result of processing one export request and turns it into the
/// <see cref="ExportLogsServiceResponse"/> required by the OTLP spec.
///
/// <para>The spec has three possible responses (all with gRPC status OK / HTTP 200):</para>
/// <code>
///  accepted all, no warnings  ->  partial_success NOT set          ("Full Success")
///  accepted all, with warnings ->  partial_success { rejected = 0,  error_message = "..." }
///  rejected some or all       ->  partial_success { rejected = N,  error_message = "..." }
/// </code>
/// <para>
/// A partial success is NOT an error: the sender MUST NOT retry the request, because
/// retrying would only send the rejected data again and duplicate the accepted data.
/// </para>
/// <para>
/// There is a fourth case: every record was rejected (<see cref="IsFullyRejected"/>).
/// The handler does not send a response then. It raises a non-retryable
/// failure (INVALID_ARGUMENT) using <see cref="BuildErrorMessage"/> and
/// <see cref="GetViolations"/>; see <see cref="LogsExportHandler"/>.
/// </para>
/// </summary>
public sealed class LogsExportOutcome
{
    // How many distinct problem kinds we describe in error_message. Without a cap,
    // one bad batch of 10k records would produce a huge response.
    private const int MaxRulesInMessage = 5;

    // rule -> (severity, occurrences, location + detail of the first example). Kept in first-seen order.
    private readonly Dictionary<string, (IssueSeverity Severity, int Count, string Location, string Detail)> _issuesByRule = new();
    private readonly List<string> _ruleOrder = [];

    public long AcceptedLogRecords { get; private set; }
    public long RejectedLogRecords { get; private set; }
    public long WarningCount { get; private set; }

    /// <summary>The request carried records and none of them was acceptable.</summary>
    public bool IsFullyRejected => RejectedLogRecords > 0 && AcceptedLogRecords == 0;

    public void RecordAccepted() => AcceptedLogRecords++;

    public void RecordRejected() => RejectedLogRecords++;

    /// <param name="location">Where the record sits in the request, e.g. "resource_logs[0].scope_logs[1].log_records[7]".</param>
    public void AddIssue(LogRecordIssue issue, string location)
    {
        if (issue.Severity == IssueSeverity.Warning) WarningCount++;

        if (_issuesByRule.TryGetValue(issue.Rule, out var entry))
        {
            _issuesByRule[issue.Rule] = entry with { Count = entry.Count + 1 };
        }
        else
        {
            _issuesByRule[issue.Rule] = (issue.Severity, 1, location, issue.Detail);
            _ruleOrder.Add(issue.Rule);
        }
    }

    public ExportLogsServiceResponse ToResponse()
    {
        var response = new ExportLogsServiceResponse();

        // Case 1: Full Success. The server MUST leave partial_success unset.
        if (RejectedLogRecords == 0 && _issuesByRule.Count == 0)
        {
            return response;
        }

        // Cases 2 and 3: initialize partial_success.
        //  - rejected_log_records MUST equal the number of records we dropped
        //    (0 if there are only warnings).
        //  - error_message: developer-facing English text that should tell the user how
        //    to fix the problem. With rejected = 0 it MUST be non-empty, otherwise the
        //    message would mean the same as Full Success.
        response.PartialSuccess = new ExportLogsPartialSuccess
        {
            RejectedLogRecords = RejectedLogRecords,
            ErrorMessage = BuildErrorMessage(),
        };
        return response;
    }

    public string BuildErrorMessage()
    {
        // Example:
        //   2 of 5 log record(s) rejected; 1 warning(s).
        //   [rejected] duplicate_attribute_key x2, e.g. resource_logs[0].scope_logs[0].log_records[3]: attribute key 'k' appears more than once
        //   [warning] invalid_trace_id x1, e.g. ...
        var total = AcceptedLogRecords + RejectedLogRecords;
        var sb = new StringBuilder();
        sb.Append($"{RejectedLogRecords} of {total} log record(s) rejected; {WarningCount} warning(s).");

        foreach (var rule in _ruleOrder.Take(MaxRulesInMessage))
        {
            var (severity, count, location, detail) = _issuesByRule[rule];
            var label = severity == IssueSeverity.Rejection ? "rejected" : "warning";
            sb.Append($"\n[{label}] {rule} x{count}, e.g. {location}: {detail}");
        }

        if (_ruleOrder.Count > MaxRulesInMessage)
        {
            sb.Append($"\n... and {_ruleOrder.Count - MaxRulesInMessage} more issue type(s).");
        }

        return sb.ToString();
    }

    /// <summary>
    /// One violation per rejection rule (first example only, capped like the message).
    /// Transports can attach these as structured details, e.g. gRPC
    /// <c>google.rpc.BadRequest.FieldViolation</c>.
    /// </summary>
    public IReadOnlyList<ExportFieldViolation> GetViolations() =>
        _ruleOrder
            .Select(rule => (Rule: rule, Entry: _issuesByRule[rule]))
            .Where(x => x.Entry.Severity == IssueSeverity.Rejection)
            .Take(MaxRulesInMessage)
            .Select(x => new ExportFieldViolation(x.Entry.Location, $"{x.Rule} (x{x.Entry.Count}): {x.Entry.Detail}"))
            .ToList();
}
