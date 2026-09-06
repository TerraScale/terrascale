//! `terrabase-tbep` — the `TerraBase` Engine Protocol engine server.
//!
//! TBEP is the Go↔Rust process-to-process wire protocol (ADR 0025):
//! gRPC over the protobuf contract in `proto/terrabase/v1/engine.proto`,
//! served by tonic. The document/query/sync-session data-plane RPCs were
//! deleted with the S-03 TBEP reduction (the public API owns the data
//! plane); the backbone subscription streams, schemas and migrations,
//! replication observation, usage, topology/admin
//! reads, and supervision operations ride gRPC.
//!
//! Layout:
//!
//! - [`catalog`]: the database catalog (S-01) — lazy open of one
//!   [`engine_host::EngineHost`] per `tenant`/`project`/`database`
//!   under a root, with an RSS-sized LRU bound and idle-close.
//! - [`registry`]: the hand-maintained domain types and closed
//!   vocabularies the hosting seam exchanges (bounds stay fail-closed).
//! - [`value`]: the recursive document value and the shared bounds
//!   discipline.
//! - [`host`]: the hosting seam the engine process wires
//!   ([`host::TbepHost`]) plus the shipped [`host::SyncDeliveryHost`]
//!   over the adopted sync core.
//! - [`member_host`]: the explicitly enabled TB-1a composition over one
//!   durable engine publication journal. The production harness continues to
//!   construct `EngineHost` until the later host-cutover stage.
//! - [`replication`]: the cross-region journal tailer's in-process form
//!   (D-35/D-36) — pull origin rows, apply them on a peer, idempotently;
//!   the QUIC lanes drive the same apply path.
//! - [`replication_lanes`]: the cross-region lanes (S-05, D-45) — the
//!   tailer's pull/apply path driven over a fault-injectable
//!   `MemberTransport`, one frame per pull with a credit window.
//! - [`wal_follower`]: the intra-region WAL-frame follower (S-05, D-35)
//!   — the writer's WAL tail, the follower's validated append with an
//!   `fdatasync` ack, fold-and-adopt across the per-wave generations,
//!   and promotion by opening the follower's location.
//! - [`anti_entropy`]: the journal-independent catch-up (S-05, D-45) —
//!   range-based set reconciliation over the kept replication-core kernels,
//!   bounded repair through the deterministic merge, and snapshot bootstrap
//!   from the region-local snapshot file set.
//! - [`grpc`]: the tonic serving layer — prost⇄domain conversion,
//!   metadata discipline, streaming subscriptions, and the UDS and
//!   TLS listeners.
//!
//! Scope limits, stated exactly (the SE-14 record):
//!
//! - The engine crate (`terrabase`) stays synchronous and
//!   zero-dependency; every async transport concern lives in this crate
//!   only.
//! - [`member_host::MemberHost`] combines the engine host with
//!   backbone delivery derived from that engine's durable publication
//!   journal. Construction requires the TB-1a option; the production harness
//!   remains on `EngineHost` during this staged landing.
//! - No performance claim is made anywhere in this crate
//!   (PD-2026-08-26-03); the per-operation message-size bounds below
//!   hold written definitions, and the conformance corpus recomputes
//!   deterministic artifact values only.

#![deny(unsafe_code)]
#![deny(missing_docs)]

pub mod anti_entropy;
pub mod auth_bootstrap;
pub mod catalog;
pub mod engine_host;
pub mod grpc;
pub mod host;
pub mod member_host;
pub mod placement;
pub mod production_causal;
pub mod reactive;
pub mod registry;
pub mod replication;
pub mod replication_lanes;
pub mod value;
pub use terrabase_cluster::wal_follower;

/// The compiled protobuf contract (prost messages and tonic stubs).
pub mod proto {
    // Ecosystem-generated code (protox + tonic-build): not first-party
    // shape, so first-party lint discipline does not apply to it.
    #![allow(missing_docs)]
    #![allow(clippy::all, clippy::pedantic, clippy::nursery)]

    /// The `terrabase.v1` package.
    pub mod terrabase {
        /// The `terrabase.v1` package.
        pub mod v1 {
            include!(concat!(env!("OUT_DIR"), "/terrabase.v1.rs"));
        }
    }
}

pub use anti_entropy::{
    DurableJournalRow, JournalRowSource, RepairDecision, SnapshotBootstrap, journal_end,
    reconcile_journal_rows, repair_once, repair_once_bounded, snapshot_bootstrap,
};
pub use grpc::TbepGrpcServer;
pub use host::HostingError;
pub use host::SyncDeliveryHost;
pub use host::TbepHost;
pub use member_host::{MemberHost, MemberHostOptions};
pub use value::BodyError;
pub use value::TbepValue;
