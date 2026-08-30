//! Strict, Rust-owned rendering of retained benchmark history.

use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use serde_json::Value;

const WORKLOADS: [&str; 4] = ["read", "write", "query", "mixed"];
const TARGETS: [&str; 3] = ["terrabase", "postgresql", "ravendb"];

#[derive(Clone, Debug)]
struct Sample {
    wall_seconds: f64,
    throughput: f64,
    p50: f64,
    p95: f64,
    p99: f64,
}

#[derive(Debug)]
struct Complete {
    identity: String,
    samples: Vec<Sample>,
}

#[derive(Debug)]
enum Loaded {
    Missing,
    Failed,
    Invalid(String),
    Complete(Complete),
}

pub fn run(arguments: impl Iterator<Item = String>) -> Result<PathBuf, String> {
    let mut results = PathBuf::from("bench/results");
    let mut history = PathBuf::from("docs/evidence/benchmarks/history");
    let mut args = arguments.peekable();
    while let Some(argument) = args.next() {
        let value = match argument.as_str() {
            "--results" => args.next().ok_or("--results requires a directory")?,
            "--history-dir" => args.next().ok_or("--history-dir requires a directory")?,
            other => return Err(format!("unknown --render-history argument {other:?}")),
        };
        match argument.as_str() {
            "--results" => results = PathBuf::from(value),
            "--history-dir" => history = PathBuf::from(value),
            _ => unreachable!(),
        }
    }
    let mut rows = Vec::new();
    let mut identities = Vec::new();
    let mut found = false;
    for workload in WORKLOADS {
        for target in TARGETS {
            match load(&results, target, workload) {
                Loaded::Missing => {
                    rows.push(format!("| {workload} | {target} | missing | — | — | — | — | — |"))
                }
                Loaded::Failed => {
                    found = true;
                    rows.push(format!("| {workload} | {target} | failed | — | — | — | — | — |"));
                }
                Loaded::Invalid(error) => {
                    found = true;
                    rows.push(format!("| {workload} | {target} | invalid | — | — | — | — | — |"));
                    eprintln!("invalid {target}/{workload} result: {error}");
                }
                Loaded::Complete(document) => {
                    found = true;
                    identities.push((target, document.identity));
                    rows.push(render_row(workload, target, &document.samples));
                }
            }
        }
    }
    if !found {
        return Err(format!("no results documents found under {}", results.display()));
    }
    let mut output = String::new();
    output.push_str("# Benchmark history\n\n");
    output.push_str("Every row is derived from a lane results document under `bench/results/`\n");
    output.push_str("by `bench/scripts/render-history.py` — never hand-edited. Verdicts and\n");
    output.push_str("receipts are produced only by the engine-side adapter\n");
    output.push_str("(`src/benchmarks/`); this table tabulates facts. A wall-clock number here\n");
    output.push_str("is a fact about the host and moment that produced it, per the limits each\n");
    output.push_str("results document carries.\n\n");
    output.push_str("Lane configuration: seed 1,000 documents · 10,000 operations per sample ·\n");
    output.push_str("5 samples · concurrency 16 · 1,000 warm-up operations · 5% noise band,\n");
    output.push_str("per `LaneConfig::RECORD_MINIMUM`.\n\n## Sides\n\n");
    for (target, identity) in identities {
        output.push_str(&format!("- **{target}**: {identity}\n"));
    }
    output.push_str("\n## Results (median across samples)\n\n");
    output.push_str("| workload | side | status | ops/s | p50 µs (median–max) | p95 µs | p99 µs | sample wall ms |\n");
    output.push_str("| --- | --- | --- | --- | --- | --- | --- | --- |\n");
    for row in rows {
        output.push_str(&row);
        output.push('\n');
    }
    output.push('\n');
    let path = write_retained(&history, &output)?;
    println!("wrote {} with {} rows", path.display(), WORKLOADS.len() * TARGETS.len());
    Ok(path)
}

fn load(results: &Path, target: &str, workload: &str) -> Loaded {
    let prefix = format!("{target}-{workload}-");
    let mut attempts = Vec::new();
    let Ok(entries) = fs::read_dir(results) else {
        return Loaded::Missing;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        let name = entry.file_name().to_string_lossy().into_owned();
        if name.starts_with(&prefix) && name.ends_with(".json") {
            attempts.push(path);
        }
    }
    if attempts.is_empty() {
        let legacy = results.join(format!("{target}-{workload}.json"));
        if legacy.is_file() {
            attempts.push(legacy);
        } else {
            return Loaded::Missing;
        }
    }
    attempts.sort_by_key(|path| attempt_key(path, &prefix));
    let path = attempts.pop().expect("attempt list is not empty");
    let text = match fs::read_to_string(&path) {
        Ok(text) => text,
        Err(error) => return Loaded::Invalid(format!("cannot read {}: {error}", path.display())),
    };
    let document: Value = match serde_json::from_str(&text) {
        Ok(document) => document,
        Err(error) => return Loaded::Invalid(format!("cannot parse {}: {error}", path.display())),
    };
    let Some(fields) = document.as_object() else {
        return Loaded::Invalid("document is not an object".to_owned());
    };
    match fields.get("status").and_then(Value::as_str) {
        Some("failed") => return Loaded::Failed,
        Some("complete") | None => {}
        Some(status) => return Loaded::Invalid(format!("invalid status {status:?}")),
    }
    match validate_complete(&document, target, workload) {
        Ok(complete) => Loaded::Complete(complete),
        Err(error) => Loaded::Invalid(error),
    }
}

fn attempt_key(path: &Path, prefix: &str) -> (u8, u128, String) {
    let name = path.file_name().and_then(|name| name.to_str()).unwrap_or_default();
    let stamp = name
        .strip_prefix(prefix)
        .and_then(|name| name.strip_suffix(".json"))
        .and_then(|name| name.split('-').next())
        .and_then(|stamp| stamp.parse::<u128>().ok());
    match stamp {
        Some(stamp) => (1, stamp, name.to_owned()),
        None => (
            0,
            path.metadata()
                .and_then(|metadata| metadata.modified())
                .ok()
                .and_then(|time| time.duration_since(UNIX_EPOCH).ok())
                .map_or(0, |duration| duration.as_nanos()),
            name.to_owned(),
        ),
    }
}

fn validate_complete(document: &Value, target: &str, workload: &str) -> Result<Complete, String> {
    let fields = document.as_object().ok_or("document is not an object")?;
    if fields.get("target").and_then(Value::as_str) != Some(target)
        || fields.get("workload").and_then(Value::as_str) != Some(workload)
    {
        return Err("identity does not match its lane".to_owned());
    }
    let config =
        fields.get("config").and_then(Value::as_object).ok_or("config is not an object")?;
    let positive = |name| -> Result<u64, String> {
        let value = number(config.get(name), &format!("config.{name}"))?;
        if value <= 0.0 || value.fract() != 0.0 {
            return Err(format!("config.{name} is not positive"));
        }
        Ok(value as u64)
    };
    let _seed = positive("seed_documents")?;
    let operations = positive("operations")?;
    let samples_count = positive("samples")? as usize;
    let concurrency = positive("concurrency")?;
    let warmup = number(config.get("warmup_operations"), "config.warmup_operations")?;
    let noise = number(config.get("noise_percent"), "config.noise_percent")?;
    if warmup < 0.0 || warmup.fract() != 0.0 || !(0.0..=100.0).contains(&noise) {
        return Err("invalid lane configuration".to_owned());
    }
    if !operations.is_multiple_of(concurrency) || !(warmup as u64).is_multiple_of(concurrency) {
        return Err("config operation counts are not divisible by concurrency".to_owned());
    }
    let environment = fields
        .get("environment")
        .and_then(Value::as_object)
        .ok_or("environment is not an object")?;
    let string = |name| -> Result<String, String> {
        let value = environment
            .get(name)
            .and_then(Value::as_str)
            .filter(|value| !value.is_empty())
            .ok_or_else(|| format!("environment.{name} is not a non-empty string"))?;
        Ok(value.to_owned())
    };
    let identity = string("observed_identity")?;
    string("endpoint")?;
    let transport = string("client_transport")?;
    string("kernel")?;
    if transport.to_ascii_lowercase().contains("in-process") {
        return Err("in-process transport cannot be compared with served references".to_owned());
    }
    let unix = number(environment.get("unix_seconds"), "environment.unix_seconds")?;
    let cpus = number(environment.get("logical_cpus"), "environment.logical_cpus")?;
    if unix <= 0.0 || unix.fract() != 0.0 || cpus <= 0.0 || cpus.fract() != 0.0 {
        return Err("invalid environment counters".to_owned());
    }
    let values =
        fields.get("samples").and_then(Value::as_array).ok_or("samples is not an array")?;
    if values.len() != samples_count {
        return Err(format!("samples must contain exactly {samples_count} entries"));
    }
    let mut samples = Vec::with_capacity(values.len());
    for (index, value) in values.iter().enumerate() {
        let sample = value.as_object().ok_or_else(|| format!("sample {index} is not an object"))?;
        let sample_operations =
            number(sample.get("operations"), &format!("sample {index}.operations"))?;
        let wall = number(sample.get("wall_seconds"), &format!("sample {index}.wall_seconds"))?;
        let throughput = number(
            sample.get("throughput_ops_per_s"),
            &format!("sample {index}.throughput_ops_per_s"),
        )?;
        let p50 = number(sample.get("p50_us"), &format!("sample {index}.p50_us"))?;
        let p95 = number(sample.get("p95_us"), &format!("sample {index}.p95_us"))?;
        let p99 = number(sample.get("p99_us"), &format!("sample {index}.p99_us"))?;
        if sample_operations != operations as f64
            || sample_operations.fract() != 0.0
            || wall <= 0.0
            || throughput <= 0.0
            || p50 < 0.0
            || p95 < p50
            || p99 < p95
        {
            return Err(format!("sample {index} has invalid timing or percentile values"));
        }
        let expected = operations as f64 / wall;
        if (throughput - expected).abs() / expected.max(f64::MIN_POSITIVE) > 1.0e-6 {
            return Err(format!("sample {index} throughput does not match wall_seconds"));
        }
        samples.push(Sample { wall_seconds: wall, throughput, p50, p95, p99 });
    }
    Ok(Complete { identity, samples })
}

fn number(value: Option<&Value>, name: &str) -> Result<f64, String> {
    let value = value.and_then(Value::as_f64).ok_or_else(|| format!("{name} is not a number"))?;
    value.is_finite().then_some(value).ok_or_else(|| format!("{name} is not finite"))
}

fn median(values: &[f64]) -> f64 {
    let mut values = values.to_vec();
    values.sort_by(f64::total_cmp);
    let middle = values.len() / 2;
    if values.len().is_multiple_of(2) {
        (values[middle - 1] + values[middle]) / 2.0
    } else {
        values[middle]
    }
}

fn render_row(workload: &str, target: &str, samples: &[Sample]) -> String {
    let throughputs: Vec<_> = samples.iter().map(|sample| sample.throughput).collect();
    let p50s: Vec<_> = samples.iter().map(|sample| sample.p50).collect();
    let p95s: Vec<_> = samples.iter().map(|sample| sample.p95).collect();
    let p99s: Vec<_> = samples.iter().map(|sample| sample.p99).collect();
    let walls: Vec<_> = samples.iter().map(|sample| sample.wall_seconds).collect();
    let p50_max = p50s.iter().copied().fold(f64::NEG_INFINITY, f64::max);
    format!(
        "| {workload} | {target} | complete | {:.0} | {:.0}–{:.0} | {:.0} | {:.0} | {:.0} |",
        median(&throughputs),
        median(&p50s),
        p50_max,
        median(&p95s),
        median(&p99s),
        median(&walls) * 1000.0
    )
}

fn write_retained(directory: &Path, contents: &str) -> Result<PathBuf, String> {
    fs::create_dir_all(directory).map_err(|error| format!("create history directory: {error}"))?;
    let now = SystemTime::now().duration_since(UNIX_EPOCH).map_err(|error| error.to_string())?;
    for attempt in 0..100 {
        let path = directory.join(format!(
            "benchmark-{}{:09}Z{}.md",
            now.as_secs(),
            now.subsec_nanos(),
            if attempt == 0 { String::new() } else { format!("-{attempt}") }
        ));
        match OpenOptions::new().write(true).create_new(true).open(&path) {
            Ok(mut file) => {
                file.write_all(contents.as_bytes()).map_err(|error| error.to_string())?;
                return Ok(path);
            }
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(error) => return Err(format!("create retained history: {error}")),
        }
    }
    Err("could not allocate a unique benchmark history filename".to_owned())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::BTreeMap;

    fn temporary_results() -> PathBuf {
        let path = std::env::temp_dir().join(format!(
            "terrabase-history-tests-{}-{}",
            std::process::id(),
            SystemTime::now().duration_since(UNIX_EPOCH).expect("clock").as_nanos()
        ));
        fs::create_dir(&path).expect("temporary result directory");
        path
    }

    fn valid() -> Value {
        serde_json::json!({
            "status":"complete", "target":"postgresql", "workload":"read",
            "config":{"seed_documents":1,"operations":2,"samples":1,"concurrency":1,"warmup_operations":0,"noise_percent":5.0},
            "environment":{"observed_identity":"PostgreSQL test","endpoint":"tcp","client_transport":"TCP","unix_seconds":1,"kernel":"Linux test","logical_cpus":1},
            "samples":[{"operations":2,"wall_seconds":1.0,"throughput_ops_per_s":2.0,"p50_us":1.0,"p95_us":2.0,"p99_us":3.0}],
        })
    }

    #[test]
    fn complete_document_is_admitted() {
        assert!(validate_complete(&valid(), "postgresql", "read").is_ok());
    }

    #[test]
    fn hostile_documents_are_rejected() {
        let mut cases = BTreeMap::new();
        let mut empty = valid();
        empty["samples"] = serde_json::json!([]);
        cases.insert("empty", empty);
        let mut mismatch = valid();
        mismatch["samples"][0]["throughput_ops_per_s"] = serde_json::json!(3.0);
        cases.insert("throughput", mismatch);
        let mut boundary = valid();
        boundary["environment"]["client_transport"] = serde_json::json!("in-process");
        cases.insert("boundary", boundary);
        for (name, value) in cases {
            assert!(validate_complete(&value, "postgresql", "read").is_err(), "{name}");
        }
    }

    #[test]
    fn newest_failed_attempt_blocks_an_older_complete_attempt() {
        let directory = temporary_results();
        fs::write(
            directory.join("postgresql-read-100-1-0.json"),
            serde_json::to_vec(&valid()).expect("valid JSON"),
        )
        .expect("old result");
        fs::write(directory.join("postgresql-read-200-1-0.json"), r#"{"status":"failed"}"#)
            .expect("failed result");
        assert!(matches!(load(&directory, "postgresql", "read"), Loaded::Failed));
        fs::remove_dir_all(directory).expect("remove temporary results");
    }

    #[test]
    fn newest_malformed_attempt_blocks_an_older_complete_attempt() {
        let directory = temporary_results();
        fs::write(
            directory.join("postgresql-read-100-1-0.json"),
            serde_json::to_vec(&valid()).expect("valid JSON"),
        )
        .expect("old result");
        fs::write(directory.join("postgresql-read-200-1-0.json"), b"{broken")
            .expect("malformed result");
        assert!(matches!(load(&directory, "postgresql", "read"), Loaded::Invalid(_)));
        fs::remove_dir_all(directory).expect("remove temporary results");
    }
}
