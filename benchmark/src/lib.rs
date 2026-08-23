//! `TerraBaseV2` engine foundation.
//!
//! This crate intentionally contains no database behavior yet. The public
//! seams, parity obligations, and evidence rules live in the workspace charter
//! and parity inventory. Feature work must add behavior behind those seams and
//! prove it at the corresponding interface.

#![deny(unsafe_code)]
#![deny(missing_docs)]
#![warn(clippy::all, clippy::pedantic)]

/// The crate version selected by Cargo for evidence and artifact identity.
pub const VERSION: &str = env!("CARGO_PKG_VERSION");

/// The current repository status. This is metadata, not a database feature.
pub const STATUS: &str = "foundation-only";

#[cfg(test)]
mod tests {
    use super::{STATUS, VERSION};

    #[test]
    fn foundation_metadata_is_present() {
        assert_eq!(STATUS, "foundation-only");
        assert!(!VERSION.is_empty());
    }
}
