//! Regeneration drift gate for the authoritative TBEP FlatBuffers schema
//! and its committed Rust binding.
//!
//! Two independent gates hold the artifact, in this order:
//!
//! 1. **Digest gate (no `flatc`, no network).** The schema + registry
//!    inputs and the committed binding are each named by a pinned BLAKE3
//!    constant in [`tbep_fbs::schema_identity`]. A hand-edited binding, a
//!    schema edit, or a registry edit each fail a named test here before
//!    any compiler is resolved.
//! 2. **Regeneration gate (pinned `flatc`).** `generate.sh --check`
//!    regenerates from the committed schema with the digest-pinned
//!    compiler and requires byte equality, proving the committed binding
//!    is not merely self-consistent but equal to regeneration.
//!
//! The rollback drill at the bottom then proves the stronger property the
//! pins exist for: generation is a pure function of the pinned inputs.

use std::path::PathBuf;
use std::process::Command;

use tbep_fbs::schema_identity::{
    BINDING_PATH, PINNED_BINDING_DIGEST, PINNED_FLATC_ARCHIVE_SHA256, PINNED_FBS_DIGEST,
    PINNED_REGISTRY_DIGEST, PINNED_SCHEMA_DIGEST, PinnedInputs, canonical_schema_digest,
    file_digest,
};
use tbep_fbs::{PINNED_FLATC_VERSION, PINNED_RUNTIME_VERSION};

fn tool_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
}

fn crate_root() -> PathBuf {
    tool_root().join("../..")
}

fn run_generator(args: &[&str]) -> (bool, String, String) {
    let output = Command::new("bash")
        .arg(tool_root().join("generate.sh"))
        .args(args)
        .current_dir(tool_root())
        .output()
        .expect("the pinned FlatBuffers generator script runs");
    (
        output.status.success(),
        String::from_utf8_lossy(&output.stdout).into_owned(),
        String::from_utf8_lossy(&output.stderr).into_owned(),
    )
}

#[test]
fn compiler_and_runtime_versions_match() {
    assert_eq!(PINNED_FLATC_VERSION, PINNED_RUNTIME_VERSION);
}

#[test]
fn the_flatc_pin_in_the_script_and_the_constant_agree() {
    let script = std::fs::read_to_string(tool_root().join("generate.sh"))
        .expect("generate.sh reads");
    let pinned = format!("linux_archive_sha256=\"{PINNED_FLATC_ARCHIVE_SHA256}\"");
    assert!(
        script.contains(&pinned),
        "generate.sh must pin the same flatc archive digest as \
         schema_identity::PINNED_FLATC_ARCHIVE_SHA256"
    );
    let version = format!("flatc_version=\"{PINNED_FLATC_VERSION}\"");
    assert!(
        script.contains(&version),
        "generate.sh must pin the same flatc version as PINNED_FLATC_VERSION"
    );
}

// ---------------------------------------------------------------------------
// Gate 1: the digest pins. No compiler, no network.
// ---------------------------------------------------------------------------

#[test]
fn the_pinned_digest_names_the_committed_schema_and_registry() {
    let inputs = PinnedInputs::load(&crate_root()).expect("the pinned inputs read");
    assert_eq!(
        inputs.digest(),
        PINNED_SCHEMA_DIGEST,
        "the schema/registry revision moved; re-pin \
         schema_identity::PINNED_SCHEMA_DIGEST (and every vector file that \
         records it) deliberately, in the same change"
    );
    assert_eq!(inputs.fbs_digest(), PINNED_FBS_DIGEST);
    assert_eq!(inputs.registry_digest(), PINNED_REGISTRY_DIGEST);
}

#[test]
fn the_committed_binding_matches_its_pinned_digest() {
    let binding = std::fs::read(crate_root().join(BINDING_PATH))
        .expect("the committed generated binding reads");
    assert_eq!(
        file_digest(&binding),
        PINNED_BINDING_DIGEST,
        "the committed Rust binding was hand-edited or regenerated without \
         re-pinning; run tools/tbep-fbs/generate.sh --write and re-pin \
         schema_identity::PINNED_BINDING_DIGEST in the same change"
    );
}

#[test]
fn a_hand_edited_binding_fails_the_digest_gate() {
    let binding = std::fs::read(crate_root().join(BINDING_PATH))
        .expect("the committed generated binding reads");
    let mut edited = binding.clone();
    // Flip one byte inside the generated body: any hand edit, however
    // small, must move the digest.
    let index = edited.len() / 2;
    edited[index] ^= 0x01;
    assert_ne!(file_digest(&edited), PINNED_BINDING_DIGEST);
    // A single appended space is equally detectable.
    edited.clear();
    edited.extend_from_slice(&binding);
    edited.push(b' ');
    assert_ne!(file_digest(&edited), PINNED_BINDING_DIGEST);
}

#[test]
fn a_schema_or_registry_edit_fails_the_digest_gate() {
    let inputs = PinnedInputs::load(&crate_root()).expect("the pinned inputs read");
    let mut schema = inputs.fbs.clone();
    schema.extend_from_slice(b"\n// a deliberate perturbation\n");
    assert_ne!(
        canonical_schema_digest(&schema, &inputs.registry),
        PINNED_SCHEMA_DIGEST,
        "a schema edit must move the pinned revision digest"
    );
    let mut registry = inputs.registry.clone();
    registry.push(b' ');
    assert_ne!(
        canonical_schema_digest(&inputs.fbs, &registry),
        PINNED_SCHEMA_DIGEST,
        "a registry edit must move the pinned revision digest"
    );
}

// ---------------------------------------------------------------------------
// Gate 2: equality with pinned-compiler regeneration.
// ---------------------------------------------------------------------------

#[test]
fn committed_rust_binding_matches_pinned_regeneration() {
    let (ok, stdout, stderr) = run_generator(&["--check"]);
    assert!(
        ok,
        "pinned FlatBuffers regeneration drifted\nstdout:\n{stdout}\nstderr:\n{stderr}"
    );
}

// ---------------------------------------------------------------------------
// The rollback drill: generation is a pure function of the pinned inputs.
// ---------------------------------------------------------------------------

/// A schema perturbation that must change the generated output: one more
/// declared table. It is deliberately *not* added to the `TbepFrame`
/// union, so the perturbation isolates "the compiler emitted one more
/// type" from any wire-shape change.
const PERTURBATION: &[u8] = b"\ntable RollbackDrillProbe { value: long (id: 0); }\n";

#[test]
fn regeneration_restores_byte_identical_bindings_after_a_reverted_perturbation() {
    let inputs = PinnedInputs::load(&crate_root()).expect("the pinned inputs read");
    let work = std::env::temp_dir().join(format!(
        "tbep-fbs-rollback-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |elapsed| elapsed.as_nanos())
    ));
    let schema_copy = work.join("tbep.fbs");
    let out = work.join("out");
    std::fs::create_dir_all(&out).expect("the drill directory creates");

    let regenerate = || -> Vec<u8> {
        let schema = schema_copy.to_string_lossy().into_owned();
        let directory = out.to_string_lossy().into_owned();
        let (ok, stdout, stderr) =
            run_generator(&["--write", "--schema", &schema, "--out-dir", &directory]);
        assert!(
            ok,
            "the drill regeneration must succeed\nstdout:\n{stdout}\nstderr:\n{stderr}"
        );
        std::fs::read(out.join("tbep_generated.rs"))
            .expect("the drill regeneration writes tbep_generated.rs")
    };

    // Baseline: the pinned schema, byte for byte.
    std::fs::write(&schema_copy, &inputs.fbs).expect("the schema copy writes");
    let baseline = regenerate();
    let committed = std::fs::read(crate_root().join(BINDING_PATH))
        .expect("the committed generated binding reads");
    assert_eq!(
        baseline, committed,
        "regeneration from the pinned schema must equal the committed binding"
    );

    // Deliberate perturbation: the generated output must move.
    let mut perturbed = inputs.fbs.clone();
    perturbed.extend_from_slice(PERTURBATION);
    std::fs::write(&schema_copy, &perturbed).expect("the perturbed schema writes");
    let drifted = regenerate();
    assert_ne!(
        drifted, committed,
        "the deliberate schema perturbation must change the generated binding"
    );

    // Rollback: restore the pinned schema bytes and regenerate.
    std::fs::write(&schema_copy, &inputs.fbs).expect("the reverted schema writes");
    let restored = regenerate();
    assert_eq!(
        restored, committed,
        "regenerating after the revert must restore the byte-identical binding"
    );
    assert_eq!(restored, baseline);

    // And it is stable across a second run: no clock, host, or ordering
    // input participates in generation.
    assert_eq!(regenerate(), committed);

    let _ = std::fs::remove_dir_all(&work);
}
