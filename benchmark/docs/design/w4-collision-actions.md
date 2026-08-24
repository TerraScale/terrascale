# W4 integrator collision-action record

This document records the executed integrator collision actions of the W4
design contract
(`docs/design/w4-transfer-replication-contract.md`, "Integrator collision
actions required before W4 behavior packets"). The parent assigned the
actions and owned the collision authority; every change below landed in the
exact named shared files, test-first at each new seam.

Base: commit `235ea6d` on `develop` (the integrated W4-0 declaration tree,
175 tests). Result: 194 tests, all green.

No parity, replication, durability, or performance claim is made anywhere in
this record. `CommitReceipt` remains local-only; no W4 code implies remote
acknowledgement from a local commit.

## W4-CA-2 — `Engine::streams()`

- **File.** `src/engine/mod.rs`.
- **Signature.** `pub fn streams(&self) -> StreamStore<'_>` delegating to the
  declaration-only `StreamStore::from_engine`, mirroring
  `Engine::queries`/`Engine::families`.
- **Tests.** `engine_streams_acquisition_path_exists_and_borrows_the_engine`
  proves the signature by function-pointer coercion and the borrow shape by a
  compile-level helper; invoking the seam is deliberately left to W4-STREAM's
  red test `engine_streams_returns_store_bound_to_durable_engine`, which
  stays red until that packet implements `from_engine`.
- **Limits.** None beyond the declaration-only constructor: no streaming
  behavior exists in this change.

## W4-CA-3 — publication-sink registry and the engine-owned index catalog

- **Files.** `src/engine/mod.rs`; `src/index/mod.rs`; call-site migrations in
  `src/index/tests.rs` and `src/query/tests.rs` (assertions unchanged).
- **Signatures.**
  - `pub(crate) fn subscribe_publications(&self, sink: CommitPublicationSink) -> SubscriptionGuard`
    with `SubscriptionGuard::is_attached()` and drop-time detach;
  - `pub(crate) const MAX_PUBLICATION_SINKS: usize = 64` — the bound;
  - `Engine` owns an `Arc<IndexCatalog>` whose sink is registered at open
    (`pub(crate) fn index_catalog()`), absorbing the queued W3-2 integrator
    catalog action: index maintenance is production-live.
- **The synchronous bounded design.** The emit path snapshots the registered
  sink handles under the registry lock — a bounded clone of at most 64
  handles — releases the lock, and only then calls each sink synchronously on
  the committing thread, after storage locks release. The registry never
  queues events and never runs a sink (or a sink destructor) under its lock:
  a removed sink closure is taken out under the lock and dropped after it, so
  a sink that owns the last `Arc<Engine>` cannot self-deadlock at teardown
  (found by a real deadlock in `index_publication_does_not_hold_the_storage_lock`
  during development and fixed before commit). A sink offered beyond the
  bound is not registered and its guard reports `!is_attached()`.
- **Tests.** `publication_events_reach_every_registered_sink`;
  `publication_sink_detaches_when_its_guard_drops`;
  `publication_registry_bounds_its_subscribers_without_blocking_the_commit`
  (a deliberately slow sink occupies the committing thread while another
  thread subscribes successfully, and the 65th sink is detached);
  `engine_index_catalog_receives_publication_events_in_production`. The
  W3-2 index tests and the query catalog test pass unchanged after the
  call-site migration.
- **Limits.** A sink that blocks inside its own closure still delays the
  commit that invokes it — that is inherent to the contract's synchronous
  emit rule and is the sink's responsibility (bounded handoff, never a
  storage lock, never an unbounded queue). `QueryStore::from_engine` still
  constructs its own catalog: `src/query/**` was read-only for this packet,
  so binding the query store to the engine-owned catalog — and removing the
  `with_index_catalog` test seam its documentation ties to this action —
  remains a follow-up for the parent.

## W4-CA-4 — the durable publication-history journal

- **Files.** `src/storage/mod.rs` (the `StoragePort` extension and the
  `PublishedCommit`/`PublishedWrite` types), `src/storage/ledger.rs` (the
  journal writer and its sequence-ordered index), `src/storage/file.rs`
  (commit-path, recovery, and port wiring), `src/storage/codec.rs`
  (published-write encoding), `src/storage/root.rs` (an exact-key document
  accessor), `src/engine/mod.rs` (the thin wrappers).
- **Signatures.**
  - `StoragePort::tenant_commits(&self, tenant: &TenantId, after: StorageSequence, limit: u32) -> Vec<PublishedCommit>`
    (the parent-assigned tenant-scoped spelling of the contract's
    sequence-ordered publication read);
  - `fn committed_tenants(&self) -> Vec<TenantId>`;
  - `fn tenant_sequence(&self, tenant: &TenantId, at: StorageSequence) -> Option<StorageSequence>`
    (the tenant's latest recorded sequence at or below `at`);
  - `fn servable_history_start(&self) -> Option<StorageSequence>` (added per
    obligation 3 below, so a cursor older than servable history is
    detectable);
  - `fn journaled_high_water(&self) -> Option<StorageSequence>` (repair F2:
    the last global sequence durably recorded in the journal);
  - `PublishedCommit { tenant, operation_id, sequence, durability, writes }`
    with `enum PublishedWrite { Put { document }, Delete { collection, id,
    version_at_delete } }` (the a2-amended shapes: the durability annotation
    is repair F4, the delete's collection is repair F3);
  - Engine mirrors all five reads `pub(crate)`, returning
    `Result<_, EngineError>` (repair F5).
- **Design choice.** A durable side structure (`history` beside `wal` and
  `ledger`), not an operation-ledger schema extension: the ledger's binding
  role stays byte-for-byte unchanged, so resolution, idempotency, and digest
  semantics are exactly the W3-1-gated ones and the full W3-1 suite passes
  unchanged. Format note (no released format, per W3 precedent): the journal
  is a new additive file; a location without one simply reports no servable
  history and is not rejected.
- **Write path.** The journal appends after the root pointer is published
  and its post-publication syncs complete, and before the `Published` ledger
  record: an entry can only describe an already published commit, and the
  uncertain path of a failed published-ledger sync still leaves the entry
  recorded. Recovery roll-forward appends entries for commits whose journal
  record is missing (annotated `Written`: that path never observed the
  commit's own post-publication syncs). The append is best-effort and never
  delays or fails the publication path. The write set is derived from the
  tables the commit moved: puts carry the published document (version
  included); deletes carry their exact key — the collection, the identifier,
  and the version that existed at that key (`0` when nothing did) — so an
  identifier reused across collections can never replay to a different
  occurrence than the source removed. The journal records no digest: the
  operation ledger already binds identity to write set, and this structure
  exists to be inverted, not to bind.
- **Failure observability (repair F2).** Sequences have legitimate holes —
  the allocation watermark advances before the durable write and other
  tenants consume interleaved sequences — so no consumer may detect a
  skipped journal append by discontinuity (the a1 record claimed exactly
  that and was wrong). The journal therefore carries a durable high-water
  mark: a checksummed mark record appended after every successful commit
  append, with the in-memory mark advanced to the newest verified commit
  record on open. `journaled_high_water() < published_state().0` (or a
  `None` mark against a nonzero published sequence) is the observable
  signal that at least one published commit is not servable from the
  journal; a consumer must fall back rather than assume completeness. A
  failed mark append leaves the mark conservative — behind a commit whose
  own record is durable — which never over-reports completeness.
- **Torn-tail truncation (repair F1).** The a1 open never truncated a torn
  journal tail, so every later append landed behind the torn bytes where no
  scan could see it and the servable window froze permanently. The scan now
  reports the verified-prefix length, and `FileStorage::open` truncates the
  journal to it exactly as the WAL is truncated; a truncation that cannot
  complete fails the open instead of masking the damage. The window bound
  and the high-water mark keep reporting the divergence that remains.
- **Durability annotation (repair F4).** Each entry carries its commit's
  achieved `LocalDurability`, so the phantom-history disclosure is
  per-entry and machine-readable: a `Buffered` **or `Written`** entry may
  describe document bytes the source lost after a restart (a `Buffered`
  commit writes no WAL/root bytes at all; a `Written` commit's are not
  synced), because the journal's own append is synced like the ledger's
  resolution records. **Registered obligation for W4-REPLICATION:** its
  dispatch must filter or reconcile journal entries by the durability
  annotation under the D-2 acknowledgement wiring — the engine records the
  disclosure and the annotation; the policy belongs to the replication
  packet.
- **Verification obligations.**
  1. *Recoverability cannot come from the ledger alone* — proven by
     `tenant_commits_reconstruct_puts_and_deletes_with_versions` (puts carry
     assigned versions; deletes carry their exact key's collection and the
     removed version; an absent key records `0` while keeping the requested
     collection) and by
     `publication_history_lets_a_follower_replay_puts_and_deletes_idempotently`
     (replaying the served commits through the storage seam reproduces the
     source view and redelivery is idempotent by operation identity; since
     repair F3 the replay stages each delete against the recorded key with
     `ExpectedVersion::Exact(version_at_delete)`, or mirrors the no-op when
     the recorded version is zero).
  2. *Sequence order needs a different iteration path* — the journal index
     is per-tenant keyed by global storage sequence;
     `tenant_commits_page_in_global_sequence_order_across_the_limit` pages
     by `after`/`limit` in publication order, which the `(tenant,
     operation)`-keyed ledger order cannot provide.
  3. *The servable window is bounded and reported* — the scan serves exactly
     the verified prefix:
     `a_torn_publication_history_tail_bounds_the_servable_window` (a torn
     tail keeps the prefix, reports the start, and leaves the truncated end
     detectable against `published_state`; the fixture walks the framed
     records to tear inside a commit record, because the interleaved
     high-water marks would otherwise absorb a fixed-length tear) and
     `an_unservable_publication_history_reports_no_window_instead_of_a_gap`
     (a damaged first record or an absent journal reports no window; data
     stays readable through the ordinary snapshot path).
- **Other tests.** `publication_history_survives_reopen_and_serves_the_same_commits`;
  `committed_tenants_and_tenant_sequence_map_a_global_cursor_per_tenant`;
  `engine_publication_history_serves_reconstructable_commits_across_tenants`
  at the Engine seam.
- **Repair-loop tests (a2).**
  `a_torn_journal_tail_is_truncated_so_later_appends_stay_servable` (F1:
  post-tear commits stay servable across a reopen — fails without the
  truncation);
  `a_failed_journal_append_leaves_the_divergence_observable` (F2: an
  injected `JournalAppend` fault skips journaling, the commit still
  publishes, the mark stays at the last successful append, and the
  divergence survives a reopen — fails if the mark never advances);
  `journal_deletes_resolve_the_exact_key_across_duplicate_identifiers`
  (F3: the deleted beta occurrence's own collection and version are
  recorded — not the first canonical alpha occurrence's — and an exact-key
  follower replay removes exactly the beta occurrence; fails under the
  first-occurrence derivation);
  `journal_records_the_achieved_local_durability_of_each_commit` (F4:
  Buffered, Synced, and Written entries carry their annotations across a
  reopen). Each repair test was mutation-checked against its bug before
  landing.
- **Limits.** No compaction exists yet; when one lands it must move
  `servable_history_start` and the high-water mark. The phantom-history
  disclosure above is recorded, not resolved: W4-REPLICATION owns the
  filtering policy. The journal adds two durable append+sync rounds per
  commit beside the ledger's — the commit record and its high-water mark
  (no performance claim; batching or a periodic mark is a later
  optimization).

## W4-CA-5 — the family-record enumeration

- **Files.** `src/families/mod.rs` (the method), `src/families/state.rs`
  (identifier parsing), `src/backup/mod.rs` (the fleshed-out crate-private
  `FamilyRecordInput` fields only).
- **Signature.** `FamilyStore::snapshot_family_records(&self, scope: &TenantScope, snapshot: &ReadSnapshot) -> Result<Vec<crate::backup::FamilyRecordInput>, FamilyError>`
  — implemented as a method on the Engine-borrowed `FamilyStore` because the
  contract's free-function spelling has no path to storage; the parameter
  list is exactly the contract's and `Engine::families()` remains the only
  acquisition path. `FamilyRecordInput` carries the reserved identifier, the
  family kind, the encoded value, and the published version, with
  crate-private accessors; it is never re-exported.
- **Tests.**
  `snapshot_family_records_enumerates_the_reserved_collection_at_a_snapshot`
  (kinds, identifiers, versions; user documents never appear; tenant
  scoped);
  `snapshot_family_records_observe_only_committed_state` (an earlier
  snapshot never sees a later commit);
  `snapshot_family_records_reject_foreign_snapshots_and_collisions`
  (foreign snapshot/scope are typed errors; a reserved-collection squatter
  is `ReservedStateInvalid`, not a skipped record).
- **Limits.** The enumeration materializes the tenant's snapshot view
  through the existing full snapshot read; a bounded windowed variant for
  backups of very large tenants is a later disposition.

## W4-CA-7 — the bounded windowed snapshot read

- **Files.** `src/storage/mod.rs` (the port method), `src/storage/root.rs`
  (the windowed table read), `src/storage/file.rs` (the delegation),
  `src/engine/mod.rs` (the Engine seam).
- **Signatures.**
  - `StoragePort::documents_at_from(&self, sequence, tenant, after: Option<&DocumentId>, limit: u32) -> Vec<Document>`;
  - `Engine::snapshot_documents_from(&self, scope, snapshot, after: Option<DocumentId>, limit: u32) -> Result<Vec<Document>, EngineError>`
    with the same validation as `snapshot_documents`.
- **Resume rule.** The cursor resolves to the first canonical key of the
  tenant bearing the identifier and the window resumes strictly after that
  key; a foreign cursor skips identifiers sorting below it. With a
  tenant-unique identifier, paging by the last document of each window is an
  exact partition of canonical order at every limit. When one identifier is
  reused across collections the rule is lossless but not exact — duplicates
  only, the streaming contract's at-least-once shape — and a whole-tenant
  paging sequence over reused identifiers can cycle; the collection-scoped
  selectors of PB-6007 are the exact path. This is the documented limit of
  the frozen id-only cursor signature.
- **Tests.**
  `documents_at_from_pages_canonical_key_order_exactly` (limits 1–9
  reproduce the canonical view with interleaved collections and
  non-monotonic identifiers; a zero bound serves nothing);
  `documents_at_from_resumes_strictly_after_the_cursor_identifier`
  (duplicate identifiers across collections: every document is served at
  least once, never lost);
  `snapshot_documents_from_serves_bounded_windows_and_validates_the_view`
  (Engine-level paging equality plus the typed foreign-snapshot, -scope,
  and -tenant errors, including after a reopen).
- **Limits.** The cursor scan walks the tenant's key range; a keyed
  secondary structure is a later optimization. The read is the seam only —
  no streaming behavior exists here.

## Check evidence

All commands were run from the worktree root on branch `fleet/w4-collisions`
with the pinned toolchain and `Cargo.lock`: `cargo fmt --all -- --check`,
`cargo check --workspace --locked`, `cargo test --workspace --locked
--release`, `cargo clippy --workspace --all-targets --all-features --locked
-- -D warnings`, `cargo doc --workspace --no-deps --locked`, and
`python3 scripts/validate_parity.py --check`. Exit codes are recorded in the
packet report; the a1 release count was 194 and the a2 count is 198, both
with 0 failures.

## Repair history

### a1 — initial execution

Base `235ea6d`; five commits ending at `505e93c`; 194 tests green. The
quality gate returned NO-GO; the findings and their dispositions are
recorded in the CA-4 amendments above and below.

### a2 — quality-gate repair loop

Base `505e93c` (quiescent); one repair loop; no push. Findings and
dispositions:

- **F1 (major, fixed).** A torn journal tail was never truncated, so every
  post-tear append landed behind the torn bytes and the servable window
  froze permanently. The scan now reports the verified-prefix length and
  open truncates to it, mirroring the WAL. Proven by
  `a_torn_journal_tail_is_truncated_so_later_appends_stay_servable`
  (mutation-checked).
- **F2 (major, fixed per parent adjudication).** The a1 record claimed a
  failed append left "a sequence gap a consumer detects by discontinuity" —
  false, because sequences have legitimate holes. The journal now records a
  durable high-water mark (a mark record after every successful commit
  append; the scan folds it with the newest verified commit record),
  exposed as `journaled_high_water()` on the port and the Engine seam, so
  `high_water < published` is observable. Proven by
  `a_failed_journal_append_leaves_the_divergence_observable` with the new
  test-only `FaultPoint::JournalAppend` (mutation-checked); the doc claim
  was rewritten to describe the real mechanism.
- **F3 (major, fixed per parent contract amendment).**
  `PublishedWrite::Delete` now carries the collection (shape: `collection +
  id + version_at_delete`; the `None` collection is exactly the provable
  no-op delete, where `version_at_delete == 0` and nothing existed at any
  key for the identifier). The version is derived from the exact keyed
  removal, never from the first canonical occurrence of the identifier, and
  a follower replay resolves the exact key with
  `ExpectedVersion::Exact(version_at_delete)`. Proven by
  `journal_deletes_resolve_the_exact_key_across_duplicate_identifiers`
  (mutation-checked) and by the updated replay test (also covering F7).
- **F4 (assessment, fixed per parent disposition).** Each journal entry is
  annotated with its commit's achieved `LocalDurability` (schema addition;
  no released format), exposed as `PublishedCommit::durability()`. The
  phantom-history disclosure now names `Written` alongside `Buffered`, and
  the W4-REPLICATION dispatch obligation — filter or reconcile entries by
  the annotation under the D-2 wiring — is registered in the CA-4 section
  above. Proven by
  `journal_records_the_achieved_local_durability_of_each_commit`.
- **F5 (rider, fixed).** The four Engine history wrappers no longer collapse
  a poisoned storage lock into empty/None answers, which a replication
  consumer would read as "nothing to catch up"; they return
  `Result<_, EngineError>` and fail closed as
  `EngineError::StorageFailure`. The frozen `StoragePort` spelling is
  unchanged.
- **F6 (rider, fixed).** The journal doc comment claimed it recorded "the
  digest binding"; it records none. The comment now states the journal
  deliberately carries no digest — the operation ledger already binds
  identity to write set, and this structure exists to be inverted, not to
  bind.
- **F7 (rider).** Covered by F3's exact-version replay.

Schema notes (no released format, per W3 precedent): the journal commit
record gained a durability field and its delete form gained a collection;
both are additive within the unreleased journal format. An older-format
journal cannot exist in any released artifact; a record that fails to parse
bounds the servable window exactly as a torn tail does.
