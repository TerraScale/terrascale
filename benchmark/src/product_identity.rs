//! Product naming and compatibility identifiers.
//!
//! `TerraBase` is the canonical product and Cargo package name. Existing Rust
//! consumers that still import `terrabase_v2` may retain that local dependency
//! alias during the N-1 source window; the published library target itself is
//! `terrabase`. Persisted and canonical-hash identifiers are format identities,
//! not branding. Readers must retain them until the corresponding artifacts
//! have been rewritten or expired under ADR 0031.

/// Canonical product name used by new user-facing metadata.
pub const PRODUCT_NAME: &str = "TerraBase";

/// Canonical Cargo package name.
pub const PACKAGE_NAME: &str = "terrabase";

/// Previous Rust dependency name that N-1 consumers may retain as a local
/// Cargo alias for the canonical `terrabase` package.
pub const PREVIOUS_CRATE_ALIAS: &str = "terrabase_v2";

/// Persisted identifiers whose bytes remain stable through the product rename.
pub const LEGACY_PERSISTED_IDENTIFIERS: &[&str] = &[
    "TerraBaseV2\\0 canonical-hash prefix",
    "TBV2FORMAT storage format magic",
    "TBV2ROOT root-file magic",
    "TBV2MANI root manifest magic",
    "TBV2LSM1 LSM format marker",
    "TBV2EB1 encrypted backup-bundle magic",
    "TBV2MLJ1 member-lane journal magic",
    "manifest.tbv2m backup manifest filename",
    ".tbv2b backup bundle extension",
];

/// Returns whether the canonical product name is active while legacy persisted
/// identities remain explicitly registered for compatibility.
#[must_use]
pub fn canonical_rename_is_active() -> bool {
    PRODUCT_NAME == "TerraBase"
        && PACKAGE_NAME == env!("CARGO_PKG_NAME")
        && !LEGACY_PERSISTED_IDENTIFIERS.is_empty()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn canonical_metadata_and_legacy_formats_have_one_explicit_stage() {
        assert!(canonical_rename_is_active());
        assert_eq!(PRODUCT_NAME, "TerraBase");
        assert_eq!(PACKAGE_NAME, "terrabase");
        assert_eq!(PREVIOUS_CRATE_ALIAS, "terrabase_v2");
        assert!(
            LEGACY_PERSISTED_IDENTIFIERS
                .iter()
                .all(|id| id.contains("TBV2") || id.contains("tbv2") || id.contains("TerraBaseV2"))
        );
    }
}
