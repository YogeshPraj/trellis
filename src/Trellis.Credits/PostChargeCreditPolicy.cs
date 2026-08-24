using Microsoft.Extensions.AI;

namespace Trellis.Credits;

/// <summary>
/// Lets a request through if the balance is above a floor, and charges the real cost
/// afterwards. Simple, one ledger write per request, and never rejects a call that would
/// have succeeded.
/// </summary>
/// <remarks>
/// <para>
/// The trade is bounded overspend. Cost is only known after the call, so N concurrent
/// requests can each see a sufficient balance and all proceed — a subject can end up
/// somewhere near <c>concurrency × cost-of-one-call</c> past the floor, and the balance goes
/// negative until it is topped up. The next request is then refused.
/// </para>
/// <para>
/// Use <see cref="PrepaidCreditPolicy"/> where that overshoot is unacceptable.
/// </para>
/// </remarks>
/// <param name="ledger">Where entries are written.</param>
/// <param name="rates">Prices usage in credits.</param>
/// <param name="minimumBalance">
/// Balance a subject must be above to start a request (default 0, so a subject may finish
/// spending into a small deficit but cannot start a new call while negative).
/// </param>
/// <param name="allowUnpricedModels">
/// What to do when the model has no price. False (default) refuses the request rather than
/// serving it free; true serves it and records a zero-credit entry for audit.
/// </param>
public sealed class PostChargeCreditPolicy(
    ICreditLedger ledger,
    ICreditRateModel rates,
    long minimumBalance = 0,
    bool allowUnpricedModels = false) : ICreditAdmissionPolicy
{
    private readonly ICreditLedger _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    private readonly ICreditRateModel _rates = rates ?? throw new ArgumentNullException(nameof(rates));

    public async ValueTask<CreditAdmission> AdmitAsync(
        CreditRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        long balance = await _ledger.GetBalanceAsync(request.SubjectId, cancellationToken).ConfigureAwait(false);
        return balance > minimumBalance
            ? CreditAdmission.Allowed
            : CreditAdmission.Denied($"Balance {balance} is not above the minimum of {minimumBalance}.");
    }

    public async ValueTask SettleAsync(
        CreditRequest request,
        CreditAdmission admission,
        UsageDetails? usage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (usage is null)
        {
            return;   // the call produced no usage to charge for
        }

        long? credits = _rates.CreditsFor(request.ModelId, usage);
        if (credits is null && !allowUnpricedModels)
        {
            return;
        }

        // The request id is the entry id, so a retried settle is a duplicate the ledger
        // refuses rather than a second charge.
        await _ledger.TryAppendAsync(
            new CreditEntry(
                request.RequestId,
                request.SubjectId,
                -(credits ?? 0),
                CreditEntryKind.Consumption,
                DateTimeOffset.UtcNow,
                Reason: "post-charge",
                request.ModelId,
                usage.InputTokenCount,
                usage.OutputTokenCount),
            cancellationToken).ConfigureAwait(false);
    }
}
