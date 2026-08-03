using Microsoft.Extensions.AI;

namespace Trellis.Tests;

public class AgentTests
{
    private sealed record FlightResult(string Destination, decimal Price);

    [Fact]
    public async Task StringAgent_ReturnsResponseText()
    {
        var client = new FakeChatClient("Hello from Trellis!");
        var agent = new Agent(client);

        AgentRunResult<string> result = await agent.RunAsync("hi");

        Assert.Equal("Hello from Trellis!", result.Output);
    }

    [Fact]
    public async Task TypedAgent_DeserializesStructuredOutput()
    {
        var client = new FakeChatClient("""{ "destination": "Pune", "price": 129.50 }""");
        var agent = new Agent<FlightResult>(client, instructions: "You book flights.");

        AgentRunResult<FlightResult> result = await agent.RunAsync("book me a flight to Pune");

        Assert.Equal("Pune", result.Output.Destination);
        Assert.Equal(129.50m, result.Output.Price);
    }

    [Fact]
    public async Task Instructions_ArePrependedAsSystemMessage()
    {
        var client = new FakeChatClient("ok");
        var agent = new Agent(client, instructions: "Always answer in French.");

        await agent.RunAsync("hi");

        IReadOnlyList<ChatMessage> sent = Assert.Single(client.Requests);
        Assert.Equal(ChatRole.System, sent[0].Role);
        Assert.Equal("Always answer in French.", sent[0].Text);
        Assert.Equal(ChatRole.User, sent[1].Role);
    }

    [Fact]
    public async Task MultiTurn_History_IsPassedThrough()
    {
        var client = new FakeChatClient("ok");
        var agent = new Agent(client);

        await agent.RunAsync(
        [
            new ChatMessage(ChatRole.User, "first"),
            new ChatMessage(ChatRole.Assistant, "reply"),
            new ChatMessage(ChatRole.User, "second"),
        ]);

        IReadOnlyList<ChatMessage> sent = Assert.Single(client.Requests);
        Assert.Equal(3, sent.Count);
        Assert.Equal("second", sent[2].Text);
    }
}
