using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;
using Trellis.Graph;

namespace Trellis.Tests;

public class TelemetryTests
{
    private sealed record FlightResult(string Destination, decimal Price);

    /// <summary>
    /// Collects activities from one source. Listeners are process-global and xUnit runs test
    /// classes in parallel, so recording is scoped to a per-test root activity: only spans
    /// sharing its trace id — i.e. produced by this test's async flow — are returned.
    /// </summary>
    private sealed class ActivityRecorder : IDisposable
    {
        // Must be a const: AddActivityListener invokes ShouldListenTo synchronously, and if
        // that callback read a static *field* of this type while its initializer was still
        // running it would see null — poisoning the type for the rest of the process.
        private const string RootSourceName = "TrellisTests.Root";

        private static readonly ActivitySource RootSource = new(RootSourceName);
        private readonly ActivityListener _listener;
        private readonly Activity? _root;
        private readonly List<Activity> _seen = [];

        public ActivityRecorder(string sourceName)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == sourceName || source.Name == RootSourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    lock (_seen)
                    {
                        _seen.Add(activity);
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
            _root = RootSource.StartActivity("test-root");
        }

        public List<Activity> Activities
        {
            get
            {
                lock (_seen)
                {
                    return [.. _seen.Where(a => a != _root && a.TraceId == _root!.TraceId)];
                }
            }
        }

        public void Dispose()
        {
            _root?.Dispose();
            _listener.Dispose();
        }
    }

    /// <summary>Collects long/double instrument measurements from one meter.</summary>
    private sealed class MetricRecorder : IDisposable
    {
        private readonly MeterListener _listener;

        public MetricRecorder(string meterName)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == meterName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                Record(instrument.Name, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                Record(instrument.Name, value, tags));
            _listener.Start();
        }

        public List<(string Instrument, double Value, Dictionary<string, object?> Tags)> Measurements { get; } = [];

        private void Record(string instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            Dictionary<string, object?> copy = [];
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                copy[tag.Key] = tag.Value;
            }
            lock (Measurements)
            {
                Measurements.Add((instrument, value, copy));
            }
        }

        /// <summary>
        /// Sums one instrument, restricted to measurements carrying a tag unique to the
        /// calling test — metrics have no trace context, so parallel classes would otherwise
        /// contribute to the same counters.
        /// </summary>
        public double Total(string instrument, string tagKey, object tagValue)
        {
            lock (Measurements)
            {
                return Measurements
                    .Where(m => m.Instrument == instrument && Equals(m.Tags.GetValueOrDefault(tagKey), tagValue))
                    .Sum(m => m.Value);
            }
        }

        public List<(string Instrument, double Value, Dictionary<string, object?> Tags)> For(
            string instrument, string tagKey, object tagValue)
        {
            lock (Measurements)
            {
                return [.. Measurements.Where(m =>
                    m.Instrument == instrument && Equals(m.Tags.GetValueOrDefault(tagKey), tagValue))];
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class UsageChatClient(long input, long output, string modelId, params string[] responses) : IChatClient
    {
        private int _served = -1;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            int index = Math.Min(Interlocked.Increment(ref _served), responses.Length - 1);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responses[index]))
            {
                ModelId = modelId,
                Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output },
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text) { ModelId = modelId };
            yield return new ChatResponseUpdate
            {
                ModelId = modelId,
                Contents = [new UsageContent(response.Usage!)],
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task AgentRun_EmitsASpanWithGenAiAttributes()
    {
        using var activities = new ActivityRecorder(AgentTelemetry.ActivitySourceName);
        var agent = new Agent<FlightResult>(new UsageChatClient(100, 20, "test-model",
            """{"destination":"Pune","price":1}"""));

        await agent.RunAsync("book");

        Activity activity = Assert.Single(activities.Activities);
        Assert.Equal("invoke_agent", activity.DisplayName);
        Assert.Equal("invoke_agent", activity.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("json", activity.GetTagItem("gen_ai.output.type"));
        Assert.Equal("test-model", activity.GetTagItem("gen_ai.response.model"));
        Assert.Equal(100L, activity.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(20L, activity.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Equal(1, activity.GetTagItem("trellis.agent.attempts"));
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
    }

    [Fact]
    public async Task FailedRun_MarksTheSpanAsError()
    {
        using var activities = new ActivityRecorder(AgentTelemetry.ActivitySourceName);
        var agent = new Agent<FlightResult>(new FakeChatClient("not json"),
            outputRetry: new OutputRetryOptions { MaxRetries = 0 });

        await Assert.ThrowsAsync<OutputValidationException>(() => agent.RunAsync("book"));

        Activity activity = Assert.Single(activities.Activities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(typeof(OutputValidationException).FullName, activity.GetTagItem("error.type"));
    }

    [Fact]
    public async Task SelfHealingRetries_AreCountedAndReportedOnTheSpan()
    {
        using var activities = new ActivityRecorder(AgentTelemetry.ActivitySourceName);
        using var metrics = new MetricRecorder(AgentTelemetry.MeterName);
        var agent = new Agent<FlightResult>(new UsageChatClient(10, 5, "rejection-metrics-model",
            "garbage", """{"destination":"Pune","price":1}"""));

        AgentRunResult<FlightResult> result = await agent.RunAsync("book");

        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, Assert.Single(activities.Activities).GetTagItem("trellis.agent.attempts"));
        Assert.Equal(1, metrics.Total(
            "trellis.agent.output.rejections", "gen_ai.request.model", "rejection-metrics-model"));
    }

    [Fact]
    public async Task TokenUsage_IsSplitByTokenType()
    {
        using var metrics = new MetricRecorder(AgentTelemetry.MeterName);
        var agent = new Agent(new UsageChatClient(1_000, 250, "token-usage-model", "hi"));

        await agent.RunAsync("hello");

        var usage = metrics.For("gen_ai.client.token.usage", "gen_ai.request.model", "token-usage-model");
        Assert.Equal(1_000, usage.Single(m => Equals(m.Tags["gen_ai.token.type"], "input")).Value);
        Assert.Equal(250, usage.Single(m => Equals(m.Tags["gen_ai.token.type"], "output")).Value);
    }

    [Fact]
    public async Task CostModel_PricesTheRun_WhenConfigured()
    {
        using var metrics = new MetricRecorder(AgentTelemetry.MeterName);
        using var activities = new ActivityRecorder(AgentTelemetry.ActivitySourceName);
        ITokenCostModel? previous = AgentTelemetry.CostModel;
        AgentTelemetry.CostModel = new StaticTokenCostModel(new Dictionary<string, ModelPrice>
        {
            ["cost-metrics-model"] = new(InputPerMillion: 3.00m, OutputPerMillion: 15.00m),
        });
        try
        {
            var agent = new Agent(new UsageChatClient(1_000_000, 1_000_000, "cost-metrics-model", "hi"));

            await agent.RunAsync("hello");

            Assert.Equal(18.0, metrics.Total("trellis.agent.cost", "gen_ai.request.model", "cost-metrics-model"), precision: 4);
            Assert.Equal(18.0m, Assert.Single(activities.Activities).GetTagItem("trellis.agent.cost"));
        }
        finally
        {
            AgentTelemetry.CostModel = previous;
        }
    }

    [Fact]
    public void CostModel_ReturnsNullForUnknownModels_NotZero()
    {
        var model = new StaticTokenCostModel(new Dictionary<string, ModelPrice>
        {
            ["known"] = new(1m, 1m),
        });

        Assert.Null(model.EstimateCost("mystery-model", new UsageDetails { InputTokenCount = 5 }));
        Assert.Null(model.EstimateCost(null, new UsageDetails { InputTokenCount = 5 }));
        Assert.NotNull(model.EstimateCost("KNOWN", new UsageDetails { InputTokenCount = 5 }));
    }

    [Fact]
    public async Task StreamingRun_EmitsItsOwnSpan()
    {
        using var activities = new ActivityRecorder(AgentTelemetry.ActivitySourceName);
        var agent = new Agent(new UsageChatClient(7, 3, "test-model", "hello there"));

        await foreach (ChatResponseUpdate _ in agent.RunStreamingAsync("hi"))
        {
        }

        Activity activity = Assert.Single(activities.Activities);
        Assert.Equal("invoke_agent stream", activity.DisplayName);
        Assert.Equal(7L, activity.GetTagItem("gen_ai.usage.input_tokens"));
    }

    [Fact]
    public async Task AbandonedStream_StillStopsItsSpan()
    {
        using var activities = new ActivityRecorder(AgentTelemetry.ActivitySourceName);
        var agent = new Agent(new FakeChatClient("a b c d e"));

        AgentStream<string> stream = agent.RunStreamingAsync("hi");
        await using (IAsyncEnumerator<ChatResponseUpdate> enumerator = stream.GetAsyncEnumerator())
        {
            await enumerator.MoveNextAsync();
        }

        // Abandoned mid-flight, but the span must not leak.
        Assert.Single(activities.Activities);
    }

    [Fact]
    public async Task StreamingFailure_MarksTheSpanAsError()
    {
        using var activities = new ActivityRecorder(AgentTelemetry.ActivitySourceName);
        var agent = new Agent<FlightResult>(new FakeChatClient("not json"));

        await Assert.ThrowsAsync<OutputValidationException>(async () =>
        {
            await foreach (ChatResponseUpdate _ in agent.RunStreamingAsync("book"))
            {
            }
        });

        Assert.Equal(ActivityStatusCode.Error, Assert.Single(activities.Activities).Status);
    }

    private sealed record CounterState(int Value);

    [Fact]
    public async Task GraphRun_EmitsRunAndNodeSpans_WithNodesParentedToTheRun()
    {
        using var activities = new ActivityRecorder(GraphTelemetry.ActivitySourceName);
        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("first", s => s with { Value = s.Value + 1 })
            .AddNode("second", s => s with { Value = s.Value + 1 })
            .AddEdge("first", "second")
            .SetEntryPoint("first")
            .Compile();

        await graph.RunAsync(new CounterState(0));

        Activity run = Assert.Single(activities.Activities, a => a.DisplayName == "graph.run");
        Activity first = Assert.Single(activities.Activities, a => a.DisplayName == "graph.node first");
        Activity second = Assert.Single(activities.Activities, a => a.DisplayName == "graph.node second");

        Assert.Equal(run.SpanId, first.ParentSpanId);
        Assert.Equal(run.SpanId, second.ParentSpanId);
        Assert.Equal("first", first.GetTagItem("trellis.graph.node"));
        Assert.Equal(0, first.GetTagItem("trellis.graph.step"));
        Assert.Equal(1, second.GetTagItem("trellis.graph.step"));
    }

    [Fact]
    public async Task RetriedNode_ProducesOneSpanPerAttempt_AndCountsRetries()
    {
        using var activities = new ActivityRecorder(GraphTelemetry.ActivitySourceName);
        using var metrics = new MetricRecorder(GraphTelemetry.MeterName);
        int calls = 0;
        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("telemetry-flaky", s =>
            {
                calls++;
                return calls < 3 ? throw new InvalidOperationException("boom") : s;
            }, new NodeResilience<CounterState>
            {
                Retry = new ExponentialBackoffRetryPolicy(3, baseDelay: TimeSpan.Zero, jitterFactor: 0),
            })
            .SetEntryPoint("telemetry-flaky")
            .Compile();

        await graph.RunAsync(new CounterState(0));

        List<Activity> nodeSpans = [.. activities.Activities.Where(a => a.DisplayName == "graph.node telemetry-flaky")];
        Assert.Equal(3, nodeSpans.Count);
        Assert.Equal([1, 2, 3], nodeSpans.Select(a => a.GetTagItem("trellis.graph.attempt")));
        Assert.Equal(ActivityStatusCode.Error, nodeSpans[0].Status);
        Assert.Equal(ActivityStatusCode.Unset, nodeSpans[2].Status);
        Assert.Equal(2, metrics.Total("trellis.graph.node.retries", "trellis.graph.node", "telemetry-flaky"));
    }

    [Fact]
    public async Task FallbackNode_IsCountedAndSpanned()
    {
        using var activities = new ActivityRecorder(GraphTelemetry.ActivitySourceName);
        using var metrics = new MetricRecorder(GraphTelemetry.MeterName);
        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("telemetry-boom", CounterState (s) => throw new InvalidOperationException("down"),
                new NodeResilience<CounterState> { Fallback = (s, _, _) => Task.FromResult(s with { Value = -1 }) })
            .SetEntryPoint("telemetry-boom")
            .Compile();

        GraphResult<CounterState> result = await graph.RunAsync(new CounterState(0));

        Assert.Equal(-1, result.FinalState.Value);
        Assert.Single(activities.Activities, a => a.DisplayName == "graph.node telemetry-boom (fallback)");
        Assert.Equal(1, metrics.Total("trellis.graph.node.fallbacks", "trellis.graph.node", "telemetry-boom"));
    }

    [Fact]
    public async Task NodeDuration_IsRecordedPerNode()
    {
        using var metrics = new MetricRecorder(GraphTelemetry.MeterName);
        CompiledGraph<CounterState> graph = new StateGraph<CounterState>()
            .AddNode("telemetry-duration", s => s)
            .SetEntryPoint("telemetry-duration")
            .Compile();

        await graph.RunAsync(new CounterState(0));

        var measurement = Assert.Single(
            metrics.For("trellis.graph.node.duration", "trellis.graph.node", "telemetry-duration"));
        Assert.True(measurement.Value >= 0);
    }
}
