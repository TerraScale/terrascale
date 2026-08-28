# SE-15 workload mapping: TerraBaseV2 against PostgreSQL

Status: the comparable-workload mapping mandated by charter
PD-2026-08-26-03 D2 (PostgreSQL reference baseline) for ticket
TerraScale/TerraBaseV2#23. Every workload's two sides, both sides'
configurations, the fairness assumptions, and the limits are stated here;
nothing here is a measured result or a performance claim. Measured values
live only in the Benchmark Receipts under `docs/evidence/benchmarks/` and use the
charter's exact verdict vocabulary (`v2-faster`, `no-improvement`,
`v2-regression`, `inconclusive`) and objective status (`met`, `not met`,
`inconclusive`).

## Source of truth for the mapping

The mapping is not prose beside the code: it is **data in the benchmark
adapter** (`src/benchmarks/lane.rs`, `Workload`), and the adapter's tests
pin that every workload names both sides. This document explains the
mapping; `Workload::terrabase_v2_operation` and
`Workload::postgres_equivalent` are what the lane actually runs for the
driven workloads and what the declared open slots (the session workload
and the sync-path class) will drive when their runs close — so the
document and the adapter cannot drift apart.

## Method carried over from V1

The methodology is V1's server-comparison benchmark at the pinned source
`fa99b9257776dbe625e6a2388c74c9ce916637ee`
(`bench/TerraBase.Server.Benchmark/ServerComparisonBenchmark.cs`), with the
comparand changed from RavenDB to PostgreSQL and the TerraBase V1 baseline
added alongside it:

- **Statistical gate.** A difference is called only when the median moves
  by more than the noise percent (5%) **and** every sample of one side
  separates from every sample of the other side. Overlapping sample ranges
  are stable noise, whatever the medians appear to say. The gate is
  implemented once, in `src/benchmarks/gates.rs`, and unit-tested against
  hand-checked cases.
- **Warm-up discipline.** 1,000 warm-up operations per side, per workload,
  before any measured sample, excluded from every measured window.
- **Order alternation.** Which side runs first alternates every sample
  (`(workloadIndex + sample) % 2`), so first-run and second-run bias
  cancels across a run instead of landing on one side.
- **Recorded-run minimums.** A *recorded* run needs at least 1,000 seed
  documents, 10,000 operations per sample, and 5 samples. Trial runs may go
  smaller and must report themselves as not recordable
  (`LaneConfig::validate_for_record` fails closed).
- **Exact source identities for both sides**, recorded per run (below).
- **Recorded allocation.** The V2 side records deterministic counters
  (operations, durable bytes appended, documents returned) and the SE-07
  adapters' counting-allocator budgets remain the allocation lane for the
  crates that permit a global allocator. See *Limits* for the one place
  this mapping cannot count allocations.

## The workloads

Nine declared workloads: the five CRUD-class workloads matching the
ticket's list (document CRUD, indexed queries, write throughput,
connection/session patterns) and V1's workload set (read, write, query,
mixed), plus the four sync-path workloads the charter mandates beside them
(PD-2026-08-26-05 D4, PD-2026-08-26-13 D1 — the class section below).

### W-1 `read` — point document read

| Side | Operation |
| --- | --- |
| TerraBaseV2 | `Engine::read_snapshot` + `Transaction::read` — one point read by tenant/collection/document id on an immutable snapshot. |
| PostgreSQL | `SELECT payload FROM users WHERE tenant = $1 AND id = $2;` — primary-key point read, one prepared statement, one round trip. |

Seeded documents are addressed round-robin by identifier, so both sides read
a uniformly distributed hot set. The document shape is identical on both
sides and is V1's benchmark document: a short name, a small integer group,
and a 256-byte payload.

### W-2 `write` — acknowledged document write

| Side | Operation |
| --- | --- |
| TerraBaseV2 | `Engine::begin` + `Transaction::put` + `commit(LocalDurability::Synced)` — one acknowledged document create/replace. |
| PostgreSQL | `INSERT ... ON CONFLICT (tenant, id) DO UPDATE SET ...` with `synchronous_commit = on`. |

This is the write-throughput workload. **Fairness hinge:** both sides run
at their equivalent acknowledged-durability setting — the engine's
fsync-per-commit `Synced` level against PostgreSQL's `synchronous_commit =
on`. Neither side's durability is weakened for timing; that would be the
most tempting place for a hidden asymmetry to hide, so it is pinned
explicitly on both sides and the engine's durability settings stay exactly
as landed.

### W-3 `query` — indexed equality query

| Side | Operation |
| --- | --- |
| TerraBaseV2 | `Engine::queries().compile` + `execute` — one equality query on the seeded `name` field, projection and paging fixed. |
| PostgreSQL | `SELECT id, name FROM users WHERE tenant = $1 AND name = $2 LIMIT 1;` over the btree index on `(tenant, name)`. |

The query targets a uniformly chosen seeded name, projected to the name
field, first row only — the same shape as V1's benchmark query. V1 waited
for a non-stale index before measuring; the equivalent here is that the
engine's auto-index materialization must have settled during warm-up, which
the 1,000-operation warm-up window provides.

**Fairness hinge (stated, not hidden):** TerraBaseV2 requires no user-created
index (PD-2026-08-26-09) and its optimizer builds auto-indexes on demand,
while PostgreSQL gets an explicitly created btree index before warm-up. This
is the honest comparable posture — the product behavior each side actually
ships — but it means the PostgreSQL side pays index creation *outside* the
measured window and the TerraBaseV2 side pays it (if at all) *inside*
warm-up. Neither lands in a measured sample. The residual asymmetry —
TerraBaseV2's first-query planning cost is amortized differently — is a
limit recorded in *Limits*.

### W-4 `mixed` — 50/30/20 read/write/query

V1's mixed workload, kept exactly: 50% read, 30% write, 20% query, selected
per operation index (`index % 10 < 5` read, `< 8` write, else query). Both
sides interleave the same three statements in the same proportions.

### W-5 `session` — connection and session churn

| Side | Operation |
| --- | --- |
| TerraBaseV2 | TBEP UDS host session open/use/close over the engine — the SE-14/SE-08 host session surface. |
| PostgreSQL | `CONNECT; SELECT 1; DISCARD ALL; DISCONNECT;` — un-pooled session churn cost. |

**This workload has an open adapter slot.** The comparable session plane on
the TerraBaseV2 side is the TBEP UDS host session, which needs the C# host
process beside the engine; the in-process lane drives the engine core only.
The slot is declared in `Workload::driver_status`, appears on every receipt
that touches it, and names its owner. It is not silently skipped.

## The sync-path workload class (PD-2026-08-26-05 D4, PD-2026-08-26-13 D1)

Four further workloads cover the sync path the charter mandates beside the
CRUD set: commit-to-delivery latency, delivery throughput under load, and
bandwidth — bytes-on-wire per delivered change and trigger-notification
size — on both link kinds. The **client link** is the C# host / SDK path
(`terrascale.sync.v2` through the TBEP host); the **member link** is the
engine-owned member transport lane (in-process lanes and engine sockets;
NATS never carries these frames — PD-2026-08-26-13 D2).

Like the session workload, each of the four is **mapped but not driven**
by this packet's in-process lane: the sync plane lives in the sibling
crates (`terrabase-sync-core`, `terrabase-member-transport`, the TBEP
host) that the zero-dependency engine crate cannot call, and the two
wall-clock quantities additionally need the SE-15 statistical gate with
both reference baselines run. Each is a declared open slot in
`Workload::driver_status` with a named owner and a row in
`docs/evidence/benchmarks/se15-sync-path-workload-slots-receipt.md`. None is
silently skipped, and none is re-derived inside the engine crate where
re-deriving it would duplicate a measurement rather than take one.

**PostgreSQL comparand — the fairness statement for the whole class.**
There is no PostgreSQL equivalent for any sync-path workload, and the
mapping says so per workload in `Workload::postgres_equivalent`.
PostgreSQL has no push-based client delivery path; its nearest surfaces
are `LISTEN/NOTIFY` — a payload-carrying channel notification with no
checkpointed delta pull and no delivery acknowledgement — and WAL
replication — a different wire contract with no shared budget definition.
Presenting either as a comparand would manufacture a comparison the
charter does not ask for. The honest comparison set for this class is
TerraBaseV2 against **its own written budgets** (ADR-0008 D-4 sync-path
budgets; PD-2026-08-26-13 D1 bandwidth budgets) and against the **TerraBase
V1 baseline's sync path**, run under the same gate, warm-up, and
alternation discipline as the CRUD workloads. PostgreSQL stays the
comparand for the CRUD class; every sync-path receipt records its
objective status as `inconclusive` until the class's comparison runs.

### W-6 `sync-commit-to-delivery-latency` — commit to delivered, per link

| Link | TerraBaseV2 path measured |
| --- | --- |
| client | commit publication → the content-free trigger frame → the checkpoint-bounded delta batch the triggered client pulls and applies: `SyncCore` apply + serve through the C# host / TBEP plane (the SE-04/SE-14/SE-08 surfaces). |
| member | commit acknowledgement → the pushed or catch-up-delivered batch a member applies: the member-transport push/catch-up envelopes and the member apply path (SE-06/SE-07). |

- **PostgreSQL comparand:** none — the class fairness statement above.
- **What exists today:** the deterministic neighbors are sequence facts,
  not times — the SE-06 receipts record replication lag as a
  journal-sequence distance and convergence as lane ticks, both
  explicitly labelled not-latency claims. No wall-clock latency exists
  anywhere in this repository, and none is invented here.
- **Limit:** wall-clock by nature; needs the served sync plane and the
  V1 baseline run under the SE-15 gate (5% noise, separated ranges,
  alternated order).

### W-7 `sync-delivery-throughput` — delivery under load, per link

| Link | TerraBaseV2 path measured |
| --- | --- |
| client | delta batches served per budget window while a session's backlog drains (`SyncCore` serve under the 65,536-byte batch budget, checkpoint-bounded). |
| member | changes delivered per catch-up round while a member's backlog drains, bounded by the validated batch (`[8, 256]` commits per round, `LaneSettings` fail-closed outside). |

- **PostgreSQL comparand:** none — the class fairness statement above.
- **What exists today:** deterministic bounds, not rates — SE-07 E-1 pins
  catch-up rounds within `ceil(B / batch) + 1` and E-8 pins delta-batch
  utilization at or above the written 70%. A *throughput under load*
  number is wall-clock and does not exist yet.
- **Limit:** same served-plane and reference requirements as W-6; the
  deterministic bounds above stay the CI-pinned floor whatever the
  measured rates later say.

### W-8 `sync-bytes-on-wire` — encoded bytes per delivered change, per link

| Link | Counter |
| --- | --- |
| client | delta-batch / snapshot-answer wire bytes divided by changes delivered — the E-11 counter in `crates/terrabase-sync-core/tests/delta_efficiency.rs`, held against the written 65,536-byte delivery budget with the 38-byte fixed envelope. |
| member | per-direction envelope byte counters over push, catch-up, and acknowledgement frames — the SE-06 bytes-on-wire definition (encoded envelope bytes at the transport's delivery boundary), held against the written 512-bytes-per-delivered-change member budget. |

- **PostgreSQL comparand:** none — PostgreSQL's replication byte counts
  measure a different wire contract (WAL frames, not budgeted delta
  envelopes), so no shared budget exists.
- **What exists today:** the deterministic TerraBaseV2-side counters are
  measured and receipted — SE-07 E-11 recorded 25 wire bytes per delivered
  change on the client-link fixture workload, and the SE-06 composite-lane
  receipts recorded the member link within the 512-byte per-change budget
  on both lanes. What does **not** exist is the comparison run alongside
  the V1 baseline under the SE-15 gate with this mapping's fairness
  documentation; that is the open slot.
- **Limit:** the existing counters are crate-side facts pinned by their
  own adapters; this packet does not re-derive them inside the engine
  crate.

### W-9 `sync-trigger-notification-size` — the content-free hint

| Link | TerraBaseV2 frame measured |
| --- | --- |
| client | the encoded `trigger` frame of `terrascale.sync.v2`: collection scope, storage sequence, operation id — three content-free fields, nothing else (PD-2026-08-26-13 D3); the same wire shape the SE-04 golden corpus records. |
| member | none by design — members are pushed the data envelopes themselves, whose size W-8 covers; there is no member-link trigger-notification frame class to size. |

- **PostgreSQL comparand:** none — `LISTEN/NOTIFY` carries a payload
  string and has no delta path, so it is not comparable; the trigger frame
  is held against its own written budget (256 bytes) and the V1 baseline.
- **What exists today:** the deterministic measurement is recorded and
  pinned — 77 bytes in the recorded SE-04 vector, within the 256-byte
  written budget, asserted by SE-07 E-11 and replayed byte-exact by the
  golden corpus. The open slot is the receipted comparison alongside the
  baselines under the SE-15 gate, not the V2-side number.
- **Limit:** sizing the frame from the corpus is a determinism check the
  sync-core crate already owns; a *delivery* claim about triggers (how
  fast the hint arrives) belongs to W-6, not here.

## Both sides' configurations

### TerraBaseV2 side

| Setting | Value |
| --- | --- |
| source | the measured commit on the packet branch, recorded in the receipt |
| engine | `Engine::open(EngineConfig::durable(...))` — the production durable path, no test constructor |
| durability | `LocalDurability::Synced` on every measured write; journal framing, digest-per-record, and fail-closed envelope discipline exactly as landed |
| data location | a fresh system-temp directory per run, discarded afterwards |
| storage class | the host's default local filesystem, the same class as the PostgreSQL reference's data directory |
| tenant | one tenant, one `users` collection, engine-enforced scoping exactly as production |
| concurrency | the lane's configured in-flight operation count (16 at the recorded minimum) |
| warm-up | 1,000 operations per workload, excluded from every measured sample |
| indexes | none created by the operator — auto-indexing is the product behavior under measurement |

### PostgreSQL reference side

See `docs/benchmarks/se15-postgresql-reference-pin.md` for the full record;
the summary is: PostgreSQL 18 at the pinned artifact's then-current minor
release, `synchronous_commit = on`, `fsync = on`, `full_page_writes = on`,
planner and memory settings at their shipped defaults, a fresh empty data
directory per run on the same storage class, loopback-only socket, no
resource limits, one table with a primary key on `(tenant, id)` and a btree
index on `(tenant, name)` created before warm-up, one prepared-statement
connection per concurrency slot, autocommit per statement.

### TerraBase V1 reference side

The pinned V1 source `fa99b9257776dbe625e6a2388c74c9ce916637ee`, built
Release, driven by its own `ServerComparisonBenchmark` harness at the same
configuration (1,000 seed documents, 10,000 operations per sample, 5
samples, concurrency 16, 1,000 warm-up operations, 5% noise). V1's
container-based server bring-up is reproduced by the operator at the lane
run; the pin's configuration table is in `src/benchmarks/reference.rs`.

## Fairness assumptions

1. **Same durability semantics.** Both sides acknowledge a write only after
   the equivalent local-durable point. No `synchronous_commit = off`, no
   `LocalDurability::Buffered`, anywhere in a measured window.
2. **Same concurrency shape.** The same number of in-flight operations on
   both sides, from the same driver, with the same warm-up.
3. **Same data.** The same document count, the same document shape, the same
   key distribution, the same round-robin addressing.
4. **Same storage class.** Fresh data directories on the same filesystem for
   both sides within one run.
5. **Same isolation.** One statement per transaction on the PostgreSQL side,
   matching the engine's per-operation acknowledgement.
6. **No hidden tuning.** Every non-default setting on either side appears in
   a configuration table in this repository. Anything not in a table is at
   its shipped default.
7. **`Stable` is parity.** Under the gate, a difference inside the 5% noise
   band with overlapping sample ranges reads as `Stable`, and `Stable`
   counts as parity for the objective. This is the gate's own definition,
   inherited from V1, and it bounds the resolution of every `met` verdict to
   that band.
8. **The sync-path class has no PostgreSQL comparand.** For W-6..W-9 the
   comparison set is TerraBaseV2 against its own written budgets and the
   TerraBase V1 baseline, never against PostgreSQL — there is no
   comparable delivery contract on the PostgreSQL side, and the mapping
   says so per workload rather than leaving the reader to infer a missing
   table. The same gate, warm-up, and alternation discipline applies to
   that comparison when it runs.

## Limits

- **The reference sides have not run here.** No PostgreSQL server exists on
  the build host (no `postgres`, `psql`, or `initdb` binary; the service is
  inactive), and the pinned V1 tree is read-only. The V2 side of the lane is
  commissioned with real runs; the reference run slots are recorded as
  unrun-with-reason and owner in the receipts. No number in this document or
  in the receipts is a PostgreSQL or V1 measurement.
- **No per-operation allocation counts on the engine workloads.** The engine
  crate carries `#![deny(unsafe_code)]`, so it cannot install the counting
  global allocator the SE-07 adapters use. Allocation budgets for the crates
  that permit it (member transport, sync core, TBEP) are unchanged and
  remain the allocation lane; engine-workload allocation counting is owed
  to a follow-up and is not claimed here.
- **Index-creation asymmetry** (W-3): the PostgreSQL side's index is created
  before warm-up by the harness; TerraBaseV2's auto-index work happens
  inside warm-up. Neither is measured, but the amortization shapes differ.
- **The session workload is mapped, not driven** (W-5), for the reason
  stated above.
- **The sync-path workload class is mapped, not driven** (W-6..W-9). The
  sync plane lives in the sibling crates the zero-dependency engine crate
  cannot call, and the wall-clock quantities (W-6, W-7) additionally need
  both reference baselines to have run; the bandwidth quantities (W-8,
  W-9) have deterministic TerraBaseV2-side counters already receipted by
  SE-07/SE-06, and what is owed is the comparison run alongside the V1
  baseline under the SE-15 gate. Each slot's reason and owner are in
  `Workload::driver_status` and in
  `docs/evidence/benchmarks/se15-sync-path-workload-slots-receipt.md`; none is
  silently skipped.
- **No wall-clock sync-path number exists anywhere in this repository.**
  The deterministic neighbors that do exist are labelled as what they are:
  sequence distances, lane ticks, byte counts, and frame counts. None of
  them is a latency or a rate, and none is presented as one.
- **In-process lane ≠ served lane.** The V2-side driver calls the engine
  core in-process (it must: `TenantScope` has no public constructor by
  design, so the lane mirrors Platform's trusted role exactly as the
  engine's own tests do). A served comparison — engine behind its TBEP host
  against PostgreSQL behind a socket — is the recorded-run shape the
  operator drives, and the in-process counters are lane facts about the
  engine core, not end-to-end service claims.
