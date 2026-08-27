//! Compare-exchange scope and completion (GAP-F10, TerraBaseV2#78).
//!
//! Declaration-only module root wired by the integrator so the GAP-F10
//! packet owns this file's contents without editing `src/families/mod.rs`.
//! The packet records the ownership decision (engine-owned cluster
//! transaction primitive vs control-plane) and implements cluster ordering
//! integration through the accepted replication plane, get-many/paging/
//! lazy/async completion, and cleanup semantics. It introduces no behavior
//! until that packet lands.
