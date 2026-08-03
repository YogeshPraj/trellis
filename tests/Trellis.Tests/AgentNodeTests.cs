using Microsoft.Extensions.AI;
using Trellis.Graph;

namespace Trellis.Tests;

public class AgentNodeTests
{
    private sealed record PipelineState(string Question, string? Answer, string? Summary);

    [Fact]
    public async Task AgentNode_RunsAgentAndAppliesResult()
    {
        var answerer = new Agent(new FakeChatClient("42"), instructions: "Answer questions.");
        var summarizer = new Agent(new FakeChatClient("The answer is 42."));

        CompiledGraph<PipelineState> graph = new StateGraph<PipelineState>()
            .AddAgentNode("answer", answerer,
                prompt: s => s.Question,
                apply: (s, output) => s with { Answer = output })
            .AddAgentNode("summarize", summarizer,
                prompt: s => $"Summarize: {s.Answer}",
                apply: (s, output) => s with { Summary = output })
            .AddEdge("answer", "summarize")
            .SetEntryPoint("answer")
            .Compile();

        GraphResult<PipelineState> result = await graph.RunAsync(
            new PipelineState("What is the meaning of life?", null, null));

        Assert.Equal("42", result.FinalState.Answer);
        Assert.Equal("The answer is 42.", result.FinalState.Summary);
    }

    [Fact]
    public async Task AgentNode_PromptIsBuiltFromState()
    {
        var client = new FakeChatClient("ok");
        var agent = new Agent(client);

        CompiledGraph<PipelineState> graph = new StateGraph<PipelineState>()
            .AddAgentNode("answer", agent,
                prompt: s => $"Q: {s.Question}",
                apply: (s, output) => s with { Answer = output })
            .SetEntryPoint("answer")
            .Compile();

        await graph.RunAsync(new PipelineState("why?", null, null));

        Assert.Equal("Q: why?", Assert.Single(client.Requests)[^1].Text);
    }

    [Fact]
    public async Task DepsAgentNode_SelectsDepsFromState()
    {
        var client = new FakeChatClient("ok");
        string? seenUser = null;
        var agent = new Agent<string, string>(
            client,
            tools: userId =>
            {
                seenUser = userId;
                return [AIFunctionFactory.Create(() => userId, name: "whoami")];
            });

        CompiledGraph<PipelineState> graph = new StateGraph<PipelineState>()
            .AddAgentNode("answer", agent,
                deps: s => $"user-of:{s.Question}",
                prompt: s => s.Question,
                apply: (s, output) => s with { Answer = output })
            .SetEntryPoint("answer")
            .Compile();

        await graph.RunAsync(new PipelineState("hello", null, null));

        Assert.Equal("user-of:hello", seenUser);
    }
}
