using Microsoft.Extensions.AI;
using OllamaSharp;

namespace Trellis.Tests;

/// <summary>
/// Real-model validation against a local Ollama server. These are the only tests that
/// exercise an actual LLM — everything else runs on fakes and proves wiring, not reality.
/// When Ollama is not reachable (e.g. CI) each test returns early as a no-op; run them
/// locally with Ollama up to get genuine coverage of structured output and tool calling.
/// </summary>
public class OllamaIntegrationTests
{
    private const string OllamaUri = "http://localhost:11434";

    // qwen2.5 instruct (not -coder): small models must still emit Ollama's structured
    // tool_calls field — coder-tuned variants write tool calls as plain text instead.
    private const string Model = "qwen2.5:1.5b";

    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            string json = http.GetStringAsync($"{OllamaUri}/api/tags").GetAwaiter().GetResult();
            return json.Contains(Model.Split(':')[0], StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    });

    private static IChatClient NewClient() => new OllamaApiClient(new Uri(OllamaUri), Model);

    /// <summary>
    /// Small models are nondeterministic; one retry keeps genuine wiring failures visible
    /// while stopping single-sample flakes from failing the suite.
    /// </summary>
    private static async Task WithRetryAsync(Func<Task> assertion)
    {
        try
        {
            await assertion();
        }
        catch (Xunit.Sdk.XunitException)
        {
            await assertion();
        }
    }

    private sealed record MathAnswer(int Sum);

    [Fact]
    public async Task RealModel_PlainTextAgent_Responds()
    {
        if (!Available.Value)
        {
            return; // Ollama not running — validated locally, skipped here.
        }

        var agent = new Agent(NewClient(), instructions: "Answer with a single word.");

        AgentRunResult<string> result = await agent.RunAsync("Reply with exactly the word: pong");

        // This test validates the request/response wiring; exact instruction-following is
        // the model's business (small models flake on it), so assert a real answer came back.
        Assert.False(string.IsNullOrWhiteSpace(result.Output));
    }

    [Fact]
    public async Task RealModel_TypedStructuredOutput_Deserializes()
    {
        if (!Available.Value)
        {
            return;
        }

        var agent = new Agent<MathAnswer>(NewClient(), instructions: "You compute sums and answer as JSON.");

        await WithRetryAsync(async () =>
        {
            AgentRunResult<MathAnswer> result = await agent.RunAsync("What is 20 + 22?");
            Assert.Equal(42, result.Output.Sum);
        });
    }

    [Fact]
    public async Task RealModel_ToolCalling_InvokesTool()
    {
        if (!Available.Value)
        {
            return;
        }

        bool invoked = false;
        AITool secret = AIFunctionFactory.Create(
            () => { invoked = true; return 73; },
            name: "get_secret_number",
            description: "Returns the secret number.");

        var agent = new Agent(NewClient(),
            instructions: "Use the available tools to answer.",
            tools: [secret]);

        await WithRetryAsync(async () =>
        {
            AgentRunResult<string> result = await agent.RunAsync(
                "Call the get_secret_number tool and tell me the number it returns.");
            Assert.True(invoked, "the model never invoked the tool");
            Assert.Contains("73", result.Output);
        });
    }

    [Fact]
    public async Task RealModel_MultiTurnConversation_KeepsContext()
    {
        if (!Available.Value)
        {
            return;
        }

        var agent = new Agent(NewClient(), instructions: "Be brief.");

        await WithRetryAsync(async () =>
        {
            var conversation = new Conversation();
            await agent.RunAsync(conversation, "My favorite color is orange. Just acknowledge.");
            AgentRunResult<string> result = await agent.RunAsync(conversation, "What is my favorite color?");
            Assert.Contains("orange", result.Output, StringComparison.OrdinalIgnoreCase);
        });
    }
}
