# Integrated receiver brief: source-group reconstruction and target staging

Status: design packet for Astra review; no runtime implementation in this
packet.

## Source facts verified in tree

- `src/engine/cross_file.rs::CrossFileIdentity` persists source coordinator
  origin, incarnation, authority epoch, route generation, read frontier,
  write digest, complete participant baseline map, operation, and decision
  epoch.
- `Engine::cross_file_completion` returns the committed source identity,
  source-local visible frontier, and visibility receipt.
- `Transaction::commit_cross_file` creates one `CommitIntent` per changed
  source participant, all with the same operation identity; the root intent
  carries the request/private controls. It sets `cross_file_control` so
  publication events are retained until completion cleanup.
- `install_cross_file_prepared_locked` emits one publication event per
  installed participant and then clears the retained root event after writing
  completion. It does not expose a complete publication byte group to the
  transport.
- `Engine::replication_publications(scope, after, limit)` returns retained
  `(source_sequence, ReplicatedPublication)` rows. The production causal
  sender (`crates/terrabase-tbep/src/production_causal.rs::outbound_frame`)
  polls this API one row at a time and retains each frame until its ACK.
- `ReplicatedPublication` carries `commit_ulid`, origin dot/HLC/context,
  records, and private payload. `publication_binding` requires the commit ULID
  to be a valid `OperationId`; cross-file intents use the original operation
  identity, so matching is exact `publication.commit_ulid == operation.as_str()`.

## Source group assembly (required helper)

Required Engine-owned helper (new; current API does not provide exact lookup):

```rust
pub(crate) fn reconstruct_cross_file_group(
    &self,
    scope: &TenantScope,
    operation: &OperationId,
) -> Result<SourceCrossFileGroup, EngineError>;
```

Algorithm:

1. Load `cross_file_completion`; require committed decision and valid source
   coordinator origin/incarnation/route generation.
2. Resolve the current admitted source route and map each identity participant
   (`shard-{id}`) to its physical Engine. Reject duplicate/missing identities,
   stale generation, and foreign incarnation/origin.
3. For each participant compare source `visible[name]` to baseline
   `identity.participants[name]`:
   - equal: untouched; store an explicit empty participant receipt;
   - greater: scan that physical Engine’s retained publications from baseline,
     bounded through visible, and select exactly one row whose
     `commit_ulid == operation.as_str()`.
4. Validate each selected row’s origin against the physical file’s persisted
   origin, sequence interval, tenant, operation, and `publication.fingerprint()`.
   Keep participant order deterministic by identity name. The source
   `identity.write_digest` remains the authoritative complete write-set digest;
   per-publication fingerprints are evidence and never replace it.
5. The root publication’s private payload is the existing source request,
   prepare/decision/visible control provenance. Decode/validate it against the
   source identity, but do not apply those source control records as target
   authority. Child publications are correlated by the same source operation
   after the root control is known.

The helper returns changed publication bytes plus explicit untouched participant
entries. It must return `HistoryExpired`/`Conflict` for a pruned or missing
changed row, duplicate match, digest/origin mismatch, or incomplete completion;
it must never fabricate an empty publication for a changed participant.

## Target adapter (dependent, not part of source helper)

The target receiver must first obtain the current admitted target route. Source
shard IDs and source sequence maps are provenance only; target keys may route to
different target physical files and target route generation is independently
validated.

Use the approved target operation namespace:

```text
target-op = OperationId("cross-file-target-" + hex(blake3(
  "terrabase.cross-file.target-operation.v1\0" +
  len(source_coordinator_origin) + source_coordinator_origin +
  len(source_operation) + source_operation +
  target_database_incarnation.to_be_bytes() +
  len(tenant) + tenant)))
```

The target `CrossFileIdentity.coordinator_origin` is the target local origin;
its `operation` is the derived target operation; its digest is the source
identity digest; its participants and visible sequences are target-local.
Source identity/provenance must be durably retained in the existing request
metadata, not silently replayed as target `cross-file-visible` metadata.

For each target participant, extract merge planning from the existing
`Engine::apply_replicated_publication` path into a private, non-visible intent
builder (required API, not currently present):

```rust
pub(crate) fn prepare_replicated_intent(
    &self,
    scope: &TenantScope,
    publication: &ReplicatedPublication,
    target_operation: OperationId,
    target_snapshot: ReadSnapshot,
) -> Result<CommitIntent, EngineError>;
```

The intent preserves source origin, origin sequence, HLC, commit ULID,
context, replication fingerprint, private payload, schema observations, and
conflicts; it carries no usage admission/billing and does not publish visibly.
The target then uses existing:

```rust
StoragePort::prepare_cross_file_intent(
    &mut self,
    &ParticipantIdentity,
    Option<&CommitIntent>,
) -> Result<PreparedCrossFileIntent, CommitRejection>
StoragePort::install_cross_file_intent(
    &mut self,
    &PreparedCrossFileIntent,
) -> CommitResult
StoragePort::finish_cross_file_intent(
    &mut self,
    &ParticipantIdentity,
) -> Result<(), ()>
```

Prepare every target participant in the complete target route, including
explicit untouched/empty receipts. Persist target decision only after all
prepares; install all; build `VisibleFrontier` from target install sequences;
advance target visibility only with the complete map; then finish intents.

## Ordering, ACK, and restart

Production source lanes are independent. The root/control publication must be
received before child publications can be interpreted. A child arriving first
returns deferred without ACK; its existing causal sender keeps the frame
pending. This is safe because `CausalDriver` retains each frame until ACK and
the source retention frontier must not advance for any participant until the
whole group is complete.

Standalone publication: existing per-publication apply and ACK behavior stays
unchanged when the publication is not identified as a cross-file group.

Cross-file group: ACK all source participant frames only after target prepares,
target decision, every target install, target-local visible completion, and
finish are durable. A missing/pruned row, target route mismatch, partial
install, or lost response leaves frames retryable and visibility absent.
Reopen reconciles target-local active/committed request metadata and prepared
intents before target lease admission; it never treats source frontier values
as target revisions.

## First acceptance cases

- Two changed participants plus one untouched participant reconstruct with
  exact source rows and explicit empty receipt.
- `commit_ulid`, origin, sequence, fingerprint, and source control alteration
  rejects reconstruction.
- Root control publication before child succeeds; child-first defers without
  ACK and succeeds after root.
- Pruned changed publication returns `HistoryExpired`, never partial export.
- Target repartition maps one source publication to multiple target participants.
- One target prepare/install refusal leaves every target result hidden and all
  source frames retryable.
- Restart after target decision/partial install resumes idempotently or stays
  invisible.
- Derived target operation differs for source origin or target incarnation and
  replays exactly once.

## Scope boundary

This packet adds no wire schema and no runtime code. The source reconstruction
helper is independently releasable only when called by the real per-physical
production outbox path; an uncalled scanner does not establish integration.
The target intent builder, target route mapping, group receiver, and group ACK
remain the next implementation slice.
