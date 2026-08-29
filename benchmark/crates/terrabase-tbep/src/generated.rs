//! Generated TBEP registry constants, enums, frame bodies, frame-kind table,
//! and operation registry.
//!
//! The content is generated from `schema/tbep-registry.json` by the
//! `tbep-gen` tool (`tools/tbep-gen`); `tests/registry_drift.rs` holds
//! the drift contract: the committed file must equal regeneration
//! byte-for-byte.

// The generated code mirrors one match arm per registry row and one
// emitted body per frame on purpose; the allowances keep the generated
// file faithful to the schema instead of fighting the linter over the
// code's generated shape.
#![allow(clippy::match_same_arms)]
#![allow(clippy::type_complexity)]
#![allow(clippy::too_many_lines)]
#![allow(clippy::unreadable_literal)]

include!("generated/registry.rs");
