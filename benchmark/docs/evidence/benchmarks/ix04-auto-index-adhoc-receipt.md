# IX-04 Benchmark Receipt: auto-indexed ad-hoc workloads

PostgreSQL-parity-or-better: inconclusive

verdict: inconclusive

This receipt records the deterministic counters of the auto-indexed
ad-hoc workload class (PD-2026-08-26-09 D3, run under PD-2026-08-26-03).
No performance claim is made in it. The counters are exact facts about
operation streams; the objective question cannot be answered on this host
because neither reference baseline ran (both statuses below).

## Run identity

- Base source: `da0df43` (develop, pre-packet); changed source: this
  packet's commits on branch `fleet/ix04-2`.
- Adapter: `src/benchmarks/optimizer.rs` (workload class, budgets, driver,
  receipt renderer) and `src/benchmarks/optimizer_tests.rs`, driven by the
  named characterization
  `benchmarks::optimizer_tests::ix04_adhoc_workload_characterization`.
- Launcher command (repository root, offline, locked):

  ```
  TERRABASE_BENCH_BRANCH=fleet/ix04-2 \
    cargo test --offline --locked --lib \
    benchmarks::optimizer_tests::ix04_adhoc_workload_characterization \
    -- --ignored --nocapture
  ```

  Exit code 0. Runtime 404.78 s, dev profile, concurrency 1.
- Exclusive lane check before the run: `ps aux | grep bench` matched no
  benchmark process (one sibling worktree's `rustc` build of `rustls` was
  the only hit and is a compile, not a run), and no file under any
  worktree's `docs/evidence/benchmarks/` had been written in the preceding ten
  minutes (the newest was 20:39, hours earlier). Recorded at
  2026-08-27T23:5x UTC.
- Environment: the shared build host, otherwise loaded by sibling builder
  worktrees. Every value in this receipt is a deterministic counter, not a
  timing, so load does not move it; it is disclosed anyway.
- Lane configuration of the window: seed 256 documents in the
  single-collection collection and 128 in each of eight cross collections;
  15 distinct ad-hoc shapes on one collection; 8 shapes × 16 repeats across
  collections; lifecycle `supersede_drain = 0` (V1's documented test mode
  for that gate — decision D3 of the packet record); idle thresholds at
  V1's defaults. **This is not a recorded baseline** under SE-15's
  `LaneConfig::validate_for_record` rule: the charter's recorded-run
  minimums (1,000 seed documents, 10,000 operations per sample, 5 samples)
  apply to wall-clock comparisons against the references, and no reference
  ran here.
- Durability: unchanged and unweakened — every seeded document and every
  measured write commits at `LocalDurability::Synced`, fsync-per-commit
  journal records, digest framing, and every fail-closed envelope
  discipline exactly as landed. No engine configuration was changed to
  prepare this run.

## Reference baselines

- postgresql reference: unrun — no pinned PostgreSQL reference exists on
  this host: no `postgres`, `psql`, or `initdb` binary is installed and the
  `postgresql` service is inactive, and no
  `TERRABASE_PG_REFERENCE_ENDPOINT` is configured. The operator must
  provide the pinned PostgreSQL 18 endpoint and capture its
  `SELECT version()` output.
  (owner: TerraScale benchmark lane owner (SE-15))
- terrabase v1 reference: unrun — the pinned TerraBase V1 checkout is
  read-only on this host, and driving its server needs the V1 build plus
  its pinned containers, which this offline environment does not provide.
  (owner: TerraScale benchmark lane owner (SE-15))

## Deterministic counters (exact, reproducible)

Single-collection window — 256 seeded documents carrying four queryable
fields, then one ad-hoc equality query per non-empty field subset (15
shapes) in canonical order:

| Quantity | Value |
| --- | --- |
| operations | 15 |
| selection records produced | 15 |
| documents returned | 15 (every triggering query answered) |
| creations | 4 |
| served by an existing index | 11 |
| served from the selection cache | 0 (every shape is a first sighting) |
| collection scans | 0 |
| refusals | 0 |
| complete candidates left unchosen with no recorded reason (SQ-B1) | 0 |
| durable bytes appended by the whole window (OH-B1) | 0 |
| live auto indexes after the window | 1 |
| retired auto indexes observable after the window | 3 |

Cross-collection window — eight collections of 128 documents each, one
ad-hoc shape per collection, sixteen repeats each:

| Quantity | Value |
| --- | --- |
| operations | 128 |
| selection records produced | 128 |
| documents returned | 128 |
| creations | 8 (one per collection) |
| served by an existing index | 120 |
| served from the selection cache | 120 |
| collection scans | 0 |
| refusals | 0 |
| complete candidates left unchosen with no recorded reason (SQ-B1) | 0 |
| durable bytes appended by the whole window | 0 |
| live auto indexes after the window | 9 (1 from the prior window + 8) |
| retired auto indexes observable after the window | 3 (the prior window's) |

Contracts these counters pin, against the budgets written before the run:

- **SQ-B1 holds at zero in both windows.** Every complete candidate that
  lost carried a recorded `CandidateNote`. The count is computed per
  selection record by the adapter, not asserted from a sample.
- **SQ-B2 holds with the amended definition.** Four creations for fifteen
  shapes: the single-field shapes each widen the union once (V1 AX-16) and
  every later subset of the union is already completely served. The first
  writing of this budget expected fifteen creations; the run returned four
  and the budget was amended to the union contract before this receipt —
  the RED observation is recorded in the packet record.
- **SQ-B3 holds.** One shape repeated sixteen times: one creation, fifteen
  served, all fifteen from the bounded selection cache, all by the same
  `Auto/` index.
- **OH-B1 holds at zero.** The creation stream of both windows together —
  twelve creations, each with an initial build over the seeded documents —
  appended no durable bytes at all. Auto-index state is in-memory engine
  state derived from storage publication order.
- **MB-B1's bound is not approached here** (9 live of 64); the bound and
  its fail-closed refusal are pinned by the budget test
  `the_auto_index_registry_is_bounded_and_refuses_past_it`.

## What these counters do not say

They are not evidence that any query got faster. Execution remains the
W3-2 bounded snapshot scan: no query in this window was narrowed or
ordered by index data, and nothing here compares TerraBaseV2 against
PostgreSQL or TerraBase V1. What they measure is the optimizer's own
behavior — what it selected, what it created, that every decision was
recorded, and that creating twelve auto indexes cost zero durable bytes.

## Harness defects found by this run, recorded honestly

1. The first run of the characterization seeded differently named
   collections than the cross-collection driver queries, so that window
   answered zero rows while still exercising selection. Fixed before this
   receipt; the counters above are from the corrected run.
2. The window counters were first computed from the retained tail of the
   bounded record store, which under-counts a window longer than
   `MAX_SELECTION_RECORDS` (128). Fixed by classifying each record as its
   query produces it; the counts above are exact for the operations driven.

## Validity notes

- The whole characterization was re-run at this packet's final commit
  (`077def3`) after the behavior-neutral cleanups that followed the run
  (a clippy-driven variant rename and the non-vacuity window): **identical
  counters in both windows**, exit 0, runtime 332.21 s.
- Every value above is a deterministic counter (operations, records,
  documents, bytes, indexes) — reproducible on any machine, independent of
  profile and load. No wall-clock number appears in this receipt at all:
  the ad-hoc class has no comparand on this host, so a timing would be
  engineering evidence supporting nothing, and it was not taken.
- The objective status is `inconclusive` — the honest outcome while the
  PostgreSQL reference baseline is unrun, and the only outcome the lane's
  fail-closed rule can produce from that state.
- What is owed next: the PostgreSQL and TerraBase V1 reference baselines
  (owner: TerraScale benchmark lane owner (SE-15)), after which this class
  can be run at the recorded-run minimums against them.
