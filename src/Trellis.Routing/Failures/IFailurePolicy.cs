using System.Net;

namespace Trellis.Routing.Failures;

/// <summary>Decides the routing consequence of a classified failure (Strategy).</summary>
public interface IFailurePolicy
{
    FailureAction Decide(FailureClassification classification);
}
