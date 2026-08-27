# SE-15 workload mapping: TerraBaseV2 against PostgreSQL

Status: the comparable-workload mapping mandated by charter
PD-2026-08-26-03 D2 (PostgreSQL reference baseline) for ticket
TerraScale/TerraBaseV2#23. Every workload's two sides, both sides'
configurations, the fairness assumptions, and the limits are stated here;
nothing here is a measured result or a performance claim. Measured values
live only in the Benchmark Receipts under `evidence/benchmarks/` and use the
charter's exact verdict vocabulary (`v2-faster`, `no-improvement`,
`v2-regression`, `inconclusive`) and objective status (`met`, `not met`,
`inconclusive`).

## Source of truth for the mapping

The mapping is not prose beside the code: it is **data in the benchmark
adapter** (`src/benchmarks/lane.rs`, `Workload`), and the adapter's tests
pin that every workload names both sides. This document explains the
mapping; `Workload::terrabase_v2_operation` and
`Workload::postgres_equivalent` are what the lane actually runs, so the two
cannot drift apart.

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

Five workloads, matching the ticket's list (document CRUD, indexed queries,
write throughput, connection/session patterns) and V1's workload set
(read, write, query, mixed).

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
- **In-process lane ≠ served lane.** The V2-side driver calls the engine
  core in-process (it must: `TenantScope` has no public constructor by
  design, so the lane mirrors Platform's trusted role exactly as the
  engine's own tests do). A served comparison — engine behind its TBEP host
  against PostgreSQL behind a socket — is the recorded-run shape the
  operator drives, and the in-process counters are lane facts about the
  engine core, not end-to-end service claims.
