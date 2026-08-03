using System.Net;

namespace Trellis.Routing;

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

/// <summary>A classified failure, with the provider's requested backoff when known.</summary>
/// <param name="Kind">The failure category.</param>
/// <param name="RetryAfter">Exact cooldown requested by the provider (e.g. a Retry-After header); overrides the exponential backoff.</param>
public sealed record FailureClassification(FailureKind Kind, TimeSpan? RetryAfter = null);

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

/// <summary>Turns provider exceptions into <see cref="FailureClassification"/>s (Strategy).</summary>
public interface IFailureClassifier
{
    FailureClassification Classify(Exception exception);
}

/// <summary>Decides the routing consequence of a classified failure (Strategy).</summary>
public interface IFailurePolicy
{
    FailureAction Decide(FailureClassification classification);
}

/// <summary>
/// Default classifier: uses typed information (HTTP status codes, timeout exception types)
/// first, then falls back to message heuristics. Numeric status codes in messages must
/// appear as standalone tokens ("HTTP 500", "status: 429") — "took 500ms" or "id 14290"
/// never trips an endpoint. Subclass and override <see cref="ExtractRetryAfter"/> or wrap
/// it to add provider-specific knowledge.
/// </summary>
public partial class DefaultFailureClassifier : IFailureClassifier
{
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![\w.])(429)(?![\w.])")]
    private static partial System.Text.RegularExpressions.Regex RateLimitStatusPattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![\w.])(500|502|503|504)(?![\w.])")]
    private static partial System.Text.RegularExpressions.Regex ServerErrorStatusPattern();
    public FailureClassification Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        TimeSpan? retryAfter = ExtractRetryAfter(exception);

        if (exception is HttpRequestException { StatusCode: HttpStatusCode status })
        {
            FailureKind? byStatus = status switch
            {
                HttpStatusCode.TooManyRequests => FailureKind.RateLimit,
                HttpStatusCode.PaymentRequired => FailureKind.QuotaExhausted,
                HttpStatusCode.RequestTimeout => FailureKind.Timeout,
                >= HttpStatusCode.InternalServerError => FailureKind.ServerError,
                _ => null,
            };
            if (byStatus is FailureKind kind)
            {
                return new FailureClassification(kind, retryAfter);
            }
        }

        if (exception is TimeoutException or TaskCanceledException)
        {
            return new FailureClassification(FailureKind.Timeout, retryAfter);
        }

        return new FailureClassification(ClassifyByText(exception.ToString()), retryAfter);
    }

    /// <summary>
    /// Extracts the provider's requested backoff. The default looks for a
    /// <c>"RetryAfter"</c> entry in <see cref="Exception.Data"/> (as a
    /// <see cref="TimeSpan"/> or a number of seconds); override to read your
    /// provider SDK's typed exception instead.
    /// </summary>
    protected virtual TimeSpan? ExtractRetryAfter(Exception exception) =>
        exception.Data["RetryAfter"] switch
        {
            TimeSpan span => span,
            int seconds => TimeSpan.FromSeconds(seconds),
            double seconds => TimeSpan.FromSeconds(seconds),
            string text when double.TryParse(text, out double seconds) => TimeSpan.FromSeconds(seconds),
            _ => null,
        };

    private static FailureKind ClassifyByText(string text)
    {
        bool Has(string phrase) => text.Contains(phrase, StringComparison.OrdinalIgnoreCase);

        if (Has("context length") || Has("context_length") || Has("context window") || Has("maximum context"))
        {
            return FailureKind.ContextWindowExceeded;
        }
        if (Has("content policy") || Has("content_policy") || Has("content filter") || Has("content_filter") || Has("content management policy"))
        {
            return FailureKind.ContentPolicy;
        }
        if (Has("quota") || Has("insufficient") || Has("billing") || Has("credit"))
        {
            return FailureKind.QuotaExhausted;
        }
        if (RateLimitStatusPattern().IsMatch(text) || Has("rate limit") || Has("ratelimit") || Has("too many requests"))
        {
            return FailureKind.RateLimit;
        }
        if (Has("timeout") || Has("timed out"))
        {
            return FailureKind.Timeout;
        }
        if (ServerErrorStatusPattern().IsMatch(text) || Has("overloaded") || Has("unavailable") || Has("server error"))
        {
            return FailureKind.ServerError;
        }
        return FailureKind.Unknown;
    }
}

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
