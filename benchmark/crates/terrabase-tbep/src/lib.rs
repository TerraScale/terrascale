//! `terrabase-tbep` — the `TerraBase` Engine Protocol engine server.
//!
//! TBEP is the custom C#↔Rust process-to-process wire protocol mandated
//! by ADR 0025: generated, verified `FlatBuffers` over authenticated QUIC.
//! The legacy UDS conformance binding retains its positional codec while
//! migration-only consumers are retired; it is not a production listener.
//! The closed typed operation set, backbone
//! subscription streams (including the content-free trigger
//! notifications of PD-2026-08-26-13 D3), sync-delivery frames,
//! topology/admin reads, and supervision frames — served by the engine
//! process over the local Unix-domain-socket binding (the required
//! transport per PD-2026-08-26-11 D1; the QUIC binding is deferred until
//! a networked-engine need is recorded).
//!
//! Layout:
//!
//! - [`codegen`]: the generator behind the shared schema (one source of
//!   truth for the Rust and C# bindings, drift-checked).
//! - [`generated`]: the generated registry and typed frame bodies.
//! - [`codec`]: the body codec discipline (`rmp-serde`, positional
//!   arrays, fail-closed bounds) and the [`codec::TbepValue`] type.
//! - [`envelope`]: the frame envelope — the SE-04 envelope discipline in
//!   its `MessagePack` profile — and the stream framing.
//! - [`host`]: the hosting seam the engine process wires
//!   ([`host::TbepHost`]) plus the shipped [`host::SyncDeliveryHost`]
//!   over the adopted sync core.
//! - [`server`]: the UDS engine server.
//!
//! Scope limits, stated exactly (the SE-14 record, `docs/sync/tbep.md`):
//!
//! - The engine crate (`terrabase`) stays synchronous and
//!   zero-dependency; any async transport concern lives in this crate
//!   only. This packet serves the UDS binding with the standard
//!   library's synchronous listener plus one thread per connection.
//! - The Rust engine-backed document/query/family operations and the
//!   engine backbone subscriptions ride the [`host::TbepHost`] seam: the
//!   engine crate's Platform adapter constructors are crate-private by
//!   design (W5-E), so the engine-side wiring of that seam is a named
//!   integrator action. The shipped [`host::SyncDeliveryHost`] serves
//!   the sync-delivery family, supervision, and admin reads over the
//!   real `terrabase-sync-core` today.
//! - No performance claim is made anywhere in this crate
//!   (PD-2026-08-26-03); the bytes-on-wire budget table holds written
//!   definitions, and the conformance corpus recomputes deterministic
//!   artifact values only.

#![deny(unsafe_code)]
#![deny(missing_docs)]

pub mod cluster_host;
pub mod codec;
pub mod codegen;
pub mod engine_host;
pub mod envelope;
mod flatbuffers_server;
pub mod generated;
pub mod host;
pub mod quic;
pub mod server;

pub use codec::BodyError;
pub use codec::TbepValue;
pub use codec::WireBody;
pub use envelope::EnvelopeError;
pub use envelope::FrameDigest;
pub use envelope::IdempotencyKey;
pub use envelope::TbepFrame;
pub use generated::FrameKind;
pub use generated::TbepBody;
pub use host::HostingError;
pub use host::SyncDeliveryHost;
pub use host::TbepHost;
pub use server::MAX_TENANT_INFLIGHT_OPERATIONS;
pub use server::TbepServer;
pub use server::TbepServerHandle;
