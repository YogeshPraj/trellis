using Microsoft.Extensions.AI;
using Trellis.Conversations.Compaction;
using Trellis.Conversations;
using Trellis.Diagnostics;
using Trellis.Outputs;

namespace Trellis.Agents;

/// <summary>The outcome of a single agent run: the typed output plus the raw response.</summary>
public sealed class AgentRunResult<TResult>
{
    internal AgentRunResult(TResult output, ChatResponse response, int attempts = 1)
    {
        Output = output;
        Response = response;
        Attempts = attempts;
    }

    /// <summary>The strongly-typed result produced by the model.</summary>
    public TResult Output { get; }

    /// <summary>The underlying response, including all messages (tool calls, etc.).</summary>
    public ChatResponse Response { get; }

    /// <summary>
    /// How many model calls this run made: 1 normally, more when self-healing retries
    /// corrected a failed output (see <see cref="OutputRetryOptions"/>). Each retry
    /// re-pays roughly the full request cost, so watch this for spend visibility.
    /// </summary>
    public int Attempts { get; }

    /// <summary>Token usage for the run, when the provider reports it.</summary>
    public UsageDetails? Usage => Response.Usage;
}
