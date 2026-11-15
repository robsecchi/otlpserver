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
///       └─ ResourceLogs[]            (who produced the logs: service.name, host, ...)
///           └─ ScopeLogs[]           (which library/logger produced them)
///               └─ LogRecord[]  ──►  ILogRecordValidator.Validate(record)
///                                      ├─ any Rejection?  -> outcome.RecordRejected()  (record dropped)
///                                      └─ otherwise       -> outcome.RecordAccepted()  (warnings, if any, still reported)
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

    // Counts the free slots for concurrent exports. The handler is a singleton, so the
    // limit applies across all connections and (later) both transports.
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

        for (var r = 0; r < request.ResourceLogs.Count; r++)
        {
            var resourceLogs = request.ResourceLogs[r];

            for (var s = 0; s < resourceLogs.ScopeLogs.Count; s++)
            {
                var scopeLogs = resourceLogs.ScopeLogs[s];

                for (var l = 0; l < scopeLogs.LogRecords.Count; l++)
                {
                    var record = scopeLogs.LogRecords[l];
                    var issues = _validator.Validate(record);

                    // A path to the record, shown in error_message so the sender can find it.
                    var location = $"resource_logs[{r}].scope_logs[{s}].log_records[{l}]";
                    foreach (var issue in issues)
                    {
                        outcome.AddIssue(issue, location);
                    }

                    // One Rejection is enough to drop the record. Warnings alone don't drop it.
                    if (issues.Any(i => i.Severity == IssueSeverity.Rejection))
                    {
                        outcome.RecordRejected();
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
}
