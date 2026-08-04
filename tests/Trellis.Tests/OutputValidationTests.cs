using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.AI;

namespace Trellis.Tests;

/// <summary>The self-healing loop: bad output → error fed back → corrected on retry.</summary>
public class OutputValidationTests
{
    private sealed record FlightResult(string Destination, decimal Price);

    private const string GoodJson = """{ "destination": "Pune", "price": 129.50 }""";

    private sealed class PositivePriceValidator : IOutputValidator<FlightResult>
    {
        public ValueTask<OutputValidationResult> ValidateAsync(FlightResult output, CancellationToken cancellationToken = default)
            => new(output.Price > 0
                ? OutputValidationResult.Success
                : OutputValidationResult.Failure("Price must be positive."));
    }

    [Fact]
    public async Task MalformedJson_IsRetried_AndSucceeds()
    {
        var client = new FakeChatClient("this is not JSON", GoodJson);
        var agent = new Agent<FlightResult>(client);

        AgentRunResult<FlightResult> result = await agent.RunAsync("book a flight");

        Assert.Equal("Pune", result.Output.Destination);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task RetryRequest_CarriesFailedAttemptAndCorrection()
    {
        var client = new FakeChatClient("this is not JSON", GoodJson);
        var agent = new Agent<FlightResult>(client);

        await agent.RunAsync("book a flight");

        IReadOnlyList<ChatMessage> retry = client.Requests[1];
        // Original user prompt, the model's failed attempt, then the correction.
        Assert.Equal(ChatRole.Assistant, retry[^2].Role);
        Assert.Equal("this is not JSON", retry[^2].Text);
        Assert.Equal(ChatRole.User, retry[^1].Role);
        Assert.Contains("rejected", retry[^1].Text);
        Assert.Contains(nameof(FlightResult), retry[^1].Text);
    }

    [Fact]
    public async Task ExhaustedRetries_ThrowTypedException_WithFailureChain()
    {
        var client = new FakeChatClient("garbage");
        var agent = new Agent<FlightResult>(client, outputRetry: new OutputRetryOptions { MaxRetries = 1 });

        var ex = await Assert.ThrowsAsync<OutputValidationException>(() => agent.RunAsync("book"));

        Assert.Equal(2, ex.Attempts);
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(typeof(FlightResult), ex.TargetType);
        Assert.All(ex.Failures, f => Assert.Equal("garbage", f.ResponseText));
        Assert.NotNull(ex.LastResponse);
    }

    [Fact]
    public async Task DefaultBudget_IsThreeAttempts_ForTypedOutputs()
    {
        var client = new FakeChatClient("still not JSON");
        var agent = new Agent<FlightResult>(client);

        await Assert.ThrowsAsync<OutputValidationException>(() => agent.RunAsync("book"));

        Assert.Equal(3, client.Requests.Count);
    }

    [Fact]
    public async Task MaxRetriesZero_FailsFast_WithSingleCall()
    {
        var client = new FakeChatClient("garbage");
        var agent = new Agent<FlightResult>(client, outputRetry: new OutputRetryOptions { MaxRetries = 0 });

        var ex = await Assert.ThrowsAsync<OutputValidationException>(() => agent.RunAsync("book"));

        Assert.Equal(1, ex.Attempts);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task SemanticValidator_ErrorsAreFedBackToModel()
    {
        var client = new FakeChatClient(
            """{ "destination": "Pune", "price": -5 }""",
            GoodJson);
        var agent = new Agent<FlightResult>(client, outputValidator: new PositivePriceValidator());

        AgentRunResult<FlightResult> result = await agent.RunAsync("book a flight");

        Assert.Equal(129.50m, result.Output.Price);
        Assert.Equal(2, result.Attempts);
        Assert.Contains("Price must be positive.", client.Requests[1][^1].Text);
    }

    private sealed record Rated([property: Range(1, 5)] int Stars);

    [Fact]
    public async Task DataAnnotationsValidator_EnforcesAttributes()
    {
        var client = new FakeChatClient("""{ "stars": 9 }""", """{ "stars": 4 }""");
        var agent = new Agent<Rated>(client, outputValidator: new DataAnnotationsOutputValidator<Rated>());

        AgentRunResult<Rated> result = await agent.RunAsync("rate it");

        Assert.Equal(4, result.Output.Stars);
        Assert.Equal(2, result.Attempts);
        Assert.Contains("Stars", client.Requests[1][^1].Text);
    }

    private sealed class ShortAnswerValidator : IOutputValidator<string>
    {
        public ValueTask<OutputValidationResult> ValidateAsync(string output, CancellationToken cancellationToken = default)
            => new(output.Length <= 4
                ? OutputValidationResult.Success
                : OutputValidationResult.Failure("Answer with at most 4 characters."));
    }

    [Fact]
    public async Task PlainTextAgent_WithValidator_SelfHeals()
    {
        var client = new FakeChatClient("a rather long-winded answer", "pong");
        var agent = new Agent(client, outputValidator: new ShortAnswerValidator());

        AgentRunResult<string> result = await agent.RunAsync("ping?");

        Assert.Equal("pong", result.Output);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task ConversationHistory_OnlyAbsorbsTheFinalResponse()
    {
        var client = new FakeChatClient("this is not JSON", GoodJson);
        var agent = new Agent<FlightResult>(client);
        var conversation = new Conversation();

        await agent.RunAsync(conversation, "book a flight");

        // Canonical history: the user turn and the accepted answer — no failed attempt,
        // no correction message.
        Assert.Equal(2, conversation.Messages.Count);
        Assert.Equal(ChatRole.User, conversation.Messages[0].Role);
        Assert.Equal(ChatRole.Assistant, conversation.Messages[1].Role);
        Assert.Equal(GoodJson, conversation.Messages[1].Text);
    }

    [Fact]
    public async Task CustomFeedbackRoleAndFormatter_AreUsed()
    {
        var client = new FakeChatClient("garbage", GoodJson);
        var agent = new Agent<FlightResult>(client, outputRetry: new OutputRetryOptions
        {
            FeedbackRole = ChatRole.System,
            FeedbackFormatter = failure => $"FIX ATTEMPT {failure.Attempt}: {failure.Errors[0]}",
        });

        await agent.RunAsync("book");

        ChatMessage feedback = client.Requests[1][^1];
        Assert.Equal(ChatRole.System, feedback.Role);
        Assert.StartsWith("FIX ATTEMPT 1:", feedback.Text);
    }

    [Fact]
    public async Task AgentWithDeps_SelfHeals()
    {
        var client = new FakeChatClient("nope", GoodJson);
        var agent = new Agent<string, FlightResult>(client, tools: _ => []);

        AgentRunResult<FlightResult> result = await agent.RunAsync("deps", "book");

        Assert.Equal("Pune", result.Output.Destination);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public void NegativeMaxRetries_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutputRetryOptions { MaxRetries = -1 });
    }

    [Fact]
    public void ValidationFailure_WithNoErrors_GetsAGenericMessage()
    {
        OutputValidationResult result = OutputValidationResult.Failure();

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
    }
}
