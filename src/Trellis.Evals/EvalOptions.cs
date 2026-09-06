namespace Trellis.Evals;

/// <summary>How a suite runs.</summary>
public sealed class EvalOptions
{
    /// <summary>
    /// How many times each case runs. Default 1, which is enough to catch a hard break and
    /// <b>not</b> enough to call a small quality change.
    /// </summary>
    /// <remarks>
    /// Model output varies between identical runs, so a single sample cannot separate "this
    /// change made it worse" from "this is a different roll of the dice". Three to five samples
    /// makes the spread visible; the report shows min and max next to the mean so the size of
    /// the noise is on the page rather than assumed away.
    /// </remarks>
    public int Samples { get; set; } = 1;

    /// <summary>
    /// How many samples run at once. Default 4 — enough to keep a suite quick, low enough that
    /// a provider's rate limiter is not the thing being measured.
    /// </summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>
    /// The mean score at or above which a case passes. Default 1.0: every scorer must be happy.
    /// </summary>
    public double PassThreshold { get; set; } = 1.0;
}
