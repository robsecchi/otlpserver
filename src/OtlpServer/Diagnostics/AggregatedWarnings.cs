using System.Collections.Concurrent;

namespace OtlpServer.Diagnostics;

/// <summary>
/// Warnings that can happen thousands of times per second (bad data, failed authentication,
/// gzip bombs...), logged without flooding the log:
/// <code>
///  first occurrence of (event, source) in an interval ──► logged at once, with its details
///  repeats in the same interval                       ──► only counted
///  end of the interval (every 10 s)                   ──► one summary line per (event, source) that repeated:
///      "Partially rejected logs export (billing-service): 3,412 more time(s) in the last 10 s, 8,904 records rejected"
/// </code>
/// <para>
/// So the first sign of a problem shows up right away, and a flood costs one line per source
/// per interval instead of one per request. A log line per event at those rates would itself
/// become a performance problem, and would bury everything else in the log.
/// </para>
/// <para>
/// Memory is bounded: at most <see cref="MaxSources"/> (event, source) pairs per interval.
/// Beyond that (e.g. an attack from many addresses), further sources are counted together as
/// "(other sources)". Counts are approximate when a report races with the end of an interval.
/// </para>
/// </summary>
public sealed class AggregatedWarnings(ILogger<AggregatedWarnings> logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    public const int MaxSources = 1000;
    private const string OtherSources = "other sources"; // shown as "(other sources)"

    private ConcurrentDictionary<(string Event, string Source), Entry> _entries = new();

    /// <param name="event">What happened, e.g. "Rejected OTLP export: invalid API key". A fixed text: it is the grouping key.</param>
    /// <param name="source">Who caused it: client name, remote address, STS URL...</param>
    /// <param name="detail">Details of THIS occurrence; only the first one per interval is logged.</param>
    /// <param name="amount">Optional quantity to add up across occurrences (e.g. records rejected).</param>
    /// <param name="amountUnit">What <paramref name="amount"/> counts, e.g. "records rejected".</param>
    public void Report(string @event, string source, string detail, long amount = 0, string? amountUnit = null)
    {
        var entries = Volatile.Read(ref _entries);
        var key = (@event, source);

        if (entries.TryGetValue(key, out var existing))
        {
            existing.Add(amount);
            return;
        }

        if (entries.Count >= MaxSources)
        {
            key = (@event, OtherSources);
        }

        var created = new Entry(amount, amountUnit);
        if (entries.TryAdd(key, created))
        {
            logger.LogWarning("{Event} ({Source}): {Detail}", @event, key.Item2, detail);
        }
        else
        {
            entries[key].Add(amount); // another thread added it first: just count
        }
    }

    /// <summary>Ends the interval: one summary line for each (event, source) that happened more than once.</summary>
    public void Flush()
    {
        var ended = Interlocked.Exchange(ref _entries, new ConcurrentDictionary<(string, string), Entry>());
        foreach (var ((@event, source), entry) in ended)
        {
            var repeats = Interlocked.Read(ref entry.Occurrences) - 1;
            if (repeats <= 0) continue;

            // Counts are structured values (and locale-independent), so log tools can filter on them.
            if (entry.AmountUnit is null)
            {
                logger.LogWarning("{Event} ({Source}): {Repeats} more time(s) in the last {Seconds} s",
                    @event, source, repeats, (int)Interval.TotalSeconds);
            }
            else
            {
                logger.LogWarning("{Event} ({Source}): {Repeats} more time(s) in the last {Seconds} s, {Total} {Unit} in total",
                    @event, source, repeats, (int)Interval.TotalSeconds, Interlocked.Read(ref entry.Amount), entry.AmountUnit);
            }
        }
    }

    private sealed class Entry(long amount, string? amountUnit)
    {
        public long Occurrences = 1;
        public long Amount = amount;
        public string? AmountUnit { get; } = amountUnit;

        public void Add(long amount)
        {
            Interlocked.Increment(ref Occurrences);
            if (amount != 0) Interlocked.Add(ref Amount, amount);
        }
    }
}

/// <summary>Ends an interval every <see cref="AggregatedWarnings.Interval"/>, and once more at shutdown.</summary>
public sealed class AggregatedWarningsFlusher(AggregatedWarnings warnings) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(AggregatedWarnings.Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                warnings.Flush();
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            warnings.Flush(); // don't lose the last interval's summary
        }
    }
}
