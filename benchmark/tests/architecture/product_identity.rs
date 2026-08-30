//! Gate 4 — canonical product identity and rename-drift gate.
//!
//! The package, library target, workspace dependency keys, and generated
//! client import path are source identities and must remain `terrabase`.
//! Format markers such as `TBV2` are intentionally not scanned here: they
//! are persisted compatibility identities and are registered by
//! `src/product_identity.rs` instead.

use std::fs;
use std::path::{Path, PathBuf};

use crate::fsx;

const RETIRED_SOURCE_IDENTITIES: &[&str] = &[
    concat!("terrabase", " v2"),
    concat!("terrabase", "_v2"),
    concat!("terrabase", "-v2"),
    concat!("terrabase", "v2"),
];

fn manifests() -> Vec<(PathBuf, String)> {
    let root = fsx::repo_root();
    let mut paths = vec![root.join("Cargo.toml")];
    let crates = root.join("crates");
    for entry in fs::read_dir(&crates).expect("crates directory is readable") {
        let path = entry.expect("crate entry is readable").path().join("Cargo.toml");
        if path.is_file() {
            paths.push(path);
        }
    }
    paths.push(root.join("bench/Cargo.toml"));
    paths.push(root.join("crates/terrabase-studio/Cargo.toml"));
    paths.sort();
    paths
        .into_iter()
        .map(|path| (path.clone(), fs::read_to_string(path).expect("manifest is readable")))
        .collect()
}

fn relative(path: &Path) -> String {
    path.strip_prefix(fsx::repo_root()).unwrap_or(path).display().to_string()
}

#[test]
fn package_and_library_targets_use_the_canonical_identity() {
    let root = fsx::read_repo_file("Cargo.toml");
    assert_eq!(fsx::package_name(&root).as_deref(), Some("terrabase"));
    assert_eq!(fsx::library_name(&root).as_deref(), Some("terrabase"));

    for (path, manifest) in manifests() {
        let package = fsx::package_name(&manifest)
            .unwrap_or_else(|| panic!("{} has no package name", relative(&path)));
        assert!(
            package == "terrabase" || package.starts_with("terrabase-"),
            "{} uses non-canonical package identity `{package}`",
            relative(&path)
        );
        for identity in RETIRED_SOURCE_IDENTITIES {
            assert!(
                !manifest.contains(identity),
                "{} retains retired identity `{identity}`",
                relative(&path)
            );
        }
        for dependency in fsx::dependency_keys(&manifest) {
            assert_ne!(
                dependency,
                concat!("terrabase", "_v2"),
                "{} retains the closed dependency alias",
                relative(&path)
            );
            assert_ne!(
                dependency,
                concat!("terrabase", "-v2"),
                "{} retains the closed package identity",
                relative(&path)
            );
        }
    }
}

#[test]
fn generated_client_binding_stays_on_the_canonical_go_package() {
    let proto = fsx::read_repo_file("crates/terrabase-tbep/proto/terrabase/v1/engine.proto");
    assert!(proto.contains("package terrabase.v1;"), "TBEP package identity drifted");
    assert!(
        proto.contains("option go_package = \"terrascale.tech/terrabase-go/terrabase/v1;v1\";"),
        "TBEP Go binding target drifted from the terrabase-go client"
    );

    for path in
        ["terrabase-go/terrabase/v1/engine.pb.go", "terrabase-go/terrabase/v1/engine_grpc.pb.go"]
    {
        let binding = fsx::read_repo_file(path);
        assert!(binding.contains("package v1"), "{path} is not a v1 Go binding");
        assert!(
            !binding.contains(concat!("terrabase", "_v2")),
            "{path} retains the closed source alias"
        );
        assert!(
            !binding.contains(concat!("terrabase", "-v2")),
            "{path} retains the closed package identity"
        );
    }
}
