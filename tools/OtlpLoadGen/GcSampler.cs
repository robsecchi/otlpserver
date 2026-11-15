using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

namespace OtlpLoadGen;

/// <summary>
/// Reads the SERVER process's garbage-collector counters from outside, over EventPipe (the
/// same mechanism as <c>dotnet-counters</c>), so the server needs no changes.
///
/// <para>Why: peak RSS alone can't tell live data from memory the GC keeps but doesn't use.</para>
/// <code>
///  RSS            everything the OS has given the process (includes memory the GC keeps in reserve)
///  GC committed   memory the GC holds for its heaps
///  GC heap size   what's actually in the heaps: the closest to "live data" we can see
/// </code>
/// </summary>
public sealed class GcSampler : IDisposable
{
    private readonly EventPipeSession _session;
    private readonly Task _processing;
    private volatile bool _measuring;
    private double _timeInGcSum;
    private int _timeInGcSamples;

    private GcSampler(int pid)
    {
        var provider = new EventPipeProvider("System.Runtime", EventLevel.Informational, 0,
            new Dictionary<string, string> { ["EventCounterIntervalSec"] = "1" });
        _session = new DiagnosticsClient(pid).StartEventPipeSession([provider], requestRundown: false);

        var source = new EventPipeEventSource(_session.EventStream);
        source.Dynamic.All += OnEvent;
        _processing = Task.Run(source.Process);
    }

    public double PeakHeapMb { get; private set; }
    public double PeakCommittedMb { get; private set; }
    public double AllocatedBytes { get; private set; }
    public double Gen2Collections { get; private set; }
    public double MeanTimeInGcPercent => _timeInGcSamples == 0 ? double.NaN : _timeInGcSum / _timeInGcSamples;

    /// <summary>Null if the process can't be attached to (e.g. not a .NET process).</summary>
    public static GcSampler? TryStart(int pid)
    {
        try { return new GcSampler(pid); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"GC counters unavailable: {ex.Message}");
            return null;
        }
    }

    public void StartMeasuring() => _measuring = true;
    public void StopMeasuring() => _measuring = false;

    private void OnEvent(TraceEvent e)
    {
        if (!_measuring || e.EventName != "EventCounters") return;

        var fields = (IDictionary<string, object>)((IDictionary<string, object>)e.PayloadValue(0))["Payload"];
        double Mean() => Convert.ToDouble(fields["Mean"]);
        double Increment() => Convert.ToDouble(fields["Increment"]);

        switch ((string)fields["Name"])
        {
            case "gc-heap-size": PeakHeapMb = Math.Max(PeakHeapMb, Mean()); break;   // MB
            case "gc-committed": PeakCommittedMb = Math.Max(PeakCommittedMb, Mean()); break; // MB
            case "alloc-rate": AllocatedBytes += Increment(); break;                     // bytes per interval
            case "gen-2-gc-count": Gen2Collections += Increment(); break;
            case "time-in-gc": _timeInGcSum += Mean(); _timeInGcSamples++; break;          // %
        }
    }

    public void Dispose()
    {
        try { _session.Stop(); } catch { /* the server may already be gone */ }
        _processing.Wait(TimeSpan.FromSeconds(2));
        _session.Dispose();
    }
}
