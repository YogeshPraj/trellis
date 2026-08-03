using Microsoft.Extensions.AI;

namespace Trellis.Tests;

public class AgentWithDepsTests
{
    private sealed record UserContext(string UserId);

    [Fact]
    public async Task ToolFactory_ReceivesRunDeps()
    {
        var client = new FakeChatClient("ok");
        UserContext? seen = null;

        var agent = new Agent<UserContext, string>(
            client,
            tools: deps =>
            {
                seen = deps;
                return [AIFunctionFactory.Create(() => $"orders for {deps.UserId}", name: "list_orders")];
            });

        await agent.RunAsync(new UserContext("u-42"), "show my orders");

        Assert.Equal("u-42", seen?.UserId);
    }

    [Fact]
    public async Task Tools_ArePassedToClient_PerRun()
    {
        var client = new FakeChatClient("ok");
        var agent = new Agent<UserContext, string>(
            client,
            tools: deps => [AIFunctionFactory.Create(() => deps.UserId, name: $"tool_{deps.UserId}")],
            autoInvokeTools: false);

        await agent.RunAsync(new UserContext("a"), "hi");
        await agent.RunAsync(new UserContext("b"), "hi");

        Assert.Equal(2, client.Options.Count);
        Assert.Equal("tool_a", Assert.Single(client.Options[0]!.Tools!).Name);
        Assert.Equal("tool_b", Assert.Single(client.Options[1]!.Tools!).Name);
    }

    [Fact]
    public async Task Tool_CanBeInvoked_AgainstDeps()
    {
        var client = new FakeChatClient("ok");
        var agent = new Agent<UserContext, string>(
            client,
            tools: deps => [AIFunctionFactory.Create(() => $"orders for {deps.UserId}", name: "list_orders")],
            autoInvokeTools: false);

        await agent.RunAsync(new UserContext("u-7"), "hi");

        var tool = (AIFunction)client.Options[0]!.Tools![0];
        object? result = await tool.InvokeAsync(new AIFunctionArguments());
        Assert.Contains("u-7", result?.ToString());
    }
}
