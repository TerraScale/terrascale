use std::fmt::Write as _;
use std::fs::{self, File};
use std::io::Write;
use std::path::{Path, PathBuf};
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
    ca_pem_path: PathBuf,
    server_binary_path: PathBuf,
    server_version: String,
    server_sha256: String,
    concurrency: Vec<u32>,
    operations_per_worker: u32,
    timeout_ms: u64,
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
    ensure!(
        hash_file(&config.server_binary_path)? == config.server_sha256,
        "server SHA-256 differs from pin"
    );
    let ca_bytes = fs::read(&config.ca_pem_path)?;
    let client = Client::builder()
        .tls_certs_only([reqwest::Certificate::from_pem(&ca_bytes)?])
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
    for &concurrency in &config.concurrency {
        for workload in [
            "point_read",
            "point_write",
            "query_filter_sort",
            "transactions",
            "mixed_90_10",
        ] {
            let start = Instant::now();
            let mut workers = JoinSet::new();
            for worker in 0..concurrency {
                let (client, token, config, collection_url, database_url) = (
                    client.clone(),
                    token.to_owned(),
                    config.clone(),
                    collection_url.clone(),
                    database_url.clone(),
                );
                workers.spawn(async move {
                    let mut samples = Vec::new();
                    for index in 0..config.operations_per_worker {
                        samples.push(
                            operation(
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
            });
            for sample in samples {
                serde_json::to_writer(&mut raw, &sample)?;
                raw.write_all(b"\n")?;
            }
            // Verify every write outside the timed workload, including both transaction documents.
            for (id, expected) in
                expected_writes(workload, concurrency, config.operations_per_worker)
            {
                correctness_checks += 1;
                let response = client
                    .get(format!("{collection_url}/documents/{id}"))
                    .bearer_auth(token)
                    .send()
                    .await;
                let valid = match response {
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
        }
    }
    raw.sync_all()?;
    let errors = rows.iter().map(|row| row.errors).sum::<usize>() + correctness_errors;
    let receipt = json!({
        "schema_version": 1, "subject": "C# TerraBase", "status": if errors == 0 {"passed"} else {"failed"},
        "server_version": config.server_version, "server_sha256": config.server_sha256,
        "driver_source_sha256": sha256(include_bytes!("main.rs")), "driver_binary_sha256": hash_file(&std::env::current_exe()?)?,
        "config_sha256": sha256(&config_bytes), "ca_sha256": sha256(&ca_bytes), "endpoint": endpoint,
        "operations_per_worker": config.operations_per_worker, "errors": errors, "correctness_errors": correctness_errors, "correctness_checks": correctness_checks,
        "timing_scope": "closed-loop request send, full response body, JSON parse and validation; HTTP/1.1 keepalive; no retries; setup and final readbacks excluded",
        "throughput_scope": "successful logical operations per wall second; transaction is two durable puts",
        "write_mode": "insert_only",
        "rows": rows,
    });
    fs::write(
        args.output.join("receipt.json"),
        serde_json::to_vec_pretty(&receipt)?,
    )?;
    let mut summary = format!(
        "# C# TerraBase acceptance baseline\n\nRelease: `{}` · SHA-256: `{}`\n\n| Workload | Concurrency | Attempts | Errors | ops/s | p50 ms | p95 ms | p99 ms |\n| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |\n",
        config.server_version, config.server_sha256
    );
    for row in &rows {
        writeln!(
            summary,
            "| {} | {} | {} | {} | {:.2} | {} | {} | {} |",
            row.workload,
            row.concurrency,
            row.attempted,
            row.errors,
            row.throughput_ops_s,
            milliseconds(row.latency_p50_ns),
            milliseconds(row.latency_p95_ns),
            milliseconds(row.latency_p99_ns)
        )?;
    }
    write!(
        summary,
        "\nFinal readback checks: {correctness_checks}; errors: {correctness_errors}. [JSON evidence](receipt.json) · [Raw samples](samples.jsonl).\n\nProcess baseline; CPU priority is recorded in environment.json; timings are observations, never CI thresholds or SQLite qualification.\n"
    )?;
    fs::write(args.output.join("summary.md"), summary)?;
    ensure!(
        errors == 0,
        "acceptance failed with {errors} errors; see receipt.json"
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
            && config.server_sha256.len() == 64
            && config
                .server_sha256
                .bytes()
                .all(|byte| byte.is_ascii_hexdigit()),
        "release version and SHA-256 are required"
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
        _ => 0,
    };
    let number = (worker * operations_per_worker + index) * 2 + slot;
    format!("{namespace:08x}-{concurrency:04x}-4000-8000-{number:012x}")
}
fn expected_writes(
    workload: &str,
    concurrency: u32,
    operations_per_worker: u32,
) -> impl Iterator<Item = (String, Value)> + '_ {
    (0..concurrency).flat_map(move |worker| {
        (0..operations_per_worker).flat_map(move |index| {
            let count = match workload {
                "transactions" => 2,
                "point_write" => 1,
                "mixed_90_10" if index % 10 == 9 => 1,
                _ => 0,
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
                    write_document(worker, index, workload, concurrency),
                )
            })
        })
    })
}
fn document(rank: u32) -> Value {
    json!({"rank": rank, "group": rank % 2, "payload": "ts500"})
}
fn write_document(rank: u32, sequence: u32, workload: &str, concurrency: u32) -> Value {
    json!({"rank": rank, "group": rank % 2, "payload": "ts500-write", "sequence": sequence, "workload": workload, "concurrency": concurrency})
}
fn valid_document(body: &Value, id: &str, expected: &Value) -> bool {
    body["id"] == id
        && body["state"] == "value"
        && body["version"]
            .as_str()
            .is_some_and(|version| !version.is_empty())
        && body["document"] == *expected
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

#[allow(clippy::too_many_arguments, clippy::too_many_lines)]
async fn operation(
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
        "mixed_90_10" => {
            if index % 10 == 9 {
                "point_write"
            } else {
                "point_read"
            }
        }
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
    let value = write_document(worker, index, workload, concurrency);
    let (method, url, payload) = match operation {
        "point_write" => (
            Method::PUT,
            format!("{collection_url}/documents/{write_id}"),
            Some(value.clone()),
        ),
        "query_filter_sort" => (
            Method::POST,
            format!("{collection_url}/query"),
            Some(
                json!({"filter": {"and": [{"eq": ["/group", 0]}, {"eq": ["/payload", "ts500"]}]}, "sort": [{"path": "/rank", "order": "desc"}], "limit": 4}),
            ),
        ),
        "transactions" => (
            Method::POST,
            format!("{database_url}/transactions"),
            Some(json!({"ops": [
                {"op": "put", "collection": config.collection, "id": write_id, "document": value, "if_none_match": "*"},
                {"op": "put", "collection": config.collection, "id": second_id, "document": value, "if_none_match": "*"}
            ]})),
        ),
        _ => (
            Method::GET,
            format!("{collection_url}/documents/{read_id}"),
            None,
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
    let start = Instant::now();
    let (http_status, failure) = match request.send().await {
        Err(_) => (None, Some("request_error")),
        Ok(response) => {
            let status = response.status().as_u16();
            match response.bytes().await {
                Err(_) => (Some(status), Some("body_error")),
                Ok(_)
                    if (operation == "point_write" && status != 201)
                        || (operation != "point_write" && status != 200) =>
                {
                    (Some(status), Some("http_status_error"))
                }
                Ok(bytes) => match serde_json::from_slice::<Value>(&bytes) {
                    Err(_) => (Some(status), Some("invalid_json")),
                    Ok(body) => {
                        let valid = match operation {
                            "point_write" => valid_write(&body, &write_id),
                            "transactions" => {
                                durable(&body)
                                    && body["results"].as_array().is_some_and(|items| {
                                        items.len() == 2
                                            && items.iter().zip([&write_id, &second_id]).all(
                                                |(item, id)| {
                                                    item["id"] == **id
                                                        && item["version"].as_str().is_some_and(
                                                            |version| !version.is_empty(),
                                                        )
                                                },
                                            )
                                    })
                            }
                            "query_filter_sort" => body["items"].as_array().is_some_and(|items| {
                                items.len() == 4
                                    && items.iter().zip([30, 28, 26, 24]).all(|(item, rank)| {
                                        valid_document(item, &document_id(rank), &document(rank))
                                    })
                            }),
                            _ => valid_document(&body, &read_id, &document(worker % 32)),
                        };
                        (
                            Some(status),
                            if valid {
                                None
                            } else {
                                Some("response_mismatch")
                            },
                        )
                    }
                },
            }
        }
    };
    Sample {
        workload,
        concurrency,
        worker,
        index,
        operation,
        latency_ns: nanos(start),
        http_status,
        failure,
    }
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
                for (id, _) in expected_writes(workload, concurrency, 20) {
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
            let writes: Vec<_> = expected_writes(workload, 2, 20).collect();
            assert_eq!(writes.len(), count, "{workload}");
            let ids: std::collections::HashSet<_> = writes.iter().map(|(id, _)| id).collect();
            assert_eq!(ids.len(), count, "duplicate IDs in {workload}");
            for worker in 0..2 {
                for index in 0..20 {
                    for slot in 0..slots {
                        let expected = (
                            write_document_id(workload, 2, worker, index, 20, slot),
                            write_document(worker, index, workload, 2),
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
            assert_eq!(expected_writes(workload, 2, 20).count(), 0);
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
                for (id, expected) in expected_writes(workload, 2, 20) {
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
        let previous = write_document(0, 9, "point_write", 1);
        let current = write_document(0, 9, "transactions", 4);
        let stale = json!({"id": "x", "state": "value", "version": "v1", "document": previous});
        assert!(!valid_document(&stale, "x", &current));
        assert!(!durable(&json!({"durability": "memory", "offset": "1"})));
    }
}
