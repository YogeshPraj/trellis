namespace Trellis.Routing.Capabilities;

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
    /// <remarks>
    /// Opt-in contract: setting this flag asserts the endpoint's <c>IChatClient</c> behaves as
    /// follows — (1) a successful response carries a non-null <c>ChatResponse.ConversationId</c>
    /// naming server state that includes all input messages of that call plus the generated
    /// response messages; (2) a request sending that id plus only newer messages continues the
    /// same context; (3) ids are opaque and never required to repeat across turns (the router
    /// always stores the latest). Trellis validates its sync logic against this contract, not
    /// against any particular vendor — conformance is the adapter's responsibility.
    /// </remarks>
    ServerConversationState = 8,
}
