using System.Net;
using Google.Rpc;
using Microsoft.Extensions.DependencyInjection;
using OtlpServer.Logs.Validation;
using OtlpServer.Tests.Infrastructure;
using static OtlpServer.Tests.Infrastructure.Requests;

namespace OtlpServer.Tests.Http;

/// <summary>
/// Spec: OTLP/HTTP "Throttling": 429 or 503, optionally with Retry-After. Also checks
/// that the concurrency limit is ONE pool shared by both transports.
/// </summary>
public sealed class HttpThrottlingTests
{
    private static readonly Dictionary<string, string?> OneSlot = new()
    {
        ["Otlp:Logs:MaxConcurrentExports"] = "1",
        // Not a whole number of seconds, to check the header rounds UP.
        ["Otlp:Logs:ThrottleRetryDelay"] = "00:00:02.500",
    };

    [Fact]
    public async Task Export_beyond_the_limit_is_503_with_retry_after_rounded_up()
    {
        var gate = new GatedValidator();
        await using var server = OtlpTestServer.Start(OneSlot, s => s.AddSingleton<ILogRecordValidator>(gate));

        var first = server.Http.PostLogsAsync(WithRecords(ValidRecord()));
        Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(10)), "first export never reached validation");

        using var throttled = await server.Http.PostLogsAsync(WithRecords(ValidRecord()));

        // 503 is one of the four retryable codes (429, 502, 503, 504).
        Assert.Equal(HttpStatusCode.ServiceUnavailable, throttled.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(3), throttled.Headers.RetryAfter?.Delta);
        var status = await throttled.ReadStatusAsync();
        Assert.Equal((int)Code.Unavailable, status.Code);

        gate.Release.Set();
        using var firstResponse = await first;
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
    }

    [Fact]
    public async Task Limit_is_shared_between_grpc_and_http()
    {
        var gate = new GatedValidator();
        await using var server = OtlpTestServer.Start(OneSlot, s => s.AddSingleton<ILogRecordValidator>(gate));

        // The only slot is taken by a gRPC export...
        var grpcCall = server.Client.ExportAsync(WithRecords(ValidRecord())).ResponseAsync;
        Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(10)), "gRPC export never reached validation");

        // ...so an HTTP export is throttled: one server-wide pool, not one per transport.
        using var throttled = await server.Http.PostLogsAsync(WithRecords(ValidRecord()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, throttled.StatusCode);

        gate.Release.Set();
        Assert.Null((await grpcCall).PartialSuccess);
    }
}
