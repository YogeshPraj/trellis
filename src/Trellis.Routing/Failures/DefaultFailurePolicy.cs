using System.Net;

namespace Trellis.Routing.Failures;

/// <summary>
/// Default policy: transient provider problems fail over and trip; request-shaped problems
/// (context window, content policy) fail over without penalizing the endpoint; unknown
/// errors propagate. Individual kinds can be remapped via the constructor.
/// </summary>
public sealed class DefaultFailurePolicy : IFailurePolicy
{
    private readonly Dictionary<FailureKind, FailureAction> _actions;

    public DefaultFailurePolicy(IReadOnlyDictionary<FailureKind, FailureAction>? overrides = null)
    {
        _actions = new Dictionary<FailureKind, FailureAction>
        {
            [FailureKind.RateLimit] = FailureAction.FailoverAndTrip,
            [FailureKind.QuotaExhausted] = FailureAction.FailoverAndTrip,
            [FailureKind.Timeout] = FailureAction.FailoverAndTrip,
            [FailureKind.ServerError] = FailureAction.FailoverAndTrip,
            [FailureKind.ContextWindowExceeded] = FailureAction.FailoverOnly,
            [FailureKind.ContentPolicy] = FailureAction.FailoverOnly,
            [FailureKind.Unknown] = FailureAction.Propagate,
        };
        if (overrides is not null)
        {
            foreach ((FailureKind kind, FailureAction action) in overrides)
            {
                _actions[kind] = action;
            }
        }
    }

    public FailureAction Decide(FailureClassification classification)
    {
        ArgumentNullException.ThrowIfNull(classification);
        return _actions.TryGetValue(classification.Kind, out FailureAction action)
            ? action
            : FailureAction.Propagate;
    }
}
