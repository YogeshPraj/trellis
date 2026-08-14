using Microsoft.Extensions.AI;

namespace Trellis.Outputs;

/// <summary>One failed attempt in a self-healing run: what came back and why it was rejected.</summary>
/// <param name="Attempt">The 1-based attempt number.</param>
/// <param name="Errors">The deserialization or validation errors for this attempt.</param>
/// <param name="ResponseText">The raw text the model produced.</param>
public sealed record OutputFailure(int Attempt, IReadOnlyList<string> Errors, string ResponseText);
