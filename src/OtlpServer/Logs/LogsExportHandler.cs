using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OtlpServer.Diagnostics;
using OtlpServer.Logs.Failures;
using OtlpServer.Logs.Validation;

namespace OtlpServer.Logs;

/// <summary>
/// Processes one OTLP logs export, whatever transport it arrived on.
///
/// <para>Flow:</para>
/// <code>
///  (Admission, i.e. backpressure, happens BEFORE this handler, for the whole request:
///   see Admission/ExportAdmissionMiddleware. By the time we're here, the export holds a slot.)
///
///  1. Validation
///     ExportLogsServiceRequest
///       └─ ResourceLogs[]  ──►  ValidateResource   Rejection? -> all its records rejected, skip it
///           │                  (who produced the logs: service.name, host, ...)
///           └─ ScopeLogs[] ──►  ValidateScope      Rejection? -> all its records rejected, skip it
///               │              (which library/logger produced them)
///               └─ LogRecord[]  ──►  Validate(record)
///                                      ├─ any Rejection?  -> record rejected (dropped)
///                                      └─ otherwise       -> record accepted (warnings, if any, still reported)
///     Whether duplicate attribute keys are a Rejection or a Warning is configurable
///     (Otlp:Logs:Validation:DuplicateAttributeKeys).
///
///  2. Result
///     every record rejected?  ──► throw InvalidData                    (non-retryable, gRPC INVALID_ARGUMENT + BadRequest)
///     otherwise               ──► export accepted records (TODO) ──► outcome.ToResponse()                 (OK, with or without partial_success)
///
///  Any other exception escapes to the transport adapter, which reports it as Internal (non-retryable).
/// </code>
/// <para>
/// Partial success is counted per LogRecord, because the spec's counter is
/// <c>rejected_log_records</c>. Resources and scopes are containers, not counted items.
/// </para>
/// </summary>
public sealed class LogsExportHandler : ILogsExportHandler
{
    private readonly ILogRecordValidator _validator;
    private readonly ILogger<LogsExportHandler> _logger;
    private readonly AggregatedWarnings _warnings;
    private readonly LogsExportOptions _options;

    public LogsExportHandler(
        ILogRecordValidator validator, IOptions<LogsExportOptions> options, AggregatedWarnings warnings, ILogger<LogsExportHandler> logger)
    {
        _validator = validator;
        _logger = logger;
        _warnings = warnings;
        _options = options.Value;
    }

    public async Task<ExportLogsServiceResponse> ExportAsync(ExportLogsServiceRequest request, ExportContext context, CancellationToken cancellationToken)
    {
        if (_options.SimulatedProcessingDelay > TimeSpan.Zero)
        {
            await Task.Delay(_options.SimulatedProcessingDelay, cancellationToken);
        }

        // ---- 1. Validation --------------------------------------------------------
        var outcome = Validate(request);

        // Debug: at load this runs thousands of times per second. Throughput is for metrics.
        _logger.LogDebug(
            "Logs export from {Client} processed: {Accepted} accepted, {Rejected} rejected, {Warnings} warning(s)",
            context.ClientOrAnonymous, outcome.AcceptedLogRecords, outcome.RejectedLogRecords, outcome.WarningCount);

        // ---- 2. Result ------------------------------------------------------------
        // Partial success is for requests where the server "accepts only parts of the
        // data". If nothing was accepted, there is no partial success: the whole request
        // failed. It is not retryable, because the same bytes would be rejected again.
        // (This is our policy. A server could also reply with partial_success where
        // rejected == total; the spec doesn't rule that out.)
        if (outcome.IsFullyRejected)
        {
            var message = outcome.BuildErrorMessage();
            // Aggregated: a client sending bad data does so on EVERY export.
            _warnings.Report("Rejected entire logs export", context.ClientOrAnonymous, message,
                outcome.RejectedLogRecords, "records rejected");
            throw OtlpExportException.InvalidData(message, outcome.GetViolations());
        }

        // TODO: Export the accepted log records (storage / forwarding / processing pipeline).
        //  - Input: only the ACCEPTED records. The validation result should carry them (e.g. a
        //    filtered copy of the request), so this step doesn't walk the request tree again.
        //  - Define it behind an abstraction (e.g. ILogsExporter.ExportAsync(accepted, ct)),
        //    so the backend can be swapped and the transports stay unaware of it.
        //  - Map exporter failures onto OtlpExportException:
        //      backend temporarily down / overloaded -> Throttled (retryable)
        //      bug / unrecoverable error             -> Internal  (non-retryable)
        //  - This runs BEFORE the response is built: a success response is only sent once
        //    the records are safely handed off, so the client never gets OK for lost data.
        //  - If this moves to a background consumer behind a bounded Channel, this call becomes
        //    TryWrite(...) and "channel full" -> Throttled. "Accepted" then means "queued in
        //    memory", so the channel must be drained on shutdown.

        var response = outcome.ToResponse();
        if (response.PartialSuccess is { } partial)
        {
            _warnings.Report(
                partial.RejectedLogRecords > 0 ? "Partially rejected logs export" : "Logs export accepted with data warnings",
                context.ClientOrAnonymous, partial.ErrorMessage, partial.RejectedLogRecords, "records rejected");
        }

        return response;
    }

    private LogsExportOutcome Validate(ExportLogsServiceRequest request)
    {
        var outcome = new LogsExportOutcome();

        // Locations are paths into the request, shown in error_message so the sender can find
        // the problem, e.g. "resource_logs[0].scope_logs[1].log_records[7]". They are only
        // turned into strings when there is an issue to report: this loop runs for every record,
        // and a clean record must allocate nothing.
        for (var r = 0; r < request.ResourceLogs.Count; r++)
        {
            var resourceLogs = request.ResourceLogs[r];
            long recordsInResource = 0;
            for (var s = 0; s < resourceLogs.ScopeLogs.Count; s++)
            {
                recordsInResource += resourceLogs.ScopeLogs[s].LogRecords.Count;
            }
            if (recordsInResource == 0) continue; // nothing to accept or drop

            // A rejected resource drops every record under it, uninspected: they would be
            // stored with an ambiguous identity.
            if (Report(outcome, _validator.ValidateResource(resourceLogs.Resource), new Location(r)))
            {
                outcome.RecordsRejected(recordsInResource);
                continue;
            }

            for (var s = 0; s < resourceLogs.ScopeLogs.Count; s++)
            {
                var scopeLogs = resourceLogs.ScopeLogs[s];
                if (scopeLogs.LogRecords.Count == 0) continue;

                // Same idea one level down: a rejected scope drops its records.
                if (Report(outcome, _validator.ValidateScope(scopeLogs.Scope), new Location(r, s)))
                {
                    outcome.RecordsRejected(scopeLogs.LogRecords.Count);
                    continue;
                }

                for (var l = 0; l < scopeLogs.LogRecords.Count; l++)
                {
                    if (Report(outcome, _validator.Validate(scopeLogs.LogRecords[l]), new Location(r, s, l)))
                    {
                        outcome.RecordsRejected(1);
                    }
                    else
                    {
                        outcome.RecordAccepted();
                    }
                }
            }
        }

        return outcome;
    }

    /// <summary>
    /// Adds the issues to the outcome. Returns true if any of them is a Rejection: one is
    /// enough to drop the item. Warnings alone never drop it.
    /// </summary>
    private static bool Report(LogsExportOutcome outcome, IReadOnlyList<LogRecordIssue> issues, Location location)
    {
        if (issues.Count == 0) return false; // the common case: nothing to build, nothing to report

        var path = location.ToString();
        var rejected = false;
        for (var i = 0; i < issues.Count; i++)
        {
            outcome.AddIssue(issues[i], path);
            rejected |= issues[i].Severity == IssueSeverity.Rejection;
        }
        return rejected;
    }

    /// <summary>
    /// Where an item sits in the request, as plain indexes. A struct, so carrying it costs
    /// nothing; the string is only built (ToString) when there is an issue to report.
    /// </summary>
    private readonly record struct Location(int Resource, int Scope = -1, int Record = -1)
    {
        public override string ToString() =>
            Scope < 0 ? $"resource_logs[{Resource}].resource"
            : Record < 0 ? $"resource_logs[{Resource}].scope_logs[{Scope}].scope"
            : $"resource_logs[{Resource}].scope_logs[{Scope}].log_records[{Record}]";
    }
}
