using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Trellis.Routing.Capabilities;

namespace Trellis.Routing.Selection;

/// <summary>Per-process latency/outcome tracking feeding <see cref="ISelectionContext"/>.</summary>
internal sealed class MetricsTracker
{
    private sealed class Entry
    {
        public readonly object Lock = new();
        public double EmaLatencyMs;
        public long Successes;
        public long Failures;
    }

    private const double Alpha = 0.2;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public void Record(string endpointName, double latencyMs, bool success)
    {
        Entry entry = _entries.GetOrAdd(endpointName, _ => new Entry());
        lock (entry.Lock)
        {
            entry.EmaLatencyMs = entry.EmaLatencyMs == 0 ? latencyMs : ((1 - Alpha) * entry.EmaLatencyMs) + (Alpha * latencyMs);
            if (success)
            {
                entry.Successes++;
            }
            else
            {
                entry.Failures++;
            }
        }
    }

    public EndpointMetricsSnapshot Snapshot(string endpointName)
    {
        if (!_entries.TryGetValue(endpointName, out Entry? entry))
        {
            return new EndpointMetricsSnapshot(0, 0, 0);
        }
        lock (entry.Lock)
        {
            return new EndpointMetricsSnapshot(entry.EmaLatencyMs, entry.Successes, entry.Failures);
        }
    }
}
