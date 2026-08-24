using Microsoft.Extensions.AI;
using Trellis.Diagnostics;

namespace Trellis.Credits;

/// <summary>
/// Prices usage in credits by scaling an <see cref="ITokenCostModel"/>, so one price table
/// drives both cost telemetry and credit accounting.
/// </summary>
/// <remarks>
/// The scale is what separates the two worlds: the price table is denominated in whatever
/// unit you keep it in, and <paramref name="microCreditsPerUnit"/> converts one of those
/// units into integer credits. Nothing here decides — or needs to know — whether that unit is
/// a dollar.
/// </remarks>
/// <param name="costs">The price table.</param>
/// <param name="microCreditsPerUnit">
/// Micro-credits per one unit of the price table (default one million, so a price of 1.0
/// becomes 1,000,000 micro-credits).
/// </param>
public sealed class TokenCostCreditRateModel(ITokenCostModel costs, long microCreditsPerUnit = 1_000_000)
    : ICreditRateModel
{
    private readonly ITokenCostModel _costs = costs ?? throw new ArgumentNullException(nameof(costs));

    private readonly long _scale = microCreditsPerUnit > 0
        ? microCreditsPerUnit
        : throw new ArgumentOutOfRangeException(nameof(microCreditsPerUnit), "Must be positive.");

    public long? CreditsFor(string? modelId, UsageDetails usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (_costs.EstimateCost(modelId, usage) is not decimal cost)
        {
            return null;
        }

        // Round away from zero so a fraction of a credit is charged rather than lost; over a
        // million small requests, truncation would silently give the spend away.
        return (long)Math.Round(cost * _scale, MidpointRounding.AwayFromZero);
    }
}
