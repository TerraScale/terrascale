//! ts-auth management API client benchmark for the official Rust SDK
//! (terrascale, the sibling ts-rust checkout). Mirrors ts-auth/go/bench.go.
//!
//! A canned-response in-process transport (no socket, no web stack) serves the
//! exact route shapes the official client requests, so every measured
//! operation pays the full client path — typed request, JSON encoding,
//! transport machinery, JSON decode, response verification — against a
//! constant service cost. This is the same measurement class as the .NET
//! `tsauth-sdk` engine-comparison lane (raw in-process handler, no socket).
//!
//! Usage:
//!
//! ```sh
//! taskset -c 0-3 nice -n 19 ./target/release/ts-auth-bench \
//!     --samples 5 --operations 5000 --warmup 1000 --seed 1337 \
//!     --output ../../../ts-benchmark/ts-auth/receipts/<date>-<slug>
//! ```

use std::future::Future;
use std::pin::Pin;
use std::process::exit;
use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::Arc;
use std::time::{Instant, SystemTime, UNIX_EPOCH};

use serde_json::{json, Value};
use terrascale::auth_idp::{AuthIdpUserPatch, AuthIdpUsers, CreateAuthIdpUser};
use terrascale::{ClientConfig, HttpTransport, Method, Request, Response, TerraScaleClient};

const INSTANCE_SLUG: &str = "bench";
const DATASET_SIZE: i64 = 1000;
const PAYLOAD_SIZE: usize = 256;
const BEARER: &str = "Bearer sk_benchmark_key";
const RUSTC_VERSION: &str = "rustc 1.97.1 (pinned toolchain)";

fn parse_flag<T: std::str::FromStr>(raw: String, flag: &str) -> T {
    match raw.parse() {
        Ok(parsed) => parsed,
        Err(_) => {
            eprintln!("--{flag} must be an integer");
            exit(2);
        }
    }
}

#[tokio::main]
async fn main() {
    let mut samples: usize = 5;
    let mut operations: usize = 5000;
    let mut warmup: usize = 1000;
    let mut seed: i64 = 1337;
    let mut output = String::new();
    let mut args = std::env::args().skip(1);
    while let Some(flag) = args.next() {
        let mut value = || match args.next() {
            Some(value) => value,
            None => {
                eprintln!("missing value for {flag}");
                exit(2);
            }
        };
        match flag.as_str() {
            "--samples" => samples = parse_flag(value(), "samples"),
            "--operations" => operations = parse_flag(value(), "operations"),
            "--warmup" => warmup = parse_flag(value(), "warmup"),
            "--seed" => seed = parse_flag(value(), "seed"),
            "--output" => output = value(),
            _ => {
                eprintln!("unknown flag {flag}");
                exit(2);
            }
        }
    }
    if samples < 5 {
        eprintln!("--samples must be at least 5");
        exit(2);
    }
    if output.is_empty() {
        eprintln!("--output is required");
        exit(2);
    }

    if let Err(error) = run(samples, operations, warmup, seed, &output).await {
        eprintln!("bench failed: {error}");
        exit(1);
    }
}

async fn run(
    samples: usize,
    operations: usize,
    warmup: usize,
    seed: i64,
    output: &str,
) -> Result<(), String> {
    std::fs::create_dir_all(output).map_err(|e| e.to_string())?;
    let config = ClientConfig::builder()
        .api_base_url("http://127.0.0.1/")
        .auth_base_url("http://127.0.0.1/")
        .auth_api_key("sk_benchmark_key")
        .build()
        .map_err(|e| e.to_string())?;
    let client = TerraScaleClient::with_transport(config, Arc::new(Stub::new()));
    let users = client
        .auth_idp(INSTANCE_SLUG)
        .map_err(|e| e.to_string())?
        .users();

    let lanes = [
        (Lane::Read, "read"),
        (Lane::Query, "query"),
        (Lane::Mixed, "mixed"),
        (Lane::Write, "write"),
    ];
    let concurrency = [1usize, 16];

    let mut results: Vec<CaseResult> = Vec::new();
    for (lane, name) in lanes {
        for workers in concurrency {
            if lane == Lane::Query && workers > 1 {
                continue; // The .NET lane matrix: query only at c1.
            }
            for i in 0..warmup {
                run_op(lane, &users, i as i64)
                    .await
                    .map_err(|e| format!("{name} warmup: {e}"))?;
            }
            for sample in 0..samples {
                let result = measure_sample(name, lane, &users, workers, operations).await;
                results.push(CaseResult {
                    lane: name,
                    concurrency: workers,
                    throughput: result.throughput,
                    p50: result.p50,
                    p95: result.p95,
                    p99: result.p99,
                });
                println!(
                    "  rust {name:<6} c{workers:<2} sample {}/{}: {:.1} ops/s",
                    sample + 1,
                    samples,
                    result.throughput
                );
            }
        }
    }

    let cases: Vec<Value> = results
        .iter()
        .map(|case| {
            json!({
                "lane": case.lane,
                "concurrency": case.concurrency,
                "throughputOpsPerSecond": case.throughput,
                "p50Microseconds": case.p50,
                "p95Microseconds": case.p95,
                "p99Microseconds": case.p99,
                "allocatedBytesPerOperation": Value::Null,
            })
        })
        .collect();
    let receipt = json!({
        "subject": "ts-rust (official Rust SDK, AuthIdpClient)",
        "sdkLanguage": "rust",
        "rustcVersion": RUSTC_VERSION,
        "samples": samples,
        "operations": operations,
        "warmup": warmup,
        "seed": seed,
        "datasetSize": DATASET_SIZE,
        "payloadSize": PAYLOAD_SIZE,
        "instanceSlug": INSTANCE_SLUG,
        "notes": "allocatedBytesPerOperation is null: stable Rust exposes no allocation counter.",
        "cases": cases,
        "finishedUTC": rfc3339_now(),
    });
    let receipt_path = format!("{output}/rust-receipt.json");
    std::fs::write(
        &receipt_path,
        serde_json::to_string_pretty(&receipt).map_err(|e| e.to_string())?,
    )
    .map_err(|e| e.to_string())?;

    let mut summary = String::from("# ts-auth Rust SDK client benchmark receipt\n\n");
    summary.push_str(
        "| Lane | Concurrency | ops/s | p50 µs | p95 µs | p99 µs | B/op |\n| --- | --- | ---: | ---: | ---: | ---: | ---: |\n",
    );
    for case in &results {
        summary.push_str(&format!(
            "| {} | c{} | {:.0} | {:.1} | {:.1} | {:.1} | - |\n",
            case.lane, case.concurrency, case.throughput, case.p50, case.p95, case.p99
        ));
    }
    std::fs::write(format!("{output}/rust-summary.md"), summary).map_err(|e| e.to_string())?;
    println!("Receipt: {receipt_path}");
    Ok(())
}

// ---------------------------------------------------------------------------
// Lanes (identical to bench.go)
// ---------------------------------------------------------------------------

#[derive(Clone, Copy, PartialEq)]
enum Lane {
    Read,
    Query,
    Mixed,
    Write,
}

struct CaseResult {
    lane: &'static str,
    concurrency: usize,
    throughput: f64,
    p50: f64,
    p95: f64,
    p99: f64,
}

struct SampleResult {
    throughput: f64,
    p50: f64,
    p95: f64,
    p99: f64,
}

async fn run_op(lane: Lane, users: &AuthIdpUsers, seq: i64) -> Result<(), String> {
    match lane {
        Lane::Read => {
            let user = users
                .get(&format!("usr_{}", seq % DATASET_SIZE))
                .await
                .map_err(|e| e.to_string())?;
            if !user.id.starts_with("usr_")
                || user.email_verified != Some(true)
                || !user
                    .public_metadata
                    .as_object()
                    .is_some_and(|metadata| !metadata.is_empty())
            {
                return Err(format!("invalid user read: {}", user.id));
            }
            Ok(())
        }
        Lane::Query => {
            let page = users
                .list(Some(50), Some(&format!("usr_{}", seq % DATASET_SIZE)))
                .await
                .map_err(|e| e.to_string())?;
            let expected = format!("usr_{}", (seq % DATASET_SIZE + 1) % DATASET_SIZE);
            if page.data.len() != 50 || page.data[0].id != expected {
                return Err(format!(
                    "invalid page: {} rows starting at {}",
                    page.data.len(),
                    page.data.first().map(|user| user.id.as_str()).unwrap_or("")
                ));
            }
            Ok(())
        }
        Lane::Mixed => {
            if seq % 2 == 0 {
                let user = users
                    .get(&format!("usr_{}", seq % DATASET_SIZE))
                    .await
                    .map_err(|e| e.to_string())?;
                if !user.id.starts_with("usr_") {
                    return Err(format!("invalid mixed read: {}", user.id));
                }
                Ok(())
            } else {
                let created = users
                    .create(CreateAuthIdpUser {
                        email: Some(format!("bench-mixed-{seq}@example.test")),
                        ..CreateAuthIdpUser::default()
                    })
                    .await
                    .map_err(|e| e.to_string())?;
                if created.id.is_empty() {
                    return Err(format!("invalid mixed write for {seq}"));
                }
                Ok(())
            }
        }
        Lane::Write => {
            if seq % 2 == 0 {
                let email = format!("bench-write-{seq}@example.test");
                let created = users
                    .create(CreateAuthIdpUser {
                        email: Some(email.clone()),
                        name: Some(format!("Write User {seq}")),
                        ..CreateAuthIdpUser::default()
                    })
                    .await
                    .map_err(|e| e.to_string())?;
                if created.email.as_deref() != Some(email.as_str()) {
                    return Err(format!("invalid create for {seq}"));
                }
                Ok(())
            } else {
                let name = format!("Renamed {seq}");
                let updated = users
                    .patch(
                        "usr_0",
                        AuthIdpUserPatch::new()
                            .with_name(name.clone())
                            .with_banned(seq % 4 == 1),
                    )
                    .await
                    .map_err(|e| e.to_string())?;
                if updated.name.as_deref() != Some(name.as_str()) {
                    return Err(format!("invalid patch for {seq}"));
                }
                Ok(())
            }
        }
    }
}

async fn measure_sample(
    lane_name: &'static str,
    lane: Lane,
    users: &AuthIdpUsers,
    workers: usize,
    operations: usize,
) -> SampleResult {
    let seq = Arc::new(AtomicI64::new(0));
    let per_worker = operations / workers;
    let mut handles = Vec::with_capacity(workers);
    let start = Instant::now();
    for _ in 0..workers {
        let users = users.clone();
        let seq = Arc::clone(&seq);
        handles.push(tokio::spawn(async move {
            let mut local = Vec::with_capacity(per_worker);
            for _ in 0..per_worker {
                let current = seq.fetch_add(1, Ordering::Relaxed) + 1;
                let op_start = Instant::now();
                if let Err(error) = run_op(lane, &users, current).await {
                    eprintln!("{lane_name} operation failed: {error}");
                    exit(1);
                }
                local.push(op_start.elapsed());
            }
            local
        }));
    }
    let mut latencies = Vec::with_capacity(operations);
    for handle in handles {
        latencies.extend(handle.await.expect("worker panicked"));
    }
    let elapsed = start.elapsed();

    latencies.sort();
    let pct = |fraction: f64| {
        let mut index = (fraction * latencies.len() as f64) as usize;
        if index >= latencies.len() {
            index = latencies.len() - 1;
        }
        latencies[index].as_nanos() as f64 / 1000.0
    };
    SampleResult {
        throughput: operations as f64 / elapsed.as_secs_f64(),
        p50: pct(0.50),
        p95: pct(0.95),
        p99: pct(0.99),
    }
}

// ---------------------------------------------------------------------------
// Canned-response transport (no socket): usr_0..usr_999, 256-byte payloads.
// ---------------------------------------------------------------------------

#[derive(Debug)]
struct Stub {
    users: Vec<String>,
}

impl Stub {
    fn new() -> Self {
        let payload = "x".repeat(PAYLOAD_SIZE);
        let users = (0..DATASET_SIZE)
            .map(|index| {
                format!(
                    r#"{{"id":"usr_{index}","email":"bench-{index}@example.test","email_verified":true,"username":"bench{index}","name":"Bench User {index}","banned":false,"ban_reason":null,"created_at":1700000000,"last_sign_in_at":null,"public_metadata":{{"index":{index},"payload":"{payload}"}},"private_metadata":{{"plan":"pro","payload":"{payload}"}}}}"#
                )
            })
            .collect();
        Self { users }
    }

    fn serve(&self, request: &Request) -> Response {
        let path_query = request
            .url
            .split_once("://")
            .map(|(_, rest)| rest)
            .and_then(|rest| rest.find('/').map(|index| &rest[index..]))
            .unwrap_or("/");
        let (path, query) = match path_query.split_once('?') {
            Some((path, query)) => (path, query),
            None => (path_query, ""),
        };
        let prefix = format!("/v1/{INSTANCE_SLUG}/users");
        let authorized = request.headers.iter().any(|(name, value)| {
            name.eq_ignore_ascii_case("authorization") && value.eq_ignore_ascii_case(BEARER)
        });
        if !path.starts_with(&prefix) || !authorized {
            return respond(
                401,
                r#"{"error":{"code":"unauthorized","message":"Missing or invalid credentials."}}"#,
            );
        }

        let remainder = &path[prefix.len()..];
        if request.method == Method::Get && remainder.is_empty() {
            let mut limit: i64 = 20;
            let mut after: i64 = 0;
            for pair in query.split('&').filter(|pair| !pair.is_empty()) {
                match pair.split_once('=') {
                    Some(("limit", raw)) => limit = raw.parse().unwrap_or(limit),
                    Some(("starting_after", cursor)) if cursor.starts_with("usr_") => {
                        after = cursor["usr_".len()..].parse().unwrap_or(0);
                    }
                    _ => {}
                }
            }
            if !(1..=100).contains(&limit) {
                return respond(
                    400,
                    r#"{"error":{"code":"invalid_request","message":"limit must be between 1 and 100"}}"#,
                );
            }
            let rows: String = (0..limit)
                .map(|offset| self.users[((after + 1 + offset) % DATASET_SIZE) as usize].as_str())
                .collect::<Vec<_>>()
                .join(",");
            return respond(200, &format!(r#"{{"data":[{rows}]}}"#));
        }
        if let Some(user_id) = remainder.strip_prefix('/') {
            if request.method == Method::Get && user_id.starts_with("usr_") {
                if let Ok(index) = user_id["usr_".len()..].parse::<i64>() {
                    if (0..DATASET_SIZE).contains(&index) && user_id == format!("usr_{index}") {
                        return respond(200, &self.users[index as usize]);
                    }
                }
            }
            if request.method == Method::Patch && user_id == "usr_0" {
                let Some(body) = parse_body(request) else {
                    return respond(
                        400,
                        r#"{"error":{"code":"invalid_json","message":"the request body is not valid JSON"}}"#,
                    );
                };
                let name = body["name"].as_str().unwrap_or("Bench User 0").to_owned();
                let banned = body["banned"].as_bool().unwrap_or(false);
                return respond(
                    200,
                    &format!(
                        r#"{{"id":"usr_0","email":"bench-0@example.test","email_verified":true,"username":"bench0","name":"{name}","banned":{banned},"ban_reason":null,"created_at":1700000000,"last_sign_in_at":null,"public_metadata":{{"index":0}},"private_metadata":{{"plan":"pro"}}}}"#
                    ),
                );
            }
        }
        if request.method == Method::Post && remainder.is_empty() {
            let Some(body) = parse_body(request) else {
                return respond(
                    400,
                    r#"{"error":{"code":"invalid_json","message":"the request body is not valid JSON"}}"#,
                );
            };
            let email = body["email"].as_str().unwrap_or_default();
            // The stub answers 200; the official client accepts any 2xx for create.
            return respond(
                200,
                &format!(
                    r#"{{"id":"usr_created","email":"{email}","email_verified":false,"username":null,"name":null,"banned":false,"ban_reason":null,"created_at":1700000001,"last_sign_in_at":null,"public_metadata":{{}},"private_metadata":{{}}}}"#
                ),
            );
        }

        respond(
            404,
            r#"{"error":{"code":"user_not_found","message":"no user with this id in this instance"}}"#,
        )
    }
}

fn respond(status: u16, body: &str) -> Response {
    Response {
        status,
        headers: vec![("content-type".to_owned(), "application/json".to_owned())],
        body: body.as_bytes().to_vec().into(),
    }
}

/// Parse a JSON request body, mirroring the Go stub's io.ReadAll + Unmarshal.
fn parse_body(request: &Request) -> Option<Value> {
    serde_json::from_slice(request.body.as_deref()?).ok()
}

// The trait is declared through async-trait inside the SDK; the desugared
// boxed-future signature is implemented here so the harness needs no extra
// dependency beyond the SDK itself.
impl HttpTransport for Stub {
    fn send<'life0, 'async_trait>(
        &'life0 self,
        request: Request,
    ) -> Pin<Box<dyn Future<Output = terrascale::Result<Response>> + Send + 'async_trait>>
    where
        'life0: 'async_trait,
    {
        Box::pin(async move { Ok(self.serve(&request)) })
    }
}

// ---------------------------------------------------------------------------
// RFC 3339 UTC timestamp (std only)
// ---------------------------------------------------------------------------

fn rfc3339_now() -> String {
    let seconds = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    let (year, month, day) = civil_from_days((seconds / 86_400) as i64);
    let time_of_day = seconds % 86_400;
    format!(
        "{year:04}-{month:02}-{day:02}T{:02}:{:02}:{:02}Z",
        time_of_day / 3600,
        time_of_day % 3600 / 60,
        time_of_day % 60
    )
}

/// Days since the unix epoch to a civil date (Howard Hinnant's algorithm).
fn civil_from_days(days: i64) -> (i64, u64, u64) {
    let shifted = days + 719_468;
    let era = if shifted >= 0 {
        shifted
    } else {
        shifted - 146_096
    } / 146_097;
    let day_of_era = shifted - era * 146_097;
    let year_of_era =
        (day_of_era - day_of_era / 1460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let year = year_of_era + era * 400;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let month_prime = (5 * day_of_year + 2) / 153;
    let day = (day_of_year - (153 * month_prime + 2) / 5 + 1) as u64;
    let month = if month_prime < 10 {
        month_prime + 3
    } else {
        month_prime - 9
    } as u64;
    (if month <= 2 { year + 1 } else { year }, month, day)
}
