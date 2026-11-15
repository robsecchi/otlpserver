using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OtlpServer.Logs.Failures;
using OtlpServer.Logs.Validation;

namespace OtlpServer.Logs;

/// <summary>
/// Processes one OTLP logs export, whatever transport it arrived on.
///
/// <para>Flow:</para>
/// <code>
///  1. Admission (backpressure)
///     too many exports already running? ──► throw Throttled      (retryable,     gRPC UNAVAILABLE + RetryInfo)
///
///  2. Validation
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
///  3. Result
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
    private readonly LogsExportOptions _options;

    // Counts the free slots for concurrent exports. The handler is a singleton, so this is
    // ONE server-wide pool shared by all connections and all transports (gRPC and HTTP).
    // That is deliberate; see LogsExportOptions.MaxConcurrentExports before changing it.
    private readonly SemaphoreSlim _admission;

    public LogsExportHandler(ILogRecordValidator validator, IOptions<LogsExportOptions> options, ILogger<LogsExportHandler> logger)
    {
        _validator = validator;
        _logger = logger;
        _options = options.Value;
        _admission = new SemaphoreSlim(_options.MaxConcurrentExports, _options.MaxConcurrentExports);
    }

    public async Task<ExportLogsServiceResponse> ExportAsync(ExportLogsServiceRequest request, CancellationToken cancellationToken)
    {
        // ---- 1. Admission -----------------------------------------------------------
        // Wait(0) never blocks: either a slot is free now, or we refuse straight away. If we
        // queued here instead, a burst of traffic would pile up requests in memory and the
        // clients would still time out. Refusing quickly with "retry after X" lets each
        // client back off and keep its data in its own buffer meanwhile.
        if (!_admission.Wait(0))
        {
            _logger.LogWarning("Throttling logs export: {Max} exports already in progress", _options.MaxConcurrentExports);
            throw OtlpExportException.Throttled(
                $"Server is busy ({_options.MaxConcurrentExports} exports in progress). Retry after the given delay.",
                _options.ThrottleRetryDelay);
        }

        try
        {
            if (_options.SimulatedProcessingDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.SimulatedProcessingDelay, cancellationToken);
            }

            // ---- 2. Validation --------------------------------------------------------
            var outcome = Validate(request);

            _logger.LogInformation(
                "Logs export processed: {Accepted} accepted, {Rejected} rejected, {Warnings} warning(s)",
                outcome.AcceptedLogRecords, outcome.RejectedLogRecords, outcome.WarningCount);

            // ---- 3. Result ------------------------------------------------------------
            // Partial success is for requests where the server "accepts only parts of the
            // data". If nothing was accepted, there is no partial success: the whole request
            // failed. It is not retryable, because the same bytes would be rejected again.
            // (This is our policy. A server could also reply with partial_success where
            // rejected == total; the spec doesn't rule that out.)
            if (outcome.IsFullyRejected)
            {
                var message = outcome.BuildErrorMessage();
                _logger.LogWarning("Rejecting entire logs export: {Message}", message);
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
                _logger.LogWarning("Responding with partial_success: rejected_log_records={Rejected}, error_message={Message}",
                    partial.RejectedLogRecords, partial.ErrorMessage);
            }

            return response;
        }
        finally
        {
            // Free the slot whatever happened (success, rejection, cancellation, bug).
            _admission.Release();
        }
    }

    private LogsExportOutcome Validate(ExportLogsServiceRequest request)
    {
        var outcome = new LogsExportOutcome();

        // Locations are paths into the request, shown in error_message so the sender can find
        // the problem, e.g. "resource_logs[0].scope_logs[1].log_records[7]".
        for (var r = 0; r < request.ResourceLogs.Count; r++)
        {
            var resourceLogs = request.ResourceLogs[r];
            var recordsInResource = resourceLogs.ScopeLogs.Sum(s => (long)s.LogRecords.Count);
            if (recordsInResource == 0) continue; // nothing to accept or drop

            // A rejected resource drops every record under it, uninspected: they would be
            // stored with an ambiguous identity.
            if (Report(outcome, _validator.ValidateResource(resourceLogs.Resource), $"resource_logs[{r}].resource"))
            {
                outcome.RecordsRejected(recordsInResource);
                continue;
            }

            for (var s = 0; s < resourceLogs.ScopeLogs.Count; s++)
            {
                var scopeLogs = resourceLogs.ScopeLogs[s];
                if (scopeLogs.LogRecords.Count == 0) continue;

                // Same idea one level down: a rejected scope drops its records.
                if (Report(outcome, _validator.ValidateScope(scopeLogs.Scope), $"resource_logs[{r}].scope_logs[{s}].scope"))
                {
                    outcome.RecordsRejected(scopeLogs.LogRecords.Count);
                    continue;
                }

                for (var l = 0; l < scopeLogs.LogRecords.Count; l++)
                {
                    var location = $"resource_logs[{r}].scope_logs[{s}].log_records[{l}]";
                    if (Report(outcome, _validator.Validate(scopeLogs.LogRecords[l]), location))
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
    private static bool Report(LogsExportOutcome outcome, IReadOnlyList<LogRecordIssue> issues, string location)
    {
        foreach (var issue in issues)
        {
            outcome.AddIssue(issue, location);
        }
        return issues.Any(i => i.Severity == IssueSeverity.Rejection);
    }
}
