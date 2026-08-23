namespace Trellis.Diagnostics;

/// <summary>Per-million-token prices for one model.</summary>
/// <param name="InputPerMillion">Price per million uncached input (prompt) tokens.</param>
/// <param name="OutputPerMillion">Price per million output (completion) tokens.</param>
public sealed record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion)
{
    /// <summary>
    /// Price per million cached input tokens. Null means cached tokens are billed at the full
    /// input price — an over-estimate rather than an under-estimate, which is the safer
    /// direction when a budget depends on the answer.
    /// </summary>
    /// <remarks>
    /// Prompt caching typically discounts input by 5–10×, so leaving this unset on a
    /// cache-heavy workload overstates spend substantially. Set it to match your provider.
    /// </remarks>
    public decimal? CachedInputPerMillion { get; init; }

    /// <summary>
    /// Prices that take over past a context threshold, in any order. Empty means one flat
    /// price at every context length.
    /// </summary>
    public IReadOnlyList<ContextPricingTier> ContextTiers { get; init; } = [];

    /// <summary>
    /// The prices that apply to a request of this input size: the highest tier whose
    /// threshold is exceeded, or the base price when none is.
    /// </summary>
    internal (decimal Input, decimal Output, decimal? Cached) RateFor(long inputTokens)
    {
        decimal input = InputPerMillion;
        decimal output = OutputPerMillion;
        decimal? cached = CachedInputPerMillion;
        long applied = -1;

        foreach (ContextPricingTier tier in ContextTiers)
        {
            if (inputTokens > tier.AboveInputTokens && tier.AboveInputTokens > applied)
            {
                applied = tier.AboveInputTokens;
                input = tier.InputPerMillion;
                output = tier.OutputPerMillion;
                cached = tier.CachedInputPerMillion;
            }
        }
        return (input, output, cached);
    }
}
