namespace Trellis.Evals;

/// <summary>Judges one answer.</summary>
/// <remarks>
/// <para>
/// Scorers are the whole design decision of an eval suite. A programmatic scorer is cheap,
/// deterministic, and only knows what you could express in code; a model-graded one reads
/// intent but is itself a model, with a model's variance and a model's blind spots. Most
/// useful suites carry both, and treat disagreement between them as a signal about the suite
/// rather than about the agent.
/// </para>
/// <para>
/// A scorer that throws marks the case errored rather than scoring zero. A judge that was
/// unreachable is not evidence that the answer was bad, and silently averaging it in as zero
/// would make an outage look like a quality regression.
/// </para>
/// </remarks>
public interface IEvalScorer<TResult>
{
    /// <summary>Names this scorer in the report. Keep it stable; baselines are keyed on it.</summary>
    string Name { get; }

    /// <summary>Scores one answer.</summary>
    ValueTask<EvalScore> ScoreAsync(EvalContext<TResult> context, CancellationToken cancellationToken = default);
}
