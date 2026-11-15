namespace OtlpServer.Admission;

/// <summary>
/// Admission control (backpressure) for OTLP exports, bound from "Otlp:Admission". Applies to
/// every OTLP export endpoint on every transport.
///
/// <para>
/// A slot is held for the WHOLE request: reading the body, decompressing, decoding,
/// validating (and later storing). That is what bounds memory and CPU: memory grows with
/// MaxConcurrentExports × (memory per export).
/// </para>
/// <para>
/// UNVERIFIED: memory per export is not known yet. A naive estimate (wire + decompressed +
/// parsed messages ≈ 16 MiB for a 4 MiB message) is contradicted by the S2.1 load test,
/// where 8 concurrent 4 MiB exports reached ~2.2 GB of RSS. How much of that is live data and
/// how much is memory the GC keeps in reserve is being measured (S2.2). Until then, size the
/// container from a load test of your own batch sizes, not from a formula.
/// </para>
/// <para>
/// Beyond about 2 slots per core, more slots don't add throughput (the baseline showed
/// throughput peaking at ~8 concurrent exports on 11 cores); they only add queueing and memory.
/// </para>
/// </summary>
public sealed class AdmissionOptions
{
    public const string SectionName = "Otlp:Admission";

    /// <summary>
    /// Exports processed at the same time, server-wide (all transports, all clients). Beyond
    /// it, an export is refused at once with "server busy, retry after RetryDelay"
    /// (gRPC UNAVAILABLE + RetryInfo, HTTP 503 + Retry-After): the client keeps the data and
    /// backs off, instead of the server queueing without limit.
    /// <para>
    /// Unset (default): 2 × the CPU count the process may use, at least 8. In Kubernetes that
    /// follows the pod's CPU limit, so the default scales with the pod.
    /// </para>
    /// <para>
    /// ONE pool for all transports by design: overload is a property of the server, not of a
    /// protocol. Per-transport pools would silently multiply the real capacity.
    /// </para>
    /// </summary>
    public int? MaxConcurrentExports { get; set; }

    /// <summary>
    /// How long refused clients are asked to wait (gRPC RetryInfo.retry_delay, HTTP Retry-After).
    /// Long enough for the server to recover, short enough that clients don't drop data while
    /// they wait.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Optional per-client cap inside the server-wide limit (fairness).</summary>
    public PerClientAdmissionOptions PerClient { get; set; } = new();

    /// <summary>The server-wide limit actually used.</summary>
    public int EffectiveMaxConcurrentExports => MaxConcurrentExports ?? Math.Max(8, 2 * Environment.ProcessorCount);
}

/// <summary>
/// Per-client cap: at most N of the server-wide slots for any single client, so one client
/// can't take them all. Clients are identified by authentication (the API-key client name or
/// the JWT's client claim). Unauthenticated requests have no reliable identity, so they are
/// only subject to the server-wide limit.
/// <code>
///  "PerClient": { "MaxConcurrentExports": 8, "Overrides": { "cluster-a-gateway": 32 } }
/// </code>
/// Over the cap: "you're over your share, retry later" (gRPC RESOURCE_EXHAUSTED + RetryInfo,
/// HTTP 429 + Retry-After), a different signal from "server busy".
/// </summary>
public sealed class PerClientAdmissionOptions
{
    /// <summary>Cap for every client. Unset (default): no per-client cap.</summary>
    public int? MaxConcurrentExports { get; set; }

    /// <summary>Caps for specific clients (by name); they win over the default above.</summary>
    public Dictionary<string, int> Overrides { get; set; } = [];

    /// <summary>The cap for one client, or null when it has none.</summary>
    public int? CapFor(string client) => Overrides.TryGetValue(client, out var cap) ? cap : MaxConcurrentExports;
}
