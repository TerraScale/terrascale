# Routed restore implementation handoff — 2026-09-09

Base HEAD: `afe177c7aaa34caed76a5d4afea57a028d558936`. No commit/push. Independent Astra review required; this is not production acceptance.

## Established causes and implemented correction

- Physical shards share the logical database ID. Capture now keys physical namespaces by database incarnation, shard and origin lane, while retaining the complete admitted participant (including its pinned snapshot) through blocking capture.
- Logical archives omit private route metadata, and split roots retain stale moved documents. Envelope v2 authenticates file identity, root designation and route ranges; snapshot capture filters by those ranges at the pinned cut.
- Envelope selection was being sent to scalar manifest restore. Native dispatch now recognizes the envelope, downloads every exact participant and invokes one staged-tree restore transaction.
- Per-shard directory exchange would publish partial databases. The new core envelope restore replays and independently reads back all fresh files, installs a root catalog/child bindings and publishes the whole directory tree once with one durable marker.
- New owned files open fenced at epoch zero. Restore activates its accepted epoch before the internal route metadata transaction. `at_location` does not create directories: staged child engine directories are explicitly created before open. These were independently exposed by the two-file regression, not worked around.
- Existing files must reopen with persisted lane identity. Cross-file integration owns the corresponding engine/member changes and their further validation.
- Lifecycle output uses one envelope-facts mapper. Supplied request envelope facts must match the selected catalog envelope, or the completed durable receipt on retries after source removal.

## Exact checks

Isolated source `/tmp/terrabase-restore-check.v06vvE/source`, target sibling `target`. Rust nice19, jobs2. Existing benchmark processes were preserved. No live fixer Cargo session remains.

- `cargo test --locked -j 2 -p terrabase --lib backup::`: **107 passed, 1 ignored, 0 failed**. Log `/tmp/terrabase-restore-check.v06vvE/backup-suite.log`; frozen source hashes `backup-suite.source-sha256` there.
- Includes new two-file encrypted restore, missing participant rejection, injected post-exchange parent-sync failure and marker reconciliation; assertion proves the injected failure was consumed. Temporary diagnostic logging removed.
- Includes routed stale-copy filter, authenticated envelope checks, provider-failure publication, existing scalar restore/PITR/retention coverage.
- `cargo check --locked -j 2 -p terrabase-server`: **passed**, including latest supplied-facts validation. Log `server-facts-check.log`; source hashes `server-facts.source-sha256`. Existing warnings remain.
- `git diff --check`: passed on live shared tree.

## Ownership and remaining acceptance work

- `engine_gate` exclusively owns NEW `crates/terrabase-server/src/routed_restore_lifecycle_tests.rs`; no such file was written by fixer. It must test actual admitted two-participant EnsureSnapshot → resolve → lifecycle restore → public reads/reopen, excluding stale moved root data, plus missing/corrupt/tampered facts.
- `envelope_retention` owns NEW `src/backup/worker/envelope_retention.rs` and narrowly `src/backup/object_store.rs` bounded list/delete seam. Helper is not yet compiled or integrated. Requested expired-only deletion candidates: existing helper initially included retained envelopes among acknowledgeable digests.
- Fixer currently retains `worker.rs` and server existing-file integration until coordinator explicitly transfers them. `member/mod.rs` and `member/shard_registry.rs` already transferred to `cross_file_integration`; do not overwrite.
- Wire helper into actual scheduled capture expiry/retry/purge and physical archive-cache discovery. Account aggregate nested bytes/entries, historical destinations, partial provider failures and orphan captures. Helper-only tests are not completion.
- Keep `ensure_native_backup_shard_capture_supported` and its test until real lifecycle and retention qualification passes. Remove guard and obsolete guard-only test in same series when ready.
- Review EnsureSnapshot measure's retained-envelope shortcut: provider/authority verification must not be bypassed by the local discovery catalog.
- Independent Astra source review and full project `make check` remain. Snapshot contains concurrent cross-file work but does not qualify later active-marker edits.

## Prevention notes

Do not model physical participant identity as logical database ID. Do not assume logical backup contains private routing state. Do not apply one-file publication repeatedly. Do not infer fresh-file lane identity when reopening durable files. Snapshot builds must include the benchmark retained-selection fixture and must stop on failed source-copy commands before launching Cargo.
