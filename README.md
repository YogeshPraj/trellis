# Trellis

**Typed agents on a structured graph, for .NET.**

Trellis is what [Pydantic AI](https://ai.pydantic.dev) and [LangGraph](https://langchain-ai.github.io/langgraph/) are for Python — rebuilt idiomatically for C#. No new abstraction layer: it sits directly on [`Microsoft.Extensions.AI`](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai), so it works with any provider that ships an `IChatClient` (OpenAI, Anthropic, Azure OpenAI, Ollama, ...).

> ⚠️ Early prototype (v0.1). The API is small on purpose and will change.

## Why

The Pydantic AI value proposition — *typed, validated agent outputs* — is almost free in C#. Define a `record`, and the model's response is requested as structured JSON and deserialized into it. No decorators, no runtime schema library:

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

## Graph runtime

`Trellis.Graph` is a LangGraph-style state machine: nodes transform a state object, edges (fixed or conditional) decide what runs next, and every step can be checkpointed for resume.

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

Checkpointing is pluggable via `ICheckpointer<TState>`; runs that reuse a `ThreadId` resume from the latest checkpoint, so a crashed workflow picks up where it left off. `GraphRunOptions.MaxSteps` guards against runaway loops.

## Packages

| Package | What's in it |
|---|---|
| `Trellis` | Typed agents (`Agent<TResult>`) on `IChatClient` |
| `Trellis.Graph` | Graph runtime: `StateGraph<TState>`, streaming, checkpointing (no AI dependency — usable for any workflow) |

## Building

```bash
dotnet test
```

Requires the .NET 8 SDK or later. No API keys needed for the test suite — agent tests run against a fake `IChatClient`.

## Roadmap

- [ ] Source-generated tool discovery (`[Tool]` attribute → `AIFunction`, zero reflection)
- [ ] `Agent<TDeps, TResult>` — compile-time-checked dependency injection into tools
- [ ] Agent-as-node helpers bridging `Trellis` and `Trellis.Graph`
- [ ] Parallel fan-out/fan-in branches
- [ ] `Trellis.Checkpointing.Sqlite` durable checkpointer
- [ ] Human-in-the-loop interrupts
- [ ] NuGet publishing

## License

[MIT](LICENSE)
