# Shard copy and stable revision source brief

No schema or serving activation changes are included in this proposal. The preceding cursor correction is separately verified by rust_checks; preserve shard-correction.md as evidence for its earlier cohort.

## Existing source and caller contracts

- src/storage/sqlite/mod.rs already stores documents.revision as TEXT. insert_put_row/delete fill it with revision_string(local sequence, numeric version). document_revision_in_collection_at reads that stored column without loading the body. Public GET/If-Match/write-response/history paths consume it through Engine/Transaction revision probes.
- Existing document rows retain original origin, origin sequence, commit identity, HLC, causal context, body, tombstone, and version. DocumentMergeState exposes current provenance and document_context exposes current causal context. These current-only readers must not be used to assemble a frozen snapshot across concurrent source commits.
- canonical_journal_row demonstrates deterministic encoding of provenance fields, but its encoded document body contains the local numeric version. It cannot be reused unchanged as a location-independent public revision.
- src/engine/document_request.rs currently persists request fingerprint plus predicted numeric result version, then reconstructs receipt.local_sequence-version. The conditional_replay owner confirmed that changing public revision requires atomically storing the exact published revision with the original request binding; looking up the live document on retry is incorrect. Deletes return no ETag.
- Current shard snapshot target tx.put invents new local versions and causal provenance. The subsequent original journal replay cannot undo that reliably. Current SnapshotRecord exposes only final document plus durability, not retained tombstones, historical versions, original operation outcomes, or causal lineage.
- Branch creation deliberately clears original publication tables and retains branch_snapshot_baseline. Normal retention prunes history. Therefore full splits must support a frozen retained baseline plus tail; rejecting every nonzero/pruned baseline or relying on original journal replay from zero is not a complete implementation.

## Stable opaque public revision

Use the existing stored revision column, with a domain-separated canonical identifier computed at row publication from scoped key, immutable source publication identity, and canonical served value/merged state. Exclude destination-local storage sequence. Replacement/delete-recreate with identical value must change identity; body hash alone is insufficient. Composite merged values must be distinguished; winner operation alone is insufficient. Do not advance a revision solely because unobservable losing-merge bookkeeping changes while the served state is unchanged.

The exact field set needs producer vectors for ordinary replacement, fieldwise composite merge, losing-merge/no visible change, duplicate replay, and identical-value delete/recreate. Compute at writes so existing cheap body-free GET/304 probes remain cheap. No old-format compatibility branch is needed for this prelaunch format change; update stored fixtures and internal producers directly.

Coordinate with conditional_replay: exact local CAS remains the observed SQLite row identity checked under the storage write lock, independently of public revision equality. Store the actual public result revision in original-request result metadata during the same storage transaction as publication. Snapshot/import must retain that operation receipt and request metadata relationship, so a moved key's retry returns the original result without probing current data.

## Full retained snapshot importer

1. Capture one fixed source boundary and durable retention pin before copy. Enumerate the actual retained per-key baseline and retained versions/tombstones at that boundary, plus causal state, schemas/private controls, and retained operation/request outcomes. Persist the copy cursor and pinned boundary. Do not stitch current-only provenance reads onto an older document snapshot.
2. Add a typed internal shard snapshot record/page contract carrying source provenance, logical/public revision, original retained history, and receipt associations. Import through a dedicated staging-storage transaction; ordinary tx.put or apply_replicated_publication recalculates target-local versions and is not the correct importer. Page commit and retry identity/checkpoint must be atomic and unbilled.
3. Preserve exact frozen logical/causal/operation state while allocating any necessary destination-local storage sequencing. Restore required schema/index materializations through the storage owner. Continue excluding source physical route/split/target control metadata.
4. Tail retained original publications strictly after the frozen snapshot boundary. Verify source and target canonical state/history/receipt facts before route publication, with explicit baseline and publication-summary completeness proof. Pin required history while the split is active; release only after verified retirement.
5. Qualify pruning before split, concurrent writes/schema changes during copy, create/delete/recreate, merge conflicts, crash between page commit and checkpoint, crash at snapshot-to-tail transition, original-request retry after movement, and public GET ETag/If-Match continuity after import. This must support ordinary retained baselines, not fail them permanently.

## Disjoint producer/runtime work once the record interfaces are fixed

- Revision/receipt owner: storage revision publication and document_request result binding/resolution; public GET/If-Match/original-retry tests. Coordinate with conditional_replay's exact CAS work.
- Snapshot owner: frozen retained source page, pin/cursor and dedicated target importer. Engine/shard files plus explicitly assigned storage import module; no public serving activation.
- Target durability/grant owner: accepted split-specific grant bound to parent database incarnation, source/target shard IDs, immutable placement epoch/range, target member identities, and measured target RF2/causal frontier. Extend physical/causal transport addressing so parent and target files are distinct admitted lanes. Parent placement acceptance alone is insufficient.
- Routing owner: admitted shared Engine handles by parent/shard, actual key resolution for reads/writes, atomic generation validation, and session-frontier translation. DatabaseLease::begin_routed_transaction currently still opens the parent Engine.
- Multi-shard consumer owner: ordered query/history/export/import/reactive behavior and cross-file batch semantics through the actual server callers. Point routing alone does not complete transparent serving.
- Retirement owner: physical source-range retirement after verified target state and outstanding reader/session/durability obligations. Ordinary replicated tombstones would delete target customer data and are not a substitute. Remove the existing no-op cleanup only when real retirement is connected.

These are implementation boundaries, not fulfilled readiness claims. Production MemberSplitRuntime startup/resume/periodic driving must be wired only with the actual admitted target handles and measured grants above.

## Existing full-state copy building blocks

storage/sqlite/branch_import.rs::copy_encrypted_snapshot already copies authenticated encrypted DB/WAL state, including retained history and operation outcomes. However it requires an immutable source, and engine/branch_restore.rs holds with_quiescent_wal through the whole copy. Reusing that whole-source writer fence would undermine online splitting. Branch provenance transformation is also unsuitable: it deliberately resets original publication lineage and outcomes for a new branch.

A dedicated bounded consistent SQLite snapshot producer can reuse encrypted VFS/ownership validation and then perform shard-specific retained-state filtering. An online SQLite backup implementation would need the rusqlite backup feature (currently bundled/cache/hooks only). Whether selecting physical snapshot/filtering or typed retained-row import, preserve full retained baseline and original receipts; do not invoke branch lineage reset and do not hold the source writer barrier across a full database-sized copy.

## Original retry durability identity

conditional_replay identified another concrete caller: WriteDurability::confirm(Global) currently recovers original origin/origin-sequence by reading journal_rows at original receipt local sequence. Pruning or migration without a coherent mapping can make a durable original request impossible to reconfirm globally. The exact original-request result binding should therefore retain the published opaque revision and original causal publication identity needed for confirmation atomically. Snapshot/import must preserve the receipt, request fingerprint/result, and that original identity together; copying document values or request metadata alone is insufficient.
