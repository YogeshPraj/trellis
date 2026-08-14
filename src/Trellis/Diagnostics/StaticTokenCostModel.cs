using Microsoft.Extensions.AI;
using System.Diagnostics.Metrics;
using System.Diagnostics;

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

    public decimal? EstimateCost(string? modelId, UsageDetails usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (modelId is null || !_prices.TryGetValue(modelId, out ModelPrice price))
        {
            return null;
        }
        return ((usage.InputTokenCount ?? 0) * price.InputPerMillion
              + (usage.OutputTokenCount ?? 0) * price.OutputPerMillion) / 1_000_000m;
    }
}
