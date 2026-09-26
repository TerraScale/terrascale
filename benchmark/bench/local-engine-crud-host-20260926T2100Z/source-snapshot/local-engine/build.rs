use std::env;
use std::path::Path;
use std::process::Command;

use sha2::{Digest, Sha256};

const PINNED_TURSO: &str = "64b8ef5742fc18937f9c89806c81e3f6475dc7a3";

fn git(turso: &Path, args: &[&str]) -> Vec<u8> {
    let output = Command::new("git")
        .arg("-C")
        .arg(turso)
        .args(args)
        .output()
        .expect("git is required to identify the pinned Turso source");
    assert!(
        output.status.success(),
        "cannot identify Turso source: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    output.stdout
}

fn main() {
    let manifest = env::var("CARGO_MANIFEST_DIR").expect("Cargo manifest directory");
    let turso = Path::new(&manifest).join("../../terrabase/crates/turso");
    let revision_bytes = git(&turso, &["rev-parse", "HEAD"]);
    let revision = String::from_utf8(revision_bytes).expect("Turso revision is UTF-8");
    let revision = revision.trim();
    assert_eq!(
        revision, PINNED_TURSO,
        "Turso source differs from the pinned TerraBase gitlink"
    );
    let status = String::from_utf8(git(
        &turso,
        &["status", "--porcelain", "--untracked-files=all"],
    ))
    .expect("Turso status is UTF-8");
    assert!(
        !status.lines().any(|line| line.starts_with("??")),
        "untracked Turso source cannot enter a reproducible build"
    );
    let diff = git(&turso, &["diff", "--binary", "HEAD", "--"]);
    let diff_sha256 = format!("{:x}", Sha256::digest(&diff));
    let changed = String::from_utf8(git(&turso, &["diff", "--name-only", "HEAD", "--"]))
        .expect("Turso changed paths are UTF-8");
    let mut file_inventory = Vec::new();
    for name in changed.lines() {
        assert!(
            !name.contains(';') && !name.contains('='),
            "Turso changed path cannot be encoded in the receipt"
        );
        let path = turso.join(name);
        let digest = if path.is_file() {
            format!(
                "{:x}",
                Sha256::digest(std::fs::read(path).expect("read changed Turso file"))
            )
        } else {
            "deleted".to_owned()
        };
        file_inventory.push(format!("{name}={digest}"));
    }
    println!("cargo:rustc-env=PINNED_TURSO_REVISION={revision}");
    println!("cargo:rustc-env=TURSO_TRACKED_DIFF_SHA256={diff_sha256}");
    println!("cargo:rustc-env=TURSO_TRACKED_DIRTY={}", !diff.is_empty());
    println!(
        "cargo:rustc-env=TURSO_TRACKED_FILE_INVENTORY={}",
        file_inventory.join(";")
    );
    println!("cargo:rerun-if-changed={}", turso.join("core").display());
    println!(
        "cargo:rerun-if-changed={}",
        turso.join("bindings/rust").display()
    );
    println!(
        "cargo:rerun-if-changed={}",
        turso.join("Cargo.toml").display()
    );
    println!(
        "cargo:rerun-if-changed={}",
        turso.join("Cargo.lock").display()
    );
}
