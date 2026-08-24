using Microsoft.Extensions.AI;

namespace Trellis.Tests;

/// <summary>Proactive rate limiting applied before a provider call.</summary>
public class RateLimitingTests
{
    private sealed class CountingClient : IChatClient
    {
        public int Calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static Task<ChatResponse> Ask(IChatClient client, string? model = null) =>
        client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions { ModelId = model });

    [Fact]
    public async Task RefusesPastTheBudget_WithoutCallingTheProvider()
    {
        var inner = new CountingClient();
        IChatClient client = inner.AsBuilder().UseRateLimit(2, TimeSpan.FromMinutes(1)).Build();

        await Ask(client);
        await Ask(client);
        await Assert.ThrowsAsync<RateLimitRejectedException>(() => Ask(client));

        // The refused request never reached the provider: it cost nothing.
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task BudgetsAreIsolatedPerSubject()
    {
        var inner = new CountingClient();
        string subject = "a";
        IChatClient client = inner.AsBuilder()
            .UseRateLimit(1, TimeSpan.FromMinutes(1), _ => subject)
            .Build();

        await Ask(client);
        await Assert.ThrowsAsync<RateLimitRejectedException>(() => Ask(client));

        subject = "b";
        await Ask(client);   // a different caller has its own budget

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task PerModelBudgetsAreSeparate()
    {
        var inner = new CountingClient();
        IChatClient client = inner.AsBuilder()
            .UseRateLimit(1, TimeSpan.FromMinutes(1), perModel: true)
            .Build();

        await Ask(client, "gpt-4o");
        await Assert.ThrowsAsync<RateLimitRejectedException>(() => Ask(client, "gpt-4o"));
        await Ask(client, "claude");   // separate bucket

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task WithoutPerModel_AllModelsShareOneBudget()
    {
        var inner = new CountingClient();
        IChatClient client = inner.AsBuilder().UseRateLimit(1, TimeSpan.FromMinutes(1)).Build();

        await Ask(client, "gpt-4o");
        await Assert.ThrowsAsync<RateLimitRejectedException>(() => Ask(client, "claude"));

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task StreamingIsLimitedToo()
    {
        var inner = new CountingClient();
        IChatClient client = inner.AsBuilder().UseRateLimit(1, TimeSpan.FromMinutes(1)).Build();

        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
        }

        await Assert.ThrowsAsync<RateLimitRejectedException>(async () =>
        {
            await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            {
            }
        });
    }

    [Fact]
    public async Task TheExceptionNamesThePartition()
    {
        var inner = new CountingClient();
        IChatClient client = inner.AsBuilder()
            .UseRateLimit(1, TimeSpan.FromMinutes(1), _ => "tenant-7", perModel: true)
            .Build();

        await Ask(client, "gpt-4o");
        var ex = await Assert.ThrowsAsync<RateLimitRejectedException>(() => Ask(client, "gpt-4o"));

        Assert.Contains("tenant-7", ex.Partition);
        Assert.Contains("gpt-4o", ex.Partition);
    }
}
