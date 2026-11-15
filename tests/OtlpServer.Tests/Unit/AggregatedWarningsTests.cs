using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OtlpServer.Diagnostics;
using OtlpServer.Tests.Infrastructure;

namespace OtlpServer.Tests.Unit;

public sealed class AggregatedWarningsTests
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _logs = new();
    private readonly AggregatedWarnings _warnings;

    public AggregatedWarningsTests()
    {
        var factory = LoggerFactory.Create(b => b.AddProvider(new CapturingLoggerProvider(_logs)));
        _warnings = new AggregatedWarnings(factory.CreateLogger<AggregatedWarnings>());
    }

    private List<string> Lines => _logs.Select(l => l.Message).ToList();

    [Fact]
    public void First_occurrence_is_logged_at_once_with_its_details()
    {
        _warnings.Report("Partially rejected logs export", "billing-service", "2 of 5 log record(s) rejected");

        var line = Assert.Single(Lines);
        Assert.Equal("Partially rejected logs export (billing-service): 2 of 5 log record(s) rejected", line);
        Assert.All(_logs, l => Assert.Equal(LogLevel.Warning, l.Level));
    }

    [Fact]
    public void Repeats_are_counted_and_summarized_once_per_interval()
    {
        for (var i = 0; i < 100; i++)
        {
            _warnings.Report("Partially rejected logs export", "billing-service", $"detail {i}", amount: 3, amountUnit: "records rejected");
        }
        Assert.Single(Lines); // only the first one so far

        _warnings.Flush();

        Assert.Equal(2, Lines.Count);
        Assert.Equal("Partially rejected logs export (billing-service): 99 more time(s) in the last 10 s, 300 records rejected in total", Lines[1]);
    }

    [Fact]
    public void Single_occurrence_needs_no_summary()
    {
        _warnings.Report("Rejected OTLP export: invalid API key", "10.0.0.7", "no match");

        _warnings.Flush();

        Assert.Single(Lines);
    }

    [Fact]
    public void Sources_and_events_are_kept_apart()
    {
        _warnings.Report("Rejected OTLP export: invalid API key", "10.0.0.7", "x");
        _warnings.Report("Rejected OTLP export: invalid API key", "10.0.0.8", "x");
        _warnings.Report("Rejected OTLP export: invalid token", "10.0.0.7", "x");

        Assert.Equal(3, Lines.Count);
    }

    [Fact]
    public void A_new_interval_logs_the_first_occurrence_again()
    {
        _warnings.Report("Rejected OTLP export: invalid API key", "10.0.0.7", "first interval");
        _warnings.Flush();

        _warnings.Report("Rejected OTLP export: invalid API key", "10.0.0.7", "second interval");

        Assert.Contains(Lines, l => l.EndsWith("second interval"));
    }

    [Fact]
    public void Too_many_sources_are_grouped_as_other_sources()
    {
        // E.g. an attack from many addresses: memory stays bounded.
        for (var i = 0; i < AggregatedWarnings.MaxSources + 500; i++)
        {
            _warnings.Report("Rejected OTLP export: invalid API key", $"10.0.{i / 256}.{i % 256}", "x");
        }

        Assert.Equal(AggregatedWarnings.MaxSources + 1, Lines.Count); // one per tracked source, plus "(other sources)"
        _warnings.Flush();
        Assert.Contains(Lines, l => l.Contains("(other sources): 499 more time(s)"));
    }

    [Fact]
    public async Task Concurrent_reports_are_all_counted()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 10_000; i++) _warnings.Report("Rejected gzip body", "HTTP", "x", 1, "bombs");
        })));

        _warnings.Flush();

        Assert.Contains(Lines, l => l.Contains("79999 more time(s)") && l.Contains("80000 bombs in total"));
    }
}
