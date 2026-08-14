using Microsoft.Extensions.AI;
using System.Diagnostics.Metrics;
using System.Diagnostics;

namespace Trellis.Diagnostics;

/// <summary>Per-million-token prices for one model.</summary>
/// <param name="InputPerMillion">Price per million input (prompt) tokens.</param>
/// <param name="OutputPerMillion">Price per million output (completion) tokens.</param>
public readonly record struct ModelPrice(decimal InputPerMillion, decimal OutputPerMillion);
