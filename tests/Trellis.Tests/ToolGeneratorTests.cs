using Microsoft.Extensions.AI;

namespace Trellis.Tests;

public partial class MathTools
{
    [Tool(Description = "Adds two numbers")]
    public static int Add(int a, int b) => a + b;

    [Tool(Name = "current_time")]
    public string GetCurrentTime() => "high noon";
}

public partial class StaticOnlyTools
{
    [Tool]
    public static string Ping() => "pong";
}

public class ToolGeneratorTests
{
    [Fact]
    public void CreateTools_IsGenerated_WithSnakeCaseAndExplicitNames()
    {
        IReadOnlyList<AITool> tools = new MathTools().CreateTools();

        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.Name == "add");
        Assert.Contains(tools, t => t.Name == "current_time");
    }

    [Fact]
    public void Description_FlowsIntoAIFunction()
    {
        var add = (AIFunction)new MathTools().CreateTools().Single(t => t.Name == "add");

        Assert.Equal("Adds two numbers", add.Description);
    }

    [Fact]
    public async Task GeneratedTool_IsInvokable()
    {
        var add = (AIFunction)new MathTools().CreateTools().Single(t => t.Name == "add");

        object? result = await add.InvokeAsync(new AIFunctionArguments { ["a"] = 20, ["b"] = 22 });

        Assert.Equal("42", result?.ToString());
    }

    [Fact]
    public void AllStaticMethods_ProduceStaticCreateTools()
    {
        IReadOnlyList<AITool> tools = StaticOnlyTools.CreateTools();

        Assert.Equal("ping", Assert.Single(tools).Name);
    }

    [Fact]
    public async Task GeneratedTools_PlugIntoAnAgent()
    {
        var client = new FakeChatClient("ok");
        var agent = new Agent(client, tools: new MathTools().CreateTools(), autoInvokeTools: false);

        await agent.RunAsync("what is 20 + 22?");

        Assert.Equal(2, client.Options[0]!.Tools!.Count);
    }
}
