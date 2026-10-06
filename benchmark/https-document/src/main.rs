use std::collections::HashSet;
use std::fmt::Write as _;
use std::fs::{self, File};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use anyhow::{Context, Result, ensure};
use clap::Parser;
use reqwest::{Client, Method, Url};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use tokio::task::JoinSet;

#[derive(Parser)]
#[command(about = "C# TerraBase HTTPS document acceptance baseline")]
struct Args {
    #[arg(long)]
    config: PathBuf,
    #[arg(long)]
    token_file: PathBuf,
    /// Fresh directory for JSON, raw samples and Markdown evidence.
    #[arg(long)]
    output: PathBuf,
}

#[derive(Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct Config {
    endpoint_override: Option<String>,
    atlas_url: String,
    project: String,
    database: String,
    collection: String,
    /// Absent for a deployed subject: system roots verify its certificate.
    ca_pem_path: Option<PathBuf>,
    /// Absent for a deployed subject; a local binary must match `server_sha256`.
    server_binary_path: Option<PathBuf>,
    server_version: String,
    server_sha256: Option<String>,
    concurrency: Vec<u32>,
    operations_per_worker: u32,
    timeout_ms: u64,
    #[serde(default = "default_workloads")]
    workloads: Vec<String>,
    /// Extra bytes in every written document ("large document" runs).
    #[serde(default)]
    payload_bytes: u32,
    /// Bounds the request rate across all workers of a case.
    max_ops_per_second: Option<u32>,
    /// Stop after the first case with an error or a p99 above this bound.
    abort_p99_ms: Option<u64>,
}

const WORKLOADS: [&str; 9] = [
    "point_read",
    "point_write",
    "update",
    "delete",
    "query_filter_sort",
    "list_page",
    "transactions",
    "mixed_90_10",
    "mixed_80_20",
];

fn default_workloads() -> Vec<String> {
    [
        "point_read",
        "point_write",
        "query_filter_sort",
        "transactions",
        "mixed_90_10",
    ]
    .map(str::to_owned)
    .to_vec()
}

// ponytail: retain at most 100,000 samples per case; stream samples if longer runs are needed.
const MAX_SAMPLES_PER_CASE: u32 = 100_000;

#[derive(Serialize)]
struct Sample {
    workload: &'static str,
    concurrency: u32,
    worker: u32,
    index: u32,
    operation: &'static str,
    latency_ns: u64,
    http_status: Option<u16>,
    failure: Option<&'static str>,
}

#[derive(Serialize)]
struct Row {
    workload: &'static str,
    concurrency: u32,
    attempted: usize,
    errors: usize,
    elapsed_ns: u64,
    throughput_ops_s: f64,
    latency_p50_ns: Option<u64>,
    latency_p95_ns: Option<u64>,
    latency_p99_ns: Option<u64>,
    latency_max_ns: Option<u64>,
}

fn main() {
    if let Err(error) = run(Args::parse()) {
        eprintln!("{error:#}");
        std::process::exit(1);
    }
}

#[allow(clippy::too_many_lines)]
#[tokio::main(flavor = "current_thread")]
async fn run(args: Args) -> Result<()> {
    let config_bytes = fs::read(&args.config).context("read config")?;
    let config: Config = serde_json::from_slice(&config_bytes).context("parse config")?;
    validate_config(&config)?;
    let token = fs::read_to_string(args.token_file).context("read token file")?;
    let token = token.trim();
    ensure!(valid_token(token), "expected one tb_ API key");
    if let (Some(path), Some(sha256)) = (&config.server_binary_path, &config.server_sha256) {
        ensure!(
            hash_file(path)? == *sha256,
            "server SHA-256 differs from pin"
        );
    }
    let ca_bytes = match &config.ca_pem_path {
        Some(path) => Some(fs::read(path)?),
        None => None,
    };
    let mut builder = Client::builder();
    if let Some(ca_bytes) = &ca_bytes {
        builder = builder.tls_certs_only([reqwest::Certificate::from_pem(ca_bytes)?]);
    }
    let client = builder
        .user_agent("ts-benchmark/1 (TS-606)")
        .https_only(true)
        .http1_only()
        .no_proxy()
        .redirect(reqwest::redirect::Policy::none())
        .timeout(Duration::from_millis(config.timeout_ms))
        .build()?;
    let endpoint = resolve_endpoint(&config).await?;
    let database_url = format!(
        "{endpoint}/v1/projects/{}/databases/{}",
        config.project, config.database
    );
    let collection_url = format!("{database_url}/collections/{}", config.collection);
    fs::create_dir(&args.output).context("create fresh evidence directory")?;
    let mut raw = File::create(args.output.join("samples.jsonl"))?;
    let mut rows = Vec::new();
    let mut correctness_errors = 0;
    let mut correctness_checks = 0;
    let mut aborted = None;
    let workloads: Vec<&'static str> = config
        .workloads
        .iter()
        .filter_map(|name| WORKLOADS.into_iter().find(|known| known == name))
        .collect();
    let limiter = Limiter::new(config.max_ops_per_second);
    'cases: for &concurrency in &config.concurrency {
        for &workload in &workloads {
            // Untimed setup: the documents that update and delete change must exist first.
            for (id, value) in setup_writes(
                workload,
                concurrency,
                config.operations_per_worker,
                config.payload_bytes,
            ) {
                limiter.acquire().await;
                // call() reads the whole body, so the HTTP/1.1 connection stays reusable.
                let (_, created) = call(
                    client
                        .put(format!("{collection_url}/documents/{id}"))
                        .bearer_auth(token)
                        .header("Content-Type", "application/json")
                        .header("If-None-Match", "*")
                        .body(value.to_string()),
                    201,
                )
                .await;
                let created = created.is_ok_and(|body| valid_write(&body, &id));
                if !created {
                    aborted = Some(format!(
                        "setup write for {workload} at concurrency {concurrency} failed"
                    ));
                    break 'cases;
                }
            }
            let start = Instant::now();
            let mut workers = JoinSet::new();
            for worker in 0..concurrency {
                let (client, token, config, collection_url, database_url, limiter) = (
                    client.clone(),
                    token.to_owned(),
                    config.clone(),
                    collection_url.clone(),
                    database_url.clone(),
                    limiter.clone(),
                );
                workers.spawn(async move {
                    let mut samples = Vec::new();
                    for index in 0..config.operations_per_worker {
                        samples.push(
                            operation(
                                &limiter,
                                &client,
                                &token,
                                &config,
                                &collection_url,
                                &database_url,
                                workload,
                                concurrency,
                                worker,
                                index,
                            )
                            .await,
                        );
                    }
                    samples
                });
            }
            let mut samples = Vec::new();
            while let Some(result) = workers.join_next().await {
                samples.extend(result.context("benchmark worker failed")?);
            }
            let elapsed = start.elapsed();
            let elapsed_ns = elapsed.as_nanos().try_into().unwrap_or(u64::MAX);
            let mut latencies: Vec<_> = samples
                .iter()
                .filter(|sample| sample.failure.is_none())
                .map(|sample| sample.latency_ns)
                .collect();
            latencies.sort_unstable();
            rows.push(Row {
                workload,
                concurrency,
                attempted: samples.len(),
                errors: samples.len() - latencies.len(),
                elapsed_ns,
                throughput_ops_s: u32::try_from(latencies.len()).map_or(0.0, f64::from)
                    / elapsed.as_secs_f64(),
                latency_p50_ns: percentile(&latencies, 50),
                latency_p95_ns: percentile(&latencies, 95),
                latency_p99_ns: percentile(&latencies, 99),
                latency_max_ns: latencies.last().copied(),
            });
            for sample in samples {
                serde_json::to_writer(&mut raw, &sample)?;
                raw.write_all(b"\n")?;
            }
            // Verify every write outside the timed workload, including both transaction documents.
            for (id, expected) in expected_writes(
                workload,
                concurrency,
                config.operations_per_worker,
                config.payload_bytes,
            ) {
                correctness_checks += 1;
                limiter.acquire().await;
                let response = client
                    .get(format!("{collection_url}/documents/{id}"))
                    .bearer_auth(token)
                    .send()
                    .await;
                let valid = match response {
                    // A deleted document must stay deleted.
                    Ok(response) if expected.is_null() => {
                        // Read the body so the keep-alive connection can be reused.
                        response.status() == 404 && response.bytes().await.is_ok()
                    }
                    Ok(response) if response.status() == 200 => response
                        .bytes()
                        .await
                        .ok()
                        .and_then(|bytes| serde_json::from_slice::<Value>(&bytes).ok())
                        .is_some_and(|body| valid_document(&body, &id, &expected)),
                    _ => false,
                };
                if !valid {
                    correctness_errors += 1;
                }
            }
            if workload == "list_page" {
                correctness_checks += 1;
                if !full_listing_valid(
                    &limiter,
                    &client,
                    token,
                    &collection_url,
                    &config,
                    concurrency,
                )
                .await
                {
                    correctness_errors += 1;
                }
            }
            let row = rows.last().expect("row pushed above");
            if let Some(limit_ms) = config.abort_p99_ms
                && (row.errors > 0
                    || correctness_errors > 0
                    || row.latency_p99_ns.is_none_or(|p99| {
                        Duration::from_nanos(p99) > Duration::from_millis(limit_ms)
                    }))
            {
                aborted = Some(format!(
                    "{workload} at concurrency {concurrency}: {} request errors, {correctness_errors} readback errors, p99 {} ms (bound {limit_ms} ms)",
                    row.errors,
                    milliseconds(row.latency_p99_ns)
                ));
                break 'cases;
            }
        }
    }
    raw.sync_all()?;
    let errors = rows.iter().map(|row| row.errors).sum::<usize>() + correctness_errors;
    let status = match (&aborted, errors) {
        (Some(_), _) => "aborted",
        (None, 0) => "passed",
        _ => "failed",
    };
    let receipt = json!({
        "schema_version": 1, "subject": "C# TerraBase", "status": status, "aborted": aborted,
        "server_version": config.server_version, "server_sha256": config.server_sha256,
        "driver_source_sha256": sha256(include_bytes!("main.rs")), "driver_binary_sha256": hash_file(&std::env::current_exe()?)?,
        "config_sha256": sha256(&config_bytes), "ca_sha256": ca_bytes.as_deref().map(sha256), "endpoint": endpoint,
        "workloads": workloads, "payload_bytes": config.payload_bytes, "max_ops_per_second": config.max_ops_per_second, "abort_p99_ms": config.abort_p99_ms,
        "operations_per_worker": config.operations_per_worker, "errors": errors, "correctness_errors": correctness_errors, "correctness_checks": correctness_checks,
        "timing_scope": "closed-loop request send, full response body, JSON parse and validation; HTTP/1.1 keepalive; no retries; setup and final readbacks excluded",
        "throughput_scope": "successful logical operations per wall second; transaction is two durable puts; list_page is two page reads; update cases also include one untimed read-back per update",
        "write_mode": "insert_only; update and delete change documents created untimed before their case",
        "rows": rows,
    });
    fs::write(
        args.output.join("receipt.json"),
        serde_json::to_vec_pretty(&receipt)?,
    )?;
    let mut summary = format!(
        "# C# TerraBase acceptance baseline\n\nRelease: `{}` · SHA-256: `{}` · Endpoint: `{endpoint}` · Payload: {} bytes\n\n| Workload | Concurrency | Attempts | Errors | ops/s | p50 ms | p95 ms | p99 ms | max ms |\n| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |\n",
        config.server_version,
        config.server_sha256.as_deref().unwrap_or("deployed"),
        config.payload_bytes
    );
    for row in &rows {
        writeln!(
            summary,
            "| {} | {} | {} | {} | {:.2} | {} | {} | {} | {} |",
            row.workload,
            row.concurrency,
            row.attempted,
            row.errors,
            row.throughput_ops_s,
            milliseconds(row.latency_p50_ns),
            milliseconds(row.latency_p95_ns),
            milliseconds(row.latency_p99_ns),
            milliseconds(row.latency_max_ns)
        )?;
    }
    write!(
        summary,
        "\nFinal readback checks: {correctness_checks}; errors: {correctness_errors}. [JSON evidence](receipt.json) · [Raw samples](samples.jsonl).\n\nProcess baseline; CPU priority is recorded in environment.json; timings are observations, never CI thresholds or SQLite qualification.\n"
    )?;
    if let Some(reason) = &aborted {
        writeln!(summary, "\n**Aborted:** {reason}.")?;
    }
    fs::write(args.output.join("summary.md"), summary)?;
    ensure!(
        errors == 0 && aborted.is_none(),
        "run {status} with {errors} errors; see receipt.json"
    );
    Ok(())
}

fn valid_token(token: &str) -> bool {
    token.starts_with("tb_") && token.len() > 3 && !token.chars().any(char::is_whitespace)
}

fn validate_config(config: &Config) -> Result<()> {
    for value in [&config.project, &config.database, &config.collection] {
        ensure!(
            !value.is_empty()
                && value
                    .bytes()
                    .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_'),
            "invalid route identifier"
        );
    }
    ensure!(
        !config.concurrency.is_empty()
            && config
                .concurrency
                .iter()
                .all(|value| (1..=256).contains(value)),
        "concurrency must be 1..256"
    );
    ensure!(
        config
            .concurrency
            .iter()
            .enumerate()
            .all(|(index, value)| !config.concurrency[..index].contains(value)),
        "concurrency levels must be unique"
    );
    ensure!(
        config.operations_per_worker >= 10
            && config.operations_per_worker <= 100_000
            && config.operations_per_worker.is_multiple_of(10),
        "operations_per_worker must be a multiple of 10 in 10..100000"
    );
    ensure!(
        config.concurrency.iter().all(|concurrency| {
            u64::from(*concurrency) * u64::from(config.operations_per_worker)
                <= u64::from(MAX_SAMPLES_PER_CASE)
        }),
        "each concurrency level must produce at most {MAX_SAMPLES_PER_CASE} samples per workload"
    );
    ensure!(
        config.timeout_ms > 0 && config.timeout_ms <= 60_000,
        "timeout_ms must be 1..60000"
    );
    ensure!(
        !config.server_version.is_empty()
            && config.server_sha256.as_ref().is_none_or(|sha256| {
                sha256.len() == 64 && sha256.bytes().all(|byte| byte.is_ascii_hexdigit())
            }),
        "release version and SHA-256 are required"
    );
    ensure!(
        config.server_binary_path.is_some() == config.server_sha256.is_some(),
        "a local server binary needs its pinned SHA-256"
    );
    ensure!(
        !config.workloads.is_empty()
            && config
                .workloads
                .iter()
                .enumerate()
                .all(|(index, name)| WORKLOADS.contains(&name.as_str())
                    && !config.workloads[..index].contains(name)),
        "workloads must be unique known names"
    );
    ensure!(
        config.payload_bytes <= 1_048_576,
        "payload_bytes must be at most 1 MiB"
    );
    ensure!(
        config.max_ops_per_second.is_none_or(|rate| rate > 0),
        "max_ops_per_second must be positive"
    );
    if let Some(endpoint) = &config.endpoint_override {
        validate_origin(endpoint)?;
    }
    validate_origin(&config.atlas_url)?;
    Ok(())
}

fn validate_origin(origin: &str) -> Result<Url> {
    let url = Url::parse(origin)?;
    ensure!(
        url.scheme() == "https"
            && url.host_str().is_some()
            && url.username().is_empty()
            && url.password().is_none()
            && matches!(url.path(), "" | "/")
            && url.query().is_none()
            && url.fragment().is_none(),
        "endpoint must be an HTTPS origin without credentials"
    );
    Ok(url)
}

async fn resolve_endpoint(config: &Config) -> Result<String> {
    if let Some(endpoint) = &config.endpoint_override {
        return Ok(endpoint.trim_end_matches('/').to_owned());
    }
    let url = format!(
        "{}/api/v1/discovery?database={}",
        config.atlas_url.trim_end_matches('/'),
        config.database
    );
    let response = Client::builder()
        .https_only(true)
        .no_proxy()
        .redirect(reqwest::redirect::Policy::none())
        .timeout(Duration::from_millis(config.timeout_ms))
        .build()?
        .get(url)
        .send()
        .await?
        .error_for_status()?
        .bytes()
        .await?;
    let discovery: Value = serde_json::from_slice(&response)?;
    let endpoint = discovery["endpoints"]
        .as_array()
        .context("Atlas endpoints missing")?
        .iter()
        .find(|entry| {
            entry["capabilities"]
                .as_array()
                .is_some_and(|caps| caps.contains(&json!("read")) && caps.contains(&json!("write")))
        })
        .and_then(|entry| entry["url"].as_str())
        .context("Atlas has no read/write endpoint")?;
    validate_origin(endpoint)?;
    Ok(endpoint.trim_end_matches('/').to_owned())
}

fn document_id(number: u32) -> String {
    format!("00000000-0000-4000-8000-{number:012x}")
}
fn write_document_id(
    workload: &str,
    concurrency: u32,
    worker: u32,
    index: u32,
    operations_per_worker: u32,
    slot: u32,
) -> String {
    let namespace = match workload {
        "point_write" => 1,
        "transactions" => 2,
        "mixed_90_10" => 3,
        "mixed_80_20" => 4,
        "update" => 5,
        "delete" => 6,
        _ => 0,
    };
    // Every update of a worker replaces the same document.
    let index = if workload == "update" { 0 } else { index };
    let number = (worker * operations_per_worker + index) * 2 + slot;
    format!("{namespace:08x}-{concurrency:04x}-4000-8000-{number:012x}")
}
/// Whether the mixed workloads write (rather than read) at this index.
fn mixed_write(workload: &str, index: u32) -> bool {
    match workload {
        "mixed_90_10" => index % 10 == 9,
        "mixed_80_20" => index % 10 >= 8,
        _ => false,
    }
}
/// Final state of every document a case changed; `Null` means deleted.
fn expected_writes(
    workload: &str,
    concurrency: u32,
    operations_per_worker: u32,
    payload_bytes: u32,
) -> impl Iterator<Item = (String, Value)> + '_ {
    (0..concurrency).flat_map(move |worker| {
        (0..operations_per_worker).flat_map(move |index| {
            let count = match workload {
                "transactions" => 2,
                "point_write" | "delete" => 1,
                "update" if index + 1 == operations_per_worker => 1,
                _ => u32::from(mixed_write(workload, index)),
            };
            (0..count).map(move |slot| {
                (
                    write_document_id(
                        workload,
                        concurrency,
                        worker,
                        index,
                        operations_per_worker,
                        slot,
                    ),
                    if workload == "delete" {
                        Value::Null
                    } else {
                        write_document(worker, index, workload, concurrency, payload_bytes)
                    },
                )
            })
        })
    })
}
/// Documents that update and delete change, created before their timed case with the run's padding.
fn setup_writes(
    workload: &str,
    concurrency: u32,
    operations_per_worker: u32,
    payload_bytes: u32,
) -> impl Iterator<Item = (String, Value)> + '_ {
    let per_worker = match workload {
        "update" => 1,
        "delete" => operations_per_worker,
        _ => 0,
    };
    (0..concurrency).flat_map(move |worker| {
        (0..per_worker).map(move |index| {
            (
                write_document_id(
                    workload,
                    concurrency,
                    worker,
                    index,
                    operations_per_worker,
                    0,
                ),
                {
                    let mut value = json!({"setup": workload});
                    if payload_bytes > 0 {
                        value["padding"] = json!("x".repeat(payload_bytes as usize));
                    }
                    value
                },
            )
        })
    })
}
/// A seed document; the harness that seeds the collection pads it like the run's writes.
fn document(rank: u32, payload_bytes: u32) -> Value {
    let mut value = json!({"rank": rank, "group": rank % 2, "payload": "ts500"});
    if payload_bytes > 0 {
        value["padding"] = json!("x".repeat(payload_bytes as usize));
    }
    value
}
fn write_document(
    rank: u32,
    sequence: u32,
    workload: &str,
    concurrency: u32,
    payload_bytes: u32,
) -> Value {
    let mut value = json!({"rank": rank, "group": rank % 2, "payload": "ts500-write", "sequence": sequence, "workload": workload, "concurrency": concurrency});
    if payload_bytes > 0 {
        value["padding"] = json!("x".repeat(payload_bytes as usize));
    }
    value
}
fn valid_document(body: &Value, id: &str, expected: &Value) -> bool {
    body["id"] == id
        && body["state"] == "value"
        && body["version"]
            .as_str()
            .is_some_and(|version| !version.is_empty())
        && body["document"] == *expected
}
/// `valid_document` against `unpadded` plus `payload_bytes` of `x` padding, checked in place: timed
/// validation never builds another padded (up to 1 MiB) value.
fn valid_padded(body: &Value, id: &str, unpadded: &Value, payload_bytes: u32) -> bool {
    let (Some(actual), Some(expected)) = (body["document"].as_object(), unpadded.as_object())
    else {
        return false;
    };
    let padding = actual.get("padding");
    let padded = match payload_bytes {
        0 => padding.is_none(),
        bytes => padding.and_then(Value::as_str).is_some_and(|padding| {
            padding.len() == bytes as usize && padding.bytes().all(|byte| byte == b'x')
        }),
    };
    body["id"] == id
        && body["state"] == "value"
        && body["version"]
            .as_str()
            .is_some_and(|version| !version.is_empty())
        && padded
        && actual.len() == expected.len() + usize::from(payload_bytes > 0)
        && expected
            .iter()
            .all(|(key, value)| actual.get(key) == Some(value))
}
fn valid_write(body: &Value, id: &str) -> bool {
    body["id"] == id
        && body["version"]
            .as_str()
            .is_some_and(|version| !version.is_empty())
        && durable(body)
}
fn durable(body: &Value) -> bool {
    body["durability"] == "local_durable"
        && body["offset"]
            .as_str()
            .is_some_and(|offset| offset.parse::<u64>().is_ok())
}
/// A full page of distinct stored documents, optionally disjoint from `previous`.
/// A full page of distinct documents, each equal to its known stored state.
fn valid_page(body: &Value, previous: &[Value], config: &Config, concurrency: u32) -> bool {
    body["items"].as_array().is_some_and(|items| {
        items.len() == PAGE_SIZE
            && items.iter().enumerate().all(|(index, item)| {
                item["id"].as_str().is_some_and(|id| {
                    expected_document(id, config, concurrency).is_some_and(|expected| {
                        valid_padded(item, id, &expected, config.payload_bytes)
                    })
                }) && !items[..index].iter().any(|other| other["id"] == item["id"])
                    && !previous.iter().any(|other| other["id"] == item["id"])
            })
    })
}
/// Whether the case (`workload`, `concurrency`) finished before `list_page` at `current` ran.
fn case_completed(config: &Config, current: u32, workload: &str, concurrency: u32) -> bool {
    let level = |value| config.concurrency.iter().position(|&level| level == value);
    let order = |name| config.workloads.iter().position(|known| known == name);
    match (
        level(concurrency),
        level(current),
        order(workload),
        order("list_page"),
    ) {
        (Some(case), Some(now), Some(workload), Some(list)) => {
            case < now || (case == now && workload < list)
        }
        _ => false,
    }
}
/// The stored state of any document this run wrote before `list_page` at `current` ran, recovered
/// from its ID: a seed, or a completed case's final write. `None` for deleted documents and for
/// IDs the driver never writes (wrong slot, a read index of a mix, an unfinished case). The value
/// is unpadded; `valid_padded` checks the run's padding in place.
fn expected_document(id: &str, config: &Config, current: u32) -> Option<Value> {
    let (operations_per_worker, payload_bytes) = (config.operations_per_worker, 0);
    let parts: Vec<_> = id.split('-').collect();
    let [namespace, concurrency, "4000", "8000", number] = parts.as_slice() else {
        return None;
    };
    let (namespace, concurrency, number) = (
        u32::from_str_radix(namespace, 16).ok()?,
        u32::from_str_radix(concurrency, 16).ok()?,
        u32::try_from(u64::from_str_radix(number, 16).ok()?).ok()?,
    );
    if namespace == 0 && concurrency == 0 {
        return (number < 32).then(|| document(number, payload_bytes));
    }
    let workload = match namespace {
        1 => "point_write",
        2 => "transactions",
        3 => "mixed_90_10",
        4 => "mixed_80_20",
        5 => "update",
        _ => return None,
    };
    let (worker, index, slot) = (
        number / 2 / operations_per_worker,
        number / 2 % operations_per_worker,
        number % 2,
    );
    let slots = match workload {
        "transactions" => 2,
        "point_write" | "update" => 1,
        _ => u32::from(mixed_write(workload, index)),
    };
    if slot >= slots || !case_completed(config, current, workload, concurrency) {
        return None;
    }
    // An update document keeps its last write.
    let index = if workload == "update" {
        operations_per_worker - 1
    } else {
        index
    };
    (worker < concurrency
        && write_document_id(
            workload,
            concurrency,
            worker,
            index,
            operations_per_worker,
            slot,
        ) == id)
        .then(|| write_document(worker, index, workload, concurrency, payload_bytes))
}
const PAGE_SIZE: usize = 10;

/// Sends one request and returns its status and, when it is `expected`, its JSON body.
async fn call(
    request: reqwest::RequestBuilder,
    expected: u16,
) -> (Option<u16>, Result<Value, &'static str>) {
    match request.send().await {
        Err(_) => (None, Err("request_error")),
        Ok(response) => {
            let status = response.status().as_u16();
            let body = match response.bytes().await {
                Err(_) => Err("body_error"),
                Ok(_) if status != expected => Err("http_status_error"),
                Ok(bytes) => serde_json::from_slice(&bytes).map_err(|_| "invalid_json"),
            };
            (Some(status), body)
        }
    }
}

#[allow(clippy::too_many_arguments, clippy::too_many_lines)]
async fn operation(
    limiter: &Limiter,
    client: &Client,
    token: &str,
    config: &Config,
    collection_url: &str,
    database_url: &str,
    workload: &'static str,
    concurrency: u32,
    worker: u32,
    index: u32,
) -> Sample {
    let operation = match workload {
        "mixed_90_10" | "mixed_80_20" if mixed_write(workload, index) => "point_write",
        "mixed_90_10" | "mixed_80_20" => "point_read",
        other => other,
    };
    let read_id = document_id(worker % 32);
    let write_id = write_document_id(
        workload,
        concurrency,
        worker,
        index,
        config.operations_per_worker,
        0,
    );
    let second_id = write_document_id(
        workload,
        concurrency,
        worker,
        index,
        config.operations_per_worker,
        1,
    );
    // Only writes need a (possibly 1 MiB padded) document; build none for reads.
    let value = matches!(operation, "point_write" | "update" | "transactions")
        .then(|| write_document(worker, index, workload, concurrency, config.payload_bytes));
    let document_url = format!("{collection_url}/documents/{write_id}");
    let list_url = format!("{collection_url}/documents?limit={PAGE_SIZE}");
    let (method, url, payload, expected) = match operation {
        "point_write" => (Method::PUT, document_url, value, 201),
        "update" => (Method::PUT, document_url, value, 200),
        "delete" => (Method::DELETE, document_url, None, 200),
        "list_page" => (Method::GET, list_url.clone(), None, 200),
        "query_filter_sort" => (
            Method::POST,
            format!("{collection_url}/query"),
            Some(
                json!({"filter": {"and": [{"eq": ["/group", 0]}, {"eq": ["/payload", "ts500"]}]}, "sort": [{"path": "/rank", "order": "desc"}], "limit": 4}),
            ),
            200,
        ),
        "transactions" => (
            Method::POST,
            format!("{database_url}/transactions"),
            Some(json!({"ops": [
                {"op": "put", "collection": config.collection, "id": write_id, "document": value, "if_none_match": "*"},
                {"op": "put", "collection": config.collection, "id": second_id, "document": value, "if_none_match": "*"}
            ]})),
            200,
        ),
        _ => (
            Method::GET,
            format!("{collection_url}/documents/{read_id}"),
            None,
            200,
        ),
    };
    let mut request = client.request(method, url).bearer_auth(token);
    if let Some(payload) = payload {
        request = request
            .header("Content-Type", "application/json")
            .body(payload.to_string());
    }
    if operation == "point_write" {
        request = request.header("If-None-Match", "*");
    }
    limiter.acquire().await;
    let start = Instant::now();
    // Time spent waiting for the second page's rate slot is not request latency.
    let mut waited = Duration::ZERO;
    let (mut http_status, body) = call(request, expected).await;
    let failure = match body {
        Err(failure) => Some(failure),
        Ok(body) => {
            let valid = match operation {
                "point_write" | "update" | "delete" => valid_write(&body, &write_id),
                "transactions" => {
                    durable(&body)
                        && body["results"].as_array().is_some_and(|items| {
                            items.len() == 2
                                && items.iter().zip([&write_id, &second_id]).all(|(item, id)| {
                                    item["id"] == **id
                                        && item["version"]
                                            .as_str()
                                            .is_some_and(|version| !version.is_empty())
                                })
                        })
                }
                "query_filter_sort" => body["items"].as_array().is_some_and(|items| {
                    items.len() == 4
                        && items.iter().zip([30, 28, 26, 24]).all(|(item, rank)| {
                            valid_padded(
                                item,
                                &document_id(rank),
                                &document(rank, 0),
                                config.payload_bytes,
                            )
                        })
                }),
                "list_page" => valid_page(&body, &[], config, concurrency),
                _ => valid_padded(
                    &body,
                    &read_id,
                    &document(worker % 32, 0),
                    config.payload_bytes,
                ),
            };
            if !valid {
                Some("response_mismatch")
            } else if operation == "list_page" {
                // Paginate: the next page follows the first one's cursor.
                let next = body["next_cursor"].as_str().and_then(|cursor| {
                    Url::parse_with_params(&list_url, [("cursor", cursor)]).ok()
                });
                match next {
                    None => Some("response_mismatch"),
                    Some(next) => {
                        waited = limiter.acquire().await;
                        let (status, page) = call(client.get(next).bearer_auth(token), 200).await;
                        http_status = status;
                        match page {
                            Err(failure) => Some(failure),
                            Ok(page)
                                if valid_page(
                                    &page,
                                    body["items"].as_array().map_or(&[], Vec::as_slice),
                                    config,
                                    concurrency,
                                ) =>
                            {
                                None
                            }
                            Ok(_) => Some("response_mismatch"),
                        }
                    }
                }
            } else {
                None
            }
        }
    };
    let latency_ns = nanos(start).saturating_sub(waited.as_nanos().try_into().unwrap_or(u64::MAX));
    // Untimed: each update must be stored before the next one overwrites it.
    let failure = if failure.is_none() && operation == "update" {
        limiter.acquire().await;
        let (_, stored) = call(
            client
                .get(format!("{collection_url}/documents/{write_id}"))
                .bearer_auth(token),
            200,
        )
        .await;
        let expected = write_document(worker, index, workload, concurrency, 0);
        let kept = stored
            .is_ok_and(|body| valid_padded(&body, &write_id, &expected, config.payload_bytes));
        (!kept).then_some("lost_update")
    } else {
        failure
    };
    Sample {
        workload,
        concurrency,
        worker,
        index,
        operation,
        latency_ns,
        http_status,
        failure,
    }
}

/// Every document stored when `list_page` ran at `current`: the seeds and the final writes of the
/// cases completed before it, deletes excluded.
fn live_documents(config: &Config, current: u32) -> HashSet<String> {
    let mut ids: HashSet<_> = (0..32).map(document_id).collect();
    for &concurrency in &config.concurrency {
        for workload in &config.workloads {
            if workload != "delete" && case_completed(config, current, workload, concurrency) {
                ids.extend(
                    expected_writes(workload, concurrency, config.operations_per_worker, 0)
                        .map(|(id, _)| id),
                );
            }
        }
    }
    ids
}

const LIST_ALL_PAGE_SIZE: usize = 100;

/// Untimed: follows cursors to the end and requires exactly the live documents, each once and with
/// its stored content, so a skipped document or a wrong continuation fails.
async fn full_listing_valid(
    limiter: &Limiter,
    client: &Client,
    token: &str,
    collection_url: &str,
    config: &Config,
    current: u32,
) -> bool {
    let expected = live_documents(config, current);
    let first = format!("{collection_url}/documents?limit={LIST_ALL_PAGE_SIZE}");
    let mut url = first.clone();
    let mut seen = HashSet::new();
    for _ in 0..expected.len() / LIST_ALL_PAGE_SIZE + 2 {
        limiter.acquire().await;
        let (_, body) = call(client.get(&url).bearer_auth(token), 200).await;
        let Some(items) = body.as_ref().ok().and_then(|body| body["items"].as_array()) else {
            return false;
        };
        for item in items {
            let Some(id) = item["id"].as_str() else {
                return false;
            };
            let stored = expected_document(id, config, current)
                .is_some_and(|expected| valid_padded(item, id, &expected, config.payload_bytes));
            if !stored || !seen.insert(id.to_owned()) {
                return false;
            }
        }
        match body
            .as_ref()
            .ok()
            .and_then(|body| body["next_cursor"].as_str())
        {
            Some(cursor) => match Url::parse_with_params(&first, [("cursor", cursor)]) {
                Ok(next) => url = next.into(),
                Err(_) => return false,
            },
            None => return seen == expected,
        }
    }
    false
}

/// One request schedule shared by every request of a run (setup, timed, both list pages and
/// readback), so no two requests start closer than `1 / max_ops_per_second`.
#[derive(Clone)]
struct Limiter(Option<(Duration, Arc<Mutex<Instant>>)>);

impl Limiter {
    fn new(max_ops_per_second: Option<u32>) -> Self {
        Self(max_ops_per_second.map(|rate| {
            (
                Duration::from_secs_f64(1.0 / f64::from(rate)),
                Arc::new(Mutex::new(Instant::now())),
            )
        }))
    }

    /// Waits until a request may start and returns the time actually spent waiting.
    ///
    /// A slot is taken only under the lock at the moment it is due, so waiters that wake
    /// together after a stall are still spaced: all but one go back to sleep.
    async fn acquire(&self) -> Duration {
        let Some((spacing, next)) = &self.0 else {
            return Duration::ZERO;
        };
        let entered = Instant::now();
        loop {
            let due = {
                let mut next = next.lock().expect("limiter lock");
                let now = Instant::now();
                if now >= *next {
                    *next = paced(*next, now, *spacing);
                    None
                } else {
                    Some(*next)
                }
            };
            match due {
                None => return entered.elapsed(),
                Some(due) => tokio::time::sleep_until(due.into()).await,
            }
        }
    }
}

/// The next request's earliest start. It counts from this request's actual start, so a slow
/// request never banks credit for a burst afterwards.
fn paced(deadline: Instant, now: Instant, spacing: Duration) -> Instant {
    deadline.max(now) + spacing
}
fn nanos(start: Instant) -> u64 {
    start.elapsed().as_nanos().try_into().unwrap_or(u64::MAX)
}
fn sha256(bytes: &[u8]) -> String {
    hex::encode(Sha256::digest(bytes))
}
fn hash_file(path: &Path) -> Result<String> {
    Ok(sha256(&fs::read(path)?))
}
fn percentile(sorted: &[u64], percent: usize) -> Option<u64> {
    sorted
        .get((sorted.len() * percent).div_ceil(100).saturating_sub(1))
        .copied()
}
fn milliseconds(ns: Option<u64>) -> String {
    ns.map_or_else(
        || "—".to_owned(),
        |value| format!("{:.3}", Duration::from_nanos(value).as_secs_f64() * 1000.0),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn expected_write_ids_are_unique_across_workloads_and_concurrency_levels() {
        let mut ids: std::collections::HashSet<_> = (0..32).map(document_id).collect();
        ids.insert(document_id(999));
        for concurrency in [1, 4, 16] {
            for workload in ["point_write", "transactions", "mixed_90_10"] {
                for (id, _) in expected_writes(workload, concurrency, 20, 0) {
                    assert!(
                        ids.insert(id.clone()),
                        "{workload} at concurrency {concurrency} reuses document {id}"
                    );
                }
            }
        }
        assert_eq!(ids.len(), 33 + 1_302);
        for index in [9, 19] {
            assert_ne!(
                write_document_id("point_write", 1, 0, index, 20, 0),
                write_document_id("mixed_90_10", 1, 0, index, 20, 0)
            );
            assert_ne!(
                write_document_id("transactions", 4, 0, index, 20, 0),
                write_document_id("transactions", 16, 0, index, 20, 0)
            );
        }
    }

    #[test]
    fn update_delete_and_mixed_80_20_bookkeeping() {
        // update: one setup document per worker, read back with its last value.
        let setup: Vec<_> = setup_writes("update", 2, 20, 0).collect();
        let updates: Vec<_> = expected_writes("update", 2, 20, 8).collect();
        assert_eq!(setup.len(), 2);
        assert_eq!(updates.len(), 2);
        for ((setup_id, _), (id, value)) in setup.iter().zip(&updates) {
            assert_eq!(setup_id, id);
            assert_eq!(value["sequence"], 19);
            assert_eq!(value["padding"], "xxxxxxxx");
        }
        // delete: every operation deletes its own setup document, which must stay deleted.
        let setup: Vec<_> = setup_writes("delete", 2, 20, 0).collect();
        let deletes: Vec<_> = expected_writes("delete", 2, 20, 0).collect();
        assert_eq!(setup.len(), 40);
        assert!(deletes.iter().all(|(_, value)| value.is_null()));
        assert_eq!(
            setup.iter().map(|(id, _)| id).collect::<Vec<_>>(),
            deletes.iter().map(|(id, _)| id).collect::<Vec<_>>()
        );
        assert_eq!(expected_writes("mixed_80_20", 2, 20, 0).count(), 8);
        assert_eq!(document(3, 4)["padding"], "xxxx");
        assert!(document(3, 0).get("padding").is_none());
        assert_eq!(setup_writes("point_write", 2, 20, 0).count(), 0);
        // Large runs delete and update large documents.
        for workload in ["update", "delete"] {
            for (_, value) in setup_writes(workload, 2, 20, 16) {
                assert_eq!(value["padding"], "x".repeat(16), "{workload}");
            }
        }
        assert!(
            !write_document(0, 0, "point_write", 1, 0)
                .as_object()
                .unwrap()
                .contains_key("padding")
        );
    }

    #[tokio::test]
    async fn limiter_spaces_concurrent_requests() {
        let limiter = Limiter::new(Some(20));
        let start = Instant::now();
        let (first, second, third) =
            tokio::join!(limiter.acquire(), limiter.acquire(), limiter.acquire());
        // Spacing, not FIFO order, is guaranteed, so compare the sorted waits.
        let mut waits = [first, second, third];
        waits.sort();
        assert!(waits[2] >= Duration::from_millis(95));
        assert!(start.elapsed() >= Duration::from_millis(100));
        // An idle period banks no credit: after it, requests are spaced again.
        tokio::time::sleep(Duration::from_millis(200)).await;
        let before = Instant::now();
        let _ = tokio::join!(limiter.acquire(), limiter.acquire());
        assert!(before.elapsed() >= Duration::from_millis(50));
        assert_eq!(Limiter::new(None).acquire().await, Duration::ZERO);
    }

    #[tokio::test]
    async fn limiter_spaces_waiters_that_wake_overdue_together() {
        let limiter = Limiter::new(Some(20));
        let acquired = || async {
            limiter.acquire().await;
            Instant::now()
        };
        // The blocker stalls the runtime past every pending waiter's slot.
        let blocker = async {
            tokio::task::yield_now().await;
            std::thread::sleep(Duration::from_millis(300));
        };
        let (first, second, third, ()) = tokio::join!(acquired(), acquired(), acquired(), blocker);
        let mut times = [first, second, third];
        times.sort();
        assert!(times[2] - times[1] >= Duration::from_millis(45));
        assert!(times[1] - times[0] >= Duration::from_millis(45));
    }

    #[test]
    fn listed_documents_are_checked_against_known_stored_state() {
        let mut config: Config =
            serde_json::from_str(include_str!("../config.example.json")).unwrap();
        config.concurrency = vec![1, 4];
        config.operations_per_worker = 20;
        config.payload_bytes = 8;
        config.workloads = [
            "point_write",
            "update",
            "delete",
            "transactions",
            "mixed_90_10",
            "list_page",
            "mixed_80_20",
        ]
        .map(str::to_owned)
        .to_vec();
        for workload in ["point_write", "update", "transactions", "mixed_90_10"] {
            for (id, value) in expected_writes(workload, 4, 20, 8) {
                let expected = expected_document(&id, &config, 4).expect("written");
                let item = json!({"id": id, "state": "value", "version": "v1", "document": value});
                assert!(valid_padded(&item, &id, &expected, 8), "{workload} {id}");
                // Not yet written while list_page ran at concurrency 1.
                assert_eq!(expected_document(&id, &config, 1), None, "{workload} {id}");
            }
        }
        // mixed_80_20 runs after list_page; deleted documents are gone.
        for (id, _) in
            expected_writes("mixed_80_20", 4, 20, 8).chain(expected_writes("delete", 4, 20, 0))
        {
            assert_eq!(expected_document(&id, &config, 4), None, "{id}");
        }
        // The live set at concurrency 4: seeds, every completed non-delete case at 1 (mixed_80_20
        // included) and at 4 before list_page; each member has a known stored state.
        let live = live_documents(&config, 4);
        assert_eq!(live.len(), 32 + (20 + 1 + 40 + 2 + 4) + (80 + 4 + 160 + 8));
        assert!(
            live.iter()
                .all(|id| expected_document(id, &config, 4).is_some())
        );
        // IDs the driver never writes: slot 1 of a single write, a read index of a mix.
        assert_eq!(
            expected_document(
                &write_document_id("point_write", 4, 0, 3, 20, 1),
                &config,
                4
            ),
            None
        );
        assert_eq!(
            expected_document(
                &write_document_id("mixed_90_10", 4, 0, 0, 20, 0),
                &config,
                4
            ),
            None
        );
        assert_eq!(
            expected_document(&document_id(7), &config, 1),
            Some(document(7, 0))
        );
        assert_eq!(expected_document(&document_id(999), &config, 4), None);
        assert_eq!(expected_document("not-an-id", &config, 4), None);
        // A listed item whose content differs from what the run stored fails the page.
        let mut items: Vec<_> = (0..10)
            .map(|rank| json!({"id": document_id(rank), "state": "value", "version": "v1", "document": document(rank, 8)}))
            .collect();
        assert!(valid_page(&json!({"items": items}), &[], &config, 1));
        items[3]["document"]["rank"] = json!(99);
        assert!(!valid_page(&json!({"items": items}), &[], &config, 1));
        // Padding is checked in place: wrong length, wrong bytes, missing or extra fields fail.
        let seed = json!({"id": document_id(1), "state": "value", "version": "v1", "document": document(1, 8)});
        assert!(valid_padded(&seed, &document_id(1), &document(1, 0), 8));
        for (field, value) in [
            ("padding", json!("xxxxxxx")),
            ("padding", json!("xxxxxxxy")),
            ("extra", json!(1)),
        ] {
            let mut item = seed.clone();
            item["document"][field] = value;
            assert!(
                !valid_padded(&item, &document_id(1), &document(1, 0), 8),
                "{field}"
            );
        }
        assert!(!valid_padded(&seed, &document_id(1), &document(1, 0), 0));
        let unpadded = json!({"id": document_id(1), "state": "value", "version": "v1", "document": document(1, 0)});
        assert!(valid_padded(&unpadded, &document_id(1), &document(1, 0), 0));
        assert!(!valid_padded(
            &unpadded,
            &document_id(1),
            &document(1, 0),
            8
        ));
    }

    #[test]
    fn pacing_never_bursts_after_a_slow_request() {
        let start = Instant::now();
        let spacing = Duration::from_millis(100);
        assert_eq!(paced(start, start, spacing), start + spacing);
        // A request that started 5 s late does not leave 50 past deadlines to run at once.
        let late = start + Duration::from_secs(5);
        let next = paced(start, late, spacing);
        assert_eq!(next, late + spacing);
        assert_eq!(
            paced(next, late + Duration::from_millis(1), spacing),
            next + spacing
        );
    }

    #[test]
    fn duplicate_concurrency_levels_are_rejected() {
        let mut config: Config =
            serde_json::from_str(include_str!("../config.example.json")).unwrap();
        for concurrency in [vec![1, 1], vec![1, 4, 16, 4]] {
            config.concurrency = concurrency;
            assert_eq!(
                validate_config(&config).unwrap_err().to_string(),
                "concurrency levels must be unique"
            );
        }
        config.concurrency = vec![16, 1, 4];
        assert!(validate_config(&config).is_ok());
    }

    #[test]
    fn expected_writes_covers_every_operation_and_transaction_slot() {
        for (workload, count, slots) in [
            ("point_write", 40, 1),
            ("transactions", 80, 2),
            ("mixed_90_10", 4, 1),
        ] {
            let writes: Vec<_> = expected_writes(workload, 2, 20, 0).collect();
            assert_eq!(writes.len(), count, "{workload}");
            let ids: std::collections::HashSet<_> = writes.iter().map(|(id, _)| id).collect();
            assert_eq!(ids.len(), count, "duplicate IDs in {workload}");
            for worker in 0..2 {
                for index in 0..20 {
                    for slot in 0..slots {
                        let expected = (
                            write_document_id(workload, 2, worker, index, 20, slot),
                            write_document(worker, index, workload, 2, 0),
                        );
                        assert_eq!(
                            writes.contains(&expected),
                            workload != "mixed_90_10" || index % 10 == 9,
                            "{workload}, worker {worker}, index {index}, slot {slot}"
                        );
                    }
                }
            }
        }
        for workload in ["point_read", "query_filter_sort"] {
            assert_eq!(expected_writes(workload, 2, 20, 0).count(), 0);
        }
    }

    #[test]
    fn readback_detects_intermediate_corruption_with_correct_final_documents() {
        for (workload, intermediate, slots) in [
            ("point_write", 1, 1),
            ("transactions", 1, 2),
            ("mixed_90_10", 9, 1),
        ] {
            for slot in 0..slots {
                let corrupt_id = write_document_id(workload, 2, 0, intermediate, 20, slot);
                let mut failures = Vec::new();
                let mut valid_final_documents = 0;
                for (id, expected) in expected_writes(workload, 2, 20, 0) {
                    let mut response = json!({
                        "id": id, "state": "value", "version": "v1", "document": expected
                    });
                    assert!(valid_document(&response, &id, &expected));
                    if id == corrupt_id {
                        response["document"]["sequence"] = json!(0);
                    }
                    let valid = valid_document(&response, &id, &expected);
                    if expected["sequence"] == 19 {
                        assert!(valid, "final document {id} must remain correct");
                        valid_final_documents += 1;
                    }
                    if !valid {
                        failures.push(id);
                    }
                }
                assert_eq!(valid_final_documents, 2 * slots, "{workload}");
                assert_eq!(failures, vec![corrupt_id], "{workload}, slot {slot}");
            }
        }
    }

    #[test]
    fn total_sample_budget_rejects_excessive_runs() {
        let mut config: Config =
            serde_json::from_str(include_str!("../config.example.json")).unwrap();
        assert!(validate_config(&config).is_ok());
        config.concurrency = vec![1, 256];
        config.operations_per_worker = 390;
        assert!(validate_config(&config).is_ok());
        config.operations_per_worker = 400;
        assert!(
            validate_config(&config)
                .unwrap_err()
                .to_string()
                .contains("samples per workload")
        );
        config.operations_per_worker = 100_000;
        assert!(validate_config(&config).is_err());
        config.concurrency = vec![1];
        assert!(validate_config(&config).is_ok());
    }

    #[test]
    fn acceptance_validation() {
        assert!(valid_token("tb_local-key"));
        assert!(!valid_token("tbk1_old"));
        assert!(!valid_token("tb_key\nother"));
        assert!(validate_origin("https://localhost:8443").is_ok());
        for origin in [
            "http://localhost",
            "https://key@localhost",
            "https://localhost/v1/tenants/x",
            "https://localhost?token=x",
        ] {
            assert!(validate_origin(origin).is_err());
        }
        assert_eq!(percentile(&[1, 2, 3, 4, 5], 95), Some(5));
        assert_eq!(percentile(&[], 99), None);
        assert!(valid_document(
            &json!({"id": "x", "state": "value", "version": "v1", "document": {"rank": 1}}),
            "x",
            &json!({"rank": 1})
        ));
        assert!(!valid_document(
            &json!({"version": 1, "state": "value", "siblings": []}),
            "x",
            &json!({})
        ));
        let previous = write_document(0, 9, "point_write", 1, 0);
        let current = write_document(0, 9, "transactions", 4, 0);
        let stale = json!({"id": "x", "state": "value", "version": "v1", "document": previous});
        assert!(!valid_document(&stale, "x", &current));
        assert!(!durable(&json!({"durability": "memory", "offset": "1"})));
    }
}
