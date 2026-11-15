using System.Net;
using Google.Protobuf;
using Google.Rpc;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using OtlpServer.Admission;
using OtlpServer.Logs.Validation;
using OtlpServer.Tests.Infrastructure;
using static OtlpServer.Tests.Infrastructure.OtlpHttpClientExtensions;
using static OtlpServer.Tests.Infrastructure.Requests;

namespace OtlpServer.Tests.Admission;

/// <summary>
/// Admission end to end: the slot covers the WHOLE request (the bug S1 found), the default
/// limit, and the per-client cap with its own signal (429 / RESOURCE_EXHAUSTED + RetryInfo).
/// </summary>
public sealed class AdmissionTests
{
    // ---- The slot covers the whole request ---------------------------------------------------

    [Fact]
    public async Task Slot_is_held_while_the_body_is_still_arriving()
    {
        // The regression test for the S1 finding. Before, admission happened in the core
        // handler, AFTER the body had been read and decoded, so a slow (or huge) upload held
        // memory without holding a slot. Now the slot is taken before the first byte is read.
        await using var server = OtlpTestServer.Start(new Dictionary<string, string?> { ["Otlp:Admission:MaxConcurrentExports"] = "1" });
        var admission = server.Services.GetRequiredService<ExportAdmission>();

        var body = WithRecords(ValidRecord()).ToByteArray();
        var slowBody = new SlowContent(body);
        var slow = server.Http.PostLogsAsync(slowBody);

        await slowBody.FirstPartSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntil(() => admission.InFlight == 1);

        // The upload isn't finished, yet the only slot is taken: the next export is refused.
        using var refused = await server.Http.PostLogsAsync(WithRecords(ValidRecord()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);

        slowBody.SendRest.SetResult();
        using var finished = await slow;
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        Assert.Equal(0, admission.InFlight);
    }

    [Fact]
    public async Task Default_limit_is_twice_the_cpu_count_with_a_minimum_of_8()
    {
        await using var server = OtlpTestServer.Start();

        var admission = server.Services.GetRequiredService<ExportAdmission>();

        Assert.Equal(Math.Max(8, 2 * Environment.ProcessorCount), admission.MaxConcurrentExports);
        Assert.Contains(server.Logs, l => l.Message.Contains($"at most {admission.MaxConcurrentExports} concurrent exports"));
    }

    [Fact]
    public async Task Non_export_requests_are_not_subject_to_admission()
    {
        // 404 / 405 never take a slot, even with no slots free.
        var gate = new GatedValidator();
        await using var server = OtlpTestServer.Start(
            new Dictionary<string, string?> { ["Otlp:Admission:MaxConcurrentExports"] = "1" },
            s => s.AddSingleton<ILogRecordValidator>(gate));
        var holding = server.Http.PostLogsAsync(WithRecords(ValidRecord()));
        Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(10)));

        using var notFound = await server.Http.PostAsync("/v1/traces", new ByteArrayContent([]));
        using var wrongMethod = await server.Http.GetAsync("/v1/logs");

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        gate.Release.Set();
        (await holding).Dispose();
    }

    // ---- Per-client cap ----------------------------------------------------------------------

    private static Dictionary<string, string?> WithPerClientCap(int global, int perClient, params (string Client, int Cap)[] overrides)
    {
        var settings = ApiKeys.Enabled();
        settings["Otlp:Admission:MaxConcurrentExports"] = global.ToString();
        settings["Otlp:Admission:PerClient:MaxConcurrentExports"] = perClient.ToString();
        settings["Otlp:Admission:RetryDelay"] = "00:00:02";
        foreach (var (client, cap) in overrides)
        {
            settings[$"Otlp:Admission:PerClient:Overrides:{client}"] = cap.ToString();
        }
        return settings;
    }

    [Fact]
    public async Task Client_over_its_cap_gets_429_while_other_clients_pass()
    {
        var gate = new GatedValidator(blockFirst: 1);
        await using var server = OtlpTestServer.Start(WithPerClientCap(global: 4, perClient: 1),
            s => s.AddSingleton<ILogRecordValidator>(gate));

        // billing-service's first export is held: it is at its cap of 1.
        var holding = server.Client.ExportAsync(WithRecords(ValidRecord()), ApiKeys.Grpc(ApiKeys.BillingKey)).ResponseAsync;
        Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(10)));

        using var billingAgain = await server.Http.WithKey(ApiKeys.BillingKey).PostLogsAsync(WithRecords(ValidRecord()));
        var frontend = await server.Client.ExportAsync(WithRecords(ValidRecord()), ApiKeys.Grpc(ApiKeys.FrontendKey));

        // "You're over your share", retryable: 429 + Retry-After, not "server busy".
        Assert.Equal(HttpStatusCode.TooManyRequests, billingAgain.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(2), billingAgain.Headers.RetryAfter?.Delta);
        var status = await billingAgain.ReadStatusAsync();
        Assert.Equal((int)Code.ResourceExhausted, status.Code);
        Assert.Contains("'billing-service'", status.Message);
        Assert.Null(frontend.PartialSuccess); // another client is unaffected

        gate.Release.Set();
        await holding;
    }

    [Fact]
    public async Task Over_cap_over_grpc_is_resource_exhausted_with_retry_info()
    {
        // RESOURCE_EXHAUSTED is retryable ONLY with RetryInfo: here it must be present.
        var gate = new GatedValidator(blockFirst: 1);
        await using var server = OtlpTestServer.Start(WithPerClientCap(global: 4, perClient: 1),
            s => s.AddSingleton<ILogRecordValidator>(gate));
        var holding = server.Client.ExportAsync(WithRecords(ValidRecord()), ApiKeys.Grpc(ApiKeys.BillingKey)).ResponseAsync;
        Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(10)));

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            server.Client.ExportAsync(WithRecords(ValidRecord()), ApiKeys.Grpc(ApiKeys.BillingKey)).ResponseAsync);

        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(2), ex.GetRpcStatus()?.GetDetail<RetryInfo>()?.RetryDelay.ToTimeSpan());

        gate.Release.Set();
        await holding;
    }

    [Fact]
    public async Task Per_client_check_comes_before_the_server_wide_one()
    {
        // One server-wide slot, taken by billing-service. Its next export says "over YOUR
        // share" (429), and never touched the shared slot; frontend's says "server busy" (503).
        var gate = new GatedValidator(blockFirst: 1);
        await using var server = OtlpTestServer.Start(WithPerClientCap(global: 1, perClient: 1),
            s => s.AddSingleton<ILogRecordValidator>(gate));
        var holding = server.Http.WithKey(ApiKeys.BillingKey).PostLogsAsync(WithRecords(ValidRecord()));
        Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(10)));

        var billing = await Assert.ThrowsAsync<RpcException>(() =>
            server.Client.ExportAsync(WithRecords(ValidRecord()), ApiKeys.Grpc(ApiKeys.BillingKey)).ResponseAsync);
        var frontend = await Assert.ThrowsAsync<RpcException>(() =>
            server.Client.ExportAsync(WithRecords(ValidRecord()), ApiKeys.Grpc(ApiKeys.FrontendKey)).ResponseAsync);

        Assert.Equal(StatusCode.ResourceExhausted, billing.StatusCode);
        Assert.Equal(StatusCode.Unavailable, frontend.StatusCode);

        gate.Release.Set();
        (await holding).Dispose();
    }

    [Fact]
    public async Task Override_gives_a_heavy_client_a_bigger_cap()
    {
        var gate = new GatedValidator(blockFirst: 2);
        await using var server = OtlpTestServer.Start(WithPerClientCap(global: 8, perClient: 1, ("billing-service", 2)),
            s => s.AddSingleton<ILogRecordValidator>(gate));
        var held = Enumerable.Range(0, 2)
            .Select(_ => server.Client.ExportAsync(WithRecords(ValidRecord()), ApiKeys.Grpc(ApiKeys.BillingKey)).ResponseAsync)
            .ToList();
        Assert.True(await gate.EnteredEach.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await gate.EnteredEach.WaitAsync(TimeSpan.FromSeconds(10)));

        var third = await Assert.ThrowsAsync<RpcException>(() =>
            server.Client.ExportAsync(WithRecords(ValidRecord()), ApiKeys.Grpc(ApiKeys.BillingKey)).ResponseAsync);

        Assert.Equal(StatusCode.ResourceExhausted, third.StatusCode); // two allowed, the third refused
        gate.Release.Set();
        await Task.WhenAll(held);
    }

    [Fact]
    public async Task Without_authentication_only_the_server_wide_limit_applies()
    {
        // No reliable identity: a per-client cap can't apply (all would be "anonymous").
        var gate = new GatedValidator(blockFirst: 1);
        await using var server = OtlpTestServer.Start(new Dictionary<string, string?>
        {
            ["Otlp:Admission:MaxConcurrentExports"] = "4",
            ["Otlp:Admission:PerClient:MaxConcurrentExports"] = "1",
        }, s => s.AddSingleton<ILogRecordValidator>(gate));
        var holding = server.Client.ExportAsync(WithRecords(ValidRecord())).ResponseAsync;
        Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(10)));

        var second = await server.Client.ExportAsync(WithRecords(ValidRecord()));

        Assert.Null(second.PartialSuccess);
        gate.Release.Set();
        await holding;
    }

    // ---- Configuration -------------------------------------------------------------------------

    [Theory]
    [InlineData("Otlp:Admission:MaxConcurrentExports", "0", "MaxConcurrentExports must be > 0")]
    [InlineData("Otlp:Admission:RetryDelay", "00:00:00", "RetryDelay must be positive")]
    [InlineData("Otlp:Admission:PerClient:MaxConcurrentExports", "0", "PerClient:MaxConcurrentExports must be > 0")]
    [InlineData("Otlp:Admission:PerClient:Overrides:billing-service", "-1", "Overrides values must be > 0")]
    public void Invalid_settings_fail_at_startup(string key, string value, string expected)
    {
        var ex = Assert.ThrowsAny<Exception>(() => OtlpTestServer.Start(new Dictionary<string, string?> { [key] = value }));

        Assert.Contains(expected, ex.ToString());
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    /// <summary>A body sent in two parts: the second only when the test says so.</summary>
    private sealed class SlowContent : HttpContent
    {
        private readonly byte[] _body;

        public SlowContent(byte[] body)
        {
            _body = body;
            Headers.TryAddWithoutValidation("Content-Type", Protobuf);
        }

        public TaskCompletionSource FirstPartSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SendRest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(_body.AsMemory(0, _body.Length / 2));
            await stream.FlushAsync();
            FirstPartSent.SetResult();
            await SendRest.Task;
            await stream.WriteAsync(_body.AsMemory(_body.Length / 2));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _body.Length;
            return true;
        }
    }
}
