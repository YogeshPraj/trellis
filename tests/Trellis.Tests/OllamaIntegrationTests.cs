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
    /// while stopping single-sample flakes from failing the suite. An exhausted self-healing
    /// budget counts as a flake here too — the loop's mechanics are proven by unit tests.
    /// </summary>
    private static async Task WithRetryAsync(Func<Task> assertion)
    {
        try
        {
            await assertion();
        }
        catch (Exception ex) when (ex is Xunit.Sdk.XunitException or OutputValidationException)
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
    public async Task RealModel_StreamsTextIncrementally()
    {
        if (!Available.Value)
        {
            return;
        }

        var agent = new Agent(NewClient(), instructions: "Be brief.");

        await WithRetryAsync(async () =>
        {
            AgentStream<string> stream = agent.RunStreamingAsync("Count from 1 to 10, separated by spaces.");
            var chunks = new List<string>();
            await foreach (string delta in stream.TextDeltasAsync())
            {
                chunks.Add(delta);
            }

            Assert.True(chunks.Count > 1, "the provider did not stream incrementally");
            Assert.Equal(string.Concat(chunks), stream.Result.Output);
        });
    }

    [Fact]
    public async Task RealModel_StreamsTypedStructuredOutput()
    {
        if (!Available.Value)
        {
            return;
        }

        var agent = new Agent<MathAnswer>(NewClient(), instructions: "You compute sums and answer as JSON.");

        await WithRetryAsync(async () =>
        {
            AgentStream<MathAnswer> stream = agent.RunStreamingAsync("What is 20 + 22?");
            await foreach (ChatResponseUpdate _ in stream)
            {
            }
            Assert.Equal(42, stream.Result.Output.Sum);
        });
    }

    private sealed record ColorAnswer(string Color);

    private sealed class MustBeBlueValidator : IOutputValidator<ColorAnswer>
    {
        public ValueTask<OutputValidationResult> ValidateAsync(ColorAnswer output, CancellationToken cancellationToken = default)
            => new(string.Equals(output.Color?.Trim(), "blue", StringComparison.OrdinalIgnoreCase)
                ? OutputValidationResult.Success
                : OutputValidationResult.Failure(
                    $"'{output.Color}' is not acceptable. The color must be exactly the word: blue"));
    }

    [Fact]
    public async Task RealModel_SelfHealing_CorrectsOutputFromValidatorFeedback()
    {
        if (!Available.Value)
        {
            return;
        }

        // The prompt steers the model away from the only answer the validator accepts, so
        // the first attempt fails and success can only come from the corrective feedback.
        var agent = new Agent<ColorAnswer>(NewClient(),
            instructions: "Answer as JSON.",
            outputValidator: new MustBeBlueValidator(),
            outputRetry: new OutputRetryOptions { MaxRetries = 3 });

        await WithRetryAsync(async () =>
        {
            AgentRunResult<ColorAnswer> result = await agent.RunAsync("Name a color other than blue.");
            Assert.Equal("blue", result.Output.Color.Trim(), ignoreCase: true);
            Assert.True(result.Attempts > 1, "expected the first answer to fail validation");
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
