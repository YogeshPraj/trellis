using System.Text;
using Microsoft.Extensions.AI;

namespace Trellis.Tests;

public class AgentStreamingTests
{
    private sealed record FlightResult(string Destination, decimal Price);

    private const string GoodJson = """{"destination":"Pune","price":129.50}""";

    [Fact]
    public async Task TextAgent_StreamsDeltas_ThenExposesResult()
    {
        var client = new FakeChatClient("Hello from Trellis today");
        var agent = new Agent(client);

        AgentStream<string> stream = agent.RunStreamingAsync("hi");
        var received = new List<string>();
        await foreach (ChatResponseUpdate update in stream)
        {
            received.Add(update.Text);
        }

        Assert.True(received.Count > 1, "expected more than one update");
        Assert.Equal("Hello from Trellis today", string.Concat(received));
        Assert.True(stream.IsCompleted);
        Assert.Equal("Hello from Trellis today", stream.Result.Output);
    }

    [Fact]
    public async Task TextDeltas_ProjectsNonEmptyTextOnly()
    {
        var client = new FakeChatClient("one two three");
        var agent = new Agent(client);

        var deltas = new List<string>();
        AgentStream<string> stream = agent.RunStreamingAsync("hi");
        await foreach (string delta in stream.TextDeltasAsync())
        {
            deltas.Add(delta);
        }

        Assert.All(deltas, d => Assert.NotEqual(string.Empty, d));
        Assert.Equal("one two three", string.Concat(deltas));
    }

    [Fact]
    public async Task Result_BeforeCompletion_Throws()
    {
        var client = new FakeChatClient("a b c");
        var agent = new Agent(client);

        AgentStream<string> stream = agent.RunStreamingAsync("hi");
        Assert.False(stream.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => stream.Result);

        await using IAsyncEnumerator<ChatResponseUpdate> enumerator = stream.GetAsyncEnumerator();
        await enumerator.MoveNextAsync();
        // Mid-stream the result is still unavailable.
        Assert.Throws<InvalidOperationException>(() => stream.Result);
    }

    [Fact]
    public async Task Stream_CanOnlyBeEnumeratedOnce()
    {
        var client = new FakeChatClient("a b");
        var agent = new Agent(client);
        AgentStream<string> stream = agent.RunStreamingAsync("hi");

        await foreach (ChatResponseUpdate _ in stream)
        {
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (ChatResponseUpdate _ in stream)
            {
            }
        });
    }

    [Fact]
    public async Task TypedAgent_Streams_ThenDeserializes()
    {
        var client = new FakeChatClient(GoodJson);
        var agent = new Agent<FlightResult>(client);

        AgentStream<FlightResult> stream = agent.RunStreamingAsync("book a flight");
        var buffer = new StringBuilder();
        await foreach (ChatResponseUpdate update in stream)
        {
            buffer.Append(update.Text);
        }

        Assert.Equal(GoodJson, buffer.ToString());
        Assert.Equal("Pune", stream.Result.Output.Destination);
        Assert.Equal(129.50m, stream.Result.Output.Price);
    }

    [Fact]
    public async Task TypedAgent_RequestsJsonSchemaResponseFormat()
    {
        var client = new FakeChatClient(GoodJson);
        var agent = new Agent<FlightResult>(client);

        await foreach (ChatResponseUpdate _ in agent.RunStreamingAsync("book"))
        {
        }

        ChatOptions? options = Assert.Single(client.Options);
        Assert.IsType<ChatResponseFormatJson>(options!.ResponseFormat);
    }

    [Fact]
    public async Task StringAgent_DoesNotForceAResponseFormat()
    {
        var client = new FakeChatClient("plain text");
        var agent = new Agent(client);

        await foreach (ChatResponseUpdate _ in agent.RunStreamingAsync("hi"))
        {
        }

        Assert.Null(Assert.Single(client.Options)?.ResponseFormat);
    }

    private sealed class RejectEverythingValidator : IOutputValidator<FlightResult>
    {
        public ValueTask<OutputValidationResult> ValidateAsync(FlightResult output, CancellationToken cancellationToken = default)
            => new(OutputValidationResult.Failure("Nope."));
    }

    [Fact]
    public async Task Streaming_DoesNotSelfHeal_ThrowsAtEndOfEnumeration()
    {
        var client = new FakeChatClient(GoodJson, GoodJson);
        var agent = new Agent<FlightResult>(client, outputValidator: new RejectEverythingValidator());

        var ex = await Assert.ThrowsAsync<OutputValidationException>(async () =>
        {
            await foreach (ChatResponseUpdate _ in agent.RunStreamingAsync("book"))
            {
            }
        });

        Assert.Equal(1, ex.Attempts);
        // One model call only: a retry would have emitted a second answer into the same stream.
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task MalformedJson_ThrowsTypedException_NotRawJsonException()
    {
        var client = new FakeChatClient("not json at all");
        var agent = new Agent<FlightResult>(client);

        await Assert.ThrowsAsync<OutputValidationException>(async () =>
        {
            await foreach (ChatResponseUpdate _ in agent.RunStreamingAsync("book"))
            {
            }
        });
    }

    [Fact]
    public async Task Conversation_AbsorbsTurn_AfterStreamCompletes()
    {
        var client = new FakeChatClient("the answer is orange");
        var agent = new Agent(client);
        var conversation = new Conversation();

        AgentStream<string> stream = agent.RunStreamingAsync(conversation, "what color?");
        Assert.Empty(conversation.Messages); // nothing happens until enumeration starts

        await foreach (ChatResponseUpdate _ in stream)
        {
        }

        Assert.Equal(2, conversation.Messages.Count);
        Assert.Equal("what color?", conversation.Messages[0].Text);
        Assert.Equal(ChatRole.Assistant, conversation.Messages[1].Role);
        Assert.Equal("the answer is orange", conversation.Messages[1].Text);
    }

    [Fact]
    public async Task Conversation_Streaming_SendsRoutingIdAndFullHistory()
    {
        var client = new FakeChatClient("first reply", "second reply");
        var agent = new Agent(client, instructions: "Be brief.");
        var conversation = new Conversation();

        await Drain(agent.RunStreamingAsync(conversation, "one"));
        await Drain(agent.RunStreamingAsync(conversation, "two"));

        Assert.Equal(conversation.RoutingId, client.Options[1]?.ConversationId);
        IReadOnlyList<ChatMessage> second = client.Requests[1];
        Assert.Equal(ChatRole.System, second[0].Role);      // instructions
        Assert.Equal("one", second[1].Text);
        Assert.Equal("first reply", second[2].Text);
        Assert.Equal("two", second[3].Text);
    }

    [Fact]
    public async Task AbandonedStream_LeavesNoAssistantTurn()
    {
        var client = new FakeChatClient("a b c d e");
        var agent = new Agent(client);
        var conversation = new Conversation();

        AgentStream<string> stream = agent.RunStreamingAsync(conversation, "hi");
        await using (IAsyncEnumerator<ChatResponseUpdate> enumerator = stream.GetAsyncEnumerator())
        {
            await enumerator.MoveNextAsync();
        }

        // The user turn is recorded, but no partial assistant reply is committed.
        Assert.Single(conversation.Messages);
        Assert.Equal(ChatRole.User, conversation.Messages[0].Role);
        Assert.False(stream.IsCompleted);
    }

    [Fact]
    public async Task AgentWithDeps_Streams()
    {
        var client = new FakeChatClient(GoodJson);
        var agent = new Agent<string, FlightResult>(client, tools: _ => []);

        AgentStream<FlightResult> stream = agent.RunStreamingAsync("deps", "book");
        await Drain(stream);

        Assert.Equal("Pune", stream.Result.Output.Destination);
    }

    [Fact]
    public async Task Streaming_WithCompactor_CompactsAfterTheTurn()
    {
        var client = new FakeChatClient("reply");
        var compactor = new ConversationCompactor(
            new StubSummarizer("summary"),
            options: new CompactionOptions { MaxHotMessages = 2, KeepRecentMessages = 1 });
        var agent = new Agent(client, compactor: compactor);
        var conversation = new Conversation();

        await Drain(agent.RunStreamingAsync(conversation, "one"));   // 2 messages: under budget
        await Drain(agent.RunStreamingAsync(conversation, "two"));   // 4 messages: over budget
        Assert.NotNull(conversation.PendingCompaction);
        await conversation.PendingCompaction!;

        Assert.Equal("summary", conversation.Summary);
        Assert.Equal(1, conversation.ContextEpoch);
        Assert.Single(conversation.Messages);
    }

    private sealed class StubSummarizer(string summary) : IConversationSummarizer
    {
        public Task<string> SummarizeAsync(
            string? existingSummary,
            IReadOnlyList<ChatMessage> evictedMessages,
            CancellationToken cancellationToken = default) => Task.FromResult(summary);
    }

    private static async Task Drain<T>(AgentStream<T> stream)
    {
        await foreach (ChatResponseUpdate _ in stream)
        {
        }
    }
}
