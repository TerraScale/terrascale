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
//! - [`protocol`]: the `terrascale.sync.v2` client-channel frame
//!   envelope discipline — version, frame kind, correlation/causation
//!   identity, idempotency pairing, content digest, fail-closed decode —
//!   and the frame kinds that survive the CRDT deletion (session,
//!   subscription, heartbeat, trigger, region route, error). The
//!   delivery frame kinds that carried CRDT payloads and CRDT
//!   checkpoints are gone with the core; the real journal-tailer
//!   vocabulary is S-04's to rebuild.
//!
//! Scope limits, stated exactly:
//!
//! - Server-authoritative only: there is no client write path to
//!   materialized state in this crate.
//! - The result-view state is a pure function of the snapshots and
//!   deltas applied to it, never of arrival order within a revision:
//!   duplicates are idempotent, gaps and reorders are typed resets.
//! - The frame digest is a correlation-integrity digest (FNV-1a 64),
//!   not a cryptographic guarantee; wire integrity is the transport's
//!   contract.

pub mod native;
pub mod protocol;
pub mod server;
pub mod subscription;

/// The correlation or causation identity of one protocol frame.
/// Monotonic per sender; opaque here.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct FrameId(u64);

impl FrameId {
    /// Creates a frame identity from its numeric form.
    #[must_use]
    pub fn new(value: u64) -> Self {
        Self(value)
    }

    /// The numeric form of the identity.
    #[must_use]
    pub fn value(self) -> u64 {
        self.0
    }
}

/// The correlation-integrity digest of a frame body: FNV-1a 64 over the
/// body's canonical encoding. Not a cryptographic guarantee; wire
/// integrity belongs to the transport.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct FrameDigest(u64);

impl FrameDigest {
    /// The digest of the given bytes (the FNV-1a offset basis seeded
    /// hash).
    fn of(bytes: &[u8]) -> Self {
        let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
        for byte in bytes {
            hash ^= u64::from(*byte);
            hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
        }
        Self(hash)
    }

    /// The numeric form of the digest.
    #[must_use]
    pub fn value(self) -> u64 {
        self.0
    }
}
