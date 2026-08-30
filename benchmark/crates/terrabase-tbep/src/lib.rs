//! `terrabase-tbep` — the `TerraBase` Engine Protocol engine server.
//!
//! TBEP is the Go↔Rust process-to-process wire protocol (ADR 0028):
//! gRPC over the protobuf contract in `proto/terrabase/v1/engine.proto`,
//! served by tonic. The retired custom MessagePack and FlatBuffers
//! framings (and their generators) are gone; the closed typed operation
//! set, backbone subscription streams, sync-delivery operations,
//! topology/admin reads, and supervision operations all ride gRPC now.
//!
//! Layout:
//!
//! - [`registry`]: the hand-maintained domain types and closed
//!   vocabularies the hosting seam exchanges (bounds stay fail-closed).
//! - [`value`]: the recursive document value and the shared bounds
//!   discipline.
//! - [`host`]: the hosting seam the engine process wires
//!   ([`host::TbepHost`]) plus the shipped [`host::SyncDeliveryHost`]
//!   over the adopted sync core.
//! - [`grpc`]: the tonic serving layer — prost⇄domain conversion,
//!   metadata discipline, streaming subscriptions, and the UDS and
//!   TLS listeners.
//!
//! Scope limits, stated exactly (the SE-14 record):
//!
//! - The engine crate (`terrabase`) stays synchronous and
//!   zero-dependency; every async transport concern lives in this crate
//!   only.
//! - The Rust engine-backed document/query/family operations and the
//!   engine backbone subscriptions ride the [`host::TbepHost`] seam: the
//!   engine crate's Platform adapter constructors are crate-private by
//!   design (W5-E), so the engine-side wiring of that seam is a named
//!   integrator action. The shipped [`host::SyncDeliveryHost`] serves
//!   the sync-delivery family, supervision, and admin reads over the
//!   real `terrabase-sync-core` today.
//! - No performance claim is made anywhere in this crate
//!   (PD-2026-08-26-03); the per-operation message-size bounds below
//!   hold written definitions, and the conformance corpus recomputes
//!   deterministic artifact values only.

#![deny(unsafe_code)]
#![deny(missing_docs)]

pub mod cluster_host;
pub mod engine_host;
pub mod grpc;
pub mod host;
pub mod registry;
pub mod value;

/// The compiled protobuf contract (prost messages and tonic stubs).
pub mod proto {
    #![allow(clippy::module_name_repetitions, clippy::return_self_not_must_use)]

    /// The `terrabase.v1` package.
    pub mod terrabase {
        /// The `terrabase.v1` package.
        pub mod v1 {
            include!(concat!(env!("OUT_DIR"), "/terrabase.v1.rs"));
        }
    }
}

pub use grpc::TbepGrpcServer;
pub use host::HostingError;
pub use host::SyncDeliveryHost;
pub use host::TbepHost;
pub use value::BodyError;
pub use value::TbepValue;
