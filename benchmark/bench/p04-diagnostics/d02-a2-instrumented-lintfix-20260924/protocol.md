# P04 D02 instrumented TerraBase scan diagnostic

Status: **lintfix source prepared; no build or guest execution of this derivative yet**. D02 is one
observational, TerraBase-only derivative of the sealed
`p04-repeat-20260924-a2` source. Its binary must have its own SHA-256; the a2
ELF `93e2099f1797033aab0263732bacb551209dad064ccc5847dcd66b3a17cf5ed2`
is a parent identity, never the D02 executable. The parent source inventory is
`b5f251d333300086f15bba8df583ad6e267c7012f8975d5f892c459aff07969e`.
The derivative's `source-manifest.json`, `source-inventory.sha256`, and
`source-delta.patch` bind the copied Cargo source, exact changes, and lockfile.
The benchmark repository HEAD observed during preparation is
`5685f163bb7e9339e8eb0c839fe2512aaf562648`; TerraBase HEAD is
`352d9f61185d1b685e3032519579040d169ad47d`. Both live trees are dirty,
so their revision strings do not identify the derivative.
This packet derives from frozen D02 source manifest
`fa768490a5e2f7b3b2f0450d01a47e9b88caa56bfac7733de768708bd8549f78`.
The original packet's `lint-d02` gate found two Clippy `struct_field_names`
warnings; this derivative adds only narrowly scoped allowances to preserve
the existing JSON field names and nanosecond units. Its failed gate log is at
`../d02-a2-gates-20260924T212742Z/lint-d02.log`. All listed gates need a
fresh run against this derivative before a guest is scheduled.

## Fixed run and interpretation

Use one fresh 4-vCPU, 8,192-MiB, swap-off Firecracker guest and fresh volume.
The binary itself creates exactly 1,024 one-byte rows with seed 1, deletes
them in eight 128-row batches, and checkpoints after deletes. It requires the
D01 logical fixture SHA-256
`1d5c1a3d24146cea8f09abafd72ce11bbedbac9722c3fb40e3cc9ae46f0504d5`
and a single immutable run with checkpoint and visible sequence 16, zero
mutable bytes, and an empty `row_limit=1` full-range scan. It performs one
`first_after_fixture` scan followed by exactly 32 ordered `warm` scans.
Do not call the first scan cold: fixture construction has already touched the
guest and host caches.

Each scan records operation wall time, calling-thread CPU time from
`CLOCK_THREAD_CPUTIME_ID`, the disjoint coarse `Instant` wall brackets for
prelude, cursor open, initial advance, and merge, plus a nested block-load
bracket and scan-work counter deltas. `Storage::scan_d02` inclusive wall time
contains unbracketed work such as locking and return/drop overhead. The
block-load bracket is a subset of advance/merge and must never be added to
those phases. Missing CPU or counter fields remain null. The 64 empty
`Instant` brackets calibrate probe scale and are not subtracted from samples.
These wall clocks include scheduling effects and instrumentation overhead;
they cannot apportion D01's whole-operation thread CPU among phases.

D02 produces no SQLite lane, paired ratio, confidence interval, acceptance
classification, optimization, or new P04 cohort. Do not combine its timings
with the uninstrumented a2 ELF's latency samples. The only permitted reading
is where time and scan work appear in this instrumented scan path, subject to
probe overhead and host/cache conditions.

## Source and low-priority build gate, to run before the guest

From this directory, verify all 81 source entries with
`sha256sum -c source-inventory.sha256 --quiet`, compare the inventory and
source-delta file hashes to `source-manifest.json`, and verify that the copied
driver `Cargo.lock` remains `3c1a4f2aabd538ad3c6e106dcf1cb6dcec62d302b33d4b4f1666be3868a2a723`.
Any source change requires a new delta and inventory, updated manifest hashes,
and fresh gates before execution. Keep the original a2 source and measured ELF
untouched. Send the frozen source and this protocol for architecture review
before scheduling these gates.

Run each gate at the lowest CPU and I/O priority, with at most two Cargo jobs
and a derivative-local Cargo target directory. Preserve command, exit status,
toolchain identity, and logs under this `bench/` directory. The prospective
commands from `ts-benchmark/terra-rust` are:

```sh
export CARGO_TARGET_DIR=../../target-d02
nice -n 19 ionice -c 3 make fmt-d02
nice -n 19 ionice -c 3 make check-d02
nice -n 19 ionice -c 3 make lint-d02
nice -n 19 ionice -c 3 make build-guest-d02
sha256sum "$CARGO_TARGET_DIR/x86_64-unknown-linux-musl/release/p04-d02"
```

The guest payload must be the built static musl `p04-d02`, with its SHA-256
recorded in `source-manifest.json` and the operator log before launch. Verify
the guest payload's hash and executable type, the pinned controller and guest
agent hashes, and the effective image/guest resource manifest. A changed source,
lock, payload, or harness input needs a new recorded identity and gate.

## One guest command and evidence placement, after the gates

Use a unique run ID, provision the existing pinned a2 Firecracker controller
and guest agent, and pass the derivative payload. Here `BENCH_ROOT`,
`ENGINE_ROOT`, `PINNED_HARNESS`, `PINNED_AGENT`, and `PROGRAM`
are absolute, operator-verified paths; `PROGRAM` is the hash-recorded
`p04-d02` musl ELF. The command is prospective and has not been run:

```sh
RUN_ID=p04d02a2t01
FC_BENCHMARK_REPO="$BENCH_ROOT" FC_GUEST_AGENT_PATH="$PINNED_AGENT" \
  "$PINNED_HARNESS" run \
  --benchmark-repo "$BENCH_ROOT" --subject-repo "$ENGINE_ROOT" \
  --program "$PROGRAM" --run-id "$RUN_ID" --timeout-seconds 300 \
  --output "$BENCH_ROOT/bench/firecracker-harness/$RUN_ID" -- \
  --database "/root/fc-bench-data/$RUN_ID.terrabase" \
  --samples "/root/fc-harness-output/$RUN_ID-samples.jsonl" \
  --receipt "/root/fc-harness-output/$RUN_ID-receipt.json" \
  --report "/root/fc-harness-output/$RUN_ID.md" \
  --guest-manifest /root/fc-harness-output/guest-manifest.json \
  --derivative-inventory-sha256 deeffbbbc7b1559de4b47c286a904bee64967f20a0d012f66bd8c3da2b9303f7 \
  --source-delta-sha256 88198cfd293d895a49d6830f9b5c83eb781e2e997716984adc95c20fb5b5a5c7
```

The frozen derivative inventory and delta arguments must be refreshed if
their files change. The harness's `--benchmark-repo` and `--subject-repo`
describe its live host checkouts; the derivative source inventory and binary
hash are the decisive D02 source identity. Never identify D02 with the a2 ELF.

The harness retains `run-manifest.json`, VM logs, and `guest-output.tar.gz`
under `bench/firecracker-harness/<run-id>/`. After the run, copy the guest
receipt, all 33 ordered JSONL scan slots, guest manifest, and generated
Markdown table into this directory's `results/<run-id>/`; retain the raw
archive and hashes. The Markdown table is the required visible benchmark
record under `bench/`. Record host CPU/cache contention, image provenance,
failed or unrun slots, and all missing fields. A failed scan or VM run remains
a failed D02 diagnostic; preserve its evidence and use a new run ID for any
corrected attempt. Do not issue a speed verdict from D02.
