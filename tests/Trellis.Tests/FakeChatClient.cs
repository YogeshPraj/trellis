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

    /// <summary>
    /// Streams the scripted response in whitespace-delimited chunks, so tests see genuine
    /// multi-update assembly rather than a single-shot update that would hide ordering bugs.
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        string text = response.Text;
        int start = 0;
        while (start < text.Length)
        {
            int space = text.IndexOf(' ', start);
            int end = space < 0 ? text.Length : space + 1;
            yield return new ChatResponseUpdate(ChatRole.Assistant, text[start..end]);
            start = end;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
