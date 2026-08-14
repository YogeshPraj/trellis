using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Trellis.Routing.Capabilities;

namespace Trellis.Routing.Selection;

/// <summary>Counts requests currently in flight per endpoint, for <see cref="LeastLoadedSelectionStrategy"/>.</summary>
internal sealed class InFlightTracker
{
    private readonly ConcurrentDictionary<string, StrongBox<int>> _counts = new();

    public int Count(string endpointName) =>
        _counts.TryGetValue(endpointName, out StrongBox<int>? box) ? Volatile.Read(ref box.Value) : 0;

    /// <summary>Marks one request in flight until the returned lease is disposed.</summary>
    public Lease Acquire(string endpointName)
    {
        StrongBox<int> box = _counts.GetOrAdd(endpointName, _ => new StrongBox<int>(0));
        Interlocked.Increment(ref box.Value);
        return new Lease(box);
    }

    /// <summary>Releases exactly once, however the request ends — success, failure, or abandonment.</summary>
    internal sealed class Lease(StrongBox<int> box) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Interlocked.Decrement(ref box.Value);
            }
        }
    }
}
