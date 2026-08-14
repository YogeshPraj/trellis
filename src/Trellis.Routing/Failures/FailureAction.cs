using System.Net;

namespace Trellis.Routing.Failures;

/// <summary>What the router should do about a classified failure.</summary>
public enum FailureAction
{
    /// <summary>Rethrow to the caller — trying other endpoints would fail the same way.</summary>
    Propagate,

    /// <summary>Move to the next endpoint and put this one on cooldown.</summary>
    FailoverAndTrip,

    /// <summary>Move to the next endpoint but leave this one healthy — the failure was request-specific.</summary>
    FailoverOnly,
}
