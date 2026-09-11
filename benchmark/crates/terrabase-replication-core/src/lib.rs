//! Range-based set reconciliation kernel for the `SQLite` storage plane
//! (ADR 0044; GOAL.md D-35/D-36/D-37/D-45).
//!
//! Intra-region replication is a physical WAL-frame follower of the
//! writer's `SQLite` file; cross-region replication is a journal tailer
//! shipping committed journal rows with document-level causal
//! metadata. Neither replays a logical per-field CRDT model, so this
//! crate no longer carries one: the per-field last-writer-wins
//! replica model, its exchange codec, and the in-process member
//! stand-ins built on it are deleted.
//!
//! What remains is the transport- and storage-independent kernel
//! both planes reuse:
//!
//! - [`anti_entropy`]: incrementally maintained additive fingerprints over
//!   per-origin journal sequence ranges, plus the stateless range-based set
//!   reconciliation protocol from ADR 0044.
//!
//! The module is a pure decision surface: no transport, no storage,
//! no process state. The concrete follower and tailer land with their
//! engine seams.

pub mod anti_entropy;
