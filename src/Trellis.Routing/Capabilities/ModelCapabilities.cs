namespace Trellis.Routing.Capabilities;

/// <summary>What one deployment can do. Attach to a <see cref="ModelEndpoint"/>.</summary>
public sealed class ModelCapabilities
{
    /// <summary>Permissive default: tools, vision, and JSON supported; server-side state opt-in.</summary>
    public static ModelCapabilities Default { get; } = new();

    public ModelFeatures Features { get; init; } =
        ModelFeatures.FunctionCalling | ModelFeatures.Vision | ModelFeatures.JsonResponseFormat;

    /// <summary>
    /// Approximate input context window. Requests estimated to exceed it route elsewhere
    /// (estimate is chars/4 — coarse on purpose; leave null for no limit).
    /// </summary>
    public int? MaxInputTokens { get; init; }

    public bool Supports(ModelFeatures features) => (Features & features) == features;
}
