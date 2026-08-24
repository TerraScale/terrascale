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
  - `PublishedCommit { tenant, operation_id, sequence, writes }` with
    `enum PublishedWrite { Put { document }, Delete { id, version_at_delete } }`;
  - Engine mirrors all four reads `pub(crate)`.
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
  record is missing. The append is best-effort — a failed append leaves a
  sequence gap a consumer detects by discontinuity, never an entry for an
  unpublished commit. The write set is derived from the tables the commit
  moved: puts carry the published document (version included), deletes carry
  the identifier plus the version the before-table held (`0` for an absent
  identifier).
- **Verification obligations.**
  1. *Recoverability cannot come from the ledger alone* — proven by
     `tenant_commits_reconstruct_puts_and_deletes_with_versions` (puts carry
     assigned versions; deletes carry the removed version; an absent
     identifier records `0`) and by
     `publication_history_lets_a_follower_replay_puts_and_deletes_idempotently`
     (replaying the served commits through the storage seam reproduces the
     source view and redelivery is idempotent by operation identity).
  2. *Sequence order needs a different iteration path* — the journal index
     is per-tenant keyed by global storage sequence;
     `tenant_commits_page_in_global_sequence_order_across_the_limit` pages
     by `after`/`limit` in publication order, which the `(tenant,
     operation)`-keyed ledger order cannot provide.
  3. *The servable window is bounded and reported* — the scan serves exactly
     the verified prefix:
     `a_torn_publication_history_tail_bounds_the_servable_window` (a torn
     tail keeps the prefix, reports the start, and leaves the truncated end
     detectable against `published_state`) and
     `an_unservable_publication_history_reports_no_window_instead_of_a_gap`
     (a damaged first record or an absent journal reports no window; data
     stays readable through the ordinary snapshot path).
- **Other tests.** `publication_history_survives_reopen_and_serves_the_same_commits`;
  `committed_tenants_and_tenant_sequence_map_a_global_cursor_per_tenant`;
  `engine_publication_history_serves_reconstructable_commits_across_tenants`
  at the Engine seam.
- **Limits.** No compaction exists yet; when one lands it must move
  `servable_history_start`. A `Buffered` commit's journal record is synced
  like the ledger's resolution records, while its data root is not durable
  — after a restart the journal may serve a `Buffered` commit whose data the
  source lost; that asymmetry is the pre-existing W3 durability-level
  property and is W4-REPLICATION's disposition. The journal append adds one
  durable append+sync per commit beside the ledger's (no performance claim;
  batching is a later optimization).

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
packet report; the release test count is 194 with 0 failures.
