using Microsoft.Extensions.AI;
using Trellis.Conversations.Compaction;
using Trellis.Conversations;
using Trellis.Diagnostics;
using Trellis.Outputs;

namespace Trellis.Agents;

/// <summary>The outcome of a single agent run: the typed output plus the raw response.</summary>
public sealed class AgentRunResult<TResult>
{
    /// <summary>
    /// Builds a result. Public because <see cref="Middleware.IAgentMiddleware{TResult}"/> has to
    /// be able to produce one — a pipeline that can only observe cannot serve a cached answer,
    /// substitute a safe reply, or refuse without reaching a model.
    /// </summary>
    /// <param name="output">The typed output.</param>
    /// <param name="response">
    /// The underlying response. Middleware answering without a model call can pass an empty
    /// <see cref="ChatResponse"/>; it must not be null, since callers read
    /// <see cref="Usage"/> and <see cref="Response"/> unconditionally.
    /// </param>
    /// <param name="attempts">How many model calls the run made.</param>
    public AgentRunResult(TResult output, ChatResponse response, int attempts = 1)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentOutOfRangeException.ThrowIfNegative(attempts);
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
