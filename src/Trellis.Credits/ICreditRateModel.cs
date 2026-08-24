using Microsoft.Extensions.AI;

namespace Trellis.Credits;

/// <summary>
/// Turns model usage into credits (Strategy). Credits are an abstract unit — this layer never
/// knows what one is worth, which is what keeps money out of the system entirely.
/// </summary>
public interface ICreditRateModel
{
    /// <summary>
    /// Micro-credits for this usage, or null when the model is unpriced. Null means "unknown",
    /// never "free" — an admission policy should decide what to do about it rather than
    /// silently charging nothing.
    /// </summary>
    long? CreditsFor(string? modelId, UsageDetails usage);
}
