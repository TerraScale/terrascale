# Recovery Receipt: crash, kill-point, and restart drills (native V2)

This is the tracked receipt of the crash/restart view of the native V2
recovery-lane runs of 2026-08-29 (regenerated after the legacy parity
scrub `8f697ba` deleted the prior receipt family; nothing here is
carried over from the deleted records). It groups, by interruption
class, the crash-facing drills of the same exclusive window recorded
per-lane in `bk02-restore-recovery-receipt.md` and
`gaps1-compaction-recovery-receipt.md` — the successor of the crash
coverage the deleted `gaps1-compaction-recovery-receipt.md` and
`ix03-crash-during-replacement.md` carried. The exclusive
recovery-lane discipline applied: no other packet's fault, restart, or
recovery runs executed concurrently (the in-flight replication-core
test cycle was observed to completion first; the process table was
checked before each command). No secret, credential, connection string,
private host path, backup content, or customer data appears here;
document values are synthetic drill payloads named only by class, and
all locations are throwaway directories under the system temp
directory.

- Regeneration date: 2026-08-29
- Source: `develop` @ `01c9abc`, the one normal checkout (no worktrees,
  ADR 0015), with concurrent uncommitted edits owned by other lanes
  present (`crates/terrabase-replication-core/**`, `src/query/**`,
  `Cargo.lock`); none belong to this packet and none touch these
  drills' sources.
- Module family: `src/storage/compaction_tests.rs` (kill-point and
  damaged-file drills), `tests/recovery/backup_public_construction.rs`
  (interrupted-backup remnant), `tests/recovery/restore_public.rs` and
  `src/backup/restore/tests.rs` (fail-closed corruption and
  restart-after-restore).
- Storage format: `TBV2FORMAT 1`, unchanged by any packet in this
  window.
- Test data class: synthetic in-process seeded documents — one tenant
  (`tenant-a`-class), string values, up to 5 documents per drill (live
  `doc-1`/`doc-3`, deleted `doc-2` whose tombstone is the tracked
  evidence), committed at `Synced` with one deliberate `Buffered`
  commit; no customer data.

## Commands run (pinned offline flags)

Focused per the AGENTS.md check discipline (no full-repo gate run
locally; CI owns `scripts/check.sh`):

- `cargo test --offline --locked --lib storage::compaction_tests` —
  **11 passed, 0 failed** (1054 filtered out).
- `cargo test --offline --locked --test recovery` — **18 passed, 0
  failed** (the crash-facing subset is listed below).
- `cargo test --offline --locked --lib backup::restore` — **14 passed,
  0 failed** (the crash-facing subset is listed below).

## Kill-point drill class (compaction sequence)

`storage::compaction_tests::kill_points_inside_the_compaction_sequence_lose_nothing`
arms each of the five sequence kill points in turn —
`FaultPoint::CompactionCheckpointPublish`,
`CompactionWalTruncate`,
`CompactionEvidenceSync`,
`CompactionJournalRewrite`,
`CompactionRootReclaim` — lets the pass abort, and drops the storage
without flush or shutdown (the process-death analog). Observed at every
kill point:

- the killed location reopens;
- no document is lost (`assert_no_loss`);
- whenever the journal no longer serves a sequence, the retained
  evidence store covers the window's deletes, and the durable bound
  names the given-up window (the coverage invariant);
- a retry pass completes and converges to
  `HistoryReclaimOutcome::Reclaimed { bound: sequence 4 }`.

`a_killed_pass_between_the_store_and_the_journal_is_idempotent_on_retry`
kills at `CompactionJournalRewrite` specifically and proves the
redundant intermediate state: evidence and bound already durable, the
journal still serving the window; the retry completes without
re-appending the durable fact (`evidence_retained() == 0`, no duplicate
facts).

## Damaged-file drill class

- `a_torn_retained_store_tail_is_truncated_and_midfile_damage_fails_closed`:
  trailing garbage from an interrupted append — the open succeeds on
  the verified prefix and truncates the tail so later appends stay
  scannable; mid-file damage to retained evidence fails the open
  (`StorageOpenError::FormatMismatch`) rather than silently serving a
  prefix.
- `an_impossible_declared_length_fails_the_open_and_a_legal_torn_tail_still_truncates`:
  a declared record length past end-of-file fails the open typed; a
  legal torn tail still truncates to the verified prefix.
- `a_truncated_wal_with_a_corrupt_manifest_recovers_from_the_newest_root`:
  a truncated WAL with a corrupt manifest re-derives from the newest
  intact root.
- `a_checkpoint_makes_a_buffered_tail_durable_and_resolvable` and
  `replay_identity_survives_compaction`: a checkpoint makes the
  deliberate `Buffered` tail durable and resolvable, and replay
  identity is preserved across a completed pass.

## Interrupted-backup and fail-closed restore drill class

- `backup_public_construction::unsealed_orphan_directories_do_not_break_discovery_or_retry`:
  a killed backup attempt's staged remnant (no sealed manifest) neither
  breaks discovery of the sealed point nor blocks a retry.
- `backup_public_construction::concurrent_backup_attempts_on_the_public_seam_reject_the_second`:
  the second of two concurrent attempts is rejected typed.
- `restore_public::corrupt_recovery_points_fail_closed_with_typed_errors`,
  `backup::restore::tests::corrupted_and_truncated_members_fail_closed_and_leave_no_target`,
  `a_missing_chain_link_fails_the_restore_before_any_target_exists`,
  `a_digest_consistent_forged_archive_fails_the_content_checks`:
  corruption, truncation, a missing chain link, and a forged archive
  all fail closed and leave no target.
- `backup::restore::tests::a_failed_restore_leaves_no_target_and_the_retry_succeeds`
  and `restart_after_restore_reopens_the_target_and_reads_the_same_values`:
  restart semantics — no residue from the failed attempt, the retry
  verifies, and a reopened restored target reads back the same values.
- `compaction_public::a_watermark_carrying_pass_leaves_the_handle_fail_closed_and_repeatable`
  (same `--test recovery` run): a pass that carried a watermark into a
  fail-closed handle shape stays repeatable rather than wedging.

## Honest limits

- The kill points are armed test faults inside the compaction sequence,
  not OS-level `SIGKILL` timing races; the process-death analog is the
  unflushed drop.
- Dev (unoptimized) profile with `debug_assert!` contracts active; no
  release run claimed.
- Single host, in-process; no multi-Member restart or Region-loss drill
  is claimed here (the leaderless simulator lanes own those).
- No performance claim of any kind.
