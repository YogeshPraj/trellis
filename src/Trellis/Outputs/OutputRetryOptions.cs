using Microsoft.Extensions.AI;
using Trellis.Agents;
using Trellis.Conversations;

namespace Trellis.Outputs;

/// <summary>
/// Controls the self-healing loop for agent outputs: when a response fails to deserialize
/// into the result type, or an <see cref="IOutputValidator{TResult}"/> rejects it, the
/// errors are appended as a correction message (alongside the model's own failed attempt)
/// and the request is retried — up to <see cref="MaxRetries"/> times — before an
/// <see cref="OutputValidationException"/> surfaces.
/// </summary>
/// <remarks>
/// Cost model: every retry resends the full request payload plus the failed attempt and
/// the correction message, so worst-case token spend is roughly
/// <c>(1 + MaxRetries)</c> × the original request (growing slightly per attempt).
/// <see cref="AgentRunResult{TResult}.Attempts"/> reports what a run actually used.
/// Retry traffic never enters a <see cref="Conversation"/>'s canonical history — only the
/// final accepted response is folded in.
/// </remarks>
public sealed class OutputRetryOptions
{
    internal static OutputRetryOptions Default { get; } = new();

    private readonly int _maxRetries = 2;

    /// <summary>
    /// Correction round-trips allowed after the first attempt (default 2, i.e. at most
    /// 3 model calls per run). Set to 0 to fail fast on the first invalid response —
    /// the failure still surfaces as a typed <see cref="OutputValidationException"/>.
    /// </summary>
    public int MaxRetries
    {
        get => _maxRetries;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxRetries = value;
        }
    }

    /// <summary>
    /// The role of the correction message (default <see cref="ChatRole.User"/> — the most
    /// portable choice; some providers reject system messages after assistant turns).
    /// </summary>
    public ChatRole FeedbackRole { get; init; } = ChatRole.User;

    /// <summary>
    /// Builds the correction message shown to the model from the failed attempt.
    /// Defaults to a bulleted list of the errors plus an instruction to answer again
    /// with only the corrected response.
    /// </summary>
    public Func<OutputFailure, string>? FeedbackFormatter { get; init; }
}
