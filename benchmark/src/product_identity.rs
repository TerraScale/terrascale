//! Product naming and compatibility identifiers.
//!
//! `TerraBase` is the canonical product and `terrabase` is its canonical
//! source/package identity. The rename cutover is complete: consumers must
//! declare the canonical package. Persisted and canonical-hash identifiers
//! are format identities, not branding. Readers must retain them until the
//! corresponding artifacts have been rewritten or expired under ADR 0031.

/// Canonical product name used by new user-facing metadata.
pub const PRODUCT_NAME: &str = "TerraBase";

/// Canonical Cargo package name.
pub const PACKAGE_NAME: &str = "terrabase";

/// Canonical library target used by Rust source imports.
pub const LIBRARY_NAME: &str = "terrabase";

/// The date the N-1 dependency-alias window closed and the canonical
/// package name became the only accepted dependency key.
pub const RENAME_CUTOVER_COMPLETED: &str = "2026-08-30";

/// Persisted identifiers whose bytes remain stable through the product rename.
pub const LEGACY_PERSISTED_IDENTIFIERS: &[&str] = &[
    "TerraBaseV2\\0 canonical-hash prefix",
    "TBV2MLJ1 member-lane journal magic",
    "manifest.tbv2m backup manifest filename",
];

/// Returns whether the canonical product name is active while legacy persisted
/// identities remain explicitly registered for compatibility.
#[must_use]
pub fn canonical_rename_is_active() -> bool {
    PRODUCT_NAME == "TerraBase"
        && PACKAGE_NAME == env!("CARGO_PKG_NAME")
        && LIBRARY_NAME == "terrabase"
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
        assert_eq!(LIBRARY_NAME, "terrabase");
        assert_eq!(RENAME_CUTOVER_COMPLETED, "2026-08-30");
        assert!(LEGACY_PERSISTED_IDENTIFIERS.iter().all(|id| {
            id.contains("TBV2") || id.contains("tbv2") || id.contains("TerraBaseV2")
        }));
        assert!(!LEGACY_PERSISTED_IDENTIFIERS.iter().any(|id| id.contains("EB1")));
    }

    #[test]
    fn the_alias_window_is_closed_everywhere_in_source() {
        // The retired alias must not appear as a dependency key anywhere in
        // the workspace manifests: the cutover date is only meaningful if
        // every source agrees with it.
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
        let mut manifests = vec![root.join("Cargo.toml")];
        for entry in std::fs::read_dir(root.join("crates")).expect("crates directory is readable") {
            let path = entry.expect("crate entry is readable").path().join("Cargo.toml");
            if path.is_file() {
                manifests.push(path);
            }
        }
        manifests.push(root.join("bench/Cargo.toml"));
        manifests.push(root.join("crates/terrabase-studio/Cargo.toml"));
        for path in manifests {
            let manifest = std::fs::read_to_string(&path)
                .unwrap_or_else(|error| panic!("{} is readable: {error}", path.display()));
            assert!(
                !manifest.contains(concat!("terrabase", "_v2")),
                "{} retains the retired dependency alias",
                path.display()
            );
            assert!(
                !manifest.contains(concat!("terrabase", "-v2")),
                "{} retains the retired package identity",
                path.display()
            );
        }
    }
}
