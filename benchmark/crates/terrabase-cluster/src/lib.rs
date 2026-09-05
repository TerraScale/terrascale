//! Production placement and routing catalogs for the member host.
//!
//! The quorum cluster host, its reads, durability coordination, backup
//! clusters, and the in-process NATS control seam were deleted with the
//! replication runtime they served (GOAL D-35).
//! What remains is the production bootstrap the server uses: member
//! placement, routing, and admission over the real member catalog.

pub mod production;
/// Physical SQLite WAL-frame replication for an in-Region RF=2 follower.
pub mod wal_follower;
/// Authenticated QUIC physical batch and durable receipt exchange.
pub mod wal_quic;
/// Authorized regional physical replication bindings.
pub mod wal_route;
/// Database-scoped physical WAL wire protocol.
pub mod wal_wire;
