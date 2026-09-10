# Astra backup hardening packet — 2026-09-09

Review input only; this packet does not approve correctness. It covers the
logical encrypted backup/retry, restore publication, object-store immutable
upload, worker inventory/retention, and native runtime fixture corrections in
the `7b58c5ee..16f81280` cohort. The auxiliary encrypted physical snapshot
roundtrip is excluded while `/root/snapshot_roundtrip_fix` owns its failing
`Snapshot` result.

## Source identity and complete inputs

The intended reviewed base is `16f81280` (inclusive authored history starts at
`7b58c5ee`). The live checkout is now `a5aac458` with an unrelated dirty
`crates/terrabase-server/src/native_snapshot_backup.rs`; that path and the
`a5aac458` snapshot implementation/tests are excluded. `a5aac458`'s two
server-only corrections are captured separately because the current server
backup run used them: `config.rs` historical-destination secret fixture and
`native_backup_runtime.rs` restore teardown.

Complete authored diff: [authored-7b58c5ee-16f81280.diff](./diffs/authored-7b58c5ee-16f81280.diff).
Server correction delta: [server-corrections-a5aac458.diff](./diffs/server-corrections-a5aac458.diff).
Full source snapshots at `16f81280` are in `source/`; hashes for all packet
inputs are in [SHA256SUMS](./SHA256SUMS). The complete changed paths are:

| Area | Paths and changed ranges |
|---|---|
| Native server seam | `crates/terrabase-server/src/lib.rs`; `native_backup_runtime.rs:127,286-294,912-915`; `native_backup_runtime_tests.rs` (one removed fixture); correction delta `config.rs:2126` and `native_backup_runtime.rs:914-915` |
| Backup capture/retry | `src/backup/mod.rs:1246-1584`; `src/backup/encrypted_retry_tests.rs:42-137`; `src/backup/tests.rs:232-234` |
| Restore publication | `src/backup/restore/mod.rs:1557-1790` |
| Provider immutability | `src/backup/object_store.rs:14,337-394,557-632,1007-1049,1077-1127,1364-1502` |
| Worker lifecycle | `src/backup/worker.rs:16-52,1055-1069,1830,2151-2180,2207-2225,2874-2876,3165-3177`; `worker/inventory.rs:197-245,324-399`; `worker/rotation.rs:27`; new `worker/tests/purge_retained_tests.rs:1-64` |
| Encryption integration | `tests/encryption/main.rs:210-218` |

The cohort also contains unrelated probe deletion (`src/engine/commit_timing_probe.rs`,
`src/engine/mod.rs`), a shard test edit, and an `AGENTS.md` edit; they are
deliberately omitted from this backup packet. No product source, tests,
contracts, or repository state was edited here.

## Shared callers and accepted context

`NativeBackupRuntime::{tick,capture,capture_shard_envelope,restore_from_provider,restore_at_seconds,restore_at_millis}`
(`crates/terrabase-server/src/native_backup_runtime.rs:110-376`) calls
`NativeBackupFleet::{sync_once,capture_snapshot,capture_participants_at,publish_envelope,restore_from_provider}`
(`src/backup/worker.rs:283-426,739-897,1295+`). The logical path is
`BackupPlan::snapshot_at/run_backup` → `seal_run/find_existing_capture` →
`ObjectDestination::upload_point/verify_point` and encrypted restore staging
(`src/backup/mod.rs:1039+,1504+`; `object_store.rs:545+`; `restore/mod.rs:1132+`).
Relevant unchanged production reachability remains in
`crates/terrabase-server/src/api.rs:643-664`, `runtime.rs:1656`,
`production_data_runtime/public_server.rs:233`, and `usage_closure_http.rs:263`;
the retired Litestream `BackupStreams` path in `crates/terrabase-server/src/backup.rs`
is still reachable and is outside this batch.

Accepted context for review: encrypted manifests bind recovery identity and
key-reader context; retries must consume a fresh key ordinal without replacing
an already published immutable point; multipart upload stages under a unique
temporary key and publishes create-if-absent; restore publishes only after
complete staged verification; retention cleanup must distinguish interrupted
ephemeral points from retained points. Review should assess the actual callers
and interactions, including the common-cut envelope contract, rather than
infer correctness from helper tests.

## Evidence and readiness

| Check / source identity | Result | Evidence and limits |
|---|---|---|
| Prior backup core focused run | 92 passed, 1 ignored | Owner-reported prior evidence; exact run log/source hash was not supplied in this packet and must not be treated as proof for a changed checkout. |
| Prior recovery focused run | 5 passed | Owner-reported; exact run log/source hash likewise missing. |
| Current native server backup run | historical-destination fixture passed; two-database restore teardown passed | Owner report on the correction delta in `server-corrections-a5aac458.diff`; no immutable terminal log was available here. |
| Auxiliary encrypted snapshot roundtrip | **FAILED** (`Snapshot`) | Pending `/root/snapshot_roundtrip_fix`; excluded from review scope. Do not inspect its unstable helper as approval evidence. |
| `git diff --check` | exit 0 | Applied to the authored backup paths and server correction delta. |
| broad compile / `make check` | pending | No Cargo/build/check was run by packet preparation. |

Routine submission still requires owners to record exact command, exit status,
tested source identity and immutable log for the core backup, recovery, and
current native-server selectors, then clear applicable formatting/compile/
focused-test failures. Reuse valid evidence only when its source hash matches;
the old `native-backup-runtime-focused-20260908.log` is historical and records
`4 passed, 1 failed` with `Fleet(Capture)`, so it cannot qualify this correction.

## Interactions, limits, and next batch

Review priorities are (1) key-reader based encrypted retry discovery versus
fresh ordinal allocation, (2) temporary multipart publication versus remote
immutability and cleanup, (3) restore staging publication versus crash/error
boundaries, and (4) worker inventory/retention versus restart discovery.
Known follow-up concerns are that the runtime async gate is still bypassed by
sync methods, and envelope `common_cut` uses local sequence semantics; neither
is resolved by this packet. Production reachability through all API/time-travel
callers, remote provider qualification, restart discovery, and multi-participant
common-cut restore remain unproven. The packet is source-review ready with
explicit pending/failed evidence, but routine acceptance-ready status is **no**.

Next batch: add only the snapshot correction delta after its owner freezes the
helper and publishes a matching pass/fail log; separately collect exact current
server/core/recovery evidence and an owner map for the async-gate and envelope
follow-ups. Do not merge the unstable snapshot into this packet or rewrite this
immutable input set.
