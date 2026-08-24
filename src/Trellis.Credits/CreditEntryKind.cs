namespace Trellis.Credits;

/// <summary>Why a ledger entry exists.</summary>
public enum CreditEntryKind
{
    /// <summary>Credits added — a plan's periodic top-up, an admin adjustment, a redeemed code.</summary>
    Grant,

    /// <summary>Credits spent on a completed request.</summary>
    Consumption,

    /// <summary>Credits held for an in-flight request under a prepaid policy.</summary>
    Reservation,

    /// <summary>The unused part of a reservation, returned once the real cost is known.</summary>
    ReservationRelease,
}
