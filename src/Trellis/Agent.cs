using Microsoft.Extensions.AI;

namespace Trellis;

/// <summary>
/// A typed agent: give it a prompt, get back a strongly-typed <typeparamref name="TResult"/>.
/// Built on <see cref="IChatClient"/>, so it works with any Microsoft.Extensions.AI provider
/// (OpenAI, Anthropic, Azure, Ollama, ...).
/// </summary>
/// <typeparam name="TResult">
/// The result type. Use <see cref="string"/> (or the non-generic <see cref="Agent"/>) for plain text;
/// any other type is requested from the model as structured JSON output and deserialized.
/// </typeparam>
public class Agent<TResult>
{
    private readonly IChatClient _client;
    private readonly string? _instructions;
    private readonly ChatOptions? _chatOptions;
    private readonly ConversationCompactor? _compactor;
    private readonly IOutputValidator<TResult>? _outputValidator;
    private readonly OutputRetryOptions? _outputRetry;

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
    public Agent(
        IChatClient client,
        string? instructions = null,
        IReadOnlyList<AITool>? tools = null,
        bool autoInvokeTools = true,
        ConversationCompactor? compactor = null,
        IOutputValidator<TResult>? outputValidator = null,
        OutputRetryOptions? outputRetry = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _instructions = instructions;
        _compactor = compactor;
        _outputValidator = outputValidator;
        _outputRetry = outputRetry;

        if (tools is { Count: > 0 })
        {
            _chatOptions = new ChatOptions { Tools = [.. tools] };
            _client = autoInvokeTools
                ? client.AsBuilder().UseFunctionInvocation().Build()
                : client;
        }
        else
        {
            _client = client;
        }
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
            _client, _instructions, _chatOptions, messages, _outputValidator, _outputRetry, cancellationToken);
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
            .RunAsync(_client, _instructions, options, payload, _outputValidator, _outputRetry, cancellationToken)
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
        OutputRetryOptions? outputRetry = null)
        : base(client, instructions, tools, autoInvokeTools, compactor, outputValidator, outputRetry)
    {
    }
}
