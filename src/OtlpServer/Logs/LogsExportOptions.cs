namespace OtlpServer.Logs;

/// <summary>
/// Settings for the logs export core, bound from the "Otlp:Logs" configuration section.
/// </summary>
public sealed class LogsExportOptions
{
    public const string SectionName = "Otlp:Logs";

    /// <summary>
    /// How many exports may be processed at the same time. Any export beyond this limit is
    /// refused at once with a retryable "throttled" failure. This is how the server applies
    /// backpressure: the client is told to slow down instead of the server queueing work
    /// without limit and running out of memory.
    /// </summary>
    public int MaxConcurrentExports { get; set; } = 64;

    /// <summary>
    /// The delay sent back to throttled clients (gRPC <c>RetryInfo.retry_delay</c>). The spec
    /// says it should be long enough for the server to recover, but not so long that the
    /// client drops data while it waits.
    /// </summary>
    public TimeSpan ThrottleRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// TESTING ONLY: an artificial delay added to each export, so concurrency limits (and so
    /// throttling) can be triggered by hand. Keep it at zero in real use.
    /// </summary>
    public TimeSpan SimulatedProcessingDelay { get; set; } = TimeSpan.Zero;
}
