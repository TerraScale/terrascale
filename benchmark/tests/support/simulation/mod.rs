//! Test-only support for the Wave-0 E-12 simulation checks.
//!
//! This module is a small reference model. It is deliberately not a stand-in
//! for the replication or sync engines. Once E-5 and E-6 expose their public
//! seams, the ignored adapter test in `tests/simulation_harness.rs` is the
//! place to connect the real implementations.

mod faults;
mod invariants;
mod model;
mod rng;
mod runner;

pub use faults::{FaultKind, FaultPlan, Link, MAX_CLOCK_SKEW_MS};
pub use invariants::assert_model_invariants;
pub use runner::{ReferenceConfig, run_reference};
