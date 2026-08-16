# Trellis

**Typed agents on a structured graph, for .NET.**

Trellis lets you build AI agents and multi-step agent workflows in idiomatic C#. Define a `record`, and your agent returns it — strongly typed, validated, deserialized. Compose agents and logic into a graph of nodes with conditional routing, stream every step as it executes, and checkpoint progress so workflows survive crashes and resume where they left off.

No new abstraction layer to learn: Trellis sits directly on [`Microsoft.Extensions.AI`](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai), so it works with any provider that ships an `IChatClient` — OpenAI, Anthropic, Azure OpenAI, Ollama, and more.

> ⚠️ Early release (v0.2). The API is small on purpose and will evolve.

## Features

- 🎯 **Strongly-typed agent outputs** — ask for an `Agent<FlightResult>` and get a `FlightResult` back, not a string to parse. Structured JSON output and deserialization are handled for you.
- 🩹 **Self-healing structured outputs** — when the model's JSON fails to parse or fails validation, the errors are fed back to the model as a correction and it retries (bounded) before a typed `OutputValidationException` surfaces. Add semantic rules via `IOutputValidator<TResult>`; a DataAnnotations validator is included.
- 📶 **Streaming agent runs** — `RunStreamingAsync` yields token-by-token updates and, once the stream ends, hands you the same assembled, deserialized, validated `Result` the buffered call would have produced.
- 🔧 **Tools are plain C# methods** — register any delegate as a tool; tool calls are executed automatically in a loop until the model produces its final answer.
- 🔌 **MCP servers as tools** — `Trellis.Mcp` connects agents to Model Context Protocol servers over stdio or HTTP, aggregating several servers with collision-free naming, an allow-list, and failure isolation so one dead server degrades the agent instead of breaking it.
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
- ⚖️ **Five load-balancing strategies** — order same-priority deployments by round-robin, **weight** (smooth weighted round-robin for mixed PTU/pay-as-you-go capacity), observed **latency** (EMA), **cost** per token, or **least loaded** (live in-flight request count).
- 🧵 **Portable conversations** — `Conversation` keeps the canonical history client-side. Endpoints with server-side context (e.g. OpenAI's Responses API) transparently receive only the new messages plus their conversation id; failing over to a stateless provider replays the full history, so mid-conversation failover loses nothing.
- 🗂️ **Multi-instance conversations** — `IConversationStore` persists live conversations (hot messages, rolling summary, epoch) so consecutive turns can land on different instances, with optimistic concurrency that refuses to silently clobber another instance's turn.
- 🪜 **Tiered storage with write-through** — chain any number of backends (Redis → Cosmos → ...) so a backend outage degrades instead of losing conversations. Every healthy tier holds the same version, so failover finds warm data and failback can't silently revert.
- 🔥❄️ **Hot / cold context** — long conversations stay bounded: recent turns stay *hot* (verbatim in the prompt); older turns go *cold* — folded into a rolling summary by a cheap model and archived verbatim to any store. Context windows never overflow, token costs stay flat, nothing is lost.
- 🕸️ **Graph workflow engine** — model multi-step processes as a state machine: nodes transform your state object, fixed or conditional edges decide what runs next, and the graph shape is validated at compile time.
- 📡 **Streaming execution** — observe every step live via `IAsyncEnumerable`: node started, node completed, graph completed — perfect for progress UIs and logging.
- 💾 **Checkpointing & resume** — pluggable `ICheckpointer<TState>` records progress after every node; rerun with the same `ThreadId` and the workflow picks up exactly where it stopped.
- 🔁 **Per-node retry & fallback** — give any node an `INodeRetryPolicy` (capped exponential backoff with jitter by default) and a fallback that turns a dead dependency into a degraded state instead of a dead workflow.
- 🌀 **Loop protection** — a `MaxSteps` guard stops runaway cycles before they burn tokens.
- 🔌 **Provider-agnostic** — anything with an `IChatClient` works; swap OpenAI for Ollama with one line.
- 🗣️ **Multi-turn conversations** — pass full message histories, with system instructions prepended automatically.
- 📊 **OpenTelemetry + cost accounting** — agent runs and graph nodes emit spans and metrics through plain `ActivitySource`/`Meter` (no SDK dependency), following the GenAI semantic conventions. Plug in a price list and every run reports what it cost.
- 🧪 **Testable by design** — the whole test suite runs against a fake `IChatClient`; no API keys, no network.
- 🧩 **Zero-AI graph core** — `Trellis.Graph` has no AI dependency at all; use it to orchestrate any workflow, agentic or not.

## Typed agents in 5 lines

```csharp
using Microsoft.Extensions.AI;
using Trellis.Agents;

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

## Streaming agent runs

Stream tokens to a UI without giving up the typed result — enumerate for deltas, then read `Result`:

```csharp
AgentStream<FlightResult> run = agent.RunStreamingAsync("book me a flight to Pune");

await foreach (string delta in run.TextDeltasAsync())
    Console.Write(delta);                 // token-by-token, as the model produces it

FlightResult flight = run.Result.Output;  // assembled, deserialized, validated
```

Conversations stream too, and stay canonical: `agent.RunStreamingAsync(conversation, "...")` appends the user turn when enumeration *starts* and folds the assembled reply in when it *finishes*, so a stream you never enumerate — or abandon halfway — never leaves a half-turn in the history.

⚠️ **Streaming does not self-heal.** Validation can only run once the last token has arrived, and tokens already handed to the caller can't be retracted — so a rejected output throws `OutputValidationException` at the end of enumeration rather than silently streaming a second, contradictory answer. Use the buffered `RunAsync` when self-healing matters more than first-token latency. (One more caveat: streaming sets the JSON-schema response format directly, so a `TResult` whose schema root isn't an object — a bare `int` or array — is rejected by providers that require an object root. Wrap primitives in a record.)

## Self-healing structured outputs

Models sometimes return JSON that doesn't parse, or values that parse but are wrong. Instead of throwing on the first bad response, a typed agent feeds the exact error back to the model — alongside its own failed attempt — and lets it correct itself:

```csharp
using System.ComponentModel.DataAnnotations;
using Trellis.Outputs;

record Booking(
    [property: Required] string Destination,
    [property: Range(0, 10_000)] decimal Price);

var agent = new Agent<Booking>(client,
    instructions: "You book flights.",
    outputValidator: new DataAnnotationsOutputValidator<Booking>(),  // or any IOutputValidator<Booking>
    outputRetry: new OutputRetryOptions { MaxRetries = 2 });         // the defaults, shown explicitly

Booking booking = (await agent.RunAsync("book me a flight to Pune")).Output;
```

How it behaves:

- **On by default** for typed outputs: deserialization failures self-heal with up to 2 correction retries even if you configure nothing. `MaxRetries = 0` restores fail-fast.
- **Semantic validation** goes through `IOutputValidator<TResult>` — return errors phrased for the model ("Price must be positive") and they become the correction prompt. `DataAnnotationsOutputValidator<TResult>` covers the declarative cases.
- **Budget exhausted → typed failure**: an `OutputValidationException` carrying every attempt's raw text and errors, not a bare `JsonException` from deep inside serialization.
- **Cost is visible and bounded**: each retry re-pays roughly the full request, and `result.Attempts` reports what a run actually used.
- **Conversations stay clean**: retry traffic lives only inside the run — a `Conversation` absorbs the user turn and the final accepted answer, never the failed attempts or corrections.
- The correction message's role and wording are configurable (`FeedbackRole`, `FeedbackFormatter`), and validators can be async (call a DB, an eval model, ...).

## MCP servers as tools

`Trellis.Mcp` connects an agent to [Model Context Protocol](https://modelcontextprotocol.io) servers — GitHub, filesystems, databases, your own — and hands their tools to the model:

```csharp
using Trellis.Mcp;

await using var github = McpServerToolSource.Stdio("github", "npx", ["-y", "@modelcontextprotocol/server-github"]);
await using var docs   = McpServerToolSource.Http("docs", new Uri("https://internal/mcp"));

var toolset = new McpToolset([github, docs], new McpToolsetOptions
{
    AllowedTools = ["create_issue", "search_docs"],   // allow-list anything you don't control
});

var agent = new Agent(client, instructions: "Use tools.", tools: await toolset.GetToolsAsync());
```

MCP tools already arrive as `AIFunction`s, so no adapter is needed to reach an agent. What Trellis adds is what a multi-server deployment actually needs:

- **Collision-free naming** — tools are prefixed with their server (`github_create_issue`), which also tells the model which system a tool belongs to. Choose `McpToolNaming.Preserve` to keep the server's names, and a cross-server duplicate then fails fast instead of one server silently shadowing another.
- **Failure isolation** — a server that's down is skipped (with a callback so a degraded agent is loud, not silent) rather than taking the whole agent with it. Set `OnServerUnavailable = Throw` for servers the agent is useless without.
- **An allow-list** — a server can add tools at any time, and whatever it advertises becomes callable by your model. ⚠️ Treat third-party servers as untrusted: their tool descriptions enter your prompt and their tools run for real.
- **Concurrent, deterministic loading** — servers are queried in parallel so a slow one doesn't serialize the rest, but the tool list is assembled in registration order. Listings are cached (5 min by default) so agent runs don't pay a round trip each time.

Trellis's own logic sits behind `IMcpToolSource` and is unit-tested without a server; protocol conformance is the official MCP SDK's job. The adapter itself is validated against the real reference server (`@modelcontextprotocol/server-everything`) in `McpIntegrationTests`, which no-op when Node isn't installed.

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

### Per-node retry & fallback

Nodes call flaky things. Give one a resilience policy and a transient failure stops being a dead workflow:

```csharp
.AddNode("enrich", EnrichFromApiAsync, new NodeResilience<OrderState>
{
    Retry = new ExponentialBackoffRetryPolicy(maxAttempts: 4, baseDelay: TimeSpan.FromMilliseconds(200),
                                              shouldRetry: e => e is not ArgumentException),
    Fallback = (state, error, ct) => Task.FromResult(state with { Enrichment = null, Degraded = true }),
})
```

Retries are **off by default and opt-in per node** — a node is arbitrary code, and re-running one re-runs its side effects. Enable them on nodes you've made idempotent; the XML docs say so at the point of use.

- Backoff doubles per attempt, clamps at `maxDelay`, and carries jitter so a fleet doesn't retry a shared dependency in lockstep.
- `shouldRetry` filters out errors that will never succeed (bad input, auth), so you don't wait 4 times for a certain failure.
- Retries don't consume `MaxSteps` (they're re-executions of one step) and don't write checkpoints — only the successful attempt does, so a resumed run never replays a failed one.
- Cancellation is never retried; it means the caller gave up, not that the node failed.
- When the fallback itself fails you get a `GraphExecutionException` holding **both** errors in an `AggregateException` — the fallback's error alone would hide why it ran.
- `StreamAsync` emits `NodeRetrying` (with attempt number and error) and `NodeFallbackApplied`, so retries are visible to progress UIs and logs rather than hidden latency.

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
| `IEndpointSelectionStrategy` | *How a priority tier is ordered* — round-robin, weighted, lowest latency, lowest cost, or least loaded | `RoundRobinSelectionStrategy` |

The failure policy distinguishes *provider* problems from *request* problems, LiteLLM-style:

- **Rate limit / quota / timeout / 5xx** → fail over **and trip** the endpoint (it's unhealthy).
- **Context-window overflow / content-policy rejection** → fail over **without tripping** — the model is healthy, this request just doesn't fit it; a bigger-window or more permissive deployment gets it instead, and the endpoint stays in rotation for the next request.
- **Unknown errors** → propagate immediately; they'd fail on every model anyway.

When a provider says how long to back off (`Retry-After`), that exact duration is used instead of the exponential cooldown.

### Load balancing within a tier

Priorities always win first; a selection strategy only arbitrates *within* one tier:

| Strategy | Orders by | Use it when |
|---|---|---|
| `RoundRobinSelectionStrategy` *(default)* | rotation | deployments are interchangeable |
| `WeightedSelectionStrategy` | `ModelEndpoint.Weight` | capacity differs — e.g. a PTU deployment beside pay-as-you-go |
| `LowestLatencySelectionStrategy` | observed latency (EMA) | you want whatever is fastest right now |
| `LowestCostSelectionStrategy` | `CostPerMillionTokens` | spend matters more than speed |
| `LeastLoadedSelectionStrategy` | live in-flight requests | request costs vary wildly and latency averages mislead |

```csharp
new ModelEndpoint("azure-ptu",  ptuClient,  priority: 0) { Weight = 4 },   // 80% of the tier
new ModelEndpoint("azure-payg", paygClient, priority: 0) { Weight = 1 },   // 20%
// options: SelectionStrategy = new WeightedSelectionStrategy()
```

`WeightedSelectionStrategy` uses smooth weighted round-robin (nginx's algorithm), so weights 3:1 yield `A B A A` rather than clustering three A's together — the split holds over short bursts, not just in the long run. It's computed from the request counter rather than mutable state, so it stays deterministic and allocation-light.

`LeastLoadedSelectionStrategy` counts requests actually outstanding, with a streaming response counted in flight until its last token. That's the strategy that measures congestion rather than inferring it — useful when a 200-token classification shares a tier with a 100k-token summarization. Counts are per-process, so it balances one instance's concurrency, not the fleet's.

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

### Conversations across instances

A `Conversation` is a live in-memory object. Behind a load balancer, turn 2 may land on a different pod than turn 1 — so give it a store:

```csharp
var store = new SharedStateConversationStore(
    new RedisSharedStateStore(mux),
    timeToLive: TimeSpan.FromHours(12),   // abandoned sessions expire instead of accumulating
    requireAtomicStore: true);            // refuse a backend that can't compare-and-swap

Conversation conversation = await store.LoadAsync(sessionId) ?? new Conversation(sessionId);
await agent.RunAsync(conversation, prompt);
await store.SaveAsync(conversation);
```

Saves carry a version. If another instance advanced the conversation since this copy was loaded, `SaveAsync` throws `ConversationConcurrencyException` instead of overwriting that turn — reload and reapply, and if it happens often, add session affinity rather than retrying harder. On Redis the check is a real compare-and-swap (a Lua script, so the read and write can't interleave); on the `IDistributedCache` bridge, which has no CAS, the version check still catches the common case but a narrow race remains — `requireAtomicStore: true` rejects such a backend up front rather than letting you inherit silent last-write-wins.

This complements the cold archive: `IConversationArchive` keeps compacted history, `IConversationStore` keeps the working conversation.

#### Surviving a backend outage: tiered write-through

If losing conversations when Redis has a bad day isn't acceptable, chain storage tiers — fastest first, durable last — and every write goes through all of them:

```csharp
IConversationStore store = new TieredConversationStore(
[
    new ConversationTier("redis",  redisStore,  TimeToLive: TimeSpan.FromHours(12)),
    new ConversationTier("cosmos", cosmosStore),          // ← authoritative: owns the version check
]);
```

Adding another tier is adding another line — the list *is* the configuration, and any `ISharedStateStore` qualifies (Redis, the `IDistributedCache` bridge, in-memory, your own):

```csharp
new ConversationTier("memory", inMemory, TimeToLive: TimeSpan.FromMinutes(5)),
new ConversationTier("redis",  redisStore, TimeToLive: TimeSpan.FromHours(12)),
new ConversationTier("cosmos", cosmosStore),
new ConversationTier("blob",   blobStore),               // ← now this one is authoritative
```

The design deliberately avoids the trap that a naive failover chain falls into — **a fallback you never wrote to is empty, so failing over to it loses the conversation, and failing *back* silently reverts it**. Write-through means every healthy tier holds the same version, so a fallback is warm and a failback is a no-op.

- **The last tier is authoritative.** It performs the version check and compare-and-swap, so concurrent writers are detected exactly as with a single store.
- **Every other tier is a replica**, written unconditionally once the authority accepts. A replica write that fails never fails the turn — the tier is marked unhealthy and its entry is **deleted**, so it can never serve a version older than the authority's.
- **Unhealthy tiers are skipped** for reads and writes, with a cooldown that doubles per consecutive failure (capped) — the same circuit-breaker shape as `ModelRouter`. The recheck is lazy and uses the next real operation as its probe, so there's no background poller hammering a struggling backend. Set `MaxUnhealthyCooldown` equal to `UnhealthyCooldown` for a flat recheck interval.
- **A recovering tier isn't trusted straight away.** It missed every write made while it was down, so it's excluded from reads until a write-through has repaired that specific conversation — which normal traffic does on its own, since a read falls to the authority and the backfill repopulates the tier. Repair mode ends once the tier's `TimeToLive` has elapsed since recovery, at which point no pre-outage entry can still exist.
- **Reads take the fastest healthy tier** that has the conversation and backfill the ones that missed it.
- **Replicas are written concurrently**, so a save costs the authority's round trip plus the *slowest* replica — not the sum of every tier. The snapshot is serialized once and reused across tiers.

#### Write-behind: pay one fast round trip

If even that is too much — or you want a durable-tier outage to stop failing turns — switch the mode:

```csharp
new TieredConversationStoreOptions
{
    ReplicationMode = ReplicationMode.WriteBehind,   // only tier 0 is written synchronously
    FlushInterval   = TimeSpan.FromSeconds(1),
    OnReplicationFailed = (id, ex) => logger.LogError(ex, "replication lagging for {Id}", id),
}
```

Only the **first** tier is written before `SaveAsync` returns — and it becomes the authority, since it's the only tier guaranteed current. The rest are updated by a background flusher, and `await using` (or an explicit `FlushAsync()`) drains it on shutdown.

⚠️ **This changes the guarantee.** A returned save is *not yet durable*: turns written since the last flush live only in tier 0. An abrupt process kill loses up to `FlushInterval` of work. In exchange, saves cost one fast round trip and the durable tier being down no longer fails a turn.

What keeps the blast radius small:

- **Snapshots are cumulative, not deltas.** A lagging tier holds a complete, older conversation — never a corrupt one. Losing unflushed writes costs the turns since the last flush, not the session.
- **Pending writes coalesce per conversation.** Twenty turns in a second replicate once, and the pending set is bounded by *active conversations*, not by traffic.
- **A write that doesn't land stays pending.** If the durable tier is down or cooling, the write re-queues for the next tick instead of being dropped — otherwise a turn that failed to replicate would vanish from the durable tier the moment that conversation went idle.
- **Replica writes are version-conditional**, so a late flush can never put an older snapshot on top of a newer one.
- **`MaxPendingReplications`** applies backpressure by flushing inline rather than letting the pending set grow unbounded.
- **If the authority itself is down**, the save fails by default — nothing is written anywhere, so the conversation cannot fork. `AuthorityUnavailableBehavior.PromoteHealthiest` keeps serving instead, at the cost of turns living only in a non-durable tier until the authority returns.

⚠️ Health is tracked **per process**. If one instance's replica write fails, other instances don't learn that the tier is stale and could read a stale copy — the authority's version check turns that into a rejected save rather than corruption, but the turn is wasted. Set `TimeToLive` on accelerator tiers to bound that window, and prefer a backend whose own replication (Redis replicas, zone redundancy) makes single-node failure invisible in the first place. Cross-service failover defends against losing a whole service in a region, which is rarer than it feels.

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

Budgets come in two flavours, and whichever trips first wins:

```csharp
new CompactionOptions
{
    MaxHotMessages = 40, KeepRecentMessages = 12,   // message count: predictable, free
    MaxHotTokens = 8_000, KeepRecentTokens = 2_500, // tokens: what windows and bills are denominated in
    TokenCounter = new HeuristicTokenCounter(),     // or wrap a real tokenizer via ITokenCounter
}
```

The token trigger prefers **the provider's own reported input tokens** for the previous turn over any estimate — exact, free, and it accounts for your instructions, the rolling summary, and images. Whatever that reported total exceeds what the counter can attribute to the hot messages is treated as fixed overhead and charged against the retained tail's allowance, so the budget still bites when history isn't what's blowing it. (If that overhead alone exceeds the budget, every turn compacts to the newest message and stays there — the budget is unreachable, and only raising it or shortening your instructions fixes it.) The rolling summary is itself capped, so it can't quietly re-inflate every prompt over weeks.

What the model sees each turn: your instructions → *"Summary of the earlier conversation: ..."* → the hot tail. Each compaction bumps the conversation's `ContextEpoch`, which changes its routing id — so a conversation-aware router discards provider-side deltas and replays the compacted history in full (a server-side delta against the pre-compaction transcript would be wrong). The archive reuses `ISharedStateStore`, so cold context can live in memory, Redis, or any `IDistributedCache` backend.

## Observability & cost

Trellis instruments the layer that provider-level tracing can't see: a whole **agent run** (self-healing retries included) and **graph orchestration** (one span per node execution). It uses only `System.Diagnostics` primitives, so there's no OpenTelemetry SDK dependency — subscribe by name:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(AgentTelemetry.ActivitySourceName)   // "Trellis.Agent"
                       .AddSource(GraphTelemetry.ActivitySourceName))  // "Trellis.Graph"
    .WithMetrics(m => m.AddMeter(AgentTelemetry.MeterName)
                       .AddMeter(GraphTelemetry.MeterName));
```

It deliberately does **not** instrument the chat call itself — `Microsoft.Extensions.AI`'s `UseOpenTelemetry()` already does, and duplicating it would double-count tokens. Compose both for the full picture.

| Signal | What it tells you |
|---|---|
| `invoke_agent` span | One agent run: result type, model, token usage, `trellis.agent.attempts` (>1 means self-healing kicked in), error status |
| `graph.run` / `graph.node {name}` spans | Orchestration shape; a **retried node produces one span per attempt**, so retries are visible instead of just looking slow |
| `trellis.agent.output.rejections` | How often the model's output fails validation — the metric that tells you a prompt or schema needs work |
| `trellis.graph.node.retries` / `.fallbacks` | Which nodes are unstable, and which are running degraded |
| `gen_ai.client.token.usage`, `trellis.agent.cost` | Tokens by type, and spend when a cost model is configured |

Cost accounting is opt-in, because token prices change and vary per contract — Trellis ships no price list it would be wrong about:

```csharp
AgentTelemetry.CostModel = new StaticTokenCostModel(new Dictionary<string, ModelPrice>
{
    ["gpt-4o"] = new(InputPerMillion: 2.50m, OutputPerMillion: 10.00m),
});
```

An unknown model prices as `null`, never `0` — a dashboard can then distinguish "not priced" from "free". With nothing listening, instrumentation costs a null check per run.

## Namespaces

Types are grouped by subsystem rather than dumped in one namespace, so the shape of the
library is visible from the `using` list:

| Namespace | What lives there |
|---|---|
| `Trellis.Agents` | `Agent<TResult>`, `Agent<TDeps,TResult>`, `AgentRunResult`, `AgentStream` |
| `Trellis.Outputs` | `IOutputValidator<T>`, `OutputRetryOptions`, validators, `OutputValidationException` |
| `Trellis.Conversations` | `Conversation` |
| `Trellis.Conversations.Compaction` | `ConversationCompactor`, `CompactionOptions`, summarizers |
| `Trellis.Conversations.Archive` | `IConversationArchive` and providers |
| `Trellis.Conversations.Storage` | `IConversationStore`, `TieredConversationStore`, providers |
| `Trellis.Tokens` | `ITokenCounter`, `HeuristicTokenCounter` |
| `Trellis.Diagnostics` | `AgentTelemetry`, `ITokenCostModel`, pricing |
| `Trellis.Tools` | `[Tool]` attribute |
| `Trellis.Graph` | `StateGraph<T>`, `CompiledGraph<T>`, events, results |
| `Trellis.Graph.Checkpointing` | `ICheckpointer<T>`, `Checkpoint<T>` |
| `Trellis.Graph.Resilience` | `NodeResilience<T>`, `INodeRetryPolicy` |
| `Trellis.Routing` | `ModelRouter`, `ModelEndpoint`, options |
| `Trellis.Routing.Selection` | Load-balancing strategies |
| `Trellis.Routing.Failures` | Classification and policy |
| `Trellis.Routing.Health` | Circuit-breaker state |
| `Trellis.Routing.Capabilities` | `ModelCapabilities`, `ModelFeatures` |

> ⚠️ **Breaking in 0.11.0.** Everything used to sit in a flat `Trellis` namespace. Replace
> `using Trellis;` with the subsystem namespaces you actually use — the compiler will name
> them. `[Tool]` now needs `using Trellis.Tools;`.

## Packages

| Package | What's in it |
|---|---|
| `Trellis` | Typed agents (`Agent<TResult>`, `Agent<TDeps, TResult>`), the `[Tool]` source generator, and agent-as-node graph helpers |
| `Trellis.Graph` | Graph runtime: `StateGraph<TState>`, parallel branches, streaming, interrupts, checkpointing (no AI dependency — usable for any workflow) |
| `Trellis.Checkpointing.Sqlite` | Durable SQLite-backed checkpointer for graph workflows |
| `Trellis.Routing` | `ModelRouter`: priority-based, capability-aware failover across model deployments with circuit-breaker cooldowns, automatic recovery, and portable conversation state |
| `Trellis.State` | Cross-instance shared state: `ISharedStateStore` with in-memory and `IDistributedCache` providers |
| `Trellis.State.Redis` | Redis provider for `Trellis.State` (StackExchange.Redis) |
| `Trellis.Azure.Cosmos` | Azure Cosmos DB provider for `Trellis.State`: durable cross-instance storage with ETag-based compare-and-swap |
| `Trellis.Mcp` | MCP client support: connect agents to Model Context Protocol servers (stdio/HTTP) with multi-server aggregation, allow-listing, and failure isolation |

## Cloud providers, and swapping them

The core is cloud-neutral by construction: `Trellis` depends only on `Microsoft.Extensions.AI`, `Trellis.Graph` on **nothing**, and every piece of infrastructure sits behind an interface — `ISharedStateStore`, `IConversationStore`, `IConversationArchive`, `ICheckpointer<TState>`, `IEndpointHealthStore`. A cloud is a leaf package, never a dependency of the framework.

The naming rule follows that split:

| Kind | Naming | Examples |
|---|---|---|
| Cloud-neutral technology | `Trellis.<Area>.<Tech>` | `Trellis.State.Redis` (Azure Cache, ElastiCache, or self-hosted), `Trellis.Checkpointing.Sqlite` |
| Cloud-specific service | `Trellis.<Cloud>.<Service>` | `Trellis.Azure.Cosmos` — and `Trellis.Aws.DynamoDb` slots in the same way |

So an Azure deployment and an AWS one differ by which leaf packages are referenced and a few lines of wiring, not by any change to agent, graph, or routing code:

```csharp
// Azure today
ISharedStateStore durable = new CosmosSharedStateStore(cosmosContainer);

// AWS tomorrow — same interface, same tiered store, same agents
// ISharedStateStore durable = new DynamoDbSharedStateStore(dynamoClient, "trellis-state");

IConversationStore store = new TieredConversationStore(
[
    new ConversationTier("redis", redis, TimeToLive: TimeSpan.FromHours(12)),
    new ConversationTier("durable", durable),
]);
```

`Trellis.Azure.Cosmos` also ships **`CosmosConversationStore`**, an append-only conversation schema. Nothing is ever replaced, updated, or patched — a turn writes only new documents:

| | Conversation as one document | `CosmosConversationStore` |
|---|---|---|
| A turn writes | the whole history, replaced | only its new messages, inserted |
| RU cost at turn 100 | ~100× turn 1 | flat |
| Ceiling | **2 MB document limit ends the conversation** | none |
| Mutations per turn | 1 replace | **none** |

A conversation is one partition (`/cid`) holding three kinds of write-once document:

- `m-{ordinal}` — one message. The id is deterministic, so a replayed append conflicts instead of duplicating.
- `v-{version}` — the metadata a turn commits: counters, epoch, usage. Small.
- `s-{epoch}` — a rolling summary, written *only* when compaction produces a new one, so ordinary turns never rewrite it.

**Concurrency control falls out of the schema.** Committing version N+1 means inserting `v-{N+1}`, a document only one writer can create; the loser gets a 409 and a `ConversationConcurrencyException`. No ETag, no read-modify-write, and no patch — the concurrency check *is* the commit, which also sidesteps the ten-operation cap on Cosmos patches.

A save that dies after appending but before committing leaves documents no reader will ever see, because reads are bounded by the newest commit's `messageCount`. Nothing needs cleaning up, and the retry re-creates the same ids harmlessly.

`CosmosSharedStateStore` implements `IAtomicSharedStateStore` using Cosmos **ETags**, so the tiered store's compare-and-swap is genuinely atomic across instances rather than emulated. Increments use the server-side Patch operation, and list appends write one document per entry so an archive isn't capped by the 2&nbsp;MB document limit. The container needs `/pk` as its partition key path, and `DefaultTimeToLive` set if you pass a TTL — the store throws rather than let Cosmos silently ignore expiry you asked for.

## Building

```bash
dotnet test
```

Requires the .NET 10 SDK. No API keys needed for the test suite — agent tests run against a fake `IChatClient`.

## Validation & the abstraction contract

Trellis is an abstraction layer over `IChatClient`. Its tests target the **contract**, never a vendor: provider wire-format correctness belongs to the adapter (OpenAI SDK, OllamaSharp, ...), and Trellis stays deliberately uncoupled from live vendor APIs.

- ✅ **Contract behavior validated against a real model** (local Ollama): plain agent runs, **typed structured outputs**, **self-healing validation retries**, **token-by-token streaming** (text and typed), **automatic tool invocation**, multi-turn conversations. These integration tests live in `OllamaIntegrationTests` and run whenever a local Ollama is reachable; they no-op otherwise (e.g. CI).
- 📜 **`ServerConversationState` is an opt-in contract**: marking an endpoint with it asserts its `IChatClient` follows the documented `ConversationId` semantics (see the flag's XML docs). Trellis's sync logic is verified against that contract; conformance of a given adapter is the adapter's responsibility.
- ⚠️ **Multi-instance notes**: router health state, conversation archives, and the conversation store are fleet-safe with an atomic backend (Redis); the `IDistributedCache` bridge emulates atomic ops (single-writer only). Conversations now persist and rehydrate through `IConversationStore` with optimistic concurrency; the graph run-guard remains per-process, so route a given thread id to one instance.

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
- [x] Self-healing structured outputs (validation-retry with error feedback)
- [x] Token-budget-based compaction thresholds (`ITokenCounter` + provider-reported usage)
- [x] Streaming agent responses (token-by-token)
- [x] OpenTelemetry instrumentation for agents and graph runs (+ cost accounting)
- [x] Retry/fallback policies per node (`INodeRetryPolicy` + `NodeResilience<TState>`)
- [x] `IConversationStore` — multi-instance hot conversation state with optimistic concurrency
- [x] MCP (Model Context Protocol) client support (`Trellis.Mcp`)
- [ ] Eval harness for agent outputs
- [ ] Durable execution semantics (idempotency keys, deterministic replay)
- [ ] Retrieval over the cold conversation archive
- [ ] Postgres checkpointer

## License

[MIT](LICENSE)
