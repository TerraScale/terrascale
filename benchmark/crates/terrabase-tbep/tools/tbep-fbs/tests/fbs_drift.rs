//! Regeneration drift gate for the authoritative TBEP FlatBuffers
//! schema and its committed Rust binding.

use std::path::PathBuf;
use std::process::Command;

use tbep_fbs::{PINNED_FLATC_VERSION, PINNED_RUNTIME_VERSION};

fn tool_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
}

#[test]
fn compiler_and_runtime_versions_match() {
    assert_eq!(PINNED_FLATC_VERSION, PINNED_RUNTIME_VERSION);
}

#[test]
fn committed_rust_binding_matches_pinned_regeneration() {
    let output = Command::new("bash")
        .arg(tool_root().join("generate.sh"))
        .arg("--check")
        .current_dir(tool_root())
        .output()
        .expect("the pinned FlatBuffers generator script runs");

    assert!(
        output.status.success(),
        "pinned FlatBuffers regeneration drifted\nstdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr),
    );
}
