# Astra backup hardening v4 — 2026-09-09

Correction supplement to [v3](../backup-hardening-20260909-v3/index.md).
Review input only; no correctness approval is asserted. This packet covers the
latest remote migration probe, thread-local publication-fault seam,
customer-BYOK provider restriction, and the current dirty overlay.

## Source and complete input

The requested source correction commit is `6c4cadec92b7590d77f9a77c9c40f1f1c90ca045`.
The authored correction since v3's `945401c3` capture is
[authored-945401c3-6c4cadec.diff](./diffs/authored-945401c3-6c4cadec.diff).
The current dirty five-file overlay is
[dirty-overlay-6c4cadec.diff](./diffs/dirty-overlay-6c4cadec.diff).
A later concurrent edit changed `tests/encryption/main.rs`; its additional
one-line overlay is
[dirty-after-capture-tests-encryption.diff](./diffs/dirty-after-capture-tests-encryption.diff)
and its current copy is included in `source-current/`.
Full HEAD and dirty source copies are in `source-head/` and `source-current/`;
their commit/tree/status and hashes are in [source-identity.txt](./source-identity.txt).
[SHA256SUMS](./SHA256SUMS) covers diffs, source copies, and evidence. Both
manifest checks passed twice. The overlays were captured as-is and must be
treated as a moving source boundary until the owner freezes them.

| Correction | Exact source scope |
|---|---|
| Historical remote migration | `src/backup/worker.rs:1409-1490,2024+,3919+`; migration now probes historical destinations before selecting/repairing remote history and adds the retained-history regression path. |
| Publication fault seam | `src/backup/restore/mod.rs:1806-1813,2736+`; publication fault injection is thread-local, with cleanup/recovery assertions in the publication tests. |
| Customer BYOK restriction | `crates/terrabase-server/src/config.rs:1134,2332-2337`; config/runtime reject customer BYOK providers other than implemented `aws-kms`. |
| Runtime/config wiring | `crates/terrabase-server/src/production_data_runtime.rs:824-829,1124`; `src/backup/worker/remote.rs` new/updated remote migration helpers. |

The relevant runtime chain is production config validation → API/runtime
restore admission → `NativeBackupFleet` remote resolution/migration → restore
publication and cancellation cleanup. Review remote probing against provider
identity, historical destination coverage, and restart state; review the
thread-local fault seam for test isolation and production noninterference; and
review customer BYOK validation through destination/key construction.

## Evidence

Evidence files are copied under `evidence/` with their original bytes:

- `core.log` is bound to HEAD `945401c3` but has no usable tree capture due an
  index-lock failure; it reports 104 passed, 1 failed, 1 ignored, with the
  retained-history upload failure.
- `customer-s3.log` and `reconciliation.log` each report 1 passed, but contain
  no source identity.
- `object_store.log` reports 13 passed, 1 ignored (MinIO unavailable).
- `publication.log` reports **2 passed, 2 failed**; publication exchange
  failures remain unresolved in that evidence.
- `workload.log` reports 3 passed, no failures, but no source identity.
- `retained-history.log` reports **1 failed**, bound to later HEAD `38bcdb31`
  and tree `ec69ae98`; it is historical relative to this correction.
- `fmt.log` is empty and records no result.

No Cargo or build was run by preparation. These logs are pending/stale/failed
according to their identities; none is silently promoted to proof for the
current `6c4cadec` HEAD or dirty overlay. The packet therefore has no current
passing targeted evidence for the migration or publication corrections.

## Verdict and drift

Source review is ready with the exact authored and dirty deltas. Routine
acceptance readiness is **no**: current product files are dirty at capture,
the retained-history and publication evidence includes failures, and exact
source-bound reruns are required. The source drift after v3 is material and is
recorded in `source-identity.txt`; any further worker commit or dirty edit
requires a new delta packet. Next batch should use a frozen tree and target
remote historical migration, publication success/failure cleanup, and the
AWS-KMS-only customer-BYOK boundary with exact command/exit/source hashes.
