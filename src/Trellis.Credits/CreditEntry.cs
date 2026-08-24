namespace Trellis.Credits;

/// <summary>
/// One immutable movement of credits. The ledger is append-only: a balance is the sum of
/// entries, never a field that gets overwritten.
/// </summary>
/// <param name="Id">
/// Idempotency key — the request id for a consumption, the grant id for a top-up. Appending
/// the same id twice is refused, so a retried charge cannot double-spend.
/// </param>
/// <param name="SubjectId">Whose balance this moves.</param>
/// <param name="Amount">
/// Micro-credits: positive adds, negative spends. Integers, because a ledger in floating
/// point accumulates drift you can never reconcile.
/// </param>
/// <param name="Kind">Why the entry exists.</param>
/// <param name="At">When it was recorded.</param>
/// <param name="Reason">Free-text context for audit ("plan-monthly", "admin-adjustment").</param>
/// <param name="ModelId">For a consumption, the model that was billed.</param>
/// <param name="InputTokens">For a consumption, the input tokens charged.</param>
/// <param name="OutputTokens">For a consumption, the output tokens charged.</param>
/// <param name="ReservationId">Links a release back to the reservation it settles.</param>
public sealed record CreditEntry(
    string Id,
    string SubjectId,
    long Amount,
    CreditEntryKind Kind,
    DateTimeOffset At,
    string? Reason = null,
    string? ModelId = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    string? ReservationId = null);
