//! Production placement and routing catalogs for the member host.
//!
//! The quorum cluster host, its reads, durability coordination, backup
//! clusters, and the in-process NATS control seam were deleted with the
//! replication runtime they served (GOAL D-35).
//! What remains is the production bootstrap the server uses: member
//! placement, routing, and admission over the real member catalog.

#[cfg(all(feature = "local-crypto", feature = "production-fips"))]
compile_error!("select exactly one of `local-crypto` or `production-fips`");

#[cfg(not(any(feature = "local-crypto", feature = "production-fips")))]
compile_error!("select one of `local-crypto` or `production-fips`");

#[cfg(feature = "production-fips")]
pub mod production;
