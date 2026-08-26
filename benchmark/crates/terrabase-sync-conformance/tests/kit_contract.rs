//! The conformance-kit contract tests (SE-05): the committed corpus
//! manifest is the machine-readable integrity gate over the SE-04
//! golden-vector corpus. It verifies the live corpus byte-for-byte, is
//! built deterministically from it, and fails closed on unknown schema
//! identities, tampered files, and missing files.

use std::fs;
use std::path::PathBuf;

use serde_json::Value;
use terrabase_sync_conformance::CorpusManifest;
use terrabase_sync_conformance::MANIFEST_SCHEMA;
use terrabase_sync_conformance::ManifestEntry;
use terrabase_sync_conformance::REQUIRED_CATEGORIES;

/// The committed manifest fixture.
fn manifest_path() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("fixtures/manifest.json")
}

/// The SE-04 corpus directory this kit executes.
fn corpus_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../terrabase-sync-core/vectors/terrascale.sync.v2")
}

fn committed_manifest() -> CorpusManifest {
    let text = fs::read_to_string(manifest_path())
        .unwrap_or_else(|e| panic!("the committed manifest must be readable: {e}"));
    CorpusManifest::from_json_str(&text)
        .unwrap_or_else(|e| panic!("the committed manifest must parse: {e}"))
}

#[test]
fn the_committed_manifest_verifies_the_live_corpus() {
    let manifest = committed_manifest();
    assert_eq!(manifest.schema(), MANIFEST_SCHEMA);
    let problems = manifest.verify(&corpus_dir()).expect("the corpus directory is readable");
    assert!(
        problems.is_empty(),
        "the committed manifest must verify the live corpus, problems: {problems:?}"
    );
    let mut categories: Vec<&str> =
        manifest.vectors().iter().map(ManifestEntry::category).collect();
    categories.sort_unstable();
    let mut expected: Vec<&str> = REQUIRED_CATEGORIES.to_vec();
    expected.sort_unstable();
    assert_eq!(categories, expected, "the manifest covers every category exactly once");
}

#[test]
fn manifest_building_round_trips_the_committed_fixture() {
    let built = CorpusManifest::build(&corpus_dir())
        .unwrap_or_else(|e| panic!("the corpus must be manifestable: {e}"));
    let committed_text = fs::read_to_string(manifest_path()).expect("fixture readable");
    let rebuilt: Value =
        serde_json::from_str(&built.to_json_string()).expect("the built manifest serializes");
    let committed: Value = serde_json::from_str(&committed_text).expect("the fixture parses");
    assert_eq!(
        rebuilt, committed,
        "rebuilding the manifest from the live corpus must reproduce the committed fixture exactly"
    );
}

#[test]
fn a_tampered_corpus_file_fails_manifest_verification() {
    let dir = corpus_dir();
    let copy = temp_copy_of(&dir, "tampered");
    let target = copy.join("trigger-notification.json");
    let text = fs::read_to_string(&target).expect("vector readable");
    // Same JSON, different bytes: a trailing newline changes the digest
    // without changing what a harness would parse.
    fs::write(&target, format!("{text}\n")).expect("vector writable");
    let problems = committed_manifest().verify(&copy).expect("the copy is readable");
    assert!(
        problems.iter().any(|p| p.contains("trigger-notification.json") && p.contains("sha256")),
        "a byte-tampered vector must fail its digest, problems: {problems:?}"
    );
}

#[test]
fn a_missing_corpus_file_fails_manifest_verification() {
    let dir = corpus_dir();
    let copy = temp_copy_of(&dir, "missing");
    fs::remove_file(copy.join("reconnect-replay.json")).expect("vector removable");
    let problems = committed_manifest().verify(&copy).expect("the copy is readable");
    assert!(
        problems.iter().any(|p| p.contains("reconnect-replay.json") && p.contains("missing")),
        "a vector the manifest names but the corpus lacks must be reported, problems: {problems:?}"
    );
}

#[test]
fn an_unknown_manifest_schema_fails_closed() {
    let bad_schema = r#"{
        "schema": "terrascale.sync.v2/corpus-manifest@9",
        "protocol": "terrascale.sync.v2",
        "protocol_version": 2,
        "vectors": []
    }"#;
    assert!(
        CorpusManifest::from_json_str(bad_schema).is_err(),
        "an unknown manifest schema fails closed"
    );
    let bad_protocol = r#"{
        "schema": "terrascale.sync.v2/corpus-manifest@1",
        "protocol": "terrascale.sync.v1",
        "protocol_version": 1,
        "vectors": []
    }"#;
    assert!(
        CorpusManifest::from_json_str(bad_protocol).is_err(),
        "a manifest for another protocol fails closed"
    );
}

/// Copies the corpus into a unique temporary directory the test owns.
fn temp_copy_of(corpus: &std::path::Path, tag: &str) -> PathBuf {
    let dir =
        std::env::temp_dir().join(format!("terrabase-se05-kit-{}-{}", std::process::id(), tag));
    let _ = fs::remove_dir_all(&dir);
    fs::create_dir_all(&dir).expect("temp dir creates");
    for entry in fs::read_dir(corpus).expect("corpus readable").flatten() {
        let path = entry.path();
        if path.extension().is_some_and(|e| e == "json") {
            let name = entry.file_name();
            fs::copy(&path, dir.join(name)).expect("vector copies");
        }
    }
    dir
}
