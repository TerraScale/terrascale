//! The reference-run tests (SE-05): the Rust reference harness executes
//! the committed SE-04 corpus against the engine's delivery core and
//! emits the versioned conformance report. These tests hold the report
//! contract itself: passing runs, the trigger-notification and
//! repeated-change bandwidth expectations (PD-2026-08-26-13 D1/D3),
//! determinism, host-path hygiene, the documented JSON shape, and the
//! manifest integrity gate.

use std::collections::BTreeSet;
use std::fs;
use std::path::PathBuf;

use serde_json::Value;
use terrabase_sync_conformance::ConformanceHarness;
use terrabase_sync_conformance::CorpusManifest;
use terrabase_sync_conformance::EXTENSION_CATEGORIES;
use terrabase_sync_conformance::REPORT_SCHEMA;
use terrabase_sync_conformance::REQUIRED_CATEGORIES;
use terrabase_sync_conformance::VectorResult;

/// The kit reference run: the base corpus plus the ADR 0024 extension
/// corpus.
fn harness() -> ConformanceHarness {
    ConformanceHarness::kit()
}

fn committed_manifest() -> CorpusManifest {
    let text = fs::read_to_string(
        PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("fixtures/manifest.json"),
    )
    .expect("the committed manifest is readable");
    CorpusManifest::from_json_str(&text).expect("the committed manifest parses")
}

#[test]
fn the_reference_harness_passes_the_committed_corpus() {
    let report = harness().run().expect("the harness runs");
    assert!(report.passed(), "the committed corpus must pass: {:#?}", report.vectors());
    let categories: BTreeSet<&str> = report.vectors().iter().map(VectorResult::category).collect();
    let expected: BTreeSet<&str> =
        REQUIRED_CATEGORIES.iter().chain(EXTENSION_CATEGORIES.iter()).copied().collect();
    assert_eq!(categories, expected, "the run covers every corpus category exactly once");
    assert!(report.vectors().iter().all(|v| v.failures().is_empty()));
}

#[test]
fn trigger_and_bandwidth_expectations_execute() {
    let report = harness().run().expect("the harness runs");
    let trigger = report
        .vectors()
        .iter()
        .find(|v| v.category() == "trigger-notification")
        .expect("the trigger-notification vector ran");
    assert!(
        trigger.check_count("trigger_content_free").is_some_and(|n| n > 0),
        "the content-free rule is checked structurally"
    );
    assert!(
        trigger.check_count("trigger_within_budget").is_some_and(|n| n > 0),
        "the trigger's written budget is checked"
    );
    let repeated = report
        .vectors()
        .iter()
        .find(|v| v.category() == "repeated-change-delta")
        .expect("the repeated-change-delta vector ran");
    for check in [
        "budget_payload_accounting",
        "budget_bytes_per_delivered_change",
        "budget_delta_below_snapshot",
    ] {
        assert!(
            repeated.check_count(check).is_some_and(|n| n > 0),
            "the repeated-change vector must exercise the {check} expectation"
        );
    }
}

#[test]
fn reports_are_deterministic_and_carry_no_host_paths() {
    let first = harness().run().expect("run one");
    let second = harness().run().expect("run two");
    assert_eq!(
        first.to_json_string(),
        second.to_json_string(),
        "two runs over the same corpus produce byte-identical reports"
    );
    let text = first.to_json_string();
    let corpus = harness().corpus_dir().to_string_lossy().to_string();
    assert!(!text.contains(&corpus), "the report never carries the host corpus path");
    let manifest_home = env!("CARGO_MANIFEST_DIR");
    assert!(!text.contains(manifest_home), "the report never carries host directories");
    let report: Value = serde_json::from_str(&text).expect("the report parses");
    for vector in report["vectors"].as_array().expect("vectors") {
        let file = vector["file"].as_str().expect("file name");
        assert!(
            !file.contains('/') && !file.contains('\\'),
            "vector file identities are plain names, not paths: {file}"
        );
    }
}

#[test]
fn the_report_matches_the_documented_schema() {
    let report = harness().run().expect("the harness runs");
    let value: Value = serde_json::from_str(&report.to_json_string()).expect("parses");
    let mut keys: Vec<&str> =
        value.as_object().expect("object").keys().map(String::as_str).collect();
    keys.sort_unstable();
    assert_eq!(
        keys,
        vec!["corpus", "harness", "protocol", "protocol_version", "schema", "summary", "vectors"],
        "the report's top-level shape is exactly the documented schema"
    );
    assert_eq!(value["schema"], Value::String(REPORT_SCHEMA.to_string()));
    assert_eq!(value["protocol"], Value::String("terrascale.sync.v2".to_string()));
    assert_eq!(value["protocol_version"], serde_json::json!(2));
    let summary = &value["summary"];
    let mut summary_keys: Vec<&str> =
        summary.as_object().expect("object").keys().map(String::as_str).collect();
    summary_keys.sort_unstable();
    assert_eq!(
        summary_keys,
        vec![
            "checks_failed",
            "checks_passed",
            "checks_total",
            "problems",
            "status",
            "vectors_failed",
            "vectors_passed",
            "vectors_total",
        ]
    );
    for vector in value["vectors"].as_array().expect("vectors") {
        let mut vector_keys: Vec<&str> =
            vector.as_object().expect("object").keys().map(String::as_str).collect();
        vector_keys.sort_unstable();
        assert_eq!(
            vector_keys,
            vec!["aborted", "category", "checks", "failures", "file", "status"]
        );
    }
}

#[test]
fn a_manifest_gated_run_passes_and_records_the_gate() {
    let report = harness().run_with_manifest(&committed_manifest()).expect("the harness runs");
    assert!(report.passed(), "the manifest-gated run passes: {:#?}", report.vectors());
    let value: Value = serde_json::from_str(&report.to_json_string()).expect("parses");
    assert_eq!(value["manifest"]["status"], Value::String("verified".to_string()));
    for vector in report.vectors() {
        assert!(
            vector.check_count("manifest_entry_matches").is_some_and(|n| n > 0),
            "every vector records its manifest gate check"
        );
    }
}
