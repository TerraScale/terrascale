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

use std::sync::{Arc, Barrier};
use std::time::Instant;

use super::{
    driver::TerraBaseLane,
    gates::{self, MedianRange, ReferenceComparison},
    lane::{LaneConfig, Workload},
    record::REGRESSION_RULE,
    vocabulary::{objective_status_line, ObjectiveStatus, ReferenceRun, Verdict},
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
        latencies_us.sort_by(|left, right| left.total_cmp(right));
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
pub(crate) fn sample_terrabase_side(lane: &TerraBaseLane, workload: Workload, config: LaneConfig) -> Vec<SampleFacts> {
    let per_slot_operations = config.operations / config.concurrency;
    let per_slot_warmup = config.warmup_operations / config.concurrency;
    let start = Arc::new(Barrier::new(config.concurrency + 1));
    let end = Arc::new(Barrier::new(config.concurrency + 1));

    std::thread::scope(|scope| -> Vec<SampleFacts> {
        let mut worker_samples = Vec::with_capacity(config.concurrency);
        for _ in 0..config.concurrency {
            let start = Arc::clone(&start);
            let end = Arc::clone(&end);
            worker_samples.push(scope.spawn(move || -> Vec<Vec<f64>> {
                // Warm-up: real work, excluded from every measured sample.
                for index in 0..per_slot_warmup {
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
        let joined: Vec<Vec<Vec<f64>>> =
            worker_samples.into_iter().map(|handle| handle.join().expect("a lane worker panicked")).collect();
        walls
            .into_iter()
            .enumerate()
            .map(|(sample, wall)| {
                let mut latencies = Vec::with_capacity(per_slot_operations * config.concurrency);
                for slot in &joined {
                    latencies.extend_from_slice(&slot[sample]);
                }
                SampleFacts::of(
                    (per_slot_operations * config.concurrency) as u64,
                    wall,
                    latencies,
                )
            })
            .collect()
    })
}

/// The engine-side transport description recorded on the TerraBase facts
/// document, so a receipt cannot mistake the boundary for a network one.
pub(crate) const TERRABASE_TRANSPORT: &str =
    "in-process durable engine library, LocalDurability::Synced commits, one shared engine across all concurrency slots";

/// Classifies the TerraBase side against one reference side under the
/// SE-15 gate, fail-closed on an absent side.
pub(crate) fn classify(engine: &SideFacts, reference: Option<&SideFacts>) -> (ReferenceRun, ReferenceComparison, ObjectiveStatus, Verdict) {
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
    let _ = writeln!(text, "| side | median ops/s | sample range ops/s | p50 us | p95 us | p99 us |");
    let _ = writeln!(text, "| --- | --- | --- | --- | --- | --- |");
    for (name, side) in [("terrabase", Some(engine)), ("postgresql", postgresql), ("ravendb", ravendb)] {
        let Some(side) = side else { continue };
        let tps = side.throughput().unwrap_or(MedianRange { median: 0.0, min: 0.0, max: 0.0 });
        let median_p50 = MedianRange::of(&side.samples.iter().map(|s| s.p50_us).collect::<Vec<_>>());
        let median_p95 = MedianRange::of(&side.samples.iter().map(|s| s.p95_us).collect::<Vec<_>>());
        let median_p99 = MedianRange::of(&side.samples.iter().map(|s| s.p99_us).collect::<Vec<_>>());
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
    let _ = writeln!(text, "- objective ({}): `{}`", objective_status_line(pg_status), pg_verdict.as_str());
    let _ = writeln!(text, "- versus ravendb: `{}` ({})", raven_verdict.as_str(), raven_status.as_str());
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
                            return Err(Error(format!("expected ',' or ']' in array, found {other:?}")))
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
                            return Err(Error(format!("expected ',' or '}}' in object, found {other:?}")))
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
                                code = code * 16 + digit.to_digit(16).ok_or_else(|| Error("bad hex digit".to_owned()))?;
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
            fields.get(key).and_then(|v| match v {
                Value::Str(text) => Some(text.clone()),
                _ => None,
            }).ok_or_else(|| Error(format!("field {key} is not a string")))
        };
        let environment = match fields.get("environment") {
            Some(Value::Obj(environment)) => environment,
            _ => return Err(Error("field environment is not an object".to_owned())),
        };
        let environment_string = |key: &str| -> Result<String, Error> {
            environment.get(key).and_then(|v| match v {
                Value::Str(text) => Some(text.clone()),
                _ => None,
            }).ok_or_else(|| Error(format!("field environment.{key} is not a string")))
        };
        let config = match fields.get("config") {
            Some(Value::Obj(config)) => config,
            _ => return Err(Error("field config is not an object".to_owned())),
        };
        let config_number = |key: &str| -> Result<f64, Error> {
            config.get(key).and_then(|v| match v {
                Value::Num(value) => Some(*value),
                _ => None,
            }).ok_or_else(|| Error(format!("field config.{key} is not a number")))
        };
        let samples = match fields.get("samples") {
            Some(Value::Arr(samples)) => samples,
            _ => return Err(Error("field samples is not an array".to_owned())),
        };
        let mut parsed_samples = Vec::with_capacity(samples.len());
        for (index, sample) in samples.iter().enumerate() {
            let Value::Obj(sample) = sample else {
                return Err(Error(format!("sample {index} is not an object")));
            };
            let sample_number = |key: &str| -> Result<f64, Error> {
                sample.get(key).and_then(|v| match v {
                    Value::Num(value) => Some(*value),
                    _ => None,
                }).ok_or_else(|| Error(format!("sample {index} field {key} is not a number")))
            };
            parsed_samples.push(SampleFacts {
                operations: sample_number("operations")? as u64,
                wall_seconds: sample_number("wall_seconds")?,
                throughput_ops_per_s: sample_number("throughput_ops_per_s")?,
                p50_us: sample_number("p50_us")?,
                p95_us: sample_number("p95_us")?,
                p99_us: sample_number("p99_us")?,
            });
        }
        Ok(super::SideFacts {
            target: string("target")?,
            workload: string("workload")?,
            config: LaneConfig {
                seed_documents: config_number("seed_documents")? as usize,
                operations: config_number("operations")? as usize,
                samples: config_number("samples")? as usize,
                concurrency: config_number("concurrency")? as usize,
                warmup_operations: config_number("warmup_operations")? as usize,
                noise_percent: config_number("noise_percent")?,
            },
            observed_identity: environment_string("observed_identity")?,
            endpoint: environment_string("endpoint")?,
            client_transport: environment_string("client_transport")?,
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
    if value.is_finite() {
        format!("{value}")
    } else {
        "0".to_owned()
    }
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
            config: LaneConfig::RECORD_MINIMUM,
            observed_identity: "PostgreSQL 18.6".to_owned(),
            endpoint: "postgres://bench@127.0.0.1:55432/postgres".to_owned(),
            client_transport: "test transport".to_owned(),
            samples: vec![
                SampleFacts { operations: 10, wall_seconds: 0.1, throughput_ops_per_s: 100.0, p50_us: 10.0, p95_us: 20.0, p99_us: 30.0 },
                SampleFacts { operations: 10, wall_seconds: 0.1, throughput_ops_per_s: 102.0, p50_us: 10.0, p95_us: 20.0, p99_us: 30.0 },
                SampleFacts { operations: 10, wall_seconds: 0.1, throughput_ops_per_s: 101.0, p50_us: 10.0, p95_us: 20.0, p99_us: 30.0 },
            ],
        }
    }

    #[test]
    fn the_reader_round_trips_the_writer() {
        let side = facts();
        let text = render_facts_json(&side, 1_700_000_000, "Linux test", 8, &["limit one".to_owned()]);
        let parsed = json::parse(&text).expect("renders parseable JSON");
        let read = json::reference_side(&parsed).expect("extracts the side");
        assert_eq!(read.target, side.target);
        assert_eq!(read.workload, side.workload);
        assert_eq!(read.samples.len(), side.samples.len());
        assert_eq!(read.samples[0].throughput_ops_per_s, side.samples[0].throughput_ops_per_s);
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
            samples: (0..5)
                .map(|index| SampleFacts {
                    operations: 10,
                    wall_seconds: 0.01,
                    throughput_ops_per_s: 1_000.0 + index as f64,
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
        assert_eq!(verdict, Verdict::V2Faster);
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

        let read_reference = |target: &str, workload: &str| -> Option<SideFacts> {
            let path = references_dir.join(format!("{target}-{workload}.json"));
            let text = std::fs::read_to_string(&path).expect("the reference results file is readable");
            let parsed = json::parse(&text).expect("the reference results file is valid JSON");
            Some(json::reference_side(&parsed).expect("the reference results file matches the schema"))
        };

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
                observed_identity: format!("terrabase {source_revision} (durable engine, Synced commits)"),
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
                    .map(|since| since.as_secs())
                    .unwrap_or(0),
                &std::fs::read_to_string("/proc/sys/kernel/osrelease")
                    .map(|text| format!("Linux {}", text.trim()))
                    .unwrap_or_else(|_| "unknown".to_owned()),
                std::thread::available_parallelism().map(|count| count.get()).unwrap_or(0),
                &limits,
            );
            let facts_path = references_dir.join(format!("terrabase-{name}.json"));
            std::fs::write(&facts_path, facts_json).expect("the TerraBase facts document is writable");

            let postgresql = read_reference("postgresql", name);
            let ravendb = read_reference("ravendb", name);
            receipt.push_str(&render_workload_summary(name, &side, postgresql.as_ref(), ravendb.as_ref()));

            let (_, _, _, pg_verdict) = classify(&side, postgresql.as_ref());
            let (_, _, _, raven_verdict) = classify(&side, ravendb.as_ref());
            println!(
                "se15 lane {name}: terrabase median {:.0} ops/s (range {:.0}–{:.0}) — vs postgresql: {}, vs ravendb: {}",
                side.throughput().map_or(0.0, |m| m.median),
                side.throughput().map_or(0.0, |m| m.min),
                side.throughput().map_or(0.0, |m| m.max),
                pg_verdict.as_str(),
                raven_verdict.as_str(),
            );
        }

        let receipt_path = references_dir.join(format!("se15-comparison-receipt-{source_revision}.md"));
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
                println!("probe 16-thread indexed queries: {:.0} ops/s", iterations as f64 / elapsed);
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
            crate::types::OperationId::try_new(&format!("probe-read-{index}")).expect("valid identifier")
        }
    }

    /// The probe's private document-id helper.
    struct DocumentIdProbe;

    impl DocumentIdProbe {
        fn of(index: u64) -> crate::types::DocumentId {
            crate::types::DocumentId::try_new(&format!("seed-{}", index % 1_000)).expect("valid identifier")
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

    fn config_as_tuple(config: LaneConfig) -> String {
        format!(
            "seed {} / ops {} / samples {} / concurrency {} / warmup {}",
            config.seed_documents, config.operations, config.samples, config.concurrency, config.warmup_operations
        )
    }
}
