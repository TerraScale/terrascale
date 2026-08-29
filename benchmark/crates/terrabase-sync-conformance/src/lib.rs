//! `terrabase-sync-conformance`: the SE-05 protocol conformance kit.
//!
//! The SE-04 golden-vector corpus
//! (`crates/terrabase-sync-core/vectors/terrascale.sync.v2/`) is the
//! compatibility contract of `terrascale.sync.v2` (PD-2026-08-26-12
//! D1): every SDK implementation — TypeScript, C#, any later language —
//! passes the same vectors in its own CI, natively, with no Rust
//! toolchain. This crate is the kit that makes that runnable and
//! verifiable:
//!
//! - [`ConformanceHarness`] is the **Rust reference harness**: it
//!   executes every corpus file against the engine's delivery core
//!   (`terrabase-sync-core`: the real `SyncCore` and protocol codec)
//!   and produces the versioned [`ConformanceReport`].
//! - [`CorpusManifest`] is the **integrity manifest**: SHA-256 digests
//!   over every corpus file, so an SDK's CI proves it is testing the
//!   exact committed corpus before running a single vector.
//! - The `terrabase-sync-conformance` binary runs the harness from a
//!   command line and emits the report as JSON.
//!
//! The per-language harness contract — the corpus layout, the step
//! vocabulary, the exact check names, and the report schema an SDK
//! harness emits — is documented in
//! `docs/sync/terrascale-sync-v2-conformance.md`. Conformance, not
//! compilation, is the compatibility guarantee (PD-2026-08-26-12 D4):
//! nothing in this kit is required to *run* an SDK's CI; the corpus and
//! that contract are the whole dependency.
//!
//! Scope limits, stated exactly:
//!
//! - This crate never modifies the corpus; the SE-04 runner
//!   (`terrabase-sync-core`'s `vectors_runner`) remains the corpus's
//!   regeneration gate, and both must stay green.
//! - Reports are deterministic and carry no host paths, no timestamps,
//!   and no document or payload bytes: failure details hold step
//!   indices, check names, byte counts, and static reason text only.
//! - No performance or bandwidth claim is made here; the corpus's
//!   recorded byte counts are deterministic artifact values
//!   (PD-2026-08-26-13 D1), and measured budgets are SE-07/SE-15's.

use std::collections::BTreeMap;
use std::fmt;
use std::fs;
use std::path::Path;
use std::path::PathBuf;

use serde_json::Value;
use serde_json::json;

pub mod manifest;

mod corpus;
mod sha256;

pub use manifest::CorpusManifest;
pub use manifest::ManifestEntry;
pub use manifest::ManifestError;

/// The protocol identity the kit verifies.
pub const PROTOCOL: &str = "terrascale.sync.v2";

/// The protocol version the kit verifies; the corpus declares it in
/// every file header.
pub const PROTOCOL_VERSION: u64 = 2;

/// The corpus schema identity every vector file carries.
pub const CORPUS_SCHEMA: &str = "terrascale.sync.v2/vector-corpus@2";

/// The report schema identity every conformance report carries.
pub const REPORT_SCHEMA: &str = "terrascale.sync.v2/conformance-report@1";

/// The manifest schema identity every corpus manifest carries.
pub const MANIFEST_SCHEMA: &str = "terrascale.sync.v2/corpus-manifest@1";

/// The corpus categories a conforming corpus covers exactly once each.
pub const REQUIRED_CATEGORIES: [&str; 9] = [
    "fresh-session",
    "checkpoint-resume",
    "reconnect-replay",
    "slow-consumer",
    "region-route-change",
    "resync-required",
    "malformed-frame-rejection",
    "repeated-change-delta",
    "trigger-notification",
];

/// Whether a corpus directory entry is a vector file (a `*.json`
/// file, compared case-insensitively).
pub(crate) fn is_corpus_file(name: &str) -> bool {
    Path::new(name).extension().is_some_and(|ext| ext.eq_ignore_ascii_case("json"))
}

/// The setup-level error of the harness: the corpus directory itself is
/// missing or unreadable. Content-level disagreement is never this
/// error — it is recorded in the report.
#[derive(Debug, PartialEq)]
pub enum HarnessError {
    /// The corpus directory cannot be read.
    CorpusUnreadable(String),
}

impl fmt::Display for HarnessError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            HarnessError::CorpusUnreadable(reason) => {
                write!(f, "the corpus directory is unreadable: {reason}")
            }
        }
    }
}

impl std::error::Error for HarnessError {}

/// One recorded check failure: which step, which named check, and a
/// static reason. Details never carry corpus bytes, payload bytes, or
/// host paths.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Failure {
    step: Option<usize>,
    check: &'static str,
    detail: String,
}

impl Failure {
    pub(crate) fn new(step: Option<usize>, check: &'static str, detail: String) -> Self {
        Self { step, check, detail }
    }

    /// The failing step's index (`None` for file-level checks).
    #[must_use]
    pub fn step(&self) -> Option<usize> {
        self.step
    }

    /// The stable name of the failed check (the documented vocabulary).
    #[must_use]
    pub fn check(&self) -> &'static str {
        self.check
    }

    /// The static reason the check failed.
    #[must_use]
    pub fn detail(&self) -> &str {
        &self.detail
    }

    fn to_json(&self) -> Value {
        json!({
            "step": self.step,
            "check": self.check,
            "detail": self.detail,
        })
    }
}

/// The outcome of one corpus vector file.
#[derive(Clone, Debug, PartialEq)]
pub struct VectorResult {
    category: String,
    file: String,
    checks: BTreeMap<&'static str, u64>,
    failures: Vec<Failure>,
    aborted: bool,
}

impl VectorResult {
    pub(crate) fn new(
        category: String,
        file: String,
        checks: BTreeMap<&'static str, u64>,
        failures: Vec<Failure>,
        aborted: bool,
    ) -> Self {
        Self { category, file, checks, failures, aborted }
    }

    /// The corpus category this file covers.
    #[must_use]
    pub fn category(&self) -> &str {
        &self.category
    }

    /// The file name within the corpus directory (never a path).
    #[must_use]
    pub fn file(&self) -> &str {
        &self.file
    }

    /// How many checks of each name executed (passed and failed).
    #[must_use]
    pub fn checks(&self) -> &BTreeMap<&'static str, u64> {
        &self.checks
    }

    /// How many checks of this name executed, if any.
    #[must_use]
    pub fn check_count(&self, name: &str) -> Option<u64> {
        self.checks.get(name).copied()
    }

    /// The recorded failures, in execution order.
    #[must_use]
    pub fn failures(&self) -> &[Failure] {
        &self.failures
    }

    /// Whether a failed check stopped the vector early.
    #[must_use]
    pub fn aborted(&self) -> bool {
        self.aborted
    }

    /// Whether the vector passed: no failures, not aborted.
    #[must_use]
    pub fn passed(&self) -> bool {
        self.failures.is_empty() && !self.aborted
    }

    /// Records one executed check by name (the manifest gate's pass
    /// path).
    pub(crate) fn record_check(&mut self, name: &'static str) {
        *self.checks.entry(name).or_insert(0) += 1;
    }

    /// Records one failed check by name, at file level (the manifest
    /// gate's failure path; it runs after interpretation, so it never
    /// marks the vector aborted).
    pub(crate) fn record_failure(&mut self, name: &'static str, detail: String) {
        self.record_check(name);
        self.failures.push(Failure::new(None, name, detail));
    }

    fn to_json(&self) -> Value {
        json!({
            "category": self.category,
            "file": self.file,
            "status": if self.passed() { "pass" } else { "fail" },
            "aborted": self.aborted,
            "checks": self
                .checks
                .iter()
                .map(|(name, count)| ((*name).to_string(), json!(count)))
                .collect::<serde_json::Map<String, Value>>(),
            "failures": self.failures.iter().map(Failure::to_json).collect::<Vec<_>>(),
        })
    }
}

/// The manifest-gate outcome of a run, when a manifest was supplied.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ManifestOutcome {
    verified: bool,
    problems: Vec<String>,
}

impl ManifestOutcome {
    /// Whether every corpus file matched its manifest entry.
    #[must_use]
    pub fn verified(&self) -> bool {
        self.verified
    }

    /// The manifest problems (file names and reasons, never bytes).
    #[must_use]
    pub fn problems(&self) -> &[String] {
        &self.problems
    }

    fn to_json(&self) -> Value {
        let mut value = json!({ "status": if self.verified { "verified" } else { "failed" } });
        if !self.problems.is_empty() {
            value["problems"] = json!(self.problems);
        }
        value
    }
}

/// The outcome of one full harness run over a corpus: deterministic,
/// versioned, and host-free.
#[derive(Clone, Debug, PartialEq)]
pub struct ConformanceReport {
    corpus: String,
    vectors: Vec<VectorResult>,
    problems: Vec<String>,
    manifest: Option<ManifestOutcome>,
}

impl ConformanceReport {
    /// The corpus identity (the corpus directory's name, not its host
    /// path).
    #[must_use]
    pub fn corpus(&self) -> &str {
        &self.corpus
    }

    /// Every vector result, sorted by file name.
    #[must_use]
    pub fn vectors(&self) -> &[VectorResult] {
        &self.vectors
    }

    /// Run-level problems (for example a missing corpus category).
    #[must_use]
    pub fn problems(&self) -> &[String] {
        &self.problems
    }

    /// The manifest gate's outcome, when a manifest gated the run.
    #[must_use]
    pub fn manifest(&self) -> Option<&ManifestOutcome> {
        self.manifest.as_ref()
    }

    /// Whether the whole run passed: every vector, the category
    /// coverage, and — when supplied — the manifest gate.
    #[must_use]
    pub fn passed(&self) -> bool {
        self.vectors.iter().all(VectorResult::passed)
            && self.problems.is_empty()
            && self.manifest.as_ref().is_none_or(ManifestOutcome::verified)
    }

    fn summary(&self) -> (u64, u64, u64) {
        let checks_total: u64 = self.vectors.iter().map(|v| v.checks.values().sum::<u64>()).sum();
        let checks_failed: u64 = self.vectors.iter().map(|v| v.failures.len() as u64).sum();
        (checks_total, checks_total - checks_failed, checks_failed)
    }

    /// The report's canonical JSON (pretty, deterministic).
    ///
    /// # Panics
    ///
    /// Panics only if serialization fails, which the report's
    /// JSON-only field types preclude.
    #[must_use]
    pub fn to_json_string(&self) -> String {
        serde_json::to_string_pretty(&self.to_json()).expect("the report serializes")
    }

    fn to_json(&self) -> Value {
        let (checks_total, checks_passed, checks_failed) = self.summary();
        let vectors_passed = self.vectors.iter().filter(|v| v.passed()).count();
        let mut value = json!({
            "schema": REPORT_SCHEMA,
            "protocol": PROTOCOL,
            "protocol_version": PROTOCOL_VERSION,
            "harness": format!("terrabase-sync-conformance/{}", env!("CARGO_PKG_VERSION")),
            "corpus": self.corpus,
            "summary": {
                "status": if self.passed() { "pass" } else { "fail" },
                "vectors_total": self.vectors.len(),
                "vectors_passed": vectors_passed,
                "vectors_failed": self.vectors.len() - vectors_passed,
                "checks_total": checks_total,
                "checks_passed": checks_passed,
                "checks_failed": checks_failed,
                "problems": self.problems,
            },
            "vectors": self.vectors.iter().map(VectorResult::to_json).collect::<Vec<_>>(),
        });
        // The manifest section appears exactly when a manifest gated
        // the run (absent, never null, otherwise).
        if let Some(outcome) = &self.manifest {
            value["manifest"] = outcome.to_json();
        }
        value
    }
}

/// The Rust reference harness: executes a `terrascale.sync.v2`
/// golden-vector corpus against the engine's delivery core.
///
/// A harness owns one corpus directory. [`ConformanceHarness::run`]
/// interprets every vector file with the real `terrabase-sync-core`
/// core and codec — the reference semantics the per-language harness
/// contract documents.
pub struct ConformanceHarness {
    corpus_dir: PathBuf,
}

impl ConformanceHarness {
    /// Creates a harness over a corpus directory.
    #[must_use]
    pub fn new(corpus_dir: impl Into<PathBuf>) -> Self {
        Self { corpus_dir: corpus_dir.into() }
    }

    /// The repository's committed corpus directory (resolved at compile
    /// time so `cargo run` and `cargo test` find it from the crate).
    #[must_use]
    pub fn default_corpus_dir() -> PathBuf {
        PathBuf::from(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../terrabase-sync-core/vectors/terrascale.sync.v2"
        ))
    }

    /// The corpus directory this harness executes.
    #[must_use]
    pub fn corpus_dir(&self) -> &Path {
        &self.corpus_dir
    }

    /// Runs the corpus and produces the conformance report.
    ///
    /// # Errors
    ///
    /// Returns [`HarnessError::CorpusUnreadable`] only when the corpus
    /// directory itself cannot be read; every content-level
    /// disagreement is a recorded failure inside the report.
    pub fn run(&self) -> Result<ConformanceReport, HarnessError> {
        self.execute(None)
    }

    /// Runs the corpus with the integrity manifest as a gate: every
    /// corpus file must match its manifest entry (recorded per vector
    /// as the `manifest_entry_matches` check), and set-level mismatch
    /// fails the run through the report's manifest section.
    ///
    /// # Errors
    ///
    /// As [`ConformanceHarness::run`].
    pub fn run_with_manifest(
        &self,
        manifest: &CorpusManifest,
    ) -> Result<ConformanceReport, HarnessError> {
        self.execute(Some(manifest))
    }

    fn execute(
        &self,
        manifest: Option<&CorpusManifest>,
    ) -> Result<ConformanceReport, HarnessError> {
        let files = self.corpus_files()?;
        let mut vectors: Vec<VectorResult> =
            files.iter().map(|file| corpus::interpret(file, &self.corpus_dir.join(file))).collect();
        vectors.sort_by(|a, b| a.file.cmp(&b.file));

        // Category coverage: a conforming corpus covers every required
        // category exactly once.
        let mut problems = Vec::new();
        for category in REQUIRED_CATEGORIES {
            let count = vectors.iter().filter(|v| v.category() == category).count();
            match count {
                0 => problems.push(format!("the corpus is missing the {category} category")),
                1 => {}
                _ => problems
                    .push(format!("the corpus covers the {category} category {count} times")),
            }
        }
        for vector in &vectors {
            if !REQUIRED_CATEGORIES.contains(&vector.category()) {
                problems.push(format!(
                    "the corpus carries an unexpected category {}",
                    vector.category()
                ));
            }
        }

        let manifest_outcome =
            manifest.map(|manifest| self.gate_with_manifest(manifest, &mut vectors));
        Ok(ConformanceReport {
            // The corpus identity is the directory's name — never its
            // host path.
            corpus: self
                .corpus_dir
                .file_name()
                .map_or_else(|| ".".to_string(), |name| name.to_string_lossy().into_owned()),
            vectors,
            problems,
            manifest: manifest_outcome,
        })
    }

    /// Attaches the manifest gate to every vector result and returns
    /// the gate's overall outcome.
    fn gate_with_manifest(
        &self,
        manifest: &CorpusManifest,
        vectors: &mut [VectorResult],
    ) -> ManifestOutcome {
        let mut problems = Vec::new();
        for vector in &mut *vectors {
            let Some(entry) = manifest.vectors().iter().find(|e| e.file() == vector.file()) else {
                problems.push(format!("{}: corpus file without a manifest entry", vector.file()));
                continue;
            };
            let Ok(bytes) = fs::read(self.corpus_dir.join(vector.file())) else {
                problems.push(format!("{}: missing from the corpus", vector.file()));
                continue;
            };
            let mut hasher = sha256::Sha256::new();
            hasher.update(&bytes);
            let digest = sha256::to_hex(&hasher.finish());
            let matches = digest == entry.sha256() && bytes.len() as u64 == entry.bytes();
            if matches {
                vector.record_check("manifest_entry_matches");
            } else {
                vector.record_failure("manifest_entry_matches", format!(
                    "the corpus file disagrees with its manifest entry (recorded {} bytes, found {})",
                    entry.bytes(),
                    bytes.len()
                ));
                problems.push(format!(
                    "{}: disagrees with its manifest entry (sha256 or byte count)",
                    vector.file()
                ));
            }
        }
        for entry in manifest.vectors() {
            if !vectors.iter().any(|vector| vector.file() == entry.file()) {
                problems.push(format!("{}: manifest entry without a corpus file", entry.file()));
            }
        }
        ManifestOutcome { verified: problems.is_empty(), problems }
    }

    /// The corpus directory's vector files, sorted; empty when the
    /// directory holds no JSON files (the report records the coverage
    /// problems).
    fn corpus_files(&self) -> Result<Vec<String>, HarnessError> {
        let entries = fs::read_dir(&self.corpus_dir).map_err(|e| {
            HarnessError::CorpusUnreadable(format!("{}: {e}", self.corpus_dir.display()))
        })?;
        let mut files: Vec<String> = entries
            .flatten()
            .map(|entry| entry.file_name().to_string_lossy().into_owned())
            .filter(|name| is_corpus_file(name))
            .collect();
        files.sort();
        Ok(files)
    }
}

#[cfg(test)]
mod tests {
    use super::ConformanceHarness;
    use super::HarnessError;

    #[test]
    fn a_missing_corpus_directory_is_a_setup_error() {
        let missing = std::env::temp_dir().join("terrabase-se05-no-such-corpus");
        let error = ConformanceHarness::new(missing).run().unwrap_err();
        assert!(matches!(error, HarnessError::CorpusUnreadable(_)));
    }
}
