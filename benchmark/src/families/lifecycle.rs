//! Lifecycle and archival execution (GAP-F06, TerraBaseV2#75).
//!
//! Declaration-only module root wired by the integrator so the GAP-F06
//! packet owns this file's contents without editing `src/families/mod.rs`.
//! The packet implements expiry deletion execution, expiration-refresh
//! family behavior, read filtering of expired documents, and archival
//! execution with its backup/retention interactions, recording the
//! execution-owner decision. It introduces no behavior until that packet
//! lands.
