using System.Net;

namespace Trellis.Routing.Failures;

/// <summary>What kind of failure a model call produced.</summary>
public enum FailureKind
{
    /// <summary>Unrecognized — assume the request itself is at fault and propagate.</summary>
    Unknown,

    /// <summary>HTTP 429 / too many requests.</summary>
    RateLimit,

    /// <summary>Out of tokens / credits / billing quota.</summary>
    QuotaExhausted,

    /// <summary>The call timed out.</summary>
    Timeout,

    /// <summary>Provider-side outage (5xx, overloaded).</summary>
    ServerError,

    /// <summary>The input exceeded this model's context window — the model is healthy, the request just doesn't fit it.</summary>
    ContextWindowExceeded,

    /// <summary>The provider's content filter rejected the request; a different provider may accept it.</summary>
    ContentPolicy,
}
