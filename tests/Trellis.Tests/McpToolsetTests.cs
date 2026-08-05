using Microsoft.Extensions.AI;
using Trellis.Mcp;

namespace Trellis.Tests;

/// <summary>
/// Exercises Trellis's own MCP logic — aggregation, naming, allow-listing, failure
/// isolation — against the <see cref="IMcpToolSource"/> contract. Speaking the protocol is
/// the SDK's job, so no live server is involved.
/// </summary>
public class McpToolsetTests
{
    private sealed class FakeSource(string name, params string[] toolNames) : IMcpToolSource
    {
        public int Calls { get; private set; }

        public Exception? FailWith { get; init; }

        public TimeSpan Delay { get; init; }

        public string Name { get; } = name;

        public async ValueTask<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }
            if (FailWith is not null)
            {
                throw FailWith;
            }
            return [.. toolNames.Select(t => AIFunctionFactory.Create(() => $"{Name}:{t}", name: t))];
        }
    }

    [Fact]
    public async Task PrefixesToolsWithTheirServerName_ByDefault()
    {
        var toolset = new McpToolset(
            new FakeSource("github", "create_issue", "list_repos"),
            new FakeSource("jira", "create_issue"));

        IReadOnlyList<AITool> tools = await toolset.GetToolsAsync();

        Assert.Equal(
            ["github_create_issue", "github_list_repos", "jira_create_issue"],
            tools.Select(t => t.Name));
    }

    [Fact]
    public async Task RenamedTool_StillInvokesTheOriginal()
    {
        var toolset = new McpToolset(new FakeSource("github", "create_issue"));

        AIFunction tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(await toolset.GetToolsAsync()));
        object? result = await tool.InvokeAsync();

        Assert.Equal("github_create_issue", tool.Name);
        Assert.Contains("github:create_issue", result?.ToString());
    }

    [Fact]
    public async Task PreserveNaming_KeepsServerNames()
    {
        var toolset = new McpToolset(
            [new FakeSource("github", "create_issue")],
            new McpToolsetOptions { Naming = McpToolNaming.Preserve });

        Assert.Equal("create_issue", Assert.Single(await toolset.GetToolsAsync()).Name);
    }

    [Fact]
    public async Task PreserveNaming_FailsFastOnCrossServerCollision()
    {
        var toolset = new McpToolset(
            [new FakeSource("github", "create_issue"), new FakeSource("jira", "create_issue")],
            new McpToolsetOptions { Naming = McpToolNaming.Preserve });

        var ex = await Assert.ThrowsAsync<McpToolConflictException>(() => toolset.GetToolsAsync());

        Assert.Equal("create_issue", ex.ToolName);
        Assert.Equal("github", ex.FirstServer);
        Assert.Equal("jira", ex.SecondServer);
    }

    [Fact]
    public async Task AllowList_FiltersOnTheServersOwnNames()
    {
        var toolset = new McpToolset(
            [new FakeSource("github", "create_issue", "delete_repo")],
            new McpToolsetOptions { AllowedTools = ["create_issue"] });

        Assert.Equal("github_create_issue", Assert.Single(await toolset.GetToolsAsync()).Name);
    }

    [Fact]
    public async Task UnavailableServer_IsSkipped_AndReported()
    {
        List<string> skipped = [];
        var toolset = new McpToolset(
            [
                new FakeSource("healthy", "ok"),
                new FakeSource("down") { FailWith = new HttpRequestException("connection refused") },
            ],
            new McpToolsetOptions { OnServerUnavailableCallback = (server, _) => skipped.Add(server) });

        IReadOnlyList<AITool> tools = await toolset.GetToolsAsync();

        Assert.Equal("healthy_ok", Assert.Single(tools).Name);
        Assert.Equal("down", Assert.Single(skipped));
    }

    [Fact]
    public async Task UnavailableServer_CanBeFatalWhenRequired()
    {
        var toolset = new McpToolset(
            [new FakeSource("critical") { FailWith = new HttpRequestException("down") }],
            new McpToolsetOptions { OnServerUnavailable = McpFailureBehavior.Throw });

        await Assert.ThrowsAsync<HttpRequestException>(() => toolset.GetToolsAsync());
    }

    [Fact]
    public async Task Cancellation_IsNotSwallowedAsAnUnavailableServer()
    {
        using var cts = new CancellationTokenSource();
        var toolset = new McpToolset(
            [new FakeSource("slow", "t") { Delay = TimeSpan.FromSeconds(30) }],
            new McpToolsetOptions { OnServerUnavailable = McpFailureBehavior.Skip });

        Task<IReadOnlyList<AITool>> pending = toolset.GetToolsAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task ServersAreQueriedConcurrently_ButResultsKeepRegistrationOrder()
    {
        var slow = new FakeSource("slow", "a") { Delay = TimeSpan.FromMilliseconds(300) };
        var fast = new FakeSource("fast", "b");
        var toolset = new McpToolset(slow, fast);

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        IReadOnlyList<AITool> tools = await toolset.GetToolsAsync();
        TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start);

        Assert.Equal(["slow_a", "fast_b"], tools.Select(t => t.Name));
        Assert.True(elapsed < TimeSpan.FromMilliseconds(900), $"servers were queried serially ({elapsed})");
    }

    [Fact]
    public void DuplicateServerNames_AreRejectedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() =>
            new McpToolset(new FakeSource("github", "a"), new FakeSource("github", "b")));
    }

    [Fact]
    public async Task EmptyToolset_IsHarmless()
    {
        Assert.Empty(await new McpToolset().GetToolsAsync());
    }

    [Fact]
    public async Task McpTools_DropStraightIntoAnAgent()
    {
        var toolset = new McpToolset(new FakeSource("weather", "get_forecast"));
        IReadOnlyList<AITool> tools = await toolset.GetToolsAsync();

        var client = new FakeChatClient("sunny");
        var agent = new Agent(client, instructions: "Use tools.", tools: tools);
        await agent.RunAsync("what's the weather?");

        AITool sent = Assert.Single(Assert.Single(client.Options)!.Tools!);
        Assert.Equal("weather_get_forecast", sent.Name);
    }
}
