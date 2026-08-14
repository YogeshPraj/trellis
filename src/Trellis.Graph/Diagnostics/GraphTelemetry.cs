using System.Diagnostics.Metrics;
using System.Diagnostics;

namespace Trellis.Graph.Diagnostics;

/// <summary>
/// OpenTelemetry instrumentation for graph runs: one span per run, one per node execution
/// (retries appear as sibling spans, so a retried node is visible rather than just slow),
/// plus duration, retry, and fallback metrics.
/// </summary>
/// <remarks>
/// Uses only <see cref="System.Diagnostics"/> primitives from the BCL, so
/// <c>Trellis.Graph</c> keeps its zero-dependency promise. Subscribe by name:
/// <c>.AddSource(GraphTelemetry.ActivitySourceName).AddMeter(GraphTelemetry.MeterName)</c>.
/// </remarks>
public static class GraphTelemetry
{
    /// <summary>Activity source name to subscribe to for graph and node spans.</summary>
    public const string ActivitySourceName = "Trellis.Graph";

    /// <summary>Meter name to subscribe to for graph metrics.</summary>
    public const string MeterName = "Trellis.Graph";

    internal static readonly ActivitySource Source = new(ActivitySourceName, "0.10.0");
    private static readonly Meter Meter = new(MeterName, "0.10.0");

    private static readonly Histogram<double> NodeDuration = Meter.CreateHistogram<double>(
        "trellis.graph.node.duration", "s", "Wall time of a single node execution.");

    private static readonly Counter<long> NodeRetries = Meter.CreateCounter<long>(
        "trellis.graph.node.retries", "{retry}", "Node attempts that failed and were retried.");

    private static readonly Counter<long> NodeFallbacks = Meter.CreateCounter<long>(
        "trellis.graph.node.fallbacks", "{fallback}", "Nodes that exhausted retries and fell back.");

    internal static Activity? StartRun(string threadId) =>
        Source.StartActivity("graph.run", ActivityKind.Internal)
            ?.SetTag("trellis.graph.thread_id", threadId);

    internal static Activity? StartNode(string node, int step, int attempt, ActivityContext parent)
    {
        Activity? activity = Source.StartActivity(
            $"graph.node {node}", ActivityKind.Internal, parent);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("trellis.graph.node", node);
            activity.SetTag("trellis.graph.step", step);
            activity.SetTag("trellis.graph.attempt", attempt);
        }
        return activity;
    }

    internal static void RecordNode(Activity? activity, string node, TimeSpan elapsed, Exception? error)
    {
        NodeDuration.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("trellis.graph.node", node),
            new KeyValuePair<string, object?>("error.type", error?.GetType().Name));

        if (error is not null && activity is not null)
        {
            activity.SetStatus(ActivityStatusCode.Error, error.Message);
            activity.SetTag("error.type", error.GetType().FullName);
        }
    }

    internal static void RecordRetry(string node) =>
        NodeRetries.Add(1, new KeyValuePair<string, object?>("trellis.graph.node", node));

    internal static void RecordFallback(string node, bool succeeded) =>
        NodeFallbacks.Add(
            1,
            new KeyValuePair<string, object?>("trellis.graph.node", node),
            new KeyValuePair<string, object?>("trellis.graph.fallback.succeeded", succeeded));
}
