# GAP-S1 Recovery Receipt: storage compaction (exclusive recovery lane)

This is the tracked receipt of the GAP-S1 exclusive recovery-lane runs
(compaction kill-point drills). GAP-S1 held the lane for this packet: no
other packet's fault or restart runs executed concurrently. No secret,
credential, connection string, private host path, backup content, or
customer data appears here; document values below are synthetic drill
payloads named only by class, and all locations are throwaway test
directories.

- Ticket: GAP-S1 (issue #81), closing the W3-1 residual (full-table
  roots, O(n) commits, no compaction)
- Branch: `fleet/gaps1-2`, base `develop` @ `8e4c4c5`
- Module family: `src/storage/**` (new `compaction.rs`, `retained.rs`,
  `compaction_tests.rs`; modified `file.rs`, `ledger.rs`, `root.rs`,
  `faults.rs`, `mod.rs`), `src/engine/mod.rs`,
  `src/replication/{retention,mod}.rs`,
  `tests/recovery/compaction_public.rs`
- Decision record: `docs/storage/gaps1-storage-compaction.md` (D1..D7)
- Storage format: `TBV2FORMAT` version 1, unchanged. GAP-S1 adds one
  file (`retained`) and shortens two existing artifacts under existing
  record kinds; a compacted and a never-compacted location open
  interchangeably (D7's "format changes named" section).
- Test data class: synthetic in-process seeded documents — one tenant,
  string values, up to 5 documents per drill (live `doc-1`/`doc-3`,
  deleted `doc-2` whose tombstone is the tracked evidence), committed at
  `Synced` with one deliberate `Buffered` commit; no customer data.

## Commands run (pinned offline flags)

From the worktree root, toolchain from `rust-toolchain.toml`,
`Cargo.lock` unchanged:

- `cargo fmt --all` — applied; `git diff` reviewed
- `cargo +1.97.1 clippy --offline --locked --all-targets -- -D warnings`
  — pass, zero warnings
- `cargo test --offline --locked` — pass (664 lib tests + all
  integration targets, 0 failures; includes the 10 compaction drills and
  the 5 public-seam tests below)
- `cargo test --workspace --offline --locked` — pass (every crate and
  integration target, 0 failures)
- `bash scripts/check.sh` with `env -u RUSTUP_TOOLCHAIN`,
  `TERRABASE_V1_ROOT` pointing at the parent-supplied pinned read-only
  TerraBase V1 checkout and `TERRASCALE_WORKSPACE_ROOT` at the workspace
  root (both private host paths redacted) — exit 0, all repository
  stages pass: fmt, check, release tests (664 lib tests and every
  integration target, including the compaction kill-point matrix, 0
  failures), clippy with warnings denied, doc build, parity inventory
  (258 items, schema 1.0), tooling tests, license metadata, secret scan,
  and the strict V1 citation audit ("STRICT V1 QUALIFICATION PASSED").
  `cargo-deny` is unavailable on this host and is recorded as unrun by
  the script itself; no global tool was installed.

## What the lane ran: the kill-point matrix (decision record D4)

In-crate drills at `src/storage/compaction_tests.rs` — each kill drill
arms exactly one injected fault inside the pass, drops the storage
without flush or shutdown (the process-kill model this engine's
durability is observed through), reopens, and asserts the no-loss
contract: both live documents readable, the deleted one absent, and
every seeded operation resolving as committed.

`kill_points_inside_the_compaction_sequence_lose_nothing` drives all
five fault points of the D4 table:

| Fault point | State the reopen proves safe |
| --- | --- |
| `CompactionCheckpointPublish` | Root file written and synced, manifest still names the older root; nothing lost, retry converges |
| `CompactionWalTruncate` | Checkpoint synced, WAL full — ordinary pre-compaction state |
| `CompactionEvidenceSync` | Store appended but unsynced (torn tail): truncated to the verified prefix at open, journal still serves |
| `CompactionJournalRewrite` | Evidence and bound durable, journal still serves the window — redundant-but-consistent, retry dedups |
| `CompactionRootReclaim` | All roots present; deletion of strictly-older roots is the last phase and never removes the manifest root |

Every kill state also satisfies the coverage invariant asserted in the
drill: whenever the journal stops serving a sequence, the durable
retained-evidence store holds the window's delete facts and a bound at
or above the given-up window. Each drill then retries the pass and
proves convergence to `Reclaimed { bound }` (idempotency; the
durable-bound exemption in the divergence gate is what makes the
post-rewrite retry converge — decision record D5).

## What the lane ran: gates, damage, recovery, identity

- `reclaim_pass_truncates_the_wal_and_removes_superseded_roots` — the
  `Reclaim` policy: checkpoint at the published sequence, WAL truncated
  to zero bytes, superseded roots removed (zero unremovable), journal
  untouched; post-compaction commits and a further reopen survive.
- `history_reclaim_gives_up_the_window_and_holds_the_evidence_durally` —
  the `ReclaimHistory` policy: window `[1, head)` given up, head record
  stays servable, `journaled_high_water` never regresses, the tombstone
  fact and the window's newest non-deletion sequence are durable in the
  store after a reopen.
- `history_reclaim_refuses_while_evidence_below_the_bound_is_diverged` —
  the divergence gate: one published-but-unjournaled commit (repair F2's
  divergence shape) refuses history reclaim with a disclosed count while
  the checkpoint/WAL/root phases still run and the workload survives.
- `history_reclaim_honors_the_retention_floor` — both sides: a floor
  below the tombstone retains the fact durably; a floor above it skips
  the fact with a disclosed count, writes nothing below-floor durably,
  and still declares only the bound it holds.
- `a_torn_retained_store_tail_is_truncated_and_midfile_damage_fails_closed`
  — the store's damage discipline (D3): trailing garbage is an
  interrupted append (truncated at open so later appends stay
  scannable); a checksum-flipped byte mid-file fails the open with
  `FormatMismatch` rather than serving a prefix of the evidence a
  declared bound rests on.
- `a_truncated_wal_with_a_corrupt_manifest_recovers_from_the_newest_root`
  — the D7 recovery drill: after a `Reclaim` pass (WAL empty), a corrupt
  manifest no longer has WAL frames to rebuild from, and recovery bases
  the rebuild on the highest-sequence root file that verifies (checksum,
  body, and file-name identity all checked).
- `a_checkpoint_makes_a_buffered_tail_durable_and_resolvable` — the D6
  disclosed consequence: a checkpointed `Buffered` commit's data survives
  the process exit and its `Ready` record resolves `Committed` after the
  reopen (at the requested level; `resolution_for` caps every claim at
  what the operation asked for).
- `replay_identity_survives_compaction` — the ledger is never rewritten:
  a same-identity same-write-set retry replays the recorded resolution
  without republishing, and a different write set under the same identity
  is still `OperationIdReuse`.
- `a_killed_pass_between_the_store_and_the_journal_is_idempotent_on_retry`
  — the redundant-state convergence: the retry re-appends nothing
  (identity dedup) and lands exactly one durable fact.

## What the lane ran: public-seam tests (`tests/recovery/compaction_public.rs`)

- `a_reclaim_pass_reports_bounded_facts_and_progress_publicly` — the
  typed `CompactionRequest` with a progress sink on a public `Engine`:
  at most one start and one completion event per phase, phase list
  bounded by the fixed phase set, compacted location reopens and shuts
  down cleanly.
- `history_reclaim_on_an_unservable_journal_refuses_and_discloses` — the
  readable-window gate (D5) on a never-journaled engine:
  `RefusedUnservableWindow`, disclosed, safe phases still run.
- `a_watermark_carrying_pass_leaves_the_handle_fail_closed_and_repeatable`
  — `Engine::compact_with_retention` with the public
  `SharedTombstoneRetention` handle: no durable state seeds nothing,
  declares nothing, the watermark stays observably untouched; repeatable
  on the same location and after a restart.
- `compaction_composes_with_the_public_backup_and_restore_seam` — the
  compacted engine stays a legal backup source (full then incremental,
  chain verifies two points, restore into a fresh target reopens as a
  real engine location, no path crosses the receipt seam).
- `the_compaction_error_surface_is_typed` — the error surface is
  `CompactionError::Storage`, never a panic or partial receipt (the
  injected-failure path is exercised by the in-crate kill drills).

## Servable-history interaction (ticket criterion)

Documented in the decision record D5 and observed by the drills: the
effective bound for a consumer composes as
`max(engine servable_history_start, watermark declared bound)`; a pass
raises the first term physically (the rewritten journal opens with a
high-water mark carrying the pre-pass head, so `journaled_high_water`
never regresses) and the second by declaration with
`durably_held = true` backed by the synced store — the GAP-R02 D2.3
assertion made honest by this packet's durable retained-evidence store,
driven in production by `Engine::compact_with_retention`'s prologue
(restart re-derivation) and declaration.

## No-loss statement

Across the matrix above, no drill observed a lost document, a lost
resolution, a regressed durable high-water mark, or a below-bound
consumer window the retained evidence did not cover. Durability is
observed through process exit (drop without flush) and reopen only — no
power-loss simulation; single-Engine-per-location and no lock file are
unchanged W3-1 assumptions, named in the decision record's Limits.

## No performance claim

Compaction facts in the receipt and tests are counts, byte lengths, and
sequences observed by tests — never speed statements
(PD-2026-08-26-03; any such claim needs the exclusive benchmark lane
and the TerraBase.Benchmark statistical gates).
