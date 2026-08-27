//! Analyzer and term-vector foundation (GAP-Q13, TerraBaseV2#67).
//!
//! Declaration-only module root wired by the integrator so the GAP-Q13
//! packet owns this file's contents without editing `src/query/mod.rs`.
//! The packet records the analyzer foundation decision (the full-text
//! search engine surface — one engine first, recorded as evidence, not
//! preference), types the term-vector contract, and makes the sandbox and
//! bounds rules explicit (no arbitrary code, bounded tokenization). It
//! introduces no behavior until that packet lands.
