using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using OtlpServer.Admission;

namespace OtlpServer.Tests.Unit;

public sealed class ExportAdmissionTests
{
    private static ExportAdmission Admission(int max, int? perClient = null, Dictionary<string, int>? overrides = null) =>
        new(Options.Create(new AdmissionOptions
        {
            MaxConcurrentExports = max,
            PerClient = new PerClientAdmissionOptions { MaxConcurrentExports = perClient, Overrides = overrides ?? [] },
        }));

    [Fact]
    public void Server_wide_limit_refuses_with_busy_and_frees_slots_on_dispose()
    {
        var admission = Admission(max: 2);

        Assert.Equal(AdmissionDecision.Admitted, admission.TryEnter(null, out var a));
        Assert.Equal(AdmissionDecision.Admitted, admission.TryEnter(null, out var b));
        Assert.Equal(AdmissionDecision.Busy, admission.TryEnter(null, out _));
        Assert.Equal(2, admission.InFlight);

        a.Dispose();
        Assert.Equal(AdmissionDecision.Admitted, admission.TryEnter(null, out var c));

        b.Dispose();
        c.Dispose();
        Assert.Equal(0, admission.InFlight);
    }

    [Fact]
    public void Per_client_cap_refuses_with_quota_exceeded_while_other_clients_pass()
    {
        var admission = Admission(max: 10, perClient: 1);

        Assert.Equal(AdmissionDecision.Admitted, admission.TryEnter("billing", out var billing));
        Assert.Equal(AdmissionDecision.QuotaExceeded, admission.TryEnter("billing", out _));
        Assert.Equal(AdmissionDecision.Admitted, admission.TryEnter("frontend", out var frontend));

        billing.Dispose();
        frontend.Dispose();
    }

    [Fact]
    public void Quota_refusal_does_not_take_a_server_wide_slot()
    {
        var admission = Admission(max: 10, perClient: 1);
        admission.TryEnter("billing", out var first);

        admission.TryEnter("billing", out _);

        Assert.Equal(1, admission.InFlight); // only the admitted one
        first.Dispose();
    }

    [Fact]
    public void Busy_refusal_gives_back_the_per_client_count()
    {
        var admission = Admission(max: 1, perClient: 5);
        admission.TryEnter("billing", out var first);

        Assert.Equal(AdmissionDecision.Busy, admission.TryEnter("billing", out _));

        first.Dispose();
        // If the refused attempt had leaked its per-client count, "billing" would still be tracked.
        Assert.Equal(0, admission.TrackedClients);
    }

    [Fact]
    public void Overrides_win_over_the_default_cap()
    {
        var admission = Admission(max: 10, perClient: 1, overrides: new() { ["gateway"] = 3 });

        var leases = Enumerable.Range(0, 3).Select(_ =>
        {
            Assert.Equal(AdmissionDecision.Admitted, admission.TryEnter("gateway", out var lease));
            return lease;
        }).ToList();
        Assert.Equal(AdmissionDecision.QuotaExceeded, admission.TryEnter("gateway", out _));

        leases.ForEach(l => l.Dispose());
    }

    [Fact]
    public void Overrides_alone_cap_only_the_listed_clients()
    {
        var admission = Admission(max: 10, perClient: null, overrides: new() { ["noisy"] = 1 });
        admission.TryEnter("noisy", out var noisy);
        admission.TryEnter("quiet", out var q1);

        Assert.Equal(AdmissionDecision.QuotaExceeded, admission.TryEnter("noisy", out _));
        Assert.Equal(AdmissionDecision.Admitted, admission.TryEnter("quiet", out var q2));

        noisy.Dispose(); q1.Dispose(); q2.Dispose();
    }

    [Fact]
    public void Anonymous_requests_have_no_per_client_cap()
    {
        var admission = Admission(max: 10, perClient: 1);

        admission.TryEnter(null, out var a);
        Assert.Equal(AdmissionDecision.Admitted, admission.TryEnter(null, out var b));
        Assert.Equal(0, admission.TrackedClients);

        a.Dispose(); b.Dispose();
    }

    [Fact]
    public void Default_lease_disposes_harmlessly()
    {
        var admission = Admission(max: 1);
        admission.TryEnter(null, out var lease);
        admission.TryEnter(null, out var refused); // refused: a default lease

        refused.Dispose();

        Assert.Equal(1, admission.InFlight); // the refused lease didn't release someone else's slot
        lease.Dispose();
    }

    [Fact]
    public async Task Under_contention_caps_hold_and_everything_is_released()
    {
        // 16 threads hammering 20 clients: no client ever exceeds its cap, the server never
        // exceeds its limit, and at the end every counter is back to zero (no leaked entries).
        const int max = 12, perClient = 2, clients = 20;
        var admission = Admission(max, perClient);
        var current = new ConcurrentDictionary<string, int>();
        var overall = 0;
        var violations = 0;

        await Task.WhenAll(Enumerable.Range(0, 16).Select(t => Task.Run(() =>
        {
            var random = new Random(t);
            for (var i = 0; i < 20_000; i++)
            {
                var client = $"client-{random.Next(clients)}";
                if (admission.TryEnter(client, out var lease) != AdmissionDecision.Admitted) continue;

                if (current.AddOrUpdate(client, 1, (_, n) => n + 1) > perClient) Interlocked.Increment(ref violations);
                if (Interlocked.Increment(ref overall) > max) Interlocked.Increment(ref violations);

                Interlocked.Decrement(ref overall);
                current.AddOrUpdate(client, 0, (_, n) => n - 1);
                lease.Dispose();
            }
        })));

        Assert.Equal(0, violations);
        Assert.Equal(0, admission.InFlight);
        Assert.Equal(0, admission.TrackedClients);
    }
}
