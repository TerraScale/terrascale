# P04 checkpointed empty-scan repeat protocol, registered before runs

Status: **protocol decision / no repeated paired run or speed verdict yet**.
This document fixes the estimator and resampling unit for one focused P04
shape. It does not alter the [v1 acceptance matrix](terrabase-rust-acceptance-v1.md),
its 5% rule, or any prior decision. Other P04 axes and acceptance gates remain
open. Do not tune the estimator, sample exclusions, or bootstrap seed after
seeing outcomes; any later change needs a separately versioned prospective
protocol and a new cohort.

## Source identity and the narrow question

The observed `ts-benchmark` HEAD is
`5685f163bb7e9339e8eb0c839fe2512aaf562648`; the TerraBase HEAD named
by the matrix is `352d9f61185d1b685e3032519579040d169ad47d`. Both
repositories have uncommitted work, so these revisions alone are insufficient
binary identities. These are SHA-256 identities of the inspected benchmark
working tree on 2026-09-24:

| Source | SHA-256 | Relevant evidence |
| --- | --- | --- |
| `bench/terrabase-rust-acceptance-v1.md` | `03188ba52673293a3f32617034079d71056c2387bc7153e78391262bf063725d` | ≥7 alternating pairs, 10,000 paired-bootstrap resamples, 95% CI and 5% rule, P04 shape |
| `terra-rust/src/main.rs` | `b3d08a1d988c66092fb6f1d1b7624d35d5f41a4e5b63d64bebdcfe61b100d360` | sample phases, nearest-rank percentiles, timeout and receipt schema |
| `terra-rust/src/terrabase.rs` | `c150595f145b2c6cd6bb841f437fe2ce17b148f71046c03a2624101bd66e3aa9` | embedded scan caller and checkpointed fixture |
| `terra-rust/README.md` | `1891859b0796ec5e3cb143d21d0c7ceddeb0e8c56526c0e0027341932ddae5eb` | 1,000-repeat CLI shape, first/warm distinction, separate diagnostics |
| `terra-rust/SCAN-EVIDENCE-20260924.md` | `ea479ace5c1a6982399a209e4a76293e0787ca6d2c25d99668b41b3ee96b3a0d` | one-scan diagnostic evidence only; no repeat interval |
| `firecracker-harness/README.md` | `bc00e64cd02a18c0db372e7cf6549dfb6036a2ea254ed13a560e95f3bc1e76ab` | historical image provenance and host cache/CPU limits |
| `terrabase/GOAL.md` | `9cd9be4a029199e6c1f9e1a6f1eab59a51e3a287127c0d724d445b8264fc404a` | section 10 environment, fair baseline and acceptance constraints |

The matrix says to bootstrap **pairs** but leaves the ratio estimator
unstated. This protocol chooses the ratio of paired-engine arithmetic means,
not the mean of per-pair ratios. The decision precedes any 1,000-scan paired
cohort. The earlier four-shape one-scan receipts are diagnostics, not samples
for this cohort or grounds to select an estimator.

## Fixed focused workload and trial unit

Use `deleted-history`, 1,024 rows, one-byte body, workload seed `1`,
`--checkpoint-fixture`, no `--checkpoint-before-deletes`, no `--sparse-tail`,
and `--operations 1000` in both lanes. SQLite uses `run`; TerraBase uses
`run-terrabase` in the **same feature-enabled executable**. Each lane creates
a fresh database/volume and verifies an empty `row_limit=1` full-range scan.
Record SQLite's post-delete `wal_checkpoint(TRUNCATE)` and TerraBase's
post-delete `Storage::checkpoint`, then record the actual physical fixture
state: immutable-run count, checkpoint sequence, mutable bytes, WAL/run bytes,
and SQLite WAL/database state. This is the registered explicit-checkpoint
empty fixture. It is not a cold-cache claim.

The independent inference unit is one **paired VM trial**: one SQLite and one
TerraBase run on reset, equivalent 4-vCPU/8,192-MiB, swap-off Firecracker VMs
and fresh equivalent volumes. This campaign has exactly **eight** latency
pairs, each lane performing 1,000 scans. Preassign pairs 01, 03, 05 and 07
`SQLite→TerraBase`, and 02, 04, 06 and 08 `TerraBase→SQLite`; this balances
order four/four and exceeds the matrix's seven-pair minimum. Run all eight
without interim CI-based stopping or expansion. Keep pairs as blocks; never
pair lanes opportunistically after seeing latency. Record pair ID, order,
VM/volume IDs, reset evidence, host and guest image identities, CPU placement,
and concurrent host activity.
Pairs must be independent resets with no retained database or guest-cache
state. If shared host cache or CPU contention is uncontrolled, report that
limitation and withhold an acceptance speed verdict. The 1,000 scans within
one VM are correlated repeats, **not** 1,000 independent bootstrap units.

In each successful lane, JSONL operation `0` is `first_after_fixture`; it is
the sole first-scan latency for that VM. Operations `1..999` are 999
`warm_repeat` latencies. For each VM separately, compute warm p50, p95, p99
and p99.9 using the driver's nearest-rank rule: sort the 999 latencies and
select one-based rank `ceil(p * 999)`. Compare each named percentile as its
own metric. Do not pool the 999 operations across VMs, call the first scan
"cold," or include operation 0 in warm quantiles. `first_after_fixture`
records order after fixture construction; it does **not** prove a cold OS or
host page cache. The first-scan metric is one value per VM, so its cross-VM
mean is the endpoint; a purported
within-run first-scan p95 has no meaning here.

Run exactly **one separate diagnostic pair**, ID D01, in preassigned
`SQLite→TerraBase` order, with one SQLite and one TerraBase
`--scan-diagnostics --operations 1` lane on the same focused shape, using
fresh VM/volume resets. This pair collects calling-thread CPU, process
I/O and TerraBase scan-work counters. Its latency has probe/bracket effects
and does not enter the unprofiled first-scan or warm performance cohort or
any CI. Missing counters remain missing, not zero. The existing 1,024-row
diagnostics establish matched logical results and work-counter behavior only;
they do not supply repeated paired timing evidence.

## Predeclared estimand and inference

For a named latency endpoint `m`, let `T_i(m)` and `S_i(m)` be the TerraBase
and SQLite values from complete pair `i`, in nanoseconds. For first scan,
each is operation 0; for a warm endpoint, each is that VM's nearest-rank
warm percentile. With the fixed `n = 8` complete latency pairs, the point
estimate is

`R_m = [sum_i T_i(m) / n] / [sum_i S_i(m) / n] = sum_i T_i(m) / sum_i S_i(m)`.

This estimates the ratio of the two engines' expected **per-VM endpoint**
under the fixed paired trial schedule, giving every VM pair equal weight in
each engine mean. The tempting `mean_i[T_i/S_i]` answers a different
question and can give a trial with an unusually small SQLite denominator
disproportionate influence. Pairing remains valuable: each bootstrap draw
keeps `T_i` and `S_i` together, preserving host/order covariance. Do not
compute a ratio from pooled operation-level percentiles, and do not silently
switch to a median or geometric mean if raw ratios look inconvenient.

For each metric independently, use exactly **10,000** nonparametric paired
bootstrap replicates with replacement. Each replicate draws eight integer
indices from `0..8`, takes both engines' values at every drawn index, and
recomputes the ratio of means above. Use recorded bootstrap seed
`2026092404`; the eventual Rust analyzer must also record its PRNG algorithm,
crate/lock identity and resample-index digest so this seed is reproducible.
Sort the 10,000 finite replicate ratios; the two-sided percentile 95% CI
uses nearest-rank one-based positions 250 and 9,750. No resampling of raw
operations occurs inside a VM, no trial is winsorized or omitted by latency,
and no model selection follows the observed CI. Reject a zero/nonfinite
denominator or malformed sample as invalid evidence.

For these latency endpoints, apply the matrix's **provisional** practical
threshold exactly: a win needs CI upper bound `< 0.95`; a loss from measured
speed needs CI lower bound `> 1.05`; otherwise the timing comparison is
statistically inconclusive. Equality does not pass. Correctness, durability
or hard resource-budget failure is a loss regardless of timing. A valid
focused result does not close the complete P04 row: larger/sparse/cold axes,
physical retention comparability, resource accounting and image/host gates
still require separate evidence. Publish each pair's raw endpoint values,
point ratio, CI, seed, status and receipt links in a Markdown table under
`bench/`, including inconclusive and negative outcomes.

## Failures, identity and comparability gates

Before inference, verify every latency lane has `status=passed`, zero errors,
`operations_requested=operations_completed=1000`, exactly 1,000 successful
ordered samples with phase 0/1..999 as above, the same workload seed and
fixture/final-state hashes, and the expected empty result. Recompute warm
quantiles from JSONL and compare with receipt values. Verify each D01 lane
has one successful scan, its matching fixture/result and its separate
diagnostic receipt; unavailable counters remain null. A scan's 30-second
watchdog timeout, wrong result, incomplete samples, bad checksum, missing
receipt, or guest failure invalidates the **whole pair** for timing; preserve
the failure receipt and guest command log. A measured engine timeout or
correctness failure is reported as a failure/loss, never dropped to recover a
favorable comparison. Do not bootstrap a partial pair. There are no
outcome-dependent extra trials and no replacement of a failed pair, including
one attributed to the harness. If root-cause correction warrants rerunning,
register a **new full eight-pair campaign** and D01 diagnostic pair under a
new immutable campaign ID; preserve the old campaign and never combine them.
If any of eight latency pairs or the one diagnostic pair is incomplete or
failed, this campaign cannot receive a win classification. A CI is computed
only when all eight latency pairs pass; a correctness, guarantee or hard
resource-budget failure can still be reported as a loss.

Across both lanes and all pairs, require the same executable SHA-256,
benchmark and TerraBase source inventories (including dirty-tree file hashes),
matrix hash, dependency-lock hash, compiler flags, workload shape, logical
fixture hash, isolation/durability promise and cache policy. Receipt
`benchmark_revision` and `engine_revision` strings alone are not enough.
Verify the guest manifest's binary/image hashes against the host run manifest;
record Firecracker/kernel/rootfs SHA-256, guest resource checks, volume
filesystem and host CPU/cache conditions. Engine-specific paths and the
`run`/`run-terrabase` selector may differ; record SQLite's WAL/FULL/64-MiB
page-cache and TerraBase's 128-MiB process admission separately because they
are different accounting scopes. If sources, guarantees, logical fixtures
or required environment controls differ, publish the raw lanes and the
specific non-comparability reason, not an invented paired ratio.

The present harness pins historical kernel/rootfs **local bytes** but lacks
their upstream image provenance; candidate image qualification is also open.
It reports that host page cache is not flushed and affinity-pinned CPUs are
not exclusively reserved. The earlier one-scan evidence lacks repeated
paired timing and complete host/cache/resource qualification. Thus this
protocol can organize a focused diagnostic cohort, but a favorable interval
under those conditions is **not** an acceptance win. Preserve that limit in
the result table rather than treating the 5% threshold as a waiver.

## Minimal Rust analyzer contract, not implemented

No paired P04 analyzer currently exists in the inspected `terra-rust` driver.
A later Rust-only analyzer should accept an explicit, immutable pair manifest
listing exactly eight latency pairs and one separate D01 diagnostic pair,
two lane receipt/sample/guest-manifest paths per pair, pair order,
run IDs, and the fixed bootstrap seed. It must validate the gates above,
derive first and warm endpoints from raw JSONL, execute the specified paired
10,000-resample ratio-of-means procedure, and emit machine-readable results
plus a Markdown table in `bench/`. Output must include the analyzer binary
and source/lock hashes, input-file hashes, every exclusion/failure reason,
individual paired values, estimator name/formula, seed and PRNG identity,
resample-index digest, point ratio, CI endpoints, provisional classification,
and unqualified environment gates. Implement and test that contract before
the first cohort is analyzed; this document neither creates an analyzer nor
runs a benchmark.
