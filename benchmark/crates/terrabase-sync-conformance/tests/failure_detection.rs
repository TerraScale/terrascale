//! The failure-detection tests (SE-05): the reference harness must
//! detect non-conformance — a corrupted wire byte, a tampered budget
//! expectation, an unknown step kind, an unparseable vector, a missing
//! category — and report it as a typed failure instead of panicking or
//! passing silently. Every mutation runs against a disposable copy of
//! the corpus; the committed corpus is never touched.

use std::fs;
use std::path::Path;
use std::path::PathBuf;

use serde_json::Value;
use terrabase_sync_conformance::ConformanceHarness;
use terrabase_sync_conformance::CorpusManifest;
use terrabase_sync_conformance::EXTENSION_CATEGORIES;

/// A disposable copy of the corpus, removed when the guard drops.
struct TempCorpus {
    dir: PathBuf,
    parent: PathBuf,
}

impl TempCorpus {
    /// Copies the corpus under a unique temporary parent, preserving
    /// the corpus's leaf name: the corpus identity a manifest entry
    /// names is the directory's name.
    fn copy_of(corpus: &Path, tag: &str) -> Self {
        let leaf = corpus
            .file_name()
            .map_or_else(|| "corpus".to_string(), |name| name.to_string_lossy().into_owned());
        let parent = std::env::temp_dir().join(format!(
            "terrabase-se05-fail-{}-{}",
            std::process::id(),
            tag
        ));
        let _ = fs::remove_dir_all(&parent);
        let dir = parent.join(leaf);
        fs::create_dir_all(&dir).expect("temp dir creates");
        for entry in fs::read_dir(corpus).expect("corpus readable").flatten() {
            let path = entry.path();
            if path.extension().is_some_and(|e| e == "json") {
                fs::copy(&path, dir.join(entry.file_name())).expect("vector copies");
            }
        }
        Self { dir, parent }
    }

    fn path(&self) -> &Path {
        &self.dir
    }

    fn harness(&self) -> ConformanceHarness {
        ConformanceHarness::new(&self.dir)
    }

    /// Reads, mutates, and rewrites one vector file.
    fn edit(&self, file: &str, edit: impl FnOnce(&mut Value)) {
        let path = self.dir.join(file);
        let mut value: Value =
            serde_json::from_str(&fs::read_to_string(&path).expect("vector readable"))
                .expect("vector parses");
        edit(&mut value);
        fs::write(&path, serde_json::to_string(&value).expect("serializes"))
            .expect("vector writable");
    }
}

impl Drop for TempCorpus {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.parent);
    }
}

fn committed_corpus() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../terrabase-sync-core/vectors/terrascale.sync.v2")
}

fn committed_extension_corpus() -> PathBuf {
    ConformanceHarness::adr0024_corpus_dir()
}

/// A disposable copy of both corpora under a kit harness: the run
/// shape of the committed manifest gate.
fn kit_over_temp_copies(tag: &str) -> (TempCorpus, TempCorpus, ConformanceHarness) {
    let base = TempCorpus::copy_of(&committed_corpus(), &format!("{tag}-base"));
    let extension = TempCorpus::copy_of(&committed_extension_corpus(), &format!("{tag}-ext"));
    let harness =
        ConformanceHarness::new(base.path()).with_extension(extension.path().to_path_buf());
    (base, extension, harness)
}

fn committed_manifest() -> CorpusManifest {
    let text = fs::read_to_string(
        PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("fixtures/manifest.json"),
    )
    .expect("the committed manifest is readable");
    CorpusManifest::from_json_str(&text).expect("the committed manifest parses")
}

#[test]
fn a_corrupted_wire_byte_fails_byte_exactness() {
    let corpus = TempCorpus::copy_of(&committed_corpus(), "wire");
    corpus.edit("fresh-session.json", |value| {
        let wire = value["steps"][0]["client_frame"]["wire"]
            .as_str()
            .expect("the recorded wire")
            .to_string();
        let (first, rest) = wire.split_at(1);
        let flipped = format!("{}{rest}", if first == "0" { "1" } else { "0" });
        value["steps"][0]["client_frame"]["wire"] = Value::String(flipped);
    });
    let report = corpus.harness().run().expect("the harness runs over the mutated corpus");
    assert!(!report.passed(), "a corrupted wire byte must fail the run");
    let vector = report.vectors().iter().find(|v| v.category() == "fresh-session").unwrap();
    assert!(
        vector.failures().iter().any(|f| f.check() == "frame_encode_bytes_exact"),
        "the failure names byte exactness: {:#?}",
        vector.failures()
    );
    assert!(vector.aborted(), "a failed step aborts the rest of its vector");
    assert_eq!(report.vectors().len(), 9, "the other vectors still run after one fails");
}

#[test]
fn a_tampered_budget_recount_fails_the_accounting_check() {
    let corpus = TempCorpus::copy_of(&committed_corpus(), "budget");
    corpus.edit("repeated-change-delta.json", |value| {
        for step in value["steps"].as_array_mut().expect("steps") {
            if let Some(budget) = step.get_mut("budget")
                && budget.get("bytes_per_delivered_change").is_some()
            {
                let told = budget["bytes_per_delivered_change"].as_u64().expect("recount");
                budget["bytes_per_delivered_change"] = serde_json::json!(told + 1);
                return;
            }
        }
        panic!("the repeated-change vector carries a per-change budget step");
    });
    let report = corpus.harness().run().expect("the harness runs");
    assert!(!report.passed(), "a lying per-change recount must fail the run");
    let vector = report.vectors().iter().find(|v| v.category() == "repeated-change-delta").unwrap();
    assert!(
        vector.failures().iter().any(|f| f.check() == "budget_bytes_per_delivered_change"),
        "the failure names the per-change accounting: {:#?}",
        vector.failures()
    );
}

#[test]
fn an_unknown_step_kind_fails_closed() {
    let corpus = TempCorpus::copy_of(&committed_corpus(), "step");
    corpus.edit("region-route-change.json", |value| {
        value["steps"]
            .as_array_mut()
            .expect("steps")
            .push(serde_json::json!({ "mystery_step": { "wire": "00" } }));
    });
    let report = corpus.harness().run().expect("the harness runs");
    assert!(!report.passed(), "an unknown step kind must fail the run");
    let vector = report.vectors().iter().find(|v| v.category() == "region-route-change").unwrap();
    assert!(
        vector.failures().iter().any(|f| f.check() == "step_kind_known"),
        "the failure names the step vocabulary: {:#?}",
        vector.failures()
    );
}

#[test]
fn an_unparseable_vector_file_is_a_reported_failure() {
    let corpus = TempCorpus::copy_of(&committed_corpus(), "parse");
    fs::write(corpus.path().join("fresh-session.json"), "not json {").expect("vector writable");
    let report = corpus.harness().run().expect("a broken file is a report, not a panic");
    assert!(!report.passed(), "an unparseable vector must fail the run");
    let vector = report.vectors().iter().find(|v| v.category() == "fresh-session").unwrap();
    assert!(
        vector.failures().iter().any(|f| f.check() == "corpus_file_parses"),
        "the failure names the file-level parse: {:#?}",
        vector.failures()
    );
}

#[test]
fn a_missing_category_fails_the_run() {
    let corpus = TempCorpus::copy_of(&committed_corpus(), "category");
    fs::remove_file(corpus.path().join("trigger-notification.json")).expect("vector removable");
    let report = corpus.harness().run().expect("the harness runs");
    assert!(!report.passed(), "a corpus missing a category must fail the run");
    let value: Value = serde_json::from_str(&report.to_json_string()).expect("parses");
    let problems = value["summary"]["problems"].as_array().expect("problems");
    assert!(
        problems.iter().any(|p| p.as_str().is_some_and(|p| p.contains("trigger-notification"))),
        "the summary problems name the missing category: {problems:?}"
    );
}

#[test]
fn a_manifest_gated_run_fails_a_mutated_corpus() {
    let (_base, extension, harness) = kit_over_temp_copies("gate");
    let target = extension.path().join("slow-consumer-overload.json");
    let text = fs::read_to_string(&target).expect("vector readable");
    fs::write(&target, format!("{text}\n")).expect("vector writable");
    let report = harness.run_with_manifest(&committed_manifest()).expect("the harness runs");
    assert!(!report.passed(), "a mutated corpus must fail the manifest gate");
    let value: Value = serde_json::from_str(&report.to_json_string()).expect("parses");
    assert_eq!(value["manifest"]["status"], Value::String("failed".to_string()));
    let vector =
        report.vectors().iter().find(|v| v.category() == "slow-consumer-overload").unwrap();
    assert!(
        vector.failures().iter().any(|f| f.check() == "manifest_entry_matches"),
        "the failing vector names its manifest gate: {:#?}",
        vector.failures()
    );
}

#[test]
fn a_corrupted_extension_wire_byte_fails_byte_exactness() {
    let (_base, extension, harness) = kit_over_temp_copies("ext-wire");
    extension.edit("authorization-reset.json", |value| {
        let Some(step) = value["steps"]
            .as_array_mut()
            .expect("steps")
            .iter_mut()
            .find(|step| step.get("server_frame").is_some())
        else {
            panic!("the authorization-reset vector carries a server frame");
        };
        let wire = step["server_frame"]["wire"].as_str().expect("the recorded wire").to_string();
        let (first, rest) = wire.split_at(1);
        let flipped = format!("{}{rest}", if first == "0" { "1" } else { "0" });
        step["server_frame"]["wire"] = Value::String(flipped);
    });
    let report = harness.run().expect("the harness runs over the mutated corpus");
    assert!(!report.passed(), "a corrupted extension wire byte must fail the run");
    let vector =
        report.vectors().iter().find(|v| EXTENSION_CATEGORIES.contains(&v.category())).unwrap();
    assert!(
        vector.failures().iter().any(|f| f.check() == "frame_encode_bytes_exact"),
        "the failure names byte exactness: {:#?}",
        vector.failures()
    );
}

#[test]
fn a_missing_extension_category_fails_the_run() {
    let (_base, extension, harness) = kit_over_temp_copies("ext-category");
    fs::remove_file(extension.path().join("authorization-reset.json")).expect("vector removable");
    let report = harness.run().expect("the harness runs");
    assert!(!report.passed(), "an extension corpus missing a category must fail the run");
    let value: Value = serde_json::from_str(&report.to_json_string()).expect("parses");
    let problems = value["summary"]["problems"].as_array().expect("problems");
    assert!(
        problems.iter().any(|p| p.as_str().is_some_and(|p| p.contains("authorization-reset"))),
        "the summary problems name the missing extension category: {problems:?}"
    );
}
