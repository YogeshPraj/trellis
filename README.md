# Trellis

**Typed agents on a structured graph, for .NET.**

Trellis lets you build AI agents and multi-step agent workflows in idiomatic C#. Define a `record`, and your agent returns it — strongly typed, validated, deserialized. Compose agents and logic into a graph of nodes with conditional routing, stream every step as it executes, and checkpoint progress so workflows survive crashes and resume where they left off.

No new abstraction layer to learn: Trellis sits directly on [`Microsoft.Extensions.AI`](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai), so it works with any provider that ships an `IChatClient` — OpenAI, Anthropic, Azure OpenAI, Ollama, and more.

> ⚠️ Early release (v0.2). The API is small on purpose and will evolve.

## Features

- 🎯 **Strongly-typed agent outputs** — ask for an `Agent<FlightResult>` and get a `FlightResult` back, not a string to parse. Structured JSON output and deserialization are handled for you.
- 🔧 **Tools are plain C# methods** — register any delegate as a tool; tool calls are executed automatically in a loop until the model produces its final answer.
- ⚡ **`[Tool]` source generation** — mark methods with `[Tool]` and a Roslyn source generator emits `CreateTools()` at compile time. No assembly scanning, no reflection-based discovery.
- 💉 **Dependency-injected agents** — `Agent<TDeps, TResult>` builds its tool set per run from a typed dependencies object, so tools can use your services (database, current user, HTTP clients) with full compile-time checking.
- 🤝 **Agents as graph nodes** — `AddAgentNode(...)` drops any agent into a workflow: build the prompt from state, fold the typed result back in.
- 🔀 **Parallel fan-out/fan-in** — `AddParallelNode(...)` runs branches concurrently against the same state and merges the results.
- ✋ **Human-in-the-loop interrupts** — pause a run in front of any node (`InterruptBefore`), let a human review or edit the state, then resume from the same thread id.
- 🗄️ **Durable SQLite checkpointing** — `Trellis.Checkpointing.Sqlite` persists progress to a database file; workflows survive crashes and process restarts.
- 🕸️ **Graph workflow engine** — model multi-step processes as a state machine: nodes transform your state object, fixed or conditional edges decide what runs next, and the graph shape is validated at compile time.
- 📡 **Streaming execution** — observe every step live via `IAsyncEnumerable`: node started, node completed, graph completed — perfect for progress UIs and logging.
- 💾 **Checkpointing & resume** — pluggable `ICheckpointer<TState>` records progress after every node; rerun with the same `ThreadId` and the workflow picks up exactly where it stopped.
- 🌀 **Loop protection** — a `MaxSteps` guard stops runaway cycles before they burn tokens.
- 🔌 **Provider-agnostic** — anything with an `IChatClient` works; swap OpenAI for Ollama with one line.
- 🗣️ **Multi-turn conversations** — pass full message histories, with system instructions prepended automatically.
- 🧪 **Testable by design** — the whole test suite runs against a fake `IChatClient`; no API keys, no network.
- 🧩 **Zero-AI graph core** — `Trellis.Graph` has no AI dependency at all; use it to orchestrate any workflow, agentic or not.

## Typed agents in 5 lines

```csharp
using Microsoft.Extensions.AI;
using Trellis;

record FlightResult(string Destination, decimal Price);

IChatClient client = /* any Microsoft.Extensions.AI provider */;

var agent = new Agent<FlightResult>(client, instructions: "You book flights.");
FlightResult flight = (await agent.RunAsync("book me a flight to Pune")).Output;
```

Tools are plain C# methods:

```csharp
var agent = new Agent<FlightResult>(client,
    instructions: "You book flights.",
    tools: [AIFunctionFactory.Create(
        (string city) => $"Cheapest flight to {city}: $129.50",
        name: "search_flights")]);
```

Or let the source generator discover them — mark methods with `[Tool]` on a `partial` class and a `CreateTools()` method is generated at compile time:

```csharp
public partial class FlightTools
{
    [Tool(Description = "Searches for the cheapest flight to a city")]
    public string SearchFlights(string city) => /* ... */;   // exposed as "search_flights"
}

var agent = new Agent<FlightResult>(client, tools: new FlightTools().CreateTools());
```

Need your services inside tools? `Agent<TDeps, TResult>` builds the tool set per run from a typed dependencies object:

```csharp
var agent = new Agent<OrderServices, OrderSummary>(client,
    tools: deps => [AIFunctionFactory.Create(
        () => deps.Db.GetOrders(deps.CurrentUserId), name: "list_orders")]);

OrderSummary summary = (await agent.RunAsync(services, "summarize my orders")).Output;
```

## Graph workflows

Nodes transform a state object; edges decide what runs next; every step can be checkpointed.

```csharp
using Trellis.Graph;

record ResearchState(string Question, string? Draft, int Revisions);

var graph = new StateGraph<ResearchState>()
    .AddNode("draft",  async s => s with { Draft = await WriteDraftAsync(s.Question) })
    .AddNode("review", async s => s with { Draft = await ReviewAsync(s.Draft!), Revisions = s.Revisions + 1 })
    .AddEdge("draft", "review")
    .AddConditionalEdge("review", s => s.Revisions < 2 ? "review" : StateGraph.End)
    .SetEntryPoint("draft")
    .Compile(new InMemoryCheckpointer<ResearchState>());

// Run to completion...
var result = await graph.RunAsync(new ResearchState("What is a trellis?", null, 0));

// ...or stream every step:
await foreach (var evt in graph.StreamAsync(new ResearchState("...", null, 0)))
    Console.WriteLine($"{evt.Type}: {evt.Node}");
```

Reuse a `ThreadId` in `GraphRunOptions` and a crashed or interrupted workflow resumes from its latest checkpoint instead of starting over.

Agents drop straight into a graph, branches can fan out in parallel, and runs can pause for a human:

```csharp
var graph = new StateGraph<ResearchState>()
    .AddAgentNode("draft", draftAgent,
        prompt: s => s.Question,
        apply: (s, output) => s with { Draft = output })
    .AddParallelNode("factcheck",
        branches: [CheckSourcesAsync, CheckToneAsync],
        merge: (input, results) => Merge(input, results))
    .AddEdge("draft", "factcheck")
    .AddEdge("factcheck", "publish")
    .AddNode("publish", PublishAsync)
    .SetEntryPoint("draft")
    .Compile(SqliteCheckpointer<ResearchState>.FromFile("workflows.db"));

// Pause before publishing so a human can approve or edit the draft:
var options = new GraphRunOptions { ThreadId = "wf-1", InterruptBefore = ["publish"] };
var paused = await graph.RunAsync(initialState, options);          // Status: Interrupted

await graph.UpdateStateAsync("wf-1", s => s with { Draft = editedDraft });
var done = await graph.RunAsync(initialState, options);            // resumes → Completed
```

## Packages

| Package | What's in it |
|---|---|
| `Trellis` | Typed agents (`Agent<TResult>`, `Agent<TDeps, TResult>`), the `[Tool]` source generator, and agent-as-node graph helpers |
| `Trellis.Graph` | Graph runtime: `StateGraph<TState>`, parallel branches, streaming, interrupts, checkpointing (no AI dependency — usable for any workflow) |
| `Trellis.Checkpointing.Sqlite` | Durable SQLite-backed checkpointer for graph workflows |

## Building

```bash
dotnet test
```

Requires the .NET 10 SDK. No API keys needed for the test suite — agent tests run against a fake `IChatClient`.

## Roadmap

- [x] Source-generated tool discovery (`[Tool]` attribute → `CreateTools()` at compile time)
- [x] `Agent<TDeps, TResult>` — compile-time-checked dependency injection into tools
- [x] Agent-as-node helpers bridging `Trellis` and `Trellis.Graph`
- [x] Parallel fan-out/fan-in branches
- [x] `Trellis.Checkpointing.Sqlite` durable checkpointer
- [x] Human-in-the-loop interrupts
- [x] NuGet release pipeline (packages publish on version tags)
- [ ] Streaming agent responses (token-by-token)
- [ ] OpenTelemetry instrumentation for agents and graph runs
- [ ] Retry/fallback policies per node
- [ ] Postgres checkpointer

## License

[MIT](LICENSE)
