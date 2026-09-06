namespace Trellis.Evals;

/// <summary>How one case moved between two runs.</summary>
/// <param name="CaseId">The case.</param>
/// <param name="Before">The baseline's mean, or null when the baseline never saw this case.</param>
/// <param name="After">The candidate's mean.</param>
/// <param name="Tolerance">
/// The widest spread the baseline's own samples showed for this case. Movement inside it is
/// indistinguishable from the variation that was already there.
/// </param>
/// <param name="IsNew">Whether the baseline had no record of this case.</param>
public sealed record EvalCaseDelta(
    string CaseId, double? Before, double After, double Tolerance, bool IsNew)
{
    /// <summary>How much the score moved. Negative is worse. Zero for a new case.</summary>
    public double Change => Before is { } before ? After - before : 0;

    /// <summary>Dropped by more than the baseline's own spread.</summary>
    public bool IsRegression => !IsNew && Change < 0 && Math.Abs(Change) > Tolerance;

    /// <summary>Rose by more than the baseline's own spread.</summary>
    public bool IsImprovement => !IsNew && Change > 0 && Change > Tolerance;

    /// <summary>Moved, but by less than the baseline already varied on its own.</summary>
    public bool IsWithinNoise => !IsNew && Math.Abs(Change) <= Tolerance;
}
