namespace Trellis.Evals;

/// <summary>What happened to one case.</summary>
public sealed record EvalCaseResult
{
    /// <summary>The case's stable id.</summary>
    public required string CaseId { get; init; }

    /// <summary>Per-scorer results across the samples.</summary>
    public required IReadOnlyList<EvalScoreSummary> Scores { get; init; }

    /// <summary>Mean across every scorer and sample — the case's headline number.</summary>
    public required double Mean { get; init; }

    /// <summary>Whether <see cref="Mean"/> reached the suite's threshold.</summary>
    public required bool Passed { get; init; }

    /// <summary>How many samples ran.</summary>
    public required int Samples { get; init; }

    /// <summary>Average wall-clock time per sample.</summary>
    public required TimeSpan AverageDuration { get; init; }

    /// <summary>Total tokens across samples, when the provider reported them.</summary>
    public long? TotalTokens { get; init; }

    /// <summary>Estimated spend across samples, when a cost model was supplied.</summary>
    public decimal? EstimatedCost { get; init; }

    /// <summary>
    /// Why the case could not be scored, if it could not. An errored case is not a zero: a
    /// provider outage is not evidence that an answer was bad.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>Whether the case failed to run at all.</summary>
    public bool Errored => Error is not null;

    /// <summary>The case's labels, carried through for slicing a report.</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
