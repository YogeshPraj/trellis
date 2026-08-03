using Microsoft.Extensions.AI;
using Trellis.Routing;

namespace Trellis.Tests;

public class CapabilityRoutingTests
{
    /// <summary>Records every request; optionally fails, optionally returns a provider conversation id.</summary>
    private sealed class RecordingClient(string name) : IChatClient
    {
        public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Requests { get; } = [];
        public int FailuresRemaining;
        public string? ProviderConversationId { get; set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(([.. messages], options));
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new HttpRequestException("429 Too Many Requests");
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, name))
            {
                ConversationId = ProviderConversationId,
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static ChatMessage User(string text) => new(ChatRole.User, text);

    [Fact]
    public async Task ToolRequest_SkipsEndpointWithoutFunctionCalling()
    {
        var noTools = new RecordingClient("no-tools");
        var withTools = new RecordingClient("with-tools");
        var router = new ModelRouter(
        [
            new("no-tools", noTools, 0, new ModelCapabilities { Features = ModelFeatures.Vision }),
            new("with-tools", withTools, 1),
        ]);

        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "x", name: "t")] };
        ChatResponse response = await router.GetResponseAsync([User("hi")], options);

        Assert.Equal("with-tools", response.Text);
        Assert.Empty(noTools.Requests);
    }

    [Fact]
    public async Task LargeRequest_SkipsSmallContextEndpoint()
    {
        var small = new RecordingClient("small");
        var large = new RecordingClient("large");
        var router = new ModelRouter(
        [
            new("small", small, 0, new ModelCapabilities { MaxInputTokens = 10 }),
            new("large", large, 1),
        ]);

        ChatResponse response = await router.GetResponseAsync([User(new string('x', 500))]);

        Assert.Equal("large", response.Text);
        Assert.Empty(small.Requests);
    }

    [Fact]
    public async Task NoCompatibleEndpoint_ThrowsInsteadOfSendingDoomedRequest()
    {
        var textOnly = new RecordingClient("text-only");
        var router = new ModelRouter(
            [new("text-only", textOnly, 0, new ModelCapabilities { Features = ModelFeatures.None })]);

        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "x", name: "t")] };
        await Assert.ThrowsAsync<NoCompatibleModelException>(() =>
            router.GetResponseAsync([User("hi")], options));
        Assert.Empty(textOnly.Requests);
    }

    [Fact]
    public async Task ServerStateEndpoint_SecondTurnSendsOnlyDeltaWithProviderId()
    {
        var stateful = new RecordingClient("stateful") { ProviderConversationId = "prov-123" };
        var router = new ModelRouter(
        [
            new("stateful", stateful, 0, new ModelCapabilities
            {
                Features = ModelCapabilities.Default.Features | ModelFeatures.ServerConversationState,
            }),
        ]);

        var options = new ChatOptions { ConversationId = "logical-1" };

        // Turn 1: full history, no provider id yet (and our logical id must not leak).
        await router.GetResponseAsync([User("u1")], options);
        Assert.Single(stateful.Requests[0].Messages);
        Assert.Null(stateful.Requests[0].Options!.ConversationId);

        // Turn 2: caller sends canonical full history [u1, a1, u2]; the endpoint already
        // knows u1 + a1, so it receives only u2 plus its own conversation id.
        ChatMessage a1 = new(ChatRole.Assistant, "stateful");
        await router.GetResponseAsync([User("u1"), a1, User("u2")], options);

        Assert.Equal("u2", Assert.Single(stateful.Requests[1].Messages).Text);
        Assert.Equal("prov-123", stateful.Requests[1].Options!.ConversationId);
    }

    [Fact]
    public async Task FailoverMidConversation_StatelessFallbackGetsFullHistory()
    {
        var stateful = new RecordingClient("stateful") { ProviderConversationId = "prov-123" };
        var fallback = new RecordingClient("fallback");
        var router = new ModelRouter(
        [
            new("stateful", stateful, 0, new ModelCapabilities
            {
                Features = ModelCapabilities.Default.Features | ModelFeatures.ServerConversationState,
            }),
            new("fallback", fallback, 1),
        ]);

        var options = new ChatOptions { ConversationId = "logical-1" };
        await router.GetResponseAsync([User("u1")], options);      // served by stateful

        // Stateful runs out of quota mid-conversation.
        stateful.FailuresRemaining = 1;
        ChatMessage a1 = new(ChatRole.Assistant, "stateful");
        ChatResponse response = await router.GetResponseAsync([User("u1"), a1, User("u2")], options);

        // The stateless fallback answers — and receives the ENTIRE canonical history,
        // because the conversation lives client-side, not inside the failed provider.
        Assert.Equal("fallback", response.Text);
        Assert.Equal(3, fallback.Requests[0].Messages.Count);
        Assert.Null(fallback.Requests[0].Options!.ConversationId);
    }

    [Fact]
    public async Task Conversation_WithAgent_AccumulatesHistoryAcrossTurns()
    {
        var client = new FakeChatClient("answer");
        var agent = new Agent(client, instructions: "Be helpful.");
        var conversation = new Conversation();

        await agent.RunAsync(conversation, "first question");
        await agent.RunAsync(conversation, "second question");

        // Second request carries the full canonical history: sys + u1 + a1 + u2.
        IReadOnlyList<ChatMessage> second = client.Requests[1];
        Assert.Equal(4, second.Count);
        Assert.Equal(ChatRole.System, second[0].Role);
        Assert.Equal("first question", second[1].Text);
        Assert.Equal(ChatRole.Assistant, second[2].Role);
        Assert.Equal("second question", second[3].Text);

        // And the conversation object holds u1, a1, u2, a2.
        Assert.Equal(4, conversation.Messages.Count);
        Assert.Equal(client.Options[1]!.ConversationId, conversation.Id);
    }
}
