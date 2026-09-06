# Trellis Roadmap

Gaps found by comparing Trellis against AgentScope 2.0's architecture and Cursor's model
router, then filtered against Trellis's actual position: *the typed, SOLID, production-honest
option for .NET*. Breadth is Microsoft Agent Framework's game. Items are sorted by whether
they are **structural** — cheap now, expensive or dangerous to retrofit — not by size.

Status legend: `[ ]` not started · `[~]` in progress · `[x]` shipped

---

## Tier 1 — Structural. Do these while the execution model is still small.

Each of these changes what an *agent*, a *tool*, or a *run* fundamentally is. Adding them
later means reopening every call site.

- [x] **1.1 Agent middleware pipeline** — shipped 0.14.0. `IAgentMiddleware<TResult>` wraps
  every buffered run; first entry outermost; may short-circuit or retry. Streaming refuses
  rather than silently bypassing it. This is the seam 1.3, 1.4, 2.1 and 3.1 hang off.

- [x] **1.2 Tool authorization / permission system** — shipped 0.13.0. `IToolAuthorizer` gates
  every call, allow-list by default, unanimous composition, fails closed, refusals metered.
  For a framework selling production-honesty that is the sharpest credibility gap we have.
  It is also a security boundary, so retrofitting fails open unless every existing call site
  is audited. AgentScope puts its Permission System directly between Toolkit and
  Reasoning/Acting for exactly this reason.

- [x] **1.3 Multi-agent messaging and handoff** — shipped 0.16.0. `IAgent<TResult>` common
  shape, `AgentTeam<TResult>` with model-decided `transfer_to_*` handoff, bounded loops, and
  nesting for manager/workers. Deliberately *not* built: deterministic speaker-selection
  policies, which `StateGraph` already expresses.

- [x] **1.4 Workspace — bounded filesystem** — shipped 0.15.0. `IWorkspace` + `LocalWorkspace`
  (link-resolving containment, quotas, read-only) + `WorkspaceTools`, composing with 1.2.
  ⚠ Still outstanding: *isolated execution*. A container / remote-sandbox `IWorkspace` and any
  form of process execution are not built, and `LocalWorkspace` explicitly does not sandbox a
  process. Tracked as 1.5.

- [ ] **1.5 Isolated execution** — a Docker or remote-sandbox `IWorkspace`, and running
  commands at all. This is what "sandbox" actually requires; 1.4 only bounds paths. Leaf
  packages, so it stays out of the cloud-neutral core.

## Tier 2 — Trust. Needed before anything optimizes quality-for-cost.

- [x] **2.1 Eval harness** — shipped 0.17.0 as `Trellis.Evals`. Suites over any
  `IAgent<TResult>`, programmatic + model-graded scorers, JSON baselines, comparison that
  treats movement inside the baseline's own sampling spread as noise, and cost beside quality.
  **2.2 is now unblocked.**

- [ ] **2.2 Semantic model routing** — `IModelSelectionPolicy` seeing request content, sitting
  above the existing alias resolver (which is already the outer routing loop). Ship the seam
  plus heuristic and LLM-classifier defaults, never a trained model. **Cache affinity is a
  hard requirement, not a nicety**: switching models mid-conversation invalidates provider
  prefix cache and forces a full replay, so a naive per-turn router can cost more than it
  saves. Requires 2.1 to be safe.

- [ ] **2.3 Durable execution semantics** — idempotency keys and deterministic replay.

## Tier 3 — Additive. No architectural cost; schedule freely.

- [ ] **3.1 Long-term memory + retrieval** — zero embeddings and zero vector storage today.
  `IEmbeddingGenerator` is unused. Covers retrieval over the cold conversation archive.
- [ ] **3.2 Postgres checkpointer** — the notable storage gap.
- [ ] **3.3 Vector store providers** — Qdrant / Milvus / pgvector, behind one abstraction.
- [ ] **3.4 Browser tool** — realistic in .NET via Playwright.
- [ ] **3.5 Event bus / pub-sub** — we have graph and agent streaming, but no general
  message/event fabric.
- [ ] **3.6 Embeddings, TTS, realtime** — `IEmbeddingGenerator`, `ISpeechToTextClient`.
- [ ] **3.7 Interop protocols** — A2A, AG-UI, OpenAI Response API compatibility.
- [ ] **3.8 `Trellis.Gateway`** — sessions, background tasks, cron, authN/Z, virtual API keys.
  Still blocked on three unanswered decisions: compile-time NuGet modules vs runtime DLL
  loading, endpoint surface, and the virtual-key auth scheme.
- [ ] **3.9 `IDeploymentRegistry`** — zero-downtime drain-then-cancel. Designed, not built.

## Deliberately not building

- **RL fine-tuning** (Trinity-RFT) and **data pipelines** (Data-Juicer) — not a .NET agent
  framework's job; building them dilutes the positioning.
- **Multi-language runtimes** (Python/Java/TS) — .NET is the whole point.
- **Observability integrations** (Langfuse, LangSmith, Phoenix) — they consume OTLP and we
  emit native OpenTelemetry, so this is a documentation task, not an integration task.

## Verified as already ahead

Model failover and load balancing, context compression, hot/cold conversation management,
compile-time tool discovery, and OpenTelemetry are all at or beyond the comparison point.

---

## Known unverified

- **Cosmos providers have never run against a live server.** The emulator suite compiles and
  skips cleanly; only the skip path has executed. Needs an elevated shell:
  `& "C:\Program Files\Azure Cosmos DB Emulator\Microsoft.Azure.Cosmos.Emulator.exe" /NoUI /NoExplorer`
  then `dotnet test --filter "FullyQualifiedName~CosmosEmulator"`.
