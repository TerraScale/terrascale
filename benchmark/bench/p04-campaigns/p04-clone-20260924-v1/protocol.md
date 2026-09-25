# P04 scan-candidate clone comparison, registered before measurements

Status: **source frozen; variant unbuilt; no comparison trials or speed verdict**.
This is a prospective, uninstrumented comparison of two TerraBase binaries.
It does not reuse the a2 SQLite/TerraBase timing samples, select an estimator
after seeing results, or close the broader P04 acceptance matrix. D03 is
deferred. Any protocol change requires a new campaign ID and a new full cohort.

## Source and binary gates

The baseline is the sealed `p04-repeat-20260924-a2` 81-entry Cargo source
inventory, SHA-256 `b5f251d333300086f15bba8df583ad6e267c7012f8975d5f892c459aff07969e`.
Its `terrabase/crates/tb-storage/src/local.rs` is
`22d5ec284fa8c1d579ca3185e268ccd11556dd39a2beb0297f6cc57e99d33e73`.
The already qualified, feature-enabled, uninstrumented baseline ELF
`../p04-repeat-20260924-a2/payloads/terrabase-sqlite-baseline` may be reused
only if its SHA-256 remains
`93e2099f1797033aab0263732bacb551209dad064ccc5847dcd66b3a17cf5ed2`
and its a2 source/build binding is rechecked. The baseline ELF must run
`run-terrabase`, never `run` (SQLite).

The variant is this directory's `source.sha256`, SHA-256
`90eff776e63210740d1e1be9c9fa3fc41777ff6b9c25d0c0aeee589f00da8da7`.
It has exactly the a2 81 paths and exactly one changed file:
`terrabase/crates/tb-storage/src/local.rs`, SHA-256
`b9642cdee1043c7869e1e19b0ddddbaceb10cd3880b23c6cc7d9b2b530253fbe`.
`source-delta.patch` records the exact field-copy correction. No newer live
engine file, test, driver change, instrumentation, or unrelated correction
belongs in either measured binary. Recheck both inventories and their
one-file delta immediately before building. Build the variant with the a2
driver's feature-enabled musl release `build-guest-engine` target, low CPU/I/O
priority, the same lock, target, compiler/toolchain, flags and link inputs.
Record all gates, build logs, ELF SHA-256, static linkage checks, and an
immutable source-to-ELF binding. If the build changes any input or differs
from the a2 build environment, stop and register a new source identity before
running VMs. Do not infer binary identity from Git revision strings.

## Fixed fixture and trial schedule

Use only `run-terrabase --case deleted-history --rows 1024 --body-bytes 1
--seed 1 --trial 1 --checkpoint-fixture --operations 1000`, with no
`--checkpoint-before-deletes`, no `--sparse-tail`, no `--scan-diagnostics`,
and a fresh database and volume per lane. This is the a2 checkpointed
deleted-history fixture: 1,024 deleted keys, an empty full-range scan with
`row_limit=1`, and a post-delete checkpoint. Require matching logical
fixture/result hashes and record the actual physical state, including
immutable runs, checkpoint sequence, mutable bytes, and WAL/run bytes.
Differences in fixture state invalidate comparability even if both scans are
empty. The driver, CLI arguments other than unique paths/IDs, durability
promise, and cache policy must match across binaries.

Run exactly **eight independent VM pairs**, each with one baseline and one
variant lane, and 1,000 scans per lane. Preassign pairs 01, 03, 05, 07 in
`baseline→variant` order, and 02, 04, 06, 08 in `variant→baseline` order.
Run all eight without interim stopping or expansion; never reuse the old a2
VM timing samples. Each lane needs a fresh reset 4-vCPU, 8,192-MiB, swap-off
Firecracker VM and rootfs copy, a fresh volume/database, the same kernel and
rootfs hashes, equivalent CPU placement and host-activity controls, and the
same explicit cache policy. Record VM/volume IDs, pair/order, reset evidence,
Firecracker/kernel/rootfs and guest binary hashes, host CPU/cache state,
volume filesystem, host/guest manifests, command logs, raw JSONL and receipts.
Do not call the first scan cold: guest or host page cache may already be warm.
Uncontrolled host cache or CPU contention must be disclosed and withholds a
qualified speed verdict. The 1,000 scans inside one VM are correlated
repeats, not bootstrap units.

Operation 0 is that VM's `first_after_fixture` endpoint. Operations 1..999
are its 999 `warm_repeat` latencies. For each VM separately, sort the 999
warm values and use one-based nearest ranks `ceil(p * 999)` for warm p50,
p95, p99 and p99.9. Compare those four endpoints and first-scan latency as
five separate metrics. Never pool operations across VMs or fold operation 0
into warm quantiles. No diagnostic lane enters this uninstrumented cohort.

## Fixed paired estimator and decision rule

For metric `m`, let `V_i(m)` and `B_i(m)` be the variant and baseline values
in complete pair `i`, in nanoseconds. With `n = 8`, report the ratio of
paired-arm arithmetic means:

`R_m = [sum_i V_i(m) / 8] / [sum_i B_i(m) / 8] = sum_i V_i(m) / sum_i B_i(m)`.

For each metric, draw exactly 10,000 nonparametric bootstrap replicates.
Each draw samples eight pair indices from `0..8` with replacement, carries
both binaries' endpoints together, and recomputes the same ratio. Fix seed
`2026092404`; record the implemented PRNG algorithm, crate/lock identity and
resample-index digest before interpreting a CI. Sort the finite ratios and
take one-based nearest ranks 250 and 9,750 for the two-sided percentile 95%
CI. Do not resample operations, switch to a mean of per-pair ratios, trim
outliers, change the seed, or omit unfavorable pairs. Reject a zero or
nonfinite denominator or malformed sample.

Use the a2 protocol's provisional practical threshold only as a focused
comparison rule: a variant speed win requires CI upper bound `< 0.95`; a
measured speed loss requires CI lower bound `> 1.05`; otherwise timing is
inconclusive. Equality does not pass. A correctness, durability, or hard
resource-budget failure is a loss regardless of timing. Even a favorable CI
is not a qualified speed claim when source, build, fixture, host/cache,
resource or VM gates are open; report the limitation beside the raw result.

## Evidence validity and publication

Before inference, verify both lane receipts have passed status, zero errors,
exactly 1,000 ordered successful samples, matching declared workload and
fixture/result hashes, expected empty rows, and the required physical state.
Recompute warm ranks from JSONL and compare with receipt values. Recheck
both ELF hashes and source/build bindings in each guest manifest. A timeout,
wrong result, missing sample or receipt, checksum mismatch, VM failure, or
source/environment mismatch invalidates the whole pair; preserve its logs.
A measured engine failure is reported as failure/loss, never dropped for a
favorable ratio. Do not bootstrap partial pairs or replace a failed pair.
Any corrected rerun needs a new full eight-pair campaign ID. Compute a CI
only when all eight pairs pass. Publish per-pair endpoints, ratios, CI,
seed/PRNG/digest, receipt links, gate failures and limitations under `bench/`.
The analyzer must validate this contract from raw files and be tested before
the first cohort is interpreted. No result is claimed by this protocol.
