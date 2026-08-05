using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Trellis.Mcp;

/// <summary>
/// The real <see cref="IMcpToolSource"/>: connects to an MCP server through the official
/// SDK and exposes its tools. Connection is lazy and shared, so a toolset can be built at
/// startup without blocking on servers that may be slow or down.
/// </summary>
/// <remarks>
/// <para>
/// This adapter is deliberately thin — protocol conformance belongs to the MCP SDK, exactly
/// as provider wire formats belong to an <c>IChatClient</c> adapter. Trellis's own logic
/// lives behind <see cref="IMcpToolSource"/> and is tested there.
/// </para>
/// <para>
/// ⚠ An MCP server is remote code you are handing your model. Its tool descriptions become
/// part of the prompt and its tools become callable — treat a third-party server as
/// untrusted input and use <see cref="McpToolsetOptions.AllowedTools"/>.
/// </para>
/// </remarks>
public sealed class McpServerToolSource : IMcpToolSource, IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<McpClient>> _connect;
    private readonly TimeSpan _toolCacheDuration;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private McpClient? _client;
    private IReadOnlyList<AITool>? _cachedTools;
    private DateTimeOffset _cachedAt;
    private bool _disposed;

    /// <param name="name">Server name; also the tool-name prefix.</param>
    /// <param name="transport">How to reach the server (stdio, HTTP, ...).</param>
    /// <param name="toolCacheDuration">
    /// How long a tool listing is reused before re-querying (default 5 minutes). Servers may
    /// add or remove tools at runtime; caching keeps every agent run from paying a round
    /// trip. Use <see cref="TimeSpan.Zero"/> to always re-list.
    /// </param>
    /// <param name="clientOptions">Optional MCP client options (client info, timeouts).</param>
    /// <param name="loggerFactory">Optional logger factory passed to the MCP client.</param>
    public McpServerToolSource(
        string name,
        IClientTransport transport,
        TimeSpan? toolCacheDuration = null,
        McpClientOptions? clientOptions = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(transport);
        Name = name;
        _toolCacheDuration = toolCacheDuration ?? TimeSpan.FromMinutes(5);
        _connect = ct => McpClient.CreateAsync(transport, clientOptions, loggerFactory, ct);
    }

    /// <summary>Wraps a client you already own; disposing this source will not dispose it.</summary>
    public McpServerToolSource(string name, McpClient client, TimeSpan? toolCacheDuration = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(client);
        Name = name;
        _toolCacheDuration = toolCacheDuration ?? TimeSpan.FromMinutes(5);
        _client = client;
        OwnsClient = false;
        _connect = _ => Task.FromResult(client);
    }

    /// <summary>A stdio server: launches <paramref name="command"/> and speaks MCP over its pipes.</summary>
    public static McpServerToolSource Stdio(
        string name,
        string command,
        IReadOnlyList<string>? arguments = null,
        IDictionary<string, string?>? environmentVariables = null,
        TimeSpan? toolCacheDuration = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = name,
                Command = command,
                Arguments = arguments is null ? null : [.. arguments],
                EnvironmentVariables = environmentVariables,
            },
            loggerFactory);
        return new McpServerToolSource(name, transport, toolCacheDuration, loggerFactory: loggerFactory);
    }

    /// <summary>An HTTP (streamable / SSE) server.</summary>
    public static McpServerToolSource Http(
        string name,
        Uri endpoint,
        IDictionary<string, string>? additionalHeaders = null,
        TimeSpan? toolCacheDuration = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = name,
                Endpoint = endpoint,
                AdditionalHeaders = additionalHeaders,
            },
            loggerFactory);
        return new McpServerToolSource(name, transport, toolCacheDuration, loggerFactory: loggerFactory);
    }

    public string Name { get; }

    /// <summary>Whether this source owns (and will dispose) the underlying MCP client.</summary>
    public bool OwnsClient { get; } = true;

    public async ValueTask<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_cachedTools is { } cached && DateTimeOffset.UtcNow - _cachedAt < _toolCacheDuration)
        {
            return cached;
        }

        // One connection per source, even under concurrent first calls: connecting twice
        // would launch a second stdio process or a second HTTP session.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedTools is { } current && DateTimeOffset.UtcNow - _cachedAt < _toolCacheDuration)
            {
                return current;
            }

            _client ??= await _connect(cancellationToken).ConfigureAwait(false);
            IList<McpClientTool> tools = await _client.ListToolsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            _cachedTools = [.. tools];
            _cachedAt = DateTimeOffset.UtcNow;
            return _cachedTools;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (OwnsClient && _client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
        _gate.Dispose();
    }
}
