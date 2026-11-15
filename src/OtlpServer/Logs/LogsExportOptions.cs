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
    /// <para>
    /// SERVER-WIDE by design: one pool of slots shared by every transport (OTLP/gRPC,
    /// OTLP/HTTP, ...) and every client. "Overloaded" describes the server, whatever protocol
    /// a request came in on: CPU, memory and the storage backend are shared. Don't split it
    /// per transport. That would silently multiply the real capacity with each transport
    /// added, and break the memory bound (about slots × max message size).
    /// </para>
    /// <para>
    /// Known gap: a single noisy client can take every slot. The fix would be a per-client
    /// limit INSIDE this one, keyed by the authenticated identity (to discuss after TLS/auth).
    /// </para>
    /// </summary>
    public int MaxConcurrentExports { get; set; } = 64;

    /// <summary>
    /// The delay sent back to throttled clients (gRPC <c>RetryInfo.retry_delay</c>, HTTP <c>Retry-After</c>). The spec
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
