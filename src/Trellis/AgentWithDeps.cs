using Microsoft.Extensions.AI;

namespace Trellis;

/// <summary>
/// A typed agent with compile-time-checked dependency injection: tools are built per run
/// from a <typeparamref name="TDeps"/> instance, so every tool can close over your services
/// (database, HTTP clients, the current user, ...) without service-locator magic.
/// </summary>
/// <typeparam name="TDeps">The dependencies handed to the tool factory on each run.</typeparam>
/// <typeparam name="TResult">The result type; non-string types use structured JSON output.</typeparam>
public class Agent<TDeps, TResult>
{
    private readonly IChatClient _client;
    private readonly string? _instructions;
    private readonly Func<TDeps, IReadOnlyList<AITool>> _toolFactory;

    /// <param name="client">The underlying chat client.</param>
    /// <param name="tools">Builds the tool set for a run from its dependencies.</param>
    /// <param name="instructions">Optional system instructions prepended to every run.</param>
    /// <param name="autoInvokeTools">
    /// When true (default), tool calls are executed automatically in a loop.
    /// </param>
    public Agent(
        IChatClient client,
        Func<TDeps, IReadOnlyList<AITool>> tools,
        string? instructions = null,
        bool autoInvokeTools = true)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(tools);
        _client = autoInvokeTools ? client.AsBuilder().UseFunctionInvocation().Build() : client;
        _toolFactory = tools;
        _instructions = instructions;
    }

    /// <summary>Runs the agent with a single user prompt and this run's dependencies.</summary>
    public Task<AgentRunResult<TResult>> RunAsync(
        TDeps deps,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return RunAsync(deps, [new ChatMessage(ChatRole.User, prompt)], cancellationToken);
    }

    /// <summary>Runs the agent with a full message history and this run's dependencies.</summary>
    public Task<AgentRunResult<TResult>> RunAsync(
        TDeps deps,
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var options = new ChatOptions { Tools = [.. _toolFactory(deps)] };
        return AgentRunner.RunAsync<TResult>(_client, _instructions, options, messages, cancellationToken);
    }
}
