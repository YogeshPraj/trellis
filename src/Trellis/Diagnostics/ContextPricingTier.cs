namespace Trellis.Diagnostics;

/// <summary>
/// A price that applies once a request's input exceeds <paramref name="AboveInputTokens"/>.
/// </summary>
/// <remarks>
/// Providers that charge more for long context (Gemini and Claude above their thresholds)
/// apply the higher price to the <b>whole request</b>, not just the tokens past the
/// threshold — it is a step, not a tax bracket. Modelling it as marginal would understate
/// long-context cost by roughly half.
/// </remarks>
/// <param name="AboveInputTokens">Input-token count past which this tier applies.</param>
/// <param name="InputPerMillion">Price per million uncached input tokens in this tier.</param>
/// <param name="OutputPerMillion">Price per million output tokens in this tier.</param>
/// <param name="CachedInputPerMillion">
/// Price per million cached input tokens; when null the tier's input price is used.
/// </param>
public sealed record ContextPricingTier(
    long AboveInputTokens,
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal? CachedInputPerMillion = null);
