using Microsoft.Extensions.AI;
using Trellis.Agents.Middleware;

namespace Trellis.Tests;

/// <summary>
/// The agent-run seam: ordering, short-circuiting, and — the one that would be a real bug —
/// that what middleware injects into a run never reaches canonical conversation history.
/// </summary>
public class AgentMiddlewareTests
{
    /// <summary>Appends its name to a shared log on the way in and on the way out.</summary>
    private sealed class TracingMiddleware(string name, List<string> log) : IAgentMiddleware<string>
    {
        public async Task<AgentRunResult<string>> InvokeAsync(
            AgentRunContext context, AgentRunDelegate<string> next, CancellationToken cancellationToken = default)
        {
            log.Add($"{name}:in");
            AgentRunResult<string> result = await next(context, cancellationToken);
            log.Add($"{name}:out");
            return result;
        }
    }

    private static Agent<string> AgentWith(
        IChatClient client, params IAgentMiddleware<string>[] middleware) =>
        new(client, middleware: middleware);

    [Fact]
    public async Task NoMiddleware_RunsNormally()
    {
        var agent = new Agent<string>(new FakeChatClient("hello"));

        AgentRunResult<string> result = await agent.RunAsync("hi");

        Assert.Equal("hello", result.Output);
    }

    [Fact]
    public async Task TheFirstEntryIsTheOutermost()
    {
        List<string> log = [];
        Agent<string> agent = AgentWith(
            new FakeChatClient("hello"),
            new TracingMiddleware("a", log),
            new TracingMiddleware("b", log));

        await agent.RunAsync("hi");

        // ASP.NET Core ordering: first in, last out. Anything else makes composing two
        // pipelines a guessing game.
        Assert.Equal(["a:in", "b:in", "b:out", "a:out"], log);
    }

    [Fact]
    public async Task MiddlewareCanEditTheRequestBeforeItIsSent()
    {
        var client = new FakeChatClient("hello");
        Agent<string> agent = AgentWith(
            client,
            DelegateAgentMiddleware<string>.OnRequest(context =>
                context.Messages.Insert(0, new ChatMessage(ChatRole.System, "remembered: the user likes brevity"))));

        await agent.RunAsync("hi");

        Assert.Contains(client.Requests[0], m => m.Text.Contains("remembered"));
    }

    [Fact]
    public async Task MiddlewareSeesTheSystemInstructions()
    {
        string? seen = null;
        var agent = new Agent<string>(
            new FakeChatClient("hello"),
            instructions: "You are terse.",
            middleware: [DelegateAgentMiddleware<string>.OnRequest(c => seen = c.Messages[0].Text)]);

        await agent.RunAsync("hi");

        // The payload is already assembled, so rewriting a system prompt is just editing
        // Messages — no separate hook needed.
        Assert.Equal("You are terse.", seen);
    }

    [Fact]
    public async Task MiddlewareCanReplaceTheResult()
    {
        Agent<string> agent = AgentWith(
            new FakeChatClient("the unsafe answer"),
            new DelegateAgentMiddleware<string>(async (context, next, ct) =>
            {
                AgentRunResult<string> result = await next(context, ct);
                return result.Output.Contains("unsafe")
                    ? new AgentRunResult<string>("[redacted]", result.Response, result.Attempts)
                    : result;
            }));

        AgentRunResult<string> result = await agent.RunAsync("hi");

        Assert.Equal("[redacted]", result.Output);
    }

    [Fact]
    public async Task MiddlewareCanAnswerWithoutCallingTheModel()
    {
        var client = new FakeChatClient("from the model");
        Agent<string> agent = AgentWith(
            client,
            new DelegateAgentMiddleware<string>((_, _, _) =>
                Task.FromResult(new AgentRunResult<string>("from the cache", new ChatResponse()))));

        AgentRunResult<string> result = await agent.RunAsync("hi");

        // Short-circuiting is the point of caching and of refusing early; skipping `next` must
        // genuinely skip the provider call, not merely discard its answer.
        Assert.Equal("from the cache", result.Output);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task MiddlewareCanRunTheRestTwice()
    {
        var client = new FakeChatClient("first", "second");
        Agent<string> agent = AgentWith(
            client,
            new DelegateAgentMiddleware<string>(async (context, next, ct) =>
            {
                await next(context, ct);
                return await next(context, ct);
            }));

        AgentRunResult<string> result = await agent.RunAsync("hi");

        Assert.Equal(2, client.Requests.Count);
        Assert.Equal("second", result.Output);
    }

    [Fact]
    public async Task ItemsCarryStateBetweenMiddleware()
    {
        object? received = null;
        Agent<string> agent = AgentWith(
            new FakeChatClient("hello"),
            DelegateAgentMiddleware<string>.OnRequest(c => c.Items["tenant"] = "acme"),
            DelegateAgentMiddleware<string>.OnRequest(c => c.Items.TryGetValue("tenant", out received)));

        await agent.RunAsync("hi");

        Assert.Equal("acme", received);
    }

    [Fact]
    public async Task AThrowingMiddleware_FailsTheRun()
    {
        var client = new FakeChatClient("hello");
        Agent<string> agent = AgentWith(
            client,
            new DelegateAgentMiddleware<string>((_, _, _) => throw new InvalidOperationException("policy")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync("hi"));

        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task MiddlewareCanChangeTheOptions()
    {
        var client = new FakeChatClient("hello");
        Agent<string> agent = AgentWith(
            client,
            DelegateAgentMiddleware<string>.OnRequest(c =>
                c.Options = new ChatOptions { ModelId = "cheap-model" }));

        await agent.RunAsync("hi");

        Assert.Equal("cheap-model", client.Options[0]?.ModelId);
    }

    [Fact]
    public async Task TheResultTypeIsVisible()
    {
        Type? seen = null;
        var agent = new Agent<string>(
            new FakeChatClient("hello"),
            middleware: [DelegateAgentMiddleware<string>.OnRequest(c => seen = c.ResultType)]);

        await agent.RunAsync("hi");

        Assert.Equal(typeof(string), seen);
    }
}

/// <summary>
/// Middleware against the conversation and streaming paths — where getting it wrong would
/// corrupt history or silently skip a guardrail.
/// </summary>
public class AgentMiddlewareIntegrationTests
{
    [Fact]
    public async Task InjectedContextIsSent_ButNeverEntersTheConversation()
    {
        var client = new FakeChatClient("noted");
        var agent = new Agent<string>(
            client,
            middleware: [DelegateAgentMiddleware<string>.OnRequest(context =>
                context.Messages.Insert(0, new ChatMessage(ChatRole.System, "RETRIEVED-MEMORY")))]);
        var conversation = new Conversation("c1");

        await agent.RunAsync(conversation, "hi");

        // Sent to the model...
        Assert.Contains(client.Requests[0], m => m.Text.Contains("RETRIEVED-MEMORY"));

        // ...but absent from canonical history. Otherwise injected context would compound every
        // turn, be persisted, and be replayed on failover — the same rule that keeps
        // self-healing retries out of a Conversation.
        Assert.DoesNotContain(conversation.Messages, m => m.Text.Contains("RETRIEVED-MEMORY"));
        Assert.Equal(2, conversation.Messages.Count);
    }

    [Fact]
    public async Task MiddlewareAppliesToConversationTurns()
    {
        var log = new List<string>();
        var agent = new Agent<string>(
            new FakeChatClient("ok"),
            middleware: [DelegateAgentMiddleware<string>.OnRequest(_ => log.Add("ran"))]);
        var conversation = new Conversation("c1");

        await agent.RunAsync(conversation, "one");
        await agent.RunAsync(conversation, "two");

        Assert.Equal(["ran", "ran"], log);
    }

    [Fact]
    public void StreamingIsRefused_RatherThanSilentlySkippingThePipeline()
    {
        var agent = new Agent<string>(
            new FakeChatClient("hello"),
            middleware: [DelegateAgentMiddleware<string>.OnRequest(_ => { })]);

        // A guardrail that protects RunAsync but not RunStreamingAsync is worse than no
        // streaming path at all, so this fails loudly instead.
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => agent.RunStreamingAsync("hi"));
        Assert.Contains("middleware", ex.Message);

        Assert.Throws<NotSupportedException>(
            () => agent.RunStreamingAsync(new Conversation("c1"), "hi"));
    }

    [Fact]
    public void StreamingStillWorksWithoutMiddleware()
    {
        var agent = new Agent<string>(new FakeChatClient("hello"));

        Assert.NotNull(agent.RunStreamingAsync("hi"));
    }

    [Fact]
    public async Task PerRunDependencyAgentsGetThePipelineToo()
    {
        var log = new List<string>();
        var agent = new Agent<string, string>(
            new FakeChatClient("ok"),
            tools: _ => [],
            middleware: [DelegateAgentMiddleware<string>.OnRequest(_ => log.Add("ran"))]);

        await agent.RunAsync("deps", "hi");

        Assert.Equal(["ran"], log);
    }

    [Fact]
    public async Task MiddlewareWrapsSelfHealing_NotEachAttempt()
    {
        // Two bad shapes then a good one: the run retries internally.
        var client = new FakeChatClient("not json", "still not json", """{"Value":3}""");
        var calls = 0;
        var agent = new Agent<Holder>(
            client,
            middleware: [DelegateAgentMiddleware<Holder>.OnRequest(_ => calls++)]);

        AgentRunResult<Holder> result = await agent.RunAsync("hi");

        // Self-healing lives inside one run, so middleware sees the run — not three of them.
        Assert.Equal(3, result.Attempts);
        Assert.Equal(1, calls);
        Assert.Equal(3, result.Output.Value);
    }

    public sealed record Holder(int Value);
}
