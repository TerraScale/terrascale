//! Integration-test target source for the observability contract.
//!
//! Cargo does not discover nested test directories automatically.  The root
//! test manifest should include `contract` as a module when the observability
//! module is exported from the engine crate.

mod contract;
