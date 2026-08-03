using Microsoft.Extensions.AI;

namespace Trellis;

/// <summary>The outcome of a single agent run: the typed output plus the raw response.</summary>
public sealed class AgentRunResult<TResult>
{
    internal AgentRunResult(TResult output, ChatResponse response)
    {
        Output = output;
        Response = response;
    }

    /// <summary>The strongly-typed result produced by the model.</summary>
    public TResult Output { get; }

    /// <summary>The underlying response, including all messages (tool calls, etc.).</summary>
    public ChatResponse Response { get; }

    /// <summary>Token usage for the run, when the provider reports it.</summary>
    public UsageDetails? Usage => Response.Usage;
}
