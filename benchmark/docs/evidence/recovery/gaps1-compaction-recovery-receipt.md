# GAP-S1 Recovery Receipt: storage compaction and tombstone retention (native V2 regeneration)

Regeneration of the compaction/tombstone recovery-lane record at the
path the accepted design (`docs/storage/gaps1-storage-compaction.md`)
already cites. The legacy parity scrub `8f697ba` deleted the prior
receipt; every row below is from fresh runs of the surviving lanes on
2026-08-29 — nothing is carried over from the deleted record. The
exclusive recovery-lane discipline applied to the runs: no other
packet's fault, restart, or recovery runs executed concurrently (the
in-flight replication-core test cycle was observed to completion before
these runs started, and the process table was checked before each
command). No secret, credential, connection string, private host path,
backup content, or customer data appears here; document values are
synthetic drill payloads named only by class, and all locations are
throwaway directories under the system temp directory.

- Regeneration date: 2026-08-29
- Source: `develop` @ `01c9abc` ("Bind and bound backup recovery
  manifests"), the one normal checkout (no worktrees, ADR 0015), with
  concurrent uncommitted edits owned by other lanes present
  (`crates/terrabase-replication-core/**`, `src/query/**`,
  `Cargo.lock`); none belong to this packet and none touch these
  drills' sources.
- Module family: `src/storage/compaction_tests.rs` (kill-point drills
  — their per-drill crash detail is expanded in
  `crash-restart-recovery-receipt.md`),
  `tests/recovery/compaction_public.rs`,
  `tests/recovery/tombstone_retention_public.rs`.
- Decision record: `docs/storage/gaps1-storage-compaction.md` (D1..D7),
  unchanged design; the receipt path it cites at its "Recovery Receipt
  (exclusive lane)" row is this file, restored.
- Storage format: `TBV2FORMAT 1`. The retained-evidence store file
  (`retained`) and the shortened artifacts remain as the design
  records; a compacted and a never-compacted location open
  interchangeably (D7).
- Test data class: synthetic in-process seeded documents — one tenant
  (`tenant-a`-class), string values, up to 5 documents per drill (live
  `doc-1`/`doc-3`, deleted `doc-2` whose tombstone is the tracked
  evidence), committed at `Synced` with one deliberate `Buffered`
  commit; no customer data.

## Commands run (pinned offline flags)

Focused per the AGENTS.md check discipline (no full-repo gate run
locally; CI owns `scripts/check.sh`):

- `cargo test --offline --locked --test recovery` — **18 passed, 0
  failed**; the compaction and tombstone-retention modules of that run
  are this receipt's public-seam rows.
- `cargo test --offline --locked --lib storage::compaction_tests` —
  **11 passed, 0 failed** (1054 filtered out).

## What the lane ran

Public seam (`tests/recovery/compaction_public.rs`, all 5):

- `a_reclaim_pass_reports_bounded_facts_and_progress_publicly`
- `history_reclaim_on_an_unservable_journal_refuses_and_discloses`
- `a_watermark_carrying_pass_leaves_the_handle_fail_closed_and_repeatable`
- `compaction_composes_with_the_public_backup_and_restore_seam`
- `a_healthy_pass_returns_a_typed_success_receipt`

Public seam (`tests/recovery/tombstone_retention_public.rs`, both):

- `the_watermark_contract_is_public_and_fail_closed`
- `a_backup_plan_carries_the_shared_watermark_on_the_public_seam`

In-crate drills (`src/storage/compaction_tests.rs`, all 11):
`reclaim_pass_truncates_the_wal_and_removes_superseded_roots`,
`history_reclaim_gives_up_the_window_and_holds_the_evidence_durally`,
`kill_points_inside_the_compaction_sequence_lose_nothing`,
`history_reclaim_refuses_while_evidence_below_the_bound_is_diverged`,
`history_reclaim_honors_the_retention_floor`,
`a_torn_retained_store_tail_is_truncated_and_midfile_damage_fails_closed`,
`an_impossible_declared_length_fails_the_open_and_a_legal_torn_tail_still_truncates`,
`a_truncated_wal_with_a_corrupt_manifest_recovers_from_the_newest_root`,
`a_checkpoint_makes_a_buffered_tail_durable_and_resolvable`,
`replay_identity_survives_compaction`,
`a_killed_pass_between_the_store_and_the_journal_is_idempotent_on_retry`.

## Per-drill-class mutation-kill record

- **Kill points in the compaction sequence** (five armed faults:
  checkpoint publish, WAL truncate, evidence sync, journal rewrite,
  root reclaim): every kill aborts the pass; the location reopens with
  no loss; the retained-evidence coverage invariant holds (the delete
  the journal gave up is covered and the bound names the window); a
  retry converges to `Reclaimed { bound: sequence 4 }`. The
  journal-rewrite kill specifically leaves the redundant intermediate
  state (durable evidence + still-serving journal) and the retry
  re-appends nothing — no duplicate facts.
- **Torn retained-store tail** (trailing garbage from an interrupted
  append): opens on the verified prefix, truncates the tail; mid-file
  damage fails the open typed (`FormatMismatch`).
- **Declared-length overrun**: an impossible record length past
  end-of-file fails the open; a legal torn tail still truncates.
- **Truncated WAL with corrupt manifest**: re-derives from the newest
  intact root.
- **Unservable window / diverged evidence below the bound / retention
  floor**: each refusal gate holds typed and discloses its reason; the
  floor bounds what any pass may give up.
- **Watermark contract**: public and fail-closed; a backup plan carries
  the shared watermark on the public seam (the one stable-frontier
  contract across snapshots, compaction, PITR, and tombstone
  collection).

## Honest limits

- The public-seam compaction tests report the typed request/response
  and composition surface; the data-bearing shapes are the in-crate
  drills above (`TenantScope` has no public constructor by design).
- Dev (unoptimized) profile with `debug_assert!` contracts active; no
  release run claimed.
- Compaction facts are counts and byte lengths observed by tests, never
  speed statements (PD-2026-08-26-03; any such claim needs the
  benchmark lane).
- The crash-expanded view of the kill-point drills (process-death
  analog, restart, retry semantics) is recorded in
  `crash-restart-recovery-receipt.md` from the same exclusive window.
