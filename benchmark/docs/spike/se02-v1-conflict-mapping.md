# SE-02 V1-parity conflict mapping draft

Status: draft for the SE wave (owner: SE-02; dispositions land through
WS-PARITY, never here). V1 source identity: TerraBase at commit
`fa99b9257776dbe625e6a2388c74c9ce916637ee` (the pinned V1 checkout).
Every V1 claim below cites a file at that commit; this draft resolves no
parity row and records no qualified status.

## The server-authoritative rule, stated first

In the V2 client sync channel the engine server is the single writer of
every sync scope. Clients (`terrascale.sync.v2` sessions) apply
server-produced delta batches and never multi-master documents
(PD-2026-08-26-05 D1/D2, PD-2026-08-26-12). Therefore no client-visible
conflict resolution exists on the client channel: what V1 called a
conflict is, for the client channel, impossible by construction, and the
V1 conflict surfaces map to events the engine emits from the member
plane (SE-03's backbone selectors), not to merge decisions clients make.

The one place V2 resolves conflicting writes is the member replication
plane, which ADR-0004 D-3 fixes as deterministic per-key
last-writer-wins on a bounded hybrid logical clock. The Loro-based sync
core never runs that policy: it materializes already-resolved journal
commits in journal order at a fixed server peer id.

## V1 evidence (pinned commit)

| V1 fact | Source at `fa99b925…` |
| --- | --- |
| Conflict notifications are a Changes API change type: `DocumentChangeTypes.Conflict = 64` (alongside `DeleteOnTombstoneReplication = 32`) | `src/TerraBase.Client/Documents/Changes/ChangeNotification.cs` |
| A conflict raises an after-commit `DocumentChange { Type = Conflict }` carrying id, collection, and change vector — never a document body — and the conflicting version goes to the conflicts table | `src/TerraBase.Server/Documents/ConflictsStorage.cs` (`AddToConflictsTable` and the `Transaction.AddAfterCommitNotification` above it) |
| Whole-document conflict resolution policy: `StraightforwardConflictResolution { None, ResolveToLatest }`, where `ResolveToLatest` is "always resolve in favor of the latest version based on the last modified time" | `src/TerraBase.Client/Documents/Replication/StraightforwardConflictResolution.cs` |
| Server-side conflict intake at document granularity (`HandleConflictForDocument(…, long lastModifiedTicks, BlittableJsonReaderObject doc, ChangeVector changeVector, …)`) with identical-document fast path and a HiLo special case | `src/TerraBase.Server/Documents/Replication/ConflictManager.cs` |
| Tombstones are first-class storage records with `TombstoneType { Document = 1, Attachment = 2, Revision = 3, Counter = 4 }` carrying `Etag`, `DeletedEtag`, `ChangeVector`, `LastModified`; a `TombstoneCleaner` reclaims them | `src/TerraBase.Server/Documents/Tombstone.cs`, `src/TerraBase.Server/Documents/TombstoneCleaner.cs` |

## Mapping draft

| V1 surface | V2 mapping (draft) | Notes and open points |
| --- | --- | --- |
| Changes API `Conflict = 64` notification | A `ChangeFilter` conflict selector on the change-event backbone (SE-03) fed by the member plane's `ConflictRecord`; notification carries `ConflictRecord` content-free facts (operations, members, HLC stamps, outcome), never bodies — mirroring V1's id+collection+change-vector shape | Backbone ownership: SE-03. The client channel itself never generates this event. |
| `StraightforwardConflictResolution.ResolveToLatest` (document-granularity LWW by last-modified ticks) | Member plane: ADR-0004 D-3 per-key LWW on the bounded HLC. Client channel: nothing to resolve (single writer). | Granularity differs (V1: whole document; V2 members: per key). The V1 database-level solver configuration (`ConflictSolver`, `ModifyConflictSolverOperation`) has no V2 member-plane equivalent yet — registered gap for WS-PARITY disposition. |
| Conflict artifact storage (conflicts table with the losing versions) | V2 retains losers as content-free `ConflictRecord` facts only (ADR-0004 D-3 records this accepted difference for the member plane). V1's script-based merge (`ConflictSolver`) is not mapped; any merge policy is a charter-level unresolved conflict-policy decision, not a builder's call. | Unresolved by charter; do not silently decide. |
| `DeleteOnTombstoneReplication = 32` change type | A delete observed through replication: in V2 the member plane converges deletes and the backbone publishes them; the client channel materializes delete as the sync-core tombstone below. | SE-03 selector mapping. |
| Document tombstone (`TombstoneType.Document`, change vector, etag, last-modified, cleaner) | Sync-core tombstone: the root-map key delete removes the document from every consumer's state while remaining replayable history until history compaction (`SyncCore::compact_history` raises the retained floor past it). Retention/GC policy (the `TombstoneCleaner` analog) is a server-side window/compaction policy — SE-07's budget decision. | V1 tombstones are storage records visible to replication and subscription; V2 sync-core tombstones are delivery-history facts. Attachment/revision/counter tombstone kinds have no client-channel analog in this draft (families own their semantics). |
| Per-key LWW vs Loro containers | Not a conflict mapping: with one writer there are no concurrent Loro ops, so container granularity never decides anything. The field-granular container layout (`docs -> document -> field`) is purely a delta-bandwidth choice (see `docs/spike/se02-prototype-evidence.md`, the tracked copy; an identical untracked mirror sits at `evidence/spike/se02-prototype-evidence.md`); it must not be read as field-level merge semantics. If the charter ever allows client-side writes, container semantics would become a conflict-policy question and would need its own decision record. | Guarded by the server-authoritative rule above. |

## What this draft does not claim

- No parity row is dispositioned; WS-PARITY owns dispositions.
- No statement about V2 conflict behavior being equivalent, better, or
  complete — the mapping is a draft input to SE-03/SE-07/SE-11 and the
  parity matrix.
- The V1 conflict-solver configuration surface (custom resolution
  scripts) is flagged, not resolved.
