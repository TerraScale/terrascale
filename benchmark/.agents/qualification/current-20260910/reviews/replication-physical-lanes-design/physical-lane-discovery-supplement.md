# Supplement: admitted physical replication-lane discovery

Status: design-ready first slice; runtime implementation remains separately gated.

## Scope

The production sender currently polls one `Engine` per `CausalDriver` in
`crates/terrabase-tbep/src/production_causal.rs::next_frame`, while the server
peer worker in `crates/terrabase-server/src/replication_peers.rs` acquires the
logical/root replication engine for a scope. This misses child physical
outboxes. The first slice enumerates admitted physical lanes and gives each an
independent source origin/sequence cursor. It does not implement cross-file
target visibility, group ACK, or target placement dispatch.

## Existing authority and proposed narrow API

`DatabaseLease::admitted_shard_read_cut` already obtains the current
`RouteParticipantRegistry` cut and returns `AdmittedShardParticipant` values
containing `identity`, `engine`, `scope`, `snapshot`, and routes. Discovery must
reuse that admission authority rather than enumerate filesystem children.

Do not add a `pub(crate)` API: the production peer worker is a separate
`terrabase-server` crate. The existing public
`EngineHost::admitted_shard_read_cut()` / `DatabaseLease::admitted_shard_read_cut()`
is the narrow cross-crate accessor and already returns public
`AdmittedShardReadCut`/`AdmittedShardParticipant` values.

If a named inventory wrapper is useful, it must be public but opaque, with
accessors rather than public mutable fields:

```rust
pub struct ReplicationLaneInventory {
    cut: AdmittedShardReadCut,
}

impl ReplicationLaneInventory {
    pub fn participants(&self) -> &[AdmittedShardParticipant];
    pub fn route_generation(&self) -> Option<u64>;
}
```

Prefer returning the existing `AdmittedShardReadCut` directly for the first
slice. Its participant clones retain the private admission permits, so the
inventory must retain the entire cut/participant values for the lifetime of
all outbox drivers; projecting to raw `Arc<Engine>` is forbidden because it
would release the lease owner while a driver is active.

The implementation captures one current registry/read cut and validates every
participant identity, incarnation, tenant, lane origin, and route ownership.
An unsharded admitted database has `route_generation == None` and must produce
its singleton root lane; missing catalog generation is not an error in that
case. Sharded cuts require the current generation and reject duplicate/stale
identities. No caller-supplied shard, path, or source sequence is accepted.

The production caller is the peer-session setup/rebind path in
`crates/terrabase-server/src/replication_peers.rs`. It should create one
`CausalDriver` per participant in the retained admitted cut, keyed by
`(logical scope, participant lane identity, source origin)`. Each driver polls
that lane Engine’s existing
`replication_publications` API and retains its own cursor/pending-byte window.
The first slice only builds this inventory and isolates cursors; it must not
enable child publication delivery into the existing root-only inbound receiver.
No driver may poll the root on behalf of a child.

## Cursor, fence, and failure behavior

- A lane cursor is source-physical and source-origin-specific; cursors are not
  merged across lanes and source sequence values are never target revisions.
- Rebind/discovery replaces a lane only after validating the exact identity and
  route generation. A stale generation suspends the lane and requires a fresh
  admitted cut.
- Discovery does not advance ACKs. A lane frame remains pending under the
  existing `CausalDriver` retention window until its existing durable receiver
  acknowledgment path completes.
- A child lane unavailable, duplicated, or identity-mismatched fails that
  logical replication admission closed; it must not fall back to root data.
- The retained `AdmittedShardReadCut`/participant admission permits are the
  currentness and lifetime fence. Releasing the cut before drivers stop is a
  correctness failure.
- Existing standalone publication behavior remains unchanged once a lane is
  selected. This slice must not apply a publication to a target yet.

The source lane identity is authenticated from the admitted participant
`FileShardIdentity` and persisted replication origin. Placement epoch and
route generation are admission facts; the TBRP causal prefix is only the
logical scope/placement authorization and does not select a target physical
route. Target route generation is checked independently when inbound dispatch
is implemented.

## Explicitly deferred dependent pipeline

### Target dispatch

Inbound `TBRP` currently enters the server worker through
`runtime.acquire_replication(&scope)` and is applied by the causal receiver.
After discovery, inbound dispatch still needs to resolve the current *target*
`RouteParticipantRegistry` and route each publication record by target key.
Source shard IDs and source sequence maps are provenance only; target routing
may repartition keys and has an independent route-generation domain.

### Cross-file relation and receipts

The source already persists `CrossFileIdentity`, completion/visible metadata,
and per-participant source publication events. A later group adapter must
reconstruct changed participant publications from each admitted physical
outbox, preserve untouched participants from the baseline map, and reuse the
existing phases. It must not add a parallel wire manifest.

Target application needs a hidden replicated-intent preparation seam that
preserves source origin dot, HLC, commit ULID, context, fingerprint, and
private payload without usage billing. Target-local install receipts, not
source sequences, form the target visible frontier.

Source lane ACK/cursor advancement for a cross-file group is deferred until
all mapped target participant prepares, target decision, target installs, and
target-local visible completion are durable. Partial staging, crash, or restart
leaves all required lane frames retryable. Recovery must reconcile target-local
active/committed operations before target admission.

## First-slice acceptance tests

Own the focused tests beside the implementation plus a server peer setup test:

1. A routed database with root and child files discovers two distinct lanes;
   each driver reads only its own publication history.
2. A duplicate lane identity, stale route generation, foreign incarnation, or
   missing lane origin is rejected before a driver is created.
3. Rebind replaces a lane only with a fresh admitted generation; old cursor
   state cannot read the new lane.
4. A child publication is emitted with the child origin/sequence and is not
   read through the root driver.
5. A control-only/empty lane remains represented and requires an explicit
   receipt in later group processing.
6. Discovery failure does not advance source cursors or retention ACKs.

These tests establish physical-lane discovery only. They must not claim
cross-file remote atomic visibility until target mapping, hidden install,
target-local frontier publication, whole-group retry, and restart recovery are
implemented and verified.

## Owned implementation files

First slice: `src/member/mod.rs`, `src/member/shard_registry.rs`,
`crates/terrabase-server/src/replication_peers.rs`, and the corresponding
focused tests. Preserve existing route/admission ownership and generated
artifacts. Later target dispatch/receipt work additionally owns the causal
receiver and Engine participant-preparation seam; it is not part of this
discovery change.
