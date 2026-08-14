using Microsoft.Extensions.AI;
using System.Diagnostics.Metrics;
using System.Diagnostics;

namespace Trellis.Diagnostics;

/// <summary>
/// Agent-level OpenTelemetry instrumentation: spans, metrics, and optional cost accounting.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately does <b>not</b> instrument the chat call itself — Microsoft.Extensions.AI
/// already does that via <c>UseOpenTelemetry()</c>, and duplicating it would double-count
/// tokens. What lives here is the layer above: a whole agent run, including self-healing
/// retries and validation, which no provider-level instrumentation can see.
/// </para>
/// <para>
/// Subscribe with the OpenTelemetry SDK by name:
/// <c>.AddSource(AgentTelemetry.ActivitySourceName).AddMeter(AgentTelemetry.MeterName)</c>.
/// With nothing listening, the cost is a null check per run.
/// </para>
/// </remarks>
public static class AgentTelemetry
{
    /// <summary>Activity source name to subscribe to for agent run spans.</summary>
    public const string ActivitySourceName = "Trellis.Agent";

    /// <summary>Meter name to subscribe to for agent run metrics.</summary>
    public const string MeterName = "Trellis.Agent";

    internal static readonly ActivitySource Source = new(ActivitySourceName, "0.10.0");
    private static readonly Meter Meter = new(MeterName, "0.10.0");

    private static readonly Histogram<double> RunDuration = Meter.CreateHistogram<double>(
        "trellis.agent.run.duration", "s", "Wall time of an agent run, retries included.");

    private static readonly Counter<long> TokenUsage = Meter.CreateCounter<long>(
        "gen_ai.client.token.usage", "token", "Tokens consumed by agent runs.");

    private static readonly Counter<double> Cost = Meter.CreateCounter<double>(
        "trellis.agent.cost", "{currency}", "Estimated spend, when a cost model is configured.");

    private static readonly Counter<long> Rejections = Meter.CreateCounter<long>(
        "trellis.agent.output.rejections",
        "{rejection}",
        "Outputs rejected by deserialization or validation, i.e. self-healing retries.");

    /// <summary>
    /// Prices runs for the cost metric and the <c>trellis.agent.cost</c> span attribute.
    /// Null (the default) disables cost accounting. Process-wide, like the metric pipeline
    /// it feeds; set it once at startup.
    /// </summary>
    public static ITokenCostModel? CostModel { get; set; }

    internal static Activity? StartRun(Type resultType, ChatOptions? options, bool streaming)
    {
        Activity? activity = Source.StartActivity(
            streaming ? "invoke_agent stream" : "invoke_agent", ActivityKind.Client);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("gen_ai.operation.name", "invoke_agent");
            activity.SetTag("gen_ai.output.type", resultType == typeof(string) ? "text" : "json");
            activity.SetTag("trellis.agent.result_type", resultType.Name);
            if (options?.ModelId is string model)
            {
                activity.SetTag("gen_ai.request.model", model);
            }
            if (options?.ConversationId is string conversationId)
            {
                activity.SetTag("gen_ai.conversation.id", conversationId);
            }
        }
        return activity;
    }

    internal static void RecordRejection(string? modelId, string reason) =>
        Rejections.Add(1, Tag("gen_ai.request.model", modelId), new("trellis.rejection.reason", reason));

    internal static void RecordSuccess(Activity? activity, ChatResponse response, int attempts, TimeSpan elapsed)
    {
        string? modelId = response.ModelId;
        RunDuration.Record(elapsed.TotalSeconds, Tag("gen_ai.request.model", modelId));

        UsageDetails? usage = response.Usage;
        if (usage is not null)
        {
            if (usage.InputTokenCount is long input)
            {
                TokenUsage.Add(input, Tag("gen_ai.request.model", modelId), new("gen_ai.token.type", "input"));
            }
            if (usage.OutputTokenCount is long output)
            {
                TokenUsage.Add(output, Tag("gen_ai.request.model", modelId), new("gen_ai.token.type", "output"));
            }
        }

        decimal? cost = usage is not null ? CostModel?.EstimateCost(modelId, usage) : null;
        if (cost is decimal spend)
        {
            Cost.Add((double)spend, Tag("gen_ai.request.model", modelId));
        }

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("trellis.agent.attempts", attempts);
            if (modelId is not null)
            {
                activity.SetTag("gen_ai.response.model", modelId);
            }
            if (usage?.InputTokenCount is long inputTokens)
            {
                activity.SetTag("gen_ai.usage.input_tokens", inputTokens);
            }
            if (usage?.OutputTokenCount is long outputTokens)
            {
                activity.SetTag("gen_ai.usage.output_tokens", outputTokens);
            }
            if (cost is decimal amount)
            {
                activity.SetTag("trellis.agent.cost", amount);
            }
        }
    }

    internal static void RecordFailure(Activity? activity, Exception error, TimeSpan elapsed)
    {
        RunDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("error.type", error.GetType().Name));
        activity?.SetStatus(ActivityStatusCode.Error, error.Message);
        activity?.SetTag("error.type", error.GetType().FullName);
    }

    private static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);
}
