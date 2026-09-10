# Astra backup hardening v3 — 2026-09-09

Correction packet against the prior `backup-hardening-20260909-refresh2` input.
Review input only; no correctness approval is asserted. This packet captures
the exact `962436c..945401c3` delta for restore flock reconciliation,
multipart cleanup wiring, `external_id` validation, customer-S3 restore
identity, and the associated API/config/runtime/worker/restore changes.

## Source identity

TerraBase HEAD is `945401c32f8ddbce113bf3e1b84963f845b316e0`; its committed tree
is recorded in [source-identity.txt](./source-identity.txt). Product source is
clean at capture. The workspace has unrelated untracked review artifacts and
the build-owner evidence files; no product source was edited here. The complete
authored delta since prior captured HEAD `962436c6` is
[authored-962436c3-945401c3.diff](./diffs/authored-962436c3-945401c3.diff), and
the current product dirty overlay is
[dirty-overlay-945401c3.diff](./diffs/dirty-overlay-945401c3.diff) (empty at
capture). Full affected source files are under `source/`. Hashes are in
[SHA256SUMS](./SHA256SUMS), excluding this index and identity text.

## Covered paths and interactions

The delta changes `crates/terrabase-server/src/api.rs` restore authority and
operation routing (`4962-5190,7977+`), production config and customer
`external_id` validation (`config.rs:1137-1253,2293+`), native recovery
(`native_recovery.rs:712`), production construction/admission/lifecycle and
TBEP lifecycle bindings, `src/backup/object_store.rs` provider/customer-S3
identity and cleanup (`35-130,898-944,1730+`), restore staging and publication
(`src/backup/restore/mod.rs:1427-1777,2762+`), worker restore flock/retention
and customer-S3 tests (`src/backup/worker.rs:1409-1490,2024+,2358+,3186+`),
workload-identity delivery, and customer key delivery.

The relevant runtime chain is API `restore_backup` → production restore/branch
admission → `NativeRecoveryRuntime`/`NativeBackupFleet` → restore staging,
provider identity resolution, publication and cancellation cleanup. Customer
S3 restore must retain canonical provider identity through `ObjectDestination`
and remote point resolution. Multipart temporary objects must be cleaned on
publication races, cancellation and failure. Config `external_id` bounds feed
the workload identity provider and customer key policy. Astra should inspect
these cross-file connections and the unchanged operation/restore contracts.

## Evidence

Copied owner evidence is under `evidence/`:

- `backup-resume-20260909-v2/core.log`: exact command output bound to
  `HEAD_BEFORE 945401c3`; it reports 106 tests, **104 passed, 1 failed, 1
  ignored**, with `retained_remote_history_restores_selected_chain_and_migrates_after_restart`
  failing on `Upload`. Its tree capture failed because `.git/index.lock` was
  present, so it is source-commit-bound but not tree-bound.
- `backup-resume-20260909-v2/fmt.log`: empty; no terminal result is recorded.
- `customer-s3.log` and `reconciliation.log`: one pass each (no source hash in
  either log; retain as owner evidence without promoting them to current-tree
  proof).
- `object_store.log`: 13 passed, 1 ignored (the MinIO test is explicitly
  ignored because no S3 emulator is running).
- `publication.log`: **2 passed, 2 failed**; both publication exchange cases
  fail and therefore leave restore publication unresolved.
- `workload.log`: 3 passed, 0 failed after a 69.80-second run; the log has no
  source hash.
- `retained-history.log`: **1 failed** against HEAD `38bcdb31` / tree
  `ec69ae98`; this is later than the requested `945401c3` capture and is an
  explicit current drift/failure signal, not v3 source-bound evidence.
- `core-backup-selector.log`, `capture_snapshot.log`, `native_fleet.log`, and
  `sustained_capture.log`: exact passes, but all are bound to the older
  `HEAD 4f25f861` / tree `19de9f6`, therefore historical evidence rather than
  current `945401c3` proof. They report respectively 101 passed/1 ignored,
  and one pass each for native-fleet restore, capture during writes, and
  sustained rotation.

No Cargo or build was run by packet preparation. The v2 targeted run is failed
evidence and requires a rerun against a stable source/tree before routine
submission. Keep the old 4f25 evidence separate from current source capture;
neither establishes correctness of this delta.

## Readiness and drift

The packet is source-review ready for the four requested hardening interactions,
with failed and stale evidence explicitly identified. Routine acceptance-ready
status is **no** until the retained-history upload failure is corrected or
explained and targeted selectors pass against the frozen `945401c3` source/tree.
After the v3 source capture, HEAD advanced to `38bcdb31` (`Backup resume
qualification logs`) without product-source changes; this is source drift after
capture. The newer retained-history log is explicitly bound to that later HEAD
and fails. Other newly available logs have no embedded source hash, so they are
reported as owner evidence only and do not silently qualify the frozen v3
source. The source had no tracked dirty files at capture; untracked review
artifacts and concurrent workspace outputs remain outside product scope.
Subsequent changes require a new correction delta and matching source-bound
evidence. The next batch should carry only that delta, prioritizing retained
remote history migration, multipart cleanup race/cancellation, customer-S3
restore identity, and bounded `external_id` validation.
