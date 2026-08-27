//! Revisions completeness (GAP-F05, TerraBaseV2#74).
//!
//! Declaration-only module root wired by the integrator so the GAP-F05
//! packet owns this file's contents without editing `src/families/mod.rs`.
//! The packet implements collection revision policies, the revision bin
//! cleaner, revert-by-change-vector (riding GAP-D02's version contract),
//! and attachment snapshots in revisions, after recording the ownership
//! adjudication (document contract vs family kind). It introduces no
//! behavior until that packet lands.
