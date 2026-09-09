//! The SE-15 comparison lane: the TerraBase side of a recorded run and
//! the classification of both sides under the gate.
//!
//! This module is the engine-side half of a **recorded lane run** — the
//! run class the reference drivers (`bench/`) feed. It applies the exact
//! discipline the operator-side reference executor enforces on itself,
//! so the two sides of a comparison stay symmetrical:
//!
//! - one durable engine under a fresh location, `LocalDurability::Synced`
//!   commits (the mirror of PostgreSQL's `synchronous_commit = on` pin);
//! - warm-up operations are real work excluded from every measured sample;
//! - every measured sample starts and ends on barriers of size
//!   `concurrency + 1`, so all workers begin together and each sample's
//!   wall clock runs from the start barrier's release to the last
//!   worker's arrival at the end barrier;
//! - per-operation latencies are captured per operation, so percentiles
//!   describe operations, not windows.
//!
//! The TerraBase side emits the same JSON document shape the reference
//! drivers emit (`bench/src/metrics.rs`), and reads the reference side's
//! documents back with a fail-closed reader, so the SE-15 gates classify
//! both sides from one format. Verdicts are produced **here, engine-side,
//! only** — never in the operator-side driver.
//!
//! Wall-clock honesty: a sample set produced here is a fact about one
//! host, one process, and one moment. The receipt states the limits
//! (loopback or in-process boundary, single host, no cross-side sample
//! interleaving) rather than hiding them.

#![allow(
    clippy::assigning_clones,
    clippy::case_sensitive_file_extension_comparisons,
    clippy::cast_possible_truncation,
    clippy::cast_precision_loss,
    clippy::cast_sign_loss,
    clippy::format_push_string,
    clippy::manual_let_else,
    clippy::new_ret_no_self,
    clippy::too_many_lines,
    clippy::wildcard_imports,
    reason = "the strict JSON reader preserves the audited wire format and hostile fixtures"
)]

use std::path::{Path, PathBuf};
use std::sync::{Arc, Barrier};
use std::time::Instant;

use super::{
    driver::TerraBaseLane,
    gates::{self, MedianRange, ReferenceComparison},
    lane::{LaneConfig, Workload},
    record::REGRESSION_RULE,
    vocabulary::{ObjectiveStatus, ReferenceRun, Verdict, objective_status_line},
};

/// The measured facts of one sample window, in the reference driver's
/// exact shape.
#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct SampleFacts {
    /// Total operations the window completed across all workers.
    pub operations: u64,
    /// The window's wall-clock duration in seconds.
    pub wall_seconds: f64,
    /// Operations per second over the window.
    pub throughput_ops_per_s: f64,
    /// Median per-operation latency in microseconds.
    pub p50_us: f64,
    /// 95th-percentile per-operation latency in microseconds.
    pub p95_us: f64,
    /// 99th-percentile per-operation latency in microseconds.
    pub p99_us: f64,
}

impl SampleFacts {
    /// Reduces one window the way `bench/src/metrics.rs::sample_of` does:
    /// nearest-rank percentiles over exact per-operation latencies.
    pub(crate) fn of(operations: u64, wall_seconds: f64, mut latencies_us: Vec<f64>) -> Self {
        latencies_us.sort_by(f64::total_cmp);
        let percentile = |fraction: f64| -> f64 {
            if latencies_us.is_empty() {
                return 0.0;
            }
            let index = ((latencies_us.len() as f64 - 1.0) * fraction).round() as usize;
            latencies_us[index.min(latencies_us.len() - 1)]
        };
        Self {
            operations,
            wall_seconds,
            throughput_ops_per_s: operations as f64 / wall_seconds.max(f64::EPSILON),
            p50_us: percentile(0.50),
            p95_us: percentile(0.95),
            p99_us: percentile(0.99),
        }
    }
}

/// One side's results document: the reference driver's `RunResults`
/// shape, plus the reader the engine side uses for reference files.
#[derive(Clone, Debug)]
pub(crate) struct SideFacts {
    /// Which side this is (`terrabase`, `postgresql`, or `ravendb`).
    pub target: String,
    /// The workload measured.
    pub workload: String,
    /// The lane configuration used.
    pub config: LaneConfig,
    /// The identity the side reported about itself.
    pub observed_identity: String,
    /// The endpoint or location the side used.
    pub endpoint: String,
    /// The client transport description.
    pub client_transport: String,
    /// One entry per measured sample, in run order.
    pub samples: Vec<SampleFacts>,
}

impl SideFacts {
    /// The median-and-range form of the throughput samples.
    pub(crate) fn throughput(&self) -> Option<MedianRange> {
        MedianRange::of(&self.samples.iter().map(|s| s.throughput_ops_per_s).collect::<Vec<_>>())
    }

    /// The median-and-range form of the p95 latency samples.
    pub(crate) fn p95(&self) -> Option<MedianRange> {
        MedianRange::of(&self.samples.iter().map(|s| s.p95_us).collect::<Vec<_>>())
    }
}

/// Runs the TerraBase side of one workload with the reference executor's
/// exact discipline and returns the per-sample facts.
///
/// # Panics
///
/// Panics if a measured operation fails: a window that cannot complete
/// fails loudly rather than emitting a partial sample set, exactly as the
/// reference executor does.
pub(crate) fn sample_terrabase_side(
    lane: &TerraBaseLane,
    workload: Workload,
    config: LaneConfig,
) -> Vec<SampleFacts> {
    config.validate().expect("a benchmark lane requires a valid configuration");
    let per_slot_operations = config.operations / config.concurrency;
    let per_slot_warmup = config.warmup_operations / config.concurrency;
    let warmup_remainder = config.warmup_operations % config.concurrency;
    let start = Arc::new(Barrier::new(config.concurrency + 1));
    let end = Arc::new(Barrier::new(config.concurrency + 1));

    std::thread::scope(|scope| -> Vec<SampleFacts> {
        let mut worker_samples = Vec::with_capacity(config.concurrency);
        for slot_index in 0..config.concurrency {
            let start = Arc::clone(&start);
            let end = Arc::clone(&end);
            worker_samples.push(scope.spawn(move || -> Vec<Vec<f64>> {
                // Warm-up: real work, excluded from every measured sample.
                let slot_warmup = per_slot_warmup + usize::from(slot_index < warmup_remainder);
                for index in 0..slot_warmup {
                    let _ = lane.one_operation(workload.select_mixed(index), index);
                }
                let mut samples = Vec::with_capacity(config.samples);
                for sample in 0..config.samples {
                    start.wait();
                    let mut latencies = Vec::with_capacity(per_slot_operations);
                    for index in 0..per_slot_operations {
                        let window_index = sample * per_slot_operations + index;
                        let started = Instant::now();
                        lane.one_operation(workload.select_mixed(window_index), window_index);
                        latencies.push(started.elapsed().as_secs_f64() * 1_000_000.0);
                    }
                    end.wait();
                    samples.push(latencies);
                }
                samples
            }));
        }
        // The coordinator times each sample from the start barrier's
        // release to the last worker's arrival at the end barrier.
        let mut walls = Vec::with_capacity(config.samples);
        for _ in 0..config.samples {
            start.wait();
            let started = Instant::now();
            end.wait();
            walls.push(started.elapsed().as_secs_f64());
        }
        let joined: Vec<Vec<Vec<f64>>> = worker_samples
            .into_iter()
            .map(|handle| handle.join().expect("a lane worker panicked"))
            .collect();
        walls
            .into_iter()
            .enumerate()
            .map(|(sample, wall)| {
                let mut latencies = Vec::with_capacity(per_slot_operations * config.concurrency);
                for slot in &joined {
                    latencies.extend_from_slice(&slot[sample]);
                }
                SampleFacts::of((per_slot_operations * config.concurrency) as u64, wall, latencies)
            })
            .collect()
    })
}

/// The engine-side transport description recorded on the TerraBase facts
/// document, so a receipt cannot mistake the boundary for a network one.
pub(crate) const TERRABASE_TRANSPORT: &str = "in-process durable engine library, LocalDurability::Synced commits, one shared engine across all concurrency slots";

/// Classifies the TerraBase side against one reference side under the
/// SE-15 gate, fail-closed on an absent side.
pub(crate) fn classify(
    engine: &SideFacts,
    reference: Option<&SideFacts>,
) -> (ReferenceRun, ReferenceComparison, ObjectiveStatus, Verdict) {
    let Some(reference) = reference else {
        return (
            ReferenceRun::Unrun {
                reason: "no reference results document was supplied for this workload".to_owned(),
                owner: super::reference::REFERENCE_OWNER,
            },
            ReferenceComparison { throughput: gates::Trend::Stable, p95: gates::Trend::Stable },
            ObjectiveStatus::Inconclusive,
            Verdict::Inconclusive,
        );
    };
    let run = ReferenceRun::Ran { identity: reference.observed_identity.clone() };
    if engine.config != reference.config
        || engine.workload != reference.workload
        || engine.samples.len() != reference.samples.len()
        || engine.client_transport.contains("in-process")
            != reference.client_transport.contains("in-process")
    {
        return (
            run,
            ReferenceComparison { throughput: gates::Trend::Stable, p95: gates::Trend::Stable },
            ObjectiveStatus::Inconclusive,
            Verdict::Inconclusive,
        );
    }
    let comparison = ReferenceComparison {
        throughput: gates::classify_higher(
            &engine.throughput().unwrap_or(MedianRange { median: 0.0, min: 0.0, max: 0.0 }),
            &reference.throughput().unwrap_or(MedianRange { median: 0.0, min: 0.0, max: 0.0 }),
            engine.config.noise_percent,
        ),
        p95: gates::classify_lower(
            &engine.p95().unwrap_or(MedianRange { median: 0.0, min: 0.0, max: 0.0 }),
            &reference.p95().unwrap_or(MedianRange { median: 0.0, min: 0.0, max: 0.0 }),
            engine.config.noise_percent,
        ),
    };
    let (status, verdict) = gates::reference_comparison_status(&run, comparison);
    (run, comparison, status, verdict)
}

/// Checks that a result document came from the side and workload selected by
/// the invoking lane. Filenames are operator input and cannot establish
/// identity on their own.
pub(crate) fn validate_reference_identity(
    side: &SideFacts,
    expected_target: &str,
    expected_workload: &str,
) -> Result<(), String> {
    if side.target != expected_target {
        return Err(format!(
            "reference target {:?} does not match requested target {expected_target:?}",
            side.target
        ));
    }
    if side.workload != expected_workload {
        return Err(format!(
            "reference workload {:?} does not match requested workload {expected_workload:?}",
            side.workload
        ));
    }
    Ok(())
}

/// The outcome of selecting one retained reference attempt.
///
/// A failed or malformed newest attempt is deliberately distinct from a
/// missing reference.  Callers may report either state as inconclusive, but
/// must not silently fall back to an older successful attempt.
#[derive(Debug)]
pub(crate) enum ReferenceDocument {
    /// No retained attempt or legacy result exists for this lane.
    Missing,
    /// The newest retained document is a complete, valid result.
    Complete(SideFacts),
    /// The newest retained document explicitly reports a failed run.
    Failed { path: PathBuf, reason: String },
    /// The newest retained document cannot be parsed or validated.
    Invalid { path: PathBuf, reason: String },
}

impl ReferenceDocument {
    fn side(&self) -> Option<&SideFacts> {
        match self {
            Self::Complete(side) => Some(side),
            Self::Missing | Self::Failed { .. } | Self::Invalid { .. } => None,
        }
    }

    fn note(&self, target: &str) -> Option<String> {
        match self {
            Self::Missing => {
                Some(format!("- {target} reference: unrun — no retained result document was found"))
            }
            Self::Failed { path, reason } => Some(format!(
                "- {target} reference: failed — {reason} (newest retained attempt: {})",
                path.display()
            )),
            Self::Invalid { path, reason } => Some(format!(
                "- {target} reference: invalid — {reason} (newest retained attempt: {})",
                path.display()
            )),
            Self::Complete(_) => None,
        }
    }
}

#[derive(Debug)]
struct RetainedCandidate {
    path: PathBuf,
    filename: String,
    /// The nanosecond timestamp emitted by `bench/src/main.rs`.
    timestamp: Option<u128>,
    modified: Option<std::time::SystemTime>,
}

/// Selects the result file for one requested target/workload pair.
///
/// Retained attempts are ordered by their filename timestamp and then the
/// complete filename. Filesystem mtime is only used for files whose name does
/// not contain the driver's timestamp. The legacy exact filename is eligible
/// only when no retained attempt exists at all.
fn select_reference_path(
    directory: &Path,
    target: &str,
    workload: &str,
) -> Result<Option<PathBuf>, String> {
    let prefix = format!("{target}-{workload}-");
    let mut candidates = Vec::new();
    let entries = match std::fs::read_dir(directory) {
        Ok(entries) => entries,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            return Ok(None);
        }
        Err(error) => return Err(format!("cannot read {}: {error}", directory.display())),
    };
    for entry in entries {
        let entry = entry.map_err(|error| format!("cannot inspect results entry: {error}"))?;
        let file_type = entry
            .file_type()
            .map_err(|error| format!("cannot inspect {}: {error}", entry.path().display()))?;
        if !file_type.is_file() {
            continue;
        }
        let filename = entry.file_name().to_string_lossy().into_owned();
        if !filename.starts_with(&prefix) || !filename.ends_with(".json") {
            continue;
        }
        let timestamp = filename
            .strip_prefix(&prefix)
            .and_then(|suffix| suffix.strip_suffix(".json"))
            .and_then(|suffix| suffix.split('-').next())
            .and_then(|stamp| stamp.parse::<u128>().ok());
        let modified = entry.metadata().ok().and_then(|metadata| metadata.modified().ok());
        candidates.push(RetainedCandidate { path: entry.path(), filename, timestamp, modified });
    }
    if candidates.is_empty() {
        let legacy = directory.join(format!("{target}-{workload}.json"));
        return Ok(legacy.is_file().then_some(legacy));
    }
    candidates.sort_by(|left, right| {
        left.timestamp
            .cmp(&right.timestamp)
            .then_with(|| match (left.timestamp, right.timestamp) {
                (Some(_), Some(_)) | (None, None) => left.modified.cmp(&right.modified),
                (Some(_), None) => std::cmp::Ordering::Greater,
                (None, Some(_)) => std::cmp::Ordering::Less,
            })
            .then_with(|| left.filename.cmp(&right.filename))
    });
    Ok(candidates.pop().map(|candidate| candidate.path))
}

/// Loads the newest retained attempt, preserving failed/invalid state for the
/// caller instead of allowing an older successful file to mask it.
pub(crate) fn read_reference_document(
    directory: &Path,
    target: &str,
    workload: &str,
) -> ReferenceDocument {
    let path = match select_reference_path(directory, target, workload) {
        Ok(Some(path)) => path,
        Ok(None) => return ReferenceDocument::Missing,
        Err(reason) => {
            return ReferenceDocument::Invalid { path: directory.to_owned(), reason };
        }
    };
    let text = match std::fs::read_to_string(&path) {
        Ok(text) => text,
        Err(error) => {
            return ReferenceDocument::Invalid {
                path,
                reason: format!("cannot read retained result: {error}"),
            };
        }
    };
    let parsed = match json::parse(&text) {
        Ok(value) => value,
        Err(error) => {
            return ReferenceDocument::Invalid { path, reason: error.to_string() };
        }
    };
    let status = match json::status(&parsed) {
        Ok(status) => status,
        Err(error) => {
            return ReferenceDocument::Invalid { path, reason: error.to_string() };
        }
    };
    if status == "failed" {
        return ReferenceDocument::Failed {
            path,
            reason: json::failure_reason(&parsed).unwrap_or_else(|| "run failed".to_owned()),
        };
    }
    if status != "complete" {
        return ReferenceDocument::Invalid {
            path,
            reason: format!("result status {status:?} is not complete"),
        };
    }
    let side = match json::reference_side(&parsed) {
        Ok(side) => side,
        Err(error) => return ReferenceDocument::Invalid { path, reason: error.to_string() },
    };
    if let Err(reason) = validate_reference_identity(&side, target, workload) {
        return ReferenceDocument::Invalid { path, reason };
    }
    ReferenceDocument::Complete(side)
}

/// Renders one side's facts document in the reference driver's exact JSON
/// shape (`bench/src/metrics.rs`), so both sides of a comparison are one
/// format.
pub(crate) fn render_facts_json(
    side: &SideFacts,
    unix_seconds: u64,
    kernel: &str,
    logical_cpus: usize,
    limits: &[String],
) -> String {
    use std::fmt::Write as _;
    let mut text = String::new();
    let _ = writeln!(text, "{{");
    let _ = writeln!(text, "  \"status\": \"complete\",");
    let _ = writeln!(text, "  \"target\": {},", json_string(&side.target));
    let _ = writeln!(text, "  \"workload\": {},", json_string(&side.workload));
    let _ = writeln!(
        text,
        "  \"config\": {{\"seed_documents\": {}, \"operations\": {}, \"samples\": {}, \"concurrency\": {}, \"warmup_operations\": {}, \"noise_percent\": {}}},",
        side.config.seed_documents,
        side.config.operations,
        side.config.samples,
        side.config.concurrency,
        side.config.warmup_operations,
        json_number(side.config.noise_percent)
    );
    let _ = writeln!(text, "  \"environment\": {{");
    let _ = writeln!(text, "    \"observed_identity\": {},", json_string(&side.observed_identity));
    let _ = writeln!(text, "    \"endpoint\": {},", json_string(&side.endpoint));
    let _ = writeln!(text, "    \"client_transport\": {},", json_string(&side.client_transport));
    let _ = writeln!(text, "    \"unix_seconds\": {unix_seconds},");
    let _ = writeln!(text, "    \"kernel\": {},", json_string(kernel));
    let _ = writeln!(text, "    \"logical_cpus\": {logical_cpus}");
    let _ = writeln!(text, "  }},");
    let _ = writeln!(text, "  \"samples\": [");
    for (index, sample) in side.samples.iter().enumerate() {
        let _ = writeln!(
            text,
            "    {{\"operations\": {}, \"wall_seconds\": {}, \"throughput_ops_per_s\": {}, \"p50_us\": {}, \"p95_us\": {}, \"p99_us\": {}}}{}",
            sample.operations,
            json_number(sample.wall_seconds),
            json_number(sample.throughput_ops_per_s),
            json_number(sample.p50_us),
            json_number(sample.p95_us),
            json_number(sample.p99_us),
            if index + 1 == side.samples.len() { "" } else { "," }
        );
    }
    let _ = writeln!(text, "  ],");
    let _ = writeln!(
        text,
        "  \"limits\": [{}]",
        limits.iter().map(|limit| json_string(limit)).collect::<Vec<_>>().join(", ")
    );
    let _ = writeln!(text, "}}");
    text
}

/// Renders the canonical objective-status line plus the per-side summary
/// a receipt carries for one workload.
pub(crate) fn render_workload_summary(
    workload: &str,
    engine: &SideFacts,
    postgresql: Option<&SideFacts>,
    ravendb: Option<&SideFacts>,
) -> String {
    use std::fmt::Write as _;
    let (_, _, pg_status, pg_verdict) = classify(engine, postgresql);
    let (_, _, raven_status, raven_verdict) = classify(engine, ravendb);
    let mut text = String::new();
    let _ = writeln!(text, "### workload: {workload}");
    let _ = writeln!(text);
    let _ =
        writeln!(text, "| side | median ops/s | sample range ops/s | p50 us | p95 us | p99 us |");
    let _ = writeln!(text, "| --- | --- | --- | --- | --- | --- |");
    for (name, side) in
        [("terrabase", Some(engine)), ("postgresql", postgresql), ("ravendb", ravendb)]
    {
        let Some(side) = side else { continue };
        let tps = side.throughput().unwrap_or(MedianRange { median: 0.0, min: 0.0, max: 0.0 });
        let median_p50 =
            MedianRange::of(&side.samples.iter().map(|s| s.p50_us).collect::<Vec<_>>());
        let median_p95 =
            MedianRange::of(&side.samples.iter().map(|s| s.p95_us).collect::<Vec<_>>());
        let median_p99 =
            MedianRange::of(&side.samples.iter().map(|s| s.p99_us).collect::<Vec<_>>());
        let _ = writeln!(
            text,
            "| {} | {:.0} | {:.0} – {:.0} | {:.1} | {:.1} | {:.1} |",
            name,
            tps.median,
            tps.min,
            tps.max,
            median_p50.map_or(0.0, |m| m.median),
            median_p95.map_or(0.0, |m| m.median),
            median_p99.map_or(0.0, |m| m.median),
        );
    }
    let _ = writeln!(text);
    let _ = writeln!(
        text,
        "- objective ({}): `{}`",
        objective_status_line(pg_status),
        pg_verdict.as_str()
    );
    let _ = writeln!(
        text,
        "- versus ravendb: `{}` ({})",
        raven_verdict.as_str(),
        raven_status.as_str()
    );
    let _ = writeln!(text);
    text
}

/// A minimal fail-closed JSON reader for the reference results document.
///
/// The engine workspace is zero-dependency, so the reader is hand-rolled
/// for exactly the schema the reference drivers emit; anything unexpected
/// is an error naming the file's field, never a silent zero.
pub(crate) mod json {
    use std::collections::BTreeMap;

    use super::SampleFacts;
    use crate::benchmarks::lane::LaneConfig;

    /// One parsed JSON value.
    #[derive(Clone, Debug, PartialEq)]
    pub(crate) enum Value {
        /// A JSON string.
        Str(String),
        /// A JSON number.
        Num(f64),
        /// A JSON array.
        Arr(Vec<Value>),
        /// A JSON object (insertion-ordered by key).
        Obj(BTreeMap<String, Value>),
    }

    /// A parse or extraction failure, naming where.
    #[derive(Debug)]
    pub(crate) struct Error(pub String);

    impl std::fmt::Display for Error {
        fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
            write!(f, "{}", self.0)
        }
    }

    impl std::error::Error for Error {}

    /// Parses one JSON document.
    ///
    /// # Errors
    ///
    /// Returns the byte offset and reason for any malformed input.
    pub(crate) fn parse(text: &str) -> Result<Value, Error> {
        let bytes: Vec<char> = text.chars().collect();
        let mut position = 0;
        let value = parse_value(&bytes, &mut position)?;
        skip_whitespace(&bytes, &mut position);
        if position != bytes.len() {
            return Err(Error(format!("trailing content at offset {position}")));
        }
        Ok(value)
    }

    /// Reads the result status before attempting to extract measured facts.
    /// Older complete documents omitted this field, so absence remains
    /// backwards-compatible while an explicit failure remains visible.
    pub(crate) fn status(value: &Value) -> Result<&str, Error> {
        let Value::Obj(fields) = value else {
            return Err(Error("the document is not an object".to_owned()));
        };
        match fields.get("status") {
            None => Ok("complete"),
            Some(Value::Str(status)) => Ok(status.as_str()),
            Some(_) => Err(Error("field status is not a string".to_owned())),
        }
    }

    /// Returns a retained failure's operator-facing error, when present.
    pub(crate) fn failure_reason(value: &Value) -> Option<String> {
        let Value::Obj(fields) = value else { return None };
        fields.get("error").and_then(|value| match value {
            Value::Str(error) if !error.is_empty() => Some(error.clone()),
            _ => None,
        })
    }

    fn skip_whitespace(bytes: &[char], position: &mut usize) {
        while *position < bytes.len() && bytes[*position].is_whitespace() {
            *position += 1;
        }
    }

    fn parse_value(bytes: &[char], position: &mut usize) -> Result<Value, Error> {
        skip_whitespace(bytes, position);
        let Some(&first) = bytes.get(*position) else {
            return Err(Error("unexpected end of input".to_owned()));
        };
        match first {
            '"' => Ok(Value::Str(parse_string(bytes, position)?)),
            '[' => {
                *position += 1;
                let mut items = Vec::new();
                skip_whitespace(bytes, position);
                if bytes.get(*position) == Some(&']') {
                    *position += 1;
                    return Ok(Value::Arr(items));
                }
                loop {
                    items.push(parse_value(bytes, position)?);
                    skip_whitespace(bytes, position);
                    match bytes.get(*position) {
                        Some(',') => *position += 1,
                        Some(']') => {
                            *position += 1;
                            return Ok(Value::Arr(items));
                        }
                        other => {
                            return Err(Error(format!(
                                "expected ',' or ']' in array, found {other:?}"
                            )));
                        }
                    }
                }
            }
            '{' => {
                *position += 1;
                let mut fields = BTreeMap::new();
                skip_whitespace(bytes, position);
                if bytes.get(*position) == Some(&'}') {
                    *position += 1;
                    return Ok(Value::Obj(fields));
                }
                loop {
                    skip_whitespace(bytes, position);
                    let key = parse_string(bytes, position)?;
                    skip_whitespace(bytes, position);
                    if bytes.get(*position) != Some(&':') {
                        return Err(Error(format!("expected ':' after key {key}")));
                    }
                    *position += 1;
                    let value = parse_value(bytes, position)?;
                    fields.insert(key, value);
                    skip_whitespace(bytes, position);
                    match bytes.get(*position) {
                        Some(',') => *position += 1,
                        Some('}') => {
                            *position += 1;
                            return Ok(Value::Obj(fields));
                        }
                        other => {
                            return Err(Error(format!(
                                "expected ',' or '}}' in object, found {other:?}"
                            )));
                        }
                    }
                }
            }
            _ => parse_number(bytes, position),
        }
    }

    fn parse_string(bytes: &[char], position: &mut usize) -> Result<String, Error> {
        if bytes.get(*position) != Some(&'"') {
            return Err(Error(format!("expected a string at offset {position}")));
        }
        *position += 1;
        let mut text = String::new();
        loop {
            let Some(&next) = bytes.get(*position) else {
                return Err(Error("unterminated string".to_owned()));
            };
            *position += 1;
            match next {
                '"' => return Ok(text),
                '\\' => {
                    let Some(&escape) = bytes.get(*position) else {
                        return Err(Error("unterminated escape".to_owned()));
                    };
                    *position += 1;
                    match escape {
                        '"' => text.push('"'),
                        '\\' => text.push('\\'),
                        '/' => text.push('/'),
                        'b' => text.push('\u{0008}'),
                        'f' => text.push('\u{000C}'),
                        'n' => text.push('\n'),
                        'r' => text.push('\r'),
                        't' => text.push('\t'),
                        'u' => {
                            let mut code = 0_u32;
                            for _ in 0..4 {
                                let Some(&digit) = bytes.get(*position) else {
                                    return Err(Error("truncated unicode escape".to_owned()));
                                };
                                *position += 1;
                                code = code * 16
                                    + digit
                                        .to_digit(16)
                                        .ok_or_else(|| Error("bad hex digit".to_owned()))?;
                            }
                            text.push(char::from_u32(code).unwrap_or('\u{FFFD}'));
                        }
                        other => return Err(Error(format!("unknown escape \\{other}"))),
                    }
                }
                other => text.push(other),
            }
        }
    }

    fn parse_number(bytes: &[char], position: &mut usize) -> Result<Value, Error> {
        let start = *position;
        while *position < bytes.len()
            && (bytes[*position].is_ascii_digit()
                || matches!(bytes[*position], '-' | '+' | '.' | 'e' | 'E'))
        {
            *position += 1;
        }
        let text: String = bytes[start..*position].iter().collect();
        text.parse::<f64>().map(Value::Num).map_err(|_| Error(format!("malformed number {text:?}")))
    }

    /// Extracts one reference results document from a parsed value.
    ///
    /// # Errors
    ///
    /// Names the first field that is missing or of the wrong shape.
    pub(crate) fn reference_side(value: &Value) -> Result<super::SideFacts, Error> {
        let Value::Obj(fields) = value else {
            return Err(Error("the document is not an object".to_owned()));
        };
        let string = |key: &str| -> Result<String, Error> {
            fields
                .get(key)
                .and_then(|v| match v {
                    Value::Str(text) => Some(text.clone()),
                    _ => None,
                })
                .ok_or_else(|| Error(format!("field {key} is not a string")))
        };
        let environment = match fields.get("environment") {
            Some(Value::Obj(environment)) => environment,
            _ => return Err(Error("field environment is not an object".to_owned())),
        };
        let environment_string = |key: &str| -> Result<String, Error> {
            environment
                .get(key)
                .and_then(|v| match v {
                    Value::Str(text) => Some(text.clone()),
                    _ => None,
                })
                .ok_or_else(|| Error(format!("field environment.{key} is not a string")))
        };
        let config = match fields.get("config") {
            Some(Value::Obj(config)) => config,
            _ => return Err(Error("field config is not an object".to_owned())),
        };
        let config_number = |key: &str| -> Result<f64, Error> {
            config
                .get(key)
                .and_then(|v| match v {
                    Value::Num(value) => Some(*value),
                    _ => None,
                })
                .ok_or_else(|| Error(format!("field config.{key} is not a number")))
        };
        let config_usize = |key: &str| -> Result<usize, Error> {
            let value = config_number(key)?;
            if !value.is_finite() || value < 0.0 || value.fract() != 0.0 {
                return Err(Error(format!("field config.{key} is not a non-negative integer")));
            }
            if value > usize::MAX as f64 {
                return Err(Error(format!("field config.{key} is too large")));
            }
            Ok(value as usize)
        };
        let environment_number = |key: &str| -> Result<f64, Error> {
            environment
                .get(key)
                .and_then(|v| match v {
                    Value::Num(value) => Some(*value),
                    _ => None,
                })
                .ok_or_else(|| Error(format!("field environment.{key} is not a number")))
        };
        let environment_usize = |key: &str| -> Result<usize, Error> {
            let value = environment_number(key)?;
            if !value.is_finite() || value < 0.0 || value.fract() != 0.0 {
                return Err(Error(format!(
                    "field environment.{key} is not a non-negative integer"
                )));
            }
            if value > usize::MAX as f64 {
                return Err(Error(format!("field environment.{key} is too large")));
            }
            Ok(value as usize)
        };
        let status = status(value)?;
        if status != "complete" {
            return Err(Error(format!("result status {status:?} is not a complete run")));
        }
        let target = string("target")?;
        if !matches!(target.as_str(), "terrabase" | "postgresql" | "ravendb") {
            return Err(Error(format!("unknown target {target:?}")));
        }
        let workload = string("workload")?;
        if !matches!(workload.as_str(), "read" | "write" | "query" | "mixed") {
            return Err(Error(format!("unknown workload {workload:?}")));
        }
        let config_seed_documents = config_usize("seed_documents")?;
        let config_operations = config_usize("operations")?;
        let config_samples = config_usize("samples")?;
        let config_concurrency = config_usize("concurrency")?;
        let config_warmup_operations = config_usize("warmup_operations")?;
        let config_noise_percent = config_number("noise_percent")?;
        if config_seed_documents == 0
            || config_operations == 0
            || config_samples == 0
            || config_concurrency == 0
        {
            return Err(Error("config counts must be greater than zero".to_owned()));
        }
        // A served adapter distributes both warm-up and measured operations
        // across slots with remainder handling, so only the measured count
        // is pinned to whole slots per sample.
        if !config_operations.is_multiple_of(config_concurrency) {
            return Err(Error(
                "config operations per sample must be divisible by concurrency".to_owned(),
            ));
        }
        if !config_noise_percent.is_finite() || !(0.0..=100.0).contains(&config_noise_percent) {
            return Err(Error("config noise_percent is not in 0..=100".to_owned()));
        }
        let observed_identity = environment_string("observed_identity")?;
        let endpoint = environment_string("endpoint")?;
        let client_transport = environment_string("client_transport")?;
        if observed_identity.is_empty() || endpoint.is_empty() || client_transport.is_empty() {
            return Err(Error(
                "environment identity, endpoint, and transport must be non-empty".to_owned(),
            ));
        }
        let unix_seconds = environment_usize("unix_seconds")?;
        let kernel = environment_string("kernel")?;
        let logical_cpus = environment_usize("logical_cpus")?;
        if kernel.is_empty() || logical_cpus == 0 || unix_seconds == 0 {
            return Err(Error(
                "environment timestamp, kernel, and logical_cpus must be populated".to_owned(),
            ));
        }
        let samples = match fields.get("samples") {
            Some(Value::Arr(samples)) => samples,
            _ => return Err(Error("field samples is not an array".to_owned())),
        };
        if samples.len() != config_samples {
            return Err(Error(format!(
                "samples has {} entries but config.samples is {config_samples}",
                samples.len()
            )));
        }
        let mut parsed_samples = Vec::with_capacity(samples.len());
        for (index, sample) in samples.iter().enumerate() {
            let Value::Obj(sample) = sample else {
                return Err(Error(format!("sample {index} is not an object")));
            };
            let sample_number = |key: &str| -> Result<f64, Error> {
                sample
                    .get(key)
                    .and_then(|v| match v {
                        Value::Num(value) => Some(*value),
                        _ => None,
                    })
                    .ok_or_else(|| Error(format!("sample {index} field {key} is not a number")))
            };
            let operations = sample_number("operations")?;
            if !operations.is_finite()
                || operations < 0.0
                || operations.fract() != 0.0
                || operations > u64::MAX as f64
            {
                return Err(Error(format!("sample {index} operations is not a valid integer")));
            }
            let operations = operations as u64;
            if operations != config_operations as u64 {
                return Err(Error(format!(
                    "sample {index} has {operations} operations, expected {config_operations}"
                )));
            }
            let wall_seconds = sample_number("wall_seconds")?;
            let throughput_ops_per_s = sample_number("throughput_ops_per_s")?;
            let p50_us = sample_number("p50_us")?;
            let p95_us = sample_number("p95_us")?;
            let p99_us = sample_number("p99_us")?;
            if !wall_seconds.is_finite()
                || wall_seconds <= 0.0
                || !throughput_ops_per_s.is_finite()
                || throughput_ops_per_s <= 0.0
                || !p50_us.is_finite()
                || p50_us < 0.0
                || !p95_us.is_finite()
                || p95_us < p50_us
                || !p99_us.is_finite()
                || p99_us < p95_us
            {
                return Err(Error(format!(
                    "sample {index} has invalid timing or percentile values"
                )));
            }
            let expected_throughput = operations as f64 / wall_seconds;
            let relative_error = (throughput_ops_per_s - expected_throughput).abs()
                / expected_throughput.max(f64::MIN_POSITIVE);
            if relative_error > 1.0e-6 {
                return Err(Error(format!(
                    "sample {index} throughput does not match wall_seconds"
                )));
            }
            parsed_samples.push(SampleFacts {
                operations,
                wall_seconds,
                throughput_ops_per_s,
                p50_us,
                p95_us,
                p99_us,
            });
        }
        Ok(super::SideFacts {
            target,
            workload,
            config: LaneConfig {
                seed_documents: config_seed_documents,
                operations: config_operations,
                samples: config_samples,
                concurrency: config_concurrency,
                warmup_operations: config_warmup_operations,
                noise_percent: config_noise_percent,
            },
            observed_identity,
            endpoint,
            client_transport,
            samples: parsed_samples,
        })
    }
}

/// JSON-escapes one string.
fn json_string(text: &str) -> String {
    let mut escaped = String::with_capacity(text.len() + 2);
    escaped.push('"');
    for character in text.chars() {
        match character {
            '"' => escaped.push_str("\\\""),
            '\\' => escaped.push_str("\\\\"),
            '\n' => escaped.push_str("\\n"),
            '\r' => escaped.push_str("\\r"),
            '\t' => escaped.push_str("\\t"),
            other if (other as u32) < 0x20 => escaped.push_str(&format!("\\u{:04x}", other as u32)),
            other => escaped.push(other),
        }
    }
    escaped.push('"');
    escaped
}

/// Renders one number in JSON's shortest round-trip form.
fn json_number(value: f64) -> String {
    if value.is_finite() { format!("{value}") } else { "0".to_owned() }
}

/// The regression rule line every recorded run carries.
pub(crate) fn regression_rule() -> &'static str {
    REGRESSION_RULE
}

#[cfg(test)]
mod tests {
    use super::*;

    fn facts() -> SideFacts {
        SideFacts {
            target: "postgresql".to_owned(),
            workload: "read".to_owned(),
            config: LaneConfig {
                seed_documents: 1_000,
                operations: 10,
                samples: 3,
                concurrency: 1,
                warmup_operations: 0,
                noise_percent: 5.0,
            },
            observed_identity: "PostgreSQL 18.6".to_owned(),
            endpoint: "postgres://bench@127.0.0.1:55432/postgres".to_owned(),
            client_transport: "test transport".to_owned(),
            samples: vec![
                SampleFacts {
                    operations: 10,
                    wall_seconds: 0.1,
                    throughput_ops_per_s: 100.0,
                    p50_us: 10.0,
                    p95_us: 20.0,
                    p99_us: 30.0,
                },
                SampleFacts {
                    operations: 10,
                    wall_seconds: 10.0 / 102.0,
                    throughput_ops_per_s: 102.0,
                    p50_us: 10.0,
                    p95_us: 20.0,
                    p99_us: 30.0,
                },
                SampleFacts {
                    operations: 10,
                    wall_seconds: 10.0 / 101.0,
                    throughput_ops_per_s: 101.0,
                    p50_us: 10.0,
                    p95_us: 20.0,
                    p99_us: 30.0,
                },
            ],
        }
    }

    #[test]
    fn the_reader_round_trips_the_writer() {
        let side = facts();
        let text =
            render_facts_json(&side, 1_700_000_000, "Linux test", 8, &["limit one".to_owned()]);
        let parsed = json::parse(&text).expect("renders parseable JSON");
        let read = json::reference_side(&parsed).expect("extracts the side");
        assert_eq!(read.target, side.target);
        assert_eq!(read.workload, side.workload);
        assert_eq!(read.samples.len(), side.samples.len());
        assert_eq!(
            read.samples[0].throughput_ops_per_s.to_bits(),
            side.samples[0].throughput_ops_per_s.to_bits()
        );
        assert_eq!(read.observed_identity, side.observed_identity);
    }

    #[test]
    fn the_reader_fails_closed_on_missing_fields() {
        let parsed = json::parse("{\"target\": \"postgresql\"}").expect("parses");
        let error = json::reference_side(&parsed).expect_err("a document without samples fails");
        assert!(
            error.0.contains("environment") || error.0.contains("workload"),
            "the error names the missing field: {error}"
        );
    }

    #[test]
    fn the_reader_fails_closed_on_malformed_json() {
        assert!(json::parse("{\"target\": ").is_err());
        assert!(json::parse("[1, 2,").is_err());
        assert!(json::parse("not json").is_err());
    }

    #[test]
    fn a_missing_reference_is_inconclusive() {
        let engine = facts();
        let (run, _, status, verdict) = classify(&engine, None);
        assert!(matches!(run, ReferenceRun::Unrun { .. }));
        assert_eq!(status, ObjectiveStatus::Inconclusive);
        assert_eq!(verdict, Verdict::Inconclusive);
    }

    #[test]
    fn a_run_reference_classifies_through_the_gate() {
        let engine = SideFacts {
            target: "terrabase".to_owned(),
            samples: (0..3)
                .map(|index| SampleFacts {
                    operations: 10,
                    wall_seconds: 0.01,
                    throughput_ops_per_s: 1_000.0 + f64::from(index),
                    p50_us: 5.0,
                    p95_us: 8.0,
                    p99_us: 9.0,
                })
                .collect(),
            ..facts()
        };
        let (run, comparison, status, verdict) = classify(&engine, Some(&facts()));
        assert!(matches!(run, ReferenceRun::Ran { .. }));
        assert_eq!(comparison.throughput, gates::Trend::Better);
        assert_eq!(status, ObjectiveStatus::Met);
        assert_eq!(verdict, Verdict::V1Faster);
    }

    #[test]
    fn an_in_process_boundary_cannot_claim_comparability_with_a_network_side() {
        let engine = SideFacts {
            client_transport: "in-process durable engine library".to_owned(),
            ..facts()
        };
        let reference =
            SideFacts { client_transport: "loopback TCP reference client".to_owned(), ..facts() };
        let (_, _, status, verdict) = classify(&engine, Some(&reference));
        assert_eq!((status, verdict), (ObjectiveStatus::Inconclusive, Verdict::Inconclusive));
    }

    #[test]
    fn a_result_filename_cannot_override_target_or_workload_identity() {
        let side = facts();
        let target_error = validate_reference_identity(&side, "ravendb", "read")
            .expect_err("a PostgreSQL document must not fill a RavenDB slot");
        assert!(target_error.contains("target"), "{target_error}");
        let workload_error = validate_reference_identity(&side, "postgresql", "write")
            .expect_err("a read document must not fill a write slot");
        assert!(workload_error.contains("workload"), "{workload_error}");
        assert!(validate_reference_identity(&side, "postgresql", "read").is_ok());
    }

    fn temporary_results_directory() -> std::path::PathBuf {
        let stamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("clock")
            .as_nanos();
        let directory = std::env::temp_dir()
            .join(format!("terrabase-lane-reference-{}-{stamp}", std::process::id()));
        std::fs::create_dir(&directory).expect("temporary results directory");
        directory
    }

    fn write_reference_attempt(directory: &Path, name: &str, contents: &str) {
        std::fs::write(directory.join(name), contents).expect("reference attempt");
    }

    fn complete_document(identity: &str) -> String {
        let mut side = facts();
        side.observed_identity = identity.to_owned();
        render_facts_json(&side, 1_700_000_000, "Linux test", 8, &[])
    }

    #[test]
    fn newest_retained_complete_attempt_wins_by_filename_timestamp() {
        let directory = temporary_results_directory();
        let fixture = json::parse(include_str!(
            "../../bench/scripts/retained-attempt-selection-fixture.json"
        ))
        .expect("shared selection fixture");
        let json::Value::Obj(fields) = fixture else { panic!("fixture object") };
        let target = match fields.get("target") {
            Some(json::Value::Str(value)) => value,
            _ => panic!("fixture target"),
        };
        let workload = match fields.get("workload") {
            Some(json::Value::Str(value)) => value,
            _ => panic!("fixture workload"),
        };
        let attempts = match fields.get("attempts") {
            Some(json::Value::Arr(values)) => values
                .iter()
                .map(|value| match value {
                    json::Value::Str(value) => value.as_str(),
                    _ => panic!("fixture attempt"),
                })
                .collect::<Vec<_>>(),
            _ => panic!("fixture attempts"),
        };
        let newest = match fields.get("newest") {
            Some(json::Value::Str(value)) => value,
            _ => panic!("fixture newest"),
        };
        write_reference_attempt(&directory, attempts[0], &complete_document("older"));
        write_reference_attempt(&directory, attempts[1], &complete_document("newer"));
        let selected = read_reference_document(&directory, target, workload);
        assert!(directory.join(newest).is_file());
        assert!(
            matches!(selected, ReferenceDocument::Complete(ref side) if side.observed_identity == "newer")
        );
        std::fs::remove_dir_all(directory).expect("cleanup");
    }

    #[test]
    fn newest_failed_attempt_blocks_fallback_to_an_older_success() {
        let directory = temporary_results_directory();
        write_reference_attempt(
            &directory,
            "postgresql-read-100-9-0.json",
            &complete_document("older"),
        );
        write_reference_attempt(
            &directory,
            "postgresql-read-200-1-0.json",
            r#"{"status":"failed","target":"postgresql","workload":"read","error":"connection refused"}"#,
        );
        let selected = read_reference_document(&directory, "postgresql", "read");
        assert!(
            matches!(&selected, ReferenceDocument::Failed { reason, .. } if reason == "connection refused")
        );
        assert!(selected.side().is_none());
        std::fs::remove_dir_all(directory).expect("cleanup");
    }

    #[test]
    fn newest_malformed_attempt_blocks_fallback_to_an_older_success() {
        let directory = temporary_results_directory();
        write_reference_attempt(
            &directory,
            "postgresql-read-100-9-0.json",
            &complete_document("older"),
        );
        write_reference_attempt(&directory, "postgresql-read-200-1-0.json", "{broken");
        let selected = read_reference_document(&directory, "postgresql", "read");
        assert!(matches!(&selected, ReferenceDocument::Invalid { .. }));
        assert!(selected.side().is_none());
        std::fs::remove_dir_all(directory).expect("cleanup");
    }

    #[test]
    fn legacy_exact_filename_is_used_only_without_retained_attempts() {
        let directory = temporary_results_directory();
        write_reference_attempt(&directory, "postgresql-read.json", &complete_document("legacy"));
        let selected = read_reference_document(&directory, "postgresql", "read");
        assert!(
            matches!(selected, ReferenceDocument::Complete(ref side) if side.observed_identity == "legacy")
        );
        write_reference_attempt(
            &directory,
            "postgresql-read-100-9-0.json",
            &complete_document("attempt"),
        );
        let selected = read_reference_document(&directory, "postgresql", "read");
        assert!(
            matches!(selected, ReferenceDocument::Complete(ref side) if side.observed_identity == "attempt")
        );
        std::fs::remove_dir_all(directory).expect("cleanup");
    }

    #[test]
    fn the_reader_rejects_truncated_or_inconsistent_result_documents() {
        let side = facts();
        let text = render_facts_json(&side, 1_700_000_000, "Linux test", 8, &[]);
        let mut parsed = json::parse(&text).expect("renders parseable JSON");
        let json::Value::Obj(ref mut fields) = parsed else { panic!("object") };
        let json::Value::Arr(samples) = fields.get_mut("samples").expect("samples") else {
            panic!("array")
        };
        samples.pop();
        assert!(json::reference_side(&parsed).is_err(), "truncated samples must not compare");
    }

    /// The recorded SE-15 comparison lane: runs the TerraBase side of the
    /// four CRUD-class workloads with the reference executor's exact
    /// discipline, classifies both sides under the gate, and writes the
    /// TerraBase facts documents plus the receipt for the operator to
    /// commit.
    ///
    /// Gated on `TERRABASE_SE15_REFERENCES` naming the directory the
    /// operator-side reference drivers wrote (`bench/results`). Without
    /// the variable the test asserts the fail-closed path and measures
    /// nothing, so an unprovisioned environment can never mint a verdict.
    #[test]
    fn the_se15_recorded_lane_run_classifies_both_references() {
        let Ok(references_dir) = std::env::var("TERRABASE_SE15_REFERENCES") else {
            // The fail-closed path: no reference documents exist, so the
            // classification must be inconclusive on every workload and
            // no TerraBase facts document may be written.
            let side = SideFacts {
                target: "terrabase".to_owned(),
                workload: "read".to_owned(),
                config: LaneConfig::RECORD_MINIMUM,
                observed_identity: "unmeasured".to_owned(),
                endpoint: "unmeasured".to_owned(),
                client_transport: "unmeasured".to_owned(),
                samples: Vec::new(),
            };
            let (run, _, status, verdict) = classify(&side, None);
            assert!(matches!(run, ReferenceRun::Unrun { .. }));
            assert_eq!((status, verdict), (ObjectiveStatus::Inconclusive, Verdict::Inconclusive));
            return;
        };
        let references_dir = std::path::PathBuf::from(references_dir);
        let source_revision =
            std::env::var("TERRABASE_SOURCE_REVISION").unwrap_or_else(|_| "unknown".to_owned());
        let config = LaneConfig::RECORD_MINIMUM;
        let workloads = [
            Workload::DocumentRead,
            Workload::DocumentWrite,
            Workload::IndexedQuery,
            Workload::Mixed,
        ];

        let mut receipt = String::from("# SE-15 comparison lane receipt\n\n");
        receipt.push_str(&format!(
            "- source revision: {source_revision}\n- lane: {:?}\n- noise percent: {}%\n- both sides ran on this host, the engine side in-process and the references over their pinned loopback transports; per-sample order alternation across sides was not possible across the process boundary\n- regression rule: {}\n\n",
            config_as_tuple(config),
            config.noise_percent,
            regression_rule()
        ));

        for workload in workloads {
            let name = workload.as_str();
            let lane = TerraBaseLane::open(&format!("se15-{name}"));
            lane.seed(config.seed_documents);
            let samples = sample_terrabase_side(&lane, workload, config);
            let side = SideFacts {
                target: "terrabase".to_owned(),
                workload: name.to_owned(),
                config,
                observed_identity: format!(
                    "terrabase {source_revision} (durable engine, Synced commits)"
                ),
                endpoint: lane.location_display(),
                client_transport: TERRABASE_TRANSPORT.to_owned(),
                samples,
            };
            lane.remove();

            let limits = [
                "the engine side is an in-process library boundary; the references serve over their pinned loopback transports, so the two sides differ in boundary, stated here rather than hidden".to_owned(),
                "the engine side runs one shared engine across all concurrency slots; the references run one independent connection per slot".to_owned(),
                "the engine query operation compiles its plan per operation (in-memory, no text protocol parse); PostgreSQL reuses a prepared statement".to_owned(),
                format!("single host, single run, {} samples per side; wall-clock numbers are facts about this host at this moment, not production claims", config.samples),
            ];
            let facts_json = render_facts_json(
                &side,
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .map_or(0, |since| since.as_secs()),
                &std::fs::read_to_string("/proc/sys/kernel/osrelease")
                    .map_or_else(|_| "unknown".to_owned(), |text| format!("Linux {}", text.trim())),
                std::thread::available_parallelism().map_or(0, std::num::NonZero::get),
                &limits,
            );
            let facts_path = references_dir.join(format!("terrabase-{name}.json"));
            std::fs::write(&facts_path, facts_json)
                .expect("the TerraBase facts document is writable");

            let postgresql_document = read_reference_document(&references_dir, "postgresql", name);
            let ravendb_document = read_reference_document(&references_dir, "ravendb", name);
            if let Some(note) = postgresql_document.note("postgresql") {
                receipt.push_str(&note);
                receipt.push('\n');
            }
            if let Some(note) = ravendb_document.note("ravendb") {
                receipt.push_str(&note);
                receipt.push('\n');
            }
            let postgresql = postgresql_document.side();
            let ravendb = ravendb_document.side();
            receipt.push_str(&render_workload_summary(name, &side, postgresql, ravendb));

            let (_, _, _, pg_verdict) = classify(&side, postgresql);
            let (_, _, _, raven_verdict) = classify(&side, ravendb);
            println!(
                "se15 lane {name}: terrabase median {:.0} ops/s (range {:.0}–{:.0}) — vs postgresql: {}, vs ravendb: {}",
                side.throughput().map_or(0.0, |m| m.median),
                side.throughput().map_or(0.0, |m| m.min),
                side.throughput().map_or(0.0, |m| m.max),
                pg_verdict.as_str(),
                raven_verdict.as_str(),
            );
        }

        let receipt_path =
            references_dir.join(format!("se15-comparison-receipt-{source_revision}.md"));
        std::fs::write(&receipt_path, receipt).expect("the receipt is writable");
        println!("receipt written: {}", receipt_path.display());
    }

    /// A cost probe for lane attribution: times the read, query, and write
    /// operations the lane drives, serially and concurrently, so a lane
    /// number can be attributed to its engine seam. Not a receipt, not a
    /// lane run.
    #[test]
    fn probe_lane_operation_costs() {
        use std::time::Instant;
        let lane = TerraBaseLane::open("probe");
        lane.seed(1_000);
        // Warm every path the lane drives.
        for index in 0..100 {
            let _ = lane.one_operation(Workload::DocumentRead, index);
            let _ = lane.one_operation(Workload::IndexedQuery, index);
        }

        let iterations = 1_000;
        let started = Instant::now();
        for index in 0..iterations {
            assert!(lane.one_operation(Workload::DocumentRead, index));
        }
        let elapsed = started.elapsed().as_secs_f64();
        println!(
            "probe serial point reads: {:.0} ops/s ({:.1} us/op)",
            iterations as f64 / elapsed,
            elapsed * 1e6 / iterations as f64
        );

        let started = Instant::now();
        for index in 0..200 {
            assert!(lane.one_operation(Workload::IndexedQuery, index));
        }
        let elapsed = started.elapsed().as_secs_f64();
        println!(
            "probe serial indexed queries: {:.0} ops/s ({:.1} us/op)",
            200.0 / elapsed,
            elapsed * 1e6 / 200.0
        );

        let started = Instant::now();
        for index in 0..200 {
            lane.one_operation(Workload::DocumentWrite, index);
        }
        let elapsed = started.elapsed().as_secs_f64();
        println!(
            "probe serial synced writes: {:.0} ops/s ({:.1} us/op)",
            200.0 / elapsed,
            elapsed * 1e6 / 200.0
        );

        let per_thread = iterations / 16;
        std::thread::scope(|scope| {
            let started = Instant::now();
            for _ in 0..16 {
                scope.spawn(|| {
                    for index in 0..per_thread {
                        assert!(lane.one_operation(Workload::DocumentRead, index));
                    }
                });
            }
            let elapsed = started.elapsed().as_secs_f64();
            println!("probe 16-thread point reads: {:.0} ops/s", iterations as f64 / elapsed);
        });

        let query_error = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            std::thread::scope(|scope| {
                let started = Instant::now();
                for _ in 0..16 {
                    scope.spawn(|| {
                        for index in 0..per_thread {
                            assert!(lane.one_operation(Workload::IndexedQuery, index));
                        }
                    });
                }
                let elapsed = started.elapsed().as_secs_f64();
                println!(
                    "probe 16-thread indexed queries: {:.0} ops/s",
                    iterations as f64 / elapsed
                );
            });
        }));
        if query_error.is_err() {
            println!("probe 16-thread indexed queries: PANICKED (see the panic hook output above)");
        }

        lane.remove();
    }

    /// Sub-call attribution for one point read, so the serial floor can be
    /// attributed to its exact seam.
    #[test]
    fn probe_read_subcall_costs() {
        use std::time::Instant;
        let lane = TerraBaseLane::open("subcall");
        lane.seed(1_000);
        let engine = lane.engine();
        let scope = lane.scope();

        let mut snapshot_ns = 0_u128;
        let mut begin_ns = 0_u128;
        let mut read_ns = 0_u128;
        let mut drop_ns = 0_u128;
        for index in 0..300_u64 {
            let started = Instant::now();
            let snapshot = engine.read_snapshot(scope).expect("snapshot");
            snapshot_ns += started.elapsed().as_nanos();

            let started = Instant::now();
            let operation = OperationIdProbe::new(index);
            let transaction = engine.begin(scope, snapshot, operation).expect("begins");
            begin_ns += started.elapsed().as_nanos();

            let started = Instant::now();
            let document = transaction.read(&DocumentIdProbe::of(index)).expect("reads");
            read_ns += started.elapsed().as_nanos();
            assert!(document.is_some());

            let started = Instant::now();
            drop(transaction);
            drop_ns += started.elapsed().as_nanos();
        }
        println!(
            "probe read subcalls over 300 ops: snapshot {:.1} us/op, begin {:.1} us/op, read {:.1} us/op, drop {:.1} us/op",
            snapshot_ns as f64 / 300.0 / 1_000.0,
            begin_ns as f64 / 300.0 / 1_000.0,
            read_ns as f64 / 300.0 / 1_000.0,
            drop_ns as f64 / 300.0 / 1_000.0,
        );
        lane.remove();
    }

    /// The probe's private operation-id helper.
    struct OperationIdProbe;

    impl OperationIdProbe {
        fn new(index: u64) -> crate::types::OperationId {
            crate::types::OperationId::try_new(&format!("probe-read-{index}"))
                .expect("valid identifier")
        }
    }

    /// The probe's private document-id helper.
    struct DocumentIdProbe;

    impl DocumentIdProbe {
        fn of(index: u64) -> crate::types::DocumentId {
            crate::types::DocumentId::try_new(&format!("seed-{}", index % 1_000))
                .expect("valid identifier")
        }
    }

    /// Durability-level attribution for the write path: the same commit
    /// shape at Buffered, Written, and Synced, so the per-level cost is
    /// visible.
    #[test]
    fn probe_write_durability_costs() {
        use std::time::Instant;
        for (tag, durability) in [
            ("buffered", crate::types::LocalDurability::Buffered),
            ("written", crate::types::LocalDurability::Written),
            ("synced", crate::types::LocalDurability::Synced),
        ] {
            let lane = TerraBaseLane::open(&format!("durability-{tag}"));
            lane.seed(1_000);
            let started = Instant::now();
            for index in 0..100_u64 {
                lane.commit_put_measured(index, durability);
            }
            let elapsed = started.elapsed().as_secs_f64();
            println!(
                "probe {tag} commits: {:.0} ops/s ({:.1} us/op)",
                100.0 / elapsed,
                elapsed * 1e6 / 100.0
            );
            lane.remove();
        }
    }

    /// Concurrent synced-write probe: the group-commit shape the lane
    /// drives (16 slots × barrier windows), reduced to its essentials.
    #[test]
    fn probe_concurrent_synced_writes() {
        use std::time::Instant;
        let lane = TerraBaseLane::open("concurrent-writes");
        lane.seed(1_000);
        let per_thread = 100_u64;
        let started = Instant::now();
        let lane_ref = &lane;
        std::thread::scope(|scope| {
            for worker in 0..16_u64 {
                scope.spawn(move || {
                    for index in 0..per_thread {
                        lane_ref.commit_put_measured(
                            worker * per_thread + index,
                            crate::types::LocalDurability::Synced,
                        );
                    }
                });
            }
        });
        let elapsed = started.elapsed().as_secs_f64();
        println!(
            "probe 16-thread synced writes: {:.0} ops/s ({:.1} us/op amortized)",
            (16 * per_thread) as f64 / elapsed,
            elapsed * 1e6 / (16 * per_thread) as f64
        );
        lane.remove();
    }

    /// Aggregate durable-write probe across independent SQLite shard files.
    /// Each lane owns its writer and commit queue; the wall clock covers all
    /// lanes so the result characterizes physical shard parallelism rather
    /// than one-file serialization.
    #[test]
    fn probe_parallel_synced_shards() {
        const SHARDS: usize = 16;
        const WRITES_PER_SHARD: u64 = 100;
        let lanes: Vec<_> = (0..SHARDS)
            .map(|index| TerraBaseLane::open(&format!("parallel-shard-{index}")))
            .collect();
        let started = Instant::now();
        std::thread::scope(|scope| {
            for (shard, lane) in lanes.iter().enumerate() {
                scope.spawn(move || {
                    for index in 0..WRITES_PER_SHARD {
                        lane.commit_put_measured(
                            (shard as u64) * WRITES_PER_SHARD + index,
                            crate::types::LocalDurability::Synced,
                        );
                    }
                });
            }
        });
        let elapsed = started.elapsed().as_secs_f64();
        let operations = (SHARDS as u64) * WRITES_PER_SHARD;
        println!(
            "probe {SHARDS}-shard synced writes: {:.0} ops/s ({:.1} us/op)",
            operations as f64 / elapsed,
            elapsed * 1e6 / operations as f64
        );
        for lane in lanes {
            lane.remove();
        }
    }

    /// Aggregate probe combining shard lanes with first-class batch writes.
    /// This measures logical operations, while each lane pays one FULL WAL
    /// flush per batch.
    #[test]
    fn probe_parallel_batched_synced_shards() {
        const SHARDS: usize = 16;
        const BATCHES_PER_SHARD: u64 = 8;
        const WRITES_PER_BATCH: u64 = 64;
        let lanes: Vec<_> = (0..SHARDS)
            .map(|index| TerraBaseLane::open(&format!("parallel-batch-shard-{index}")))
            .collect();
        let started = Instant::now();
        std::thread::scope(|scope| {
            for (shard, lane) in lanes.iter().enumerate() {
                scope.spawn(move || {
                    for batch in 0..BATCHES_PER_SHARD {
                        let start = ((shard as u64) * BATCHES_PER_SHARD + batch) * WRITES_PER_BATCH;
                        lane.commit_batch_measured(
                            start,
                            WRITES_PER_BATCH,
                            crate::types::LocalDurability::Synced,
                        );
                    }
                });
            }
        });
        let operations = (SHARDS as u64) * BATCHES_PER_SHARD * WRITES_PER_BATCH;
        let elapsed = started.elapsed().as_secs_f64();
        println!(
            "probe {SHARDS}-shard batched synced writes: {:.0} ops/s ({:.1} us/op)",
            operations as f64 / elapsed,
            elapsed * 1e6 / operations as f64
        );
        for lane in lanes {
            lane.remove();
        }
    }

    fn config_as_tuple(config: LaneConfig) -> String {
        format!(
            "seed {} / ops {} / samples {} / concurrency {} / warmup {}",
            config.seed_documents,
            config.operations,
            config.samples,
            config.concurrency,
            config.warmup_operations
        )
    }
}
