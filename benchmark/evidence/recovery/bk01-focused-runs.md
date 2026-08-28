# BK-01 focused-run receipt: backup core

Focused tests only. No recovery-lane claim: crash/kill-point restore
drills, Recovery Receipts, and durability qualification belong to the
exclusive recovery lane, which is BK-02's, not this record. No secret,
credential, customer data, backup content, or private host path appears
here.

- Ticket: BK-01 (issue #42)
- Branch: `fleet/bk01`, base `develop` @ `431841a`
- Module family: `src/backup/**`, `tests/recovery/**`
- Salvage input: `dc43f29` in-scope paths (`src/backup/{records,sha256,tests}.rs`,
  `tests/recovery/{main,backup_public_construction}.rs`), re-verified and
  finished here; nothing from that branch was taken on trust.
- Repair loop 1: warden findings on `5cba015` fixed in this tree; see the
  last section of this record.

## Commands run (pinned offline flags)

From the worktree root, toolchain from `rust-toolchain.toml`,
`Cargo.lock` unchanged:

- `cargo fmt --all -- --check` — pass
- `cargo check --offline --locked` — pass
- `cargo clippy --offline --locked --all-targets -- -D warnings` — pass
- `cargo test --offline --locked` — pass (250 lib tests + 10 integration
  targets, 0 failures; includes the 21 backup tests below)
- `cargo doc --workspace --no-deps --offline --locked` — pass (one
  pre-existing warning in `src/engine/mod.rs`, untouched by this packet)
- `bash scripts/check.sh` with
  `TERRABASE_V1_ROOT=../TerraBase` and
  `TERRASCALE_WORKSPACE_ROOT=..` — all repository stages pass (fmt,
  check, release tests, clippy, doc, parity inventory, tooling tests,
  license, secret scan) except the V1 citation audit, which fails before
  examining any citation because the shared pinned V1 checkout carries
  local modifications. The identical failure reproduces from the
  untouched main checkout at `develop` @ `431841a`, so it is
  environmental, not caused by this packet; recorded as unrun-in-effect.

## Focused behavior proved (through the public `backup` seam)

In-crate suite (`src/backup/tests.rs`, 23 tests):

- `full_backup_writes_a_manifest_with_ordered_digests_and_counts`
- `backup_includes_family_records_from_the_reserved_collection`
- `backup_progress_reports_skipped_items_without_hiding_loss`
- `backup_cancellation_leaves_no_usable_partial_recovery_point`
- `backup_admits_one_job_and_rejects_a_concurrent_attempt_with_a_typed_error`
- `backup_throttling_limits_reads_without_reordering_or_dropping_records`
- `manifest_validation_rejects_path_traversal_and_in_progress_manifests`
- `incremental_backup_appends_to_its_base_chain_with_the_journal_range_boundary`
- `incremental_chain_verification_rejects_a_missing_or_reordered_link`
- `tampered_member_bytes_fail_verification_with_checksum_mismatch`
- `truncated_and_missing_members_fail_verification_with_typed_errors`
- `incremental_with_an_unservable_base_fails_closed`
- `snapshot_backup_pins_its_boundary_and_stays_self_contained`
- `backup_preserves_tenant_scopes_in_per_tenant_counts`
- `repeated_backup_at_the_same_boundary_is_idempotent`
- `a_torn_publish_orphan_is_repaired_by_the_identical_retry` (repair 1)
- `discovery_ignores_unsealed_orphan_directories` (repair 1)
- `tenants_with_interior_whitespace_round_trip_through_the_manifest`
  (repair 1)
- `record_frames_bind_the_commit_durability_into_the_archive_digest`
  (repair 1)
- `progress_disclosures_are_bounded_while_the_total_stays_exact`
  (repair 1)
- three SHA-256 known-answer/streaming tests (`src/backup/sha256.rs`)

Integration suite (`tests/recovery/`, 5 tests, public construction
only, no `TenantScope` fixture):

- `empty_source_full_backup_creates_a_verifiable_recovery_point`
- `empty_source_incremental_fails_closed_with_a_typed_error`
- `manifest_validation_rejects_path_traversal_and_in_progress_manifests`
- `concurrent_backup_attempts_on_the_public_seam_reject_the_second`
- `unsealed_orphan_directories_do_not_break_discovery_or_retry`
  (repair 1)

## Facts the focused runs assert

- Full, incremental, and snapshot runs seal one recovery point
  `rp-<identity>` whose manifest binds ordered member names, lengths,
  and SHA-256 digests; per-kind and per-tenant counts; and a
  content-derived identity that is stable across destinations and
  idempotent on retry.
- Incremental chains append: each link names its base boundary, the base
  must be a sealed recovery point in the same destination, and
  `verify_latest_chain` walks the complete ordered chain and re-digests
  every bound member.
- Snapshot mode: a pinned-boundary, self-contained recovery point with
  no base link (contract stated in `src/backup/mod.rs`).
- Corruption and truncation fail closed with typed errors:
  tampered member bytes `ChecksumMismatch`, truncated member
  `ChecksumMismatch`, missing member `ChainIncomplete`, missing chain
  link `ChainIncomplete`, traversal/in-progress/forged-identity
  manifests `ManifestInvalid`.
- A `Buffered` commit in an incremental range is archived and disclosed
  (`DurabilityBelowSynced`), never dropped and never presented as fully
  durable; a full or snapshot manifest carries state, not history, and
  records no disclosures.
- Admission rejects a concurrent backup of the same source with
  `Throttled`; a zero read bound rejects before any read; cancellation
  leaves no `rp-` directory and no loadable recovery point.

## Limits

- Focused, single-process runs on temporary local directories. No
  crash, kill-point, restart, or restore drill ran here; no Recovery
  Receipt was produced; the exclusive recovery lane is BK-02's.
- The source read path is the durable publication history (W4-CA-4) only:
  a source whose journal is absent or truncated below a requested range
  fails closed (`SourceUnavailable`/`ChainIncomplete`) or binds the
  smaller provable boundary; a scope-carrying snapshot read path is a
  later disposition (the engine identity accessor is test-only, so
  `FamilyStore::snapshot_family_records` is not reachable from the
  engine-side run; family records are counted by the mirrored reserved
  collection name, following the query and streaming precedent).
- `BackupError::DestinationUnavailable` extends the W4 category table
  for destination I/O failure (documented on the variant).
- This receipt is not tracked by git (`.gitignore` admits only
  `evidence/replication/`); integrator action required to track
  `evidence/recovery/` (same mechanism as W4-CA-12).

## Repair loop 1: warden findings fixed (base `5cba015`)

All five findings fixed inside `src/backup/**` and `tests/recovery/**`.
Each fix has a regression test that failed on `5cba015` (verified by
stashing the source fixes and running the new tests against the pre-fix
tree: 5/5 in-crate and 1/1 integration failed at the assertions named
below) and passes on the repaired tree.

1. Torn-publish crash window (MAJOR). `seal_point` created
   `destination/rp-<identity>/` before moving members in and before the
   manifest was written and renamed, so a crash in that window left an
   unsealed `rp-` directory and the identical retry failed
   `DestinationUnavailable` on `EEXIST`. Publication is now atomic at
   directory granularity: members plus the sealed manifest are assembled
   inside the staging candidate directory and the complete directory is
   renamed into place as one atomic rename, followed by a destination
   directory sync. An `rp-` directory with no sealed manifest is an
   unsealed orphan: `seal_point` replaces it (nothing sealed ever lived
   there) and `discover_points` skips it instead of failing the whole
   destination, while a sealed manifest that does not validate still
   fails closed. Red on `5cba015`:
   `a_torn_publish_orphan_is_repaired_by_the_identical_retry`
   (`retry seals: DestinationUnavailable`),
   `discovery_ignores_unsealed_orphan_directories`
   (`ManifestInvalid`), integration
   `unsealed_orphan_directories_do_not_break_discovery_or_retry`
   (`ManifestInvalid`).
2. Interior-whitespace tenant names sealed unparseable manifests (MAJOR).
   `tenant <name> …` was space-separated and the parser took the first
   token, so a committable tenant like `team a` produced a manifest whose
   run reported success and whose point could never be loaded. Chosen
   fix: encoding, not fail-closed rejection — the tenant line is now
   `tenant <name-bytes>:<name> <documents> <family_records>` and the
   manifest header is bumped to `terrabase-backup-manifest 2` (version 1
   manifests parse as `ManifestInvalid`; nothing outside this module
   reads the format, and no released artifact exists). Encoding was
   preferred because every valid `TenantId` stays representable, which the
   alternative would have narrowed. Red on `5cba015`:
   `tenants_with_interior_whitespace_round_trip_through_the_manifest`
   (`the manifest reloads: ManifestInvalid`).
3. State archives buffer one tenant's whole live state (MINOR). Documented,
   not streamed: the only reachable source of document values is the
   publication history, and last-write-wins cannot be emitted in one pass
   without a spill format, which would be a new feature rather than a
   repair. The bound is now stated in the module documentation's
   "Memory bound of a state archive" section and on
   `archive_state_at`: peak memory is the largest selected tenant's live
   byte weight, uncapped by this module, and incremental runs never
   materialize state. No test: a documentation disclosure, and the
   existing focused suites already pin the streaming behavior of
   incremental runs.
4. Durability disclosures were not durable (MINOR). The manifest bound
   only the aggregate count while the docs claimed per-run binding. Chosen
   fix: every archive record frame now carries its commit's achieved
   `LocalDurability` (one tag byte, all kinds, state archives carry the
   last write's durability), so the member digests bind the per-record
   fact and a restore or audit reading the archive sees the disclosure the
   run reported; the manifest keeps binding the count, and the doc
   comments now say exactly that. Red on `5cba015`:
   `record_frames_bind_the_commit_durability_into_the_archive_digest`
   (identical member digests for the same document at `Synced` versus
   `Buffered`).
5. Every progress snapshot cloned the whole disclosure list (MINOR).
   Snapshots now carry the newest `PROGRESS_DISCLOSURE_WINDOW` (64)
   disclosures plus the exact `skipped_total`; only the terminal
   `BackupResult` carries the complete list. The bound is documented on
   the type and the accessor. Red on `5cba015`:
   `progress_disclosures_are_bounded_while_the_total_stays_exact`
   ("widest window 72 of 72").

Post-repair receipts (this tree): `cargo fmt`; `cargo check --offline
--locked`; `cargo clippy --offline --locked --all-targets -- -D warnings`;
`cargo test --offline --locked` — all pass, 255 lib tests (was 250) and
10 integration targets, 0 failures. The in-crate backup suite is 20 tests
(was 15) and `tests/recovery/` is 5 (was 4).

Residual limits introduced or carried by this repair:

- Manifest format version 2 is not backward-compatible with version 1
  files; no version-1 artifact is known to exist outside this branch, and
  the reader rejects the old header fail-closed.
- `discover_points` treats an `rp-` directory without a sealed manifest as
  an orphan to skip; a deleted (not torn) manifest on a real point is
  therefore reported as an absent link (`ChainIncomplete`) rather than
  `ManifestInvalid`. Both are fail-closed; the distinction is recorded so
  the recovery lane can qualify it.
- The state-archive memory bound of finding 3 remains open for BK-02.
