using Microsoft.Extensions.AI;
using System.Diagnostics;
using Trellis.Conversations.Compaction;
using Trellis.Conversations;
using Trellis.Diagnostics;
using Trellis.Outputs;

namespace Trellis.Agents;

/// <summary>
/// A live agent run: enumerate it for token-by-token updates, then read <see cref="Result"/>
/// for the assembled, deserialized, validated output.
/// </summary>
/// <remarks>
/// <para>
/// Enumerable exactly once — the underlying provider stream cannot be rewound. Abandoning
/// the enumeration early (a <c>break</c>, or a cancelled token) leaves <see cref="Result"/>
/// unavailable and, for conversation runs, folds nothing into the conversation.
/// </para>
/// <para>
/// ⚠ <b>Streaming does not self-heal.</b> Validation can only run once the last token has
/// arrived, and tokens already delivered to a caller cannot be retracted — so a rejected
/// output throws <see cref="OutputValidationException"/> at the end of enumeration instead
/// of silently retrying (which would emit a second, contradictory answer into the same
/// stream). Use the buffered <c>RunAsync</c> overloads when self-healing matters more than
/// first-token latency.
/// </para>
/// </remarks>
public sealed class AgentStream<TResult> : IAsyncEnumerable<ChatResponseUpdate>
{
    private readonly Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> _source;
    private readonly Func<ChatResponse, CancellationToken, ValueTask<AgentRunResult<TResult>>> _complete;
    private readonly ChatOptions? _optionsHint;
    private int _enumerated;
    private AgentRunResult<TResult>? _result;

    internal AgentStream(
        Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> source,
        Func<ChatResponse, CancellationToken, ValueTask<AgentRunResult<TResult>>> complete,
        ChatOptions? optionsHint = null)
    {
        _source = source;
        _complete = complete;
        _optionsHint = optionsHint;
    }

    /// <summary>Whether the stream finished and <see cref="Result"/> is available.</summary>
    public bool IsCompleted => _result is not null;

    /// <summary>
    /// The completed run. Available only after the stream has been enumerated to the end;
    /// reading it earlier throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public AgentRunResult<TResult> Result => _result
        ?? throw new InvalidOperationException(
            "The result is not available until the stream has been fully enumerated.");

    /// <summary>Streams provider updates, then materializes <see cref="Result"/>.</summary>
    public async IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _enumerated, 1) == 1)
        {
            throw new InvalidOperationException(
                "An agent stream can be enumerated only once. Buffer the updates if you need them twice.");
        }

        // The span must cover the whole enumeration, and a stream can be abandoned or throw:
        // 'using' (a try/finally) is legal around a yield, so the activity is always stopped.
        // The inner enumerator is driven by hand because a try/catch may not wrap a yield.
        using Activity? activity = AgentTelemetry.StartRun(typeof(TResult), _optionsHint, streaming: true);
        long startedAt = Stopwatch.GetTimestamp();

        List<ChatResponseUpdate> updates = [];
        await using IAsyncEnumerator<ChatResponseUpdate> source =
            _source(cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            ChatResponseUpdate update;
            try
            {
                if (!await source.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }
                update = source.Current;
            }
            catch (Exception ex)
            {
                AgentTelemetry.RecordFailure(activity, ex, Stopwatch.GetElapsedTime(startedAt));
                throw;
            }

            updates.Add(update);
            yield return update;
        }

        ChatResponse response = updates.ToChatResponse();
        try
        {
            _result = await _complete(response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AgentTelemetry.RecordFailure(activity, ex, Stopwatch.GetElapsedTime(startedAt));
            throw;
        }
        AgentTelemetry.RecordSuccess(activity, response, _result.Attempts, Stopwatch.GetElapsedTime(startedAt));
    }

    /// <summary>
    /// Convenience projection: just the text deltas, with empty ones (tool-call-only or
    /// usage-only updates) filtered out.
    /// </summary>
    public async IAsyncEnumerable<string> TextDeltasAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (ChatResponseUpdate update in this.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (update.Text is { Length: > 0 } text)
            {
                yield return text;
            }
        }
    }
}
