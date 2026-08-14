using System.Net;

namespace Trellis.Routing.Failures;

/// <summary>A classified failure, with the provider's requested backoff when known.</summary>
/// <param name="Kind">The failure category.</param>
/// <param name="RetryAfter">Exact cooldown requested by the provider (e.g. a Retry-After header); overrides the exponential backoff.</param>
public sealed record FailureClassification(FailureKind Kind, TimeSpan? RetryAfter = null);
