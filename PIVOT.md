# PIVOT PLAN: TerraScale becomes a SQL-only, multi-region database

Status: **started 2026-10-29 (G-GO given).** It runs through T3 threads under the COS skill. **Priority: best practice first** (user, 2026-10-29: "make best practice the priority and let's fix all issues and risks that you found"). Every issue and risk found in the research has a fix and an owning task in section 6. None are accepted as "known risks". The research and spikes that back each decision are in the appendices. Every number is an estimate unless it says "measured".

Sources: `.cos/research-sql-only-turso-2026-10-29.md`, `.cos/pivot/01..04-*.md`, the spike reports in `.cos/spike-*-2026-10-29.md`, `benchmark/bench/2026-10-23-free-reads.md`. Snapshots: terrascale `4bacd8a81`; Turso main `3c5726122` (stable v0.8.3; TerraBase pins v0.8.2).

---

## 0. Goal

> "i still want to use turso under the hood for the database but i want users to not even know that we are using turso and provide them our own sdks and our own dashboard with a better developer experience with the agentic AI age giving them a really cheap and true multi region database"

> "people prefer sql over nosql and document json now a days, and it will be hard to maintain both"

> "if we write home all the time it will be slow to write, i want to write to the closest region and then sync to home using turso sync, i do not want to expose libsql i just want to expose the psql protocol and sql over rest apis and sql over websocket"

> "i also want to work on the free reads that electricsql has"

**DONE =**
- A customer creates a database in the dashboard or SDK.
- They write SQL through psql, REST or WebSocket in the region closest to them (gru, sjc or fra).
- They read it in every region, and subscribe to live data that is served free from Cloudflare's CDN.
- No Turso or libSQL name is visible anywhere.
- ts-auth and ts-dashboard run on it (dogfooding).
- The published benchmarks show the latency targets in section 3.
- **Clean:** the legacy scan (C1) and dead-code checks (C2) report zero hits in every TerraScale repo. No obsolete docs remain (C3).
- **All SDKs work:** all 5 SDKs pass the shared conformance suite against TerraBase from the same commit and against the live API after deploy (C4). Their old document-API package versions are deprecated (C5).

**NOT IN SCOPE:**
- the document API
- leaderless replication
- MVCC
- offline or embedded replicas inside customer apps (possible later)
- dense vector ANN
- full Postgres compatibility claims, unless Q-DIALECT passes

---

## 1. Locked decisions

| # | Decision | Why (appendix) |
|---|---|---|
| D1 | **SQL only.** Delete the document API, lattices, the custom wire protocol, webhooks and document feeds. JSON columns stay | one data model; customer demand (A, C) |
| D2 | **Turso stays under the hood, invisible.** Our names, error codes, docs and SDKs. Turso's MIT license and NOTICE ship in third-party notices | G |
| D3 | **Public surfaces: Postgres wire, SQL over REST, SQL over WebSocket, plus shapes over HTTP GET** (the free-reads read path, which is REST). Hrana and `/libsql` are deleted | user decision |
| D4 | **Writes commit in the closest region.** Smart routing: safe statement shapes commit locally, unsafe ones go to home as a whole transaction (SQLSTATE `40001` retry for psql) | E, I.3 |
| D5 | **Turso Sync runs between our regions only.** Home → region by page pull. Region → home by our streaming push plus **our** atomic home apply (HLC column LWW, conflict log, triggers off for applied changes) | E, I.1, I.3 |
| D6 | **A durable revision layer** (incarnation + checkpoint generation + frame) before any sync serves customers | I.2 checkpoint bug |
| D7 | **One shared process per node** with an LRU cache of open databases. Dedicated containers are a paid Team/Enterprise option | D, I.2 (26–33x less memory) |
| D8 | **Free reads, Electric style:** keep Shapes, sourced from home CDC `full`. Sealed TBS1 ciphertext chunks go to **R2**, served through **Cloudflare's CDN**. Shape/CDN reads are not billed | F |
| D9 | **Region-local fast path for shapes:** the writer's region shows its own writes in milliseconds, and other regions get them after one direct hop. Home confirms after that | F, section 3 |
| D10 | **Object storage: Cloudflare R2 is the default** (shape chunks, files, anything served to users, managed with `cf r2`). **Tigris is for backups and PITR only,** managed with `fly storage … --org terrascale`, never the tigris CLI (user, 2026-10-29) | user decision, F |
| D11 | **WAL mode only; Turso pinned,** and each upgrade is qualified by the integration suite | B, I |
| D12 | **ULID keys; no AUTOINCREMENT** in tables that take local writes (the router sends those home) | I.3 |
| D13 | **Greenfield:** no migrations or shims. Delete old code and data, then re-bootstrap production | workspace rule |
| D14 | **We never use Turso's push or its server-side apply.** Turso Sync is used only for page pull (home → regions). This removes upstream bugs #10178 (lost table), #10147 (doubled triggers) and #10180 (blocked pulls) from our path entirely | I.1, B |
| D15 | **SQL sandbox on every customer statement:** an authorizer denies ATTACH/DETACH, `VACUUM INTO`, `load_extension`, file functions and file or engine PRAGMAs, and hides internal tables (`turso_cdc`, `_ts_*`) from queries and schema listings | B (open server lets SQL escape the filesystem) |
| D16 | **A local ack is durable on 2 nodes in the region** (same region, different fault domain) before it returns. A single-node region is never sold for local writes; it forwards writes home | new; avoids losing acked writes when a node dies before its push |
| D17 | **Home leases and epochs live in ts-orch's existing NATS, stretched into one cross-region JetStream cluster** (user, 2026-10-29: Q-LEASE "change ts-orch's NATS rule"; "prefer re using the nats cluster that we already have and use it to the max"). JetStream is turned on in the mesh NATS on ts-gru-1/ts-sjc-1/ts-fra-1 with routes over private mTLS; lease KV with revision CAS and per-key TTL; every write path carries the fencing epoch. ts-orch's "quorum never spans regions" rule is replaced, recorded in a new ts-orch doc (task N1) | W0-9 (.cos/pivot/nats-topology.md), user decision |
| D18 | **A restore or failover starts a new incarnation.** Replicas never continue incrementally across it; they rebootstrap from a snapshot. Continuous anti-entropy compares home and replica checksums and rebootstraps on mismatch | upstream #8129, #9430 |
| D19 | **Crypto-shredding:** deleting a database or tenant deletes its shape epoch keys and its at-rest keys right away, so cached CDN ciphertext and bucket objects become unreadable immediately | F (deleted objects stay in cache up to 7 days) |
| D20 | **Per-tenant resource governance in the shared process:** statement timeouts, a progress-handler CPU budget, memory and result-size caps, concurrency limits and rate limits | D (noisy neighbours) |
| D21 | **HLC with a clock-skew guard:** chrony on every node, skew exported as a metric; a node whose skew passes 50 ms stops taking local writes and forwards them home | I.3 stale overwrites |
| D22 | **Transport security:** TLS on every public surface (psql with SCRAM or token auth, REST, WebSocket); mTLS on every region link over the private network | B (open server plaintext, no auth) |
| D23 | **Clean slate (user, 2026-10-29: "make sure that everything will be clean and all obsolete and legacy code, docs will be removed and all sdks will be working correctly").** Nothing obsolete survives: legacy code, tests, docs, config, packages, buckets, secrets, DNS and branches are deleted, not deprecated in place. Each repo's CI enforces it with a legacy scan and dead-code checks | user decision |
| D24 | **Go fast, break fast (user, 2026-10-29: "go fast and break fast, we dont have any users yet we are greenfield").** Pivot PRs merge straight to main; production may be broken until cutover (Q4 re-bootstrap). CI on main stays green: every PR builds and passes its integration tests. The pivot COS owns StorageFormat bumps and production resets for the pivot; wipes need no approval | user decision Q-PIVOT-ORDER |
| D25 | **NATS is TerraBase's inter-region fabric, used to the max** (user, 2026-10-29). JetStream streams carry the region→home push (durable, ordered, deduplicated by message id), the direct region→region provisional stream (F8), "new revision" notifications that trigger page pulls instead of polling (R3), conflict and revert events, catalog change events and CDN purge jobs. JetStream KV holds leases, epochs, placement and HLC skew reports. Page bytes still move over Turso Sync's HTTP pull; NATS tells regions when to pull. TerraBase gets its own NATS account with scoped permissions | user decision |

---

## 2. Open decisions (each has a default, so nothing blocks)

| ID | Question | Default until answered |
|---|---|---|
| Q-DIALECT | Public SQL dialect | **Answered by evidence (W0-1, benchmark/bench/2026-10-29-orm-dialect-matrix.md, #896): SQLite dialect on Postgres wire, SQL REST and SQL WebSocket.** No ORM (Prisma, Drizzle, EF Core, SQLAlchemy) completes a stock PostgreSQL workflow on either TerraBase's wire or Turso's `postgres/`; psql/pgcli partly work on Turso's. Market the wire as a tested driver transport, never "Postgres compatible" |
| Q-WEDGE | Launch pitch | "Multi-region SQL for AI apps: writes in the closest region, reads free from the edge, a database per agent, one capped bill" |
| Q-PRICING | Free / $3 / $20 / $50 / $999 vs Turso at $5.99 | Bill writes and storage; shape/CDN reads free; SQL reads have a fair-use allowance per plan; hard caps |
| Q-ENGINE-POLICY | Turso upgrades | Pin, with a qualification gate per upgrade (W0-5). Keep a ready-to-build fork mirror (W0-11) so a hotfix ships in a day; contribute every fix upstream |

**User gates:**
- **G-GO: given (user, 2026-10-29: "re read the COS skill and use it to start the pivot").** Execution runs through T3 threads under the COS skill.
- **G-R2: approved (user, 2026-10-29),** for task F6: create the production R2 bucket, custom domain, cache rule and a bucket-scoped key (stored in OCI Vault). Usage past R2's free tier is billed.
- Production wipes need no gate in alpha.

---

## 3. Architecture

```
 app / agent / ORM / psql
   │ REST · WebSocket · Postgres wire                 shape catch-up (HTTP GET)
   ▼                                                       │
 nearest region node (gru | sjc | fra), one process         ▼
   router ── safe write ─► local commit ──┐         Cloudflare CDN (cache + tiered cache)
         └── unsafe tx ──► forward to home │               │ miss: ~1 read per object per location
   SQL reads ─► local copy                 │               ▼
   shape live tail ◄─ local overlay ◄──────┤         R2 bucket: sealed TBS1 chunks (ciphertext)
          ▲  ◄── direct region→region provisional stream (one hop)        ▲
          │                                │                            │ sealer uploads
          │ page pull (durable revision)   │ streaming push              │
          │                                ▼                            │
        home node: atomic apply · HLC · terrascale.conflicts · CDC full ─┘
          │ backup stream
          ▼
        Tigris: backups, PITR
```

**Latency targets** (estimates to prove in W0-4 and Q2). Measured RTTs: gru↔sjc 168 ms, gru↔fra 181–184 ms, sjc↔fra 137–144 ms.

| Path | Target |
|---|---|
| Local write ack (safe shape) | p50 < 5 ms |
| Forwarded write (unsafe shape) | one RTT to home + commit (137–184 ms measured RTT) |
| **SQL read of your own write, writer's region** | immediate (same local copy) |
| **Shape live update, writer's region** | **p50 < 10 ms** (local overlay; no trip to home) |
| **Shape live update, other regions** | **one one-way hop, about 70–95 ms** (direct provisional stream) |
| Home-confirmed everywhere | about 2 one-way hops, 150–190 ms (streaming push + streaming pull) |
| Shape catch-up (sealed chunks) | Cloudflare edge hit, no origin read (measured 0.999 hit ratio at N = 1000) |

**How the shape fast path works (D9):**
1. **Local overlay.** When a region commits a safe write locally, it appends the row's after-image (with its HLC) to an in-memory per-database overlay and wakes that region's live shape tails right away. A forwarded (unsafe) transaction's response carries its committed rows, so the writer's region injects them as soon as home answers, with no wait for the pull.
2. **Direct provisional stream.** The region also streams the same after-images straight to the other regions (one hop) while it pushes to home. Each receiving region adds them to its overlay.
3. **Home confirms.** Home's CDC entries arrive by pull and supersede overlay entries by primary key and HLC. If home rejects a change (rare after routing), every region emits a `revert` control message with home's row image, and the conflict lands in `terrascale.conflicts`.
4. **Caching stays correct.** Immutable sealed chunks (R2 + CDN) contain **only home-confirmed** changes. Live tails are origin-only, `private, no-store`, so provisional data never enters a shared cache. Shape messages carry `state: provisional | confirmed`, and the SDK merges them for you.

---

## 4. Engineering rules (every task, every PR)

- **Model routing (user, 2026-10-29):**

  | Model | Target | Use for |
  |---|---|---|
  | Haiku 5.5 | `claude-haiku-5-5` (explicit id, never the alias) | **small jobs of 20 minutes or less**: one-file edits, renames, deletions from an inventory, doc page removals, config, notices |
  | Sol 6.1 | `delegate_task` `{"providerInstanceId":"codex","model":"gpt-6.1-sol","options":{"reasoningEffort":"high"}}` | **medium tasks, every review and every refactor** |
  | Opus 5.5 | `claude-opus-5-5` | **everything else**: engine, sync, routing, apply, failover, auth, billing, production infrastructure |

- **Best practice first.** No shortcuts or temporary workarounds. A task is done only when its named integration tests pass in CI on the exact merged head and every review finding is fixed. When best practice and speed conflict, best practice wins and the timeline moves.
- **No code comments.** New and rewritten code has no `//`, `/* */` or `///` comments. OpenAPI endpoint descriptions stay, because they are product docs, not comments. When a task rewrites a file, it drops that file's comments. XML documentation is not required either: make types and members `internal` wherever possible. Server projects (TerraBase, ts-auth, ts-dashboard) keep `GenerateDocumentationFile=true`, because IDE0005 needs it, and add `NoWarn CS1591`. That suppression is approved by the user's no-comments rule (COS ruling, 2026-10-29). SDKs are not covered.
- **Integration tests only, no unit tests.**
  - Tests start real TerraBase processes (1 node, or 3 nodes as gru, sjc and fra with injected latency) on real Turso files.
  - They drive only real surfaces: psql, REST, WebSocket, shapes HTTP, and the internal region links.
  - No mocks of our own components. Unit tests of deleted code are deleted with it.
  - **SDK repos** can't run TerraBase (workspace rule), so their integration tests run against recorded contract fixtures from the monorepo suite.
  - Every task names its proving integration tests in the PR body.
- **Process:**
  - Branch from fresh `origin/main` in the thread's own worktree. One open PR per repo per thread, under about 400 changed lines (pure deletions exempt), ready for review.
  - **A fresh Sol reviewer reviews every PR.** Fix every finding, then merge on green CI for the exact head.
  - Feature PRs write `Needs format bump`; the deploy lane batches bumps.
  - Benchmarks run 3 repeats; flaky-fix proof is 5 repeats.
  - Briefs start with `WRK:`.
- **Capacity:**
  - Opus limits are low, so run **at most 4 Opus tasks at once**, critical path first.
  - Sol can run wide (8 or more at once); Haiku runs as wide as needed.

---

## 5. Parallel task plan

Effort is in agent-days (estimates). "Dep" lists tasks that must merge first. "Proof" names the integration tests that close the task.

### Wave 0: proofs and prep (week 1, all parallel)
| ID | Task | Model | Dep | Effort | Proof |
|---|---|---|---|---|---|
| W0-1 | ORM and dialect matrix: Prisma, Drizzle, EF Core/Npgsql, SQLAlchemy, psql and one BI tool through TerraBase's Postgres wire and Turso's `postgres/` translation. Closes Q-DIALECT | Sol | – | 2–3 | published pass/fail matrix |
| W0-2 | Durable revision prototype: an official client with pending writes through checkpoint, restart, rewrite and restore | Opus | – | 3–5 | 0 lost acked writes, 0 skipped commits, 3 repeats |
| W0-3 | CDC `full` qualification: before/after images through checkpoint, ALTER and restart; `turso_cdc` growth per day | Sol | – | 2 | report |
| W0-4 | Lag lab on dev hosts with netem 70/90 ms one-way: streaming pull, streaming push, direct region→region stream | Sol | – | 2–3 | p50/p99, 3 repeats |
| W0-5 | Turso pin: qualify 0.8.3 (and 0.9.0-pre) with the spike suites | Sol | – | 1–2 | pass/fail table |
| W0-6 | Deletion inventory: exact files, tests, docs pages and config keys per area (engine, auth, dashboard, 5 SDKs, docs, orch/status/terraform/landing) | Haiku ×10 | – | ≤20 min each | `.cos/pivot/inventory-*.md` |
| W0-7 | Production R2 + CDN design: bucket, location hint, custom domain, cache rule, Smart Tiered Cache, bucket-scoped token, OCI Vault path, cost at 1/10/100 TB | Sol | – | 1 | doc, ready for G-R2 |
| W0-8 | 3 design-partner interviews | user | – | – | notes |
| W0-9 | Check ts-orch NATS topology: JetStream on, 3 replicas across gru/sjc/fra, KV with CAS | Haiku | – | ≤20 min | topology note |
| W0-10 | Router parser: export Turso's own SQL parser through the native shim, so routing reads the exact grammar the engine runs | Sol | – | 2–3 | parser round-trip on the engine's SQL test corpus |
| W0-11 | Turso fork mirror: our GitHub fork at the pinned tag, reproducible native builds (x64 + ARM64) in CI, patch queue | Sol | – | 1–2 | CI builds the pinned tag from the fork |
| N1 | **ts-orch NATS to the max:** turn on JetStream in the mesh NATS on all 3 nodes; one cross-region JetStream cluster (gru/sjc/fra routes over private mTLS, election timeouts sized to the measured RTTs); a scoped TerraBase account; KV buckets (leases, epochs, placement) and streams (push, provisional, revisions, events) with limits; new doc replacing the per-region quorum rule; health metrics | Opus (prod infra) | W0-9 | 4–6 | 3-node JetStream healthy; KV CAS from all regions; kill one region: quorum holds with 2 |

### Wave 1: SQL-only core (weeks 2–4)
| ID | Task | Model | Dep | Effort | Proof |
|---|---|---|---|---|---|
| E1 | Native SQL storage: the executor runs on real Turso tables per database. DDL passthrough; FK, UNIQUE and CHECK enforced; JSON columns | Opus | W0-1 | 10–15 | CRUD, constraints, transactions and rollback over REST and psql |
| E2 | SQL catalog database (orgs, databases, home region, placement, leases) replacing the document catalog | Opus | – | 8–12 | create/drop/list, restart, 3-node catalog |
| E3 | Gateways on native SQL: REST (batch, transaction), Postgres wire, **new WebSocket SQL protocol (ours)** | Sol | E1 | 5–8 | the same results on all 3 surfaces |
| E4 | Delete the document API, lattices, wire, webhooks, document feeds and Hrana (one PR per area, from the W0-6 inventory) | Sol (refactor) | E4a now (Hrana, wire, webhooks); E4b after E1 (documents, lattices, feeds) | 4–6 | build plus integration suite green |
| E5 | Delete leaderless replication (peer streams, joins, repair, refill, remote acks) | Sol (refactor) | – (starts now) | 2–3 | suite green, single region |
| E6 | Shared-process handle LRU, per-tenant quotas, backpressure | Sol | E1 | 3–5 | 1,000 databases, eviction keeps data, RSS budget |
| E7 | Error mapping to TerraScale codes; no engine names in any response; PRAGMA allow-list | Sol | E3 | 2–3 | response scan for engine names on all surfaces |
| E8 | Backups and PITR on native tables (CDC → Tigris), independent of replication | Sol | E1 | 3–4 | backup, restore, PITR |
| E9 | Leftover cleanups from the inventory: config keys, metric names, dead options, links | Haiku (many) | E4 | ≤20 min each | build green |
| E10 | SQL sandbox (D15): authorizer, denied statements and functions, hidden internal tables | Opus | E1 | 3–5 | every denied statement refused on all 3 surfaces; internal tables invisible |
| E11 | Resource governance (D20): timeouts, CPU budget, memory/result caps, concurrency and rate limits per tenant | Opus | E1, E6 | 4–6 | a runaway query in tenant A leaves tenant B's p99 unchanged |
| E12 | Transport security (D22): TLS on psql/REST/WebSocket, SCRAM and token auth on psql | Opus | E3 | 3–5 | plaintext and bad credentials refused on every surface |
| RF1 | Refactor pass over wave 1: duplicate code, dead paths, naming | Sol | E1–E8 | 2–3 | suite green |

### Wave 2A: multi-region writes and replicas (weeks 2–10; R1 starts with E1)
| ID | Task | Model | Dep | Effort | Proof |
|---|---|---|---|---|---|
| R1 | Durable revision layer in production; checkpoint policy with a retained page-history window; restore or failover starts a new incarnation (D18) | Opus | W0-2 | 6–9 | sync through checkpoint, restart, rewrite, restore; replica rebootstraps after restore |
| R2 | In-process sync host: native shim artifact (x64 + ARM64), page pull per database, internal auth, private network only | Opus | R1 | 8–12 | 1,000 databases, 0 wrong-tenant rows |
| R3 | Region replicas: one synced copy per selected region, bootstrapped from a snapshot and then pulls triggered by NATS revision notifications (D25); position tokens; lag metric | Opus | R2, E2 | 6–10 | 3-node replicas equal; read-your-writes token; new region joins a 10 GB database |
| R4 | Write router on the W0-10 parser: classifier (Appendix E table), `40001` retry, fingerprint memory, session/header/table overrides | Opus | E3, W0-10 | 8–12 | one test per routing row |
| R5 | Whole-transaction forwarding to home (psql connection pinning, REST batch, WebSocket); the response returns the committed rows | Sol | R4, E2 | 4–6 | forwarded transaction over all 3 surfaces |
| R6 | Home apply: all or nothing per transaction, HLC column LWW, triggers off for applied changes, FK re-check, `terrascale.conflicts`, write states. A rejected transaction never blocks the stream | Opus | R2 | 8–12 | every spike I.3 case, with no silent loss and no stalled stream |
| R7 | Streaming push region → home over a JetStream stream (D25: batching, transaction boundaries, HLC stamps, dedupe by message id); `durability=home` ack | Opus | R6 | 5–8 | lag targets on netem |
| R8 | Home lease, failover and fencing on the stretched ts-orch NATS JetStream KV (D17, after N1): epoch on every write path; promote the most caught-up replica | Opus | R3, N1 | 8–12 | kill home mid-write: no acked loss, no second writer |
| R9 | Placement API: home region per database, all-regions mode, move home | Sol | E2, R3 | 3–5 | move home under load |
| R10 | In-region quorum ack (D16): replicate each local commit to a second node in another fault domain before acking | Opus | R7 | 5–8 | kill the acking node: no acked write lost |
| R11 | Anti-entropy (D18): periodic home/replica checksums per table range, automatic rebootstrap and an alert on mismatch | Sol | R3 | 3–5 | injected divergence is detected and healed |
| R12 | HLC and the skew guard (D21): chrony checks, skew metric, local writes forwarded when skew is too high | Sol | R6 | 2–3 | injected 100 ms skew: no stale overwrite |
| R13 | Router differential tests: random workloads run in local mode and in home-only mode must reach the same final state, or log a visible conflict | Opus | R6, R7 | 3–5 | 10k random transactions, 3 repeats, 0 silent differences |
| RF2 | Refactor pass over wave 2A | Sol | R1–R9 | 2–3 | suite green |

### Wave 2B: free reads, R2 and CDN (weeks 3–9, parallel with 2A)
| ID | Task | Model | Dep | Effort | Proof |
|---|---|---|---|---|---|
| F1 | Shapes sourced from home CDC `full` (replaces `_tb_feed`); sealed chunks hold only home-confirmed changes | Opus | E1, W0-3 | 5–7 | shape equals table after 100k operations |
| F2 | SQL shapes: table + WHERE subset + columns; move-in/move-out from before/after images; DDL gives a new handle and `409` | Opus | F1 | 4–6 | filter and move-in/out cases |
| F3 | Snapshot at a revision for SQL tables | Sol | F1 | 2–3 | snapshot + tail equals table |
| F4 | Table-level read grants, end-user tokens, TerraScale header names | Sol | F2, E2 | 2–3 | denied and allowed readers |
| F5 | `turso_cdc` retention trim tied to the shape floor and the backup position | Sol | F1, E8 | 2–3 | bounded size under load |
| F6 | **Production R2 + Cloudflare CDN** via `cf`: bucket, custom domain `shapes-…terrascale.tech`, cache rule, Smart Tiered Cache, bucket-scoped token in OCI Vault, purge queue on | Opus (prod infra) | W0-7, **G-R2** | 1–2 | Slow suite: CDN `HIT`; R2 `GetObject` = 1 per object per location |
| F7 | **Local overlay:** local commits and forwarded responses feed region live tails immediately; `provisional`/`confirmed`/`revert` messages | Opus | F2, R4 | 5–7 | same-region shape update p50 < 10 ms |
| F8 | **Direct region→region provisional stream over NATS (D25)**, superseded by home confirmations; falls back to the home path when a region link is down | Opus | F7, R7 | 5–8 | other-region update in one hop, revert on rejection, link cut handled |
| F9 | Region-local live tails from the replicated `turso_cdc` | Sol | F1, R3 | 2–3 | tails served in every region |
| F10 | Free-reads benchmark on the SQL source: N = 1/10/100/1000, 3 repeats, published | Sol | F6, F2 | 1–2 | `benchmark/bench/…` |
| F11 | Crypto-shredding (D19): delete keys on database or tenant deletion; purge queued CDN URLs | Sol | F6 | 2–3 | deleted tenant's cached objects can't be opened |
| F12 | Live-tail limits: per-tenant connection caps, one flight per offset, WebSocket "new data" hints | Sol | F7 | 2–3 | 10k live clients on one shape: origin work stays flat |
| RF3 | Refactor pass over wave 2B | Sol | F1–F9 | 1–2 | suite green |

### Wave 3: product surfaces (weeks 4–11, parallel)
| ID | Task | Model | Dep | Effort | Proof |
|---|---|---|---|---|---|
| P1 | ts-auth onto SQL tables (40 collections): unique keys, conditional writes, credential and deletion fences | Opus | E3, P4b | 12–20 | ts-auth suite against TerraBase from the same commit |
| P2 | ts-dashboard data onto SQL (27 collections) | Sol | P4b | 6–10 | dashboard suite |
| P3 | Dashboard features, one task per page: SQL editor, table browser, conflict console, regions and lag, placement, shapes inspector; MCP SQL tools (document tools deleted) | Sol (pages), Haiku (small UI bits) | E3, R6, F2 | 6–10 total | page flows, PR previews |
| P4a–e | Thin SDKs: **P4b ts-dotnet first** (dogfood), then ts-js (+ `shape().subscribe()`, React/Vue hooks, provisional merge), ts-go, ts-python, ts-rust. SQL REST + WebSocket + shapes; error codes; document API deleted | Sol each | E3, F7 | 4–8 each | contract-fixture integration tests |
| P5 | ts-docs: delete collection/feed/replication pages (Haiku, per page group); write SQL, regions, write routing, free reads, the `llms.txt` SQL contract (Sol) | Haiku + Sol | E3, R4, F2 | 6–10 | docs build |
| P6 | Billing and metering: writes and storage billed, shape/CDN reads free, SQL reads with fair use, hard caps | Opus | E2 | 5–8 | metering end to end |
| P7 | Database per agent: create in under 100 ms, fork from a snapshot, TTL, scoped tokens | Sol | E2, E8 | 4–6 | create/fork/expire timings |
| P8 | ts-orch, ts-status, terraform provider, ts-landing, benchmark probes and copy | Haiku (copy, probes), Sol (terraform resource) | E3 | 6–10 | each repo's CI |
| P9 | Third-party notices: Turso MIT license + NOTICE in every shipped artifact | Haiku | – | ≤20 min | notice file present |
| P10 | Contract fixtures: the monorepo integration suite records request/response fixtures per release; SDK repos run against them | Sol | E3 | 2–3 | SDK CI fails when a fixture drifts |

### Wave 4: qualify and launch (weeks 11–14)
| ID | Task | Model | Dep | Effort | Proof |
|---|---|---|---|---|---|
| Q1 | Chaos suite: region kill, 60 s partition, checkpoint and restore during sync, home failover, CDN purge, rolling deploy with live sync streams | Opus | R8, F8, R10 | 4–6 | all pass, 3 repeats |
| Q2 | Published benchmarks on real gru/sjc/fra: write ack, shape latency per path (section 3), density, free reads | Sol | R7, F10 | 2–3 | `benchmark/bench/…`, 3 repeats |
| Q3 | Final review of every public surface for Turso leaks and auth gaps | Sol | all | 1–2 | findings fixed |
| Q4 | Re-bootstrap production (greenfield), deploy to all regions, re-seed the Demo org | Opus (prod infra) | all | 1–2 | live DONE check (section 0) |
| Q5 | Automated restore drills: every backup restored daily on a scratch node, `integrity_check` and checksum compare; alert on failure | Sol | E8 | 2–3 | drill log green 7 days in a row |
| Q6 | Observability and SLO alerts: lag, router decisions, conflict rate, revert rate, skew, quorum-ack latency, CDN hit ratio, per-tenant governance trips | Sol | R7, F7 | 3–4 | alerts fire on injected faults |

### Wave C: clean slate and SDK correctness (runs alongside every wave; the final gate is part of DONE)
| ID | Task | Model | Dep | Effort | Proof |
|---|---|---|---|---|---|
| C1 | Legacy scanner: one term list plus allow-list (collections, documents, feeds, lattices, leaderless, Hrana, libsql, `/libsql`, wire protocol, webhooks, Fly.io, Turso in public surfaces, `IDocumentService`…), shipped as a reusable script and a CI step in every TerraScale repo (one small PR per repo) | Sol (script), Haiku (per-repo CI steps) | W0-6 | 2–3 + ≤20 min per repo | the scan runs in CI everywhere; every hit is listed with its owner task |
| C2 | Dead-code checks as CI errors: .NET unused-member analyzers in TerraBase/ts-auth/ts-dashboard/ts-dotnet, `knip` for ts-js, `deadcode`/`staticcheck U1000` for ts-go, `vulture` for ts-python, `dead_code = deny` for ts-rust | Sol | E4, P4a–e | 3–4 | CI fails on unused code in every repo |
| C3 | Docs clean-out: delete obsolete pages in ts-docs, `terrascale/docs/terrabase` (replication, documents, old shapes and free-reads design docs, superseded by the new SQL docs), SDK READMEs and examples; link checker in ts-docs CI; obsolete `.cos` plans moved to `.cos/archive/` | Haiku (per page group), Sol (rewrites, link checker) | W0-6, P5 | 4–6 | link check green; legacy scan zero on docs |
| C4 | **SDK conformance suite:** one shared behaviour spec (SQL over REST and WebSocket, transactions, write routing overrides, `40001` retry, shapes subscribe with provisional/confirmed/revert, error codes, auth, retries, pagination). Runs (a) in each SDK repo against the P10 contract fixtures, (b) in the monorepo CI against TerraBase built from the same commit with every SDK at its main branch, (c) as a post-deploy smoke against the live public API. Every README and example compiles and runs inside it | Opus (spec and harness), Sol (per-SDK adapters) | E3, F7, P4a–e, P10 | 6–9 | all 5 SDKs green on (a), (b) and (c) |
| C5 | Packages: publish the new 0.0.X of every SDK; deprecate every old document-API version (npm deprecate, NuGet unlist, PyPI yank, crates yank, Go `retract`) | Haiku per registry | C4 | ≤20 min each | registry pages show only SQL versions as current |
| C6 | Resource sweep: stale branches, PRs and worktrees; unused CI secrets; obsolete OCI Vault secrets (e.g. leaderless peer and e2e backup keys); the parked Tigris `terrascale-terrabase-e2e-backups`; unused DNS records and Workers | Sol | Q4 | 1–2 | sweep report with every deletion |
| C7 | **Final clean gate:** C1 and C2 zero in every repo, C3 link check green, C4 green on all 5 SDKs (live included), C6 report done. A fresh Sol reviewer signs off | Sol (review) | C1–C6, Q4 | 1 | gate report linked from DONE |

### Critical path and timeline (estimates)
- **Longest chain:** W0-2 → R1 → R2 → R6 → R7 → R10 → Q1 → C7, about 43–66 agent-days. The second chain is W0-10 → E1 → E3 → R4 → F7 → F8.
- **Wall time with this parallelism: about 11–14 weeks** to DONE. That's 2 more than the speed-first version, the cost of putting best practice first (D14–D22), assuming no more than 4 Opus tasks run at once.
- **Count:**
  - Opus: 24 tasks, the hard parts only.
  - Sol: about 40 tasks, including every review and refactor.
  - Haiku: dozens of small jobs, inventories and deletions.

---

## 6. Issue and risk register: every finding has a fix and a task

Nothing below is "accepted". Each row closes when its task's integration proof passes.

### Engine (Turso)
| # | Issue (source) | Best-practice fix | Task |
|---|---|---|---|
| 1 | Pre-1.0 engine: 1,144 open issues, 14 "corruption?" (B) | pin a version; qualify each upgrade; fork mirror with reproducible builds; daily restore drills with `integrity_check` | W0-5, W0-11, Q5 |
| 2 | CDC is Early Preview with no retention (B) | qualify `full` mode; our trim tied to the shape floor and backup position | W0-3, F5 |
| 3 | CDC refuses ALTER (I.1) | DDL is home-only; a schema change gives shapes a new handle; replicas get DDL through page pull, never CDC replay | R4, F2, R3 |
| 4 | Triggers fire twice on replay (#10147) | no Turso push (D14); our apply runs with triggers off, so a trigger fires once, where the write commits | R6 |
| 5 | MVCC bootstrap fails (#8725); snapshot isolation only | WAL only (D11); MVCC re-checked at each upgrade gate | W0-5 |
| 6 | Encryption experimental; `VACUUM INTO` writes plaintext copies (B) | OCI volume encryption always; per-database `aegis256` qualified before use; `VACUUM INTO` denied by the sandbox | E10, W0-5 |
| 7 | Turso's Postgres layer: trust auth, plaintext (B) | never exposed; our own Postgres wire with TLS and SCRAM/token auth; translation code borrowed only if W0-1 passes | E12, W0-1 |
| 8 | 7.8 MiB page cache per connection (B) | LRU of open databases plus a per-tenant cache budget | E6, E11 |
| 9 | No dense vector ANN (B) | not promised; exact search documented for small sets | P5 |
| 10 | Customer SQL can reach the filesystem (ATTACH, file functions; B) | SQL sandbox (D15) | E10 |
| 11 | Internal tables (`turso_cdc`) visible to customers; engine names in errors (G) | hidden by the authorizer; mapped error codes; leak scan | E10, E7, Q3 |

### Sync and replication
| # | Issue (source) | Best-practice fix | Task |
|---|---|---|---|
| 12 | Push is not atomic: partial apply, lost table (#10178) | D14: Turso push never used; our atomic apply | R6, R7 |
| 13 | A replay failure blocks all pulls, with no dead letter (#10180) | our apply records the rejection in `terrascale.conflicts` and moves on | R6 |
| 14 | Checkpoint resets the revision: silent stale clients (I.2) | durable revision (D6) | W0-2, R1 |
| 15 | WAL grows 0.66–1.22 GiB/day without checkpoints (I.2) | checkpoint policy with a retained history window; older clients rebootstrap | R1 |
| 16 | Silent loss after restore (#8129), divergence (#9430) | new incarnation on restore or failover; anti-entropy with auto-rebootstrap (D18) | R1, R11 |
| 17 | Sync stalls (#9911) | lag SLO alerts and a stream watchdog that reconnects and resumes from the durable revision | R3, Q6 |
| 18 | Dev server: serial, plaintext, no auth, 503 cap, no LRU (I.2) | in-process host with mTLS, per-database auth, LRU and backpressure | R2, E6, E12 |
| 19 | Polling lag 0.7–1.3 s at 150 ms per hop (I.3) | streaming pull and streaming push | R3, R7, W0-4 |
| 20 | Full copy per region multiplies storage cost (new) | per-database region set (home + chosen regions); all-regions only by choice or for platform databases | R9, P6 |
| 21 | A new region replica of a big database bootstraps slowly (new) | bootstrap from a snapshot, then pull from its revision | R3 |

### Write model
| # | Issue (source) | Best-practice fix | Task |
|---|---|---|---|
| 22 | Stale region overwrites newer data (I.3) | HLC column LWW with the skew guard (D21) | R6, R12 |
| 23 | UNIQUE collision: partial apply, silent drop (I.3) | UNIQUE inserts and changes routed home; atomic apply | R4, R6 |
| 24 | FK outcome depends on order (I.3) | parent deletes and FK changes home-only; child inserts re-checked at home and visibly reverted if orphaned | R4, R6, F7 |
| 25 | Balance lost update and overdraft (I.3) | relative updates and read-then-write transactions routed home | R4 |
| 26 | AUTOINCREMENT ids collide (I.3) | rowid/AUTOINCREMENT inserts routed home; ULID keys recommended (D12) | R4 |
| 27 | Router misclassifies a write (new) | conservative default (unknown means home); engine parser (W0-10); differential tests | R4, R13 |
| 28 | Node dies before pushing an acked local write (new) | quorum ack on 2 nodes in the region (D16) | R10 |
| 29 | Split-brain after failover; catalog as a single point of failure (new) | leases and epochs in the stretched ts-orch NATS JetStream KV, fencing on every write (D17) | N1, R8 |
| 30 | Long schema changes block writers (new) | DDL home-only; large-table rewrites flagged in the dashboard and run as online copy-and-swap | R4, P3 |

### Free reads, CDN and R2
| # | Issue (source) | Best-practice fix | Task |
|---|---|---|---|
| 31 | `turso_cdc` grows forever (F) | trim tied to the shape floor and backup position | F5 |
| 32 | Purge can fail; a deleted tenant's objects stay cached up to 7 days (F) | crypto-shredding (D19) | F11 |
| 33 | Live tails at the origin scale with clients (F) | one flight per offset, connection caps, WebSocket hints | F12 |
| 34 | Own writes were slow to show in shapes (user rejected the 150–190 ms wait) | local overlay plus a direct region→region stream (D9) | F7, F8 |
| 35 | Provisional data later reverted (new) | rare after routing; explicit `revert`; never cached; revert rate tracked as an SLO | F7, Q6 |
| 36 | R2 costs past the free tier; production credentials (F) | G-R2 approval; bucket-scoped token in OCI Vault; cost model at 1/10/100 TB | W0-7, F6 |
| 37 | Tigris has no CDN tier (F) | R2 behind Cloudflare for free reads; Tigris stays for backups (D10) | F6 |

### Platform, product and process
| # | Issue (source) | Best-practice fix | Task |
|---|---|---|---|
| 38 | psql users and ORMs expect the Postgres dialect (Q-DIALECT) | matrix first; claims follow the result | W0-1, P5 |
| 39 | Noisy neighbours in the shared process (D) | resource governance (D20) | E11 |
| 40 | Public surfaces need real security (new) | TLS, SCRAM/token auth, rate limits, final leak and auth review | E12, E11, Q3 |
| 41 | Restarts during auto-deploy cut live sync streams (new) | graceful drain; streams resume from the durable revision; covered by chaos tests | R2, Q1 |
| 42 | SDK repos can't run TerraBase (workspace rule) | recorded contract fixtures from the monorepo suite | P10 |
| 43 | Supabase now owns Turso (C) | own every surface; fork mirror; upstream contributions | W0-11, D2 |
| 44 | "Cheaper Turso" isn't a moat (C) | wedge: closest-region writes, free edge reads, regions Turso lacks, capped bill | Q-WEDGE, P5 |
| 45 | Turso's $5.99 unlimited plan (C) | free reads plus hard caps; pricing decision before launch | Q-PRICING, P6 |
| 46 | Engine bugs hidden by missing observability (new) | SLO alerts on lag, conflicts, reverts, skew, quorum, CDN hits, governance | Q6 |
| 48 | Legacy code, docs or packages survive the pivot (user, D23) | legacy scan and dead-code checks fail CI; docs link check; package deprecation; final clean gate | C1, C2, C3, C5, C7 |
| 49 | SDKs drift from the API or break silently | shared conformance suite in 3 places (fixtures, same-commit, live) | C4, P10 |
| 47 | Opus capacity is limited (workspace) | at most 4 Opus tasks at once; medium work on Sol; small work on Haiku | section 4 |

---

# Appendices: the research behind the plan

Appendix section numbers replace the old document's numbers. Where an appendix and sections 0–6 disagree, sections 0–6 win.


## Appendix A. TerraBase today

TerraBase is about 108k lines of C# (physical lines including blanks). The main areas, in lines:

| Area | Lines | In a SQL-only world |
|---|---:|---|
| Replication (peer streams, joins, repair, refill, remote acks) | 7.5k | delete |
| Documents (CRUD, queries, transactions, schemas, TTL, feeds) | 7.4k | delete |
| Shapes (snapshot plus live-tail sync, free reads T0/T1/T2) | 6.8k | **keep and re-source from SQL change capture** (Appendix F) |
| Lattices (HLC/LWW, counters, sets) | 1.4k | delete |
| Wire (custom document protocol), Webhooks, Actors | 2.7k | delete |
| Turso engine wrapper | 23.3k | split: keep files, sessions, encryption, transactions, backup capture; remove lattice, feed, shape and repair code |
| Management (lifecycle, auth, quotas, usage, catalog) | 24.1k | split: the catalog is a replicated set of documents today |
| Sql 4.3k / Postgres 2.2k / Hrana 1.2k | 7.7k | keep Sql and Postgres, delete Hrana (Appendix E); but SQL writes go through `IDocumentService.ApplyAsync` today (`SqlExecutor.cs:635`) |
| Backup (snapshots, CDC to Tigris, PITR) | 5.9k | keep, decoupled from replication |

Facts that shape the pivot:
- **SQL is built on documents.** Each SQL table is a collection with `id TEXT, doc TEXT`, and SQL columns are `json_extract(doc, …)` (`TursoStorageTransaction.Relational.cs:86`). Foreign keys are not enforced.
- **SQL already replicates leaderless across regions, and any region accepts SQL writes** (`tests/TerraBase.Fast/Sql/SqlReplicationTests.cs:44`).
- **Our replication and our backups already ship `turso_cdc` rows, not WAL frames.** The change-capture plumbing exists.
- **Our own services don't use multi-region writes.** ts-auth is pinned to gru (`App.cs:32`), and the dashboard keeps its data in gru on purpose.
- **Estimated removal:** about 45 to 55k lines of production code, plus about 46k lines of replication and document tests.

Who breaks (engineer-days, estimated):

| Repo | What changes | Days |
|---|---|---:|
| terrascale engine | Native SQL storage, SQL catalog, new replication | 47–73 |
| ts-auth | 40 collections onto SQL tables: unique keys, versioned conditional writes, credential and deletion fences | 12–20 |
| ts-dashboard | 27 collections, data browser, MCP document tools, placement cards | 15–25 |
| 5 SDKs | Delete the document API (25–45% of each surface); thin SQL REST/WebSocket and shape clients, no local replica | 20–35 |
| ts-docs | About 440 pages mention collections, feeds or replication | 6–10 |
| orch, status, terraform, landing, benchmark | Probes, canaries, collection resource, copy | 12–21 |
| **Total** | | **about 112–184** (plus free reads, routing and fast path; see section 5) |

---

## Appendix B. Turso, read from the source

### Engine (`03-core-engine.md`)
| Feature | State | Use it for |
|---|---|---|
| WAL mode | stable path | **default storage mode** |
| MVCC / `BEGIN CONCURRENT` | snapshot isolation (not serializable). The manual says "unsuitable for critical data"; no multiprocess coordination | later, after qualification |
| CDC (`turso_cdc`, modes id/before/after/full, DDL as `sqlite_schema` changes) | "Early Preview"; no built-in retention | replicas, backup, change feeds (we already use it) |
| IVM (live materialized views) | experimental; broad SQL coverage, but no MVCC, no customer streaming, no indexes on views | maybe later, as a building block for live queries |
| Postgres frontend (`postgres/`: parser, pg_catalog, pgwire) | experimental; trust auth and plaintext only, one DB handle, text-only parameters, no LISTEN/NOTIFY | borrow its translation and catalog code; keep our own wire service |
| Vector search | exact functions; dense ANN still on the roadmap | brute-force search for small sets; ANN is a gap |
| Full-text search (Tantivy, `USING fts`, BM25) | experimental | we already use it |
| JSON / JSONB, `->`, `->>` | broad | JSON columns stay a feature |
| Encryption (AES-GCM, AEGIS) | experimental; `VACUUM INTO` writes an unencrypted copy | we already use `aegis256` |
| io_uring async I/O | available | driver patch, 2–5 days |
| Many databases per process | a shared per-file registry; each connection's page cache defaults to 2,000 pages (about 7.8 MiB capacity) | capped handle cache; density to be measured |

Quality: strong testing (deterministic simulation, fuzzing, Antithesis, TLA+). Very fast pace: 3,621 non-merge commits in 6 months, and 9 stable releases out of 66 tags. Still pre-1.0: 1,144 open issues, with labels for correctness (136), compatibility (152), mvcc (51), sync (16) and "corruption?" (14). Upgrading 0.8.2 to current main brings 1,667 commits of fixes (Postgres, MVCC snapshots, CDC ids, .NET pooling).

### Sync (`01-sync-engine.md`)
- **Protocol:** `POST /v2/pipeline` (Hrana v2 JSON: push and remote SQL) and `POST /pull-updates` (protobuf; WAL pages or the MVCC logical log). The open server already routes `/db/<name>/…` for many databases in one process.
- **Push sends generated SQL with captured values, not the app's original statement.** Updates with a change mask send only the changed columns, so two regions editing different columns of the same row both survive. Upserts overwrite the whole row. `counter = counter + 1` becomes a captured value, not an increment.
- **Biggest bug: push is not reliably atomic.** A UNIQUE, CHECK or FK failure can leave the transaction open. Later steps, the watermark update and COMMIT still run. That's a silent partial-apply path (#10178 reports a lost table).
- **A persistent replay failure blocks pulls** (#10180). There's no dead-letter queue.
- **Conflict hooks (transforms) exist only in JS.** The native layer has `use_transform=false` and a `todo!()`.
- **AUTOINCREMENT ids collide across replicas.** Use ULID/UUID keys (we already do: plain ULIDs).
- **Triggers can fire twice on replay** (#10147).
- **Partial sync caches pages, not rows**, so it's no substitute for per-row access control. MVCC logical sync rejects partial sync and encrypted remotes.
- **The open server is dev-only:** plaintext, connections handled serially, no auth, no HA, unbounded WAL growth, and SQL can escape filesystem boundaries. Turso Cloud's production server is not open source.
- Activity: 32 non-merge sync commits in the last 90 days, and 16 open sync issues (#8129 silent loss after restore, #9430 divergence, #8725 bootstrap, #9911 stalls).

### Bindings (`02-bindings-sdks.md`)
| Our SDK | Upstream sync client | Notes | Days to wrap |
|---|---|---|---:|
| ts-dotnet | `TursoSyncDatabase` (NuGet 0.8.3) | auto-sync timer only pulls; we schedule pushes | 4–7 |
| ts-js | `@tursodatabase/sync` (native) + `sync-wasm` (browser, OPFS) | two entry points; no Intel Mac; 20–50 MB packages | 8–13 |
| ts-go | `tursogo` via **purego (no cgo)** | no high-level encryption options; no Windows ARM64 | 4–7 |
| ts-python | `pyturso` (PyO3 wheels) | **no Windows wheels** | 5–8 |
| ts-rust | `turso` crate, `sync` feature | simplest | 3–5 |
| shared | auth refresh, reconnect, pending/accepted/rejected state, platform checks | | 6–10 |

Java and C have no sync wrapper. React Native has one (iOS/Android, 360 MB unpacked).

---

## Appendix C. Market

- **Supabase is acquiring Turso** (2026-10-02). Their pitch is "each agent deserves its own isolated database ... at millions of databases", with an upgrade path to Postgres. Turso Cloud has 10 AWS regions including São Paulo (added 2026-10-01), MCP, branching, PITR and AgentFS.
- **Turso pricing to beat:** Free (100 DBs, 5 GB) and Developer at $5.99/month (unlimited DBs, 9 GB).
- **Gaps we can use:**
  - Turso Cloud **deprecated Data Edge global replicas for new users**. Turso Sync syncs to one region.
  - Turso has **no Frankfurt or San Jose region**.
  - Cloudflare D1 has **no South America region** and a 10 GB limit.
  - MongoDB **ended Device Sync**, and InstantDB Cloud is shutting down.
- **Demand:**
  - SQL is rising: in Stack Overflow 2026, PostgreSQL is used by 57.9% and SQLite by 42.1%.
  - MongoDB fell from 24% to 19%, but **document databases aren't dead**: Convex, Firestore and MongoDB keep investing.
  - AI app builders default to SQL (Lovable to Supabase; Bolt and Replit ship their own SQL databases).

---

## Appendix D. Tenancy: one process vs a container per tenant

| Option | For | Why |
|---|---|---|
| **Shared process, many tenants** (default) | Free, Starter, Pro | Thousands of databases per node, instant create, capped open-handle cache, existing QoS. This is what makes "really cheap" true. |
| **Dedicated container, same binary in single-tenant mode** | Team, Enterprise | Isolation, its own CPU, compliance, sold as a premium. ts-orch already deploys an app in about 10 s warm. |
| Function per tenant | not for storage | Cold starts, and one owner per WAL. Fits later as "compute next to data". |

**Measured (I.2):**
- One shared process held 1,000 databases at 212–250 MiB RSS, and a new tenant's first pull took 2.4–3.1 ms. That's about $0.03 per tenant-month of A1 compute.
- One process per database used 21 MiB RSS each and about 2 idle cores per 1,000 processes.
- **The shared process wins by 26–33x on memory.**

---

## Appendix E. Write model design: write to the closest region

> "if we write home all the time it will be slow to write, i want to write to the closest region and then sync to home using turso sync, i do not want to expose libsql i just want to expose the psql protocol and sql over rest apis and sql over websocket"

### What changes
- **Public surface is 3 things:** the Postgres wire, SQL over REST and SQL over WebSocket. No Hrana, no `/libsql`, no public sync endpoint. SDKs are thin clients over REST and WebSocket, with no native Turso library inside.
- **Turso Sync runs inside TerraBase, between our own regions, not in customer apps.** Each database has a home copy plus a synced copy in every other region. A region commits a write locally and pushes it to home. Home pages flow back to every region with a pull. Region-to-home traffic stays on our private network, behind our auth, and customers never see it.

```
 app / agent / ORM / psql ──(REST · WebSocket · Postgres wire)──► nearest region
                                                                    │
  region node (sjc): router ─► local commit (fast path) ──push──►  home node (gru)
                       └──────► forward whole tx (unsafe shapes) ─►  atomic apply, HLC, conflict log
                                                                    │
  every region ◄───────────────── pull (pages, durable revision) ───┘
```

### Why this is better than SDK sync (my input)
1. **We own both ends.** Every sync client is our code on our hosts, at a pinned version. We can patch the push side and write the home apply, and no customer is stuck on a buggy client we can't upgrade.
2. **Few sync clients.** There are 3 regions per database (up to 20 later), not thousands of devices. Conflicts are rarer, and all conflict state stays server-side, where the dashboard can show it.
3. **Thin SDKs in every language.** No native binaries, Windows and browser support for free, no Turso names leaking from packages. SDK work drops a lot.
4. **psql transactions run locally.** A 10-statement transaction costs 10 local round trips, not 10 × 140–190 ms to home.

**What we give up:** offline mode and sub-ms in-process reads inside customer apps. They can come back later as an opt-in "edge replica" SDK feature, still hidden.

### The hard part doesn't go away
A local ack can't promise a global UNIQUE, a foreign key or a correct balance. Spike I.3 proved that stock push loses acked writes. So the default is **local writes with smart routing**: the region parses each write before running it and picks the path.

| Write shape | Path | Why |
|---|---|---|
| INSERT with a ULID/UUID key into a table with no other UNIQUE | **local** | can't conflict |
| UPDATE by primary key with constant values, not touching a UNIQUE or FK column | **local**, last write wins per column by HLC | merges cleanly (column merge worked in I.3) |
| UPDATE that changes a UNIQUE or FK column | **home** | uniqueness and references are only exact at home |
| INSERT of a child row with an FK | **local**, FK re-checked atomically at home | parent deletes are home-only, so a reject is rare; when it happens it is reverted visibly |
| DELETE by primary key | **local**, tombstone | merges cleanly |
| Relative update (`SET n = n - 1`), INSERT into a table with a secondary UNIQUE, AUTOINCREMENT/rowid keys, a parent delete under an FK, DDL | **home**, whole transaction forwarded | these are exactly the cases that lost or corrupted data in I.3 |
| Explicit transaction that reads and then writes | **home** | needs a consistent read at the writer |

- **psql interactive transactions:** start locally. If a home-only statement shows up after local writes, abort with SQLSTATE `40001` (serialization failure). ORMs and drivers already retry that error, and the retry is routed home. The router remembers the statement fingerprint, so the next run goes home directly.
- **Overrides:** `SET terrascale.write = 'home' | 'local'` in psql, a header on REST/WebSocket, and a per-table setting in the dashboard.
- **Ack contract:** a local ack means committed and durable on 2 nodes in that region (D16); home acceptance follows within the sync lag. `durability=home` waits for home acceptance (this matches the earlier "ack locally, opt-in remote ack" decision).
- **Rare rejections are visible, never silent:** for example, a local child insert whose parent was deleted at home. They land in a SQL-queryable `terrascale.conflicts` table, in the change feed and in the dashboard conflict console.

**Result:** most traffic (inserts, agent memory, events, chat, profile updates) acks in a few ms in the closest region. Writes that could lose money or uniqueness pay one home round trip, automatically, with no app changes.

### Latency (estimates unless marked measured)
| Path | Expected |
|---|---|
| Local write ack | low single-digit ms (to measure) |
| Forwarded write | 137–184 ms RTT between regions (**measured**) plus the home commit |
| Visible at home | one one-way trip (~70–90 ms) plus apply, with streaming push |
| Visible in other regions | ~2 one-way trips, about 150–190 ms with streaming. Polling measured 0.7–1.3 s in I.3, so streaming is required |

### What comes from Turso vs what we build
| Piece | Turso | Ours |
|---|---|---|
| Storage, local commit, local change capture in each region | yes | |
| Home → region pages (pull) | yes | durable revision layer (I.2 checkpoint bug) |
| Rebase of unpushed local writes after a pull | yes (sync client) | verify under load |
| Region → home push transport | its captured-change format | streaming, batching, transaction boundaries, HLC stamps |
| Home apply | | all or nothing per transaction, column LWW by HLC, triggers off for applied changes (fixes the doubled triggers), conflict log |
| Write router / classifier | parser (via our driver) | routing rules, `40001` retry, fingerprints |
| Postgres wire, REST, WebSocket gateways | | ours (psql and HTTP exist in TerraBase today) |

### My feedback on the decision
- **I agree.** With sync server-side, local writes become a safe default for the right statement shapes. My earlier objection was to stock client push, not to local writes. This design removes the reason for it.
- **Biggest remaining risk: the dialect (Q-DIALECT).** With psql as a main surface, people expect Postgres SQL. Run an ORM compatibility spike before promising "Postgres compatible".
- **Second risk:** the classifier has to be conservative. When in doubt, route home. A slow write is a support ticket; a lost write is a lost customer.

---

## Appendix F. Free reads, the ElectricSQL way

> "i also want to work on the free reads that electricsql has, will we be able to do that?"

**Short answer: yes. We already have most of it.** TerraBase built and measured Electric-style free reads in October as **Shapes**. The pivot changes only where the change log comes from: SQL tables instead of documents.

### How Electric does it (checked 2026-10-29, electric.ax)
- **Pricing:** "Pay for writes and retention. Reads are free." Writes cost $1 per 1M (+$2 per 1M for Postgres Sync), retention $0.10 per GB-month, and "Egress, fan-out, concurrent users, and data delivery are unlimited at no additional cost." "Your costs scale with writes, not users."
- **Protocol:** a shape is a table plus a filter. Clients read it over plain HTTP:
  - `offset=-1` gets the snapshot
  - then `offset` and `handle` page through the log until `up-to-date`
  - `live=true` long-polls the tail, with a `cursor` that normalises request collapsing across CDNs

  Identical URLs return identical bytes, so a CDN serves N readers for the cost of one. They chose HTTP over WebSocket because of this.

### What TerraBase already has (`src/TerraBase/Features/Shapes`, 6.8k lines; `docs/terrabase/free-reads*.md`, `shape-reads.md`)
| Tier | What | Status |
|---|---|---|
| T0 | origin: one flight per live poll, ETag/304, closed-chunk cache | built |
| T1 | sealed chunks mirrored to a bucket as TBS1 ciphertext (AES-GCM, 10-minute epoch keys), presigned manifest | built |
| T2 | Cloudflare cache in front of an R2 bucket. Immutable SHA-256-named objects, `Cache-Control: public, max-age=31536000, immutable` | built |
| Key route | `GET …/shape/keys`: authenticated, `private, no-store`, revocation rechecked. A cached copy is useless without the key | built |
| Ops | sealer, retention, sweeper, CDN purge queue, metrics, conformance vectors | built |

- **Measured** (`benchmark/bench/2026-10-23-free-reads.md`, 3 repeats): R2 behind Cloudflare served **1,000 readers × 11 objects with 11 bucket reads**, exactly 1 per object per cache location, with a 0.999 hit ratio and **zero origin payload reads**.
- Tigris (T1 only) needed one bucket read per reader (11,000 at N = 1000), so R2 behind Cloudflare is the free-reads tier. This matches the earlier "R2 target, Tigris for tests" decision.
- **This beats Electric on one point:** cached objects are ciphertext, so **private, per-user data** can sit in a shared public CDN safely. Electric's CDN story is strongest for data you can cache unencrypted or behind your own auth proxy.

### How it fits the pivot
- **Change log source:** at the **home** region, turn on Turso CDC in `full` mode (before and after images, `03-core-engine.md:15`). Rows go to the on-disk `turso_cdc` table in the **same transaction** as the write. Our home apply (Appendix E) commits pushed region writes through the same capture path.
- **Every region gets the log for free.** `turso_cdc` is an ordinary table, so the Turso Sync page pull copies it to every region with the data. Any region can serve snapshots and live tails locally. The SQLite record decoder already exists (`TursoChangeTap.cs`).
- **Publish only home-accepted changes.** A local region write that home later rejects never enters a published chunk. That's simpler and safer than today's remote-ack publication rule.
- **Shape = table + `WHERE` + columns over plain SQL.** Start with a filter subset (`=`, `IN`, comparisons, `AND`/`OR` on columns). Before/after images let an update that moves a row into or out of a shape emit insert or delete, as Electric does. DDL on a table gives the shape a new handle and `409 must_refetch`, which the protocol already has.
- **Surface:** it's SQL over REST (HTTP GET), so it fits the "psql + REST + WebSocket only" rule. WebSocket can push "new data" hints, but data stays on cacheable HTTP. Scopes move from `documents:read` to table-level read grants, and header names move to TerraScale names.
- **SDK:** a thin `db.shape("todos", { where: "owner = $1" }).subscribe()`, plus React/Vue hooks. This is the local-first experience, without shipping Turso in the SDK.

### What it costs us
- **Cloudflare:** R2 egress is free. The CDN collapses readers to about 1 bucket read per object per cache location (measured). The bills that remain are R2 storage and operations (check Cloudflare's current price list before launch), the origin for live tails and manifests, and the key route.
- **Not free for us:** live long-polls at the head stay on the origin (`private, no-store`), and so do manifests and key requests. They are small, collapsed and region-local, but they scale with connected clients. Watch them with the existing shape metrics.

### What I'd sell, and how (my input)
- **"Reads are free" for shape/sync reads and for CDN-served data.** Bill writes and storage, Electric-style. This matches the earlier "free reads, replication not billed" decision.
- **Ad-hoc SQL reads** (psql, REST query, WebSocket) run on region replicas. They're cheap but use CPU, so give them a generous fair-use allowance per plan rather than "unlimited free". Otherwise one heavy analytic query pattern becomes our bill.
- **Optional later: cached query reads.** `GET /query?sql=…&at=<revision>` is immutable for a fixed revision and could be CDN-cached with the same ciphertext trick. Useful for public or read-heavy dashboards. Not a launch item.
- **Positioning:** "multi-region SQL: writes in the closest region, reads free from the edge." Electric needs your own Postgres, and Turso Cloud doesn't sell free reads. Nobody in the SQLite space has both.

### Effort and risks (estimates; nothing started)
- **Port Shapes: about 10–15 days.** Swap the document feed source (`_tb_feed`) for CDC and swap document filters for SQL column filters. Snapshots become a table read at a revision. Sealer, crypto, mirror, CDN, purge, retention, sweeper and metrics mostly stay. That's far cheaper than the "delete" in the old plan's Appendix A row.
- **Risks:**
  - Turso CDC is **Early Preview**. Spike I.1 saw it refuse ALTER (fine: DDL means a new handle) and double triggers (our home apply turns triggers off for applied changes).
  - `turso_cdc` grows with every write, so it needs a retention trim tied to the shape retention floor.
  - ~~A shape sees a region's own write only after the round trip home.~~ **Fixed by D9:** a local overlay shows it in the writer's region in milliseconds, and a direct provisional stream reaches other regions in one hop (tasks F7, F8).
- **Qualify before launch:** CDC `full` correctness through checkpoints and Turso upgrades, the size of `turso_cdc` under load, and a repeat of the 1,000-reader benchmark on the SQL source.

---

## Appendix G. Hiding Turso

- **Public surfaces are ours:** SQL over REST, SQL over WebSocket, shapes over HTTP, the Postgres wire, SDK names, error codes, docs, dashboard. Turso Sync runs only between our regions.
- **Leak points to close:**
  - the Hrana path `/libsql`: make it internal-only or drop it
  - engine error strings: map them to our error codes
  - native library names inside SDK packages: rename where possible
  - SQLite-specific `PRAGMA`s: allow-list them
- **Legal:** Turso is MIT. Its license and NOTICE ship in our third-party notices. Hidden from the brand, never from the legal files.
- **Dialect is the real lock-in.** Customers who write SQLite SQL keep us on SQLite-family engines. See question Q-DIALECT.

---

## Appendix H. Developer experience for the agentic AI age

- **A database per user or agent:** create in under 100 ms (to be measured), fork from a Tigris snapshot, a TTL for short-lived sandboxes, scoped per-database tokens.
- **One SDK call to start:** `ts.db("app").query(sql)`, typed rows and migrations. Live data with `db.shape(...).subscribe()`, free from the CDN, with region-local updates in milliseconds (D9).
- **MCP built in:** schema, query, explain, fork, restore. The dashboard already has SQL MCP tools.
- **SQL that AI codegen writes well:** publish a versioned "TerraScale SQL contract" page that agents can read (llms.txt).
- **Vector and full-text search in plain SQL;** hybrid search recipes.
- **Changes as a feed** (CDC to webhooks and SSE) for realtime UIs, built on CDC. That replaces document feeds.
- **Pricing that rounds to zero,** with hard spend caps (a market gap: "one capped bill").

---

## Appendix I. Spike results

| Spike | Question | Result |
|---|---|---|
| Replica model ([report](.cos/spike-sql-repl-2026-10-29.md)) | Turso Sync vs our CDC shipper for read replicas | **Turso Sync, pull-only, with writes forwarded to home.** See I.1 |
| In-process sync host ([report](.cos/spike-turso-sync-host-2026-10-29.md), sources in `.cos/benchmark/bench/turso-sync-host-20261029/`) | Can one C# process serve the sync protocol for 1,000+ databases? | **Yes: 1,000 databases at 212–250 MiB. But a checkpoint silently leaves clients stale.** See I.2 |
| Multi-writer conflicts ([report](.cos/spike-turso-multiwriter-2026-10-29.md)) | Local writes everywhere through home | **Not safe as the default; possible as an opt-in mode with our own home apply.** See I.3 |

### I.1 Replica model spike (2026-10-29, measured on delta, beta and mariogk-dev; Turso 0.8.2 and 0.8.3)

**Correctness, 3 repeats each.** The workload: 100k inserts, 10k updates, 5k deletes, UNIQUE and FK rejections at home, a multi-statement transaction plus a rollback, `ALTER TABLE ADD COLUMN`, `CREATE INDEX`, a replica kill mid-pull, a home kill after an acked commit, and a damaged header.
- **Turso Sync, pull-only: PASS 3/3 on everything**, with identical row hashes on home and replicas. DDL and triggers come across with the pages.
- **Our own CDC replay: FAIL on DDL.** ALTER is refused (it needs a DDL envelope or a snapshot). CREATE TRIGGER isn't in CDC, and restoring the trigger on the replica then **duplicates** its effects.

**Lag, home to replica, rows written per second** (p50 / p99 in ms; ranges cover the 3 repeats):
| Path | Turso Sync p50 | Sync p99 | Own CDC p50 | CDC p99 |
|---|---|---|---|---|
| Same host, 200/s | 26–27 | 32–36 | 3.7–3.9 | 5–7 |
| Same host, 2,000/s | 26 | 33–34 | 5.3–5.5 | 14 |
| Across dev hosts, 200/s | 22–31 | 33–205 | 4.2–5.1 | 8–1,687 (one outlier) |
| Across dev hosts, 2,000/s | 23–45 | 36–627 (one run didn't sustain 2,000/s) | 6–10 | 85–331 |

Catch-up after reopening a replica: Sync 65–71 ms, CDC 319–403 ms. The dev CLI server handles connections one at a time and managed only **82–85 single-row commits/s**.

**All 3 upstream bugs reproduced 3/3 on both 0.8.2 and 0.8.3:**
- **#10178:** a failed push followed by a retry **deletes the table at home**, and the next pull deletes it locally too.
- **#10147:** a push **doubles trigger effects** (one local audit row becomes 2 at home and 2 locally).
- **#8725:** **MVCC bootstrap** fails (`no such table`).

So, for now: **no client push, and no MVCC sync.**

**Measured OCI round-trip times between regions (TCP, medians):**
| | gru | sjc | fra |
|---|---|---|---|
| gru | – | 168 ms | 181–184 ms |
| sjc | 168 ms | – | 137–144 ms |
| fra | 181 ms | 137–144 ms | – |

What follows:
- A fra client writing to a gru home pays about **181 ms plus commit time**.
- A pull request per change can't meet a 200 ms lag target between gru and fra. Only a **push stream or long-poll** can (one-way is about 90 ms). The dev CLI ignores long-poll.

**Effort to production replication: 40–65 engineer-days** with Turso Sync, versus 45–75 with our own CDC. That covers:
- a production authority (concurrent I/O, still a single writer)
- auth and TLS
- local encryption on replicas
- bounded WAL history and snapshot rebootstrap
- fenced failover
- coexistence with the Tigris backup
- hardening

### I.2 In-process sync host spike (2026-10-29; Rust C ABI shim pinned to 0.8.2, a small ASP.NET host, the official JS client; x86 dev hosts)

**It works.** One C# process served Turso's sync protocol at `/db/{name}/pull-updates` and `/db/{name}/v2/pipeline` with per-database bearer auth. The official JS client did bootstrap, pull, push, offline write then push, and 2-client convergence, all 3/3 with no SDK changes. Over 1,000 distinct databases, there were zero wrong-tenant rows. The shim itself is 33 lines of C ABI plus about 1.6k lines reused from upstream.

| Measured (3 repeats) | Result |
|---|---|
| Push p50 / p99 (same host) | 6.8–7.5 / 9.6–21 ms |
| Changed pull p50 / p99 | 15.8–18.2 / 22.8–29.1 ms |
| Client bootstrap | 22–34 ms |
| New tenant's first pull in a warm host | **2.4–3.1 ms** |
| 1,000 databases all open, one process | **212–250 MiB RSS** (about 124–163 KiB per database) |
| 1,000 databases, LRU cache of 64 open | 148–155 MiB RSS; every value survives eviction and reopen |
| One `tursodb` process per database | 21 MiB RSS / 6.1–6.4 MiB PSS each; first pull 21–28 ms; **idle CPU about 2 cores per 1,000 processes**, so about 200 per 2-core node |
| Shared process vs process per tenant | **about 26–33x less memory per tenant** shared |
| A1 2 OCPU / 12 GB cost | $27.74/month list ($0 inside the free allowance), so **about $0.03 per tenant-month** of compute at 1,000 small tenants |

Upstream has no LRU at this pin: it refuses with 503 at the cap. The spike added one.

**Critical bug found: checkpoints break page sync.** The revision is just the WAL frame number. After `wal_checkpoint(TRUNCATE)` the numbering restarts, and the official client's `pull()` returns **false while still missing the new row** (it stays at 100 rows when there are 101). That's silent stale data. Not checkpointing isn't an option either: the WAL grows about 0.66 GiB/day per active database at 1 insert/s, or 1.22 GiB/day with CDC on. TerraBase checkpoints on shutdown, rewrite, copy and restore today. **This also affects the read-replica design in I.1,** whose runs never checkpointed.

The fix (5–8 days, part of the estimate below): a durable revision = database incarnation + checkpoint generation + frame position. Retained page history is served across generations, and anything older gets a tested full rebootstrap or an explicit expiry error, never an empty "nothing changed". CDC stays a separate backup stream with its own retention.

**MVCC bootstrap failed twice**, matching #8725, so MVCC stays excluded.

**Effort for a page-mode sync host in TerraBase: about 20–30 engineer-days.** That covers:
- one native artifact plus ARM64
- Hrana, auth, tenant SQL policy and CDC admission
- the revision generations
- LRU pins, quotas and backpressure
- qualifying all 4 SDK languages, plus crash and restore

MVCC is another 5–15. Much of this overlaps the 40–65-day replication estimate in I.1, since the authority and revision layer are shared. Combined, budget about 50–80 days for replication plus sync, not their sum.

### I.3 Multi-writer spike (2026-10-29; official JS sync client plus the CLI home, on 0.8.2 and 0.8.3, same results on both)

Setup: home H with 3 "regions" R1, R2, R3.

| Case | What happened | Meaning for "local writes everywhere" |
|---|---|---|
| Same row, different columns | **Both edits kept** (only changed columns are sent) | good: the common case merges |
| Same row, same column, stale region pushes later | The older edit **overwrites** the newer one (arrival order) | needs our HLC check at home |
| UNIQUE collision (same email from 2 regions) | **Partial apply:** the other rows of the losing transaction committed; the losing row was **silently dropped** from the region after pull, although it had been acked locally | needs atomic apply plus visible rejection |
| FK: one region deletes a parent while another adds a child | The outcome depends on push order; an acked child or delete **disappears** | needs a policy, or route these to home |
| Balance 100, two regions each debit 80 | **Both approved:** an 80 debit was lost, a 60 overdraft, and the CHECK still passed | invariant transactions must run at home |
| ALTER ADD COLUMN racing with writes | worked (additive) | migrations need one owner at home |
| AUTOINCREMENT ids | **collide and overwrite** | ULID keys only (already our rule) |
| Trigger | **fires twice** (#10147) | forbid triggers in local mode until fixed |
| 60 s partition, 1,803 rows | **converged in 315–503 ms** after reconnect; all 4 hashes equal | partitions are fine for distinct keys |

Lag from a local ack on R1 until visible on R3 (two hops through H; p50 / p99 in ms, 3 repeats). The CLI closes every connection and ignores long-poll, so these are a worst case:
| Load | No added delay | 70 ms per hop | 150 ms per hop |
|---|---|---|---|
| 100 single-row commits/s | 41–53 / 74–95 | 393–406 / 591–661 | 670–737 / 987–1,296 |
| 1,000 rows/s in 10-row commits | 44–52 / 62–1,232 | 578–1,230 / 1,031–2,455 | 3,112–3,305 / 4,403–4,905 |
| 1,000 single-row commits/s | not sustained (778–896/s); p50 3.5–4.1 s | | |

The spike's verdict:
- (a) Default SQL database with local writes everywhere: **No**.
- (b) Opt-in local-writes mode: **conditionally useful**, for conflict-tolerant data with global keys, or counters designed as deltas.
- (c) Useless: **too strong**.
