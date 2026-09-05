> Review-time snapshot of the workspace root file. The live workspace file is authoritative.

Backup decision and references amended on 2026-09-05 to match the owner’s native Rust rebuild decision (D-29, TerraBase ADR 0046).

# TerraScale production goal

This file is the **single authority** for the TerraScale workspace: what we
are building, the closed decisions, launch scope, and road to production. It
wins over every ADR, design doc, and README and lives at the workspace root. The operational architecture is reconciled
with `ts-orchestrator/GOAL.md`, including GRU authority, same-host management,
greenfield replacement, and the Firecracker qualification contract.
Each repository `GOAL.md` comprehensively restates every applicable decision
in repository-specific language. It may add local acceptance criteria but must
keep this file's direction and change with it when the two diverge.

This file holds decisions and plan, never progress: no dates, no status, no
audit snapshots, no changelog.

## 0. Parallelism mandate (read before anything else)

**Use as many subagents as the work allows, always.** Wall-clock time to
launch is the metric this plan optimizes; agent-hours are cheap and
sequential work is the only thing that is expensive. Every agent that
receives a task, in every repository, at every level:

1. **Decomposes first.** Split the task into units that share no files, then
   launch one subagent per unit at once (the whole batch in one step, never
   one after another). The parent agent integrates, runs the verification
   set, and pushes; it writes code itself only when a task cannot be split.
2. **Splits along seams.** A unit is one repository, one crate/package, one
   feature slice folder, one ADR section, or one test lane. Two agents never
   hold the same file; if they must, the later one rebases and the parent
   picks the order.
3. **Never waits.** Blocked on another repository's work → build against the
   published contract (E-1), the mock server or temporary dashboard SQLite
   store (P-1/D-59), the stub origin (X-4), or the local stack (E-15).
   Blocked on the owner → one line in section 8, next
   unit. Blocked on a decision → the simplest choice consistent with sections
   1–4, noted in the commit message.
4. **Starts the whole wave.** Section 6 marks which items have no
   prerequisites; on day one every one of them is staffed simultaneously,
   across all repositories, including every immediate deletion list. Within a track,
   items with the same prerequisites start together, not in listed order.
5. **Fans out inside items.** Every track item lists its parallel units
   (`‖`); those are the minimum fan-out, not a suggestion. Deletions,
   SDK ports, dashboard feature slices, reconcilers, and simulation
   invariants are each one agent per unit.
6. **Keeps pushes independent.** Small commits, each green on its own, so a
   hundred agents can rebase onto one `develop` without serializing.

## How to work here (agents)

1. **Read first.** Sections 1–5 hold every closed decision. Do not reopen one;
   if you believe a decision is wrong, add one line to section 8 and keep
   building under the decision as written.
2. **One branch per repository.** Everyone works on the local `develop` branch
   of each repository's single checkout. No feature branches, no worktrees,
   no PRs (D-32). `git pull --rebase` before you start and before every push.
3. **Green before push.** Run the repository's verification set (section 5)
   and push only when it passes. Small commits with descriptive subjects,
   pushed often. A red `develop` stops the line: whoever broke it fixes or
   reverts immediately, and anyone may revert.
4. **Delete as you go.** Code, docs, tests, and config for retired directions
   are removed in the same commit series that supersedes them.
5. **Humans own `AGENTS.md` / `CLAUDE.md`.** Needed changes go to section 8.
6. Re-read this file at every gate transition and whenever the owner says it
   changed.
7. **Parallelism is section 0.** A task done sequentially that could have
   been fanned out is a defect, reported like a red build.

---

## 1. What TerraScale is

TerraScale is a **database-as-a-service**: a fast, multi-region document
database with leaderless CRDT replication, CDN-backed reactive live queries,
per-document history, backups/PITR/branching, and the full product around it
(dashboard, billing, SDK, CLI, MCP), running on our own orchestrator instead
of Kubernetes. The business goal is to be reliable, fast, efficient, and
**cheap to run**, so we undercut competitors with radically simpler pricing:
**pay for writes and storage; reads, egress, fan-out, subscribers, and
delivery are free** (D-16).

Who it's for: individual developers, AI-assisted builders shipping apps fast,
and companies that need multi-region data without a cloud architect on staff.

The three sentences every task serves:

- **vs DynamoDB global tables / CosmosDB**: the same active-active
  multi-region writes and single-digit-ms local reads, **way cheaper**: one
  write price with the per-region replication cost visible up front, reads
  and egress free, storage at parity. Never claim "no per-region cost":
  replicated writes cost one write per region; honesty is the brand.
- **vs Convex**: the same reactive DX (live queries, optimistic updates,
  end-to-end types, instant dev branches) in **SQL you already know**, from
  TypeScript, C#, Go, or Rust (D-13/D-78), but **multi-region active-active**,
  full-text search, and CDN-collapsed HTTP delivery whose
  database load follows writes and distinct shared views, not attached
  readers. Server-side TypeScript functions follow right after launch (D-19).
- **vs Electric**: Electric's pricing model (writes + retention, everything
  else free) on a complete database instead of a sync layer bolted onto
  someone else's Postgres. **The two engines are the product**: the **sync
  engine** (result views maintained incrementally from the engine's durable
  publication journal; each view encoded once into an immutable HTTP log that
  Cloudflare caches and request-collapses with zero metered compute on cache
  hits) and the **replication engine** (leaderless CRDT across regions, RF=2
  inside a region, `RegionalDurable` / `GlobalDurable` write classes,
  deterministic conflict resolution with per-document history). Storage is
  **mainline SQLite** (D-27): the most battle-tested storage engine on earth,
  with our multi-region CRDT sync on top. Provable claims: zero-parse reads
  (D-30) and auto-indexing without stale reads (D-31).

When a tradeoff comes up, prefer: simple pricing > feature count; developer
experience > enterprise checkboxes; cost per write > peak benchmark numbers;
**working end to end > provably green in isolation**.

---

## 2. Architecture

### Runtime data path

```text
customer app ──────────────── @terrascale/sdk ─┐
dashboard (Vinext on Cloudflare Workers, D-59) ─┤ server side: service binding → ts-proxy; browser side: plain SDK
                                                │
                                                │ REST first contact: api.terrascale.tech → ts-proxy Worker (D-58)
                                                │   picks the nearest healthy Region THAT HOSTS THE DATABASE, then the SDK
                                                │   pins REST (reads AND writes) to <region>.api.terrascale.tech: no Worker, no CDN
                                                │
                                                │ reactive HTTP: sync.api.terrascale.tech → Snippet gate (free) → Cache Rules
                                                │   Snippet verifies the view capability header, strips it, fetches the canonical
                                                │   URL through the cache; cache lock collapses misses, Tiered Cache bounds fills;
                                                │   a cache HIT never runs metered compute (D-55)
                                                ▼
                    terrabase member  (Rust, one process per host, symmetric)
                    ├── public API      axum: auth (API keys, BYO-JWT), access rules, REST /api/v1, read-only SQL (D-54),
                    │                   view attach + capability issue, immutable snapshot/log responses, optional WebSocket,
                    │                   ETags, metering export
                    ├── engine          one SQLite file per database (WAL, one writer), publication journal in the same
                    │                   transaction, SQL over extracted columns + JSON body (D-78), auto-index, FTS5, TTL, history
                    ├── sync            shared result/collection views from the journal, encode-once log + fan-out
                    ├── replication     intra-region RF=2 = physical WAL-frame follower;
                    │                   cross-region = journal tailer over QUIC + doc-level CRDT merge (leaderless)
                    ├── backup          native Rust backup per file → OCI Object Storage / Tigris (one or both by policy): PITR, restore-to-new-db, branching
                    └── control gRPC    private TBEP: placement, health, drain, admin, faults — orchestrator/CLI only
                                                ▲
                    ts-orchestrator (Go + NATS) ─┘  reconciles OCI + Cloudflare to declared state, places members, rolls
                                                   binaries under systemd (no containers), certs, keys, KV, Worker/Snippet
                                                   promotion. Never on the customer data path. NATS never carries customer data.
                    ts-deploy (Go)                 the only CI logic: check, build, test, publish, hand the candidate to the
                                                   orchestrator. Workflows are five lines that download it and run it.
                    terrascale CLI (Go)            cloud dev branches, push, codegen, import/export; talks to the public API.
```

There is no separate gateway process. A REST request for database X that lands
on a member that is not X's in-region writer is served locally if it is a read
the session token allows, otherwise forwarded in-region to the writer. A
request for a database the Region does not host returns `421 Misdirected
Request` with `X-TerraScale-Placement`; the proxy and SDK repair themselves
from that header (D-58). Reactive HTTP never pins away from Cloudflare.
Customers never run terrabase (D-50): `terrascale dev` is a cloud dev branch
(D-18). Every member host runs TerraBase, its local orchestrator, `ts-node`, and a
Grafana agent as independent systemd services. No host is dedicated to
orchestration.

### Control authority and regional growth

Every member host runs a local `ts-orchestrator` and `ts-node` alongside
TerraBase. Local controllers use same-machine TBEP for TerraBase lifecycle;
`ts-node` alone mutates TerraScale runtime files and systemd after cloud-init.
Restarting either controller process does not stop the admitted database.
A surviving authorized controller replaces a lost host and installs both
TerraBase and its local orchestrator before admission.

GRU owns one fenced global mutation authority. Each Region executes its
accepted declaration under GRU-issued regional authority. An operation binds
Region, desired-state revision, operation ID, and fencing epoch, with durable
intent and receipts. A provider or TBEP timeout is an unknown outcome until
observed. A regional controller or a JetStream storage leader cannot promote
itself to global master. Missing authority or durable control storage stops new
mutations, while admitted data-plane service continues.

Bootstrap has an independent durable journal and artifact trust root. It
creates two GRU hosts and a temporary pre-customer R1, then synchronizes SFO and
FRA candidates before a fenced promotion to permanent GRU/SFO/FRA R3. Retire
bootstrap authority only after quorum and a post-transition snapshot are
verified. Customer admission requires R3 and the production gates. Restoring
control state may never regress the accepted epoch or recreate an old master.

| Estate | Minimum member hosts | Local orchestrator processes | Global control replicas | Admission scope |
| --- | ---: | ---: | ---: | --- |
| 3 Regions | 6 | 6 | 3, in GRU/SFO/FRA | Launch topology |
| 10 Regions | 20 | 20 | The same 3 | Expansion design |
| 20 Regions | 40 | 40 | The same 3 | Maximum planned estate |

Regions 4–20 use the global R3 without joining it. An independent regional
R3 is post-launch capacity work and requires at least three eligible hosts.
Adding a Region to the estate does not add it to any database's immutable
placement. A database has RF=2 in each selected Region; replication, backups,
restore, export, branches, and repair obey that same residency lock.

Before production, declare control RPO/RTO, recovery deadlines, resource
budgets, and supported release skew. Qualify authority transitions, local
process restart, joint host loss, indeterminate mutations, and durable recovery
through real Firecracker guests. The fourth-Region admission scenario extends
the six-VM baseline. Passing it does not qualify 10- or 20-Region production
performance. The [architecture atlas](ts-docs/reviews/architecture/README.md)
shows the three estate sizes and request paths.

### REST load balancing and capacity

`ts-proxy` is the REST load balancer. First filter by database placement and
residency, then by admitted Region/member readiness and available capacity.
Route around a failed, draining, or overloaded server to another eligible
member, preferring the same Region. Use another permitted Region when needed;
never widen residency or send work back to a known failed server because every
candidate is unavailable. Preserve method, scope, session frontier, and
idempotency. Return a typed unavailable/overload result when no safe target
exists. Tenant quota rejection must not be mistaken for host overload.

The orchestrator publishes bounded, timestamped member identity, endpoint,
readiness, drain state, and capacity observations. Apply thresholds, freshness
limits, hysteresis, and recovery probation to avoid flapping. Bound retries and
use passive failure feedback between control observations. Never blindly replay
a mutation after an unknown outcome. Regional REST pins must recover through
the load balancer when their target fails or refuses work. Reactive snapshot/log
delivery remains Snippet + CDN and never passes through a Worker.

Every Region starts with exactly two member servers. Scale out when measured
CPU, memory, disk/I/O pressure, query latency, or queue saturation exceeds a
sustained declared threshold. Thresholds and cooldowns are explicit configuration
qualified against workload measurements. The orchestrator provisions the host,
installs both TerraBase and a local orchestrator, proves RF=2/placement catch-up,
and publishes readiness before traffic. Scale-in drains, preserves at least two
hosts, honors database placement and durability, and requires stable low load.
A host's removal cannot remove the last required durable replica.

Load balancing does not create another writer for the same SQLite file.
Distribute independent databases and eligible reads across capacity; a hot
write workload uses the accepted key-range split and placement mechanisms.
Failover from one of two hosts does not by itself restore RF=2. Preserve the
promised durability class until a replacement has caught up.

### The two engines (D-35..D-38, D-45, D-46, D-78; design in terrabase ADR 0044)

**A write.** The Region's writer parses once, canonicalizes, extracts indexed
fields, assigns `dot=(origin, seq)`, `hlc`, and `context` (the client's
session token ∪ local frontier), and commits one SQLite transaction: the
document row with body bytes and causal metadata, extracted columns, FTS, and
the reconciliation bucket fingerprint. WAL frames reach the RF=2 follower;
its `fdatasync` ack is `RegionalDurable`. A journal tailer per peer Region
ships rows in `seq` order (FIFO per origin) in zstd waves over QUIC. The
receiver applies a row only when every dot in its context is applied
(causal+): dominated → drop; clean → write bytes, no parse; concurrent →
parse both, remove-wins per field, HLC tiebreak, merged bytes + both inputs
and the rule into history. Rows awaiting dependencies wait in a bounded
queue; overflow triggers reconciliation, never a skip. `GlobalDurable` acks
when every selected Region has fsynced the row and all of its causal
dependencies are already uniform; < 2 reachable Regions fails closed.
Range-based set reconciliation over `(origin, seq) → hash` runs on reconnect,
bootstrap, queue overflow, and a slow sweep. Merge is a pure function of the
observed set, so any delivery order converges to identical bytes.

**A read.** The SDK carries its session token; a member behind it on any
origin waits a bounded time, then serves or returns `StaleReplica` naming a
Region that can, and the SDK follows. Defaults: read-your-writes + monotonic
reads; per request `eventual` or `writes-follow-reads`; never linearizable.

**A subscription.** `POST /reactive/views` plans the SQL, classifies it
(single collection + `WHERE` on extracted columns or JSON paths + `ORDER BY`
+ `LIMIT` + `MATCH` = live; anything else `NotSubscribable`, still
executable), compiles the access rule into the plan, derives the view key
(database incarnation, plan, params, projection, schema and index versions,
rule revision, entitlement epoch, Region), attaches to the shared view or
creates it, registers its predicate in the interval tree by
`(collection, index_id, range)`, builds a 2k boundary buffer for top-k, and
returns view id, generation, snapshot chunk URLs, log URL, checkpoint (a
journal cut), and the capability. On every commit the interval tree yields
the affected views from extracted values already in hand; each gets the row's
Z-set delta `{(old,-1),(new,+1)}` through its compiled filter (linear, no
state) or its boundary buffer; once per 100 ms window one delta per affected
view is encoded to MessagePack and appended to the immutable log with its
cut. Readers at offset R all receive the same bytes and ask for R+1. Rule or
schema changes bump the generation; the old log lives through retention and
is purged by tag. Resume with retained revisions sends them; any changed
bound value is a typed `Reset`. A revision is one cut; two different views
promise no mutual consistency (use one collection-sync view for one cut).

**An index (D-31, ADR 0045).** Every execution records its template and
logical cost; the write path keeps a distinct-count sketch and histogram per
extracted column instead of `ANALYZE`. Per collection, the top templates by
frequency × cost yield candidates in equality → range → order → covering
order; a hypothetical-index compile check discards what the planner would
not use; a bandit picks the next index by uncertainty-weighted measured
benefit under the write budget. It builds invisible in `seq` order with a
watermark (queries meanwhile get a hybrid plan or `IndexBuilding`), is
verified in shadow against the templates on logical cost, and only then
becomes visible, journaled so every Region follows. Any later regression
flips it invisible at once; unused indexes retire; prefixes merge. Nothing
is asynchronous on the read side: a visible index is maintained in the
write's own transaction.

### Repositories (D-33: multi-repo)

Eleven Forgejo repositories under `TerraScale/`, each with a `develop` trunk:

| Repository | Language | What it is |
| --- | --- | --- |
| `terrabase` | Rust | Engine + public API + sync + replication + backup + control gRPC. `terrabase-go/` inside it is the Go client for the private control protocol. **Publishes the contract artifacts.** |
| `ts-typescript` | TypeScript | `@terrascale/sdk`: data-first client (root), typed SQL builder, `./react`, `./tanstack`, `./management`, `./better-auth`. Generated from the pinned contract. |
| `ts-dotnet` | C# | `TerraScale.Sdk` on NuGet: same core surface as `ts-typescript` (D-13). |
| `ts-go` | Go | `terrascale.tech/sdk`: same core surface (D-13). |
| `ts-rust` | Rust | `terrascale` crate: same core surface (D-13). |
| `ts-dashboard` | TypeScript (Vinext on Cloudflare Workers) | Customer product UI and the TerraScale admin console (D-79), StyleX design system (D-80), Better Auth identity, orgs/projects, Stripe billing, MCP server; stateless in every deployed environment, with a disposable persistent SQLite store only for local development until the control-plane database is ready (D-59). |
| `ts-cli` | Go | `terrascale` CLI: `dev`, `push`, `diff`, `codegen`, `import`, `db export`, `db`, `tenants`, `index wait`, `doctor`. |
| `ts-orchestrator` | Go + embedded NATS | Our Kubernetes alternative (D-06): the single automation authority for OCI and Cloudflare (D-67); ships `ts-node`, the per-host agent that runs binaries under systemd (D-69). |
| `ts-deploy` | Go | The one CI/CD program (D-68): every repository's workflow downloads its latest release and runs it. |
| `ts-proxy` | TypeScript | `api.` REST first-contact Worker plus the `sync.api.` Snippet source (D-58). |
| `ts-docs` | — | Product docs site source (runnable snippets, D-56) and the ADRs that are still true. |

**Contract flow (D-33, section 5).** terrabase publishes `openapi.json`, the
MessagePack manifest, and the TBEP protos as a versioned package on every
green `develop`; consumers pin `CONTRACT_VERSION` and regenerate with
`make contracts`; CI fails on drift.

---

## 3. Closed decisions

Owner decisions. Numbered in the order they were made; the number is an
identifier, not a priority. Superseded rows say so and point forward.

| # | Area | Decision |
| --- | --- | --- |
| D-01 | Languages | Go for backend services and tooling (`ts-cli`, `ts-orchestrator`, `ts-deploy`); `terrabase` (engine **and public API**, D-34) is Rust; `ts-dashboard` is TypeScript on Vinext deployed as a Cloudflare Worker (D-59); `ts-proxy` is TypeScript (a Worker and a Snippet, D-58): Cloudflare's edge does not run Go. Client SDKs are written in their own language (TS, C#, Go, Rust; D-13); C#/.NET exists nowhere else. |
| D-02 | TBEP | protobuf/gRPC over HTTP/2, TLS 1.3 mTLS, SPIFFE identity: the **private control protocol only** (placement, health, drain, admin, dev-only faults). It carries no customer request path. |
| D-03 | Go HTTP framework | Moot for the data path (D-34). Go tools use the standard library or Fiber v3 as they already do. |
| D-04 | Cloud | OCI holds the live source of truth; encrypted native TerraBase backups may also exist in Tigris when selected by backup policy (D-29), the explicit exception to OCI-only durable storage; Ampere ARM64 default (x86_64 supported). Cloudflare provides DNS, TLS, WAF, the `ts-proxy` Worker and Snippet, the dashboard Worker, and the CDN copies of authorized reactive data. **Cloudflare plan: Pro for the zone + Workers Paid; no Enterprise** (D-63). No other compute cloud or PaaS; Tigris is an approved S3-compatible backup destination. |
| D-05 | Regions | Launch with **GRU, SFO, FRA**. Design for 20; build, test, qualify only 3. |
| D-06 | Orchestrator | `ts-orchestrator` is our own control plane (no K8s): GRU global authority, embedded JetStream control storage, binaries under systemd system units, no container runtime (D-69). Greenfield bootstrap may run a **temporary, pre-customer GRU R1**. SFO and FRA then join as synchronized candidates and one fenced transition promotes the accepted control epoch to a cross-Region R3 without regression; launch and production never downgrade to R1. The permanent global R3 remains in GRU/SFO/FRA. GRU starts first and hosts the single active global master, which manages all regional orchestrators. Every Region runs its own regional orchestrator from initial admission to manage its OCI resources and TerraBase members. Every TerraBase member machine runs a local `ts-orchestrator` alongside TerraBase, including the GRU master machine. Host-local processes coordinate regional work under fenced authority; only selected processes host global control-store replicas. Regions 4–20 start with two member hosts and use the global R3 for authoritative control state without joining it; a Region may gain a separate regional R3 only after it has ≥ 3 eligible hosts. Non-goal: K8s/Nomad/ECS APIs. |
| D-07 | Deploy | Superseded by D-68: `ts-deploy` is the whole CI/CD logic, runs in Forgejo Actions, and never owns rollout (the orchestrator does, D-67). |
| D-08 | Identity & billing | Better Auth owns identity (dashboard), storing its data in TerraScale through the SDK adapter. Stripe via the Better Auth Stripe plugin. Polar is dead. |
| D-09 | Repos | **Superseded by D-33.** |
| D-10 | Process | Lean. Done = code + tests + CI green + short doc when non-obvious. No receipts, evidence ledgers, or signed reviews. SOC 2 post-launch. 7-day soak. |
| D-11 | Authority | This file is the workspace authority. Every repository `GOAL.md` is a comprehensive, repository-specific projection of all applicable workspace decisions and direction. It may add stricter local requirements but may not contradict, narrow, or silently omit applicable workspace policy. README files route readers to the two goals. |
| D-12 | Team | One owner + agents in parallel. Tasks are small, self-contained, independently verifiable. |
| D-13 | SDKs | **Four launch SDKs: TypeScript, C#, Go, Rust** (`ts-typescript`, `ts-dotnet`, `ts-go`, `ts-rust`). Every SDK is generated from the pinned contract (types, routes, MessagePack frames) plus a small hand-written core with the same surface in every language: document CRUD, the typed SQL builder (D-78), raw parameterized SQL, `subscribe`/`execute`, checkpoints and typed resets, region pinning, tenants, management. terrabase publishes an **SDK conformance suite** (HTTP and reactive fixtures) with the contract; an SDK is not done until it passes. Framework adapters (React, TanStack, Better Auth, Next starter) are TypeScript-only at launch. |
| D-14 | Reactive protocol | One generated MessagePack contract serves a **CDN-first HTTP delta log** and an optional WebSocket. `.subscribe()` uses bounded HTTP long-polling (25 s window) on `sync.api.terrascale.tech` by default; WebSocket is opt-in because it cannot be cached or collapsed; SSE is not offered for the same reason. An authorized `POST /api/v1/reactive/views` on the member creates or attaches to a canonical shared view and returns an opaque view id, snapshot chunk URLs, log URL, checkpoint, and a **view capability**: HMAC-SHA256 over view, generation, authorization scope, entitlement epoch, and a 10-minute expiry bucket (one-bucket overlap), **identical for every authorization-equivalent client** so their requests share one cache key. The capability travels in a request header, never in the URL or cache key. `GET /api/v1/reactive/views/{view}/{generation}/log?offset=<revision>` is the shared cache key. **Delta cadence**: data-bearing responses for one view are emitted at most every 100 ms (writes within the window coalesce), so reader request rate is bounded independently of write rate. A data-bearing response for an offset is frozen, carries an ETag, and is cacheable through the view-retention window: `Cache-Control: private, max-age=0` plus `Cloudflare-CDN-Cache-Control: public, max-age=<remaining-retention>, immutable`. An empty long-poll timeout is `no-store`. Responses never set cookies or vary by authorization. Document bodies ride as raw canonical JSON bytes (D-30). Frames come from the generated manifest. |
| D-15 | MCP | Full read-write, AI-friendly, scoped by per-call authorization, tenant isolation, audit logging. No read-only ceilings or confirmation ceremonies. |
| **D-16** | **Pricing** | **Writes and storage are the only billed dimensions.** One write = one create/update/patch/delete of ≤ 4 KiB (`ceil(bytes/4 KiB)`); replicating a write to another selected region = +1 write per region, shown up front. Storage = **logical bytes** (canonical bytes of current documents + retained history, TTL-aware, measured in byte-seconds) **× regions**, at `$/GB-month`; indexes, WAL, and backups are not billed. **Unbilled within published abuse and safety guards**: reads, queries, egress, CDN delivery, fan-out, concurrent subscribers, WebSocket connections, webhook delivery, catch-up and replay, TTL expiry. Engine usage facts are the source of truth. Exceeding a guard returns a typed error and never creates a usage charge (D-64). |
| **D-17** | **Free tier** | 1 database, 100 MB, **100k writes/month**, up to 3 regions, community support. Card required at signup (Radar + rate limits); effortless to proceed without a plan; first paid month free if subscribing during onboarding. |
| **D-18** | **Launch DX** | (a) optimistic updates; (b) `terrascale codegen` typed bindings from the schema file; (c) **`terrascale dev` is a cloud dev branch**: it creates or reuses a personal branch of the project's database in the nearest Region and runs seed (`--seed ./seed.ts`), codegen watch, log tail, and `--faults` against it. No local engine or customer-database emulator ships; dev and prod use the same engine binary in the same Regions. D-59's temporary SQLite store is only an internal dashboard development dependency for dashboard-owned control-plane records, never an engine emulator or launch feature. (d) database and branch creation < 5 s. |
| D-19 | Server functions | TypeScript queries/mutations next to the data, in a sandboxed V8-isolate runtime process (workerd or equivalent) under systemd on member hosts, never containers (D-69): **first post-launch feature**, runtime chosen by ADR then. Nothing at launch may preclude them. |
| D-20 | CDN-friendly API | Every document read has a strong revision-derived ETag and supports 304. Public-rule point reads may opt into Cloudflare caching (Cache Rules, no Worker) and are purged by cache tag, **batched per zone per second** (Pro allows 5 purge calls/s × 100 tags). Private point reads are served by members directly and are never CDN-cached: at the member they cost microseconds, at a Worker they would cost $0.30 per million. Authorized reactive snapshots and immutable delta logs follow D-14/D-55 and need no purge on writes. |
| **D-21** | **Plans** | **Free** (D-17) / **Starter $5**: 1M writes + 5 GB / **Pro $50**: 100M writes + 25 GB / **Scale $500**: a support-and-compliance fee (priority support, SOC 2 report access, security questionnaires, DPA/SLA) with every write and GB billed at PAYG. Overage and PAYG: **$1 per 1M writes**, **$0.20 per GB-month per region**; both may only go down. **Branch allowance**: every org, on any plan, gets 100k writes/month + 1 GB shared across all of its branches (dev branches included) on top of plan quotas; branches beyond it bill like databases. Optional WebSocket concurrency 100 / 1,000 / 10,000 / custom and distinct-view caps (D-64) are unbilled abuse guards. |
| **D-22** | **Reactive billing** | **A write is billed once, regardless of how many shared views it affects or how many clients are attached.** Authorization-equivalent subscribers share one view, one delta computation, one immutable log append. CDN hits, collapsed long polls, fan-out, catch-up, replay, and TTL removal pushes are free. The guard against view explosion is the per-database distinct-view cap (D-64), not a price. |
| D-23 | Launch posture | Waitlist beta. Before it: status page, paging + escalation, tested degraded mode (data path serves while control plane is sick). Billing real from day one; $50 credits per beta account; data always durable. Open registration after ≥ 4 weeks, ≥ 25 active external projects, two clean billing cycles, no open Sev-1. |
| **D-24** | **Domains** | `terrascale.tech`. `api.terrascale.tech` is the `ts-proxy` REST entry point; `sync.api.terrascale.tech` is the global CDN-backed reactive HTTP entry point (Snippet + Cache Rules, no Worker); `gru|sfo|fra.api.terrascale.tech` address members for pinned REST, view attach, and optional WebSocket traffic. `app.` is the dashboard Worker (D-59), plus `docs.` and `status.`. Regional `sync.api.` hostnames are gone (D-52). |
| **D-25** | **Limits** | PITR retention Free 1 / Starter 7 / Pro 30 / Scale 35 days. Databases per org 1 / 5 / 25 / custom soft limit. Max document 1 MiB. **History retention default 7 days, configurable per collection 1–30 days** (plan-capped at the PITR figure), billed as storage. Typed errors with upgrade paths. |
| D-26 | Email | OCI Email Delivery through its **HTTPS submission API** (Workers cannot open SMTP), sender domain `terrascale.tech` with SPF/DKIM/DMARC in Cloudflare. `terrascale dev` streams dev-branch emails to the CLI log. |
| D-27 | Storage | **Mainline SQLite via `rusqlite`**, exact version pinned, amalgamation build. One file per customer database, WAL mode, one writer + read pool, group commit. Publication journal rows are committed in the same transaction as the write (row model in D-42). The custom LSM/WAL/compaction/RootFiles are deleted, no transition period, no backend switch. libSQL not adopted. |
| D-28 | Write scale | One writer per SQLite file per region, always. Hot databases split by **key range** into more files with journal catch-up and atomic cutover (trigger: > 10k writes/s for 5 min, > 10 GB, or p99 breach for 15 min). Never intra-region multi-master. The hash-bucket `src/sharding` model is deleted. |
| D-29 | Backup | **Rebuild Litestream as an internal, native Rust subsystem of TerraBase** (ADR 0046). Read and study [Litestream source](https://github.com/benbjohnson/litestream), pin the reviewed revision, and improve its design for our multi-database member. No external Litestream binary, Go runtime, subprocess per database, or runtime dependency on upstream. Share bounded background scheduling, buffers, upload clients, and coordinated WAL/checkpoint capture across databases; maintain independent backup retention and per-database isolation. Faster backup/restore and lower CPU, memory, disk I/O, and object-request overhead are required engineering goals, verified against upstream and the current integration in `terrabase/bench/` before claiming gains. **Completely remove Litestream** from runtime, dependencies, packaging, configuration, and supervision when the native rebuild lands; no shim or fallback. One logical stream per file sends every snapshot, incremental segment, and restore manifest to **one or both S3-compatible providers: OCI Object Storage and Tigris**, selected by plan policy with an explicit operator-managed override for a specific user; the user override takes precedence and resolves to one effective policy per owned database, never per requesting collaborator. No empty destination set is allowed. OCI uses the same Region; Tigris uses an explicitly configured location compatible with the database residency policy, verified before admission. Maintain independent durable upload frontiers and retries; full backup readiness requires complete verified restore chains on every provider selected by the effective policy. A provider outage marks backup degraded and preserves pending work without blocking RF=2 write acknowledgements. Restore independently from either provider and test loss of each. Use **sync interval 10 s** (durability comes from the RF=2 follower; the backup tier's RPO is 10 s), OCI lifecycle rules move segments older than 24 h to Infrequent Access and older than 7 days to Archive; Tigris enforces the same logical retention and deletion through supported mechanisms without assuming OCI storage classes exist there. DR + PITR + **branching**: never replication, never hot path. Restore lands as a new database. Encrypted under the same per-database key. Time-travel reads are post-launch (D-40). |
| D-30 | Bytes everywhere | A document is parsed once (write, `simd-json`): validate, canonicalize, extract index fields. Afterwards the body is opaque canonical JSON bytes: BLOB in SQLite, `bytes` on every internal boundary, spliced into HTTP responses, shipped as-is in replication, raw inside reactive frames. Exceptions: index backfill, CRDT conflict merge. Public consequence: reads return semantically, not byte-, identical JSON. |
| D-31 | Auto-indexing | Non-negotiable, RavenDB-class or better. An index must never serve a stale read. A candidate activates only when shadow replay finds no statistically significant logical-work regression and neither p95 latency nor pages read / rows examined regresses by more than 5% against the pinned no-index baseline. Templates (normalized SQL) are weighted by frequency × logical cost and compressed to the 95 % set (ISUM); candidates follow equality → range → order → covering, grown by Extend's benefit-per-byte rule; a compile check plans against hypothetical indexes (`sqlite3_expert` technique); selection is a **contextual combinatorial bandit** (DBA Bandits / C²UCB, UTune uncertainty weighting) rewarded by measured logical work saved, under a per-collection **write budget** (index maintenance ≤ 2× base write cost, cap 12). Selected indexes build **invisible** under budget with a durable `seq` watermark (hybrid plans serve during backfill), are **verified in shadow** by replaying templates with a cost cap and a Welch t-test on pages read / rows examined (Azure SQL, Oracle), then activated, activated with per-template plan pins, or abandoned and blocklisted. A live regression above the same 5% threshold for one 10-minute window makes the index invisible immediately and drops it after 24 h. Unused 7 days → invisible → dropped; prefix indexes merge. Extraction in Rust at write time (never SQLite expression indexes); maintenance in the same transaction = **zero stale reads**. Descriptors and lifecycle state are journaled so Regions agree. Access rules only reference extracted fields; saving a rule auto-creates its index. Every transition is audit-trailed with evidence; one-click apply, `pin`, `forbid`, `auto: on/recommend-only/off`. Design: terrabase ADR 0045. |
| D-32 | Single branch, no PRs | Per repository: all work happens on the local `develop` branch of one checkout; no feature branches, no worktrees, no PRs. Pull/rebase before every push; verification set green locally before every push; red `develop` stops the line and anyone may revert. PRs return at open registration, at the owner's call. |
| **D-33** | **Multi-repo with published contracts** | Eleven repositories per section 2; no monorepo. terrabase is the source of truth for every cross-repository contract (OpenAPI, MessagePack manifest, TBEP protos) and publishes them as versioned artifacts to the Forgejo package registry; consumers pin a version and regenerate with `make contracts`. Before launch, replace retired greenfield contracts directly and update every consumer together. Published launch contracts follow D-73; producer-first generation and exact artifact pins apply throughout. |
| **D-34** | **Terrabase is the public API** | The member process serves the public REST + reactive API directly with axum. There is no separate API gateway or gateway-to-engine hop. OpenAPI is generated from the Rust routes, Scalar displays every endpoint, and the SDK is generated from that OpenAPI. |
| **D-35** | **Replication topology** | **Inside a region**: the RF=2 copy is a **physical WAL-frame follower** of the writer's SQLite file. `RegionalDurable` acks when the follower has fsynced the frame. Promotion = open the file under a new replica epoch. No logical replay, no CRDT, byte-identical. **Across regions**: **leaderless active-active**: every region has its own writer per database; a journal tailer ships committed journal rows (canonical body bytes + causal metadata) over pooled QUIC lanes; the receiver applies clean rows as bytes in causal order (D-37) and merges true conflicts (D-36). Anti-entropy by range-based set reconciliation (D-45); region bootstrap from a native TerraBase backup snapshot then tail. Detailed design and research base: terrabase ADR 0044. ADR 0023's intra-region two-writer state machine is superseded. |
| **D-36** | **CRDT granularity** | **Document-level causal metadata by default**: one dotted version + HLC per document. A clean apply writes bytes. A concurrent conflict parses both bodies and merges per field with remove-wins map semantics and HLC tiebreak; losers go to history with a deterministic explanation. Merge is a pure function of the set of observed rows (delta-state CRDT, Melda-style deterministic reconstruction), so delivery order and duplication never matter. Collections may declare a **merge schema** enabling typed CRDT fields (PN-counter, add-wins/remove-wins set, MV register) from ADR 0022, and post-launch a `text` field merged by Eg-walker event-graph replay (EuroSys 2025); only those collections store per-field causal kernels. |
| **D-37** | **Consistency under leaderless** | Leaderless everywhere (no home-region or single-writer mode). The cross-Region model is **causal+** (COPS): a Region applies a remote row only once every row in its causal context is applied, and concurrent rows converge by the D-36 merge. Documented publicly: unique indexes and conditional writes are enforced **within a region**; cross-region uniqueness is best-effort and conflicts are surfaced in history; `GlobalDurable` acks only when the row and all its causal dependencies are durable in every selected Region and fails closed with < 2 active regions (UNISTORE, ADR 0035); the client **session token** is its causal frontier (Occult): read-your-writes + monotonic reads by default, `eventual` or `writes-follow-reads` per request, never linearizable. Design: terrabase ADR 0044. |
| **D-38** | **Sync scope** | Two modes over the same durable delta-log contract. **Server live query** maintains the complete result of a live-subset SQL query (D-78) and emits incremental result deltas: bounded results, authorization-sensitive queries, ordering, search. **Collection sync** maintains an authorized outer subset that a local store such as TanStack DB narrows with client-side live queries, joins, and grouping; it is the recommended pattern because it keeps distinct-view counts low (D-64). Both use CDN-backed HTTP long-polling by default and support checkpoint/resume/reset and optimistic reconciliation. WebSocket is an explicit transport override. No offline writes or full client replicas at launch. |
| **D-39** | **Launch additions** | Because SQLite makes them cheap, these are IN launch: **database branching**, **full-text search (FTS5)**, **change streams → webhooks**, **read-only SQL over HTTP** (D-54). |
| **D-40** | **Launch cuts** | Deleted from product, API, SDK, dashboard, and docs: **project queues**, **"repositories"** as a concept, **database environments** (branches replace them), the public `@terrascale/sdk/local` emulator, any local terrabase distribution (D-18/D-50), **PostgreSQL import** (D-60), **Regional Services hostnames** (D-52). D-59 permits one non-public, explicitly enabled SQLite backend only while TerraBase cannot persist the dashboard's control-plane records; it is deleted at the readiness gate and never ships. **Post-launch (roadmap, not deleted from design)**: vector search (sqlite-vec), time-travel reads, cross-tenant admin queries, Vercel/Netlify/Cloudflare deploy integrations, Expo and React+Vite starters. Kept: saved queries, passkeys, agent auth, notifications, per-query cost attribution. |
| **D-41** | **Plan and CI hygiene** | This file stays under ~1000 lines; no status, no commit hashes, diff digests, review verdicts, or "frozen"/"preserve" instructions appear here. Each repository's portable **format + lint + unit/integration test + build** set is `make check`, and CI runs it unchanged. Provider or privileged-host qualification may be an additional named gate. FIPS builds, SBOM, provenance attestation, hostile release-contract fixtures, multi-arch promotion validators, and the Windows runner gate are **post-first-customer**; existing implementations are **deleted**, not parked. |
| **D-42** | **SQLite only, and the row model** | **The LSM is removed completely: code, tests, benches, docs, config flags, the `StorageBackend` enum, the format marker.** Row model, one B-tree write per op: the publication journal is **folded into the documents table** (`seq`, `commit_ulid`, `dot`, `hlc`, `kind` columns + a covering index on `seq`); **history = journal retention** (previous rows stay reachable by `(doc_id, seq)` until the pruner passes both consumer frontiers and the collection's history window, D-25); **idempotency** is a `client_request_ulid` column with a unique partial index; **usage facts** are in-memory counters flushed once per group-commit wave as `(db, seq_hi, writes, bytes)` and reconciled from the journal on restart; **extracted index values are STORED columns** written by the Rust write path with ordinary indexes on them. |
| **D-43** | **Commit path** | One dedicated committer thread per open file with a fixed 1–2 ms wave; WAL checkpoints (`wal_autocheckpoint=0`, `PRAGMA wal_checkpoint(PASSIVE)`) on a separate thread between waves. `synchronous=NORMAL` for databases with an RF=2 follower (durability from the follower's fsynced ack, D-35); `synchronous=FULL` only for single-replica databases. Prepared statements cached per (file, shape); `mmap_size` from the member's RAM share; `cache_size` per file from the tenant budget; `temp_store=MEMORY`. Build ARM-first: `target-cpu=neoverse-n1`, NEON `simd-json`/`aws-lc`, mimalloc. |
| **D-44** | **Read path** | Responses are hand-assembled envelopes that splice body bytes; `serde_json::Value` never appears on a data path. API-key → principal is a sharded LRU with a revocation generation. `If-None-Match` is answered from the `revision` column without touching the body. Reads are **metered for abuse guards and cost attribution, never billed**. Idle databases and branches (no request for N minutes) are **closed** and their native TerraBase backup stream paused; the open-file LRU is sized by RSS, not count: thousands of idle free-tier databases and dev branches must cost ~0. |
| **D-45** | **Replication mechanics** | Intra-region follower via SQLite WAL-frame hooks (not LTX); ack on `fdatasync` of the frame batch; promotion = open the file. Cross-region: one frame per wave per peer region, zstd with a dictionary trained on canonical JSON, credit-window pipelining, same-document writes coalesced within a wave. Anti-entropy is **range-based set reconciliation** (Meyer 2023/2026, Negentropy wire format) over `(origin, seq) → row hash` with additive range fingerprints maintained in the write transaction: O(log n) rounds worst case, no per-connection state, no rigid tree; the Merkle tree is deleted. A joining region bootstraps from the peer's native TerraBase backup snapshot in **its own region's** object storage, tails, and proves equality by one full reconciliation before it serves. Members get their peer list from the orchestrator over control gRPC; NATS exists only inside the orchestrator. |
| **D-46** | **Sync mechanics** | A canonical shared-view key binds database and branch incarnation, normalized SQL plan and parameters, projection/order/limit, schema and index-plan versions, authorization predicate, rule revision, entitlement epoch, and Region. Authorization-equivalent clients attach to one view state, revision line, and bounded immutable delta log. Snapshot chunks and data-bearing log responses have deterministic boundaries and byte-identical MessagePack for the same `(view, generation, offset, protocol, encoding)` cache key. Dependencies are keyed in an interval tree by `(collection, index_id, range)`; each committed row is matched using extracted values already in hand. **Deltas are Z-sets** (DBSP): a committed row is `{(old,-1),(new,+1)}`, a view delta is the plan's incremental form applied to it; filter/projection/`MATCH` are linear (no state), `ORDER BY … LIMIT` keeps a 2k boundary buffer, joins and aggregates need integrated state and are therefore post-launch (D-78). One delta per affected shared view per cadence window is encoded and appended once. A revision is one journal cut; consistency across different views is not promised. An origin `singleflight` keyed by `(view, generation, offset)` holds one journal wait even when several upper tiers miss. HTTP, optional WebSocket, webhooks, and every attached client consume the same revisions. A checkpoint is opaque and binds view identity and generation, database/branch incarnation, schema version, authorization revision and entitlement epoch, Region/replica epoch, retained journal boundary, and expiry; any mismatch returns a typed reset reason and a fresh authorized snapshot. |
| **D-47** | **Developer velocity** | Every repository has `make check` = its verification set exactly; CI calls the same target. `make contracts` in a consumer downloads the pinned contract and regenerates; in terrabase it regenerates and publishes. `ts-typescript` and `ts-dashboard` use **Vitest + Playwright only**. `ts-typescript/test/journey.test.ts` is **the** product integration suite: a feature is not done until the journey exercises it; it runs in terrabase's CI against real members and on the owner's machine. The other SDKs prove parity through the conformance suite (D-13), not a second journey. Every task states done-when, verify command, files touched. Benchmarks are one command appending to `terrabase/bench/RESULTS.md`. Deletion lands before construction. |
| **D-48** | **Product & DX** | **Schema JSON is the source of truth** (D-78), authored through each SDK's typed schema builder; `terrascale push` applies it to a branch; codegen generates types for every SDK language from it. Application queries are live: a typed-builder or raw SQL query ends in `.subscribe()` (CDN HTTP by default), `.subscribe({ transport: "websocket" })` opts into the direct transport, `.execute()` is "subscribe once". Collection sync feeds the official `@terrascale/sdk/tanstack` adapter. The SDK pins REST and optional WebSockets to a healthy hosting Region, keeps reactive HTTP on `sync.api.`, carries the session token, and accepts `regionHint` per request. **Branch-per-preview** and **branch-per-developer** replace environments. **Conflict viewer** ("why this version won": dot, HLC, policy) in dashboard and `db.history(id)`. **MCP is a client of `@terrascale/sdk`**. **Live pricing calculator** fed by the org's own last-hour `usage`. Public IDs are branded `string`s; `revision` is a string; no wrapper classes. |
| **D-49** | **Stopped** | **Studio** (`terrabase-studio`) is deleted; the dashboard + `/metrics` + Grafana are the operator surface. **Spatial/R-tree** is out of launch. **Cloudflare Regional Services / Enterprise** is not pursued before launch (D-52/D-63). **A Worker on the reactive hot path** is forbidden (D-55). **PostgreSQL wire protocol and PostgreSQL import** do not exist (D-54/D-60). **Containers, images, and registries** are gone from production and CI (D-69). |
| **D-50** | **Closed source; cloud only; the binary never leaves** | The engine is not open-sourced and there is no self-host, single-binary, container, or authenticated download distribution of terrabase, to customers or anyone else. TerraScale's OCI hosts are the only place terrabase runs; local development is a cloud dev branch (D-18). |
| **D-51** | **Database-per-tenant is a first-class pattern** | Thousands to millions of small databases per project must be routine and near-free when idle (D-44). API + SDK: `ts.tenants.create(key)` → a database with placement and key template inherited from a **tenant template**; tenant-scoped API keys minted from the template; `terrascale tenants` CLI; per-tenant branching for support; per-tenant usage rows. Tenant = database; no new storage concept. Cross-tenant admin queries are post-launch (D-40). |
| **D-52** | **Regions are cheap and many; placement is residency** | A region is one ARM node class + object storage + the orchestrator's `add-region` runbook; adding one is routine work. **Placement is the residency guarantee**: live copies, restore targets, exports, and branches stay in selected OCI Regions. Backups use the policy-selected providers: same-Region OCI storage, Tigris with verified placement within the permitted geography, or both (D-29); provider-specific backup locations are explicit policy. If Tigris is selected and no compliant location can be verified, placement fails backup qualification. A **placement lock** (`eu-only`, `br-only`, or an explicit region list) is set at creation, cannot be widened, and is enforced at placement, replication, backup, restore, export, and `add-region`. Cloudflare's edge decrypts reactive traffic at the PoP nearest the reader worldwide as a sub-processor under its DPA; we do not promise in-region edge processing at launch. Post-launch opt-in: a DNS-only `<region>.direct.api.` hostname straight to in-region members (no CDN, no WAF, subscriber-capped). Three regions at launch; the 4th onward is an orchestrator command. |
| D-53 | Reserved | Reserved intentionally; do not renumber later decisions. |
| **D-54** | **HTTP SQL endpoint** | `POST /api/v1/databases/{id}/sql` is **the** query endpoint (D-78): read-only SQL over extracted columns, `body` as JSON, and `_` metadata columns, returning JSON or MessagePack; `subscribe` submits the same statement to view attach (D-14). Statements are parsed and validated with an allow-list of `select`, `with`, and `explain`; no DDL, DML, attachment, pragma, or non-deterministic functions. Authorization compiles into the plan, with row/byte/time bounds, auto-index policy, and actionable `IndexBuilding` / `NotSubscribable` errors. **There is no PostgreSQL-compatible listener, protocol emulation, or compatibility of any kind.** |
| **D-55** | **CDN-scaled reactive delivery with zero metered compute on hits** | Cloudflare CDN is the delivery network. On `sync.api.terrascale.tech` a **Cloudflare Snippet** (unbilled within the current published plan terms, 5 ms CPU) verifies the D-14 capability header with `crypto.subtle`, checks it against the URL's view and generation and the expiry bucket, strips it, and performs the canonical `fetch()` through the zone cache; Cache Rules mark snapshot and log paths eligible; cache lock collapses simultaneous misses per PoP and Smart Tiered Cache bounds origin fills across PoPs. **No Worker runs on this path**: Cloudflare bills every Worker request including cache hits ($0.30/M), which is incompatible with free fan-out. Production never uses Workers `caches.default`. Immutable snapshot chunks and data-bearing log responses are never purged on writes. Cache tags support database deletion, security purge, and retired-generation cleanup. The supported claim: database compute and origin connections scale with writes and distinct authorized shared views, not attached readers; edge cost per cache hit is zero metered compute and zero bandwidth. **Portability hedge**: the contract is plain HTTP caching semantics, so any coalescing CDN can serve it; the Snippet is the only Cloudflare-specific code. Launch gate: one million concurrent readers of one shared view at 16 writes/s, no reader-proportional member connections or RSS, one delta computation per cadence window, correct bytes for every reader, origin fills bounded by cache/tier structure, **zero Worker requests on the sync hostname**. |
| **D-56** | **Developer love is scope, not polish** | Ships with launch: `npx create-terrascale-app` (Next starter); a docs site whose every snippet runs against a real sandbox branch pre-filled with the reader's key; `terrascale doctor`; every error carries the fix (`IndexBuilding: retry in 800 ms, or terrascale index wait`); **per-query cost attribution** and **automatic index recommendations with one-click apply** in the dashboard; **schema evolution**: `terrascale diff`, resumable backfills, field renames as first-class migrations, schema version pinned per branch. Deploy integrations and the other starters are post-launch (D-40). |
| **D-57** | **Local-first stays post-launch** | Physical WAL-frame replication is reserved for an authorized whole-database replica running a format-compatible SQLite build; it is never used to deliver an access-controlled subset. Selective clients use the logical journal/result-delta path. Delivery order after launch: persistent result cache, optimistic idempotent outbox, read-only whole-database replica, then offline CRDT writes. |
| **D-58** | **`ts-proxy` = REST load balancer + the sync Snippet** | No Cloudflare Load Balancing, no OCI Traffic Management at launch. The **Worker** on `api.terrascale.tech` uses Workers KV maps for Region health and database placement, routes first-contact REST to the nearest healthy hosting Region, serves `GET /regions`, follows one `421`, and repairs stale placement; it never reads API keys or bodies. The SDK then pins REST to `<region>.api.terrascale.tech` and never returns to the Worker unless pinning fails. The **Snippet** on `sync.api.terrascale.tech` is the capability gate of D-55: it verifies, strips, canonicalizes, and fetches through the cache; it never decodes MessagePack or bodies and has no KV (the Region is encoded in the view identity). Verification keys live in Cloudflare Secrets Store and are rotated by the orchestrator. Detailed logic lives in `ts-proxy/docs/design.md`; member/SDK contract is terrabase ADR 0043. |
| **D-59** | **The dashboard is a Cloudflare Worker** | `ts-dashboard` (Vinext) deploys to **Cloudflare Workers** with vinext's native target (`@cloudflare/vite-plugin`, `wrangler.jsonc`, static assets on Workers Assets); no containers, no Node server, no other PaaS. **Deployed state**: every durable byte (Better Auth users/sessions/orgs, projects, billing records, API-key metadata, MCP audit) lives in the **control-plane database**, an ordinary TerraScale database placed in all launch Regions and reached through `@terrascale/sdk` exactly as a customer would. **Temporary local persistence**: until TerraBase implements the dashboard-required contract, `@terrascale/sdk` provides an internal, explicitly enabled SQLite backend and `ts-dashboard` uses it for persistent local development and focused local logic tests. It stores dashboard-owned control-plane records only, persists across local restarts, is resettable, and uses the same SDK-facing repository interfaces as TerraBase. It is not a customer database emulator, public SDK export, or compatibility promise; it must fail closed outside explicit local development and is forbidden in CI qualification, preview, staging, and production. Generated mocks continue to supply engine, Region, usage, and other facts SQLite does not own. **Deletion trigger**: once the published TerraBase contract can persist Better Auth, organizations/projects, billing, API-key metadata, and MCP audit and the dashboard journey passes against `make stack`, delete the SQLite backend, dependencies, configuration, data, and SQLite-specific tests from `ts-typescript` and `ts-dashboard` in the same series; no data migration is required. **Runtime rules**: `workerd` only; no Node-only modules in Worker server code, and the SQLite adapter stays behind the SDK development boundary so it cannot enter Worker or browser production graphs; `nodejs_compat` only for Better Auth and Stripe; `vinext dev` runs on workerd too. **Reaching the API**: first-contact calls go through a **service binding to `ts-proxy`** (`env.PROXY.fetch`, the SDK accepts an injected `fetch`); pinned `<region>.api.` calls are plain `fetch`. The dashboard never relays reactive bytes; its browser SDK uses `sync.api.` like any customer. **Secrets** are Worker secrets set by the orchestrator. **Email** via OCI Email Delivery HTTPS (D-26). **Stripe webhooks and MCP** are route handlers in the same Worker. **Observability**: Workers Logs + Analytics Engine, OTLP over HTTP to Grafana. **Deploy**: `ts-deploy` uploads a Worker version on every green `develop`, the orchestrator promotes it (D-67/D-68); a preview deployment on `preview.app.terrascale.tech` points at a control-plane database branch. Fallback if Workers ever blocks the product: the same build under Nitro's `node` preset as a systemd unit on GRU hosts (D-69); the "no Node-only modules" rule keeps that possible. |
| **D-60** | **Portability** | An authorized owner can export the whole logical database at any time without a support ticket. `POST /api/v1/databases/{id}/exports` creates a consistent, resumable export and the CLI exposes `terrascale db export`; the lossless versioned bundle contains schemas, index definitions, current documents, retained history, branch metadata, a manifest, and checksums; progress within 5 s, completion within a published size-based SLO. A current-data JSONL export is also available. **Import** (`terrascale import`) accepts JSONL, CSV, Mongo/Cosmos JSON, and DynamoDB export format. There is no PostgreSQL import or follow. |
| **D-61** | **Unit economics are a gate** | Section 4 carries the cost model. Every billed line must show ≥ 50 % gross margin at list price on measured (not estimated) unit costs, and every unbilled dimension must have a measured worst case under its abuse guard. Edge cost per cache hit must measure as zero metered compute. Local costs are measured at G1, the real-zone stub test runs at G2, and production costs are re-measured before G4. Cloud prices, quotas, and plan terms are planning assumptions reverified against current provider publications at G1 and G4. A pricing page never publishes a number without a measurement behind it. |
| **D-62** | **Tracks and gates replace serial milestones** | Work runs in five parallel tracks (engine, edge, product, infra, launch) with four integration gates (section 6). Immediate deletion is a per-repository prerequisite, not a global milestone; D-59's temporary dashboard SQLite bridge is the sole bounded exception and exits after E-15 before G1. Inside a track, tasks are independent and small; across tracks, the pinned contract is the only coupling. |
| **D-63** | **Cloudflare plan and fallback** | The zone runs on **Cloudflare Pro** (Snippets: 25 per zone, 2 subrequests each; purge 5 req/s; bandwidth unmetered) plus **Workers Paid** for the two Workers and KV. No Enterprise, no Data Localization Suite, no Argo. Owner obtains written confirmation from Cloudflare that small cacheable API responses at high request volume are within the CDN terms (O-4). Documented fallback if Cloudflare's terms or pricing turn: Bunny.net (request coalescing, ~$0.01/GB), served by the same HTTP contract. |
| **D-64** | **Abuse guards (unbilled, enforced, no upsell UI)** | Per database, per plan (Free / Starter / Pro / Scale), owner-tunable: read + query requests/s 100 / 1,000 / 5,000 / custom; **direct member read bandwidth** (bytes leaving OCI for reads served by members, not by the CDN) 50 GB / 500 GB / 2 TB / custom per month; distinct active shared views 1k / 10k / 100k / custom; optional WebSocket connections 100 / 1,000 / 10,000 / custom; webhook endpoints 3 / 10 / 50 / custom. CDN-delivered reactive data has no bandwidth guard. Exceeding one returns a typed error with the fix; nothing is billed. Subscribers per view have no plan upsell cap and remain unbilled within published abuse and safety guards. |
| **D-65** | **Launch estate: as cheap as possible** | Per Region **2 member hosts** (`VM.Standard.A1.Flex`, 2 OCPU / 12 GB, Balanced block volumes), no dedicated control hosts (D-06), no self-hosted Prometheus/Grafana (Grafana Cloud free tier with OnCall, fed by an agent on each member host), status page as a Worker static asset, Tailscale free tier, OCI default vault with software-protected keys (never a Virtual Private Vault), CI runner on the owner's workstation until a preemptible A1 runner pays for itself. Tenancy is pay-as-you-go with **GRU as home region** so the Always Free 2 OCPU / 12 GB and 200 GB block storage cover one GRU member. Target fixed floor ≈ $205/month for three Regions (≈ $70 for GRU only). Placement refuses a Region below 2 member hosts. Every Region has its own regional orchestrator under the GRU master, with a local `ts-orchestrator` process on every TerraBase member machine. Regions 4–20 do not join the global R3 but use it for authoritative control state; a separate regional R3 requires ≥ 3 eligible hosts. Every added host is justified by measured saturation, not a plan. |
| **D-66** | **Network cost model** | Replication is member-to-member QUIC over the public internet; **Cloudflare never carries replication, backups, or control traffic** (metered and uncacheable; D-04 permits Tigris for encrypted backups). Costs: intra-Region traffic (RF=2 follower, OCI backup leg) is free; policy-selected Tigris uploads incur separately accounted OCI outbound and Tigris storage/request/retrieval costs, verified against current pricing; cross-Region journal frames and bootstrap snapshots bill as OCI outbound at the source Region's rate (NA/EU $0.0085/GB, GRU $0.025/GB, first 10 TB/Region/month free) ≈ $0.01 per million replicated 1 KiB writes; direct member reads bill the same way (~1.5 KB each) and are bounded by the D-64 bandwidth guard; CDN fills are bounded by tiers; reader-side delivery is Cloudflare's, at $0. Every response is compressed (MessagePack, zstd/brotli on REST) and replication frames use the trained dictionary; egress per Region is a Grafana alert at 8 TB/month. |
| **D-67** | **The orchestrator automates OCI and Cloudflare end to end** | `ts-orchestrator` (Go, embedded NATS JetStream) is a **reconciler**: the whole estate is declared as typed Go configuration in its repository (Regions, host counts and shapes, buckets and lifecycle rules, vault keys, NSGs, DNS records, Cache Rules, Snippet rules, Worker routes, KV maps, WAF and rate rules, capability keys, certificates), and the running orchestrator continuously drives OCI and Cloudflare to match it and alerts on drift. **Nothing is clicked in a console after O-4** (account, plan, and payment setup are the only human steps). `terrascale-orchestrator bootstrap` runs once from the owner's machine with an OCI API key: it creates the compartment, VCN, NSGs, vault, buckets, and the first two GRU hosts with a cloud-init that installs only `ts-node`; the hosts fetch the orchestrator and TerraBase and start the temporary pre-customer GRU R1. `add-region sfo` and `add-region fra` synchronize candidate replicas and atomically promote control storage to the permanent cross-Region R3 without regressing its accepted epoch; the bootstrap credential and path lose authority after handoff. Later Regions are one config entry plus a release and each starts its own regional orchestrator under GRU authority, using the global control store unless it qualifies for a separate regional R3 under D-06. **Delivery**: the orchestrator's signed delivery API accepts release candidates from `ts-deploy` (D-68) and rolls them out itself: terrabase to a GRU canary host, then host by host across Regions with drain, health, and catch-up gates and automatic rollback; Worker versions promoted through Cloudflare's gradual deployments API; Snippet code and zone rules through the Cloudflare API; the same run also publishes KV placement and rotates keys. Only the orchestrator holds Cloudflare tokens with write scope beyond Worker version upload. Green `develop` is production: push → serving target < 15 min for terrabase, < 5 min for Workers. **Self-healing is the default**: a failed host is replaced automatically (new host, `ts-node`, follower resync from the surviving RF=2 copy or native TerraBase backups, placement updated, KV updated); a failed drain or health gate rolls back automatically; a full disk, expired certificate, stale KV map, or missing bucket lifecycle rule is repaired by the reconciler, not a runbook. **Paging fires only on customer-visible SLO burn (D-75)**; cause-level alerts are dashboards, never pages. Runbooks exist only for what the reconciler cannot do (OCI account issues, Cloudflare account issues, data-loss decisions). |
| **D-68** | **`ts-deploy` is the only CI logic; workflows are five lines** | Every repository's `.forgejo/workflows/ci.yml` does exactly: checkout, download the latest `ts-deploy` release binary from the `ts-deploy` repository's releases, run `ts-deploy ci --repo <name>`. No other steps, matrices, profiles, or repository-specific YAML, and no `.terrascale/deploy.json`; the repository kinds (Rust engine, Go binary, Worker, TypeScript package, docs) and their check, build, test, publish, and hand-off steps live in Go in `ts-deploy` and change in one place. `ts-deploy ci` runs the portable verification set (section 5) with its independent steps in parallel. On every `ts-orchestrator` `develop` push, it also runs the complete six-VM Firecracker `make vm-test` on the native Linux/KVM runner before it may submit or deploy a candidate. After those gates, a `develop` run additionally builds release artifacts for linux/arm64 (and amd64 where needed), signs them (ed25519), publishes binaries and contracts to the Forgejo package registry and releases, uploads Worker versions with `wrangler versions upload` (no traffic), and submits the candidate to the orchestrator's delivery API (D-67). **Caches are `ts-deploy`'s job, never the workflow's**: on the persistent `terrascale-builder` runner (host mode, no job containers) it owns `/var/cache/terrascale/{cargo,target/<repo>,go-mod,go-build,npm,toolchains}`, exports `CARGO_HOME`, `CARGO_TARGET_DIR`, `GOMODCACHE`, `GOCACHE`, `npm_config_cache` to every step, installs and caches the toolchain each repository declares (`rust-toolchain.toml`, `go.mod`, `.node-version`), **installs every missing dependency itself** before the first step (`ts-deploy doctor --fix` runs implicitly): rustup with the `aarch64-unknown-linux-musl` target, Go, Node, `wrangler`, `vp`, `buf`, `protoc`, Playwright browsers, `fj`, and the apt packages a kind needs (`build-essential`, `clang`, `lld`, `musl-tools`, `pkg-config`, `zstd`) through the runner user's apt-only sudo, so a blank Ubuntu host with `git` and `ts-deploy` becomes a working `terrascale-builder` on its first job; keys caches by toolchain version, prunes by size and age at the end of every run (`ts-deploy cache status|prune`), and falls back to a cold build with a warning if a cache is corrupt. Nothing about caching appears in any `ci.yml`. **`ts-deploy` ships itself**: its own workflow is the same five lines with `go run ./cmd/ts-deploy ci --repo ts-deploy` in place of the download; that run checks, builds linux/arm64 + amd64 static binaries, tests, runs the full `ci` against a fixture of every repository kind (a bad release breaks every pipeline), then on green `develop` signs the binaries, creates the Forgejo release with a semver tag derived from the commit, publishes the checksums, and the next push in any repository picks it up. A repository may temporarily pin `TS_DEPLOY_VERSION` as the only permitted workflow variable; a bad release is fixed by shipping the next one, never by editing a workflow. Never a pipeline engine, never rollout state, never a cluster connection. |
| **D-69** | **No containers: binaries under systemd on plain Linux** | OCI hosts run Ubuntu LTS arm64 minimal with **no container runtime installed** (no podman, docker, containerd, OCI images, or registries anywhere in production or CI). Cloud-init performs the initial OS and `ts-node` setup. After handoff, `ts-node` is the only component allowed to mutate TerraScale runtime state: it downloads signed release binaries into `/opt/terrascale/<component>/<version>/`, verifies signature and sha256, writes hardened systemd units (dedicated user, `ProtectSystem=strict`, `ReadWritePaths` to the data volume only, `NoNewPrivileges`, `CapabilityBoundingSet=`, `SystemCallFilter=@system-service`, `RestrictAddressFamilies`, cgroup v2 `CPUWeight`/`MemoryMax`/`IOWeight`), flips a `current` symlink, and restarts under orchestrator-coordinated drain. The base OS still owns normal boot, systemd, networking, time synchronization, and security updates. Rollback is the symlink back plus restart. TerraScale-managed services on every member machine are exactly `ts-node`, `terrabase`, local `ts-orchestrator`, and the Grafana agent. Only selected local orchestrators host authoritative control-store replicas; no separate server is provisioned just for orchestration. Unattended security updates use drain-coordinated reboots. Isolation between tenants is terrabase's job (scope, admission, budgets), not the kernel's. **Clocks**: chrony against OCI's NTP on every host; members export measured offset as a metric, refuse new writes when it exceeds 500 ms (HLCs are never minted from a suspect clock), and bound HLC drift to that figure. **Architecture**: arm64 (Ampere A1) is the optimized default (`target-cpu=neoverse-n1`); every release also ships amd64, and the declared estate names `VM.Standard.E5.Flex` as the fallback shape the reconciler uses automatically when A1 capacity is unavailable, so a capacity shortage never blocks `add-region` or a host replacement. |
| **D-70** | **Key hierarchy and crown jewels** | One OCI Vault software KEK per Region wraps a **per-database DEK** stored beside the database's placement record; SQLite files, WAL, native TerraBase backup segments, exports, and branches are encrypted under the DEK; account deletion = destroy the DEKs (crypto-shred, L-4). The **capability HMAC key** rotates every 24 h with one-bucket overlap, pushed to members and Secrets Store by the orchestrator. API keys are stored hashed (argon2id), shown once, prefixed for leak scanning. **Crown jewels**: the orchestrator's Cloudflare and OCI credentials, the release signing key, and the Forgejo admin account; each has hardware 2FA, lives in exactly one place, and has a written rotation procedure before G2. `security.txt` and a disclosure address ship with the docs site; a paid external pentest is post-first-customer. |
| **D-71** | **Backups are proven, not assumed** | The orchestrator runs a **daily restore drill**: it restores a random sample of databases (and the control-plane database) independently from OCI Object Storage and Tigris native backups into scratch branches, verifies checksums and row counts against the journal, and deletes the branch; a failed drill pages. JetStream control state is snapshotted daily to Object Storage in every Region. Release artifacts accepted by the delivery API are **mirrored into each Region's bucket** before rollout starts, so a Forgejo outage can block a new deploy but never strand one mid-way. |
| **D-72** | **Metrics stay cheap** | No per-database, per-tenant, or per-view label on any Prometheus metric; cardinality is bounded by host × Region × subsystem so Grafana Cloud's free tier holds. Per-database and per-tenant numbers are engine **usage facts** (D-42), queried from the control-plane database and shown in the dashboard. Logs are structured, sampled above 100 lines/s per host, retained 14 days. |
| **D-73** | **API compatibility promise** | `/api/v1` is additive-only from launch: fields and endpoints are added, never renamed or removed; deprecations are announced in the changelog and kept ≥ 12 months; a breaking change uses a separately versioned endpoint served side by side. The SDK follows semver; a major bump is a product event, not a routine. The contract publish (D-33) fails CI on a non-additive diff. |
| **D-74** | **Time to first live query < 5 minutes** | From `npx create-terrascale-app` to a browser tab updating live from another tab, on the free tier, with no configuration beyond signing in: five minutes, measured on a fresh machine before G4 and kept as a launch metric. The starter ships an `AGENTS.md` for the customer's repository (how to use the SDK, the schema builder, the SQL builder, `terrascale dev`, MCP setup) so AI-assisted builders get a working assistant on minute one. |
| **D-75** | **Availability posture** | We publish an **SLO, not an SLA**: 99.9 % monthly API availability target per Region, measured by the external probe and shown live on `status.`; incidents get a public postmortem within 5 days. A contractual SLA with credits exists only on Scale and only post-launch. Nobody outside the owner and agents uses the product before the G4 waitlist beta (no design-partner program). |
| **D-76** | **Free-tier reaper** | A Free database with no request for **30 days** is paused (closed, native TerraBase backup stream stopped, snapshot kept) and the owner is emailed; at **90 days** idle, after two more emails, it is deleted (crypto-shred). Paid plans are never reaped. Reactivation is one request. Shown honestly on the pricing page. |
| **D-77** | **Support and commerce** | Support: a public **Discord** for everyone, **email** for paid plans, and a shared channel with response-time targets for Scale; no ticketing product, no phone. **Stripe Tax** is on from day one so every invoice is tax-correct; invoices, dunning, and receipts are Stripe's. **Accepted single-provider risk**: every customer byte lives in one OCI tenancy (D-04); a tenancy-level failure is a documented, accepted risk revisited at the first $10k MRR, not before. |
| **D-78** | **SQL is the only query language; TQL is deleted** | Every collection is a SQL table: schema-declared (extracted) fields are typed columns, the document is `body` addressable with SQLite JSON operators (`->`, `->>`), and metadata is exposed as `_id`, `_rev`, `_hlc`, `_created`, `_updated`. Queries are SQLite-dialect SQL, read-only (D-54 allow-list); **writes stay document operations** (create/update/patch/delete/bulk through the API and SDKs) so CRDT semantics, history, and the write unit (D-16) remain exact; there is no SQL DML. **Live subset**: a `SELECT` over one collection with `WHERE` on columns or JSON paths, `ORDER BY`, `LIMIT`, and FTS5 `MATCH` is subscribable and maintained incrementally by the sync engine (D-38/D-46); joins, aggregates, window functions, and subqueries are `execute`-only at launch and return `NotSubscribable` with the fix. Auto-indexing (D-31) derives descriptors from SQL predicates and order keys; access rules compile into the plan. **Schema JSON is the contract**: `terrascale.schema.json` (collections, fields and types, indexes, TTL, history window, merge policy) is what `terrascale push` applies and what codegen reads; every SDK ships a typed schema builder that emits it (`defineSchema` in TS, attributes in C#, a builder in Go, a derive macro in Rust). **The happy path never concatenates SQL strings**: every SDK ships a typed query builder over the generated schema types that compiles to parameterized SQL; raw parameterized SQL is always available. Cross-tenant admin queries (post-launch) are `FROM tenants.<collection>`. |
| **D-79** | **One dashboard, two surfaces: customer product and TerraScale admin** | `ts-dashboard` at `app.terrascale.tech` is both the customers' product UI and our operator console at `/admin`, one codebase and one design system. **Admin access**: Better Auth `staff` role granted only through the control-plane database by an owner, passkey required, Cloudflare WAF rule restricting `/admin` to Tailscale egress IPs, every admin read and action audited with actor and reason. **Everything terrabase and the orchestrator know is visible there**: Regions and hosts (shape, arch, health, clock offset, disk, version), members and placement per database, replication lag per Region pair and dependency-queue depth, RBSR reconciliation runs, SLO burn and error budget, rollout status (candidate → canary → Regions) and versions per host, reconciler drift, KV placement map, Cloudflare cache hit ratio and Worker metrics, tenants and databases (search, plan, usage, spend, guards hit, reaper state), revenue and the D-61 unit-economics gate live, auto-index engine actions and regressions, backup and restore drill results (D-71), abuse-guard hits, incident notes. **Data path**: the orchestrator publishes estate state as documents into the control-plane database (`estate.regions`, `estate.hosts`, `estate.databases`, `estate.rollouts`, `estate.slo`, `estate.reconcile`, updated on change and every 10 s), and terrabase exposes admin-scoped usage and index facts through the API, so the admin console reads everything through `@terrascale/sdk` live queries like any customer app and needs no new transport. **Admin actions at launch** are product-level: pause/resume database, set or lift guards and spend caps, extend a trial, refund, reaper exemption, feature flags, read-only impersonation ("view as", audited, banner shown). Infrastructure actions (drain, replace, add-region, rollback) stay in the orchestrator CLI. |
| **D-80** | **StyleX design system; the Worker stays fast** | **Styling**: `@stylexjs/stylex` compiled at build time with `@stylexjs/unplugin` (before the React plugin, `useCSSLayers: true`, output appended to the single CSS asset served from Workers Assets, no runtime style injection in production). **Tailwind, shadcn/ui, `class-variance-authority`, `clsx`, `tailwind-merge`, `tw-animate-css`, and `components.json` are deleted.** Our own design system lives in `src/design/`: `tokens.stylex.ts` (`defineVars`: neutral ramp, semantic surfaces, accent, status, chart, glass, shadow, radius, typography; dark values are the defaults, `lightTheme = createTheme(...)` applied on `<html>` together with `data-theme` atomically), `consts.stylex.ts` (`defineConsts`: breakpoints, durations, z-index, anatomy constants like 36 px controls), `keyframes` for motion, and primitives in `src/components/ui/` rebuilt on headless `radix-ui` + `cmdk` with StyleX (Button, Panel, DataTable, FormField, Dialog, Sheet, Tabs, Tooltip, Badge, Toast, ChartShell, Sidebar). **Rules**: no `className` strings and no `style={{}}` except StyleX dynamic styles for true runtime values (chart dimensions); variants are object lookups, composition is a `style` prop merged last; every value comes from tokens or consts; `@stylexjs/eslint-plugin` (`valid-styles`, `no-unused`, `valid-shorthands`, `sort-keys`, `enforce-extension`) is part of `make check`; `stylex.when` for relational states; `docs/design-system.md` is rewritten for StyleX and remains the source of truth. **Look and feel is preserved exactly**: Playwright visual snapshots of every route in dark and light at 390 / 1024 / 1440 px are captured before the migration and gate it (≤ 0.1 % pixel difference), and a token audit proves each `--ts-*` value maps to one `defineVars` entry. **Performance on Workers**: server components by default and client components only for interaction; budgets enforced in `make check`: ≤ 120 KB gzipped client JS on any route, ≤ 3 MB compressed Worker bundle, CSS ≤ 60 KB; `recharts`, `dotted-map`, and the SQL editor are lazy-loaded; fonts self-hosted and preloaded; static assets immutable-cached; Smart Placement on (the control-plane database lives in OCI); one SDK client per request; streaming SSR; no polyfilled Node modules beyond `nodejs_compat` for Better Auth and Stripe; Lighthouse ≥ 90 on every route on a throttled profile as a G4 check. |
| **D-81** | **Firecracker qualifies the whole orchestrator locally** | `ts-orchestrator` must pass end-to-end qualification without OCI or Cloudflare credentials on six low-resource Ubuntu LTS systemd guests under pinned, checksum-verified Firecracker on Linux/KVM. The repository goal owns the complete VM, network, disk-fault, provider-endpoint, resource-budget, and evidence contract. `make check` stays portable; `make vm-smoke` is the focused KVM check, and every `develop` CI push must pass the complete `make vm-test` before candidate submission or deployment. Documentation-only local work does not require KVM. The native runner architecture gets the full suite while every release cross-builds both linux/amd64 and linux/arm64. Native arm64 Firecracker becomes required when an arm64 KVM runner exists; until then, the real OCI A1 gate qualifies arm64 and local results must not claim it. Firecracker proves orchestrator behavior and host integration, never OCI/Cloudflare availability or production performance. |

| **D-82** | **First-release naming** | The product is TerraScale v1 and the engine is TerraBase. The launch API remains `/api/v1`, sync is `terrascale.sync.v1` with MessagePack envelope 1.0, and the orchestrator control namespace is `ts.orch.v1`. Replace discarded pre-launch names, fixtures, and internal formats with their launch counterparts in one coordinated producer/consumer series. No aliases or old-format readers are added to preserve a discarded design. Real third-party dependency versions, operating-system terminology, revision test data, protected human-owned instructions, and archived history keep their correct meanings. |

---

## 4. Launch scope

### In the first release

**Engine and API**

- Databases, branches, and collections; document CRUD, JSON-merge PATCH,
  bounded bulk operations, idempotency keys. Transactions are single-database
  (one file); cross-database transactions never exist.
- **SQL queries** (D-78/D-54): explain, pagination, streaming results, over
  extracted columns and JSON body paths; indexes only on extracted columns
  (D-30). Auto-indexing per D-31. The live subset is defined by example in
  `terrabase/docs/sql/live-subset.md`.
- **Database-per-tenant**: tenant templates, tenant-scoped keys (D-51).
  **Placement lock** enforced at placement, replication, backup, restore,
  export (D-52).
- **Full-text search** (FTS5 `MATCH`) with its own auto-index kind.
- Schemas (optional, with merge schemas per D-36) and resumable migrations,
  authored through any SDK's schema builder as schema JSON and applied with
  `terrascale push` (D-48/D-78).
- Live queries and collection sync over canonical shared views: subscribe,
  checkpoint, resume, reset; MessagePack over CDN-backed HTTP long-polling by
  default, optional direct WebSocket (D-14, D-38, D-46). View capabilities per
  D-14; 100 ms delta cadence.
- Per-document history with deterministic conflict explanations (7-day
  default, D-25).
- Replication per D-35: RF=2 per region, up to 3 regions per database,
  `RegionalDurable` / `GlobalDurable`, session tokens for read-your-writes.
  Default placement is one Region; more are explicit and priced (D-16).
- Online key-range shard split (D-28).
- Native Rust backups, PITR, **restore-to-new-database, branching** (D-29).
  Per-database encryption keys (OCI Vault).
- **Change streams → webhooks** with retries and signing (D-39).
- Document TTL with free expiry. Write and storage metering per D-16 end to
  end; read metering for guards and attribution only.
- Declarative access rules (CEL) with bring-your-own-JWT so browser apps talk
  to the API directly; publishable keys; abuse guards per D-64.
- Edge: `ts-proxy` Worker for REST first contact and `/regions`; Snippet gate
  and Cache Rules on `sync.api.` (D-55/D-58). Per-tenant resource governance
  on shared members.
- Whole-database lossless and JSONL export; import from JSONL, CSV,
  Mongo/Cosmos JSON, DynamoDB export (D-60).

**Product**

- SDKs (TS, C#, Go, Rust; D-13): generated types from schema JSON, typed SQL
  builder + raw SQL, region-aware REST, CDN-backed HTTP subscriptions by
  default, optional WebSocket, live queries, collection sync, tenants,
  management; TS adds the TanStack DB adapter, `./react` hooks (`useQuery`,
  `useMutation`, optimistic), and `./better-auth` (D-38, D-48, D-51).
- CLI: `terrascale dev` (cloud dev branch, `--seed`, `--faults`, log tail,
  codegen watch), `push`, `diff`, `codegen`, `import`, `db export`, `db`,
  `tenants`, `index wait`, `doctor` (D-18, D-56, D-60).
- Dashboard (customer surface): registration, orgs/projects/members, database
  and branch lifecycle with diff and preview branches, **data explorer**
  (collections as tables, document editor, filters, JSON view), **SQL console
  with live results, EXPLAIN, saved queries, and index suggestions inline**,
  history + conflict viewer, **schema viewer and `diff`**, **query
  performance** (templates ranked by logical cost and count), **indexes**
  (state machine, evidence, one-click apply, pin/forbid), **subscriptions**
  (active views, subscribers, CDN hit ratio from usage facts), Regions and
  placement on the dotted map with per-Region latency for this database,
  backups and PITR restore, import wizard (JSONL/CSV) and export, access
  rules editor with a test harness, webhooks with delivery log and replay,
  usage with per-query cost attribution and cost-anomaly alerts, billing
  (Stripe) with the live pricing calculator, spend caps + budget alerts, API
  keys, audit log, notifications, onboarding checklist tied to D-74, command
  palette (no shortcut hints), full read-write MCP built on the SDK.
- Dashboard (admin surface, `/admin`, D-79): estate map, hosts, members,
  placement, replication lag, rollouts and versions, reconciler drift, SLO
  burn, tenants and databases, revenue and unit economics, index engine
  actions, drill results, guards, incidents, flags, view-as. Deployed as one
  Cloudflare Worker (D-59) on the StyleX design system (D-80).
- Distribution: `npx create-terrascale-app` (Next) and a docs site with
  runnable snippets against a sandbox branch (D-56).
- Orchestrator: `bootstrap` and `add-region` as routine commands (D-52,
  D-67), OCI and Cloudflare reconciled from declared state with drift alerts,
  placement honouring locks, canary + rolling delivery of binaries under
  systemd with automatic rollback (D-69), Worker/Snippet promotion,
  certificates and key rotation. The complete behavior is qualified first on
  the six-host local Firecracker estate (D-81). `ts-deploy` is the single CI
  program (D-68).
- Observability: Prometheus-format metrics from every subsystem shipped to
  Grafana Cloud (free tier) with alerts and OnCall; structured logs; runbooks
  for the ~10 most likely incidents; Cloudflare cache status, tiered fills,
  origin fetches, collapsed-wait metrics, Worker request counts per hostname
  (must be zero on `sync.api.`), egress per Region. Public status page.

### Post-launch roadmap (priority order)

1. TypeScript server functions (D-19).
2. Vector search (sqlite-vec) as a SQL function and auto-index kind.
3. Time-travel reads; cross-tenant admin queries (`FROM tenants.orders`);
   joins and aggregates in the live subset.
4. Local-first: read-only replicas over the WAL-frame stream (SQLite WASM),
   then offline writes (D-57).
5. Deploy integrations (Vercel, Netlify, Cloudflare); Expo and React+Vite
   starters; Python and Java SDKs.
6. In-region edge processing (`<region>.direct.api.`, D-52); Enterprise +
   Regional Services when revenue justifies it. Regions 4–20; regional R3
   on shared TerraBase member hosts once a Region has ≥ 3 eligible hosts
   (D-65). No dedicated orchestrator servers. Spatial (R-tree).
7. SOC 2, Jepsen-style public correctness report, Scale SLA (D-75), BYOK,
   audit export, startup credits, external pentest (D-70), FIPS/SBOM (D-41).

### Performance targets

Measured at the public API boundary, 1 KiB hot documents, under a hard
ceiling equal to **one launch member host (2 OCPU / 12 GB, D-65)** for the
single-node family and two such hosts for RF=2 (cgroups), load generator
outside the budget; results without that record are non-gating. Numbers must
hold on the hardware we actually pay for.

- Same-region point read p99 ≤ 10 ms. `RegionalDurable` write p99 ≤ 20 ms.
- Local reactive visibility: WebSocket p99 ≤ 50 ms; CDN HTTP p99 ≤ 250 ms
  (100 ms cadence + edge).
- Cross-region visibility (write in A, subscriber sees it in B) p50 ≤ 300 ms,
  p99 ≤ 1 s.
- CDN fan-out: one million concurrent HTTP readers of one authorization-
  equivalent shared view at 16 writes/s; delivery p99 ≤ 1 s; one view-delta
  computation per cadence window; member open waits, CPU, and RSS do not grow
  with readers; every client receives the same ordered bytes; zero Worker
  requests on `sync.api.`. Not a public claim until the production test
  passes.
- Beat PostgreSQL-with-JSONB and RavenDB on point read/write, indexed query,
  and bulk ingest, like-for-like (single-node and RF=2 as separate families).
  Bulk ingest is the family allowed an owner waiver with measured evidence.
- Auto-indexing vs RavenDB: first uncovered query, time-to-active for 1M
  documents, templates-to-fixed-point on a replayed workload, the D-31 5%
  regression threshold and automatic rollback, indexed latency, write throughput at
  0/5/12 indexes and at the budget ceiling, and zero stale reads as a
  correctness test.
- Database and branch creation p99 ≤ 5 s. Cost per admitted write is the
  primary efficiency metric.

### Unit economics (D-61)

Planning assumptions, not guarantees. At G1 and G4, reverify every provider
price, quota, plan limit, and term; replace inputs with measured costs:

- OCI: Ampere A1 ≈ $0.01/OCPU-h + $0.0015/GB-h (2 OCPU / 12 GB ≈ $28/month);
  block volume Balanced $0.0425/GB-month; Object Storage Standard
  $0.0255/GB-month, requests $0.0034/10k; egress after 10 TB/Region/month:
  NA/EU $0.0085/GB, GRU $0.025/GB.
- Cloudflare: bandwidth $0; Snippet invocations $0; Worker requests $0.30/M
  beyond 10M (only `api.` first contact and the dashboard); KV reads $0.50/M,
  writes $5/M; purge free within 5 req/s.
- Fixed floor (D-65): ≈ $205/month for three Regions; every fixed line above
  it needs a measured reason.
- Cost per logical GB-month per Region ≈ 2 block copies ($0.085) + backup and
  WAL retention (~$0.035) ≈ **$0.12**; price $0.20 → margin ≈ 40 %, the one
  line allowed below the 50 % rule while storage cost is being reduced
  (owner-acknowledged).
- Cost per million writes: committer CPU + follower ack + journal + view
  deltas + backup PUTs to OCI Object Storage and Tigris at 10 s sync
  (recalculate requests, duplicate storage, retrieval, and OCI outbound costs;
  the former single-provider estimate is superseded) + cross-Region egress (~$0.01/M per extra Region); target: $1/M
  carries ≥ 50 % margin at 1 KiB documents and ≤ 10 affected views per write.
- Cost per million direct reads ≈ $0.001 CPU + $0.013–0.04 egress once past
  the free 10 TB; worst case per Free database under D-64 (100 req/s and
  50 GB/month) ≈ $1.5/month from GRU, $0.75 elsewhere.
- Edge cost per million reader-hours: bandwidth $0 + Snippet $0 + origin
  fills (bounded by tiers) → measured on the stub-origin test.

---

## 5. Engineering rules

### Commit requests and qualification

The owner's explicit request to commit and push takes precedence over the
normal pre-push verification order: commit and push the named changes first,
without running tests, checks, or builds beforehand. Verify afterward and
repair attributable failures promptly. This changes the work order, never the
release or production qualification gates. Preserve unrelated local changes;
never add co-author trailers. Forgejo operations use `fj`.

### Orchestrator greenfield refactor

`TerraScale is preparing its first public release. `ts-orchestrator` is greenfield. Refactor or replace anything needed to finish
its TerraBase-specific production implementation. Update consumers, contracts,
tests, and documentation together and delete superseded code. Do not add or
retain compatibility layers, shims, legacy aliases, migration bridges, or
parallel implementations to preserve retired behavior. The final design must
still provide durable recovery, fencing, backups, and tested release rollback.

Qualification runs the real orchestrator, `ts-node`, and TerraBase together on
every member guest under Firecracker and systemd, including the six-member
GRU/SFO/FRA baseline and an additional fourth-Region scenario. Passing unit
fakes or leaving production adapters unwired does not complete implementation.
The repository goal specifies the required fault matrix and production gates.


**Definition of done**: code pushed to the repository's `develop`, focused
tests for changed behavior, the verification set green locally and in CI, docs the task made stale fixed or deleted in the same series.
Nothing else.

**Git discipline (D-32)**: as in "How to work here"; a rebase conflict is
resolved in place; when two agents need the same file, the later one rebases.

**Cross-repository changes (D-33)**: terrabase changes the contract and
publishes a new version; the consumer bumps `CONTRACT_VERSION`, runs
`make contracts`, and lands the regenerated output with its code in one
commit. Pre-launch replacement follows the greenfield policy; released public
contracts follow D-73. A consumer never hand-edits generated files and
never depends on an unpublished contract.

**Simplification playbook**: one implementation per concern; delete, don't
deprecate; docs describe the present; no speculative surface
(`#[allow(dead_code)]`, unregistered routes, unwired seams: wire or delete);
packages under ~10k LOC; every fail-closed stub names the section 6 item
that replaces it; tests are the spec; pushes stay small and independently
green.

**Anti-ledger rule (D-41)**: hashes, "frozen"/"preserve" instructions, review
verdicts, and status never enter a planning document. **Deletion first**: a
repository's immediate deletion list is finished before replacement work;
D-59's temporary SQLite bridge is the only exception and has its own gate.

**The spine is staffed first, everything else runs beside it**: the items on
the critical path to the next gate always have agents on them; every other
open item runs in parallel with them, never instead of them. A flurry of green
cleanup pushes while the engine can't serve a query end to end and nobody is
on the engine is failure, not progress.

**When you are blocked**: owner-only action → one line in section 8, pick
another unit. Uncovered decision → the simplest choice consistent with
sections 1–4, noted in the commit message. Never stop the work to write a plan;
never wait for another agent when a contract, mock, stub, or stack exists.

### Verification sets (D-41)

Each portable set is exactly what that repository's `make check` and
`ts-deploy ci --repo <name>` run. Additional named qualification gates follow.

```bash
# terrabase/
cargo fmt --all --check && cargo clippy --workspace --all-targets --locked -- -D warnings
cargo test --workspace --locked && buf lint
(cd terrabase-go && gofmt -l . && go vet ./... && go test -race ./...)
make contracts && git diff --exit-code     # regenerates + publishes on CI

# ts-cli/ ts-deploy/
gofmt -l . && go vet ./... && go build ./... && go test -race ./...

# ts-orchestrator/                    (portable `make check`; D-81)
gofmt -l . && go vet ./... && go build ./... && go test -race ./...
make vm-smoke     # focused feedback for VM architecture changes on Linux/KVM
make vm-test      # every `develop` CI push before candidate submission

# ts-typescript/                      (Vitest only; drift checks are Vitest tests)
make contracts && git diff --exit-code && npm ci && npm run typecheck && npm run build && npm test
npm run test:browser           # when the reactive client changes

# ts-dotnet/ ts-go/ ts-rust/  (conformance suite is part of `test`)
make contracts && git diff --exit-code && make check   # dotnet: format+build+test; go: gofmt/vet/test -race; rust: fmt/clippy/test

# ts-dashboard/                (Vitest + Playwright only; Vitest on workerd via @cloudflare/vitest-pool-workers)
make contracts && git diff --exit-code && vp install && vp check && vp run vitest && vp run vinext:build && vp run budget && npx wrangler deploy --dry-run
vp run playwright              # when routes/pages change; against `vinext dev` on workerd; includes the D-80 visual snapshots
# `vp check` runs the StyleX ESLint rules; `budget` fails on the D-80 client JS, CSS, and Worker bundle limits

# ts-proxy/                    (Worker: Vitest on workerd; Snippet: plain Vitest with a 5 ms CPU budget assertion)
npm ci && npm run typecheck && npm test && npx wrangler deploy --dry-run

# ts-docs/
npm ci && npm run build && npm test     # snippet runner against a sandbox branch
```

---

## 6. Road to production

Five tracks run in parallel. Four gates integrate them. Track items below are
the tasks; each is done when its stated behavior is verified by the
repository's verification set. A gate closes when every listed exit condition
holds; a track never waits for another track except at a gate.

### Waves: what starts when (section 0 applied to this plan)

Each item names its prerequisites with `after:`; items without one start on
day one. Everything in a wave runs simultaneously, across all repositories,
one agent per item and one subagent per `‖` unit inside it.

- **Wave 0, no prerequisites, all at once**: every immediate deletion list (one agent
  per repository, terrabase split five ways as listed); E-1, E-2, E-4, E-12,
  E-14; X-1, X-2, X-3, X-4; P-1 (from the E-1 draft; re-pin on publish),
  P-5b, P-6, P-8; I-1, I-2, I-3, I-6, I-8, I-12; L-1, L-4, L-5 drafts. Wave 0 is
  ~35 agents before any integration exists.
- **Wave 1, after E-1 + E-2**: E-3, E-4b, E-5, E-6, E-7, E-8, E-9, E-10 in
  parallel (they share the substrate, not each other); P-2, P-2b, P-4, P-5
  against the P-1 mock; X-5 after X-3 + X-4 + O-4; I-4, I-5 after I-2.
- **Wave 2, after the wave-1 engine items**: E-15 `make stack`; P-3 journey
  moves from mock to stack; X-6 after E-5 + X-2; I-7 with the owner (O-4);
  I-11 after I-1; P-5c after I-11; P-7.
- **Wave 3, verification and scale**: E-11, E-13, E-16, E-17; I-9, I-10;
  L-2, L-3, L-6, L-7. These run against the stack and production, in
  parallel with each other.

Gates (below) are the only synchronization points. Nothing in a later wave
waits for an unrelated earlier item.

### Deletion prerequisites (D-59's bridge exits after E-15)

Exit per repository: every immediate item gone from `develop`, verification
green, and `rg` clean. D-59's bridge alone remains through E-15 and must be
gone before G1. No other replacement is built beside retired code; dependent
code is deleted or reduced to a compiling stub naming its replacement. **All
repositories delete at the same time, one agent each; inside terrabase, five agents work
the bullets below in parallel** (storage engine ‖ replication/sharding ‖
studio/spatial/vector/TBEP ‖ docs/CI/Go/observability ‖ TQL).

- **terrabase**: the LSM storage engine entirely (memtable, SSTables, WAL,
  compaction, `RootFiles`, tiers, backup-cluster, `StorageBackend`,
  `TERRABASE_STORAGE_BACKEND`, format marker, LSM benches/tests/fixtures/docs);
  `src/replication` leader-per-range and every quorum/leader/lease type (keep
  `terrabase-replication-core` and `terrabase-member-transport`); `src/sharding`
  hash buckets; `terrabase-studio` (after O-7); spatial index modes and
  operators; sqlite-vec and vector operators (post-launch, D-40); the two
  extra TBEP host binaries; `docs/evidence/`, every ledger, every doc for a
  deleted subsystem, FIPS/SBOM/provenance/hostile-fixture/multi-arch CI;
  `terrabase/go/`, `terrabase/observability/`, empty `src/orchestrator`;
  every Postgres-wire mention; **the TQL language** (grammar, parser, planner
  front end, `docs/tql/`, examples): authorization compile and index
  derivation move onto the SQL AST (D-78), nothing of the syntax survives.
- **Legacy Go API repository**: delete the gateway (handlers, auth,
  middleware, OpenAPI, tests); move the `terrascale` CLI into the new
  `ts-cli` repository with history; archive the old repository.
- **ts-typescript**: queues, repositories,
  environments; the `./local` emulator (once the Rust API serves the
  dashboard's needs);
  sync v1 residue; `Int64String`/`Ulid` wrappers; `.mjs` scripts and
  `node --test`; any hand-copied OpenAPI slice (replaced by the pinned
  contract); TQL query strings and the TQL builder.
- **ts-dashboard**: queues, repositories, environments routes/components/
  server code/MCP tools/tests; `.terrascale/` strays; `.mjs`/`node --test`;
  the Node server target (`build-standalone`, `Dashboard.Runtime.Dockerfile`,
  `otel-bootstrap.mjs`, `@opentelemetry/*-node`, `start` tasks, and every
  `node:*` import in server code). Keep the SDK SQLite bridge only until the
  D-59 readiness gate, then delete its code, `better-sqlite3` + `kysely`,
  configuration, data, and SQLite-specific tests before G1. After the snapshot gate passes: `tailwindcss`,
  `@tailwindcss/vite`, `shadcn`, `components.json`, `class-variance-authority`,
  `clsx`, `tailwind-merge`, `tw-animate-css`, every `className` string, and
  the `@theme inline` / shadcn token blocks in `globals.css` (D-80).
- **ts-orchestrator**: retired role catalogs and NATS message types where the launch
  equivalent exists; the podman/container supervisor, image and registry
  types, Dashboard/Database-API/legacy product roles, Infisical; C#-era
  residue; `docs/archive/`; phase docs. Replace the workflow with the D-68
  five-liner.
- **ts-deploy**: dotnet toolchain discovery, the Windows runner gate, the
  trust-root plumbing D-41 defers, `.terrascale/deploy.json`, `ci plan`,
  profiles and matrices, image build/push/manifest commands and
  `container-publishing.md`, Discord notify; repoint the remote off
  `TerraScale-Old`. Every repository's `ci.yml` becomes the D-68 five-liner.
- **ts-proxy**: the Postgres-wire hostname, the "Worker on every reactive
  request" and Regional Services text in `docs/design.md`.
- **Workspace**: `.codex-worktrees/` in every repository; `-Old` remotes.

### Tracks

**Engine track (terrabase)** — one agent per item, one subagent per `‖` unit.

- E-1 Contract publishing: `make contracts` generates OpenAPI, manifest, and
  protos and publishes them versioned to the Forgejo registry; consumers can
  pin v1 before any other engine work lands. ‖ OpenAPI ‖ protos ‖ manifest +
  publish pipeline.
- E-2 SQLite substrate per D-27/D-42/D-43: row model, committer thread,
  checkpointer, open-file LRU, idle close (D-44). ‖ row model + schema ‖
  committer/group commit ‖ checkpointer + file LRU + idle close.
- E-3 (after E-1, E-2) Public API (axum): auth, access rules (CEL) + BYO-JWT,
  REST `/api/v1`, ETags, typed errors with fixes, OpenAPI generation, Scalar.
  ‖ auth + rules ‖ documents/collections routes ‖ management routes ‖
  errors/OpenAPI/Scalar.
- E-4 SQL planner (D-78/D-54): allow-list parser, collection-as-table model,
  live-subset classification, hypothetical-index planning, per-template
  plan pins, watermark hybrid plans, FTS5; schema JSON apply and codegen
  source; SDK conformance suite (D-13). ‖ parser + allow-list ‖ planner +
  live classification ‖ hypothetical indexes + pins + hybrid plans ‖ schema
  JSON apply ‖ conformance suite.
- E-4b (after E-4) Auto-indexing engine (D-31, ADR 0045). ‖ observe (template
  ring, sketches) + compress ‖ candidates + compile check ‖ bandit selection
  + write budget ‖ invisible build + watermark ‖ shadow verification + pins ‖
  monitor/revert/retire/merge ‖ journaled lifecycle + audit/evidence API.
- E-5 (after E-2) Sync engine (D-38/D-46, ADR 0044). ‖ view key + attach +
  capability issue ‖ interval-tree dependencies ‖ Z-set deltas + three live
  operators + top-k buffer ‖ encode-once immutable log + `singleflight` +
  cadence ‖ checkpoints + typed resets ‖ WebSocket + webhooks.
- E-6 (after E-2) Replication (D-35/D-36/D-37/D-45, ADR 0044). ‖ WAL-frame
  follower ‖ journal tailer over QUIC + causal dependency queue ‖ delta-state
  merge + history explanations ‖ range-based set reconciliation ‖ session
  tokens + `StaleReplica` ‖ `GlobalDurable` uniformity ‖ region bootstrap +
  proof-of-equality.
- E-7 (after E-2) Backup: native Rust backup per file, PITR, restore-to-new-database,
  branching, per-database encryption (OCI Vault; local-file provider for CI).
  Read the pinned Litestream source and relevant recovery tests first, then
  rebuild the required functionality in native Rust (ADR 0046).
  ‖ source review + recovery invariants ‖ coordinated WAL capture + bounded
  shared backup workers + OCI/Tigris policy selection + uploads + PITR
  ‖ independent provider restore + outage recovery ‖ branching ‖ encryption + key
  hierarchy ‖ fault/restore tests + comparative benchmarks. Delete the
  external Litestream integration, packaging, configuration, and obsolete
  tests in the replacement series; retain no compatibility shim. This is
  required implementation work, not a claim that the rebuild has shipped.
- E-8 (after E-2) Tenants (D-51), placement lock (D-52), TTL, history windows
  (D-25), export and import formats (D-60). ‖ tenants ‖ placement lock ‖
  TTL + history ‖ export ‖ import formats.
- E-9 (after E-2) Metering (D-16): write and storage facts, read counters for
  guards, usage export cursor; chaos tests (retries, splits, restarts
  reconcile). ‖ facts + cursor ‖ chaos tests.
- E-10 (after E-3) Abuse guards (D-64) and per-tenant resource governance
  (adversarial-neighbour test: victim p99 within 2× of solo).
- E-11 (after E-6) Online key-range shard split (D-28) with zero
  acknowledged-write loss.
- E-12 Deterministic simulation harness for replication + sync (seeded
  faults; the eight invariants of ADR 0044 §4). The harness skeleton and
  fault injectors start on day one against trait interfaces; ‖ one subagent
  per invariant once E-5/E-6 land; CI seed batch + nightly sweep.
- E-13 (after E-15) Benchmarks vs PostgreSQL/JSONB and RavenDB under the
  8 vCPU / 8 GiB ceiling; auto-published tables. ‖ one subagent per
  benchmark family (point ops, indexed query, ingest, auto-indexing, sync).
- E-14 Prometheus metrics for every subsystem; structured logs. Starts on day
  one with the metric registry and log schema; each engine item wires its
  own metrics.
- E-15 (after E-3, E-5, E-6) Local three-member harness (`make stack`) used
  by our CI and the owner's machine to run the journey; never shipped to
  customers.
- E-16 (after E-15) Tenant scale test: 100k databases on one member class;
  idle cost ≈ 0; create p99 < 5 s.
- E-17 (after E-15) Security pass. ‖ dependency + secret scanning ‖ fuzzing
  (public API, SQL allow-list, capability issuer, frame decoders) ‖ authz
  matrix including cache hits, expiry, entitlement changes, purge, placement
  lock.

**Edge track (ts-proxy + Cloudflare zone)** — X-1..X-4 all start on day one.

- X-1 REST Worker (exists): keep `pick()` table-tested; add Worker request
  counters per hostname.
- X-2 Snippet gate: HMAC-SHA256 verify with `crypto.subtle`, view/generation
  binding, expiry-bucket overlap, header strip, canonical `fetch()`; plain
  Vitest with a 5 ms CPU assertion; deployment through the Cloudflare API by
  `ts-deploy`; keys from Secrets Store. ‖ Snippet code + tests ‖ deployment
  path in `ts-deploy`.
- X-3 Zone configuration as orchestrator code: DNS, Cache Rules for snapshot
  and log paths, Smart Tiered Cache, WAF and per-IP rate rules, cache tags,
  purge batching per second. ‖ one subagent per Cloudflare resource type.
- X-4 Stub origin: a small Rust or Go server that speaks the D-14 contract
  (immutable log responses, `no-store` timeouts, capability issue) with
  synthetic writes; used before the engine exists.
- X-5 (after X-3, X-4, O-4) Reader ramp against the stub on the real zone: 10k → 100k → 1M
  concurrent readers at 16 writes/s. Record client latency, edge and
  upper-tier misses, origin fetches, cache byte-hit ratio, Worker requests on
  `sync.api.` (must be 0), and cost per million reader-hours (D-61). Inject
  cache bypass, eviction, rule changes, capability expiry, mixed credentials
  without leaking a byte.
- X-6 (after E-5, X-2) Local edge harness for the journey: one deterministic cache location
  in front of the local stack; 10k authorization-equivalent subscribers, one
  origin fetch, identical MessagePack for all, `no-store` on timeouts, another
  capability cannot read the cached bytes.

**Product track (four SDKs, ts-dashboard, ts-cli, ts-docs)** — product items
build against the P-1 mock until E-15 exists; dashboard-owned records use the
D-59 persistent local SQLite backend. Nothing here waits for the engine.

- P-1 Pin contract v1 (from the E-1 draft, re-pinned when published); generate
  types and routes; generate the OpenAPI mock; add the explicitly enabled SDK
  SQLite backend for persistent local dashboard records until the D-59 gate.
- P-2 (after P-1) `ts-typescript`. ‖ data-first API + management ‖ schema
  builder + codegen ‖ typed SQL builder ‖ reactive client (HTTP default,
  WebSocket opt-in, capability header, checkpoints, typed resets) ‖ TanStack
  adapter + React hooks ‖ Better Auth adapter ‖ `ts.tenants`.
- P-2b (after P-1) `ts-dotnet` ‖ `ts-go` ‖ `ts-rust`: the D-13 core surface,
  generated from the contract, each passing the conformance suite; one agent
  per SDK, in parallel, starting from the same generated base; inside each,
  ‖ schema builder ‖ query builder ‖ client ‖ conformance run.
- P-3 (after P-2; mock + local SQLite first, stack after E-15; delete SQLite once the D-59 gate passes)
  `ts-typescript/test/journey.test.ts` covering the whole launch scope; grows
  with every feature; runs against `make stack` (E-15) in terrabase CI. ‖ one
  subagent per journey chapter (CRUD/SQL, live + sync, replication, backup +
  branch, tenants + usage, dashboard flows).
- P-4 (after P-1) CLI. ‖ `dev` (cloud dev branch, seed, faults, logs,
  codegen watch) ‖ `push` + `diff` + `codegen` ‖ `import` + `db export` +
  `db` ‖ `tenants` + `index wait` + `doctor`.
- P-5 (after P-1) Dashboard on workerd with D-59 local persistence, one subagent per feature slice:
  ‖ registration + orgs/projects ‖ databases + branches ‖ data explorer ‖ SQL
  + live consoles ‖ history + conflict viewer ‖ schema viewer + query
  performance ‖ indexes ‖ subscriptions ‖ usage + cost attribution ‖ API keys
  + spend caps ‖ webhooks ‖ access-rule editor + test harness.
- P-5b StyleX migration (D-80), day one, independent of every other item:
  snapshots first, then ‖ `src/design/` tokens + consts + themes ‖ primitives
  on headless Radix (one subagent per primitive group) ‖ one subagent per
  feature slice folder behind the snapshot gate ‖ `docs/design-system.md`
  rewrite ‖ `vp run budget`; Tailwind and shadcn deleted last.
- P-5c (after I-11) Admin console (D-79). ‖ `staff` role + passkey gate +
  WAF rule ‖ estate views (`estate.*` live queries) ‖ tenants + revenue +
  unit economics ‖ product-level actions with audit ‖ view-as.
- P-6 Billing, day one against Stripe sandbox: signup → quota → subscribe →
  upgrade → cancel → dunning, webhooks on the Worker, live pricing
  calculator, branch allowance and PAYG lines, capped org never pays above
  cap. ‖ subscription lifecycle ‖ webhooks ‖ calculator + PAYG lines.
- P-7 (after P-2) MCP read-write on the SDK with audit. Preview deployment
  against a control-plane branch. CPU-time and memory ceilings measured on
  the heaviest pages.
- P-8 Day one: `npx create-terrascale-app` (Next) ‖ docs site with runnable
  snippets against a sandbox branch ‖ llms.txt.

**Infra track (ts-orchestrator, ts-deploy, OCI estate)** — I-1, I-2, I-3,
I-6, I-8, I-12 start on day one; reconcilers are one subagent per resource
type.

- I-1 Launch orchestrator integration complete; `control` split into < 10k LOC
  packages; declared-estate types and the reconcile loop with drift alerts
  (D-67). ‖ estate types ‖ reconcile loop + drift ‖ package split.
- I-2 OCI and Cloudflare reconcilers for every resource named in D-67;
  `bootstrap` from an empty tenancy; placement honouring locks. ‖ compute ‖
  block/object storage + lifecycle ‖ VCN/NSG ‖ vault ‖ DNS ‖ Cache Rules +
  Snippet rules ‖ Worker routes + KV ‖ WAF + rate rules ‖ certificates ‖
  bootstrap ‖ placement.
- I-3 `ts-node` per D-69 and delivery. ‖ `ts-node` (signed install, hardened
  units, symlink rollback, drain-coordinated restart) ‖ delivery API for
  `ts-deploy` candidates ‖ canary → rolling rollout with health and catch-up
  gates and automatic rollback ‖ certificates + SPIFFE identity ‖ key
  rotation into Secrets Store.
- I-4 (after I-2) KV writer loop (D-58): Region health every 10 s and on
  transition, placement on every change, ordered so KV staleness is always
  safe; bulk API; write-budget alert.
- I-5 (after I-2) `terrascale-orchestrator add-region <code>`: one command from empty OCI
  region to serving members, object storage with lifecycle rules, native TerraBase backup
  targets, routes, and probes; the runbook is its `--dry-run` output; estate
  per D-65: every Region starts its own regional orchestrator under the GRU
  master. SFO/FRA also stage global R3 candidates; later two-host Regions use
  the global control store until three hosts qualify for regional R3. Placement
  refuses a Region with fewer than two members.
- I-6 `ts-deploy ci --repo <name>` for all repository kinds: automatic
  cargo/go/npm/toolchain caches on the runner, parallel verification set,
  signed release binaries and contracts to the Forgejo registry and releases,
  `wrangler versions upload`, candidate hand-off; fixture matrix as its own
  release gate; every repository on the five-line workflow; push → serving
  timings recorded warm and cold (D-68). ‖ one subagent per repository kind
  (Rust engine, Go binary, Worker, TypeScript package, docs) ‖ caches +
  `doctor --fix` ‖ signing + publishing ‖ self-release ‖ fixture matrix.
- I-7 (after I-2, I-12, O-4) Owner-supervised automated GRU bootstrap (D-65): PAYG tenancy with GRU
  as home region, two members (one on Always Free), native TerraBase backup to OCI Object
  Storage and Tigris, default vault, instance principals, NSGs, Tailscale, external
  probe, API on the real domains, and the fenced temporary pre-customer R1.
- I-8 Observability, day one: Grafana Cloud free tier (metrics, logs,
  OnCall) fed by an agent on each member host; **SLO burn-rate alerts are the
  only pages** (D-67/D-75), everything else is a dashboard; egress per
  Region, clock offset (D-69); Cloudflare cache and Worker metrics; OTLP from
  the Workers. ‖ host agent + dashboards ‖ SLO alerts + OnCall ‖ Cloudflare +
  Worker telemetry.
- I-9 (after I-7) Degraded-mode drill: control plane down, data path keeps
  serving. Self-healing drills: ‖ kill a host ‖ fill a disk ‖ expire a cert ‖
  exhaust A1 capacity (fallback shape) ‖ skew a clock; each heals with no
  human action.
- I-10 (after I-7) ‖ daily restore drill + JetStream snapshots + per-Region
  artifact mirror (D-71) ‖ DEK/KEK hierarchy + capability key rotation +
  crown-jewel procedures (D-70) ‖ free-tier reaper (D-76).
- I-11 (after I-1) Estate publisher (D-79): the orchestrator writes
  `estate.*` documents into the control-plane database on change and every
  10 s; terrabase exposes admin-scoped usage and index facts; both feed the
  admin console with no new transport. ‖ orchestrator publisher ‖ terrabase
  admin facts.
- I-12 Firecracker qualification stack (D-81): pinned VMM, jailer, Go SDK and
  direct API adapter; reproducible Ubuntu LTS systemd disk; six 1-vCPU launch
  hosts; TAP/namespace/bridge networking; `nftables` and `tc netem` faults;
  persistent and faultable drives; local OCI, Cloudflare, object-store and edge
  endpoints; explicit readiness and crash-safe cleanup. `make vm-test` proves
  the GRU R1 to GRU/SFO/FRA R3 transition, I-1..I-5, and I-9..I-11 locally
  before I-7 can touch OCI. ‖ VM lifecycle ‖ guest image + `ts-node` ‖ network
  faults ‖ disk faults ‖ provider endpoints ‖ full qualification suite.

**Launch track** — L-1, L-4, L-5 start on day one as drafts and are
regenerated when the numbers land.

- L-1 Pricing sheet and limits page generated from D-16/D-17/D-21/D-25/D-64;
  states that a write is billed once and delivery is free, never that
  infrastructure is free.
- L-2 (after X-5, I-7) Unit-economics measurements (D-61) recorded from X-5
  and production.
- L-3 (after I-8) Status page, paging to the owner with escalation, runbooks
  for the top ten alerts, rollback rehearsed in every region.
- L-4 Legal minimum: ‖ ToS + privacy + DPA (Cloudflare and OCI as
  sub-processors) ‖ account deletion = crypto-shred (D-70) + Stripe Tax on ‖
  Discord and support email live (D-77) + `security.txt` published.
- L-5 Launch posts, one subagent each: "Why TerraScale runs on SQLite" ‖
  "Reads are free: how" ‖ benchmarks ‖ replication design ‖ "a database per
  customer".
- L-6 (after G3) 7-day soak on the exact launch digests.
- L-7 (after G3) D-74 measured on a fresh machine; D-75 SLO probe and status
  page live; D-71 restore drill has passed daily for 14 days before G4.

### Gates

**G1 — Works end to end (our machines and CI)**

- Every deletion list is finished, including D-59 SQLite removal after the
  real dashboard journey; contract v1 is published and pinned everywhere.
- `ts-orchestrator`'s `make vm-test` is green on the pinned Firecracker stack:
  bootstrap, `add-region`, the GRU/SFO/FRA R3 control group, reconciliation,
  delivery and rollback, host and Region faults, restore, degraded mode, and
  crash-safe cleanup all pass without OCI or Cloudflare credentials (D-81).
- `make stack` boots GRU, SFO, and FRA members, the REST Worker under
  `wrangler dev`, the Snippet under the local edge harness, the dashboard on
  workerd, and a webhook receiver; the journey (P-3) proves the complete
  launch scope: SQLite storage and auto-indexing; the Rust API; CRUD, SQL
  (execute and live), FTS, TTL, history, schema migration, tenants, usage, webhooks,
  backup/PITR/branch, export and import, dashboard flows; three-Region
  replication, session reads, `RegionalDurable`/`GlobalDurable`,
  deterministic convergence, region restart and snapshot catch-up, the
  partition/kill/clock-skew fault matrix; server live-query and
  collection-sync views, TanStack adapter, optimistic reconciliation,
  checkpoint replay, every typed reset reason.
- X-6 local edge proof passes without real Cloudflare credentials.

**G2 — One Region on OCI, real Cloudflare, no customers**

- GRU live on the temporary pre-customer R1 (I-7); members serve on the real domains; the Snippet, Cache Rules,
  and KV loop are live; the dashboard Worker is live at `app.` against the
  production control-plane database; Stripe sandbox end to end; encryption,
  access rules, abuse guards, shard split, simulation harness, metering
  chaos, benchmarks, observability, tenant scale, and security pass (E-7,
  E-10..E-17) are green.
- X-5 passes its real-zone million-reader stub ramp with zero Worker requests
  on `sync.api.` and recorded unit cost, then repeats against the real engine at
  100k readers. `terrascale dev` works for an external tester.

**G3 — Three Regions**

- SFO and FRA bootstrapped **with `add-region`**; their synchronized control
  candidates atomically promote the GRU R1 to permanent GRU/SFO/FRA R3 without
  regressing the accepted epoch; databases replicate across all three; a
  placement-locked database provably has no bytes at rest outside its Regions
  in placement, replication, backups, restore, exports,
  and branches.
- Fault matrix: member loss, region loss, WAN partition, clock skew, slow
  region (local writes unaffected), region rejoin catch-up.
- Performance targets re-run per Region and cross-Region; the million-reader
  test passes on production with the real engine.
- Status page, paging, runbooks, rollback rehearsed everywhere (L-3).

**G4 — Launch (waitlist beta, D-23)**

- 7-day soak on the launch digests; pricing and limits published with
  measured unit economics behind them; billing proven with a real test
  customer; docs site live with runnable snippets; legal minimum published;
  launch checklist (section 7) signed by the owner; waitlist opens.

---

## 7. Launch acceptance checklist

1. G1–G3 closed; the Firecracker qualification suite, journey, and fault
   matrix are green.
2. CI green on `develop` of all eleven repositories; all verification sets
   pass; every consumer pins the contract version terrabase serves; all four
   SDKs pass the conformance suite.
3. Performance targets met in all three Regions with resource-ceiling
   records; any waived family has the owner's written justification.
4. The production million-reader test passes with one delta computation per
   cadence window, bounded origin fills, no reader-proportional member
   resources, identical ordered bytes, no authorization crossover, and zero
   Worker requests on `sync.api.`.
5. Unit economics measured (D-61): every billed line's margin recorded; every
   free dimension's worst case under its guard recorded; edge cost per hit
   recorded as zero metered compute.
6. 7-day soak complete on the launch digests.
7. Security pass complete; capabilities, cache isolation, purge, and bounded
   revocation lag have no critical findings.
8. Billing journey proven end to end; dunning tested; spend caps hold; a
   write is billed once; reads and delivery are unbilled; the branch
   allowance and PAYG lines are correct on a real invoice.
9. Placement-lock proof for `eu-only` and `br-only` databases covers
   placement, replication, backups, restore, exports, and branches (G3).
10. Runbooks for the top alerts; rollback by digest rehearsed everywhere.
11. Status page live; paging fires in a drill; degraded mode demonstrated.
12. ToS/privacy/DPA published; crypto-shred and CDN purge tests green;
    Cloudflare's written confirmation on CDN terms on file (O-4).
13. Docs describe the present; `rg` sweeps for dead directions (C#/.NET
    services outside `ts-dotnet`, FlatBuffers, QUIC-TBEP, Pulumi, Flux, Kubernetes, Polar, Loro, LSM,
    SSTable, memtable, RootFiles, `StorageBackend`, Studio, Datastar,
    hash-bucket sharding, leader-per-range, queues, repositories,
    environments, Go gateway, `Int64String`, `build-standalone`,
    `Dashboard.Runtime.Dockerfile`, `better-sqlite3`, monorepo, Postgres-wire,
    `pg.`, Postgres import, Regional Services, `sqlite-vec`, emulator, podman,
    containerd, Dockerfile, OCI image, `deploy.json`, Infisical, TQL) are
    clean.
14. Restore drill green 14 days running; key rotation procedures written and
    rehearsed once; time to first live query under five minutes (L-7).
15. Owner sign-off.

---

## 8. Owner inbox (human-only actions)

| # | Action | Blocks |
| --- | --- | --- |
| O-1 | Maintain push access to the eleven active repositories and enable the immutable Forgejo package publication required by D-33. Retired repositories belong in archived/read-only `TerraScale-Old`; remove local worktrees only after useful work is recoverable and pushed. | Contract publication and repository access |
| O-2 | Update every `AGENTS.md`/`CLAUDE.md` (drafts in `AGENTS-RECOMMENDATIONS.md`, revised for multi-repo, cloud dev, the Snippet gate, and the pricing model): root, `terrabase`, `ts-cli`, `ts-orchestrator`, `ts-dashboard`, `ts-typescript`, `ts-deploy`, `ts-proxy`, `ts-docs`. Strike the Studio section from `terrabase/AGENTS.md` (was O-7). | deletion prerequisites, terrabase Studio deletion |
| O-3 | Least-privilege Forgejo Actions secrets per repository: package registry publish and release upload, npm publish for `@terrascale/sdk`, Stripe sandbox, the release signing key, the orchestrator delivery token, and a Cloudflare token scoped to **Worker version upload only**. Every other Cloudflare and OCI credential lives in the orchestrator (D-67). Confirm the `terrascale-builder` runner is persistent, runs jobs in host mode (no job containers), has a disk for `/var/cache/terrascale`, and its user has passwordless sudo for `apt-get` only (D-68 installs everything else). For `ts-orchestrator`, also confirm writable KVM and the narrow audited host privileges required to create and clean up only TerraScale-labelled TAPs, namespaces, traffic rules, jail directories, and test drives (D-81); do not grant the general job unrestricted persistent root. | G1/G2 |
| O-4 | Cloudflare: zone on **Pro**, **Workers Paid**, Secrets Store enabled; obtain written confirmation from Cloudflare that small cacheable API responses at high request volume are within the CDN terms for the Pro plan. OCI: pay-as-you-go tenancy with **GRU as home region** (Always Free lives there and cannot move), confirm with OCI support in writing that the Always Free A1 allotment applies to the PAYG tenancy; Grafana Cloud free account; supervise the automated greenfield bootstrap and approve its displayed plan and authority handoff (I-7), with no manual estate mutation. | X-3, X-5, G2 |
| O-5 | Legal entity name + jurisdiction. | L-4 |
| O-6 | Confirm the price sheet before it is published (D-16/D-17/D-21/D-25/D-64) once D-61 measurements exist. | L-1, G4 |
| O-8 | ts-typescript/AGENTS.md line 65 still says "typed bounded TQL queries"; D-78 renamed the surface to live SQL result views / subscribable SELECT. Agents may not edit AGENTS.md (human-owned), so the last TQL name in that repository waits for a human rewrite. | ts-typescript deletion-list exit sweep |

## Feature gates by user, organization, and plan tier

Build a shared feature/module gate system for the dashboard and TerraBase.
Authorized TerraScale staff can enable or disable registered features by plan
tier, organization, or individual user from `/admin`. Store versioned policy
in the control-plane database through the SDK. Each override is `inherit`,
`enabled`, or `disabled`; removing an override restores inheritance. Show the
effective value and its source before saving, and audit actor, reason, scope,
previous value, new value, and policy revision.

For user-scoped operations, resolve explicit user override, then organization
override, then plan-tier default, then the registered feature default. A global
operator disable takes precedence over all of these. Resolve organization and
plan from verified ownership/membership, never client-supplied claims. Gates
never grant roles or bypass authorization, tenant isolation, residency,
durability invariants, or unbilled safety guards. An enabled feature still
requires ordinary permission to use it. Unknown feature IDs fail closed.

Each registered feature declares whether it is user-scoped or a shared
organization/database capability. Shared modules resolve against their owning
organization and its plan; a collaborator's user override cannot start, stop,
or reconfigure shared background work. Personal databases resolve against
their owning user's policy. Backup provider selection remains a typed policy
(OCI only, Tigris only, or both), integrated with this policy system; a boolean
module gate cannot discard backups or bypass safe provider transitions.

The dashboard consumes effective capabilities to hide or disable unavailable
navigation, routes, controls, and actions with a clear reason. Dashboard server
handlers and MCP tools enforce the same policy. TerraBase independently checks
protected public/private operations, SDK/API requests, reactive attachment and
resume, and background-module admission. Hiding UI is never enforcement.
Return a stable typed `FeatureDisabled` error with the feature ID and effective
policy revision, without leaking private policy details.

Propagate policy revisions to members and invalidate capability caches on
plan, membership, ownership, and override changes. Use bounded local evaluation;
no dashboard request, external flag service, or Worker is added to the database
or reactive hot path. Define and test a maximum revocation propagation bound,
stale-policy behavior, and restart recovery. Do not admit gated work with
missing or expired policy authority. Changes affecting reactive access advance
the entitlement epoch and invalidate affected capabilities using the existing
revocation contract, including cached delivery.

Every module defines safe disable behavior: reject new work and drain, cancel,
or pause existing work at a recoverable boundary. Disabling is not deletion;
retain data and honor retention, recovery, and in-flight transaction guarantees.
An unsafe transition is refused with a reason. Re-enabling resumes safely and
idempotently. Core authorization, encryption, tenant isolation, and required
write durability cannot be disabled through product feature gates.

Acceptance covers the plan/org/user precedence matrix, inherit and explicit
deny, global disable, cross-tenant denial, shared-resource scope, direct API
and MCP bypass attempts, plan and ownership changes, cache/epoch invalidation,
revocation bounds, outages/restarts, and safe disable/re-enable transitions.
The dashboard and TerraBase must agree on effective policy for the same scope
and revision. This is launch implementation work, not an implemented feature.
