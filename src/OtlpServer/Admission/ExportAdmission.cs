using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace OtlpServer.Admission;

/// <summary>
/// Counts exports in flight, server-wide and per client, and decides whether one more may
/// start. Never waits: it answers yes or no immediately. Refusing quickly with "retry after X"
/// lets clients back off and keep the data in their own buffers; queueing here would pile
/// requests up in memory while the clients time out anyway.
///
/// <code>
///  TryEnter(client)
///    1. per-client cap (if this client has one):  in flight for client &lt; cap ?   no → QuotaExceeded
///    2. server-wide limit:                        in flight overall    &lt; max ?   no → Busy
///    → Admitted: the lease releases both counters when disposed
/// </code>
/// <para>
/// The per-client check comes FIRST, so a client that is over its own share is refused without
/// ever taking a server-wide slot: it can't push other clients out.
/// </para>
/// <para>
/// Per-client counters live in a dictionary and an entry is REMOVED as soon as its count
/// drops back to zero, so memory follows the number of clients currently exporting, not the
/// number ever seen.
/// </para>
/// </summary>
public sealed class ExportAdmission
{
    private readonly int _max;
    private readonly PerClientAdmissionOptions _perClient;
    private readonly ConcurrentDictionary<string, int> _perClientInFlight = new(StringComparer.Ordinal);
    private int _inFlight;

    public ExportAdmission(IOptions<AdmissionOptions> options)
    {
        _max = options.Value.EffectiveMaxConcurrentExports;
        _perClient = options.Value.PerClient;
    }

    public int MaxConcurrentExports => _max;

    /// <summary>Exports in flight right now (for diagnostics and tests).</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>Clients with at least one export in flight (for diagnostics and tests).</summary>
    public int TrackedClients => _perClientInFlight.Count;

    /// <param name="client">The authenticated client, or null (no per-client cap then).</param>
    public AdmissionDecision TryEnter(string? client, out Lease lease)
    {
        lease = default;
        var cap = client is null ? null : _perClient.CapFor(client);

        if (cap is { } perClientCap && !TryIncrementClient(client!, perClientCap))
        {
            return AdmissionDecision.QuotaExceeded;
        }

        if (Interlocked.Increment(ref _inFlight) > _max)
        {
            Interlocked.Decrement(ref _inFlight);
            if (cap is not null) DecrementClient(client!);
            return AdmissionDecision.Busy;
        }

        lease = new Lease(this, cap is null ? null : client);
        return AdmissionDecision.Admitted;
    }

    private bool TryIncrementClient(string client, int cap)
    {
        while (true)
        {
            if (_perClientInFlight.TryGetValue(client, out var current))
            {
                if (current >= cap) return false;
                if (_perClientInFlight.TryUpdate(client, current + 1, current)) return true;
            }
            else if (_perClientInFlight.TryAdd(client, 1))
            {
                return true;
            }
            // Another thread changed the entry in between: read it again.
        }
    }

    private void DecrementClient(string client)
    {
        while (true)
        {
            var current = _perClientInFlight[client];
            if (current == 1)
            {
                // Remove only if it is STILL 1 (an atomic compare-and-remove), so a concurrent
                // increment can't be lost.
                if (_perClientInFlight.TryRemove(KeyValuePair.Create(client, 1))) return;
            }
            else if (_perClientInFlight.TryUpdate(client, current - 1, current))
            {
                return;
            }
        }
    }

    /// <summary>One admitted export. Dispose exactly once, whatever happens to the request.</summary>
    public readonly struct Lease(ExportAdmission? admission, string? client) : IDisposable
    {
        public void Dispose()
        {
            if (admission is null) return;
            Interlocked.Decrement(ref admission._inFlight);
            if (client is not null) admission.DecrementClient(client);
        }
    }
}

public enum AdmissionDecision
{
    Admitted,

    /// <summary>The server-wide limit is reached: "server busy" (UNAVAILABLE / 503).</summary>
    Busy,

    /// <summary>This client is at its own cap: "over your share" (RESOURCE_EXHAUSTED / 429).</summary>
    QuotaExceeded,
}
