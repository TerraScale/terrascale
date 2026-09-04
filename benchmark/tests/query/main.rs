//! Integration test binary for the query seam's public construction surface.
//!
//! Cargo compiles `tests/<directory>/main.rs` as one test target, so the
//! focused files in this directory are declared as modules here.

mod analyzer_foundation;
mod bulk_by_query_contract;
mod family_sources_contract;
mod includes_contract;
mod query_public_construction;
mod result_statistics;
mod sql_conformance;
