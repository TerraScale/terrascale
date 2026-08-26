# BK-02 Recovery Receipt: restore and recovery receipts (restore lane)

This is the tracked receipt of the BK-02 exclusive recovery-lane runs
(restore drills). BK-02 owns the lane for this wave: no other packet's
fault or restart runs executed concurrently. No secret, credential,
connection string, private host path, backup content, or customer data
appears here; document values below are synthetic drill payloads named
only by class, and all locations are throwaway test directories.

- Ticket: BK-02 (issue #43), part of the BK map (#41)
- Branch: `fleet/bk02-2`, base `develop` @ `c6359a9` (contains BK-01 with
  atomic publish and manifest v2)
- Module family: `src/backup/restore/**`, `tests/recovery/**`
- Storage format of every restored target: `TBV2FORMAT` version 1
  (verified by the durable open path on every reopen; the label is
  mirrored in `src/backup/restore/mod.rs` as `STORAGE_FORMAT_LABEL`)
- Recovery-point identity scheme: BK-01's manifest v2 content-derived
  SHA-256 identity (`terrabase-backup-manifest 2`); restore adds the
  rule that a selected point directory's name must equal the identity
  its sealed manifest claims (`BackupError::ManifestInvalid` otherwise)
- Test data class: synthetic in-process seeded documents — short string,
  integer, and single-field object values, 1–3 tenants (synthetic names
  `alpha`/`beta`), up to 5 documents and one reserved family record per
  drill, committed at `Synced` with one deliberate `Buffered` commit; no
  customer data of any kind

## Commands run (pinned offline flags)

From the worktree root, toolchain from `rust-toolchain.toml`,
`Cargo.lock` unchanged:

- `cargo fmt --all -- --check` — pass
- `cargo check --offline --locked` — pass
- `cargo clippy --offline --locked --all-targets -- -D warnings` — pass
- `cargo test --offline --locked` — pass (344 lib tests + integration
  targets, 0 failures; includes the 12 restore drills below and the 4
  public-seam restore tests)
- `cargo doc --workspace --no-deps --offline --locked` — pass; one
  pre-existing warning remains in `src/engine/mod.rs` (untouched by this
  packet, same as the BK-01 baseline)
- `bash scripts/check.sh` with `TERRABASE_V1_ROOT` pointing at the
  parent-supplied pinned read-only TerraBase V1 checkout and
  `TERRASCALE_WORKSPACE_ROOT` at the workspace root (both private host
  paths redacted) — all stages pass, including the strict V1 citation
  audit ("STRICT V1 QUALIFICATION PASSED") and the release-test stage
  (344 lib tests, all integration targets, 0 failures). Without
  `TERRASCALE_WORKSPACE_ROOT`, the audit's default workspace-root probe
  cannot resolve from a `.worktrees/*` checkout (it looks for siblings
  the worktree does not have) and the stage exits before examining any
  citation; the same override BK-01 used. `cargo-deny` is unavailable on
  this host and is recorded as unrun by the script itself; no global
  tool was installed

## What the lane ran (restore drills, in-crate `src/backup/restore/tests.rs`)

- `full_backup_restores_exact_values_counts_and_tenant_scopes`
- `incremental_chain_restore_replays_updates_and_deletes_in_publication_order`
- `snapshot_backup_restores_its_pinned_boundary_not_the_latest_state`
- `restore_selection_by_identity_restores_an_older_chain_depth_than_latest`
- `empty_source_full_backup_restores_an_empty_engine_location`
- `restore_refuses_a_non_empty_or_unrelated_target`
- `corrupted_and_truncated_members_fail_closed_and_leave_no_target`
- `a_missing_chain_link_fails_the_restore_before_any_target_exists`
- `a_digest_consistent_forged_archive_fails_the_content_checks`
- `a_failed_restore_leaves_no_target_and_the_retry_succeeds`
- `restart_after_restore_reopens_the_target_and_reads_the_same_values`
- `the_receipt_discloses_counts_duplicates_and_the_memory_bound`

Public-seam integration tests (`tests/recovery/restore_public.rs`):

- `empty_source_full_backup_restores_into_a_fresh_engine_location`
- `restore_refuses_a_non_empty_target_on_the_public_seam`
- `corrupt_recovery_points_fail_closed_with_typed_errors`
- `restore_selection_validates_the_named_identity`

## Fault injection performed, and the observed result

| Injected fault | Observed result (typed; no partial success) |
| --- | --- |
| Tampered member bytes (first byte flipped) | `BackupError::ChecksumMismatch`; no target directory created |
| Truncated member (one byte removed) | `BackupError::ChecksumMismatch`; no target directory created |
| Deleted member file | `BackupError::ChainIncomplete`; no target directory created |
| Deleted chain root directory (incremental chain) | `BackupError::ChainIncomplete`; no target directory created |
| Forged identity line on a sealed manifest | `BackupError::ManifestInvalid`; no target directory created |
| Digest-consistent garbage member frames, manifest and point directory honestly re-derived around them | `BackupError::ChecksumMismatch` at frame decode; no target directory created |
| Digest-consistent valid frames that disagree with the manifest's bound counts (one extra record) | `BackupError::ChecksumMismatch` at the count cross-check; no target directory created |
| Manifest claiming a different identity than its point directory | `BackupError::ManifestInvalid` (restore's named-point identity rule) |
| Malformed selection identity (empty, short, traversal, non-hex) | `BackupError::ManifestInvalid` before any directory lookup |
| Non-empty target (unrelated file, unrelated engine, path occupied by a file, prior completed restore) | `BackupError::TargetNotEmpty`, target bytes untouched |
| Interrupted/failed restore before publish (corrupt head link; pre-existing empty target) | No engine built into the target — the pre-created empty target stays exactly empty; only a staging sibling exists, and the retry succeeds into the same target |
| Stale staging sibling from a crashed run (torn `wal` bytes) | Ignored by the next run; retry succeeds |

There is no fault class in these drills whose result was a silent
partial success; every failure is one of the five typed categories
above and leaves no target directory.

## Restart-after-restore proof

- Every restore's verification step reopens the staged location through
  the durable storage open path (format verification and WAL recovery —
  the same path `Engine::open` runs) before the atomic publish.
- `restart_after_restore_reopens_the_target_and_reads_the_same_values`
  opens the published target as a real durable `Engine`, reads all
  values, shuts down, reopens, and reads the identical values again.
- The public-seam test
  `empty_source_full_backup_restores_into_a_fresh_engine_location`
  proves `terrabase_v2::Engine::open` + `shutdown` on the restored
  location for an external caller.

## Durability result and duplicate/missing handling

- Replay re-publishes through the storage publication seam (the seam the
  `Engine` transaction path lowers to) in batches of at most 256 writes
  at `LocalDurability::Synced`; the receipt records the commit count,
  the target's final sequence, and its durable root identity.
- Below-`Synced` source records (BK-01's durability disclosures) are
  restored **and disclosed**: the receipt's decoded disclosure count
  must equal the incremental manifest's bound disclosure count, and the
  count of disclosed records restored is carried as its own fact.
- Duplicate handling: a put onto an already-live key replays last-write-
  wins in publication order and is counted (`duplicate_put_replays`).
- Missing handling: a delete against an absent key (the source's no-op
  delete) replays and is counted (`missing_key_deletes`), never dropped.

## Memory bound, sized and disclosed (inheriting BK-01 repair F3)

- Inherited (backup side, uncapped): producing a full or snapshot
  recovery point materializes the largest selected tenant's complete
  live document set in memory; no bound caps that byte weight. Restore
  sizes it per run: the receipt records the largest tenant's archive
  byte weight, re-derived from the decoded frame lengths.
- Restore side (this module): members stream through a 65,536-byte read
  window; replay batches hold at most 256 writes; peak memory is the
  restored **live key set** — `(tenant, collection, identifier)` triples
  with no document values — plus one retained representative document
  per tenant. Restore never materializes a tenant's document byte
  weight.

Observed lane render (scenario: 3 documents + 1 family record across two
tenants, full root at boundary 4 plus one incremental link at boundary 5
containing one `Buffered` commit; identities are content-derived and
reproduce for the same data and commit order — the drill mirrors the
committed tests named above, from which every number re-derives):

```text
selected recovery point: 03984c0210335245a54b06326d92c05d8b28a074751b043c1f669fb54d55d1c6 (kind incremental, boundary 5)
chain depth: 2 (root first)
checksum results: 2 members re-digested, 264 archive bytes verified
expected counts (manifest-bound): documents=4 family_records=1 deletes=0 disclosures=1
decoded counts (replayed frames): documents=4 family_records=1 deletes=0 disclosures=1
restored live state: documents=4 family_records=1 tenants=2
  tenant 5:alpha documents=3 family_records=0
  tenant 4:beta documents=1 family_records=1
durability disclosure: 1 records below Synced restored as disclosed
publication: 2 commits at Synced, target sequence 2
verification: target reopened durably; 2 tenants, 5 documents read back, 2 representative values matched byte-exactly
memory bound: ... the largest tenant's archive byte weight in this run was 133 bytes
```

For this data class the inherited bound was 133 bytes (the largest
tenant's archive byte weight); it scales linearly with the largest
tenant's live byte weight and is uncapped on the backup side. The
committed assertion `member_bytes_verified == full.total_bytes +
link.total_bytes` pins the archive-byte accounting an inspector
recounts.

## Limits that remain

- Wrong-key restore: backups are not encrypted yet — encryption,
  compression, and the wrong-key fail-closed drill belong to BK-04
  (PB-6013); when it lands, the wrong-key drill must run in this lane
  and extend this receipt.
- Document versions and storage sequences are re-minted by the target's
  own publication order; values, keys, counts, tenant scopes, and
  durability disclosures are the reproduced facts. A client holding
  source-side versions across a restore must re-attach with
  `ExpectedVersion::Any`.
- Verification proves counts, live key sets, and one representative
  value per tenant through a fresh durable reopen; it is not a
  whole-value digest. (The committed data-bearing drills do compare
  every restored value against the source through a real `Engine`.)
- The replay writes through the storage publication seam; the Platform's
  schema-enforcement read on client writes does not run during restore
  (the records were publishable when the source published them, and
  storage re-checks the structural limits).
- `BackupError` granularity: a target that refuses a replayed batch
  (impossible-by-construction inputs) reports `ChecksumMismatch`; an
  unknown publication outcome reports `DestinationUnavailable`. Both
  fail closed with no target.
- Focused drills on temporary local directories with synthetic data; no
  multi-region, member-loss, or large-data-class scenario ran here, and
  no performance claim is made or implied.

## Shared-file note

`tests/recovery/main.rs` (BK-01's integration harness) gained exactly one
`mod restore_public;` line for this packet; no other shared file was
touched. `src/backup/mod.rs` was not modified: the public restore entries
are associated functions on the crate-root-re-exported
`terrabase_v2::RestorePlan`, implemented in `src/backup/restore/mod.rs`.
The integrator may re-export `RecoveryReceipt` from the crate root (the
`RestoreResult` declaration in `src/backup/mod.rs` remains the
placeholder to wire or drop at integration).
