using Microsoft.Extensions.AI;
using System.Diagnostics.Metrics;
using System.Diagnostics;

namespace Trellis.Diagnostics;

/// <summary>
/// Prices a run (Strategy). Token prices change often and vary per deployment, so Trellis
/// ships no built-in price list — you register the numbers your contract actually says.
/// </summary>
public interface ITokenCostModel
{
    /// <summary>
    /// Estimated cost of one response in your currency, or null when the model is unknown —
    /// null means "not priced", never "free", so dashboards can tell the difference.
    /// </summary>
    decimal? EstimateCost(string? modelId, UsageDetails usage);
}
