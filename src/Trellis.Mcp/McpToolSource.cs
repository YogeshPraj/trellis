using Microsoft.Extensions.AI;

namespace Trellis.Mcp;

/// <summary>
/// A named source of MCP tools (Strategy). Trellis's own logic — aggregation, name
/// collisions, allow-listing, failure isolation — depends on this interface, never on the
/// MCP SDK, so all of it is testable without a live server. Speaking the protocol is the
/// adapter's job (<see cref="McpServerToolSource"/>).
/// </summary>
public interface IMcpToolSource
{
    /// <summary>Identifies this server in tool names, errors, and logs.</summary>
    string Name { get; }

    /// <summary>Fetches the server's currently advertised tools.</summary>
    ValueTask<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken = default);
}

/// <summary>What to do when an MCP server cannot be reached while collecting tools.</summary>
public enum McpFailureBehavior
{
    /// <summary>
    /// Skip the unreachable server and continue with the rest (default). One broken
    /// integration degrades the agent instead of taking it down.
    /// </summary>
    Skip,

    /// <summary>Propagate the failure — for servers the agent is useless without.</summary>
    Throw,
}

/// <summary>How tool names from several servers are kept distinct.</summary>
public enum McpToolNaming
{
    /// <summary>
    /// Prefix every tool with its server name (<c>github_create_issue</c>). Predictable and
    /// collision-free, and it tells the model which system a tool belongs to.
    /// </summary>
    PrefixWithServerName,

    /// <summary>
    /// Keep the server's own names. Fails fast on a duplicate across servers rather than
    /// letting one server silently shadow another's tool.
    /// </summary>
    Preserve,
}

/// <summary>Options for aggregating tools across MCP servers.</summary>
public sealed class McpToolsetOptions
{
    /// <summary>How to name tools (default: prefix with the server name).</summary>
    public McpToolNaming Naming { get; init; } = McpToolNaming.PrefixWithServerName;

    /// <summary>What to do when a server is unreachable (default: skip it).</summary>
    public McpFailureBehavior OnServerUnavailable { get; init; } = McpFailureBehavior.Skip;

    /// <summary>
    /// Optional allow-list of tool names (as advertised by the server, before prefixing).
    /// Null admits everything.
    /// </summary>
    /// <remarks>
    /// An MCP server can add tools at any time, and whatever it advertises becomes callable
    /// by the model. Allow-list anything you don't control.
    /// </remarks>
    public IReadOnlyCollection<string>? AllowedTools { get; init; }

    /// <summary>Called when a server is skipped, so a degraded agent is loud rather than silent.</summary>
    public Action<string, Exception>? OnServerUnavailableCallback { get; init; }
}

/// <summary>
/// Aggregates the tools of one or more MCP servers into a tool set an agent can use.
/// </summary>
/// <remarks>
/// MCP tools arrive as <see cref="AIFunction"/>s, so they need no adapter to reach an
/// agent. What this adds is what a multi-server deployment actually needs: stable naming
/// across servers, an allow-list, and isolation so one dead server doesn't break the agent.
/// </remarks>
public sealed class McpToolset
{
    private readonly IReadOnlyList<IMcpToolSource> _sources;
    private readonly McpToolsetOptions _options;

    public McpToolset(IReadOnlyList<IMcpToolSource> sources, McpToolsetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _options = options ?? new McpToolsetOptions();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IMcpToolSource source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!names.Add(source.Name))
            {
                throw new ArgumentException($"Duplicate MCP server name '{source.Name}'.", nameof(sources));
            }
        }
        _sources = sources;
    }

    public McpToolset(params IMcpToolSource[] sources) : this((IReadOnlyList<IMcpToolSource>)sources)
    {
    }

    /// <summary>
    /// Collects tools from every server. Servers are queried concurrently — a slow one
    /// shouldn't serialize the rest — but results are assembled in registration order so the
    /// tool list handed to the model is deterministic.
    /// </summary>
    public async Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AITool>?[] perSource = await Task.WhenAll(
            _sources.Select(source => LoadAsync(source, cancellationToken))).ConfigureAwait(false);

        List<AITool> tools = [];
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < _sources.Count; i++)
        {
            if (perSource[i] is not IReadOnlyList<AITool> sourceTools)
            {
                continue;
            }
            string server = _sources[i].Name;

            foreach (AITool tool in sourceTools)
            {
                if (_options.AllowedTools is { } allowed && !allowed.Contains(tool.Name))
                {
                    continue;
                }

                AITool named = _options.Naming == McpToolNaming.PrefixWithServerName
                    ? Rename(tool, $"{server}_{tool.Name}")
                    : tool;

                if (!seen.TryAdd(named.Name, server))
                {
                    throw new McpToolConflictException(named.Name, seen[named.Name], server);
                }
                tools.Add(named);
            }
        }
        return tools;
    }

    private async Task<IReadOnlyList<AITool>?> LoadAsync(IMcpToolSource source, CancellationToken cancellationToken)
    {
        try
        {
            return await source.GetToolsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                   && _options.OnServerUnavailable == McpFailureBehavior.Skip)
        {
            _options.OnServerUnavailableCallback?.Invoke(source.Name, ex);
            return null;
        }
    }

    /// <summary>
    /// Renames a tool without reimplementing it: the MCP SDK's own rename when the tool came
    /// from a server, a pass-through wrapper otherwise. Non-function tools are left alone.
    /// </summary>
    private static AITool Rename(AITool tool, string name)
    {
        if (tool is not AIFunction function || string.Equals(function.Name, name, StringComparison.Ordinal))
        {
            return tool;
        }
        return tool is ModelContextProtocol.Client.McpClientTool mcpTool
            ? mcpTool.WithName(name)
            : new RenamedFunction(function, name);
    }

    /// <summary>Passes every call through to the original function under a different name.</summary>
    private sealed class RenamedFunction(AIFunction inner, string name) : DelegatingAIFunction(inner)
    {
        public override string Name { get; } = name;
    }
}

/// <summary>Two servers advertise the same tool name and naming is set to preserve them.</summary>
public sealed class McpToolConflictException(string toolName, string firstServer, string secondServer)
    : Exception($"Tool '{toolName}' is advertised by both '{firstServer}' and '{secondServer}'. " +
                $"Use {nameof(McpToolNaming)}.{nameof(McpToolNaming.PrefixWithServerName)} or an allow-list to disambiguate.")
{
    public string ToolName { get; } = toolName;

    public string FirstServer { get; } = firstServer;

    public string SecondServer { get; } = secondServer;
}
