//! Integration test binary for the sharding seam's public construction
//! paths (SH-02, PB-5036/5037).
//!
//! Cargo compiles `tests/<directory>/main.rs` as one test target, so the
//! focused files in this directory are declared as modules here.

mod sharded_query_public_surface;
mod sharding_public_construction;
