using System.Net;

namespace Trellis.Routing.Failures;

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
