using Microsoft.Extensions.AI;
using Trellis.Agents.Middleware;
using Trellis.Agents.Teams;
using Trellis.Conversations.Compaction;
using Trellis.Conversations;
using Trellis.Diagnostics;
using Trellis.Outputs;
using Trellis.Tools;

namespace Trellis.Agents;

/// <summary>
/// A typed agent: give it a prompt, get back a strongly-typed <typeparamref name="TResult"/>.
/// Built on <see cref="IChatClient"/>, so it works with any Microsoft.Extensions.AI provider
/// (OpenAI, Anthropic, Azure, Ollama, ...).
/// </summary>
/// <typeparam name="TResult">
/// The result type. Use <see cref="string"/> (or the non-generic <see cref="Agent"/>) for plain text;
/// any other type is requested from the model as structured JSON output and deserialized.
/// </typeparam>
public class Agent<TResult> : IAgent<TResult>
{
    private readonly IChatClient _client;
    private readonly string? _instructions;
    private readonly ChatOptions? _chatOptions;
    private readonly ConversationCompactor? _compactor;
    private readonly IOutputValidator<TResult>? _outputValidator;
    private readonly OutputRetryOptions? _outputRetry;
    private readonly IReadOnlyList<IAgentMiddleware<TResult>>? _middleware;
    private readonly IToolAuthorizer? _toolAuthorizer;

    /// <param name="client">The underlying chat client.</param>
    /// <param name="instructions">Optional system instructions prepended to every run.</param>
    /// <param name="tools">Optional tools the model may call (create with <see cref="AIFunctionFactory"/>).</param>
    /// <param name="autoInvokeTools">
    /// When true (default) and tools are provided, the client is wrapped with
    /// function invocation so tool calls are executed automatically in a loop.
    /// </param>
    /// <param name="compactor">
    /// Optional hot/cold context management for conversation runs: when the hot history
    /// grows past its budget, old turns are summarized and archived automatically.
    /// </param>
    /// <param name="outputValidator">
    /// Optional semantic validation beyond deserialization; rejected outputs are fed back
    /// to the model for correction (see <see cref="OutputRetryOptions"/>).
    /// </param>
    /// <param name="outputRetry">
    /// Self-healing configuration. When null, typed outputs still self-heal with the
    /// defaults (2 correction retries); use <c>MaxRetries = 0</c> to fail fast.
    /// </param>
    /// <param name="toolAuthorizer">
    /// Gates every tool call before it runs. Null (the default) means no gate: whatever the
    /// model asks for, runs. Supply one whenever tools can do anything you would not let an
    /// untrusted caller do, since a model can be steered by any text it reads.
    /// </param>
    /// <param name="middleware">
    /// Wraps every buffered run, first entry outermost. ⚠ An agent with middleware refuses to
    /// stream — see <see cref="IAgentMiddleware{TResult}"/>.
    /// </param>
    public Agent(
        IChatClient client,
        string? instructions = null,
        IReadOnlyList<AITool>? tools = null,
        bool autoInvokeTools = true,
        ConversationCompactor? compactor = null,
        IOutputValidator<TResult>? outputValidator = null,
        OutputRetryOptions? outputRetry = null,
        IToolAuthorizer? toolAuthorizer = null,
        IReadOnlyList<IAgentMiddleware<TResult>>? middleware = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _instructions = instructions;
        _middleware = middleware is { Count: > 0 } ? [.. middleware] : null;
        _compactor = compactor;
        _outputValidator = outputValidator;
        _outputRetry = outputRetry;

        _toolAuthorizer = toolAuthorizer;

        // Wrapped whenever auto-invocation is on, even with no tools at construction: a team
        // supplies handoff tools per run, and a client that was never wrapped would list them
        // to the model and then silently never invoke the one it picked.
        _client = autoInvokeTools ? client.AsBuilder().UseFunctionInvocation().Build() : client;

        if (tools is { Count: > 0 })
        {
            _chatOptions = new ChatOptions
            {
                Tools = [.. toolAuthorizer is null ? tools : tools.WithAuthorization(toolAuthorizer)],
            };
        }
    }

    /// <summary>
    /// How this agent is addressed inside a team. Defaults to "agent"; set it whenever the
    /// agent joins one, since handoff tools are named from it.
    /// </summary>
    public string Name { get; init; } = "agent";

    /// <summary>What this agent is for. Read by other agents deciding whether to hand to it.</summary>
    public string Description { get; init; } = "A general-purpose agent.";

    /// <summary>
    /// Takes one turn on behalf of a team: runs normally, but reports a transfer instead of an
    /// answer when the model called a handoff tool.
    /// </summary>
    public async Task<AgentTurn<TResult>> TakeTurnAsync(
        IEnumerable<ChatMessage> messages,
        IReadOnlyList<AITool>? additionalTools = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // Opened before the run so the holder flows down into the tool; a value the tool
        // assigned to an async-local of its own would never be visible back here.
        HandoffSignal.Begin();
        try
        {
            ChatOptions? options = MergeTools(additionalTools);
            AgentRunResult<TResult> result = await AgentRunner
                .RunAsync(
                    _client, _instructions, options, messages, _outputValidator, _outputRetry,
                    cancellationToken, _middleware)
                .ConfigureAwait(false);
            return AgentTurn<TResult>.Answered(result);
        }
        catch (HandoffRequestedException handoff)
        {
            return AgentTurn<TResult>.HandedOff(handoff.Target, handoff.Reason);
        }
        finally
        {
            HandoffSignal.End();
        }
    }

    /// <summary>
    /// Combines this agent's own tools with any supplied for a single turn, gating the
    /// newcomers with the same authorizer. A per-run tool that skipped the gate would be a way
    /// around it.
    /// </summary>
    private ChatOptions? MergeTools(IReadOnlyList<AITool>? additionalTools)
    {
        if (additionalTools is null or { Count: 0 })
        {
            return _chatOptions;
        }

        IReadOnlyList<AITool> extra = _toolAuthorizer is null
            ? additionalTools
            : additionalTools.WithAuthorization(_toolAuthorizer);

        ChatOptions merged = _chatOptions?.Clone() ?? new ChatOptions();
        merged.Tools = [.. merged.Tools ?? [], .. extra];
        return merged;
    }

    /// <summary>Runs the agent with a single user prompt.</summary>
    public Task<AgentRunResult<TResult>> RunAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return RunAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken);
    }

    /// <summary>Runs the agent with a full message history (multi-turn).</summary>
    public Task<AgentRunResult<TResult>> RunAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return AgentRunner.RunAsync(
            _client, _instructions, _chatOptions, messages, _outputValidator, _outputRetry,
            cancellationToken, _middleware);
    }

    /// <summary>
    /// Runs the agent with a single user prompt, streaming updates as they arrive.
    /// Enumerate the returned <see cref="AgentStream{TResult}"/> for token-by-token output,
    /// then read its <see cref="AgentStream{TResult}.Result"/> for the typed value.
    /// </summary>
    public AgentStream<TResult> RunStreamingAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return RunStreamingAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken);
    }

    /// <summary>Runs the agent with a full message history, streaming updates as they arrive.</summary>
    public AgentStream<TResult> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ThrowIfMiddlewareCannotStream();
        return AgentRunner.Stream(
            _client,
            _ => new((
                AgentRunner.BuildPayload(_instructions, messages),
                AgentRunner.WithStructuredOutputFormat<TResult>(_chatOptions))),
            _outputValidator);
    }

    /// <summary>
    /// Streams one turn of an ongoing <see cref="Conversation"/>. The conversation is
    /// mutated lazily: the user prompt is appended when enumeration starts and the
    /// assembled response is folded in when it completes, so a stream that is never
    /// enumerated — or is abandoned mid-flight — leaves no half-turn behind.
    /// </summary>
    public AgentStream<TResult> RunStreamingAsync(
        Conversation conversation,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(prompt);
        ThrowIfMiddlewareCannotStream();

        return AgentRunner.Stream(
            _client,
            async ct =>
            {
                if (conversation.PendingCompaction is Task pending)
                {
                    await pending.ConfigureAwait(false);
                    conversation.PendingCompaction = null;
                }
                conversation.Add(new ChatMessage(ChatRole.User, prompt));

                ChatOptions options = AgentRunner.WithStructuredOutputFormat<TResult>(_chatOptions)
                    ?? new ChatOptions();
                options.ConversationId = conversation.RoutingId;
                return (AgentRunner.BuildPayload(_instructions, BuildConversationPayload(conversation)), options);
            },
            _outputValidator,
            result =>
            {
                conversation.AddRange(result.Response.Messages);
                conversation.RecordUsage(result.Response.Usage);
                if (_compactor is not null)
                {
                    conversation.PendingCompaction =
                        _compactor.CompactIfNeededAsync(conversation, CancellationToken.None);
                }
                return ValueTask.CompletedTask;
            });
    }

    /// <summary>
    /// Refuses to stream when middleware is configured. A streaming run has no result to hand
    /// middleware until the last token, and tokens already emitted cannot be withdrawn — so the
    /// pipeline could not do its job. Skipping it quietly would mean a guardrail that protects
    /// one code path and not the other, which is worse than not offering the path.
    /// </summary>
    private void ThrowIfMiddlewareCannotStream()
    {
        if (_middleware is not null)
        {
            throw new NotSupportedException(
                "This agent has middleware, which runs on buffered runs only, so streaming would " +
                "silently bypass it. Use RunAsync, or build a second agent without middleware for " +
                "the streaming path.");
        }
    }

    /// <summary>The rolling summary (when the conversation has been compacted) plus the hot history.</summary>
    private static List<ChatMessage> BuildConversationPayload(Conversation conversation)
    {
        List<ChatMessage> payload = [];
        if (conversation.Summary is string summary)
        {
            payload.Add(new ChatMessage(
                ChatRole.System,
                $"Summary of the earlier conversation (older turns were archived): {summary}"));
        }
        payload.AddRange(conversation.Messages);
        return payload;
    }

    /// <summary>
    /// Runs one turn of an ongoing <see cref="Conversation"/>: appends the user prompt,
    /// sends the hot history plus the rolling summary of any compacted cold context
    /// (tagged with the conversation's routing id for conversation-aware routers), and
    /// folds the response back into the conversation.
    /// </summary>
    public async Task<AgentRunResult<TResult>> RunAsync(
        Conversation conversation,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(prompt);

        // Compaction runs in the background after a turn completes; catch up on it here so
        // the summarizer's latency lands between turns, not on the user's response path.
        if (conversation.PendingCompaction is Task pending)
        {
            await pending.ConfigureAwait(false);
            conversation.PendingCompaction = null;
        }
        conversation.Add(new ChatMessage(ChatRole.User, prompt));

        List<ChatMessage> payload = BuildConversationPayload(conversation);

        ChatOptions options = _chatOptions?.Clone() ?? new ChatOptions();
        options.ConversationId = conversation.RoutingId;

        AgentRunResult<TResult> result = await AgentRunner
            .RunAsync(
                _client, _instructions, options, payload, _outputValidator, _outputRetry,
                cancellationToken, _middleware)
            .ConfigureAwait(false);
        conversation.AddRange(result.Response.Messages);
        conversation.RecordUsage(result.Response.Usage);

        // Kick off compaction for the NEXT turn without blocking this one. CompactIfNeededAsync
        // never throws (failures invoke OnCompactionFailure), so the pending task is safe to await.
        if (_compactor is not null)
        {
            conversation.PendingCompaction = _compactor.CompactIfNeededAsync(conversation, CancellationToken.None);
        }
        return result;
    }
}

/// <summary>A plain-text agent — shorthand for <c>Agent&lt;string&gt;</c>.</summary>
public sealed class Agent : Agent<string>
{
    public Agent(
        IChatClient client,
        string? instructions = null,
        IReadOnlyList<AITool>? tools = null,
        bool autoInvokeTools = true,
        ConversationCompactor? compactor = null,
        IOutputValidator<string>? outputValidator = null,
        OutputRetryOptions? outputRetry = null,
        IToolAuthorizer? toolAuthorizer = null,
        IReadOnlyList<IAgentMiddleware<string>>? middleware = null)
        : base(
            client, instructions, tools, autoInvokeTools, compactor, outputValidator, outputRetry,
            toolAuthorizer, middleware)
    {
    }
}
