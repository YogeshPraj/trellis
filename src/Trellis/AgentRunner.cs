using Microsoft.Extensions.AI;

namespace Trellis;

/// <summary>Shared run pipeline for all agent flavors.</summary>
internal static class AgentRunner
{
    public static async Task<AgentRunResult<TResult>> RunAsync<TResult>(
        IChatClient client,
        string? instructions,
        ChatOptions? options,
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        List<ChatMessage> all = [];
        if (!string.IsNullOrEmpty(instructions))
        {
            all.Add(new ChatMessage(ChatRole.System, instructions));
        }
        all.AddRange(messages);

        if (typeof(TResult) == typeof(string))
        {
            ChatResponse response = await client
                .GetResponseAsync(all, options, cancellationToken)
                .ConfigureAwait(false);
            return new AgentRunResult<TResult>((TResult)(object)response.Text, response);
        }

        ChatResponse<TResult> typed = await client
            .GetResponseAsync<TResult>(all, options, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new AgentRunResult<TResult>(typed.Result, typed);
    }
}
