//! Integration test binary for the recovery seam's public construction
//! paths (BK-01 backup core, BK-03 logical migration).
//!
//! Cargo compiles `tests/<directory>/main.rs` as one test target, so the
//! focused files in this directory are declared as modules here.

mod backup_public_construction;
mod migration_public_contract;
mod restore_public;
