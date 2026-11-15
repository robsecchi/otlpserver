using Google.Rpc;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using OtlpServer.Logs.Validation;
using OtlpServer.Tests.Infrastructure;

namespace OtlpServer.Tests.Grpc;

/// <summary>
/// Backpressure. Spec: OTLP/gRPC "Throttling": UNAVAILABLE, optionally with RetryInfo.
///
/// <para>
/// Instead of a timing-based delay, the first export is held inside the server by a
/// <see cref="GatedValidator"/>. The test knows exactly when the only slot is taken, so it
/// can't be flaky.
/// </para>
/// <code>
///  call 1 ──► takes the slot ──► validator: Entered.Set(), waits on Release ...
///  call 2 ──► no free slot   ──► UNAVAILABLE + RetryInfo(3s)
///  test   ──► Release.Set()  ──► call 1 finishes OK, slot freed
///  call 3 ──► OK
/// </code>
/// </summary>
public sealed class ThrottlingTests
{
    private static readonly Dictionary<string, string?> OneSlot = new()
    {
        ["Otlp:Admission:MaxConcurrentExports"] = "1",
        ["Otlp:Admission:RetryDelay"] = "00:00:03",
    };

    [Fact]
    public async Task Export_beyond_the_concurrency_limit_is_unavailable_with_retry_info()
    {
        var gate = new GatedValidator();
        await using var server = OtlpTestServer.Start(OneSlot, s => s.AddSingleton<ILogRecordValidator>(gate));
        var request = Requests.WithRecords(Requests.ValidRecord());

        var first = server.Client.ExportAsync(request).ResponseAsync;
        Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(10)), "first export never reached validation");

        var ex = await Assert.ThrowsAsync<RpcException>(() => server.Client.ExportAsync(request).ResponseAsync);

        // UNAVAILABLE is retryable; RetryInfo tells the client how long to wait.
        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
        var retryInfo = ex.GetRpcStatus()?.GetDetail<RetryInfo>();
        Assert.NotNull(retryInfo);
        Assert.Equal(TimeSpan.FromSeconds(3), retryInfo.RetryDelay.ToTimeSpan());

        gate.Release.Set();
        var firstResponse = await first;
        Assert.Null(firstResponse.PartialSuccess);

        // The slot was given back, so the next export is admitted.
        var third = await server.Client.ExportAsync(request);
        Assert.Null(third.PartialSuccess);
    }

    [Fact]
    public async Task Slot_is_released_after_a_failed_export()
    {
        await using var server = OtlpTestServer.Start(OneSlot);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            server.Client.ExportAsync(Requests.WithRecords(Requests.RecordWithDuplicateKey())).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);

        // If the failure had leaked the only slot, this would be UNAVAILABLE.
        var response = await server.Client.ExportAsync(Requests.WithRecords(Requests.ValidRecord()));
        Assert.Null(response.PartialSuccess);
    }
}
