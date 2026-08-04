# Trellis — Project Context

> Claude reads this file at the start of every session in this folder. Keep it current.

## What This Is

**Trellis** — "Typed agents on a structured graph, for .NET." An agent framework built
directly on `Microsoft.Extensions.AI` (`IChatClient`), positioned as the typed, SOLID,
production-honest alternative in the .NET ecosystem (vs Microsoft Agent Framework's breadth).

- Repo: https://github.com/YogeshPraj/trellis (public, MIT)
- Owner: Yogesh Prajapati (`YogeshPraj`)
- Current version: **0.9.0**. 118 tests. (0.8.0 tagged; GitHub release with all nupkgs.)
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

## Solution Layout (all net10.0 except the generator)

| Project | Purpose |
|---|---|
| `src/Trellis` | Typed agents: `Agent<TResult>`, `Agent<TDeps,TResult>` (per-run tool DI), self-healing outputs (`IOutputValidator<TResult>` + `OutputRetryOptions`, on by default for typed results), `Conversation` (canonical client-side history; hot/cold compaction via `ConversationCompactor`), `[Tool]` attribute, agent-as-node graph bridge |
| `src/Trellis.Graph` | Zero-AI-dependency graph runtime: `StateGraph<TState>`, conditional edges, `AddParallelNode`, streaming events, `InterruptBefore` human-in-the-loop, `ICheckpointer<TState>`, per-process ThreadId run guard |
| `src/Trellis.Routing` | `ModelRouter : IChatClient` — priority tiers + circuit breaker. Strategies: `IFailureClassifier`, `IFailurePolicy`, `IEndpointHealthStore`, `IEndpointSelectionStrategy` (round-robin / lowest-latency EMA / lowest-cost). Capability filtering (`ModelCapabilities`), conversation sync (delta + provider id for server-state endpoints, full replay on failover) |
| `src/Trellis.State` | `ISharedStateStore` cross-instance KV with atomic `IncrementAsync`/`AppendAsync`/`GetListAsync`; InMemory + `IDistributedCache` bridge (bridge is read-modify-write — single-writer only) |
| `src/Trellis.State.Redis` | Redis provider (StackExchange.Redis 3.x — `StringSetAsync` takes `Expiration`, not TimeSpan); INCR/RPUSH truly atomic |
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
- `Directory.Build.props` owns version + packaging; `TreatWarningsAsErrors` is on.

## Commands

```bash
dotnet test                      # full suite (Ollama tests auto-skip if server down)
dotnet pack -c Release -o packages
git tag v0.X.0 && git push origin v0.X.0   # cut a release
```

## Open Roadmap (owner-approved direction: layers 4–5 of the vision — trust + ecosystem)

- MCP client support (ecosystem unlock) — top pick
- OpenTelemetry GenAI spans + cost accounting; eval harness
- `IConversationStore` (multi-instance hot conversation state; currently per-process)
- Token-budget compaction thresholds; retrieval over the cold archive
- Durable execution semantics (idempotency keys, deterministic replay; Orleans/DTF)
