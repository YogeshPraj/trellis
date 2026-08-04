using Microsoft.Extensions.AI;

namespace Trellis.Tests;

/// <summary>
/// A canned-response IChatClient so agent tests run without a model provider. Give it a
/// script of responses to serve in order; the last one repeats once the script runs out.
/// </summary>
public sealed class FakeChatClient(params string[] responses) : IChatClient
{
    private int _served = -1;

    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public List<ChatOptions?> Options { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Requests.Add([.. messages]);
        Options.Add(options);
        int index = Math.Min(Interlocked.Increment(ref _served), responses.Length - 1);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responses[index])));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
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
