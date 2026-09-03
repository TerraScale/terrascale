//! Replication support kernels for the SQLite storage plane
//! (ADR 0041; GOAL.md D-35/D-36/D-45).
//!
//! Intra-region replication is a physical WAL-frame follower of the
//! writer's SQLite file; cross-region replication is a journal tailer
//! shipping committed journal rows with document-level causal
//! metadata. Neither replays a logical per-field CRDT model, so this
//! crate no longer carries one: the per-field last-writer-wins
//! replica model, its exchange codec, and the in-process member
//! stand-ins built on it are deleted (issue #130, ADR 0041).
//!
//! What remains are the transport- and storage-independent kernels
//! those planes reuse:
//!
//! - [`progress`]: durable counter-stream progress — a contiguous
//!   prefix plus bounded received ranges — receiver-owned durable
//!   acknowledgements, and retention guards. This is the frontier
//!   vocabulary the journal tailer, the WAL-frame follower, and the
//!   history pruner exchange (GOAL D-42: the pruner passes consumer
//!   frontiers and the history floor).
//! - [`anti_entropy`]: the deterministic dual-root Merkle comparison,
//!   bounded repair planning, trigger scheduling, the fair repair
//!   queue, and the verified stable-frontier kernels (GOAL D-45:
//!   Merkle anti-entropy over ranges, descended only on mismatch).
//!
//! Both modules are pure decision surfaces: no transport, no storage,
//! no process state. The concrete follower and tailer land with their
//! engine seams.

pub mod anti_entropy;
pub mod progress;

pub use progress::{
    DEFAULT_MAX_GAP_RANGES, DurableAcknowledgement, DurableProgressTracker,
    DurableReplicationProgress, GapInterval, GapRange, MAX_GAP_RANGES, NativeReplicationProgress,
    ProgressCursor, ProgressError, ReplicationCursor, ReplicationProgress, RetentionGuard,
    StableRetentionFrontier,
};
