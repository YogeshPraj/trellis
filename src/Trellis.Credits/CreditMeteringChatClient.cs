using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Trellis.Tokens;

namespace Trellis.Credits;

/// <summary>
/// Puts credit admission and settlement around any <see cref="IChatClient"/> — including a
/// <c>ModelRouter</c> — so metered access works in-process, with or without a gateway in front.
/// </summary>
/// <remarks>
/// <para>
/// Admission runs before the call and throws <see cref="InsufficientCreditsException"/> when
/// refused. Settlement runs afterwards in a <c>finally</c>, so a hold is released even when
/// the call throws — an unsettled prepaid reservation would otherwise strand credits until it
/// expired.
/// </para>
/// <para>
/// Streaming settles once the stream ends or is abandoned, using whatever usage the provider
/// reported. A caller that disconnects mid-stream still settles, because the enumerator's
/// <c>finally</c> runs on disposal.
/// </para>
/// </remarks>
public sealed class CreditMeteringChatClient(
    IChatClient innerClient,
    ICreditAdmissionPolicy policy,
    Func<ChatOptions?, string?> subjectSelector,
    ITokenCounter? tokenCounter = null) : DelegatingChatClient(innerClient)
{
    private readonly ICreditAdmissionPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));

    private readonly Func<ChatOptions?, string?> _subject =
        subjectSelector ?? throw new ArgumentNullException(nameof(subjectSelector));

    private readonly ITokenCounter _counter = tokenCounter ?? HeuristicTokenCounter.Default;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        IList<ChatMessage> all = messages as IList<ChatMessage> ?? [.. messages];
        (CreditRequest request, CreditAdmission admission) =
            await AdmitAsync(all, options, cancellationToken).ConfigureAwait(false);

        UsageDetails? usage = null;
        try
        {
            ChatResponse response = await base.GetResponseAsync(all, options, cancellationToken).ConfigureAwait(false);
            usage = response.Usage;
            return response;
        }
        finally
        {
            await _policy.SettleAsync(request, admission, usage, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IList<ChatMessage> all = messages as IList<ChatMessage> ?? [.. messages];
        (CreditRequest request, CreditAdmission admission) =
            await AdmitAsync(all, options, cancellationToken).ConfigureAwait(false);

        List<ChatResponseUpdate> updates = [];
        try
        {
            await foreach (ChatResponseUpdate update in base
                .GetStreamingResponseAsync(all, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
                yield return update;
            }
        }
        finally
        {
            // Runs on completion, on an exception, and on an abandoned enumeration, so a
            // prepaid hold is never left dangling by a caller that walked away.
            await _policy
                .SettleAsync(request, admission, updates.ToChatResponse().Usage, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<(CreditRequest Request, CreditAdmission Admission)> AdmitAsync(
        IList<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        string subject = _subject(options)
            ?? throw new InvalidOperationException(
                "No credit subject was resolved for this request. The selector must identify who pays.");

        var request = new CreditRequest(
            subject,
            Guid.NewGuid().ToString("N"),
            options?.ModelId,
            _counter.CountTokens(messages),
            options?.MaxOutputTokens);

        CreditAdmission admission = await _policy.AdmitAsync(request, cancellationToken).ConfigureAwait(false);
        return admission.IsAllowed
            ? (request, admission)
            : throw new InsufficientCreditsException(subject, admission.Reason ?? "refused");
    }
}
