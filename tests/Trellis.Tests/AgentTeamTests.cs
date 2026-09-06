using Microsoft.Extensions.AI;
using Trellis.Agents.Teams;

namespace Trellis.Tests;

/// <summary>
/// Model-decided routing between agents: that a transfer actually moves the conversation, that
/// it cannot loop forever, and that a hallucinated target does not take the run down with it.
/// </summary>
public class AgentTeamTests
{
    /// <summary>
    /// Calls a named handoff tool on its first turn, then answers on later turns. Enough to
    /// drive a real function-invoking loop without a model.
    /// </summary>
    private sealed class ScriptedClient(params string?[] script) : IChatClient
    {
        private int _turn = -1;

        public int ModelCalls { get; private set; }

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public List<ChatOptions?> Options { get; } = [];

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            await Task.Yield();
            Requests.Add([.. messages]);
            Options.Add(options);
            ModelCalls++;

            int index = Math.Min(Interlocked.Increment(ref _turn), script.Length - 1);
            string? step = script[index];

            // A null step means "answer"; otherwise the step names the agent to transfer to.
            return step is null
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, "handled"))
                : new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                        $"call-{index}",
                        "transfer_to_" + step,
                        new Dictionary<string, object?> { ["reason"] = "not my area" })]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken c = default) =>
            throw new NotSupportedException();

        public object? GetService(Type t, object? k = null) => null;

        public void Dispose()
        {
        }
    }

    private static Agent<string> NewAgent(string name, IChatClient client, string description = "does things") =>
        new(client) { Name = name, Description = description };

    [Fact]
    public async Task AnAgentThatAnswers_EndsTheRun()
    {
        Agent<string> solo = NewAgent("solo", new ScriptedClient([null]));
        var team = new AgentTeam<string>(solo, [solo]);

        AgentTeamResult<string> result = await team.RunAsync("hello");

        Assert.Equal("handled", result.Output);
        Assert.Equal("solo", result.AnsweredBy);
        Assert.Empty(result.Handoffs);
    }

    [Fact]
    public async Task AHandoffMovesTheConversationToTheNamedAgent()
    {
        Agent<string> triage = NewAgent("triage", new ScriptedClient(["billing"]));
        var billingClient = new ScriptedClient([null]);
        Agent<string> billing = NewAgent("billing", billingClient, "refunds and invoices");

        var team = new AgentTeam<string>(triage, [billing]);
        AgentTeamResult<string> result = await team.RunAsync("I was charged twice");

        Assert.Equal("billing", result.AnsweredBy);
        Assert.Equal("handled", result.Output);

        HandoffRecord record = Assert.Single(result.Handoffs);
        Assert.Equal("triage", record.From);
        Assert.Equal("billing", record.To);
        Assert.Equal("not my area", record.Reason);
    }

    [Fact]
    public async Task TheReceivingAgentSeesTheWholeConversation()
    {
        Agent<string> triage = NewAgent("triage", new ScriptedClient(["billing"]));
        var billingClient = new ScriptedClient([null]);
        Agent<string> billing = NewAgent("billing", billingClient);

        var team = new AgentTeam<string>(triage, [billing]);
        await team.RunAsync("I was charged twice");

        // Hiding the transfer would leave the receiving agent reading a request with no trace
        // of how it got there — and would break the tool call/result pairing providers require.
        Assert.Contains(billingClient.Requests[0], m => m.Text.Contains("charged twice"));
    }

    [Fact]
    public async Task AHandingAgentIsNotAskedToAnswerAsWell()
    {
        var triageClient = new ScriptedClient(["billing"]);
        Agent<string> triage = NewAgent("triage", triageClient);
        Agent<string> billing = NewAgent("billing", new ScriptedClient([null]));

        var team = new AgentTeam<string>(triage, [billing]);
        await team.RunAsync("hi");

        // Continuing the loop after a transfer would pay for a round trip to have the wrong
        // agent answer the question it just said was not its own.
        Assert.Equal(1, triageClient.ModelCalls);
    }

    [Fact]
    public async Task HandoffLoops_AreStopped()
    {
        // Two agents that each think the other owns the request.
        Agent<string> a = NewAgent("a", new ScriptedClient(["b"]));
        Agent<string> b = NewAgent("b", new ScriptedClient(["a"]));

        var team = new AgentTeam<string>(a, [b], options: new AgentTeamOptions { MaxHandoffs = 4 });

        HandoffLimitException ex = await Assert.ThrowsAsync<HandoffLimitException>(
            () => team.RunAsync("who owns this?"));

        Assert.Equal(4, ex.Limit);
        // The trail is what makes the misconfiguration diagnosable without a tracing backend.
        Assert.Equal(5, ex.Handoffs.Count);
        Assert.Equal("a", ex.Handoffs[0].From);
    }

    /// <summary>
    /// A custom member that hands off to whatever it is told to, including a name that does not
    /// exist. A real model cannot do this: it can only call tools it was given, so a bogus
    /// target is an unknown *function* and Microsoft.Extensions.AI answers it before the team
    /// sees anything. The unknown-target path is reachable only from a hand-written
    /// <see cref="IAgent{TResult}"/>, so that is what exercises it.
    /// </summary>
    private sealed class StubAgent(string name, params string?[] script) : IAgent<string>
    {
        private int _turn = -1;

        public List<IReadOnlyList<ChatMessage>> Seen { get; } = [];

        public string Name => name;

        public string Description => "a stub";

        public Task<AgentTurn<string>> TakeTurnAsync(
            IEnumerable<ChatMessage> messages,
            IReadOnlyList<AITool>? additionalTools = null,
            CancellationToken cancellationToken = default)
        {
            Seen.Add([.. messages]);
            int index = Math.Min(Interlocked.Increment(ref _turn), script.Length - 1);
            string? target = script[index];
            return Task.FromResult(target is null
                ? AgentTurn<string>.Answered(new AgentRunResult<string>("handled", new ChatResponse()))
                : AgentTurn<string>.HandedOff(target, "guessing"));
        }
    }

    [Fact]
    public async Task AnUnknownTarget_IsCorrectedRatherThanFatal()
    {
        var triage = new StubAgent("triage", ["nowhere", null]);
        var billing = new StubAgent("billing", [null]);

        var team = new AgentTeam<string>(triage, [billing]);
        AgentTeamResult<string> result = await team.RunAsync("hi");

        // A bad name must not crash a run the agent can recover from on its own.
        Assert.Equal("triage", result.AnsweredBy);
        Assert.Empty(result.Handoffs);
    }

    [Fact]
    public async Task AnUnknownTarget_IsToldWhoDoesExist()
    {
        var triage = new StubAgent("triage", ["nowhere", null]);
        var billing = new StubAgent("billing", [null]);

        var team = new AgentTeam<string>(triage, [billing]);
        await team.RunAsync("hi");

        string correction = string.Join(" ", triage.Seen[^1].Select(m => m.Text));
        Assert.Contains("no agent named 'nowhere'", correction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("billing", correction);
    }

    [Fact]
    public async Task RepeatedUnknownTargets_StillHitTheHandoffLimit()
    {
        var stubborn = new StubAgent("stubborn", "nowhere");

        // The correction path must not become an unbounded loop of its own.
        var team = new AgentTeam<string>(stubborn, [], options: new AgentTeamOptions { MaxHandoffs = 3 });

        await Assert.ThrowsAsync<HandoffLimitException>(() => team.RunAsync("hi"));
    }

    [Fact]
    public async Task AnAgentIsNotOfferedATransferToItself()
    {
        var client = new ScriptedClient([null]);
        Agent<string> solo = NewAgent("solo", client);
        var team = new AgentTeam<string>(solo, [solo]);

        await team.RunAsync("hi");

        // An agent that can transfer to itself eventually will, and burns the whole budget.
        IEnumerable<string> names = client.Options[0]?.Tools?.OfType<AIFunction>().Select(t => t.Name) ?? [];
        Assert.DoesNotContain("transfer_to_solo", names);
    }

    [Fact]
    public async Task HandoffToolsDescribeTheirTarget()
    {
        var client = new ScriptedClient([null]);
        Agent<string> triage = NewAgent("triage", client);
        Agent<string> billing = NewAgent("billing", new ScriptedClient([null]), "refunds and invoices");

        var team = new AgentTeam<string>(triage, [billing]);
        await team.RunAsync("hi");

        AIFunction transfer = client.Options[0]!.Tools!.OfType<AIFunction>()
            .Single(t => t.Name == "transfer_to_billing");

        // The description is the only thing the model has to route on.
        Assert.Contains("refunds and invoices", transfer.Description);
    }

    [Fact]
    public async Task AnAgentsOwnToolsSurviveTheMerge()
    {
        AIFunction own = AIFunctionFactory.Create(() => "ok", name: "lookup_order");
        var client = new ScriptedClient([null]);
        var triage = new Agent<string>(client, tools: [own]) { Name = "triage" };
        Agent<string> billing = NewAgent("billing", new ScriptedClient([null]));

        var team = new AgentTeam<string>(triage, [billing]);
        await team.RunAsync("hi");

        IEnumerable<string> names = client.Options[0]!.Tools!.OfType<AIFunction>().Select(t => t.Name);
        Assert.Contains("lookup_order", names);
        Assert.Contains("transfer_to_billing", names);
    }

    [Fact]
    public void DuplicateNames_AreRefusedAtConstruction()
    {
        Agent<string> one = NewAgent("billing", new ScriptedClient([null]));
        Agent<string> two = NewAgent("billing", new ScriptedClient([null]));

        // A handoff to a name two agents answer to would be a coin flip decided by enumeration
        // order — better to refuse to start than to route unpredictably.
        Assert.Throws<ArgumentException>(() => new AgentTeam<string>(one, [two]));
    }

    [Fact]
    public async Task TeamsNest()
    {
        // An inner team that routes internally, presented to the outer team as one agent.
        Agent<string> worker = NewAgent("worker", new ScriptedClient([null]));
        Agent<string> foreman = NewAgent("foreman", new ScriptedClient(["worker"]));
        var crew = new AgentTeam<string>(foreman, [worker], name: "crew", description: "does the work");

        Agent<string> manager = NewAgent("manager", new ScriptedClient(["crew"]));
        var company = new AgentTeam<string>(manager, [crew]);

        AgentTeamResult<string> result = await company.RunAsync("build it");

        // A team is an IAgent, so manager/workers needs no separate concept.
        Assert.Equal("crew", result.AnsweredBy);
        Assert.Equal("handled", result.Output);
    }

    [Fact]
    public async Task ATypedAgentHandingOff_DoesNotPayForSelfHealingRetries()
    {
        // A handoff produces no JSON, so a validator would reject it and self-healing would
        // buy two correction round trips before anyone noticed the transfer.
        var triageClient = new ScriptedClient(["billing"]);
        var triage = new Agent<Ticket>(triageClient) { Name = "triage" };
        var billing = new Agent<Ticket>(new JsonClient()) { Name = "billing" };

        var team = new AgentTeam<Ticket>(triage, [billing]);
        AgentTeamResult<Ticket> result = await team.RunAsync("refund me");

        Assert.Equal("billing", result.AnsweredBy);
        Assert.Equal(1, triageClient.ModelCalls);
        Assert.Equal(7, result.Output.Id);
    }

    public sealed record Ticket(int Id);

    /// <summary>Answers with valid JSON for the typed test.</summary>
    private sealed class JsonClient : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            await Task.Yield();
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"Id":7}"""));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken c = default) =>
            throw new NotSupportedException();

        public object? GetService(Type t, object? k = null) => null;

        public void Dispose()
        {
        }
    }
}
