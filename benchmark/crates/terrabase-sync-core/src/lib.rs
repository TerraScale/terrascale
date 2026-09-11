//! `terrabase-sync-core`: the owner of the `TerraBase` sync result
//! views.
//!
//! Per S-04 (decision D-38) the crate's per-field CRDT document
//! materialization and its `SERVER_PEER_ID` actor model are deleted:
//! journal events are no longer folded into last-writer-wins field
//! registers, and no delta/snapshot payload codec over `terrabase-crdt`
//! and `terrabase-causal` is spoken here. What remains, and what the
//! sync engine rebuilds on, is:
//!
//! - [`native`]: the transport-independent result-view state machine —
//!   authorized ordered results, revisions and opaque resume
//!   checkpoints, bounded deltas and coalescing delivery queues,
//!   subscription expansion, and optimistic mutation presentation. A
//!   view is an ordered, authorized projection; its revisions and
//!   checkpoints are delivery facts, not database causal positions.
//!
//! Scope limits, stated exactly:
//!
//! - Server-authoritative only: there is no client write path to
//!   materialized state in this crate.
//! - The result-view state is a pure function of the snapshots and
//!   deltas applied to it, never of arrival order within a revision:
//!   duplicates are idempotent, gaps and reorders are typed resets.
//! - Wire integrity is the transport's contract; the public
//!   `terrascale.sync.v1` frame discipline is owned by [`wire`].

pub mod native;
pub mod server;
pub mod subscription;
/// Bounded canonical shared-view registry and immutable revision publications.
pub mod view;
/// The bounded MessagePack codec for the public `terrascale.sync.v1` wire.
pub mod wire;
