use std::fs::{self, File, OpenOptions};
use std::io::{BufWriter, Write};
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result, bail, ensure};
use clap::Parser;
use reqwest::header::CONNECTION;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use sha2::{Digest, Sha256};

const SAMPLES: u32 = 1_000;
const DEFAULT_INTER_REQUEST_DELAY_MS: u32 = 20;

const fn default_inter_request_delay_ms() -> u32 {
    DEFAULT_INTER_REQUEST_DELAY_MS
}

#[derive(Parser)]
#[command(about = "Measure verified, serial TerraBase HTTPS document point reads")]
struct Args {
    /// JSON workload and provenance config. It must not contain the bearer token.
    #[arg(long)]
    config: PathBuf,
    /// File containing only the scoped bearer token (no token is written to receipts).
    #[arg(long)]
    token_file: PathBuf,
    /// Existing bench/ directory in which an immutable run directory is created.
    #[arg(long)]
    bench_dir: PathBuf,
    /// Unique ASCII run ID using letters, digits, hyphens, or underscores.
    #[arg(long)]
    run_id: String,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Config {
    url: String,
    ca_pem_path: PathBuf,
    expected_response_path: PathBuf,
    server_binary_path: PathBuf,
    server_source_revision: String,
    server_source_manifest_path: PathBuf,
    timeout_ms: u64,
    #[serde(default = "default_inter_request_delay_ms")]
    inter_request_delay_ms: u32,
}

#[derive(Serialize)]
struct Sample {
    index: u32,
    latency_ns: u64,
    http_status: Option<u16>,
    ok: bool,
    failure: Option<&'static str>,
}

struct FixtureInputs {
    config_bytes: Vec<u8>,
    config: Config,
    expected_bytes: Vec<u8>,
    expected: Value,
    token: String,
    ca_bytes: Vec<u8>,
    ca: reqwest::Certificate,
    server_binary_sha256: String,
    server_source_manifest_sha256: String,
}

struct SampleRun {
    preflight: Sample,
    latencies: Vec<u64>,
    failed: usize,
    attempted: usize,
}

#[derive(Serialize)]
struct Receipt<'a> {
    schema_version: u8,
    workload: &'static str,
    status: &'static str,
    run_id: &'a str,
    started_unix_ms: u128,
    ended_unix_ms: u128,
    url: &'a str,
    samples_planned: u32,
    samples_attempted: usize,
    samples_ok: usize,
    samples_failed: usize,
    preflight: &'a Sample,
    timing_scope: &'static str,
    connection_policy: &'static str,
    pacing_policy: &'static str,
    inter_request_delay_ms: u32,
    configured_offered_load_cap_rps: f64,
    driver_source_sha256: String,
    driver_manifest_sha256: String,
    driver_binary_sha256: String,
    config_sha256: String,
    expected_response_sha256: String,
    ca_pem_sha256: String,
    server_binary_sha256: String,
    server_source_revision: &'a str,
    server_source_manifest_sha256: String,
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

#[tokio::main(flavor = "current_thread")]
async fn run(args: Args) -> Result<()> {
    let Args {
        config: config_path,
        token_file,
        bench_dir,
        run_id,
    } = args;
    ensure!(
        !run_id.is_empty()
            && run_id
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_'),
        "run ID must contain only ASCII letters, digits, hyphens, or underscores"
    );
    ensure!(
        bench_dir.file_name().is_some_and(|name| name == "bench"),
        "--bench-dir must name an existing bench/ directory"
    );
    ensure!(bench_dir.is_dir(), "bench/ directory does not exist");

    let inputs = load_fixture_inputs(&config_path, &token_file)?;
    let driver_binary_sha256 = hash_file(&std::env::current_exe().context("find driver binary")?)
        .context("hash driver binary")?;

    let case_dir = bench_dir.join("https-document-point-read");
    fs::create_dir_all(&case_dir).context("create case directory under bench/")?;
    let run_dir = case_dir.join(&run_id);
    fs::create_dir(&run_dir)
        .with_context(|| format!("create fresh run directory {}", run_dir.display()))?;
    let raw_path = run_dir.join("samples.jsonl");
    let raw_file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&raw_path)
        .context("create raw sample file")?;
    let raw = BufWriter::new(raw_file);
    let started_unix_ms = unix_ms()?;
    let samples = collect_samples(
        &inputs.config,
        &inputs.ca,
        &inputs.token,
        &inputs.expected,
        raw,
    )
    .await?;

    let passed =
        samples.preflight.ok && samples.failed == 0 && samples.attempted == SAMPLES as usize;
    let receipt = Receipt {
        schema_version: 1,
        workload: "serial_authenticated_https_document_point_read",
        status: if passed { "passed" } else { "failed" },
        run_id: &run_id,
        started_unix_ms,
        ended_unix_ms: unix_ms()?,
        url: &inputs.config.url,
        samples_planned: SAMPLES,
        samples_attempted: samples.attempted,
        samples_ok: samples.latencies.len(),
        samples_failed: samples.failed,
        preflight: &samples.preflight,
        timing_scope: "request send through full response body read, JSON parse, and exact JSON equality; client and request construction and pacing excluded",
        connection_policy: "new rustls reqwest client per operation; HTTP/1.1; Connection: close",
        pacing_policy: "minimum idle delay after every completed timed attempt except the last; no retries",
        inter_request_delay_ms: inputs.config.inter_request_delay_ms,
        configured_offered_load_cap_rps: 1_000.0 / f64::from(inputs.config.inter_request_delay_ms),
        driver_source_sha256: sha256(include_bytes!("main.rs")),
        driver_manifest_sha256: sha256(include_bytes!("../Cargo.toml")),
        driver_binary_sha256,
        config_sha256: sha256(&inputs.config_bytes),
        expected_response_sha256: sha256(&inputs.expected_bytes),
        ca_pem_sha256: sha256(&inputs.ca_bytes),
        server_binary_sha256: inputs.server_binary_sha256,
        server_source_revision: &inputs.config.server_source_revision,
        server_source_manifest_sha256: inputs.server_source_manifest_sha256,
        latency_p50_ns: percentile(&samples.latencies, 50),
        latency_p95_ns: percentile(&samples.latencies, 95),
        latency_p99_ns: percentile(&samples.latencies, 99),
        latency_max_ns: samples.latencies.last().copied(),
    };
    let receipt_path = run_dir.join("receipt.json");
    let mut receipt_file = File::create(&receipt_path).context("create receipt")?;
    serde_json::to_writer_pretty(&mut receipt_file, &receipt).context("write receipt")?;
    receipt_file.write_all(b"\n").context("terminate receipt")?;
    receipt_file.sync_all().context("sync receipt")?;

    write_summary(&run_dir, &run_id, &inputs.config, &receipt, &samples)?;
    println!("{}", run_dir.display());
    if !passed {
        bail!(
            "HTTPS document point read failed; see {}",
            receipt_path.display()
        );
    }
    Ok(())
}

fn load_fixture_inputs(config_path: &Path, token_path: &Path) -> Result<FixtureInputs> {
    let config_bytes = fs::read(config_path).context("read workload config")?;
    let config: Config = serde_json::from_slice(&config_bytes).context("parse workload config")?;
    validate_config(&config)?;
    let expected_bytes =
        fs::read(&config.expected_response_path).context("read expected document response")?;
    let expected: Value =
        serde_json::from_slice(&expected_bytes).context("parse expected document response")?;
    ensure!(
        expected["version"] == 1 && expected["state"] == "value",
        "expected response must be a version 1 live document point read"
    );
    ensure!(
        expected["siblings"]
            .as_array()
            .is_some_and(|items| items.len() == 1),
        "expected response must contain exactly one sibling"
    );
    ensure!(
        expected["siblings"][0]["fields"].is_array(),
        "expected response must contain sibling fields"
    );

    let token = fs::read_to_string(token_path).context("read bearer token file")?;
    let token = token.trim_end_matches(['\r', '\n']).to_owned();
    ensure!(
        !token.is_empty() && !token.chars().any(char::is_whitespace),
        "bearer token file must contain one nonempty token"
    );
    let ca_bytes = fs::read(&config.ca_pem_path).context("read CA PEM")?;
    let ca = reqwest::Certificate::from_pem(&ca_bytes).context("parse CA PEM")?;
    let server_binary_sha256 =
        hash_file(&config.server_binary_path).context("hash supplied server binary")?;
    let server_source_manifest_sha256 = hash_file(&config.server_source_manifest_path)
        .context("hash supplied server source manifest")?;

    Ok(FixtureInputs {
        config_bytes,
        config,
        expected_bytes,
        expected,
        token,
        ca_bytes,
        ca,
        server_binary_sha256,
        server_source_manifest_sha256,
    })
}

async fn collect_samples(
    config: &Config,
    ca: &reqwest::Certificate,
    token: &str,
    expected: &Value,
    mut raw: BufWriter<File>,
) -> Result<SampleRun> {
    // The preflight checks the supplied fixture but is never included in sample statistics.
    let preflight = read_once(0, config, ca, token, expected).await;
    let mut latencies = Vec::with_capacity(SAMPLES as usize);
    let mut failed = 0_usize;
    let mut attempted = 0_usize;
    if preflight.ok {
        for index in 1..=SAMPLES {
            let sample = read_once(index, config, ca, token, expected).await;
            attempted += 1;
            if sample.ok {
                latencies.push(sample.latency_ns);
            } else {
                failed += 1;
            }
            serde_json::to_writer(&mut raw, &sample).context("write raw sample")?;
            raw.write_all(b"\n").context("terminate raw sample")?;
            raw.flush().context("flush raw sample")?;
            if index < SAMPLES {
                tokio::time::sleep(Duration::from_millis(u64::from(
                    config.inter_request_delay_ms,
                )))
                .await;
            }
        }
    }
    raw.into_inner()
        .map_err(std::io::IntoInnerError::into_error)?
        .sync_all()
        .context("sync raw samples")?;
    latencies.sort_unstable();

    Ok(SampleRun {
        preflight,
        latencies,
        failed,
        attempted,
    })
}

fn write_summary(
    run_dir: &Path,
    run_id: &str,
    config: &Config,
    receipt: &Receipt<'_>,
    samples: &SampleRun,
) -> Result<()> {
    let summary = format!(
        "# HTTPS document point read: {}\n\n\
         | Workload | Status | Attempted | Correct | Failed | Idle delay (ms) | Offer cap (req/s) | p50 (ms) | p95 (ms) | p99 (ms) | Max (ms) |\n\
         | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |\n\
         | 1,000 serial authenticated GETs | {} | {} | {} | {} | {} | {:.1} | {} | {} | {} | {} |\n\n\
         Each sample uses a new HTTPS connection and verifies the entire parsed JSON response. \
         The untimed preflight {}. A minimum {} ms idle delay follows each timed attempt except the last; \
         the configured offer is at most {:.1} requests/s, and actual offered load may be lower. \
         Latency starts immediately before request send and includes TLS, server authentication, HTTP, body read, JSON parsing, and validation. \
         Client and request construction, pacing, and sample writes are outside that timer.\n\n\
         [Receipt](receipt.json) · [Raw samples](samples.jsonl)\n",
        run_id,
        receipt.status,
        samples.attempted,
        samples.latencies.len(),
        samples.failed,
        config.inter_request_delay_ms,
        receipt.configured_offered_load_cap_rps,
        ms(receipt.latency_p50_ns),
        ms(receipt.latency_p95_ns),
        ms(receipt.latency_p99_ns),
        ms(receipt.latency_max_ns),
        if samples.preflight.ok {
            "passed"
        } else {
            "failed"
        },
        config.inter_request_delay_ms,
        receipt.configured_offered_load_cap_rps,
    );
    fs::write(run_dir.join("summary.md"), summary).context("write Markdown summary")
}

fn validate_config(config: &Config) -> Result<()> {
    let url = reqwest::Url::parse(&config.url).context("parse HTTPS document URL")?;
    ensure!(url.scheme() == "https", "document URL must use HTTPS");
    ensure!(
        url.username().is_empty() && url.password().is_none(),
        "document URL must not contain credentials"
    );
    ensure!(
        url.query().is_none() && url.fragment().is_none(),
        "document URL must not contain a query or fragment"
    );
    let segments: Vec<_> = url
        .path_segments()
        .context("document URL must have path segments")?
        .collect();
    ensure!(
        segments.len() == 11
            && segments[0] == "v1"
            && segments[1] == "tenants"
            && segments[3] == "databases"
            && segments[5] == "shards"
            && segments[7] == "collections"
            && segments[9] == "documents"
            && [2, 4, 6, 8, 10]
                .into_iter()
                .all(|index| !segments[index].is_empty()),
        "document URL must use the TerraBase v1 document point-read route"
    );
    ensure!(config.timeout_ms > 0, "timeout_ms must be positive");
    ensure!(
        (10..=1_000).contains(&config.inter_request_delay_ms),
        "inter_request_delay_ms must be between 10 and 1000"
    );
    ensure!(
        !config.server_source_revision.trim().is_empty(),
        "server_source_revision is required"
    );
    for path in [
        &config.ca_pem_path,
        &config.expected_response_path,
        &config.server_binary_path,
        &config.server_source_manifest_path,
    ] {
        ensure!(path.is_absolute(), "config input paths must be absolute");
    }
    Ok(())
}

async fn read_once(
    index: u32,
    config: &Config,
    ca: &reqwest::Certificate,
    token: &str,
    expected: &Value,
) -> Sample {
    let Ok(client) = reqwest::Client::builder()
        .tls_certs_only([ca.clone()])
        .https_only(true)
        .http1_only()
        .no_proxy()
        .redirect(reqwest::redirect::Policy::none())
        .timeout(Duration::from_millis(config.timeout_ms))
        .build()
    else {
        return Sample {
            index,
            latency_ns: 0,
            http_status: None,
            ok: false,
            failure: Some("client_configuration_error"),
        };
    };
    let request = client
        .get(&config.url)
        .header(CONNECTION, "close")
        .bearer_auth(token);
    let started = Instant::now();
    let response = request.send().await;
    let (http_status, failure) = match response {
        Err(_) => (None, Some("request_error")),
        Ok(response) => {
            let status = response.status().as_u16();
            match response.bytes().await {
                Err(_) => (Some(status), Some("response_body_error")),
                Ok(_) if status != 200 => (Some(status), Some("http_status_error")),
                Ok(bytes) => match serde_json::from_slice::<Value>(&bytes) {
                    Err(_) => (Some(status), Some("invalid_json")),
                    Ok(actual) if &actual != expected => (Some(status), Some("response_mismatch")),
                    Ok(_) => (Some(status), None),
                },
            }
        }
    };
    Sample {
        index,
        latency_ns: started.elapsed().as_nanos().try_into().unwrap_or(u64::MAX),
        http_status,
        ok: failure.is_none(),
        failure,
    }
}

fn sha256(bytes: &[u8]) -> String {
    hex::encode(Sha256::digest(bytes))
}

fn hash_file(path: &Path) -> Result<String> {
    Ok(sha256(&fs::read(path)?))
}

fn unix_ms() -> Result<u128> {
    Ok(SystemTime::now().duration_since(UNIX_EPOCH)?.as_millis())
}

fn percentile(sorted: &[u64], percent: usize) -> Option<u64> {
    if sorted.is_empty() {
        return None;
    }
    let rank = (sorted.len() * percent).div_ceil(100).saturating_sub(1);
    sorted.get(rank).copied()
}

fn ms(nanoseconds: Option<u64>) -> String {
    nanoseconds.map_or_else(
        || "—".to_owned(),
        |value| format!("{}.{:03}", value / 1_000_000, value % 1_000_000 / 1_000),
    )
}
