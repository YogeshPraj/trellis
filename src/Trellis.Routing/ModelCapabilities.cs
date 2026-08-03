namespace Trellis.Routing;

/// <summary>Features a model deployment supports.</summary>
[Flags]
public enum ModelFeatures
{
    None = 0,

    /// <summary>The model can call tools / functions.</summary>
    FunctionCalling = 1,

    /// <summary>The model accepts image inputs.</summary>
    Vision = 2,

    /// <summary>The model supports a JSON / JSON-schema response format natively.</summary>
    JsonResponseFormat = 4,

    /// <summary>
    /// The provider keeps conversation context server-side (e.g. OpenAI's Responses API):
    /// follow-up requests can send only new messages plus a provider conversation id.
    /// The router exploits this per endpoint while keeping the client-side history canonical,
    /// so failing over to a stateless provider replays the full conversation seamlessly.
    /// </summary>
    ServerConversationState = 8,
}

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

/// <summary>Thrown when no registered endpoint supports what the request needs.</summary>
public sealed class NoCompatibleModelException(string message) : Exception(message);
