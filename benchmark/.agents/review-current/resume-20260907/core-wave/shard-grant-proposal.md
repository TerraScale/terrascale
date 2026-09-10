# Split grant producer and shard-lane implementation brief

Decision draft: `docs/adr/0048-shard-replication-and-split-admission.md`.
Status: proposed; no issuer/runtime activation implemented by this packet.

## Source-backed boundaries

- `terrabase-cluster/src/production.rs::DatabaseRouteScope` has only logical IDs.
- `wal_route.rs::{WalAssignment,DurableWalAssignmentSource,WalReplicaBinding}`
  load and authenticate one database lane, not a child file.
- `wal_wire.rs::{ScopedWalBatch,ScopedWalAck}`, `wal_snapshot.rs`, `wal_driver.rs`
  and `wal_quic.rs` must agree with transport dispatch and engine selection.
- `terrabase-tbep/src/registry.rs::BoundScope` is also customer authorization;
  changing it globally would mix unrelated public backbone and internal lanes.
  Add a separate internal bound shard identity for `production_causal.rs`.
- `terrabase-server/src/production_data_runtime.rs::build_peer_databases` and
  `build_physical_driver` select the parent engine. `replication_peers.rs` and
  production progress/wait consumers must become lane qualified together.
- `ts-orchestrator/src/orchestrator/autoscale/autoscale.go::DatabaseProof` has
  replication watermarks but no split trigger/range. `productionAutoscale`
  applies Region/member count; it cannot supply authenticated split input.
- `placementtransition/types.go::Request` and engine.proto's
  `PlacementDatabaseAssignment` bind database placement, not child admission.

## Ready bounded source slice for release

Approve lane identity plumbing in existing physical replication callers first:
introduce `ShardLaneIdentity` beside `DatabaseRouteScope`; require it in
WalAssignment/load_assignment/WalReplicaBinding, ScopedWalBatch/ScopedWalAck and
snapshot targeting; propagate it through existing physical dispatch to the
engine's persisted identity. Replace database-only wire directly and update
producer/consumer fixtures in one coordinated batch. Keep public BoundScope
unchanged. Stop at an explicit fail-closed missing-child-registration boundary;
do not create a source-engine fallback. This is active-path strengthening,
not a new dormant signer. Root must assign a single wire owner and coordinate
identity changes with shard_serving_runtime's durable file identity work.

The next separately assignable slice is causal lane propagation and progress/
durability keys, after physical identity is fixed. No target serving activation
is part of either slice. Runtime registration then consumes real baseline and
lane evidence from the other workers' agreed contracts.

## Producer implementation release gate

Do not land a standalone `shardtransition.Issuer`. Release it only in a batch
with `productionShardTransition` in cmd/orchestrator, its production reconcile
registration in runtime_composition.go, authenticated Candidate/Admit/Status
server endpoints, durable accepted grants, and recovery callers. Use existing
authorityjournal fencing and enrolled receipt-key resolution, with a separate
split signature domain and operation keyspace. A missing endpoint or unknown
receipt is a failure, not permission to synthesize progress.

Candidate provenance and exact grant/receipt field distinctions are in ADR
0048. Receipt baseline format/digest must be finalized by the retained snapshot
owner. Original origin/sequence provenance must survive transfer because
conditional replay's GlobalDurable confirmation uses it. Do not collapse
source publication, target publication, causal vector or physical WAL units.

## Review requests

Core audit: verify complete file identity propagation boundaries, source/target
receipt non-substitutability, parent proof authority limits, and lifecycle CAS.
Wire coordinator: choose producer-owned new internal versions and publish
vectors/pinned consumers atomically; no shared proto/generated edits by this
worker. Root: approve bounded identity batch before authorizing issuer batch.
