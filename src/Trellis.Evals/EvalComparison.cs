namespace Trellis.Evals;

/// <summary>
/// A candidate run measured against a stored baseline.
/// </summary>
/// <remarks>
/// <para>
/// The reason this type exists rather than a bare score difference: model output varies between
/// identical runs, so a small delta is usually noise. Reporting "quality dropped 3%" without
/// saying how far the baseline's own samples spread is how teams end up chasing changes they
/// never made — and, worse, how a real regression gets dismissed as noise the one time it isn't.
/// </para>
/// <para>
/// So each case is compared against the spread the baseline actually observed, and the ones
/// whose movement exceeds it are named separately. That is a heuristic, not a significance test:
/// with a single sample the spread is zero and every difference looks meaningful, which is
/// exactly why <see cref="EvalOptions.Samples"/> is worth raising before trusting a verdict.
/// </para>
/// </remarks>
public sealed class EvalComparison
{
    private EvalComparison(
        EvalReport baseline,
        EvalReport candidate,
        IReadOnlyList<EvalCaseDelta> deltas)
    {
        Baseline = baseline;
        Candidate = candidate;
        Deltas = deltas;
    }

    /// <summary>The stored run.</summary>
    public EvalReport Baseline { get; }

    /// <summary>The new run.</summary>
    public EvalReport Candidate { get; }

    /// <summary>Per-case movement, ordered by case id.</summary>
    public IReadOnlyList<EvalCaseDelta> Deltas { get; }

    /// <summary>Change in overall mean. Negative is worse.</summary>
    public double MeanDelta => Candidate.Mean - Baseline.Mean;

    /// <summary>Cases that got worse by more than the baseline's own spread.</summary>
    public IReadOnlyList<EvalCaseDelta> Regressions =>
        field ??= [.. Deltas.Where(d => d.IsRegression)];

    /// <summary>Cases that improved by more than the baseline's own spread.</summary>
    public IReadOnlyList<EvalCaseDelta> Improvements =>
        field ??= [.. Deltas.Where(d => d.IsImprovement)];

    /// <summary>Cases in the candidate that the baseline never saw.</summary>
    public IReadOnlyList<string> NewCases => field ??= [.. Deltas.Where(d => d.IsNew).Select(d => d.CaseId)];

    /// <summary>Cases in the baseline that the candidate did not run.</summary>
    public IReadOnlyList<string> MissingCases =>
        field ??= [.. Baseline.Cases.Select(c => c.CaseId)
            .Except(Candidate.Cases.Select(c => c.CaseId), StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)];

    /// <summary>Change in estimated spend, when both runs priced themselves.</summary>
    public decimal? CostDelta =>
        Baseline.EstimatedCost is { } before && Candidate.EstimatedCost is { } after
            ? after - before
            : null;

    /// <summary>Compares a candidate run against a baseline.</summary>
    public static EvalComparison Against(EvalReport baseline, EvalReport candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        Dictionary<string, EvalCaseResult> before =
            baseline.Cases.ToDictionary(c => c.CaseId, StringComparer.Ordinal);

        List<EvalCaseDelta> deltas = [];
        foreach (EvalCaseResult after in candidate.Cases.OrderBy(c => c.CaseId, StringComparer.Ordinal))
        {
            if (!before.TryGetValue(after.CaseId, out EvalCaseResult? baselineCase))
            {
                deltas.Add(new EvalCaseDelta(after.CaseId, null, after.Mean, 0, IsNew: true));
                continue;
            }

            // The tolerance is the baseline's own observed spread: a movement smaller than the
            // variation the baseline already showed is not evidence of anything.
            double tolerance = baselineCase.Scores.Count == 0
                ? 0
                : baselineCase.Scores.Max(s => s.Spread);

            deltas.Add(new EvalCaseDelta(
                after.CaseId, baselineCase.Mean, after.Mean, tolerance, IsNew: false));
        }

        return new EvalComparison(baseline, candidate, deltas);
    }
}
