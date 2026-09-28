# Embedded SQLite and TerraBase benchmark driver

## Three-way P04 deleted-history diagnostic

`p04-three-way` runs the same 1,024-key, one-byte, all-deleted, explicitly
checkpointed scan fixture against embedded SQLite, embedded Turso Database
0.7.2, or TerraBase. It uses one feature-enabled executable for all lanes,
checks WAL/FULL/64-MiB page-cache PRAGMAs for both SQL engines, verifies
TerraBase's local durable commits, and writes one raw JSONL sample per scan.
The first scan is reported separately from warm repeats. This focused
diagnostic does not replace the registered full acceptance matrix.

Build the static guest executable with
`make build-guest-three-way`. Execute each lane with `make -C
../firecracker-harness guest-run` on a fresh VM and unique run ID, using the
same binary, `--scans 1000`, and the required arguments `--engine`,
`--database`, `--output`, `--guest-manifest`, `--benchmark-revision`, and
`--terrabase-revision`. The database belongs under `/root/fc-bench-data/`;
set output to a fresh child of `/root/fc-harness-output/` so the guest agent
archives `samples.jsonl` and `receipt.json`. `--engine` accepts `sqlite`,
`turso`, or `terrabase`. Store the comparison table and source/guest receipt
links under `bench/`.

This crate is benchmark-only and lives outside the TerraBase production dependency graph. It pins `rusqlite 0.40.2` and bundled SQLite `3.53.2`. The v1 workload and scoreboard are registered in [`../bench/terrabase-rust-acceptance-v1.md`](../bench/terrabase-rust-acceptance-v1.md). It has SQLite and feature-gated embedded TerraBase callers; their individual diagnostic receipts do not establish a performance verdict without the registered paired trials and resource gates.

The guest has Ubuntu 24.04 and no Rust toolchain, so use the statically linked musl artifact. Install the pinned `x86_64-unknown-linux-musl` Rust standard library and provide a musl cross C compiler for bundled SQLite:

```sh
rustup target add x86_64-unknown-linux-musl --toolchain 1.98.1
make -C terra-rust build-guest MUSL_CC=/path/to/x86_64-unknown-linux-musl-gcc
```

The executable is `terra-rust/target/x86_64-unknown-linux-musl/release/terrabase-sqlite-baseline`; hash this exact artifact in the harness receipt. For the real embedded TerraBase caller, use `make check-engine`, `make lint-engine`, and `make build-guest-engine MUSL_CC=...`. The resulting feature-enabled binary accepts both `run` (SQLite) and `run-terrabase` (the same CLI arguments, real `tb-storage` API). Use that **same binary hash** for paired guest trials. `make check` compiles source on the host but is not a test or benchmark result. Guest runtime execution supplies the first end-to-end verification.

Bare `make -C terra-rust` prints help; lockfile refresh requires explicit `make lock`. `make test-build` compiles the feature-enabled unit-test executable for musl with `--no-run`. Cargo prints its path relative to `terra-rust`; resolve it to an absolute path. Run that executable in Firecracker through `make test-vm` (or `make test`) with a unique run ID and a frozen controller/guest-agent pair:

```sh
make -C terra-rust test-build
make -C terra-rust test-vm \
  TEST_BINARY=/absolute/path/to/printed/musl/test-executable \
  RUN_ID=unique-adapter-unit-run \
  HARNESS_BINARY=/absolute/path/to/frozen-packet/fc-harness \
  GUEST_AGENT_BINARY=/absolute/path/to/frozen-packet/fc-guest-agent \
  HARNESS_SHA256=<controller-sha256> \
  GUEST_AGENT_SHA256=<agent-sha256>
```

The VM target requires both executables from one packet directory and verifies their exact hashes before calling `firecracker-harness test-vm`; it never runs the unit tests on the host. `SUBJECT_REPO` defaults to the sibling TerraBase checkout and can be set explicitly. The harness keeps the guest test output, binary hash, and run manifest under `bench/firecracker-harness/<RUN_ID>/`. A missing packet, mismatched hash, or unavailable Firecracker guest is a failure, not a skipped test.

The harness runs the release binary **inside its 4-vCPU/8,192-MiB Firecracker engine guest**, with a freshly created data path and a guest manifest. Example (paths are guest paths):

```sh
terrabase-sqlite-baseline run \
  --case point-hit-random --rows 100000 --operations 100000 \
  --body-bytes 256 --seed 1 --trial 1 \
  --database /root/fc-bench-data/point-hit-1.sqlite \
  --samples /root/fc-harness-output/samples.jsonl \
  --receipt /root/fc-harness-output/receipt.json \
  --guest-manifest /root/fc-harness-output/guest-manifest.json \
  --benchmark-revision <ts-benchmark-git-revision> \
  --engine-revision <terrabase-git-revision>
```

The guest manifest is a harness-generated JSON object containing `vcpu_count: 4`, `mem_size_mib: 8192`, `swap_disabled: true`, and `firecracker_version`, plus guest-observed CPU, memory and swap, pinned image hashes, source identities, host CPU affinity, output filesystem, and the disabled-network statement. The current harness does not yet qualify a dedicated SSD, mount and cache state, volume reset, or exclusive host CPU use; those remain acceptance gates. The driver checks the mandatory envelope fields, embeds the manifest and its SHA-256 in the receipt, and records its own binary, source, matrix and dependency-lock hashes, SQLite version, source revisions, fixture hash, timings, errors, integrity check, and DB bytes. It scans every final row against independently regenerated keys, payloads and revisions, then records a logical SHA-256 over big-endian u64 key, u64 payload length, payload bytes, and big-endian u64 revision. The TerraBase adapter emits the same logical digest and its observed fixture state. The harness must independently verify manifest claims against the running VM. The driver refuses to overwrite prior data or receipts. Raw per-operation samples are JSONL. Keep raw and summary outputs under `bench/` when copied from the guest, with a Markdown results table; no host run qualifies. The guest agent archives `/root/fc-harness-output` up to 512 MiB, so keep the database at a fresh child of `/root/fc-bench-data` outside that directory; the VM is fresh and is discarded after the receipt is archived.

Cases: `point-hit-random`, `point-miss-random`, `point-hit-sequential`, `indexed-equality`, `ordered-range`, `insert`, `batch-insert` (16 inserts per durable commit), `update`, `delete`, `upsert`, `read-modify-write`, `mixed`, `reopen` (one clean reopen and first query), and `deleted-history` (one-row empty or sparse-tail scan, repeated up to 10,000 times over one fixture). All use one client in this initial driver. Every case has deterministic u64 big-endian keys and byte payloads. Point, range and mutation cases use a primary-key SQLite table without an unrelated secondary index or document field. Indexed equality uses a separate fixture with integer group/sort fields, SQLite JSONB, and the `(group_key, sort_key, id)` index. The driver runs `ANALYZE`, records the selected query plan, and refuses an indexed-equality result without that index. `run-terrabase` explicitly rejects indexed equality until a real secondary-index caller exists. Reads materialize and validate returned values. Mutations use `synchronous=FULL` WAL or the engine's local-durable receipt; RMW uses a SQLite `BEGIN IMMEDIATE` transaction or TerraBase snapshot/conflict commit. Fixture construction and raw sample emission are outside the measured interval. `--checkpoint-fixture` requests a final checkpoint; omitting it does not disable the engine's bounded automatic checkpoints. The TerraBase receipt records immutable-run count, checkpoint sequence and mutable bytes before measurement, and labels the observed physical state. `deleted-history` also accepts `--checkpoint-before-deletes` and `--sparse-tail`. A run is limited to 10 million rows and operations each to bound fixture and raw-latency memory; current TerraBase mutation runs are limited to 50,000 measured commits while its retry table is bounded. This is a closed-loop throughput baseline; open-loop offered-rate, concurrency, cold cache, dirty crash recovery and complete resource cases remain open in the matrix.

The embedded adapter calls `tb-storage` directly. It encodes the logical revision as an eight-byte prefix of each engine value and verifies full payloads, commit durability frontiers, final rows/revisions, and a logical SHA-256 matching the SQLite receipt's key/payload/revision format. It records storage, WAL, admitted memory and process counters. SQLite's 64-MiB page cache and TerraBase's 128-MiB process admission budget are separate accounting scopes, both subject to the common 8-GiB guest limit; paired resource claims require measured whole-guest evidence and fair cache allowances. The SQLite and TerraBase receipts are **individual lane evidence**, not an automatic win/loss classifier. They become comparable only with a verified Firecracker manifest, source-matched fixture and guarantees, repeated paired trials, statistical intervals and all matrix resource gates. `--scan-diagnostics` is a separate one-operation `ordered-range` or `deleted-history` run for both engines. It samples calling-thread CPU runtime with Linux `CLOCK_THREAD_CPUTIME_ID` and process read/write bytes from `/proc/self/io` around the measured operation; unavailable fields are JSON `null`, never zero-filled. The receipt names the CPU clock and records the enclosing CPU-bracket wall interval plus its observed overhead outside the pure operation interval; the latter includes probe and scheduling cost and is not subtracted from CPU time. Earlier receipts sampled `/proc/self/schedstat`; their CPU deltas are unqualified and must not be compared with the new clock's values. The TerraBase lane also snapshots cumulative storage scan-work counters outside that interval and reports the delta for run opens, metadata calls, directory/header/footer/block bytes, and examined candidates. The scan-work delta now also reports `run_block_cache_evictions` (checked block buffers discarded under cache pressure) and `run_block_rereads` (successful loads of a previously loaded block by that scan cursor). They are cumulative saturating engine counters subtracted immediately before and after one scan; absence in older receipts means unavailable, not zero. Counter file reads and storage snapshots are outside the operation latency clock, and these diagnostic runs do not replace unprofiled repeated latency/throughput trials. `deleted-history` accepts one scan for first-scan diagnostics or 2–10,000 scans for repeated measurements on the same fixture. In repeated runs, sample 0 is labeled `first_after_fixture` and later samples `warm_repeat`; the receipt retains the all-sample `latency` field and adds `warm_scan_samples` and warm-only `warm_scan_latency` quantiles. The first scan is not called cold because no cache reset is established. A single watchdog worker enforces a separate 30-second deadline for each active scan, then is canceled and joined before final verification. A timed-out scan exits with code 124 and writes a `timed_out` receipt with its zero-based `timeout_operation`, already completed count, fixture and manifest hashes, and source identities. Earlier completed scan samples are buffered until the run ends and are unavailable when a later scan times out; `operations_completed` in the timeout receipt is not a persisted-sample count. A timed-out or failed run must not enter successful latency summaries or paired statistics. Retain that receipt and the guest command log as a failure, not a dropped case. Use separate one-scan `--scan-diagnostics` runs for CPU, I/O, and scan-work counters; repeated runs do not aggregate those diagnostic fields.

For paired repeated P04 runs, use the same newly built and source-pinned feature-enabled binary in both lanes, with fresh output paths (guest paths shown):

```sh
terrabase-sqlite-baseline run --case deleted-history --rows 1024 --operations 1000 \
  --body-bytes 1 --seed 1 --trial 1 --checkpoint-fixture \
  --database /root/fc-bench-data/p04-sqlite.sqlite \
  --samples /root/fc-harness-output/p04-sqlite-samples.jsonl \
  --receipt /root/fc-harness-output/p04-sqlite-receipt.json \
  --guest-manifest /root/fc-harness-output/guest-manifest.json \
  --benchmark-revision <ts-benchmark-git-revision> --engine-revision <terrabase-git-revision>
terrabase-sqlite-baseline run-terrabase --case deleted-history --rows 1024 --operations 1000 \
  --body-bytes 1 --seed 1 --trial 1 --checkpoint-fixture \
  --database /root/fc-bench-data/p04-terrabase \
  --samples /root/fc-harness-output/p04-terrabase-samples.jsonl \
  --receipt /root/fc-harness-output/p04-terrabase-receipt.json \
  --guest-manifest /root/fc-harness-output/guest-manifest.json \
  --benchmark-revision <ts-benchmark-git-revision> --engine-revision <terrabase-git-revision>
```

The prospective [P04 repeat protocol](../bench/p04-repeat-protocol-20260924.md) fixes eight alternating 1,000-scan latency pairs and a separate one-scan D01 diagnostic pair. After all guest outputs are copied into a sealed campaign directory, run the analyzer-enabled executable inside the analysis VM from the staged `ts-benchmark` root:

```sh
terrabase-sqlite-baseline analyze-p04 \
  --campaign bench/p04-campaigns/p04-repeat-20260924-a1/campaign.json \
  --json-out bench/p04-campaigns/p04-repeat-20260924-a1/reports/analysis.json \
  --markdown-out bench/p04-campaigns/p04-repeat-20260924-a1/reports/analysis.md
```

The immutable `campaign.json` has `schema_version: 1`, a unique `campaign_id`, the protocol SHA-256, `bootstrap_seed: 2026092404`, a `measured` object with the release program SHA-256, source-inventory and artifact-map paths and hashes, source revisions, kernel/rootfs hashes and Firecracker version, `pairs` with IDs `01`–`08` in preassigned alternating order, and `diagnostic` with ID `D01`. Every pair has `sqlite` and `terrabase` lanes. Each lane names its run, VM and volume IDs, cache policy, host CPU control, reset evidence, guest command log, host run manifest, guest manifest, receipt, JSONL samples, compiled-binding sidecar, and archived guest output. The sidecar records the archive hash plus `guest_args`, `host_command_argv`, and `guest_command_log_sha256`; the command log records the exact guest argv. An `operator_log_sha256` in that sidecar retains its separate meaning as the digest of the plain-text host operator log. The analyzer checks serial host timestamps and the guest command shape, then matches the receipt, samples, and guest manifest byte-for-byte to their archive members. Every referenced artifact uses a unique flat filename beside `campaign.json`; the original run directories may be preserved separately. Keep the campaign JSON byte-identical when moving the sealed artifact directory into an analysis guest. The analyzer writes fresh JSON and Markdown under `bench/`, preserves failed lanes, and exits nonzero on an invalid campaign. It computes intervals only after all eight latency pairs pass; D01 follows pair 08 and remains outside the timing resamples. Its classifications are provisional because the full P04 acceptance and environment gates remain separate. The pinned artifact map is an operator build assertion, not cryptographic proof of compilation from its listed sources.

`p04-analysis-launcher` runs that analyzer inside one Firecracker guest. Build it with `make build-guest-launcher MUSL_CC=...`; `make test-build-launcher` compiles its musl unit tests without running them. Stage the launcher as the harness's main program and pass the analyzer executable and the compressed flat campaign bundle as the supplemental payloads `p04_analyzer` and `p04_a2_bundle`. It requires `--campaign-dir` (the flat directory name, which must equal `campaign_id`), `--bundle-sha256`, `--analyzer-sha256`, and `--campaign-sha256`, verifies those inputs, extracts the bundle under `/root/fc-bench-data`, and writes the analyzer reports to `bench/p04-campaigns/<campaign-dir>/reports/` inside the guest output archive, plus `p04-launcher-receipt.json`.
