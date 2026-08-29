# BK-02 Recovery Receipt: backup construction, schedule chains, restore (native V2 regeneration)

This is the tracked receipt of the native V2 recovery-lane runs for the
backup-construction, schedule-chain, and restore lanes, regenerated on
2026-08-29. The prior receipt family under `docs/evidence/recovery/`
was deleted by the legacy parity scrub `8f697ba`; this file regenerates
the record at the same path (`bk02-restore-recovery-receipt.md`) from
fresh runs of the surviving lanes — no row here is carried over from the
deleted receipts. The exclusive recovery-lane discipline applied to the
runs: no other packet's fault, restart, or recovery runs executed
concurrently (the in-flight replication-core test cycle was observed to
completion before these runs started, and the process table was checked
before each command). No secret, credential, connection string, private
host path, backup content, or customer data appears here; document
values below are synthetic drill payloads named only by class, and all
locations are throwaway directories under the system temp directory.

- Regeneration date: 2026-08-29
- Source: `develop` @ `01c9abc` ("Bind and bound backup recovery
  manifests"), the one normal checkout (no worktrees, per ADR 0015).
  The shared checkout contained concurrent uncommitted edits owned by
  other lanes (`crates/terrabase-replication-core/**`, `src/query/**`,
  `Cargo.lock`); this packet modified none of them and none of this
  receipt's lanes' sources, and the runs reflect the checkout as it
  stood.
- Module family: `tests/recovery/backup_public_construction.rs`,
  `tests/recovery/restore_public.rs`,
  `tests/recovery/schedule_public_chain.rs` (one integration target,
  `tests/recovery/main.rs`), and the in-crate suite
  `src/backup/restore/tests.rs`.
- Storage format of every restored target: `TBV2FORMAT 1` (the label
  mirrored in `src/backup/restore/mod.rs` as `STORAGE_FORMAT_LABEL`),
  verified by the durable open path on every reopen.
- Recovery-point identity scheme: manifest v3
  (`terrabase-backup-manifest 3`, `src/backup/manifest.rs`) — the
  identity is the SHA-256 digest of every manifest line except the
  `identity` line itself, so it is content-derived; the manifest binds
  the ordered archive member names, lengths, and SHA-256 digests. This
  is the scheme landed at `01c9abc`, one version past the manifest v2
  the deleted receipt described.
- Test data class: synthetic in-process seeded documents — short string,
  integer, and single-field object values, synthetic tenants
  (`tenant-a`-class names, 1–3 per drill), up to 5 documents and one
  reserved family record per drill, committed at `Synced` with one
  deliberate `Buffered` commit; no customer data of any kind.

## Commands run (pinned offline flags)

From the repository root, toolchain from `rust-toolchain.toml`, focused
per the AGENTS.md check discipline (no full-repo gate was run locally;
CI owns `scripts/check.sh`):

- `cargo test --offline --locked --test recovery` — **18 passed, 0
  failed** (all five modules of the target; the compaction and
  tombstone modules of the same run are recorded in
  `gaps1-compaction-recovery-receipt.md`).
- `cargo test --offline --locked --lib backup::restore` — **14 passed,
  0 failed** (1051 filtered out).

## What the lane ran

Public-seam construction (`tests/recovery/backup_public_construction.rs`):

- `empty_source_full_backup_creates_a_verifiable_recovery_point`
- `empty_source_incremental_fails_closed_with_a_typed_error`
- `manifest_validation_rejects_path_traversal_and_in_progress_manifests`
- `unsealed_orphan_directories_do_not_break_discovery_or_retry`
- `concurrent_backup_attempts_on_the_public_seam_reject_the_second`

Public-seam restore (`tests/recovery/restore_public.rs`):

- `empty_source_full_backup_restores_into_a_fresh_engine_location`
- `restore_refuses_a_non_empty_target_on_the_public_seam`
- `corrupt_recovery_points_fail_closed_with_typed_errors`
- `restore_selection_validates_the_named_identity`

Public-seam schedule chains (`tests/recovery/schedule_public_chain.rs`):

- `a_first_full_anchors_and_cadence_links_append_idempotently`
- `an_unanchored_cadence_link_fails_typed_and_leaves_no_point`

In-crate data-bearing drills (`src/backup/restore/tests.rs`, all 14):
`full_backup_restores_exact_values_counts_and_tenant_scopes`,
`incremental_chain_restore_replays_updates_and_deletes_in_publication_order`,
`a_later_chain_delete_of_the_sample_document_still_verifies_green`,
`one_identifier_reused_across_many_collections_restores_and_verifies`,
`snapshot_backup_restores_its_pinned_boundary_not_the_latest_state`,
`restore_selection_by_identity_restores_an_older_chain_depth_than_latest`,
`empty_source_full_backup_restores_an_empty_engine_location`,
`restore_refuses_a_non_empty_or_unrelated_target`,
`corrupted_and_truncated_members_fail_closed_and_leave_no_target`,
`a_missing_chain_link_fails_the_restore_before_any_target_exists`,
`a_digest_consistent_forged_archive_fails_the_content_checks`,
`a_failed_restore_leaves_no_target_and_the_retry_succeeds`,
`restart_after_restore_reopens_the_target_and_reads_the_same_values`,
`the_receipt_discloses_counts_duplicates_and_the_memory_bound`.

## Fault injection and observed results

- Unsealed orphan directory (a killed backup attempt's remnant: a
  staged member with no sealed `manifest.tbv2m`) present in the
  destination: discovery and retry still succeed; the orphan neither
  breaks the sealed point's verification nor is mistaken for one.
- A second concurrent backup attempt on the public seam: rejected with
  a typed error; the first attempt's point stands.
- Path-traversal member names and in-progress (unsealed) manifests in a
  crafted archive: manifest validation rejects both, typed.
- Corrupted and truncated archive members, a missing chain link, and a
  digest-consistent forged archive: every restore fails closed with
  typed errors and leaves no target directory behind.
- Non-empty or unrelated restore target: refused before any write.
- Failed restore followed by retry: no target residue; the retry
  succeeds and verifies.
- Restart after restore: reopening the target through a real `Engine`
  reads back the same values, counts, and tenant scopes.

## Honest limits

- Dev (unoptimized) test profile with `debug_assert!` contracts active;
  no release-profile run is claimed here.
- Single host, in-process drills; `TenantScope` has no public
  constructor by design, so the data-bearing restore drills run
  in-crate — the public seam is covered by the four
  `tests/recovery/restore_public.rs` tests above.
- These are correctness drills only; no performance claim is made
  (any such claim needs the benchmark lane).
- Crash/restart and compaction/tombstone drills of the same exclusive
  window are recorded in the sibling receipts
  `crash-restart-recovery-receipt.md` and
  `gaps1-compaction-recovery-receipt.md`.
