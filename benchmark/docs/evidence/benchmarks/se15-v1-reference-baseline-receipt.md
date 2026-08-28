# SE-15 Benchmark Receipt: TerraBase V1 reference baseline run slot

PostgreSQL-parity-or-better: inconclusive

verdict: inconclusive

This receipt records an **unrun** reference slot, not a measurement. No
TerraBase V1 benchmark number exists in this repository and none is invented
here.

## Run identity

- Ticket: TerraBase/TerraBaseV2#23 (SE-15), charter PD-2026-08-26-03 D2.
- Pinned V1 source identity: `fa99b9257776dbe625e6a2388c74c9ce916637ee`
  (the same pin the parity contracts and the citation audit resolve
  against, so the benchmark baseline and the parity baseline cannot drift
  apart).
- Harness: `bench/TerraBase.Server.Benchmark/ServerComparisonBenchmark.cs`
  at that commit, run at V1's own recorded configuration (1,000 seed
  documents, 10,000 operations per sample, 5 samples, concurrency 16,
  1,000 warm-up operations, 5% noise), server order alternating each
  sample.

## Status: unrun, with the reason and the owner

- terrabase v1 reference: unrun — the pinned TerraBase V1 checkout is
  read-only on this host, and driving its server needs the V1 build plus
  its pinned containers, which this offline environment does not provide;
  the operator must run the V1 baseline from the pinned source
  `fa99b9257776dbe625e6a2388c74c9ce916637ee`.
  (owner: TerraScale benchmark lane owner (SE-15))

The V1 tree on this host is a read-only worktree (`.worktrees/`
`terrabase-v1-fa99b925-readonly`); the V1 harness's own reproduction steps
call for `dotnet build` of the pinned source and two pinned container
images, neither of which the offline no-live-systems build environment
provides. The methodology transfer is therefore complete — the gate, the
warm-up rule, the alternation rule, and the recorded-run minimums were read
out of that harness and are implemented and tested in
`src/benchmarks/gates.rs` and `src/benchmarks/lane.rs` — while the V1
*numbers* wait for the operator's run.

## Why this is not a hidden skip

- The fail-closed rule covers this side too:
  `gates::reference_comparison_status` returns `inconclusive` while either
  reference is `Unrun`, and the adapter's test
  `a_missing_reference_forces_inconclusive` covers the combinations where
  only V1 is missing.
- Unlike the PostgreSQL pin, the V1 pin's identity is already observed — a
  source commit is readable without running it — so
  `TERRABASE_V1_REFERENCE.observed_identity` is filled and the gap is
  precisely and only the execution.

## Validity notes

- No performance statement of any kind is made here.
- The objective status is `inconclusive` — the honest outcome for the
  objective question while a reference baseline is missing.
- Nothing about the V1 methodology was silently re-derived: the gate and
  configuration rules above are quoted from the pinned harness, and the
  adapter's tests pin their behavior against hand-checked cases.
