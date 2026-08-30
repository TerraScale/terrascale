//! Integration test binary for native backup and recovery construction paths.
//!
//! Cargo compiles `tests/<directory>/main.rs` as one test target, so the
//! focused files in this directory are declared as modules here.

mod backup_public_construction;
mod compaction_public;
mod restore_public;
mod tombstone_retention_public;
