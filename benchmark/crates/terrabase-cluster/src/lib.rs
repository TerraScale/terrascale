//! Production placement and routing catalogs for the member host.
//!
//! The quorum cluster host, its reads, durability coordination, backup
//! clusters, and the in-process NATS control seam were deleted with the
//! replication runtime they served (GOAL D-35).
//! What remains is the production bootstrap the server uses: member
//! placement, routing, and admission over the real member catalog.

pub mod production;
