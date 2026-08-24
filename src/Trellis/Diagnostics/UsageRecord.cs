namespace Trellis.Diagnostics;

/// <summary>How a request ended.</summary>
public enum UsageOutcome
{
    /// <summary>The provider returned a response.</summary>
    Succeeded,

    /// <summary>The request failed after every attempt.</summary>
    Failed,

    /// <summary>The caller cancelled, or walked away from a stream before it finished.</summary>
    Abandoned,
}

/// <summary>
/// A durable record of one request. Where <see cref="AgentTelemetry"/> emits spans and metrics
/// for live observability, this is the row you keep: what was spent, by whom, on what.
/// </summary>
/// <param name="Id">Unique per request; also the idempotency key for a sink that needs one.</param>
/// <param name="At">When the request started.</param>
/// <param name="SubjectId">Who the request was for, when the application tracks that.</param>
/// <param name="RequestedModel">The model the caller asked for — an alias, if they used one.</param>
/// <param name="ServedModel">The concrete model that answered, once resolution is done.</param>
/// <param name="InputTokens">Prompt tokens the provider reported.</param>
/// <param name="OutputTokens">Completion tokens the provider reported.</param>
/// <param name="CachedInputTokens">Prompt tokens served from cache, when reported.</param>
/// <param name="Cost">Estimated cost, when a cost model priced it.</param>
/// <param name="Duration">Wall time of the whole request, retries included.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="ErrorType">Type name of the failure, when it failed.</param>
public sealed record UsageRecord(
    string Id,
    DateTimeOffset At,
    string? SubjectId,
    string? RequestedModel,
    string? ServedModel,
    long? InputTokens,
    long? OutputTokens,
    long? CachedInputTokens,
    decimal? Cost,
    TimeSpan Duration,
    UsageOutcome Outcome,
    string? ErrorType = null);
