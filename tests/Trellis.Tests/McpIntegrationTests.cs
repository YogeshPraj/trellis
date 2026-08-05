using Microsoft.Extensions.AI;
using Trellis.Mcp;

namespace Trellis.Tests;

/// <summary>
/// Validates the MCP SDK adapter against a real server — the reference
/// <c>@modelcontextprotocol/server-everything</c>, launched over stdio. Protocol conformance
/// belongs to the SDK, so this checks only the seam Trellis owns: that a live server's tools
/// arrive, get named, and are actually callable. Each test no-ops when npx is unavailable
/// (e.g. CI without Node), exactly like the Ollama tests.
/// </summary>
public class McpIntegrationTests
{
    private static readonly Lazy<bool> NpxAvailable = new(() =>
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sh",
                Arguments = OperatingSystem.IsWindows() ? "/c npx --version" : "-c \"npx --version\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            return process is not null && process.WaitForExit(30_000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    });

    private static McpServerToolSource NewEverythingServer(string name = "everything") =>
        McpServerToolSource.Stdio(
            name,
            command: OperatingSystem.IsWindows() ? "cmd.exe" : "npx",
            arguments: OperatingSystem.IsWindows()
                ? ["/c", "npx", "-y", "@modelcontextprotocol/server-everything"]
                : ["-y", "@modelcontextprotocol/server-everything"]);

    [Fact]
    public async Task RealServer_ListsToolsThroughTheAdapter()
    {
        if (!NpxAvailable.Value)
        {
            return; // Node not installed — validated locally, skipped here.
        }

        await using McpServerToolSource server = NewEverythingServer();

        IReadOnlyList<AITool> tools = await server.GetToolsAsync();

        Assert.NotEmpty(tools);
        // "echo" is the one tool the reference server has always advertised; asserting on
        // the whole list would just track upstream's demo catalogue.
        Assert.Contains(tools, t => t.Name == "echo");
        Assert.All(tools, t => Assert.IsAssignableFrom<AIFunction>(t));
    }

    [Fact]
    public async Task RealServer_ToolsArePrefixed_AndStillInvokeTheServer()
    {
        if (!NpxAvailable.Value)
        {
            return;
        }

        await using McpServerToolSource server = NewEverythingServer("demo");
        var toolset = new McpToolset(server);

        IReadOnlyList<AITool> tools = await toolset.GetToolsAsync();
        AIFunction sum = Assert.IsAssignableFrom<AIFunction>(Assert.Single(tools, t => t.Name == "demo_get-sum"));

        object? result = await sum.InvokeAsync(new AIFunctionArguments
        {
            ["a"] = 2,
            ["b"] = 3,
        });

        // Renaming must not break the call path back to the server.
        Assert.Contains("5", result?.ToString());
    }

    [Fact]
    public async Task RealServer_AllowListKeepsUnwantedToolsOut()
    {
        if (!NpxAvailable.Value)
        {
            return;
        }

        await using McpServerToolSource server = NewEverythingServer("demo");
        var toolset = new McpToolset(
            [server],
            new McpToolsetOptions { AllowedTools = ["echo"] });

        Assert.Equal("demo_echo", Assert.Single(await toolset.GetToolsAsync()).Name);
    }

    [Fact]
    public async Task RealServer_ToolListingIsCached()
    {
        if (!NpxAvailable.Value)
        {
            return;
        }

        await using McpServerToolSource server = NewEverythingServer();

        IReadOnlyList<AITool> first = await server.GetToolsAsync();
        IReadOnlyList<AITool> second = await server.GetToolsAsync();

        Assert.Same(first, second);
    }

    [Fact]
    public async Task UnreachableServer_DegradesTheToolset_InsteadOfBreakingIt()
    {
        // No npx needed: this command cannot start anywhere.
        await using var broken = McpServerToolSource.Stdio(
            "broken", command: "trellis-no-such-mcp-server-executable");
        List<string> skipped = [];
        var toolset = new McpToolset(
            [broken],
            new McpToolsetOptions { OnServerUnavailableCallback = (name, _) => skipped.Add(name) });

        IReadOnlyList<AITool> tools = await toolset.GetToolsAsync();

        Assert.Empty(tools);
        Assert.Equal("broken", Assert.Single(skipped));
    }
}
