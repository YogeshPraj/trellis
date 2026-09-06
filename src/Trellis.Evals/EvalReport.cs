using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trellis.Evals;

/// <summary>The result of running a suite. Serializable, so it can be stored as a baseline.</summary>
public sealed record EvalReport
{
    /// <summary>What the suite was called.</summary>
    public required string SuiteName { get; init; }

    /// <summary>When the run finished.</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>Every case, ordered by id so two reports diff cleanly.</summary>
    public required IReadOnlyList<EvalCaseResult> Cases { get; init; }

    /// <summary>How many samples each case ran.</summary>
    public required int Samples { get; init; }

    /// <summary>Mean across every case that ran. Errored cases are excluded, not counted as zero.</summary>
    public double Mean => Scored.Count == 0 ? 0 : Scored.Average(c => c.Mean);

    /// <summary>Cases that passed.</summary>
    public int PassedCount => Scored.Count(c => c.Passed);

    /// <summary>Cases that ran but scored below the threshold.</summary>
    public int FailedCount => Scored.Count(c => !c.Passed);

    /// <summary>Cases that could not be scored at all.</summary>
    public int ErroredCount => Cases.Count(c => c.Errored);

    /// <summary>Total estimated spend, when a cost model was supplied.</summary>
    public decimal? EstimatedCost =>
        Cases.Any(c => c.EstimatedCost is not null)
            ? Cases.Sum(c => c.EstimatedCost ?? 0m)
            : null;

    /// <summary>Total tokens, when providers reported them.</summary>
    public long? TotalTokens =>
        Cases.Any(c => c.TotalTokens is not null)
            ? Cases.Sum(c => c.TotalTokens ?? 0)
            : null;

    [JsonIgnore]
    private IReadOnlyList<EvalCaseResult> Scored => field ??= [.. Cases.Where(c => !c.Errored)];

    /// <summary>Serializes for storage as a baseline.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, EvalJson.Options);

    /// <summary>Reads a stored baseline back.</summary>
    public static EvalReport FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<EvalReport>(json, EvalJson.Options)
            ?? throw new InvalidOperationException("The baseline deserialized to null.");
    }
}

/// <summary>Shared serializer settings, so a baseline written today reads back tomorrow.</summary>
internal static class EvalJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
