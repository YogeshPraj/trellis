namespace Trellis.Evals;

/// <summary>A scorer written as a lambda — the deterministic, free, obvious-first option.</summary>
public sealed class PredicateScorer<TResult>(
    string name, Func<EvalContext<TResult>, EvalScore> score) : IEvalScorer<TResult>
{
    private readonly Func<EvalContext<TResult>, EvalScore> _score =
        score ?? throw new ArgumentNullException(nameof(score));

    /// <inheritdoc />
    public string Name { get; } = !string.IsNullOrWhiteSpace(name)
        ? name
        : throw new ArgumentException("A scorer needs a stable name.", nameof(name));

    /// <summary>Passes when the output equals <see cref="EvalCase{TResult}.Expected"/>.</summary>
    public static PredicateScorer<TResult> ExactMatch(string name = "exact_match") =>
        new(name, context => Equals(context.Output, context.Case.Expected)
            ? EvalScore.Pass()
            : EvalScore.Fail($"expected '{context.Case.Expected}', got '{context.Output}'"));

    /// <summary>Passes when the response text contains <paramref name="substring"/>.</summary>
    public static PredicateScorer<TResult> Contains(
        string substring, bool ignoreCase = true, string? name = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(substring);
        StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return new PredicateScorer<TResult>(
            name ?? $"contains:{substring}",
            context => context.Text.Contains(substring, comparison)
                ? EvalScore.Pass()
                : EvalScore.Fail($"the answer never mentions '{substring}'"));
    }

    /// <summary>Passes when a boolean check holds.</summary>
    public static PredicateScorer<TResult> Boolean(string name, Func<EvalContext<TResult>, bool> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return new PredicateScorer<TResult>(name, context => check(context) ? EvalScore.Pass() : EvalScore.Fail());
    }

    /// <inheritdoc />
    public ValueTask<EvalScore> ScoreAsync(
        EvalContext<TResult> context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.FromResult(_score(context));
    }
}
