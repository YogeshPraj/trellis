using Microsoft.Extensions.AI;

namespace Trellis.Tests;

/// <summary>Durable per-request usage records, including the requests that went wrong.</summary>
public class UsageRecordingTests
{
    private sealed class ScriptedClient(long input, long output, Exception? failure = null) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            failure is not null
                ? throw failure
                : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
                {
                    ModelId = options?.ModelId,
                    Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output },
                });

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "one ") { ModelId = options?.ModelId };
            yield return new ChatResponseUpdate(ChatRole.Assistant, "two") { ModelId = options?.ModelId };
            yield return new ChatResponseUpdate
            {
                ModelId = options?.ModelId,
                Contents = [new UsageContent(new UsageDetails { InputTokenCount = input, OutputTokenCount = output })],
            };
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task RecordsASuccessfulRequest()
    {
        var sink = new InMemoryUsageRecordSink();
        IChatClient client = new ScriptedClient(100, 20)
            .AsBuilder()
            .UseUsageRecording(sink, _ => "tenant-1",
                new StaticTokenCostModel(new Dictionary<string, ModelPrice> { ["m"] = new(1m, 1m) }))
            .Build();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions { ModelId = "m" });

        UsageRecord record = Assert.Single(sink.Records);
        Assert.Equal("tenant-1", record.SubjectId);
        Assert.Equal("m", record.RequestedModel);
        Assert.Equal("m", record.ServedModel);
        Assert.Equal(100, record.InputTokens);
        Assert.Equal(20, record.OutputTokens);
        Assert.Equal(UsageOutcome.Succeeded, record.Outcome);
        Assert.Equal(0.00012m, record.Cost);
        Assert.True(record.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task RecordsAFailedRequest()
    {
        var sink = new InMemoryUsageRecordSink();
        IChatClient client = new ScriptedClient(0, 0, new HttpRequestException("down"))
            .AsBuilder().UseUsageRecording(sink).Build();

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        UsageRecord record = Assert.Single(sink.Records);
        Assert.Equal(UsageOutcome.Failed, record.Outcome);
        Assert.Equal(nameof(HttpRequestException), record.ErrorType);
    }

    [Fact]
    public async Task RecordsAnAbandonedStream()
    {
        var sink = new InMemoryUsageRecordSink();
        IChatClient client = new ScriptedClient(100, 20).AsBuilder().UseUsageRecording(sink).Build();

        await using (IAsyncEnumerator<ChatResponseUpdate> stream = client
            .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]).GetAsyncEnumerator())
        {
            await stream.MoveNextAsync();   // walk away
        }

        // A caller who disconnects halfway still consumed tokens; under-reporting exactly
        // that traffic would hide the cases worth investigating.
        UsageRecord record = Assert.Single(sink.Records);
        Assert.Equal(UsageOutcome.Abandoned, record.Outcome);
    }

    [Fact]
    public async Task RecordsACompletedStream()
    {
        var sink = new InMemoryUsageRecordSink();
        IChatClient client = new ScriptedClient(100, 20).AsBuilder().UseUsageRecording(sink).Build();

        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
        }

        UsageRecord record = Assert.Single(sink.Records);
        Assert.Equal(UsageOutcome.Succeeded, record.Outcome);
        Assert.Equal(100, record.InputTokens);
    }

    [Fact]
    public async Task ASinkFailureNeverFailsTheRequest()
    {
        IChatClient client = new ScriptedClient(1, 1).AsBuilder().UseUsageRecording(new ThrowingSink()).Build();

        // The response already happened; an audit problem must not become a user-visible error.
        ChatResponse response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("ok", response.Text);
    }

    [Fact]
    public async Task TheBufferIsBounded()
    {
        var sink = new InMemoryUsageRecordSink(capacity: 3);
        IChatClient client = new ScriptedClient(1, 1).AsBuilder().UseUsageRecording(sink).Build();

        for (int i = 0; i < 10; i++)
        {
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        }

        Assert.Equal(3, sink.Records.Count);
    }

    private sealed class ThrowingSink : IUsageRecordSink
    {
        public ValueTask RecordAsync(UsageRecord record, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("sink down");
    }
}
