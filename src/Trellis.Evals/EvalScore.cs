namespace Trellis.Evals;

/// <summary>What one scorer thought of one answer.</summary>
/// <param name="Value">
/// Between 0 and 1. Continuous rather than pass/fail because "slightly worse" is the change an
/// eval suite exists to catch, and a boolean cannot express it.
/// </param>
/// <param name="Explanation">Why, for a human reading a failure. Optional but worth writing.</param>
public readonly record struct EvalScore(double Value, string? Explanation = null)
{
    /// <summary>Full marks.</summary>
    public static EvalScore Pass(string? explanation = null) => new(1.0, explanation);

    /// <summary>No marks.</summary>
    public static EvalScore Fail(string? explanation = null) => new(0.0, explanation);

    /// <summary>A partial score, clamped into range so a scorer cannot skew an average.</summary>
    public static EvalScore Partial(double value, string? explanation = null) =>
        new(Math.Clamp(value, 0.0, 1.0), explanation);
}
