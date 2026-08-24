using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Trellis.Diagnostics;

/// <summary>
/// Writes a <see cref="UsageRecord"/> for every request passing through the pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The record is written in a <c>finally</c>, so a request that fails or is abandoned still
/// produces a row. That matters most for streaming: a caller who disconnects halfway has
/// still consumed tokens, and a sink that only recorded clean completions would under-report
/// exactly the traffic worth investigating.
/// </para>
/// <para>
/// A sink failure never fails the request. The response already happened; turning an audit
/// problem into a user-visible error would be the wrong trade.
/// </para>
/// </remarks>
public sealed class UsageRecordingChatClient(
    IChatClient innerClient,
    IUsageRecordSink sink,
    Func<ChatOptions?, string?>? subjectSelector = null,
    ITokenCostModel? costModel = null) : DelegatingChatClient(innerClient)
{
    private readonly IUsageRecordSink _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        long started = Stopwatch.GetTimestamp();
        DateTimeOffset at = DateTimeOffset.UtcNow;
        ChatResponse? response = null;
        Exception? failure = null;
        try
        {
            response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            await WriteAsync(at, started, options, response?.ModelId, response?.Usage, failure).ConfigureAwait(false);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long started = Stopwatch.GetTimestamp();
        DateTimeOffset at = DateTimeOffset.UtcNow;
        List<ChatResponseUpdate> updates = [];
        bool completed = false;
        try
        {
            await foreach (ChatResponseUpdate update in base
                .GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
                yield return update;
            }
            completed = true;
        }
        finally
        {
            ChatResponse assembled = updates.ToChatResponse();
            await WriteAsync(
                at, started, options, assembled.ModelId, assembled.Usage,
                failure: null, abandoned: !completed).ConfigureAwait(false);
        }
    }

    private async ValueTask WriteAsync(
        DateTimeOffset at,
        long started,
        ChatOptions? options,
        string? servedModel,
        UsageDetails? usage,
        Exception? failure,
        bool abandoned = false)
    {
        UsageOutcome outcome = failure is not null
            ? UsageOutcome.Failed
            : abandoned ? UsageOutcome.Abandoned : UsageOutcome.Succeeded;

        var record = new UsageRecord(
            Guid.NewGuid().ToString("N"),
            at,
            subjectSelector?.Invoke(options),
            options?.ModelId,
            servedModel,
            usage?.InputTokenCount,
            usage?.OutputTokenCount,
            usage?.CachedInputTokenCount,
            usage is null ? null : (costModel ?? AgentTelemetry.CostModel)?.EstimateCost(servedModel, usage),
            Stopwatch.GetElapsedTime(started),
            outcome,
            failure?.GetType().Name);

        try
        {
            await _sink.RecordAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The response already happened; an audit failure must not become a user-visible
            // error. Sinks are expected to handle their own durability.
        }
    }
}
