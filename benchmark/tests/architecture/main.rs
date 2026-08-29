//! Integration test binary for the P8 architecture gates (GOAL.md
//! 'P8: close qualification and performance': dependency-direction,
//! extension-reference-feature, and forbidden-central-dispatch gates).
//!
//! Cargo compiles `tests/<directory>/main.rs` as one test target, so the
//! focused gate files in this directory are declared as modules here.
//! Every gate encodes the repository's committed truth at freeze time and
//! names the authority for each rule. Amendments to any frozen table
//! require a review note in the changing commit that cites the accepting
//! record (an ADR or a charter PD id).

mod dependency_direction;
mod extension_reference_features;
mod forbidden_imports;

mod fsx;
