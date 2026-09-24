# Embedded SQLite baseline driver

This crate is benchmark-only and lives outside the TerraBase production dependency graph. It pins `rusqlite 0.40.2` and bundled SQLite `3.53.2`. The v1 workload and scoreboard are registered in [`../bench/terrabase-rust-acceptance-v1.md`](../bench/terrabase-rust-acceptance-v1.md). The driver reports **SQLite measurements only**; no TerraBase performance claim is valid until an equivalent real embedded adapter, resource accounting, and paired Firecracker trials exist.

Build with the lowest CPU/I/O priority. The guest has Ubuntu 24.04 and no Rust toolchain, so use the statically linked musl artifact. Install the pinned `x86_64-unknown-linux-musl` Rust standard library and provide a musl cross C compiler for bundled SQLite:

```sh
rustup target add x86_64-unknown-linux-musl --toolchain 1.98.1
nice -n 19 ionice -c 3 make -C terra-rust build-guest MUSL_CC=/path/to/x86_64-unknown-linux-musl-gcc
```

The executable is `terra-rust/target/x86_64-unknown-linux-musl/release/terrabase-sqlite-baseline`; hash this exact artifact in the harness receipt. `make check` compiles the source on the host but is not a test or benchmark result. Guest runtime execution supplies the first end-to-end verification.

The harness runs the release binary **inside its 4-vCPU/8,192-MiB Firecracker engine guest**, with a freshly created data path and a guest manifest. Example (paths are guest paths):

```sh
terrabase-sqlite-baseline run \
  --case point-hit-random --rows 100000 --operations 100000 \
  --body-bytes 256 --seed 1 --trial 1 \
  --database /mnt/ssd/point-hit-1.sqlite \
  --samples /mnt/results/point-hit-1.jsonl \
  --receipt /mnt/results/point-hit-1.json \
  --guest-manifest /mnt/results/guest-manifest.json \
  --benchmark-revision <ts-benchmark-git-revision> \
  --engine-revision <terrabase-git-revision>
```

The guest manifest is a harness-generated JSON object containing `vcpu_count: 4`, `mem_size_mib: 8192`, `swap_disabled: true`, and `firecracker_version`, plus the actual image hashes, CPU topology, SSD and filesystem setup, mount options, cache state, network topology, volume reuse/reset state, and host contention record. The driver checks the mandatory envelope fields, embeds the manifest and its SHA-256 in the receipt, and records its own binary hash, SQLite version, source revisions, fixture hash, timings, errors, integrity check, and DB bytes. The harness must independently verify those manifest claims against the running VM. The driver refuses to overwrite prior data or receipts. Raw per-operation samples are JSONL. Keep raw and summary outputs under `bench/` when copied from the guest, with a Markdown results table; no host run qualifies.

Cases: `point-hit-random`, `point-miss-random`, `point-hit-sequential`, `indexed-equality`, `ordered-range`, `insert`, `update`, `delete`, `upsert`, `read-modify-write`, and `mixed`. All use one client in this initial driver. The fixture uses deterministic u64 keys, a byte payload, an integer group key and sort key, plus SQLite JSONB. The matching physical index is `(group_key, sort_key, id)`. Reads use prepared SQL, materialize the body, and validate returned keys and bytes. Mutations use one `synchronous=FULL` WAL commit per operation; RMW uses a `BEGIN IMMEDIATE` transaction. Fixture construction and raw sample emission are outside the measured interval. A run is limited to 10 million operations to bound raw latency memory. This is a closed-loop throughput baseline; open-loop offered-rate, concurrency, cold cache, crash/recovery and complete resource cases remain open in the matrix.

The future TerraBase adapter must call the **real embedded engine** with the same key bytes (`u64::to_be_bytes`), payload generator, operation order, index and transaction semantics, then validate returned values before a paired ratio is computed. Its first local-durable operation contract is `open(path, LocalDurable)`, `get`, `put`/`upsert`, `delete`, ordered range, indexed equality, atomic batch and read-modify-write, `close`/`reopen`, and durability/resource receipts. Adapter paths are intentionally absent until the engine caller is real.
