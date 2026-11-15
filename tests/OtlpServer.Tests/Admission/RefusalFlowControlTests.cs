using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OtlpServer.Tests.Infrastructure;
using static OtlpServer.Tests.Infrastructure.Requests;

namespace OtlpServer.Tests.Admission;

/// <summary>
/// Regression test for a hang the S2.1 load test found, on REAL sockets (the in-memory test
/// server has no HTTP/2 stream management, so it can't show it).
///
/// <para>What went wrong:</para>
/// <code>
///  32 concurrent exports on ONE HTTP/2 connection, limit 22
///    refused export ──► answered BEFORE its body is read (by design: no work for refusals)
///                   ──► the client is still uploading, so Kestrel RESETS the stream (RST_STREAM)
///    thousands of resets/s on one connection = what an HTTP/2 "rapid reset" attack
///    (CVE-2023-44487) looks like ──► Kestrel's protection answers ENHANCE_YOUR_CALM, then
///    closes the connection
///  client sees: RESOURCE_EXHAUSTED (from ENHANCE_YOUR_CALM), INTERNAL, and calls that hang
/// </code>
/// <para>
/// The fix: after answering a refusal, the rest of the body is read and discarded, so the
/// stream ends normally instead of being reset. A refusal must only ever look like
/// UNAVAILABLE (or the specific early error) to the client, and the connection must survive.
/// </para>
/// </summary>
public sealed class RefusalFlowControlTests
{
    private static readonly ExportLogsServiceRequest Big =
        WithRecords(Enumerable.Range(0, 1000).Select(i => ValidRecord(new string('x', 200) + i)).ToArray()); // ~250 KB

    /// <summary>32 workers on one connection for a few seconds; returns how often each status came back.</summary>
    private static async Task<ConcurrentDictionary<StatusCode, int>> Hammer(LogsService.LogsServiceClient client, CallOptions? options = null)
    {
        var outcomes = new ConcurrentDictionary<StatusCode, int>();
        var until = DateTime.UtcNow.AddSeconds(3);
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            while (DateTime.UtcNow < until)
            {
                var call = (options ?? new CallOptions()).WithDeadline(DateTime.UtcNow.AddSeconds(5));
                StatusCode status;
                try
                {
                    await client.ExportAsync(Big, call).ResponseAsync;
                    status = StatusCode.OK;
                }
                catch (RpcException ex)
                {
                    status = ex.StatusCode;
                }
                outcomes.AddOrUpdate(status, 1, (_, n) => n + 1);
            }
        })));
        return outcomes;
    }

    [Fact]
    public async Task Concurrent_refusals_keep_the_connection_healthy()
    {
        await using var server = KestrelOtlpServer.Start(tls: null,
            new Dictionary<string, string?> { ["Otlp:Admission:MaxConcurrentExports"] = "2" });
        using var channel = GrpcChannel.ForAddress(server.GrpcAddress); // one HTTP/2 connection
        var client = new LogsService.LogsServiceClient(channel);

        var outcomes = await Hammer(client);

        var summary = string.Join(", ", outcomes.Select(kv => $"{kv.Key}:{kv.Value}"));
        Assert.True(outcomes.GetValueOrDefault(StatusCode.Unavailable) > 0, $"expected refusals: {summary}");
        Assert.True(outcomes.GetValueOrDefault(StatusCode.OK) > 0, $"expected successes: {summary}");
        // Only those two: no ENHANCE_YOUR_CALM (ResourceExhausted), no torn connection
        // (Internal), no hang (DeadlineExceeded).
        Assert.True(outcomes.Keys.All(k => k is StatusCode.OK or StatusCode.Unavailable), summary);
    }

    [Fact]
    public async Task Concurrent_unauthenticated_exports_keep_the_connection_healthy()
    {
        // Same mechanism, other early refusal: UNAUTHENTICATED is also answered before the body.
        await using var server = KestrelOtlpServer.Start(tls: null, ApiKeys.Enabled());
        using var channel = GrpcChannel.ForAddress(server.GrpcAddress);
        var client = new LogsService.LogsServiceClient(channel);

        var outcomes = await Hammer(client);

        var summary = string.Join(", ", outcomes.Select(kv => $"{kv.Key}:{kv.Value}"));
        Assert.True(outcomes.Keys.All(k => k is StatusCode.Unauthenticated), summary);

        // And the connection still serves an authenticated export afterwards.
        var after = await client.ExportAsync(Big, ApiKeys.Grpc(ApiKeys.BillingKey).WithDeadline(DateTime.UtcNow.AddSeconds(5)));
        Assert.Null(after.PartialSuccess);
    }
}
