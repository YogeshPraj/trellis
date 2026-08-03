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

    /// <param name="client">The underlying chat client.</param>
    /// <param name="instructions">Optional system instructions prepended to every run.</param>
    /// <param name="tools">Optional tools the model may call (create with <see cref="AIFunctionFactory"/>).</param>
    /// <param name="autoInvokeTools">
    /// When true (default) and tools are provided, the client is wrapped with
    /// function invocation so tool calls are executed automatically in a loop.
    /// </param>
    public Agent(
        IChatClient client,
        string? instructions = null,
        IReadOnlyList<AITool>? tools = null,
        bool autoInvokeTools = true)
    {
        ArgumentNullException.ThrowIfNull(client);
        _instructions = instructions;

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
        return AgentRunner.RunAsync<TResult>(_client, _instructions, _chatOptions, messages, cancellationToken);
    }

    /// <summary>
    /// Runs one turn of an ongoing <see cref="Conversation"/>: appends the user prompt,
    /// sends the canonical history (tagged with the conversation's id for conversation-aware
    /// routers), and folds the response back into the conversation.
    /// </summary>
    public async Task<AgentRunResult<TResult>> RunAsync(
        Conversation conversation,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(prompt);

        conversation.Add(new ChatMessage(ChatRole.User, prompt));
        ChatOptions options = _chatOptions?.Clone() ?? new ChatOptions();
        options.ConversationId = conversation.Id;

        AgentRunResult<TResult> result = await AgentRunner
            .RunAsync<TResult>(_client, _instructions, options, conversation.Messages, cancellationToken)
            .ConfigureAwait(false);
        conversation.AddRange(result.Response.Messages);
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
        bool autoInvokeTools = true)
        : base(client, instructions, tools, autoInvokeTools)
    {
    }
}
