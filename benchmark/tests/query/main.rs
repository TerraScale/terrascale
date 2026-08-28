//! Integration test binary for the query seam's public construction surface.
//!
//! Cargo compiles `tests/<directory>/main.rs` as one test target, so the
//! focused files in this directory are declared as modules here.

mod analyzer_foundation;
mod bulk_by_query_contract;
mod evaluator_parity;
mod family_sources_contract;
mod includes_contract;
mod query_public_construction;
mod result_statistics;
mod spatial_foundation;
mod tql_golden_vectors;
mod tql_public;
mod tql_rql_parity_vectors;
mod vector_conformance_vectors;
mod vector_search;
