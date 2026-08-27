//! Integration test binary for the query seam's public construction surface.
//!
//! Cargo compiles `tests/<directory>/main.rs` as one test target, so the
//! focused files in this directory are declared as modules here.

mod evaluator_parity;
mod includes_contract;
mod query_public_construction;
mod tql_golden_vectors;
mod tql_public;
mod tql_rql_parity_vectors;
