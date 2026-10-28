//! Lane-independent parity helpers: peers, seeds, raw validation, token source and window.
use std::collections::{hash_map::RandomState, BTreeSet};
use std::hash::BuildHasher;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use base64::{engine::general_purpose::STANDARD, Engine};
use futures_util::future::BoxFuture;
use reqwest::header::{HeaderValue, ACCEPT, AUTHORIZATION};
use reqwest::{StatusCode, Version as Http};
use serde::Deserialize;
use serde_json::Value;
use terrascale::v1::DocumentId;
use tokio::sync::watch;
use tokio::time::{sleep, timeout, timeout_at, Instant};

pub const TOKEN_URL: &str = "https://auth.terrascale.tech/terrascale/oauth/token";
pub const REQUIRED: [&str; 3] = ["gru", "sjc", "fra"];
pub const RESERVOIR: usize = 16_384;
pub const WARMUP: Duration = Duration::from_secs(2);
pub const MEASURE: Duration = Duration::from_secs(5);
pub const DRAIN: Duration = Duration::from_secs(30);
const READY: Duration = Duration::from_secs(60);
const READY_OP: Duration = Duration::from_secs(10);

/// One logical read; `Err` carries a fixed reason, never server text.
pub type Op = Arc<dyn Fn() -> BoxFuture<'static, Result<(), &'static str>> + Send + Sync>;

pub enum Failure {
    Rejected(&'static str),
    Interrupted,
}

/// Interrupt (supervisor gone) or the whole-run deadline, whichever comes first.
#[derive(Clone)]
pub struct Stop {
    pub interrupt: watch::Receiver<bool>,
    pub deadline: Instant,
}

impl Stop {
    async fn stopped(&self) {
        let mut interrupt = self.interrupt.clone();
        tokio::select! {
            _ = interrupt.wait_for(|stop| *stop) => {}
            _ = tokio::time::sleep_until(self.deadline) => {}
        }
    }

    pub async fn guard<T>(&self, work: impl std::future::Future<Output = T>) -> Result<T, Failure> {
        tokio::select! {
            value = work => Ok(value),
            _ = self.stopped() => Err(Failure::Interrupted),
        }
    }
}

pub struct Seed {
    pub size: usize,
    pub id: DocumentId,
    pub version: String,
    pub body: Value,
}

/// Compact JSON of exactly `size` bytes: `{"payload":""}` is 14.
pub fn seed_body(size: usize) -> String {
    format!(r#"{{"payload":"{}"}}"#, "x".repeat(size - 14))
}

fn check_document(seed: &Seed, body: &[u8]) -> Result<(), &'static str> {
    let doc: Value = serde_json::from_slice(body).map_err(|_| "body")?;
    let same = doc["state"] == "value"
        && doc["id"] == seed.id.to_string()
        && doc["version"] == seed.version
        && doc["document"] == seed.body;
    same.then_some(()).ok_or("document")
}

/// Status 200, the lane's protocol, exactly one matching region header and the seed document.
struct RawAnswer<'a> {
    status: StatusCode,
    protocol: Http,
    regions: Vec<&'a [u8]>,
    body: &'a [u8],
}

fn check_raw(
    answer: &RawAnswer<'_>,
    protocol: Http,
    region: &str,
    seed: &Seed,
) -> Result<(), &'static str> {
    if answer.status != StatusCode::OK {
        return Err("status");
    }
    if answer.protocol != protocol {
        return Err("protocol");
    }
    if answer.regions != [region.as_bytes()] {
        return Err("region");
    }
    check_document(seed, answer.body)
}

/// Every `<node> <region> <netbird-ipv4>` line of the ts-orch peers file; gru/sjc/fra required.
pub fn parse_peers(text: &str) -> Result<Vec<String>, &'static str> {
    let mut regions = BTreeSet::new();
    for line in text
        .lines()
        .map(str::trim)
        .filter(|l| !l.is_empty() && !l.starts_with('#'))
    {
        let parts: Vec<&str> = line.split_whitespace().collect();
        let valid = parts.len() == 3
            && parts[1].len() <= 63
            && parts[1].starts_with(|c: char| c.is_ascii_lowercase())
            && parts[1]
                .bytes()
                .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'-')
            && parts[2].parse::<std::net::Ipv4Addr>().is_ok();
        if !valid {
            return Err("peer catalog is malformed");
        }
        regions.insert(parts[1].to_owned());
    }
    if REQUIRED.iter().any(|r| !regions.contains(*r)) {
        return Err("a required serving region is absent");
    }
    Ok(regions.into_iter().collect())
}

/// The SDK's transport settings: rustls web-PKI TLS 1.3, no proxy, no redirects, 30 s
/// request and 10 s connect timeouts. `None` keeps the SDK's ALPN default (h2, http/1.1).
pub fn http(protocol: Option<Http>) -> reqwest::Result<reqwest::Client> {
    let builder = reqwest::Client::builder()
        .use_rustls_tls()
        .min_tls_version(reqwest::tls::Version::TLS_1_3)
        .redirect(reqwest::redirect::Policy::none())
        .no_proxy()
        .timeout(Duration::from_secs(30))
        .connect_timeout(Duration::from_secs(10))
        .user_agent(concat!("terrascale-parity-raw/", env!("CARGO_PKG_VERSION")));
    match protocol {
        None => builder,
        Some(p) if p == Http::HTTP_11 => builder.http1_only(), // ALPN offers only http/1.1.
        Some(_) => builder.http2_prior_knowledge(),            // ALPN offers only h2.
    }
    .build()
}

/// The raw lanes' cached API-key exchange, shaped as the SDK's: Basic, client_credentials, project.
struct Token {
    http: reqwest::Client,
    basic: HeaderValue,
    project: String,
    cached: tokio::sync::Mutex<Option<(HeaderValue, Instant)>>,
}

impl Token {
    /// The SDK's Basic credential: the key's leading ULID as client id, the whole key as secret.
    fn new(key: &str, project: &str) -> Option<Self> {
        let encoded = STANDARD.encode(format!("{}:{key}", key.get(..26)?));
        let mut basic = HeaderValue::from_str(&format!("Basic {encoded}")).ok()?;
        basic.set_sensitive(true);
        Some(Self {
            http: http(None).ok()?,
            basic,
            project: project.to_owned(),
            cached: Default::default(),
        })
    }

    async fn bearer(&self) -> Result<HeaderValue, &'static str> {
        let mut cached = self.cached.lock().await;
        if let Some((header, _)) = cached.as_ref().filter(|(_, at)| Instant::now() < *at) {
            return Ok(header.clone());
        }
        #[derive(Deserialize)]
        struct Grant {
            access_token: String,
            token_type: String,
            expires_in: u64,
        }
        let started = Instant::now();
        let response = self
            .http
            .post(TOKEN_URL)
            .header(AUTHORIZATION, self.basic.clone())
            .header(ACCEPT, "application/json")
            .form(&[
                ("grant_type", "client_credentials"),
                ("project", self.project.as_str()),
            ])
            .send()
            .await
            .map_err(|_| "token_transport")?;
        if response.status() != StatusCode::OK {
            return Err("token_status");
        }
        let grant: Grant = response.json().await.map_err(|_| "token_body")?;
        let mut header = HeaderValue::from_str(&format!("Bearer {}", grant.access_token))
            .map_err(|_| "token_body")?;
        if !grant.token_type.eq_ignore_ascii_case("Bearer") || grant.expires_in == 0 {
            return Err("token_body");
        }
        header.set_sensitive(true);
        // Refreshed from the later of half-life and 60 s before expiry, as the SDK starts renewal.
        let lifetime = Duration::from_secs(grant.expires_in);
        let refresh_at = started + lifetime - Duration::from_secs(60).min(lifetime / 2);
        *cached = Some((header.clone(), refresh_at));
        Ok(header)
    }
}

/// A raw lane: one persistent client offering only `protocol`, with its own token source.
pub fn raw_lane(
    protocol: Http,
    key: &str,
    project: &str,
    url: String,
    region: &str,
    seed: Arc<Seed>,
) -> Option<Op> {
    let api = http(Some(protocol)).ok()?;
    let token = Arc::new(Token::new(key, project)?);
    let region = region.to_owned();
    Some(Arc::new(move || {
        let (api, token, url, region, seed) = (
            api.clone(),
            token.clone(),
            url.clone(),
            region.clone(),
            seed.clone(),
        );
        Box::pin(async move {
            let response = api
                .get(url)
                .header(AUTHORIZATION, token.bearer().await?)
                .header(ACCEPT, "application/json")
                .header("X-TerraScale-Region-Pin", &region)
                .send()
                .await
                .map_err(|_| "transport")?;
            let (status, version) = (response.status(), response.version());
            let regions: Vec<Vec<u8>> = response
                .headers()
                .get_all("x-terrabase-region")
                .iter()
                .map(|v| v.as_bytes().to_vec())
                .collect();
            let body = response.bytes().await.map_err(|_| "transport")?;
            let regions = regions.iter().map(Vec::as_slice).collect();
            let answer = RawAnswer {
                status,
                protocol: version,
                regions,
                body: &body,
            };
            check_raw(&answer, protocol, &region, &seed)
        })
    }))
}

/// Retries a fresh seed read for up to 60 s; each try is bounded to 10 s.
pub async fn ready(op: &Op) -> bool {
    let deadline = Instant::now() + READY;
    loop {
        if matches!(timeout(READY_OP, op()).await, Ok(Ok(()))) {
            return true;
        }
        if Instant::now() >= deadline {
            return false;
        }
        sleep(Duration::from_millis(100)).await;
    }
}

#[derive(Default)]
pub struct Measured {
    pub latencies: Vec<f64>,
    pub started: u64,
    pub ok: u64,
    pub failed: u64,
    pub error: Option<&'static str>,
    pub stuck: bool,
    pub elapsed_s: f64,
    last: Option<Instant>,
    random: RandomState,
}

impl Measured {
    /// Keeps a uniform reservoir of measured latencies, in milliseconds.
    fn record(&mut self, latency: Duration, done: Instant) {
        self.ok += 1;
        self.last = self.last.max(Some(done));
        let ms = latency.as_secs_f64() * 1000.0;
        if self.latencies.len() < RESERVOIR {
            self.latencies.push(ms);
        } else if let Some(slot) = self
            .latencies
            .get_mut((self.random.hash_one(self.ok) % self.ok) as usize)
        {
            *slot = ms;
        }
    }

    /// The `q` quantile of the sampled latencies (nearest rank), in milliseconds.
    pub fn quantile(sorted: &[f64], q: f64) -> Option<f64> {
        let rank = ((q * sorted.len() as f64).ceil() as usize).max(1);
        sorted.get(rank - 1).map(|v| (v * 1000.0).round() / 1000.0)
    }
}

/// Runs `concurrency` workers on one persistent lane: 2 s warmup, 5 s window, <=30 s drain.
/// Every operation started inside the window keeps its latency, drain included, and the
/// throughput denominator extends through the final measured completion.
pub async fn measure(op: Op, concurrency: usize) -> Measured {
    let from = Instant::now() + WARMUP;
    let until = from + MEASURE;
    let stats = Arc::new(Mutex::new(Measured::default()));
    let mut tasks = tokio::task::JoinSet::new();
    for _ in 0..concurrency {
        let (op, stats) = (op.clone(), stats.clone());
        tasks.spawn(async move {
            while Instant::now() < until && stats.lock().unwrap().failed == 0 {
                let began = Instant::now();
                stats.lock().unwrap().started += 1;
                let result = op().await;
                let done = Instant::now();
                let mut stats = stats.lock().unwrap();
                match result {
                    Err(reason) => {
                        stats.failed += 1;
                        stats.error.get_or_insert(reason);
                        return;
                    }
                    Ok(()) if began >= from => stats.record(done - began, done),
                    Ok(()) => {}
                }
            }
        });
    }
    let drained = timeout_at(until + DRAIN, async {
        while tasks.join_next().await.is_some() {}
    })
    .await;
    tasks.shutdown().await;
    let mut measured = std::mem::take(&mut *stats.lock().unwrap());
    measured.stuck = drained.is_err();
    measured.elapsed_s = (measured.last.unwrap_or(until).max(until) - from).as_secs_f64();
    measured
}

#[cfg(test)]
#[path = "controls.rs"]
mod controls;
