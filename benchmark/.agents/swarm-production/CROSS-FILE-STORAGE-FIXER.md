# Cross-file SQLite storage takeover (work in progress)

Coordinator transferred sole storage ownership after the previous writer stopped. This task implements the methods; no Cargo was launched by this fixer. `engine_gate` owns the sole build lane.

## Concrete implementation

- `src/storage/sqlite/cross_file.rs`: durable full-intent prepare, exact resolve, transactional install with retained receipt, pre-install abort, explicit finish; untouched participants retain a reservation without a fabricated publication.
- `src/storage/prepared_codec.rs`: bounded private full-intent encoding including TTL guards, schema CAS, metadata, replication context/observations/conflicts, usage admission, and complete previously verified allocation. `usage/budget.rs` adds only crate-private durable encoding/restoration, authorized by root; no public deserialization/inbound authority bypass.
- Normal `CommitContext` publication now has a transaction-scoped staging function. Prepare runs that same path inside a rollback savepoint, then retains the full intent under a database-wide reservation. SQL effects including billing roll back; watermark/root/origin allocation caches are local scratch. Install resolves/compares the durable payload and skips only already-admitted temporal allocation/CAS conditions. Other validation dependencies are frozen by the reservation.
- Every ordinary SQLite table existing at prepare has dynamic TEMP INSERT/UPDATE/DELETE guards consulting durable reservation rows on every statement. This includes document/metadata/schema/causal/lifecycle/usage/budget tables. FTS virtual/shadow tables cannot accept SQLite triggers; production FTS mutation callers also modify guarded `fts_keys`/`fts_descriptors` in the same transaction. Index DDL is likewise transactional with guarded descriptor metadata. Guards are refreshed at prepare for newly created candidate tables. An active reservation causes open to skip mutating migration/reconciliation before recovery. Branch snapshot copying/rebinding rejects active preparations.
- Exact coordinator metadata uses a transaction-local authorization row that is removed before commit; it is never a persisted bypass. Shape/key authorization runs even without an existing reservation and invokes the engine canonical decoder for embedded identity/phase/keyset validation, including clear-only control requests. Durable resolve uses a coherent read transaction.
- Explicit cross-file billing context binds operation/participant/root metering participant/full logical units. Ordinary commit rejects it. The stable root alone charges/consumes full admission, including a real metering-only root intent with no fake writes; other participants retain ordinary customer origin, publication and storage accounting. Selecting a different touched shard per operation would allow grant reuse and is rejected as a protocol design.
- The original uniform cut is retained. Prepare permits only an exactly contiguous local-origin delta proven to consist of the same operation's canonical metadata-only controls; foreign origins, gaps, malformed payloads, document publications and completion visibility fail closed. Exact retained install uses that admitted guard.
- Canonical coordinator origin is authenticated against the local publication origin for controls and uniform-delta proof. Private `cross_file_dependencies` is retained in full-intent Wire v2; bounded canonical nonempty context is allowed only for prepared participants or canonical COMPLETE controls, with local-prefix sanity and no replicated-row/generic-commit smuggling. It joins publication context, never applied causal frontiers. Origin coordinator proves remote prefixes from actual physical-owner receipts under its gate; replica admission must separately prove actual installed owner prefixes on apply/restart. A lone SQLite file cannot attest another file's installed rows.
- Backup high-water now covers documents, complete replication publications, committed operation facts (including metadata/no-row/local receipts), and durable usage facts. Missing document storage still errors. No derived cache manufactures coverage.

## Causal findings / prevention

Previous storage work supplied trait defaults but no SQLite bodies. Missing codecs and staging separation were implementation work, not external dependencies.

The old participant codec mixed big-endian length framing with the little-endian Reader. Corrected to consistent little-endian and bumped private payload version to 3. Existing publication roundtrip plus new metadata-containing reopen test discriminate this failure.

The existing write meter compared the whole logical admission to one shard's local writes; other shards would independently mint more facts. Explicit nominated billing context fixes that without marking customer writes as internal maintenance.

SQLite forbids triggers on FTS shadow tables (confirmed with an in-memory SQLite diagnostic). Guard discovery uses `pragma_table_list` type `table`, and actual FTS caller transactions were inspected for guarded base metadata writes.

Backup capture's pinned cut can exceed the last document sequence after metadata publication. High-water must cover durable publication facts, not only document rows; the old no-row-delete expectation is updated deliberately.

## Verification queued

Source formatted with rustfmt. Build/test results pending `engine_gate`; do not treat this note as acceptance.

Selectors:

- `prepared_cross_file_` (ten new SQLite tests: dependency retention without fabricated applied frontier, malformed dependency encodings, reopen/receipt, reservation bypasses, CAS/untouched participant, uniqueness, original uniform cut with own/foreign/gap/malformed controls, allocation/single charge, nonmetering origin behavior, one root refill conserved across different child files).
- `storage::participant::` (existing codec roundtrips).
- `journal_high_water_covers_metadata_only_publication_after_reopen`.
- `sequence_only_delete_is_durable_across_reopen`.
- `journal_metadata_distinguishes_empty_storage_from_failed_reads`.

## Explicit protocol limits

Intermediate coordinator controls still use ordinary origin publication. The bounded uniform delta proof addresses self-invalidation without making controls local-only. Root is considering explicit causal edges from root ACTIVE to children and children to COMPLETE; follower visibility remains unverified. No end-to-end/global cross-file safety claim is made. The separate Astra reviewer must review this storage cohort and affected engine interactions after corrections/tests. Earlier stale snapshot selectors ran zero tests, which is not acceptance evidence.
