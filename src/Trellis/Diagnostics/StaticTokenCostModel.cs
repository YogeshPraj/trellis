using Microsoft.Extensions.AI;

namespace Trellis.Diagnostics;

/// <summary>
/// A fixed price list, matched on model id (case-insensitive). Immutable once built, so it is
/// safe to share across threads.
/// </summary>
public sealed class StaticTokenCostModel : ITokenCostModel
{
    private readonly Dictionary<string, ModelPrice> _prices;

    public StaticTokenCostModel(IReadOnlyDictionary<string, ModelPrice> prices)
    {
        ArgumentNullException.ThrowIfNull(prices);
        _prices = new(prices, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Prices a response, honouring cached-input discounts and long-context tiers.
    /// </summary>
    /// <remarks>
    /// <see cref="UsageDetails.CachedInputTokenCount"/> is treated as a subset of
    /// <see cref="UsageDetails.InputTokenCount"/>, matching how providers report it: the
    /// prompt total includes the cached portion. Counting them separately would bill the
    /// cached tokens twice.
    /// </remarks>
    public decimal? EstimateCost(string? modelId, UsageDetails usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (modelId is null || !_prices.TryGetValue(modelId, out ModelPrice? price))
        {
            return null;
        }

        long input = usage.InputTokenCount ?? 0;
        long output = usage.OutputTokenCount ?? 0;
        long cached = Math.Clamp(usage.CachedInputTokenCount ?? 0, 0, input);
        long uncached = input - cached;

        (decimal inputRate, decimal outputRate, decimal? cachedRate) = price.RateFor(input);

        return ((uncached * inputRate)
              + (cached * (cachedRate ?? inputRate))
              + (output * outputRate)) / 1_000_000m;
    }
}
