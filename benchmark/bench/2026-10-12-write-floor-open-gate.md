# Write-throughput floor: OPEN release gate (2026-10-12)

**Status: OPEN, blocking release.** No build measured so far meets the design's floor on the standard stand-in for a
Fly `shared-cpu-2x` 512 MB machine. Nothing here lowers the floor or claims a Fly number.

## The floor

docs/terrabase/multi-region-replication.md, section 22, item 1: a lane below about **500 operations/s** on
`shared-cpu-2x` triggers the CDC fallbacks named there (capture only `_tb_state`; batch more operations per lane
transaction).

## Measured (R8 owner, controlled, on beta)

Source: [2026-10-12-r8-feed-cost.md](https://github.com/TerraScale/terrascale/blob/9277893f/benchmark/bench/2026-10-12-r8-feed-cost.md)
at `9277893f` (branch `feat/r8-changes-sse`, local while the preview push freeze holds). These are that report's numbers,
not new measurements.

| Build | docs/s, 3 runs | Mean | vs 500 |
| --- | --- | ---: | ---: |
| base `07f8d81f` (R3 stack, no R8) | 424 / 422 / 422 | 423 | below |
| r8 `b8e011c9` | 385 / 385 / 384 | 385 | below |

- **Load:** one region (gru), `repl-bench --docs 30000 --batch 1 --concurrency 16`, sizes 256 B 70 % / 1 KiB 25 % /
  8 KiB 5 %, 1 % deletes. Every run verified exactly.
- **Limits, read from the serving process each run:**
  - `Cpus_allowed_list 0-1`: the two SMT threads of **one** physical core of a Ryzen 5 PRO 6650H;
  - `cpu.max 200000 100000`;
  - `memory.max` 512 MiB and `memory.swap.max` 4 GiB;
  - load driver on CPUs 2-11;
  - no foreign process above 20 % CPU during any run.
- **Earlier numbers are not comparable.** R3's earlier "above the floor" figures, and the exploratory 595/518 docs/s,
  had no working CPU limit (`Cpus_allowed_list 0-11`). T1's beta runs used `taskset 2,4` (two physical cores), a more
  generous shape.

## Standard stand-in (COS decision, 2026-10-12)

For comparability, local runs that stand in for `shared-cpu-2x` 512 MB use:
- 2 SMT siblings of one physical core;
- `cpu.max 200000 100000` and `memory.max 512M` (swap as configured);
- the load driver pinned to other cores;
- limits read from the server process every run.

Any other shape is labeled with its exact limits. Runs that check correctness under deliberate load are labeled apart
from quiet benchmark runs.

## Floor check in the 1M run (planned; blocked)

The Fly 1M run (`benchmark/scripts/repl-fly.sh`, #79) reports created documents/s per region from its timeline. It adds
a floor run per region with the load above (`--batch 1 --concurrency 16`), so the number compares with the table. Each
region's result is stated against 500 operations/s, with the machine size and limits as Fly reports them.

Blocked on:
- the credential handoff (user-run);
- R3 deployed;
- the merge and preview-push holds being lifted.

## Closing this gate

The gate closes only with a measured run at or above the floor, either:
- on Fly `shared-cpu-2x` at the release commit; or
- until then, as a stand-in, on the standard shape above.

The run must follow a fix: a section 22 fallback, or a write-path optimization measured before and after with the same
load and limits.
