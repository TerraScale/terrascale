//! Public Logical Migration contract tests (BK-03).
//!
//! These tests exercise what an external caller of the public
//! `terrabase_v2::migration` seam can construct and observe without a
//! tenant scope: manifest loading and strict validation, fail-closed
//! rejection of malformed and future-version representations, and the
//! no-direct-V1-file-open rule. The data-bearing conversion behavior needs
//! a Platform-authorized scope, so it is proved by the in-crate suite at
//! `src/migration/tests.rs`.
//!
//! The one valid manifest here is hand-crafted from the documented format:
//! a `document` count of zero bound to one empty segment whose digest is
//! the well-known SHA-256 of no bytes, with the identity precomputed over
//! the non-identity lines.

use std::path::PathBuf;

use terrabase_v2::migration::{MigrationError, MigrationKind, MigrationManifest};

/// The SHA-256 of no bytes.
const EMPTY_SHA256: &str = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
/// The identity of the valid empty manifest this file builds.
const EMPTY_IDENTITY: &str = "b31e90c8b89bc3cab21169796f34fab649480a62a0807deb810ea98dcbcfd81e";

fn location(tag: &str) -> PathBuf {
    std::env::temp_dir().join(format!("tbv2-bk03-public-{tag}-{}", std::process::id()))
}

/// Writes one representation directory holding one valid empty manifest.
fn empty_representation(tag: &str) -> PathBuf {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    std::fs::create_dir_all(&dir).expect("creates the directory");
    std::fs::write(dir.join("segment-000000"), b"").expect("writes the empty segment");
    let manifest = format!(
        "terrabase-v1-export 1\nidentity {EMPTY_IDENTITY}\ncount document 0\nmember segment-000000 0 {EMPTY_SHA256}\n"
    );
    std::fs::write(dir.join("manifest.tbv1m"), manifest).expect("writes the manifest");
    dir
}

#[test]
fn migration_manifest_loads_a_valid_representation() {
    let dir = empty_representation("valid");
    let manifest = MigrationManifest::load(&dir).expect("loads the valid manifest");
    assert_eq!(manifest.format_version(), 1);
    assert_eq!(manifest.identity(), EMPTY_IDENTITY);
    assert_eq!(manifest.count(MigrationKind::Document), 0);
    assert_eq!(manifest.members().len(), 1);
    assert_eq!(manifest.members()[0].name(), "segment-000000");
    assert_eq!(manifest.members()[0].length(), 0);
    assert_eq!(manifest.members()[0].digest(), EMPTY_SHA256);
    assert!(manifest.verify(&dir).is_ok(), "the empty segment verifies");
}

#[test]
fn migration_manifest_rejects_an_unsupported_version() {
    let dir = empty_representation("version");
    let path = dir.join("manifest.tbv1m");
    let body = std::fs::read_to_string(&path).expect("reads");
    std::fs::write(&path, body.replace("terrabase-v1-export 1", "terrabase-v1-export 7"))
        .expect("writes");
    assert_eq!(MigrationManifest::load(&dir).err(), Some(MigrationError::UnsupportedVersion));
}

#[test]
fn migration_manifest_rejects_a_tampered_identity() {
    let dir = empty_representation("identity");
    let path = dir.join("manifest.tbv1m");
    let body = std::fs::read_to_string(&path).expect("reads");
    let tampered = format!(
        "terrabase-v1-export 1\nidentity {}\ncount document 1\nmember segment-000000 0 {EMPTY_SHA256}\n",
        EMPTY_IDENTITY
    );
    assert_ne!(&tampered, &body, "the fact being tampered must change");
    std::fs::write(&path, tampered).expect("writes");
    assert_eq!(
        MigrationManifest::load(&dir).err(),
        Some(MigrationError::ManifestInvalid),
        "an identity that does not bind its counts fails closed"
    );
}

#[test]
fn migration_manifest_rejects_path_traversal_member_names() {
    let dir = empty_representation("traversal");
    let path = dir.join("manifest.tbv1m");
    let manifest = format!(
        "terrabase-v1-export 1\nidentity {EMPTY_IDENTITY}\ncount document 0\nmember ../../etc/passwd 0 {EMPTY_SHA256}\n"
    );
    std::fs::write(&path, manifest).expect("writes");
    assert_eq!(
        MigrationManifest::load(&dir).err(),
        Some(MigrationError::ManifestInvalid),
        "only canonical segment names are valid"
    );
}

#[test]
fn migration_manifest_rejects_unknown_lines_and_kinds() {
    let dir = empty_representation("unknown-line");
    let path = dir.join("manifest.tbv1m");
    std::fs::write(
        &path,
        format!(
            "terrabase-v1-export 1\nidentity {EMPTY_IDENTITY}\nsurprise 1\ncount document 0\nmember segment-000000 0 {EMPTY_SHA256}\n"
        ),
    )
    .expect("writes");
    assert_eq!(MigrationManifest::load(&dir).err(), Some(MigrationError::ManifestInvalid));

    let dir = empty_representation("unknown-kind");
    let path = dir.join("manifest.tbv1m");
    std::fs::write(
        &path,
        format!(
            "terrabase-v1-export 1\nidentity {EMPTY_IDENTITY}\ncount quantum-state 4\nmember segment-000000 0 {EMPTY_SHA256}\n"
        ),
    )
    .expect("writes");
    assert_eq!(
        MigrationManifest::load(&dir).err(),
        Some(MigrationError::ManifestInvalid),
        "unknown record kinds fail closed"
    );
}

#[test]
fn logical_migration_makes_no_direct_v1_file_open_claim() {
    // The seam's only input is a representation directory: a directory
    // without a representation manifest is refused, and no API accepts a V1
    // storage file. V2 never opens V1 files directly.
    let dir = location("no-manifest");
    std::fs::remove_dir_all(&dir).ok();
    std::fs::create_dir_all(&dir).expect("creates");
    std::fs::write(dir.join("RavenDB.exe.db"), b"\x00v1-page-bytes").expect("writes");
    assert_eq!(MigrationManifest::load(&dir).err(), Some(MigrationError::ManifestInvalid));

    // A missing directory is also a typed refusal, never a fallback to
    // interpreting raw files.
    assert_eq!(
        MigrationManifest::load(&location("absent")).err(),
        Some(MigrationError::ManifestInvalid)
    );
}
