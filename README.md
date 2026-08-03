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
- 🚦 **Model failover & prioritization** — `ModelRouter` spreads requests across multiple deployments by priority. A model that hits a rate limit or runs out of quota is tripped with an exponential cooldown and *skipped entirely* until it recovers — later requests go straight to the fallback with zero added latency.
- 🧭 **Capability-aware routing** — endpoints declare what they support (tools, vision, JSON output, context window); a request needing tools never gets sent to a model that can't call them.
- 🩺 **Typed failure handling** — status-code-based classification with `Retry-After` support; context-window and content-policy failures fail over *without* penalizing a healthy endpoint. Every policy is an interface you can swap.
- 🌐 **Fleet-shared circuit state** — cooldown state flows through the `Trellis.State` abstraction (`ISharedStateStore`): in-memory by default, Redis via `Trellis.State.Redis`, or any `IDistributedCache` provider (SQL Server, Cosmos, Garnet) through the built-in bridge. One instance tripping a dead deployment protects the whole fleet.
- ⚖️ **Latency & cost-aware selection** — order same-priority deployments by observed latency (EMA) or price per token instead of plain round-robin.
- 🧵 **Portable conversations** — `Conversation` keeps the canonical history client-side. Endpoints with server-side context (e.g. OpenAI's Responses API) transparently receive only the new messages plus their conversation id; failing over to a stateless provider replays the full history, so mid-conversation failover loses nothing.
- 🔥❄️ **Hot / cold context** — long conversations stay bounded: recent turns stay *hot* (verbatim in the prompt); older turns go *cold* — folded into a rolling summary by a cheap model and archived verbatim to any store. Context windows never overflow, token costs stay flat, nothing is lost.
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

## Model failover & prioritization

Running multiple deployments (Azure + OpenAI + a local Ollama, or the same model in several regions)? `ModelRouter` is an `IChatClient`, so it drops underneath any agent or graph unchanged — and it solves the "one model ran out of tokens" problem without adding latency to every request:

```csharp
using Trellis.Routing;

IChatClient router = new ModelRouter(
[
    new ModelEndpoint("azure-eastus", azureClient,  priority: 0),  // preferred
    new ModelEndpoint("azure-west",   azureClient2, priority: 0),  // same tier → round-robin
    new ModelEndpoint("openai",       openaiClient, priority: 1),  // fallback
    new ModelEndpoint("ollama-local", ollamaClient, priority: 2),  // last resort
],
new ModelRouterOptions
{
    BaseCooldown = TimeSpan.FromSeconds(30),   // doubles per consecutive failure, capped at MaxCooldown
    OnEndpointTripped = (e, ex, until) => logger.LogWarning("{Name} tripped until {Until}", e.Name, until),
    OnEndpointRecovered = e => logger.LogInformation("{Name} recovered", e.Name),
});

var agent = new Agent<FlightResult>(router, instructions: "You book flights.");
```

How it behaves when a deployment hits a 429 / quota exhaustion / outage:

1. The failing request pays the failover cost **once** and is answered by the next tier.
2. The failed endpoint is **tripped** — subsequent requests skip it entirely (no probe, no wait, no friction) while its cooldown runs.
3. Repeated failures double the cooldown (30s → 60s → 120s → ... up to `MaxCooldown`).
4. When the cooldown expires, the next request quietly retries it; on success it's restored to full priority automatically.

If *everything* is cooling down, the router either degrades gracefully to the soonest-recovering endpoint (default) or fails fast, per `AllTrippedBehavior`. Streaming fails over too, up until the first token arrives.

### Typed failure handling

The router is built from four pluggable strategies (each an interface with a sensible default) so every policy decision is yours to override without touching routing code:

| Extension point | Decides | Default |
|---|---|---|
| `IFailureClassifier` | *What went wrong* — typed `FailureKind` from status codes and messages, plus the provider's `Retry-After` when available | `DefaultFailureClassifier` |
| `IFailurePolicy` | *What to do about it* — propagate, fail over + trip, or fail over only | `DefaultFailurePolicy` |
| `IEndpointHealthStore` | *Where cooldown state lives* — `SharedStateEndpointHealthStore` adapts any `ISharedStateStore` backend so your whole fleet shares one view of which deployments are down | `InMemoryEndpointHealthStore` |
| `IEndpointSelectionStrategy` | *How a priority tier is ordered* — round-robin, lowest observed latency, or lowest cost | `RoundRobinSelectionStrategy` |

The failure policy distinguishes *provider* problems from *request* problems, LiteLLM-style:

- **Rate limit / quota / timeout / 5xx** → fail over **and trip** the endpoint (it's unhealthy).
- **Context-window overflow / content-policy rejection** → fail over **without tripping** — the model is healthy, this request just doesn't fit it; a bigger-window or more permissive deployment gets it instead, and the endpoint stays in rotation for the next request.
- **Unknown errors** → propagate immediately; they'd fail on every model anyway.

When a provider says how long to back off (`Retry-After`), that exact duration is used instead of the exponential cooldown. Latency is tracked per endpoint (EMA), so `LowestLatencySelectionStrategy` routes to whatever is actually fastest right now, and `LowestCostSelectionStrategy` uses `ModelEndpoint.CostPerMillionTokens`.

### Sharing circuit state across your fleet

Cross-instance state goes through one narrow abstraction — `ISharedStateStore` in `Trellis.State` (get / set-with-TTL / remove) — with providers, not couplings:

```csharp
// Redis (Trellis.State.Redis):
var options = new ModelRouterOptions
{
    HealthStore = new SharedStateEndpointHealthStore(
        new RedisSharedStateStore(connectionMultiplexer)),
};

// ...or any existing IDistributedCache provider (SQL Server, Cosmos, Garnet):
HealthStore = new SharedStateEndpointHealthStore(
    new DistributedCacheSharedStateStore(distributedCache)),
```

With a shared backend, one instance hitting a dead deployment trips it for every instance, and recovery propagates fleet-wide the same way. The router never knows which backend is in play — it only sees `IEndpointHealthStore`.

### Heterogeneous providers: capabilities

Deployments rarely support the same things. Declare per-endpoint capabilities and the router only considers endpoints that can actually serve the request — a tool-calling request skips the model without function calling, an oversized prompt skips the small context window, instead of failing there first:

```csharp
new ModelEndpoint("gpt-4o", openaiClient, priority: 0),                       // default: tools + vision + JSON
new ModelEndpoint("small-local", ollamaClient, priority: 2, new ModelCapabilities
{
    Features = ModelFeatures.JsonResponseFormat,                              // no tools, no vision
    MaxInputTokens = 8_000,
}),
```

If *no* registered endpoint supports what a request needs, you get a `NoCompatibleModelException` up front — not a provider error after a doomed round-trip.

### Heterogeneous providers: conversation context

Some providers manage conversation state server-side (OpenAI's Responses API); most are stateless. Trellis resolves the mismatch with one rule: **the client-side `Conversation` is always the source of truth**, and provider-side state is only an optimization.

```csharp
var agent = new Agent(router, instructions: "You are a travel assistant.");
var conversation = new Conversation();

await agent.RunAsync(conversation, "Find me flights to Pune");
await agent.RunAsync(conversation, "What about hotels near the airport?");  // full context, any provider
```

Mark an endpoint with `ModelFeatures.ServerConversationState` and the router exploits it automatically: follow-up turns send only the unsynced messages plus the provider's own conversation id. The moment a request routes anywhere else — failover, load balancing, recovery — that endpoint receives the complete canonical history instead. Mid-conversation failover between providers is seamless, and your conversation is never trapped inside one vendor's API.

### Hot & cold context: conversations that never overflow

A long-running conversation can't send its whole history forever. Give the agent a `ConversationCompactor` and the history is tiered automatically:

- **Hot** — the most recent turns, kept verbatim and sent every request.
- **Cold** — once the hot history exceeds its budget, the oldest turns are (1) folded into a **rolling summary** by an `IConversationSummarizer` (point it at a small, cheap model) and (2) **archived verbatim** through an `IConversationArchive`, so the full transcript remains retrievable for display, audit, or search.

```csharp
var compactor = new ConversationCompactor(
    summarizer: new ChatClientConversationSummarizer(cheapClient),   // e.g. a local or mini model
    archive: new SharedStateConversationArchive(new RedisSharedStateStore(mux)),
    options: new CompactionOptions { MaxHotMessages = 40, KeepRecentMessages = 12 });

var agent = new Agent(router, instructions: "You are a travel assistant.", compactor: compactor);

// Turn 500 costs the same as turn 5: summary + hot tail, never the whole transcript.
await agent.RunAsync(conversation, "so which hotel did we settle on again?");
```

What the model sees each turn: your instructions → *"Summary of the earlier conversation: ..."* → the hot tail. Each compaction bumps the conversation's `ContextEpoch`, which changes its routing id — so a conversation-aware router discards provider-side deltas and replays the compacted history in full (a server-side delta against the pre-compaction transcript would be wrong). The archive reuses `ISharedStateStore`, so cold context can live in memory, Redis, or any `IDistributedCache` backend.

## Packages

| Package | What's in it |
|---|---|
| `Trellis` | Typed agents (`Agent<TResult>`, `Agent<TDeps, TResult>`), the `[Tool]` source generator, and agent-as-node graph helpers |
| `Trellis.Graph` | Graph runtime: `StateGraph<TState>`, parallel branches, streaming, interrupts, checkpointing (no AI dependency — usable for any workflow) |
| `Trellis.Checkpointing.Sqlite` | Durable SQLite-backed checkpointer for graph workflows |
| `Trellis.Routing` | `ModelRouter`: priority-based, capability-aware failover across model deployments with circuit-breaker cooldowns, automatic recovery, and portable conversation state |
| `Trellis.State` | Cross-instance shared state: `ISharedStateStore` with in-memory and `IDistributedCache` providers |
| `Trellis.State.Redis` | Redis provider for `Trellis.State` (StackExchange.Redis) |

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
- [x] Model failover & prioritization (`Trellis.Routing`)
- [x] Capability-aware routing (tools / vision / JSON / context window)
- [x] Portable conversation state across stateful and stateless providers
- [x] Error-type-specific failover (context window / content policy vs rate limit)
- [x] Status-code failure classification + `Retry-After` honoring
- [x] Pluggable shared circuit-breaker state (`IEndpointHealthStore`)
- [x] Latency (EMA) and cost-based tier selection strategies
- [x] `Trellis.State` shared-state abstraction with in-memory, `IDistributedCache`, and Redis providers
- [x] Hot/cold conversation context (rolling summary + verbatim archive)
- [ ] Token-budget-based compaction thresholds (currently message-count based)
- [ ] Streaming agent responses (token-by-token)
- [ ] OpenTelemetry instrumentation for agents and graph runs
- [ ] Retry/fallback policies per node
- [ ] Postgres checkpointer

## License

[MIT](LICENSE)
