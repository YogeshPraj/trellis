using Microsoft.Extensions.AI;
using Trellis.Routing.Capabilities;
using Trellis.Routing.Failures;
using Trellis.Routing.Health;
using Trellis.Routing.Selection;

namespace Trellis.Routing;

/// <summary>Thrown when no endpoint could serve the request.</summary>
public sealed class AllModelsUnavailableException : Exception
{
    public AllModelsUnavailableException(string message, IReadOnlyList<Exception> attempts)
        : base(message, attempts.Count > 0 ? new AggregateException(attempts) : null)
    {
        Attempts = attempts;
    }

    /// <summary>The failure from each endpoint that was attempted for this request.</summary>
    public IReadOnlyList<Exception> Attempts { get; }
}
