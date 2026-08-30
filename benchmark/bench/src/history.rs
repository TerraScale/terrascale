//! Strict, Rust-owned rendering of retained benchmark history.

use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use serde_json::Value;

use crate::config::LaneConfig;
use crate::metrics;

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
    comparability: Comparability,
}

#[derive(Clone, Debug, PartialEq)]
struct Comparability {
    run_digest: String,
    candidate_digest: String,
    workload_digest: String,
    topology_digest: String,
    run_group: String,
    config_digest: String,
    controlled_host_identity: String,
    time_window: String,
    window_start_unix_seconds: u64,
    window_end_unix_seconds: u64,
    workload_version: String,
    boundary_class: String,
    concurrency: u64,
    samples: u64,
    warmup_operations: u64,
    noise_percent: f64,
    seed_policy: String,
    document_policy: String,
    operation_policy: String,
    write_id_policy: String,
    durability_semantics: String,
    server_configuration_provenance: String,
}

#[derive(Debug)]
struct Failure {
    path: String,
    error: Option<String>,
}

#[derive(Debug)]
enum Loaded {
    Missing,
    Failed(Failure),
    Invalid(Failure),
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
    let mut loaded = Vec::new();
    let mut found = false;
    for workload in WORKLOADS {
        for target in TARGETS {
            let document = load(&results, target, workload);
            found |= !matches!(document, Loaded::Missing);
            loaded.push((workload, target, document));
        }
    }
    if !found {
        return Err(format!("no results documents found under {}", results.display()));
    }
    // A document is only comparable with another document when every
    // admission fact agrees.  Keep the check workload-local so a read run
    // cannot accidentally admit a write run with different provenance.
    let mut group_errors = std::collections::BTreeMap::new();
    for workload in WORKLOADS {
        let complete: Vec<_> = loaded
            .iter()
            .filter(|(candidate_workload, _, document)| {
                *candidate_workload == workload && matches!(document, Loaded::Complete(_))
            })
            .collect();
        if let Some((_, _, Loaded::Complete(first))) = complete.first() {
            for (_, target, document) in complete.iter().skip(1) {
                let Loaded::Complete(candidate) = document else { unreachable!() };
                if let Some(field) = differing_field(&first.comparability, &candidate.comparability)
                {
                    group_errors.insert(
                        workload,
                        format!("{target} differs from the comparison group in {field}"),
                    );
                    break;
                }
            }
        }
    }

    let complete_counts: std::collections::BTreeMap<_, _> = WORKLOADS
        .into_iter()
        .map(|workload| {
            let count = loaded
                .iter()
                .filter(|(candidate_workload, _, document)| {
                    *candidate_workload == workload && matches!(document, Loaded::Complete(_))
                })
                .count();
            (workload, count)
        })
        .collect();
    let mut rows = Vec::new();
    let mut identities = std::collections::BTreeMap::new();
    let mut admission = Vec::new();
    for (workload, target, document) in loaded {
        match document {
            Loaded::Missing => {
                rows.push(format!("| {workload} | {target} | missing | — | — | — | — | — |"))
            }
            Loaded::Failed(failure) => {
                rows.push(format!("| {workload} | {target} | failed | — | — | — | — | — |"));
                let error = failure.error.unwrap_or_else(|| "error not recorded".to_owned());
                admission.push(format!(
                    "- {target}/{workload}: failed/raw — `{}` — {}",
                    failure.path,
                    markdown_inline(&error)
                ));
            }
            Loaded::Invalid(error) => {
                rows.push(format!("| {workload} | {target} | invalid | — | — | — | — | — |"));
                let reason = error.error.as_deref().unwrap_or("error not recorded");
                eprintln!("invalid {target}/{workload} result: {reason}");
                admission.push(format!(
                    "- {target}/{workload}: invalid/raw — `{}` — {}",
                    error.path,
                    markdown_inline(reason)
                ));
            }
            Loaded::Complete(document) => {
                identities.entry(target).or_insert(document.identity.clone());
                if let Some(error) = group_errors.get(workload) {
                    rows.push(format!("| {workload} | {target} | invalid | — | — | — | — | — |"));
                    eprintln!("invalid {target}/{workload} result: {error}");
                    admission.push(format!("- {target}/{workload}: invalid — {error}"));
                } else {
                    rows.push(render_row(workload, target, &document.samples));
                }
            }
        }
    }
    for workload in WORKLOADS {
        let complete_count = complete_counts[workload];
        let status = if complete_count == 0 {
            "no complete comparison group"
        } else if group_errors.contains_key(workload) {
            "not comparable"
        } else {
            "admitted facts (no verdict)"
        };
        admission.push(format!("- {workload}: {status}"));
    }

    let mut output = String::new();
    output.push_str("# Benchmark history\n\n");
    output.push_str("Every row is derived from a lane results document under `bench/results/`\n");
    output
        .push_str("by `terrabase-bench-lane --render-history` — never hand-edited. Verdicts and\n");
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
    output.push_str("\n## Admission and comparability\n\n");
    output.push_str("Only rows with a valid comparability contract may join a workload comparison; no verdict is produced by this renderer. Failed/raw attempts remain retained and never contribute measurements.\n\n");
    for line in admission {
        output.push_str(&line);
        output.push('\n');
    }
    let retained_failures = retained_failures(&results);
    if !retained_failures.is_empty() {
        output.push_str("\n## Retained failed/raw attempts\n\n");
        output.push_str("These attempts are retained for audit and are never used as complete measurements.\n\n");
        for (path, error) in retained_failures {
            output.push_str(&format!("- `{}` — {}\n", path, markdown_inline(&error)));
        }
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
        Err(error) => {
            return Loaded::Invalid(Failure {
                path: path.display().to_string(),
                error: Some(format!("cannot read retained result: {error}")),
            });
        }
    };
    let document: Value = match serde_json::from_str(&text) {
        Ok(document) => document,
        Err(error) => {
            return Loaded::Invalid(Failure {
                path: path.display().to_string(),
                error: Some(format!("cannot parse retained result: {error}")),
            });
        }
    };
    let Some(fields) = document.as_object() else {
        return Loaded::Invalid(Failure {
            path: path.display().to_string(),
            error: Some("document is not an object".to_owned()),
        });
    };
    match fields.get("status").and_then(Value::as_str) {
        Some("failed") => {
            return Loaded::Failed(Failure {
                path: path.display().to_string(),
                error: fields.get("error").and_then(Value::as_str).map(str::to_owned),
            });
        }
        Some("complete") | None => {}
        Some(status) => {
            return Loaded::Invalid(Failure {
                path: path.display().to_string(),
                error: Some(format!("invalid status {status:?}")),
            });
        }
    }
    match validate_complete(&document, target, workload) {
        Ok(complete) => Loaded::Complete(complete),
        Err(error) => {
            Loaded::Invalid(Failure { path: path.display().to_string(), error: Some(error) })
        }
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
    positive("concurrency")?;
    let warmup = number(config.get("warmup_operations"), "config.warmup_operations")?;
    let noise = number(config.get("noise_percent"), "config.noise_percent")?;
    if warmup < 0.0 || warmup.fract() != 0.0 || !(0.0..=100.0).contains(&noise) {
        return Err("invalid lane configuration".to_owned());
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
    if target == "terrabase" && is_diagnostic_result(fields, &identity) {
        return Err("diagnostic qualification result is not decision-grade evidence".to_owned());
    }
    let comparability =
        validate_comparability(fields.get("comparability"), config, environment, target)?;
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
    Ok(Complete { identity, samples, comparability })
}

fn is_diagnostic_result(fields: &serde_json::Map<String, Value>, identity: &str) -> bool {
    const DIAGNOSTIC_SERVER_IDENTITY: &str = "tbep-qualification-harness/network-mtls/v1";
    const DIAGNOSTIC_SOURCE_IDENTITY: &str =
        "source identity: terrabase-rust-diagnostic-qualification/v1";
    identity == DIAGNOSTIC_SERVER_IDENTITY
        || fields.get("limits").and_then(Value::as_array).is_some_and(|limits| {
            limits.iter().any(|limit| limit.as_str() == Some(DIAGNOSTIC_SOURCE_IDENTITY))
        })
}

fn validate_comparability(
    value: Option<&Value>,
    config: &serde_json::Map<String, Value>,
    environment: &serde_json::Map<String, Value>,
    target: &str,
) -> Result<Comparability, String> {
    let fields = value.and_then(Value::as_object).ok_or("comparability is not an object")?;
    let string = |name: &str| -> Result<String, String> {
        fields
            .get(name)
            .and_then(Value::as_str)
            .filter(|text| !text.trim().is_empty())
            .map(str::to_owned)
            .ok_or_else(|| format!("comparability.{name} is not a non-empty string"))
    };
    let digest = |name: &str| -> Result<String, String> {
        let value = string(name)?;
        if value.len() != 64 || !value.bytes().all(|byte| byte.is_ascii_hexdigit()) {
            return Err(format!("comparability.{name} is not a SHA-256 digest"));
        }
        Ok(value)
    };
    // These four digests are the join key for decision-grade evidence. A
    // result can contain perfectly valid timings and still be diagnostic if
    // it cannot prove which run, candidate, workload, and topology produced
    // them.
    let run_digest = digest("run_digest")?;
    let candidate_digest = digest("candidate_digest")?;
    let workload_digest = digest("workload_digest")?;
    let topology_digest = digest("topology_digest")?;
    let run_group = string("run_group")?;
    if run_group == "unassigned" {
        return Err("comparability.run_group is unassigned".to_owned());
    }
    let config_digest = string("config_digest")?;
    if config_digest.len() != 64 || !config_digest.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err("comparability.config_digest is not a SHA-256 digest".to_owned());
    }
    let controlled_host_identity = string("controlled_host_identity")?;
    let time_window = string("time_window")?;
    let workload_version = string("workload_version")?;
    let boundary_class = string("boundary_class")?;
    if boundary_class.eq_ignore_ascii_case("in-process") {
        return Err("in-process boundary is not a served comparison boundary".to_owned());
    }
    let seed_policy = string("seed_policy")?;
    let document_policy = string("document_policy")?;
    let operation_policy = string("operation_policy")?;
    let write_id_policy = string("write_id_policy")?;
    let durability_semantics = string("durability_semantics")?;
    let server_configuration_provenance = string("server_configuration_provenance")?;
    let positive_u64 = |name: &str| -> Result<u64, String> {
        let value = number(fields.get(name), &format!("comparability.{name}"))?;
        if value <= 0.0 || value.fract() != 0.0 {
            return Err(format!("comparability.{name} is not positive"));
        }
        Ok(value as u64)
    };
    let concurrency = positive_u64("concurrency")?;
    let samples = positive_u64("samples")?;
    let warmup_operations =
        number(fields.get("warmup_operations"), "comparability.warmup_operations")?;
    if warmup_operations < 0.0 || warmup_operations.fract() != 0.0 {
        return Err("comparability.warmup_operations is invalid".to_owned());
    }
    let noise_percent = number(fields.get("noise_percent"), "comparability.noise_percent")?;
    if !(0.0..=100.0).contains(&noise_percent) {
        return Err("comparability.noise_percent is outside 0..=100".to_owned());
    }
    let window_start = positive_u64("window_start_unix_seconds")?;
    let window_end = positive_u64("window_end_unix_seconds")?;
    if window_end < window_start || window_end - window_start > 86_400 {
        return Err("comparability time window is invalid".to_owned());
    }
    if concurrency != positive_config(config, "concurrency")?
        || samples != positive_config(config, "samples")?
        || warmup_operations != number(config.get("warmup_operations"), "config.warmup_operations")?
        || noise_percent != number(config.get("noise_percent"), "config.noise_percent")?
    {
        return Err("comparability configuration does not match config".to_owned());
    }
    if config_digest != config_digest_for(config)? {
        return Err("comparability.config_digest does not match config".to_owned());
    }
    // The controlled identity may be an operator-assigned host UUID rather
    // than a kernel string.  Environment still validates the observed kernel
    // separately; equality of this explicit token is what joins a group.
    environment
        .get("kernel")
        .and_then(Value::as_str)
        .filter(|kernel| !kernel.is_empty())
        .ok_or("environment.kernel is not a non-empty string")?;
    if target == "terrabase" && boundary_class == "unknown" {
        return Err("TerraBase boundary class is unknown".to_owned());
    }
    if boundary_class == "diagnostic-qualification"
        || boundary_class.eq_ignore_ascii_case("diagnostic")
    {
        return Err("diagnostic qualification is not decision-grade evidence".to_owned());
    }
    Ok(Comparability {
        run_digest,
        candidate_digest,
        workload_digest,
        topology_digest,
        run_group,
        config_digest,
        controlled_host_identity,
        time_window,
        window_start_unix_seconds: window_start,
        window_end_unix_seconds: window_end,
        workload_version,
        boundary_class,
        concurrency,
        samples,
        warmup_operations: warmup_operations as u64,
        noise_percent,
        seed_policy,
        document_policy,
        operation_policy,
        write_id_policy,
        durability_semantics,
        server_configuration_provenance,
    })
}

fn positive_config(config: &serde_json::Map<String, Value>, name: &str) -> Result<u64, String> {
    let value = number(config.get(name), &format!("config.{name}"))?;
    if value <= 0.0 || value.fract() != 0.0 {
        return Err(format!("config.{name} is not positive"));
    }
    Ok(value as u64)
}

fn config_digest_for(config: &serde_json::Map<String, Value>) -> Result<String, String> {
    let lane = LaneConfig {
        seed_documents: positive_config(config, "seed_documents")? as usize,
        operations: positive_config(config, "operations")? as usize,
        samples: positive_config(config, "samples")? as usize,
        concurrency: positive_config(config, "concurrency")? as usize,
        warmup_operations: {
            let value = number(config.get("warmup_operations"), "config.warmup_operations")?;
            if value < 0.0 || value.fract() != 0.0 {
                return Err("invalid warmup".to_owned());
            }
            value as usize
        },
        noise_percent: number(config.get("noise_percent"), "config.noise_percent")?,
    };
    Ok(metrics::config_digest(&lane))
}

fn differing_field(left: &Comparability, right: &Comparability) -> Option<&'static str> {
    macro_rules! compare {
        ($($field:ident),+ $(,)?) => { $(if left.$field != right.$field { return Some(stringify!($field)); })+ };
    }
    compare!(
        run_digest,
        candidate_digest,
        workload_digest,
        topology_digest,
        run_group,
        config_digest,
        controlled_host_identity,
        time_window,
        workload_version,
        boundary_class,
        concurrency,
        samples,
        warmup_operations,
        noise_percent,
        seed_policy,
        document_policy,
        operation_policy,
        write_id_policy,
        durability_semantics,
        server_configuration_provenance,
    );
    // The measurement windows may be different moments inside one controlled
    // run group; validity is bounded above, while exact equality would make
    // sequentially driven sides impossible to admit.
    None
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

fn markdown_inline(value: &str) -> String {
    value.replace('|', "\\|").replace('\n', " ").replace('\r', " ")
}

fn retained_failures(results: &Path) -> Vec<(String, String)> {
    let mut failures = Vec::new();
    let Ok(entries) = fs::read_dir(results) else { return failures };
    for entry in entries.flatten() {
        let path = entry.path();
        if path.extension().and_then(|extension| extension.to_str()) != Some("json") {
            continue;
        }
        let Ok(text) = fs::read_to_string(&path) else { continue };
        let Ok(document) = serde_json::from_str::<Value>(&text) else { continue };
        if document.get("status").and_then(Value::as_str) != Some("failed") {
            continue;
        }
        let error = document
            .get("error")
            .and_then(Value::as_str)
            .unwrap_or("error not recorded")
            .to_owned();
        failures.push((path.display().to_string(), error));
    }
    failures.sort();
    failures
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
        let config = LaneConfig {
            seed_documents: 1,
            operations: 2,
            samples: 1,
            concurrency: 1,
            warmup_operations: 0,
            noise_percent: 5.0,
        };
        serde_json::json!({
            "status":"complete", "target":"postgresql", "workload":"read",
            "config":{"seed_documents":1,"operations":2,"samples":1,"concurrency":1,"warmup_operations":0,"noise_percent":5.0},
            "environment":{"observed_identity":"PostgreSQL test","endpoint":"tcp","client_transport":"TCP","unix_seconds":1,"kernel":"Linux test","logical_cpus":1},
            "comparability":{
                "run_digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "candidate_digest":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "workload_digest":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                "topology_digest":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
                "run_group":"test-run", "config_digest":metrics::config_digest(&config),
                "controlled_host_identity":"Linux test;logical-cpus=1", "time_window":"test-window",
                "window_start_unix_seconds":1, "window_end_unix_seconds":1,
                "workload_version":"test-workload-v1", "boundary_class":"network-reference",
                "concurrency":1, "samples":1, "warmup_operations":0, "noise_percent":5.0,
                "seed_policy":"seed", "document_policy":"documents", "operation_policy":"operations",
                "write_id_policy":"write ids", "durability_semantics":"durability",
                "server_configuration_provenance":"server config"
            },
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
        let mut missing_admission = valid();
        missing_admission.as_object_mut().expect("object").remove("comparability");
        cases.insert("missing comparability", missing_admission);
        for (name, value) in cases {
            assert!(validate_complete(&value, "postgresql", "read").is_err(), "{name}");
        }
    }

    #[test]
    fn mismatched_admission_facts_cannot_form_a_group() {
        let left = match validate_complete(&valid(), "postgresql", "read") {
            Ok(complete) => complete,
            Err(error) => panic!("valid fixture rejected: {error}"),
        };
        let mut right_value = valid();
        right_value["comparability"]["boundary_class"] = serde_json::json!("other-boundary");
        let right = validate_complete(&right_value, "postgresql", "read")
            .expect("field mutation remains individually valid");
        assert_eq!(
            differing_field(&left.comparability, &right.comparability),
            Some("boundary_class")
        );
    }

    #[test]
    fn run_candidate_workload_and_topology_digests_are_join_keys() {
        let left = validate_complete(&valid(), "postgresql", "read").expect("valid fixture");
        for (field, replacement) in [
            ("run_digest", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"),
            (
                "candidate_digest",
                "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
            ),
            ("workload_digest", "1111111111111111111111111111111111111111111111111111111111111111"),
            ("topology_digest", "2222222222222222222222222222222222222222222222222222222222222222"),
        ] {
            let mut right_value = valid();
            right_value["comparability"][field] = serde_json::json!(replacement);
            let right = validate_complete(&right_value, "postgresql", "read")
                .expect("digest mutation remains individually valid");
            assert_eq!(differing_field(&left.comparability, &right.comparability), Some(field));
        }
    }

    #[test]
    fn diagnostic_complete_result_is_invalid_and_keeps_its_raw_path() {
        let directory = temporary_results();
        let mut document = valid();
        document["target"] = serde_json::json!("terrabase");
        document["environment"]["observed_identity"] =
            serde_json::json!("tbep-qualification-harness/network-mtls/v1");
        let path = directory.join("terrabase-read-300-1-0.json");
        fs::write(&path, serde_json::to_vec(&document).expect("diagnostic JSON"))
            .expect("diagnostic result");
        let Loaded::Invalid(failure) = load(&directory, "terrabase", "read") else {
            panic!("diagnostic complete result must not be admitted");
        };
        assert_eq!(failure.path, path.display().to_string());
        assert!(failure.error.expect("reason").contains("diagnostic"));
        fs::remove_dir_all(directory).expect("remove temporary results");
    }

    #[test]
    fn side_identities_are_rendered_once_across_workloads() {
        let results = temporary_results();
        let history = temporary_results();
        for workload in WORKLOADS {
            let mut document = valid();
            document["workload"] = serde_json::json!(workload);
            fs::write(
                results.join(format!("postgresql-{workload}-1-0.json")),
                serde_json::to_vec(&document).expect("result JSON"),
            )
            .expect("result");
        }
        let output = run([
            "--results".to_owned(),
            results.display().to_string(),
            "--history-dir".to_owned(),
            history.display().to_string(),
        ]
        .into_iter())
        .expect("history output");
        let rendered = fs::read_to_string(output).expect("history document");
        assert_eq!(rendered.matches("- **postgresql**:").count(), 1);
        fs::remove_dir_all(results).expect("remove results");
        fs::remove_dir_all(history).expect("remove history");
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
        assert!(matches!(load(&directory, "postgresql", "read"), Loaded::Failed(_)));
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
