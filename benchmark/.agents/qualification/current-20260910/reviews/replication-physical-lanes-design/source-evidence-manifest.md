# Source evidence manifest (2026-09-10)

This manifest freezes the source-backed facts used by
`integrated-receiver-brief.md`. Hashes are `git hash-object` values from the
review source state; no runtime changes or Cargo checks were made.

## Current file hashes

| File | Blob hash |
| --- | --- |
| `src/engine/cross_file.rs` | `b7bedd0dd77cf2723dc3781151f75d9a434e41a0` |
| `src/engine/replicated_publication.rs` | `0d8dab6a1482bdf0cde78d20aae4cbaa8c53b5e5` |
| `src/storage/mod.rs` | `30c47b57df2543fd89d6d75f85827a98b3c6efa3` |
| `crates/terrabase-tbep/src/production_causal.rs` | `3da679bc20cffcaf6b9690100861f5a5b48a720e` |
| `crates/terrabase-server/src/replication_peers.rs` | `9a5205ab9f15cc3e30e9aeeb293a28163d5ea53c` |
| `crates/terrabase-cluster/src/wal_route.rs` | `a1226f6b4eca2296a779015162594a21cbb74d0e` |

## Caller and identity excerpts

- `src/engine/cross_file.rs:124-142`: source `CrossFileIdentity` is created
  from source origin, source incarnation, authority epoch, route generation,
  source read frontier, a hash of the encoded source payload plus ordinary
  digest, complete `shard-{id}` participant baselines, original operation, and
  decision epoch.
- `src/engine/cross_file.rs:143-160`: completion is resolved by the original
  operation and exact write digest before a retry is accepted.
- `src/engine/cross_file.rs:165-177`: the prepare receipt’s source publication
  dot becomes the cross-file dependency context for every participant intent.
- `src/engine/cross_file.rs:180-195`: one `CommitIntent` is prepared per
  participant; empty untouched participants may have no intent.
- `src/engine/cross_file.rs:201-238`: prepare refusal durably aborts; committed
  decision precedes installation; installation returns participant events.
- `src/engine/cross_file.rs:1355-1370`: request metadata is read from the
  existing `control:cross-file-request/v1/<operation>` key.
- `src/engine/cross_file.rs:1396-1484`: visible publication writes the source
  `visible`, completion, and finishing metadata in one maintenance transaction,
  then deletes the active marker. Its participant sequences are source-local.
- `src/engine/cross_file.rs:1557-1637`: prepare control is a maintenance
  transaction writing the operation record, active marker, and optional request;
  it sets `cross_file_control` but does not expose a direct transport manifest.
- `src/engine/cross_file.rs:1656-1725`: decision control is a separate
  maintenance transaction writing commit/abort phase and active marker.
  Therefore a group must not assume one combined root control publication.
- `src/engine/replicated_publication.rs:13-29`: a publication contains origin,
  origin sequence, HLC, commit ULID, context, records, and private payload.
- `src/engine/replicated_publication.rs:48-54`: `ReplicatedPublication::fingerprint`
  is public and delegates to the private binding validator; it is not a free
  function available to another crate.
- `crates/terrabase-tbep/src/production_causal.rs:262-294`: the live outbox
  polls one Engine’s `replication_publications` cursor and frames one row.
- `crates/terrabase-tbep/src/production_causal.rs:331-350`: each frame is
  retained in the driver pending window with its source sequence checkpoint
  until acknowledgement.
- `crates/terrabase-server/src/replication_peers.rs:980-1008`: inbound TBRP
  uses the current repair lane and its acquired logical/root replication Engine;
  it does not yet dispatch by target physical route.
- `src/storage/mod.rs:682-705`: existing hidden participant lifecycle is
  `prepare_cross_file_intent`, `resolve_cross_file_intent`,
  `install_cross_file_intent`, and finish/abort; this is the target staging seam.

## Corrected assumptions

1. `publication.fingerprint()` exists, but only as the associated method shown
   above; source reconstruction must call it on the concrete publication.
2. `CrossFileIdentity` completion/request helpers are crate-private. A helper
   that needs Member route ownership must receive the retained admitted cut (or
   be called inside the Engine/Member crate); it cannot discover the registry
   from an Engine alone.
3. Prepare, decision, visible, finishing, and completion controls are separate
   maintenance transactions/metadata phases. The source group assembler must
   locate actual retained publication rows by their maintenance operation IDs
   and original participant operation, rather than assume one root data row
   contains all controls.
4. Source publication sequence and visible frontier sequence are source-file
   local. They are evidence/baselines only; target installation receipts form a
   separate target-local frontier.

## Required API changes (not currently present)

- An Engine/Member-internal exact retained-publication lookup or bounded group
  reconstruction helper that receives the retained admitted participant cut and
  validates all source control/data publication rows against the existing
  identity and completion metadata.
- A private replicated-intent planning seam extracted from
  `Engine::apply_replicated_publication`; current public apply is visibly
  committing and cannot be used for hidden target preparation.
- A target route dispatch/reconciliation adapter that groups source records by
  the target admitted route and drives the existing hidden intent lifecycle.

These are missing contracts, not claims that an unused adapter is integrated.
