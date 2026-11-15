using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OtlpServer.Diagnostics;
using OtlpServer.Tests.Infrastructure;
using static OtlpServer.Tests.Infrastructure.Requests;

namespace OtlpServer.Tests.Diagnostics;

/// <summary>
/// End to end: events that repeat on every request cost one line at first, plus one summary
/// per interval, never one line per request. And the per-export line is gone from Information.
/// </summary>
public sealed class HighRateLoggingTests
{
    private static List<string> Warnings(OtlpTestServer server, string containing) =>
        server.Logs.Where(l => l.Level == LogLevel.Warning && l.Message.Contains(containing)).Select(l => l.Message).ToList();

    [Fact]
    public async Task Bad_data_from_a_client_is_one_warning_plus_one_summary_naming_the_client()
    {
        await using var server = OtlpTestServer.Start(ApiKeys.Enabled());

        for (var i = 0; i < 50; i++)
        {
            await server.Client.ExportAsync(WithRecords(ValidRecord(), RecordWithDuplicateKey()), ApiKeys.Grpc(ApiKeys.BillingKey));
        }
        var beforeFlush = Warnings(server, "Partially rejected logs export");
        server.Services.GetRequiredService<AggregatedWarnings>().Flush();

        var first = Assert.Single(beforeFlush);
        Assert.Contains("(billing-service)", first);
        Assert.Contains("duplicate_attribute_key", first); // the first occurrence keeps its details
        Assert.Contains(Warnings(server, "Partially rejected logs export"),
            l => l.Contains("49 more time(s)") && l.Contains("50 records rejected in total"));
    }

    [Fact]
    public async Task Repeated_invalid_keys_are_one_warning_plus_one_summary()
    {
        await using var server = OtlpTestServer.Start(ApiKeys.Enabled());

        server.Http.WithKey("guess"); // once: WithKey ADDS a header each time it's called
        for (var i = 0; i < 20; i++)
        {
            using var _ = await server.Http.PostLogsAsync(WithRecords(ValidRecord()));
        }
        Assert.Single(Warnings(server, "invalid API key"));
        server.Services.GetRequiredService<AggregatedWarnings>().Flush();

        Assert.Contains(Warnings(server, "invalid API key"), l => l.Contains("19 more time(s)"));
    }

    [Fact]
    public async Task Successful_exports_log_nothing_at_information()
    {
        await using var server = OtlpTestServer.Start();

        for (var i = 0; i < 20; i++)
        {
            await server.Client.ExportAsync(WithRecords(ValidRecord()));
        }

        Assert.DoesNotContain(server.Logs, l => l.Level >= LogLevel.Information && l.Message.Contains("Logs export"));
    }
}
