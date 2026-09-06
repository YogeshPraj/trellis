namespace Trellis.Evals;

/// <summary>One scorer's results for one case across every sample.</summary>
/// <param name="Scorer">The scorer's name.</param>
/// <param name="Mean">Average score.</param>
/// <param name="Min">Lowest sample.</param>
/// <param name="Max">Highest sample.</param>
/// <param name="Explanations">What each sample said, in order.</param>
public sealed record EvalScoreSummary(
    string Scorer,
    double Mean,
    double Min,
    double Max,
    IReadOnlyList<string> Explanations)
{
    /// <summary>
    /// How far apart the best and worst samples were. The width of this is how large a
    /// difference has to be before it means anything.
    /// </summary>
    public double Spread => Max - Min;
}
