# Trellis — Project Context

> Claude reads this file at the start of every session in this folder. Keep it current.

## What This Is

**Trellis** — "Typed agents on a structured graph, for .NET." An agent framework built
directly on `Microsoft.Extensions.AI` (`IChatClient`), positioned as the typed, SOLID,
production-honest alternative in the .NET ecosystem (vs Microsoft Agent Framework's breadth).

- Repo: https://github.com/YogeshPraj/trellis (public, MIT)
- Owner: Yogesh Prajapati (`YogeshPraj`)
- Current version: **0.10.0**. 229 tests. (0.8.0 tagged; GitHub release with all nupkgs.)
- NuGet publishing: release workflow pushes on `v*` tags **only if** the `NUGET_API_KEY`
  repo secret exists (not configured yet — packages are attached to GitHub releases).

## Working Agreement (owner's explicit expectations — follow these)

1. **Production rigor over task completion.** Before shipping anything, run an explicit
   failure analysis: multi-instance deployment, concurrent access, dependency-down,
   restart mid-operation, unbounded growth over weeks, latency on the hot path. Surface
   accepted limitations as decisions in conversation — never bury them in docstrings.
2. **Lead with vision, don't just implement requests.** The owner expects proactive
   pointers on what a best-in-class system looks like, ranked gaps, and recommendations —
   then execution.
3. **SOLID + design patterns.** Every policy decision behind an interface with a default
   implementation (see the routing layer for the house style).
4. **Abstraction-contract testing, never vendor testing.** Trellis validates against the
   `IChatClient` contract. Provider behavior is the adapter's responsibility — document
   required semantics as opt-in contracts (see `ModelFeatures.ServerConversationState`),
   don't test against live vendor APIs. Real-model wiring validation uses **local Ollama**
   (`OllamaIntegrationTests`, model `qwen2.5:1.5b`; tests no-op when Ollama is down).
   Note: coder-tuned small models emit tool calls as plain text — useless for tool tests.
   The same rule shapes MCP: Trellis logic sits behind `IMcpToolSource` and is unit-tested
   with fakes; the SDK adapter is validated against the reference server in
   `McpIntegrationTests` (`npx @modelcontextprotocol/server-everything`, no-ops without Node).

## Solution Layout (all net10.0 except the generator)

| Project | Purpose |
|---|---|
| `src/Trellis` | Typed agents: `Agent<TResult>`, `Agent<TDeps,TResult>` (per-run tool DI), self-healing outputs (`IOutputValidator<TResult>` + `OutputRetryOptions`, on by default for typed results), streaming (`RunStreamingAsync` → `AgentStream<TResult>`), `Conversation` (canonical client-side history; hot/cold compaction, message **and** token budgets via `ITokenCounter`), `IConversationStore` (multi-instance, optimistic concurrency) + `TieredConversationStore` (write-through chain, per-tier circuit breaker), `AgentTelemetry` (spans/metrics/cost), `[Tool]` attribute, agent-as-node graph bridge |
| `src/Trellis.Graph` | Zero-AI-dependency graph runtime: `StateGraph<TState>`, conditional edges, `AddParallelNode`, streaming events, `InterruptBefore` human-in-the-loop, `ICheckpointer<TState>`, per-node retry/fallback (`NodeResilience<TState>`), `GraphTelemetry`, per-process ThreadId run guard |
| `src/Trellis.Routing` | `ModelRouter : IChatClient` — priority tiers + circuit breaker. Strategies: `IFailureClassifier`, `IFailurePolicy`, `IEndpointHealthStore`, `IEndpointSelectionStrategy` (round-robin / lowest-latency EMA / lowest-cost). Capability filtering (`ModelCapabilities`), conversation sync (delta + provider id for server-state endpoints, full replay on failover) |
| `src/Trellis.State` | `ISharedStateStore` cross-instance KV with atomic `IncrementAsync`/`AppendAsync`/`GetListAsync`; opt-in `IAtomicSharedStateStore` (compare-and-swap); InMemory + `IDistributedCache` bridge (bridge is read-modify-write, no CAS — single-writer only) |
| `src/Trellis.State.Redis` | Redis provider (StackExchange.Redis 3.x — `StringSetAsync` takes `Expiration`, not TimeSpan); INCR/RPUSH truly atomic; CAS via a Lua script |
| `src/Trellis.Azure.Cosmos` | Azure Cosmos DB provider for `ISharedStateStore` — ETag CAS, server-side Patch increments, one-document-per-entry lists. Container needs `/pk` partition key and `DefaultTimeToLive` for TTL. Documents are public (`CosmosStateDocument`) as they are the on-disk schema, and carry BOTH Newtonsoft and STJ attributes because the Cosmos SDK defaults to Newtonsoft |
| `src/Trellis.Mcp` | MCP client support (ModelContextProtocol 2.x): `IMcpToolSource` + `McpToolset` (multi-server aggregation, server-name prefixing, allow-list, failure isolation) and the SDK-backed `McpServerToolSource` (stdio/HTTP, lazy connect, cached tool listings) |
| `src/Trellis.Checkpointing.Sqlite` | Durable checkpointer: WAL, busy_timeout, per-thread retention (default 100). SQLitePCLRaw pinned ≥3.0.5 (CVE in the transitive default) |
| `src/Trellis.Tools.Generator` | netstandard2.0 incremental source generator: `[Tool]` methods on partial classes → `CreateTools()`. Ships inside the `Trellis` package as an analyzer. Diagnostics TRL001–TRL003 |
| `tests/Trellis.Tests` | xunit; fakes for unit coverage (`FakeChatClient`), NSubstitute for Redis, real-model `OllamaIntegrationTests` with one-retry flake tolerance |

## Key Invariants (don't break these)

- **Client-side conversation is the source of truth**; provider-side state is only an
  optimization. `Conversation.ContextEpoch` bumps on compaction → `RoutingId` changes →
  router discards provider deltas and replays full history.
- **Compaction can never fail a user turn** (failures → `OnCompactionFailure`), never
  splits tool call/result chains, and runs off the response path
  (`Conversation.PendingCompaction` — await before persisting/shutdown).
- **Request-shaped failures don't trip endpoints**: context-window / content-policy →
  failover WITHOUT cooldown; rate-limit / quota / timeout / 5xx → failover AND trip;
  unknown → propagate. Status codes in messages match as standalone tokens only.
- Failure counting must stay atomic (`IEndpointHealthStore.RecordFailureAsync`).
- **Self-healing retries live inside a single run**: failed attempts + correction messages
  go into the run's payload only — a `Conversation` absorbs just the final accepted
  response. (With `ServerConversationState` endpoints, a retried turn over-counts
  `SyncedCount`, so the router's `SyncedCount <= full.Count` guard forces a full replay
  next turn — self-correcting, never silently divergent.)
- **Streaming never self-heals**: validation runs only after the last token and emitted
  tokens cannot be retracted, so `AgentStream` throws instead of streaming a second answer.
  Conversation mutation is lazy — user turn on first enumeration, reply on completion.
- **Graph retries are opt-in per node** (nodes are arbitrary user code with side effects),
  never consume `MaxSteps`, never checkpoint a failed attempt, and never retry cancellation.
- **Telemetry does not instrument the chat call** — M.E.AI's `UseOpenTelemetry()` owns that;
  duplicating it would double-count tokens. Trellis spans cover agent runs and graph nodes.
- Conversation saves are version-checked; a stale write raises
  `ConversationConcurrencyException` rather than clobbering another instance's turn.
- **Tiered conversation storage is write-through, last tier authoritative.** A fallback that
  was never written to is empty, so plain failover loses the conversation and failback
  silently reverts it — write-through keeps every healthy tier on the same version. Replica
  write failure never fails the turn but MUST delete that tier's entry, and a recovering tier
  is excluded from reads until a write repairs the specific conversation. Tier health is
  per-process (documented limitation; bound it with per-tier TTLs).
- **Cloud code is a leaf package, never a framework dependency.** Core stays cloud-neutral
  (`Trellis.Graph` has zero deps). Naming: cloud-neutral tech is `Trellis.<Area>.<Tech>`
  (`Trellis.State.Redis`), cloud-specific is `Trellis.<Cloud>.<Service>`
  (`Trellis.Azure.Cosmos`; `Trellis.Aws.DynamoDb` would slot in identically).
- `Directory.Build.props` owns version + packaging; `TreatWarningsAsErrors` is on.

## Commands

```bash
dotnet test                      # full suite (Ollama tests auto-skip if server down)
dotnet pack -c Release -o packages
git tag v0.X.0 && git push origin v0.X.0   # cut a release
```

## Open Roadmap (owner-approved direction: layers 4–5 of the vision — trust + ecosystem)

Shipped in 0.10.0: streaming agents, token-budget compaction, per-node retry/fallback,
OpenTelemetry + cost accounting, `IConversationStore`, MCP client support.

- Eval harness for agent outputs (regression-test prompts/validators) — top pick
- Durable execution semantics (idempotency keys, deterministic replay; Orleans/DTF)
- Retrieval over the cold conversation archive
- Postgres checkpointer
- Cross-instance graph run leasing (the ThreadId guard is still per-process)
