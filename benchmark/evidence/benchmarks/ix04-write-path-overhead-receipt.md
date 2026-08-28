# IX-04 Benchmark Receipt: write-path overhead of active auto indexes

PostgreSQL-parity-or-better: inconclusive

verdict: no-improvement

This receipt records the one comparison of the IX-04 class that **does**
exist on this host: the same acknowledged write stream run with twelve
active auto indexes and with none, both sides TerraBaseV2, measured by the
lane's deterministic durable-byte counter. The verdict below is the gate's
reading of that comparison. It is **not** a PostgreSQL-parity statement —
the objective status stays `inconclusive` because the PostgreSQL reference
baseline did not run.

## Run identity

- Base source: `da0df43` (develop, pre-packet); changed source: this
  packet's commits on branch `fleet/ix04-2`.
- Adapter: `src/benchmarks/optimizer.rs`
  (`WritePathComparison::of` runs the SE-15 gate over the counter), driven
  by the named characterization
  `benchmarks::optimizer_tests::ix04_write_path_overhead_characterization`.
- Launcher command (repository root, offline, locked):

  ```
  TERRABASE_BENCH_BRANCH=fleet/ix04-2 \
    cargo test --offline --locked --lib \
    benchmarks::optimizer_tests::ix04_write_path_overhead_characterization \
    -- --ignored --nocapture
  ```

  Exit code 0. Runtime 144.91 s, dev profile, concurrency 1.
- Exclusive lane check before the run: `ps aux | grep bench` matched no
  benchmark process; no `evidence/benchmarks/` file in any worktree had
  been written in the preceding ten minutes.
- Environment: the shared build host, otherwise loaded by sibling builder
  worktrees. The measured quantity is a deterministic byte counter, so
  load does not move it; disclosed anyway.
- Lane configuration: twelve collections seeded with eight documents each;
  on one side twelve ad-hoc queries (one per collection) create twelve
  live auto indexes **on the very collections the writes land in**; on the
  other side the same collections are seeded and no query runs, so no
  index exists. Five measured rounds of twelve acknowledged writes per
  side, each write a ~300-byte document committed at
  `LocalDurability::Synced`.
- Durability: unchanged and unweakened. No engine configuration was
  changed for this run.

## Reference baselines

- postgresql reference: unrun — no pinned PostgreSQL reference exists on
  this host: no `postgres`, `psql`, or `initdb` binary is installed and the
  `postgresql` service is inactive, and no
  `TERRABASE_PG_REFERENCE_ENDPOINT` is configured.
  (owner: TerraScale benchmark lane owner (SE-15))
- terrabase v1 reference: unrun — the pinned TerraBase V1 checkout is
  read-only on this host, and driving its server needs the V1 build plus
  its pinned containers, which this offline environment does not provide.
  (owner: TerraScale benchmark lane owner (SE-15))

## The comparison

Durable bytes per acknowledged write, one entry per measured round:

| Side | Round 1 | Round 2 | Round 3 | Round 4 | Round 5 |
| --- | ---: | ---: | ---: | ---: | ---: |
| no index at all | 38,798.83 | 43,499.08 | 48,197.83 | 52,895.08 | 57,593.08 |
| 12 active auto indexes | 38,798.83 | 43,499.08 | 48,197.83 | 52,895.08 | 57,593.08 |

The per-round growth is the engine's own journal record numbering — the
same operation stream appends the same bytes in the same order, which is
what makes this counter a lane fact. The two sides are **byte-identical
in every round**: the medians are equal, so the median delta is 0% and the
gate reads `Stable`.

Gate reading (SE-15's rule, V1's classification, 5% noise band):

- trend: `Stable`
- verdict: `no-improvement` — no measurable difference beyond the noise
  band, which for this budget **is** the budget met.

Budget OH-B3 (written before the run): 0 additional durable bytes per
acknowledged write per active auto index, for K ∈ {1, 8, 32}. This run
measured K = 12; the budget test
`active_auto_indexes_add_no_durable_bytes_to_the_write_path` measures
K = 4 with the same gate and the same result. **Status: budget met at the
K values driven.** The K ∈ {8, 32} rows of the written budget are covered
only insofar as 4 and 12 were driven; a larger-K run is owed below.

Why the counter is what it is, stated rather than implied: the engine's
index maintenance on the commit path is one derived delta recorded per
publication — in-memory engine state, applied once regardless of how many
namespaces exist — and segments are built from snapshot views at creation
and maintenance time, never appended to the durable journal. This receipt
measures that contract; it does not measure in-memory maintenance cost,
which no deterministic counter in the lane observes.

## What this comparison does not say

- It says nothing about PostgreSQL. The PostgreSQL equivalent — the same
  insert stream on a table carrying twelve indexes versus none — needs the
  pinned PostgreSQL reference, which is unrun on this host.
- It says nothing about latency, CPU, or memory: only durable bytes. No
  wall-clock number was taken.
- It is not evidence that queries are faster.

## Harness defects and honest limits of this run

1. The first run of this window drove 32 collections of 64 documents and
   did not finish inside its 1800-second timeout (terminated, exit 143);
   no number from that attempt appears here. The window was resized and
   re-run to completion.
2. The second attempt put the twelve auto indexes on collections *beside*
   the ones being written (a harness naming defect), which would have
   measured indexes present in the tenant rather than indexes on the
   written collections. Fixed before this receipt; the numbers above are
   from the corrected run, with `documents_returned: 12` in the creation
   window confirming the indexes sit on the written collections.

## Validity notes

- The measured quantity is a deterministic counter; the values are
  reproducible on any machine, independent of profile and load.
- Objective status `inconclusive`: the PostgreSQL reference baseline is
  unrun, so the objective question cannot be answered, and the lane's
  fail-closed rule returns exactly that.
- Verdict `no-improvement` describes this V2-versus-V2 comparison only.
- Owed next: the same window at K = 32, and the PostgreSQL reference side
  (owner: TerraScale benchmark lane owner (SE-15)), so the write-path
  overhead of auto indexes can be stated against the PostgreSQL equivalent
  rather than only against TerraBaseV2 itself.
