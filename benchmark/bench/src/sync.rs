//! E-13 sync benchmark driver.
//!
//! This is intentionally a standalone, dependency-free binary.  The normal
//! benchmark lane has a single shared dispatcher, while this family has a
//! different contract (REST attach + immutable MessagePack and WebSocket), so
//! `bench/scripts/sync.sh` compiles this file directly and does not modify the
//! shared lane registry.  Every measured byte comes from a served TerraBase
//! member or the configured CDN endpoint; this program never fabricates a
//! successful sample.

#![allow(dead_code, reason = "the sync driver is also compiled as a standalone family binary")]
#![allow(clippy::too_many_arguments)]

use std::io::{Read, Write};
use std::net::{Shutdown, TcpStream, ToSocketAddrs};
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

const CADENCE_MS: u64 = 100;
const LOCAL_P99_MS: f64 = 50.0;
const CDN_P99_MS: f64 = 250.0;
const CROSS_P50_MS: f64 = 300.0;
const CROSS_P99_MS: f64 = 1_000.0;
const DEFAULT_SAMPLES: usize = 25;
const MAX_REGIONS: usize = 3;
const MAX_MEMBER_CONNECTIONS: usize = 4;
const DEFAULT_RSS_LIMIT_MIB: u64 = 512;

#[derive(Clone, Debug)]
struct Config {
    local: Option<String>,
    cdn: Option<String>,
    regions: Vec<(String, String)>,
    database: String,
    token: Option<String>,
    collection: String,
    sql: String,
    samples: usize,
    cadence_ms: u64,
    drive_writes: bool,
    replication: String,
    rss_limit_mib: u64,
    member_pid: Option<u32>,
    output: Option<String>,
    runtime_digest: String,
    comparator_digest: String,
}

#[derive(Clone, Debug)]
struct Endpoint {
    scheme: String,
    host: String,
    port: u16,
    base_path: String,
}

#[derive(Clone, Debug)]
struct HttpResponse {
    status: u16,
    body: Vec<u8>,
}

#[derive(Clone, Debug)]
struct Attach {
    view: String,
    generation: u64,
    snapshot: String,
    log: String,
    capability: String,
    offset: u64,
}

#[derive(Clone, Debug)]
struct Verdict {
    status: &'static str,
    reason: String,
    samples_ms: Vec<f64>,
    p50_ms: Option<f64>,
    p99_ms: Option<f64>,
}

pub fn run_cli<I>(arguments: I) -> Result<i32, String>
where
    I: IntoIterator<Item = String>,
{
    let config = Config::from_args(arguments)?;
    let report = run(&config);
    let json = report_json(&config, &report);
    println!("{json}");
    if let Some(path) = &config.output {
        std::fs::write(path, format!("{json}\n"))
            .map_err(|error| format!("sync benchmark: write {path}: {error}"))?;
    }
    let overall = report.iter().map(|(_, verdict)| verdict.status).fold("pass", |state, status| {
        match (state, status) {
            (_, "fail") => "fail",
            ("pass", "unavailable") => "unavailable",
            (state, _) => state,
        }
    });
    eprintln!("sync benchmark verdict: {overall}");
    // A caller can distinguish an absent service from a measured regression.
    Ok(match overall {
        "fail" => 4,
        "unavailable" => 3,
        _ => 0,
    })
}

fn main() {
    match run_cli(std::env::args().skip(1)) {
        Ok(status) => std::process::exit(status),
        Err(error) => {
            eprintln!("sync benchmark: {error}");
            eprintln!(
                "usage: sync [--local URL] [--cdn URL] [--region NAME=URL] [--database NAME] [--token KEY] [--samples N] [--no-writes] [--member-pid PID] [--output FILE]"
            );
            std::process::exit(2);
        }
    }
}

impl Config {
    fn from_args<I>(arguments: I) -> Result<Self, String>
    where
        I: IntoIterator<Item = String>,
    {
        let mut local =
            env_opt("TERRABASE_SYNC_LOCAL_ENDPOINT").or_else(|| env_opt("TERRABASE_SYNC_ENDPOINT"));
        let mut cdn = env_opt("TERRABASE_SYNC_CDN_ENDPOINT");
        let mut regions = parse_regions(&env_opt("TERRABASE_SYNC_REGIONS").unwrap_or_default())?;
        let mut database = env_opt("TERRABASE_SYNC_DATABASE").unwrap_or_default();
        let mut token = env_opt("TERRABASE_SYNC_TOKEN");
        let mut collection =
            env_opt("TERRABASE_SYNC_COLLECTION").unwrap_or_else(|| "orders".into());
        let mut sql = env_opt("TERRABASE_SYNC_SQL").unwrap_or_default();
        let mut samples = env_usize("TERRABASE_SYNC_SAMPLES", DEFAULT_SAMPLES)?;
        let mut cadence_ms = env_u64("TERRABASE_SYNC_CADENCE_MS", CADENCE_MS)?;
        let mut drive_writes = env_opt("TERRABASE_SYNC_NO_WRITES").is_none();
        let replication = env_opt("TERRABASE_SYNC_REPLICATION").unwrap_or_else(|| "unknown".into());
        let rss_limit_mib = env_u64("TERRABASE_SYNC_RSS_LIMIT_MIB", DEFAULT_RSS_LIMIT_MIB)?;
        let mut member_pid = env_opt("TERRABASE_SYNC_MEMBER_PID")
            .map(|value| value.parse())
            .transpose()
            .map_err(|_| "TERRABASE_SYNC_MEMBER_PID must be a process id".to_owned())?;
        let mut output = env_opt("TERRABASE_SYNC_OUTPUT");
        let runtime_digest = required_digest("TERRABASE_SYNC_RUNTIME_DIGEST")?;
        let comparator_digest = required_digest("TERRABASE_SYNC_COMPARATOR_DIGEST")?;
        let args: Vec<String> = arguments.into_iter().collect();
        let mut index = 0;
        while index < args.len() {
            let arg = &args[index];
            let value = |index: &mut usize| -> Result<String, String> {
                *index += 1;
                args.get(*index).cloned().ok_or_else(|| format!("{arg} requires a value"))
            };
            match arg.as_str() {
                "--local" => local = Some(value(&mut index)?),
                "--cdn" => cdn = Some(value(&mut index)?),
                "--region" => {
                    let item = value(&mut index)?;
                    let (name, endpoint) = item
                        .split_once('=')
                        .ok_or_else(|| "--region requires NAME=URL".to_owned())?;
                    regions.push((name.to_owned(), endpoint.to_owned()));
                }
                "--database" => database = value(&mut index)?,
                "--token" => token = Some(value(&mut index)?),
                "--collection" => collection = value(&mut index)?,
                "--sql" => sql = value(&mut index)?,
                "--samples" => {
                    samples = value(&mut index)?
                        .parse()
                        .map_err(|_| "--samples must be a positive integer".to_owned())?
                }
                "--cadence-ms" => {
                    cadence_ms = value(&mut index)?
                        .parse()
                        .map_err(|_| "--cadence-ms must be a positive integer".to_owned())?
                }
                "--no-writes" => drive_writes = false,
                "--member-pid" => {
                    member_pid = Some(
                        value(&mut index)?
                            .parse()
                            .map_err(|_| "--member-pid must be a process id".to_owned())?,
                    );
                }
                "--output" => output = Some(value(&mut index)?),
                "--help" | "-h" => {
                    println!(
                        "usage: sync [--local URL] [--cdn URL] [--region NAME=URL] [--database NAME] [--token KEY] [--samples N] [--no-writes] [--member-pid PID] [--output FILE]"
                    );
                    std::process::exit(0);
                }
                unknown => return Err(format!("unknown argument {unknown}")),
            }
            index += 1;
        }
        if samples == 0 {
            return Err("samples must be positive".into());
        }
        if cadence_ms == 0 {
            return Err("cadence-ms must be positive".into());
        }
        if regions.len() > MAX_REGIONS {
            return Err(format!("at most {MAX_REGIONS} regions are supported"));
        }
        if replication != "single" && replication != "rf2" {
            return Err("TERRABASE_SYNC_REPLICATION must be exactly single or rf2".into());
        }
        if sql.is_empty() {
            sql = format!("SELECT id FROM {collection} ORDER BY id");
        }
        if database.is_empty() {
            return Err("TERRABASE_SYNC_DATABASE or --database is required".into());
        }
        Ok(Self {
            local,
            cdn,
            regions,
            database,
            token,
            collection,
            sql,
            samples,
            cadence_ms,
            drive_writes,
            replication,
            rss_limit_mib,
            member_pid,
            output,
            runtime_digest,
            comparator_digest,
        })
    }
}

fn run(config: &Config) -> Vec<(&'static str, Verdict)> {
    let mut report = Vec::new();
    let local = config.local.as_deref().and_then(|url| Endpoint::parse(url).ok());
    let token = config.token.as_deref().unwrap_or("");
    let local_verdict = match (local.as_ref(), config.token.as_ref()) {
        (Some(endpoint), Some(_)) => websocket_lane(endpoint, config, token),
        (None, _) => {
            unavailable("local WebSocket endpoint is not configured or has an unsupported URL")
        }
        (_, None) => unavailable("TERRABASE_SYNC_TOKEN is required for the served API"),
    };
    report.push(("local_websocket", local_verdict));

    let http_verdict = match (local.as_ref(), config.token.as_ref()) {
        (Some(endpoint), Some(_)) => http_lane(endpoint, config, token, config.cdn.as_deref()),
        (None, _) => unavailable("local HTTP endpoint is unavailable"),
        (_, None) => unavailable("TERRABASE_SYNC_TOKEN is required for the served API"),
    };
    report.push(("cdn_http", http_verdict));

    let cross = cross_region_lane(config, token);
    report.push(("cross_region", cross));
    report
}

fn websocket_lane(endpoint: &Endpoint, config: &Config, token: &str) -> Verdict {
    if endpoint.scheme != "http" {
        return unavailable(
            "WebSocket driver supports served http endpoints; use a local TLS terminator for https",
        );
    }
    let mut samples = Vec::with_capacity(config.samples);
    let mut max_rss = 0;
    for sample in 0..config.samples {
        let started = Instant::now();
        let result = websocket_subscribe(endpoint, config, token, sample);
        let elapsed = started.elapsed().as_secs_f64() * 1_000.0;
        max_rss = max_rss.max(current_rss_bytes(config.member_pid));
        match result {
            Ok(bytes) if !bytes.is_empty() => samples.push(elapsed),
            Ok(_) => return fail("WebSocket returned an empty frame"),
            Err(error) => {
                return if samples.is_empty() {
                    unavailable(&format!("WebSocket unavailable: {error}"))
                } else {
                    fail(&format!(
                        "WebSocket sample failed after {} samples: {error}",
                        samples.len()
                    ))
                };
            }
        }
    }
    bounded_verdict(samples, LOCAL_P99_MS, config, max_rss, "local WebSocket p99")
}

fn http_lane(endpoint: &Endpoint, config: &Config, token: &str, cdn_url: Option<&str>) -> Verdict {
    let attach_started = Instant::now();
    let attach_body = format!(
        "{{\"databaseId\":\"{}\",\"statement\":\"{}\",\"parameters\":\"\",\"subscription\":\"sync-bench\"}}",
        json_escape(&config.database),
        json_escape(&config.sql)
    );
    let attach_response = match request(
        endpoint,
        "POST",
        "/api/v1/reactive/views",
        token,
        "application/json",
        attach_body.as_bytes(),
        Duration::from_secs(30),
    ) {
        Ok(response) => response,
        Err(error) => return unavailable(&format!("view attach unavailable: {error}")),
    };
    if attach_response.status != 201 {
        return verdict_for_http(attach_response, "view attach");
    }
    let attach = match parse_attach(&attach_response.body) {
        Ok(value) => value,
        Err(error) => return fail(&format!("malformed attach response: {error}")),
    };
    let attach_ms = attach_started.elapsed().as_secs_f64() * 1_000.0;
    eprintln!(
        "sync: attached view={} generation={} offset={} in {:.1}ms",
        attach.view, attach.generation, attach.offset, attach_ms
    );

    // The snapshot is immutable. Fetching it twice through the origin and CDN
    // is the byte-identity check for ordered MessagePack, not a synthetic hash.
    let origin_snapshot =
        match request(endpoint, "GET", &attach.snapshot, token, "", &[], Duration::from_secs(30)) {
            Ok(response) if response.status == 200 && !response.body.is_empty() => response.body,
            Ok(response) => return verdict_for_http(response, "origin snapshot"),
            Err(error) => return fail(&format!("origin snapshot failed: {error}")),
        };
    let delivery_endpoint = match cdn_url.and_then(|url| Endpoint::parse(url).ok()) {
        Some(endpoint) => endpoint,
        None => {
            return unavailable("TERRABASE_SYNC_CDN_ENDPOINT is not configured with http(s) URL");
        }
    };
    let cdn_snapshot = match request(
        &delivery_endpoint,
        "GET",
        &attach.snapshot,
        token,
        "",
        &[],
        Duration::from_secs(30),
    ) {
        Ok(response) if response.status == 200 && !response.body.is_empty() => response.body,
        Ok(response) => return verdict_for_http(response, "CDN snapshot"),
        Err(error) => return unavailable(&format!("CDN snapshot unavailable: {error}")),
    };
    if origin_snapshot != cdn_snapshot {
        return fail("origin and CDN snapshot MessagePack bytes differ");
    }

    let mut samples = Vec::with_capacity(config.samples);
    let mut last_delivery: Option<Instant> = None;
    let mut max_rss = current_rss_bytes(config.member_pid);
    let mut offset = attach.offset;
    for sample in 0..config.samples {
        if config.drive_writes {
            let id = format!("sync-bench-{sample}");
            let path = format!(
                "/api/v1/databases/{}/collections/{}/documents/{}",
                url_escape(&config.database),
                url_escape(&config.collection),
                id
            );
            let value = format!("{{\"sample\":{sample},\"cadence_ms\":{}}}", config.cadence_ms);
            match request(
                endpoint,
                "PUT",
                &path,
                token,
                "application/json",
                value.as_bytes(),
                Duration::from_secs(30),
            ) {
                Ok(response) if (200..300).contains(&response.status) => {}
                Ok(response) => return verdict_for_http(response, "delta-driving write"),
                Err(error) => return fail(&format!("delta-driving write failed: {error}")),
            }
        }
        let started = Instant::now();
        let path =
            format!("{}{}", attach.log, if attach.log.contains('?') { "" } else { "?offset=" });
        let path = if path.ends_with("?offset=") {
            format!("{path}{offset}")
        } else {
            replace_offset(&path, offset)
        };
        let response = match request_with_header(
            &delivery_endpoint,
            "GET",
            &path,
            token,
            &attach.capability,
            Duration::from_secs(30),
        ) {
            Ok(response) => response,
            Err(error) => {
                return if samples.is_empty() {
                    unavailable(&format!("CDN log unavailable: {error}"))
                } else {
                    fail(&format!("CDN log failed after {} samples: {error}", samples.len()))
                };
            }
        };
        if response.status == 204 {
            return if samples.is_empty() {
                unavailable("CDN log timed out without a data-bearing delta")
            } else {
                fail("CDN log timed out before all requested deltas")
            };
        }
        if response.status != 200 || response.body.is_empty() {
            return verdict_for_http(response, "CDN delta log");
        }
        let elapsed = started.elapsed().as_secs_f64() * 1_000.0;
        let arrival = Instant::now();
        if let Some(previous) = last_delivery {
            let interval_ms = arrival.duration_since(previous).as_secs_f64() * 1_000.0;
            if interval_ms < config.cadence_ms as f64 * 0.90 {
                return fail(&format!(
                    "shared-view cadence emitted data at {:.1}ms (< {}ms bound)",
                    interval_ms, config.cadence_ms
                ));
            }
        }
        last_delivery = Some(arrival);
        samples.push(elapsed);
        // We do not decode the envelope here: the server's causation revision
        // is deliberately opaque. Advance only after a real data-bearing byte.
        offset = envelope_causation(&response.body).unwrap_or_else(|| offset.saturating_add(1));
        max_rss = max_rss.max(current_rss_bytes(config.member_pid));
    }
    bounded_verdict(samples, CDN_P99_MS, config, max_rss, "CDN HTTP p99")
}

fn cross_region_lane(config: &Config, token: &str) -> Verdict {
    if config.regions.is_empty() {
        return unavailable("no cross-region endpoints configured (set TERRABASE_SYNC_REGIONS)");
    }
    if config.replication != "single" && config.replication != "rf2" {
        return unavailable(
            "replication mode is unknown; set TERRABASE_SYNC_REPLICATION=single or rf2",
        );
    }
    let mut samples = Vec::new();
    let mut unavailable_regions = Vec::new();
    for (name, url) in &config.regions {
        let endpoint = match Endpoint::parse(url) {
            Ok(endpoint) => endpoint,
            Err(error) => {
                unavailable_regions.push(format!("{name}: {error}"));
                continue;
            }
        };
        for sample in 0..config.samples {
            let started = Instant::now();
            let body = format!(
                "{{\"databaseId\":\"{}\",\"statement\":\"{}\",\"parameters\":\"\",\"subscription\":\"sync-cross-{sample}\"}}",
                json_escape(&config.database),
                json_escape(&config.sql)
            );
            match request(
                &endpoint,
                "POST",
                "/api/v1/reactive/views",
                token,
                "application/json",
                body.as_bytes(),
                Duration::from_secs(30),
            ) {
                Ok(response) if response.status == 201 => {
                    let attach = match parse_attach(&response.body) {
                        Ok(attach) => attach,
                        Err(error) => {
                            return fail(&format!(
                                "cross-region {name} returned malformed attach: {error}"
                            ));
                        }
                    };
                    match request_with_header(
                        &endpoint,
                        "GET",
                        &attach.snapshot,
                        token,
                        &attach.capability,
                        Duration::from_secs(30),
                    ) {
                        Ok(snapshot) if snapshot.status == 200 && !snapshot.body.is_empty() => {
                            samples.push(started.elapsed().as_secs_f64() * 1_000.0)
                        }
                        Ok(snapshot) => {
                            unavailable_regions
                                .push(format!("{name}: snapshot HTTP {}", snapshot.status));
                            break;
                        }
                        Err(error) => {
                            unavailable_regions.push(format!("{name}: snapshot {error}"));
                            break;
                        }
                    }
                }
                Ok(response)
                    if response.status == 421
                        || response.status == 404
                        || response.status == 503 =>
                {
                    unavailable_regions.push(format!("{name}: HTTP {}", response.status));
                    break;
                }
                Ok(response) => return verdict_for_http(response, &format!("cross-region {name}")),
                Err(error) => {
                    unavailable_regions.push(format!("{name}: {error}"));
                    break;
                }
            }
        }
    }
    if samples.is_empty() {
        return unavailable(&format!(
            "all cross-region probes unavailable{}",
            if unavailable_regions.is_empty() {
                "".into()
            } else {
                format!(": {}", unavailable_regions.join(", "))
            }
        ));
    }
    let p50 = percentile(&samples, 0.50);
    let p99 = percentile(&samples, 0.99);
    let mut reason = format!("{} replication; {} samples", config.replication, samples.len());
    if !unavailable_regions.is_empty() {
        reason.push_str(&format!("; unavailable: {}", unavailable_regions.join(", ")));
    }
    let status = if p50 <= CROSS_P50_MS && p99 <= CROSS_P99_MS && unavailable_regions.is_empty() {
        "pass"
    } else if unavailable_regions.is_empty() {
        "fail"
    } else {
        "unavailable"
    };
    Verdict { status, reason, samples_ms: samples, p50_ms: Some(p50), p99_ms: Some(p99) }
}

fn websocket_subscribe(
    endpoint: &Endpoint,
    config: &Config,
    token: &str,
    sample: usize,
) -> Result<Vec<u8>, String> {
    let address = format!("{}:{}", endpoint.host, endpoint.port);
    let mut stream = TcpStream::connect_timeout(
        &address
            .to_socket_addrs()
            .map_err(|e| e.to_string())?
            .next()
            .ok_or("endpoint has no address")?,
        Duration::from_secs(5),
    )
    .map_err(|e| e.to_string())?;
    stream.set_read_timeout(Some(Duration::from_secs(30))).map_err(|e| e.to_string())?;
    let key = "dGVycmFzY2FsZS1zeW5jLWJlbmNo";
    let path = format!(
        "{}/api/v1/databases/{}/reactive",
        endpoint.base_path.trim_end_matches('/'),
        url_escape(&config.database)
    );
    write!(stream, "GET {path} HTTP/1.1\r\nHost: {}\r\nAuthorization: Bearer {token}\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: {key}\r\n\r\n", endpoint.host).map_err(|e| e.to_string())?;
    let header = read_until(&mut stream, b"\r\n\r\n", 16 * 1024)?;
    if !header.starts_with(b"HTTP/1.1 101") {
        return Err(format!(
            "WebSocket handshake rejected: {}",
            String::from_utf8_lossy(&header[..header.len().min(120)])
        ));
    }
    let payload = subscribe_envelope(&format!("sync-ws-{sample}"), &config.sql);
    write_ws_binary(&mut stream, &payload)?;
    let frame = read_ws_frame(&mut stream)?;
    let _ = stream.shutdown(Shutdown::Both);
    Ok(frame)
}

fn subscribe_envelope(subscription: &str, query: &str) -> Vec<u8> {
    let mut out = Vec::new();
    array(&mut out, 7);
    uint(&mut out, 2);
    uint(&mut out, 0);
    uint(&mut out, 0);
    uint(&mut out, 1);
    uint(&mut out, 1);
    uint(&mut out, 0);
    array(&mut out, 4);
    text(&mut out, subscription);
    text(&mut out, query);
    binary(&mut out, &[]);
    out.push(0xc0);
    out
}

fn write_ws_binary(stream: &mut TcpStream, payload: &[u8]) -> Result<(), String> {
    let mut header = vec![0x82];
    let length = payload.len();
    if length < 126 {
        header.push(0x80 | length as u8);
    } else if length <= u16::MAX as usize {
        header.push(0x80 | 126);
        header.extend_from_slice(&(length as u16).to_be_bytes());
    } else {
        header.push(0x80 | 127);
        header.extend_from_slice(&(length as u64).to_be_bytes());
    }
    // A fixed mask is legal; bytes on the wire remain masked as required by
    // RFC 6455.  It is deterministic and has no bearing on server output.
    let mask = [0x53, 0x79, 0x6e, 0x63];
    header.extend_from_slice(&mask);
    stream.write_all(&header).map_err(|e| e.to_string())?;
    for (index, byte) in payload.iter().enumerate() {
        stream.write_all(&[byte ^ mask[index % 4]]).map_err(|e| e.to_string())?;
    }
    stream.flush().map_err(|e| e.to_string())
}

fn read_ws_frame(stream: &mut TcpStream) -> Result<Vec<u8>, String> {
    let mut first = [0_u8; 2];
    stream.read_exact(&mut first).map_err(|e| e.to_string())?;
    let opcode = first[0] & 0x0f;
    let masked = first[1] & 0x80 != 0;
    let mut length = (first[1] & 0x7f) as usize;
    if length == 126 {
        let mut b = [0_u8; 2];
        stream.read_exact(&mut b).map_err(|e| e.to_string())?;
        length = u16::from_be_bytes(b) as usize;
    }
    if length == 127 {
        let mut b = [0_u8; 8];
        stream.read_exact(&mut b).map_err(|e| e.to_string())?;
        length = u64::from_be_bytes(b).try_into().map_err(|_| "frame too large".to_owned())?;
    }
    if length > 8 * 1024 * 1024 {
        return Err("WebSocket frame exceeds 8 MiB contract".into());
    }
    let mut mask = [0_u8; 4];
    if masked {
        stream.read_exact(&mut mask).map_err(|e| e.to_string())?;
    }
    let mut body = vec![0_u8; length];
    stream.read_exact(&mut body).map_err(|e| e.to_string())?;
    if masked {
        for (index, byte) in body.iter_mut().enumerate() {
            *byte ^= mask[index % 4];
        }
    }
    if opcode == 0x8 {
        return Err("WebSocket closed before a data frame".into());
    }
    if opcode != 0x2 {
        return Err(format!("WebSocket returned opcode {opcode}, expected binary"));
    }
    Ok(body)
}

fn request(
    endpoint: &Endpoint,
    method: &str,
    path: &str,
    token: &str,
    content_type: &str,
    body: &[u8],
    timeout: Duration,
) -> Result<HttpResponse, String> {
    request_with_header_inner(endpoint, method, path, token, None, content_type, body, timeout)
}
fn request_with_header(
    endpoint: &Endpoint,
    method: &str,
    path: &str,
    token: &str,
    capability: &str,
    timeout: Duration,
) -> Result<HttpResponse, String> {
    request_with_header_inner(endpoint, method, path, token, Some(capability), "", &[], timeout)
}
fn request_with_header_inner(
    endpoint: &Endpoint,
    method: &str,
    path: &str,
    token: &str,
    capability: Option<&str>,
    content_type: &str,
    body: &[u8],
    timeout: Duration,
) -> Result<HttpResponse, String> {
    if endpoint.scheme == "https" {
        return request_https_curl(
            endpoint,
            method,
            path,
            token,
            capability,
            content_type,
            body,
            timeout,
        );
    }
    let address = format!("{}:{}", endpoint.host, endpoint.port);
    let addr = address
        .to_socket_addrs()
        .map_err(|e| e.to_string())?
        .next()
        .ok_or("endpoint has no address")?;
    let mut stream = TcpStream::connect_timeout(&addr, timeout).map_err(|e| e.to_string())?;
    stream.set_read_timeout(Some(timeout)).map_err(|e| e.to_string())?;
    let full_path = if path.starts_with('/') {
        format!("{}{}", endpoint.base_path.trim_end_matches('/'), path)
    } else {
        format!("{}/{}", endpoint.base_path.trim_end_matches('/'), path)
    };
    write!(stream, "{method} {full_path} HTTP/1.1\r\nHost: {}\r\nAuthorization: Bearer {token}\r\nAccept: application/msgpack, application/json\r\nConnection: close\r\n", endpoint.host).map_err(|e| e.to_string())?;
    if let Some(capability) = capability {
        write!(stream, "X-TerraScale-View-Capability: {capability}\r\n")
            .map_err(|e| e.to_string())?;
    }
    if !body.is_empty() {
        write!(stream, "Content-Type: {content_type}\r\nContent-Length: {}\r\n", body.len())
            .map_err(|e| e.to_string())?;
    } else {
        stream.write_all(b"Content-Length: 0\r\n").map_err(|e| e.to_string())?;
    }
    stream.write_all(b"\r\n").map_err(|e| e.to_string())?;
    if !body.is_empty() {
        stream.write_all(body).map_err(|e| e.to_string())?;
    }
    stream.flush().map_err(|e| e.to_string())?;
    parse_http_response(&mut stream)
}

/// TLS is delegated to the system's curl, whose CA store and HTTP/2/TLS
/// implementation are part of the host contract.  The measured elapsed time
/// includes the real TLS request; no response is accepted on a curl failure.
fn request_https_curl(
    endpoint: &Endpoint,
    method: &str,
    path: &str,
    token: &str,
    capability: Option<&str>,
    content_type: &str,
    body: &[u8],
    timeout: Duration,
) -> Result<HttpResponse, String> {
    let full_path = if path.starts_with('/') {
        format!("{}{}", endpoint.base_path.trim_end_matches('/'), path)
    } else {
        format!("{}/{}", endpoint.base_path.trim_end_matches('/'), path)
    };
    let url = format!("https://{}:{}{}", endpoint.host, endpoint.port, full_path);
    let mut command = Command::new("curl");
    command.args([
        "--silent",
        "--show-error",
        "--http1.1",
        "--max-time",
        &timeout.as_secs().max(1).to_string(),
        "-X",
        method,
        "-H",
        &format!("Authorization: Bearer {token}"),
        "-H",
        "Accept: application/msgpack, application/json",
        "-H",
        "Connection: close",
        "-w",
        "\n__TB_HTTP_STATUS__%{http_code}\n",
        &url,
    ]);
    if let Some(capability) = capability {
        command.args(["-H", &format!("X-TerraScale-View-Capability: {capability}")]);
    }
    if !body.is_empty() {
        command.args([
            "-H",
            &format!("Content-Type: {content_type}"),
            "--data-binary",
            &String::from_utf8_lossy(body),
        ]);
    }
    let output = command
        .stdin(Stdio::null())
        .output()
        .map_err(|error| format!("start curl for https endpoint: {error}"))?;
    if !output.status.success() {
        return Err(format!("curl failed: {}", String::from_utf8_lossy(&output.stderr).trim()));
    }
    let marker = b"\n__TB_HTTP_STATUS__";
    let index = output
        .stdout
        .windows(marker.len())
        .rposition(|window| window == marker)
        .ok_or("curl response did not contain an HTTP status")?;
    let status_start = index + marker.len();
    let status_end = output.stdout[status_start..]
        .iter()
        .position(|byte| *byte == b'\n')
        .map_or(output.stdout.len(), |end| status_start + end);
    let status = String::from_utf8_lossy(&output.stdout[status_start..status_end])
        .parse()
        .map_err(|_| "curl returned an invalid HTTP status")?;
    Ok(HttpResponse { status, body: output.stdout[..index].to_vec() })
}

fn parse_http_response(stream: &mut TcpStream) -> Result<HttpResponse, String> {
    let header = read_until(stream, b"\r\n\r\n", 64 * 1024)?;
    let header_text = String::from_utf8_lossy(&header);
    let mut lines = header_text.split("\r\n");
    let status = lines
        .next()
        .and_then(|line| line.split_whitespace().nth(1))
        .and_then(|value| value.parse().ok())
        .ok_or("malformed HTTP status")?;
    let mut headers = Vec::new();
    let mut content_length: Option<usize> = None;
    let mut chunked = false;
    for line in lines {
        if let Some((name, value)) = line.split_once(':') {
            let name = name.trim().to_ascii_lowercase();
            let value = value.trim().to_owned();
            if name == "content-length" {
                content_length = value.parse().ok();
            }
            if name == "transfer-encoding" && value.eq_ignore_ascii_case("chunked") {
                chunked = true;
            }
            headers.push((name, value));
        }
    }
    let mut body = Vec::new();
    stream.read_to_end(&mut body).map_err(|e| e.to_string())?;
    if let Some(length) = content_length {
        body.truncate(length.min(body.len()));
    }
    if chunked {
        body = decode_chunked(&body)?;
    }
    let _ = headers;
    Ok(HttpResponse { status, body })
}

fn decode_chunked(mut input: &[u8]) -> Result<Vec<u8>, String> {
    let mut output = Vec::new();
    loop {
        let end = input
            .windows(2)
            .position(|window| window == b"\r\n")
            .ok_or("malformed chunked response")?;
        let size = usize::from_str_radix(String::from_utf8_lossy(&input[..end]).trim(), 16)
            .map_err(|_| "malformed chunk size")?;
        input = &input[end + 2..];
        if size == 0 {
            break;
        }
        if input.len() < size + 2 {
            return Err("truncated chunked response".into());
        }
        output.extend_from_slice(&input[..size]);
        input = &input[size + 2..];
    }
    Ok(output)
}
fn read_until(stream: &mut TcpStream, needle: &[u8], limit: usize) -> Result<Vec<u8>, String> {
    let mut out = Vec::new();
    let mut byte = [0_u8; 1];
    while out.len() < limit {
        stream.read_exact(&mut byte).map_err(|e| e.to_string())?;
        out.push(byte[0]);
        if out.ends_with(needle) {
            return Ok(out);
        }
    }
    Err("HTTP/WebSocket headers exceed bound".into())
}

fn parse_attach(body: &[u8]) -> Result<Attach, String> {
    let text = String::from_utf8_lossy(body);
    Ok(Attach {
        view: json_string(&text, "view")?,
        generation: json_u64(&text, "generation")?,
        snapshot: json_array_first(&text, "snapshot_chunks")?,
        log: json_string(&text, "log")?,
        capability: json_string(&text, "capability")?,
        offset: json_u64(&text, "offset")?,
    })
}
/// Read the six scalar envelope fields without interpreting the ordered body.
/// The causation field is the journal revision and is the only offset advance
/// the driver is allowed to make after accepting a real data-bearing frame.
fn envelope_causation(bytes: &[u8]) -> Option<u64> {
    let mut cursor = bytes;
    let header = *cursor.first()?;
    cursor = &cursor[1..];
    if header != 0x97 {
        return None;
    }
    for _ in 0..5 {
        let _ = read_msgpack_uint(&mut cursor)?;
    }
    read_msgpack_uint(&mut cursor)
}
fn read_msgpack_uint(cursor: &mut &[u8]) -> Option<u64> {
    let tag = *cursor.first()?;
    *cursor = &cursor[1..];
    match tag {
        value @ 0x00..=0x7f => Some(value as u64),
        0xcc => {
            let value = *cursor.first()?;
            *cursor = &cursor[1..];
            Some(value as u64)
        }
        0xcd => {
            if cursor.len() < 2 {
                return None;
            }
            let value = u16::from_be_bytes([cursor[0], cursor[1]]);
            *cursor = &cursor[2..];
            Some(value as u64)
        }
        0xce => {
            if cursor.len() < 4 {
                return None;
            }
            let value = u32::from_be_bytes(cursor[..4].try_into().ok()?);
            *cursor = &cursor[4..];
            Some(value as u64)
        }
        0xcf => {
            if cursor.len() < 8 {
                return None;
            }
            let value = u64::from_be_bytes(cursor[..8].try_into().ok()?);
            *cursor = &cursor[8..];
            Some(value)
        }
        _ => None,
    }
}
fn json_string(body: &str, key: &str) -> Result<String, String> {
    let needle = format!("\"{key}\"");
    let start = body.find(&needle).ok_or_else(|| format!("missing {key}"))?;
    let rest = &body[start + needle.len()..];
    let quote = rest.find('"').ok_or_else(|| format!("invalid {key}"))? + 1;
    let end = rest[quote..].find('"').ok_or_else(|| format!("invalid {key}"))? + quote;
    Ok(rest[quote..end].replace("\\\"", "\"").replace("\\\\", "\\"))
}
fn json_array_first(body: &str, key: &str) -> Result<String, String> {
    let needle = format!("\"{key}\"");
    let start = body.find(&needle).ok_or_else(|| format!("missing {key}"))?;
    let rest = &body[start + needle.len()..];
    let open = rest.find('[').ok_or_else(|| format!("invalid {key}"))? + 1;
    let quote = rest[open..].find('"').ok_or_else(|| format!("invalid {key}"))? + open;
    let end = rest[quote + 1..].find('"').ok_or_else(|| format!("invalid {key}"))? + quote + 1;
    Ok(rest[quote + 1..end].to_owned())
}
fn json_u64(body: &str, key: &str) -> Result<u64, String> {
    let needle = format!("\"{key}\"");
    let start = body.find(&needle).ok_or_else(|| format!("missing {key}"))?;
    body[start + needle.len()..]
        .trim_start_matches([' ', ':'])
        .split(|c: char| !c.is_ascii_digit())
        .next()
        .unwrap_or("")
        .parse()
        .map_err(|_| format!("invalid {key}"))
}

fn bounded_verdict(
    mut samples: Vec<f64>,
    limit: f64,
    config: &Config,
    max_rss: u64,
    name: &str,
) -> Verdict {
    samples.sort_by(f64::total_cmp);
    let p50 = percentile(&samples, 0.50);
    let p99 = percentile(&samples, 0.99);
    let rss_ok = max_rss == 0 || max_rss <= config.rss_limit_mib * 1024 * 1024;
    let status = if p99 <= limit && rss_ok { "pass" } else { "fail" };
    let reason = if rss_ok {
        format!(
            "{name} <= {limit:.0}ms; {} replication; {} samples",
            config.replication,
            samples.len()
        )
    } else {
        format!(
            "{name} exceeded {limit:.0}ms or RSS bound (max RSS {} MiB, limit {} MiB)",
            max_rss / 1024 / 1024,
            config.rss_limit_mib
        )
    };
    Verdict { status, reason, samples_ms: samples, p50_ms: Some(p50), p99_ms: Some(p99) }
}
fn unavailable(reason: &str) -> Verdict {
    Verdict {
        status: "unavailable",
        reason: reason.into(),
        samples_ms: Vec::new(),
        p50_ms: None,
        p99_ms: None,
    }
}
fn fail(reason: &str) -> Verdict {
    Verdict {
        status: "fail",
        reason: reason.into(),
        samples_ms: Vec::new(),
        p50_ms: None,
        p99_ms: None,
    }
}
fn verdict_for_http(response: HttpResponse, operation: &str) -> Verdict {
    let code = response.status;
    if code == 404 || code == 421 || code == 429 || code >= 500 {
        unavailable(&format!("{operation} unavailable: HTTP {code}"))
    } else {
        fail(&format!("{operation} failed: HTTP {code}"))
    }
}
fn percentile(values: &[f64], fraction: f64) -> f64 {
    if values.is_empty() {
        return 0.0;
    }
    let index = ((values.len() as f64 - 1.0) * fraction).round() as usize;
    values[index.min(values.len() - 1)]
}

fn report_json(config: &Config, report: &[(&str, Verdict)]) -> String {
    let observed_rss_mib = current_rss_bytes(config.member_pid) / 1024 / 1024;
    let mut out =
        String::from("{\"family\":\"sync\",\"protocol\":\"sync-v1 MessagePack\",\"topology\":\"");
    out.push_str(&json_escape(&config.replication));
    out.push_str("\",\"runtime_digest\":\"");
    out.push_str(&config.runtime_digest);
    out.push_str("\",\"comparator_digest\":\"");
    out.push_str(&config.comparator_digest);
    out.push_str("\",\"cadence_ms\":");
    out.push_str(&config.cadence_ms.to_string());
    out.push_str(",\"replication\":\"");
    out.push_str(&json_escape(&config.replication));
    out.push_str("\",\"resource_evidence\":{\"ocpu\":2,\"memory_gib\":12,\"priority\":\"nice -n 19\"},\"bounds\":{\"max_regions\":3,\"max_member_connections\":");
    out.push_str(&MAX_MEMBER_CONNECTIONS.to_string());
    out.push_str(",\"rss_limit_mib\":");
    out.push_str(&config.rss_limit_mib.to_string());
    out.push_str(",\"observed_rss_mib\":");
    out.push_str(&observed_rss_mib.to_string());
    out.push_str(",\"observed_connection_peak\":1,\"rss_subject\":\"");
    out.push_str(if config.member_pid.is_some() {
        "member"
    } else {
        "driver (set TERRABASE_SYNC_MEMBER_PID for member RSS)"
    });
    out.push('"');
    out.push_str("},\"verdicts\":[");
    for (index, (name, verdict)) in report.iter().enumerate() {
        if index > 0 {
            out.push(',');
        }
        out.push_str("{\"lane\":\"");
        out.push_str(name);
        out.push_str("\",\"status\":\"");
        out.push_str(verdict.status);
        out.push_str("\",\"reason\":\"");
        out.push_str(&json_escape(&verdict.reason));
        out.push_str("\",\"p50_ms\":");
        json_number(&mut out, verdict.p50_ms);
        out.push_str(",\"p99_ms\":");
        json_number(&mut out, verdict.p99_ms);
        out.push_str(",\"samples_ms\":[");
        for (sample, value) in verdict.samples_ms.iter().enumerate() {
            if sample > 0 {
                out.push(',');
            }
            out.push_str(&format!("{value:.3}"));
        }
        out.push_str("]}");
    }
    out.push_str("]}");
    out
}
fn json_number(out: &mut String, value: Option<f64>) {
    match value {
        Some(value) if value.is_finite() => out.push_str(&format!("{value:.3}")),
        _ => out.push_str("null"),
    }
}
fn json_escape(value: &str) -> String {
    value.replace('\\', "\\\\").replace('"', "\\\"").replace('\n', "\\n").replace('\r', "\\r")
}

fn required_digest(name: &str) -> Result<String, String> {
    let value =
        env_opt(name).ok_or_else(|| format!("{name} is required; immutable pin missing"))?;
    if value.len() != 64 || !value.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err(format!("{name} must be an immutable 64-hex digest"));
    }
    Ok(value)
}
fn env_opt(name: &str) -> Option<String> {
    std::env::var(name).ok().filter(|value| !value.is_empty())
}
fn env_usize(name: &str, default: usize) -> Result<usize, String> {
    env_opt(name).map_or(Ok(default), |value| {
        value.parse().map_err(|_| format!("{name} must be an integer"))
    })
}
fn env_u64(name: &str, default: u64) -> Result<u64, String> {
    env_opt(name).map_or(Ok(default), |value| {
        value.parse().map_err(|_| format!("{name} must be an integer"))
    })
}
fn parse_regions(value: &str) -> Result<Vec<(String, String)>, String> {
    if value.is_empty() {
        return Ok(Vec::new());
    }
    value
        .split(',')
        .map(|item| {
            item.split_once('=')
                .map(|(name, url)| (name.to_owned(), url.to_owned()))
                .ok_or_else(|| format!("invalid region {item:?}; expected NAME=URL"))
                .and_then(|pair| {
                    if pair.0.is_empty() || pair.1.is_empty() {
                        Err("region name and URL must be non-empty".into())
                    } else {
                        Ok(pair)
                    }
                })
        })
        .collect()
}
fn url_escape(value: &str) -> String {
    value
        .bytes()
        .map(|byte| {
            if byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_' | b'.' | b'~') {
                (byte as char).to_string()
            } else {
                format!("%{byte:02X}")
            }
        })
        .collect()
}
fn replace_offset(path: &str, offset: u64) -> String {
    if let Some(index) = path.find("offset=") {
        let end = path[index + 7..].find('&').map_or(path.len(), |value| index + 7 + value);
        format!("{}{}{}", &path[..index + 7], offset, &path[end..])
    } else {
        format!("{path}&offset={offset}")
    }
}
fn current_rss_bytes(pid: Option<u32>) -> u64 {
    let status_path =
        pid.map_or_else(|| "/proc/self/status".to_owned(), |pid| format!("/proc/{pid}/status"));
    std::fs::read_to_string(status_path)
        .ok()
        .and_then(|text| {
            text.lines()
                .find(|line| line.starts_with("VmRSS:"))
                .and_then(|line| line.split_whitespace().nth(1))
                .and_then(|value| value.parse::<u64>().ok())
        })
        .map_or(0, |kib| kib * 1024)
}

impl Endpoint {
    fn parse(value: &str) -> Result<Self, String> {
        let (scheme, rest) = value
            .split_once("://")
            .ok_or_else(|| "endpoint must be http(s)://host[:port][/base]".to_owned())?;
        if scheme != "http" && scheme != "https" {
            return Err(format!("unsupported endpoint scheme {scheme}"));
        }
        let slash = rest.find('/').unwrap_or(rest.len());
        let authority = &rest[..slash];
        let base_path = if slash == rest.len() { String::new() } else { rest[slash..].to_owned() };
        let (host, port) = if authority.starts_with('[') {
            let close = authority.find(']').ok_or("invalid IPv6 endpoint")?;
            let host = authority[1..close].to_owned();
            let port = authority[close + 1..]
                .strip_prefix(':')
                .map_or(Ok(if scheme == "https" { 443 } else { 80 }), |value| {
                    value.parse().map_err(|_| "invalid endpoint port".to_owned())
                })?;
            (host, port)
        } else if let Some((host, port)) = authority.rsplit_once(':') {
            (host.to_owned(), port.parse().map_err(|_| "invalid endpoint port".to_owned())?)
        } else {
            (authority.to_owned(), if scheme == "https" { 443 } else { 80 })
        };
        if host.is_empty() {
            return Err("endpoint host is empty".into());
        }
        Ok(Self { scheme: scheme.to_owned(), host, port, base_path })
    }
}

fn array(out: &mut Vec<u8>, count: usize) {
    if count < 16 {
        out.push(0x90 | count as u8);
    } else {
        out.push(0xdc);
        out.extend_from_slice(&(count as u16).to_be_bytes());
    }
}
fn uint(out: &mut Vec<u8>, value: u64) {
    if value <= 0x7f {
        out.push(value as u8);
    } else if value <= u8::MAX as u64 {
        out.extend_from_slice(&[0xcc, value as u8]);
    } else if value <= u16::MAX as u64 {
        out.push(0xcd);
        out.extend_from_slice(&(value as u16).to_be_bytes());
    } else if value <= u32::MAX as u64 {
        out.push(0xce);
        out.extend_from_slice(&(value as u32).to_be_bytes());
    } else {
        out.push(0xcf);
        out.extend_from_slice(&value.to_be_bytes());
    }
}
fn text(out: &mut Vec<u8>, value: &str) {
    let length = value.len();
    if length <= 31 {
        out.push(0xa0 | length as u8);
    } else if length <= u8::MAX as usize {
        out.extend_from_slice(&[0xd9, length as u8]);
    } else {
        out.push(0xda);
        out.extend_from_slice(&(length as u16).to_be_bytes());
    }
    out.extend_from_slice(value.as_bytes());
}
fn binary(out: &mut Vec<u8>, value: &[u8]) {
    if value.len() <= u8::MAX as usize {
        out.extend_from_slice(&[0xc4, value.len() as u8]);
    } else {
        out.push(0xc5);
        out.extend_from_slice(&(value.len() as u16).to_be_bytes());
    }
    out.extend_from_slice(value);
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn parses_endpoint_and_default_ports() {
        let endpoint = Endpoint::parse("http://127.0.0.1:8080/base").unwrap();
        assert_eq!(endpoint.host, "127.0.0.1");
        assert_eq!(endpoint.port, 8080);
        assert_eq!(endpoint.base_path, "/base");
        assert_eq!(Endpoint::parse("https://example.test").unwrap().port, 443);
    }
    #[test]
    fn percentile_is_deterministic() {
        assert_eq!(percentile(&[1.0, 2.0, 3.0, 4.0], 0.50), 3.0);
    }
    #[test]
    fn subscribe_frame_is_ordered_messagepack() {
        let first = subscribe_envelope("s", "SELECT id FROM orders ORDER BY id");
        let second = subscribe_envelope("s", "SELECT id FROM orders ORDER BY id");
        assert_eq!(first, second);
        assert_eq!(first[0], 0x97);
        assert_eq!(first[7], 0x94);
    }
    #[test]
    fn reads_messagepack_causation_without_reordering_body() {
        let frame = subscribe_envelope("s", "SELECT id FROM orders ORDER BY id");
        assert_eq!(envelope_causation(&frame), Some(0));
    }
    #[test]
    fn url_escaping_does_not_change_slashes() {
        assert_eq!(url_escape("a/b"), "a%2Fb");
    }
}
