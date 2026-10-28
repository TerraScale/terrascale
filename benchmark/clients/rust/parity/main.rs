//! Rust SDK versus raw HTTPS parity on an owned synthetic database; see README.md.
//! `stage.sh` places this directory at `examples/api_parity/` of a clean ts-rust checkout.
mod support;

use std::fs::{File, OpenOptions};
use std::io::{Read, Write};
use std::os::unix::fs::OpenOptionsExt;
use std::os::unix::process::CommandExt;
use std::process::{Command, ExitCode, Stdio};
use std::sync::Arc;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use reqwest::Version as Http;
use serde::Deserialize;
use serde_json::{json, value::RawValue, Value};
use sha2::{Digest, Sha256};
use support::{Failure, Measured, Op, Seed, Stop};
use terrascale::v1::{Client, DatabasePlacement, DocumentId, DocumentState, WriteOptions};
use tokio::sync::watch;
use tokio::time::{timeout, Instant};

const ORIGIN: &str = "https://api.terrascale.tech";
const SDK_REV: &str = env!("TS_PARITY_SDK_REV");
const SIZES: [usize; 2] = [256, 4096];
const CONCURRENCY: [usize; 2] = [1, 16];
const LANES: [&str; 3] = ["raw-h1", "raw-h2", "sdk"];
const COLLECTION: &str = "bench";
const SETUP: Duration = Duration::from_secs(60);
const CLEANUP: Duration = Duration::from_secs(30);
const RUN: Duration = Duration::from_secs(1800);
const WORKER: &str = "TS_PARITY_WORKER";
const LIMITATIONS: [&str; 5] = [
    "sdk attempts and protocol are null: v0.0.5 has no attempt hook; retries, 401 refresh and ALPN stay internal",
    "sdk uses the shipped ALPN default (h2 preferred, HTTP/1.1 fallback); its protocol is not matched to a raw lane",
    "raw attempts count runner-issued requests; reqwest protocol-NACK and unsent pooled-request resends are unobservable in every lane",
    "tcp_connects is null in every lane: reqwest 0.12.28 exposes no connection counter",
    "database_deletion records logical deletion acceptance; remote erasure is unverified",
];

struct Run {
    out: File,
    output_failed: bool,
    key: String,
    project: String,
    db: String,
    stop: Stop,
}

impl Run {
    fn emit(&mut self, row: Value) {
        // Preserve cleanup after a lost row, but reject the run.
        self.output_failed |= writeln!(self.out, "{row}").is_err();
    }

    /// The shipped SDK with API-key exchange, background refresh and retry policy unchanged.
    /// The pin reaches only API requests; the token exchange goes to the fixed auth origin.
    fn sdk(&self, region: &str) -> terrascale::v1::Result<Client> {
        Client::for_project_api_key(&self.project, self.key.clone())
            .endpoint(ORIGIN) // A region pin requires an explicit static endpoint.
            .region_pin(region)
            .build()
    }

    /// One persistent client per cell; the SDK verifies the pinned response region itself.
    fn lane(&self, lane: &str, region: &str, seed: Arc<Seed>) -> Result<Op, Failure> {
        let built = Failure::Rejected("client could not be built");
        if lane == "sdk" {
            let client = self.sdk(region).map_err(|_| built)?;
            let collection = client.database(&self.db).collection(COLLECTION);
            return Ok(Arc::new(move || {
                let (collection, seed) = (collection.clone(), seed.clone());
                Box::pin(async move {
                    let doc = collection.get::<Value>(seed.id).await.map_err(|_| "sdk")?;
                    match doc.state {
                        DocumentState::Value(body)
                            if doc.id == seed.id
                                && doc.version.as_str() == seed.version
                                && body == seed.body =>
                        {
                            Ok(())
                        }
                        _ => Err("document"),
                    }
                })
            }));
        }
        let protocol = if lane == "raw-h1" {
            Http::HTTP_11
        } else {
            Http::HTTP_2
        };
        let url = format!(
            "{ORIGIN}/v1/projects/{}/databases/{}/collections/{COLLECTION}/documents/by-key?id={}",
            self.project, self.db, seed.id
        );
        support::raw_lane(protocol, &self.key, &self.project, url, region, seed).ok_or(built)
    }

    async fn cell(
        &mut self,
        region: &str,
        repeat: u32,
        lane: &str,
        seed: &Arc<Seed>,
        concurrency: usize,
    ) -> Result<(), Failure> {
        let op = self.lane(lane, region, seed.clone())?;
        let ready = self.stop.guard(support::ready(&op)).await?;
        let m = if ready {
            self.stop.guard(support::measure(op, concurrency)).await?
        } else {
            Measured::default()
        };
        let mut sorted = m.latencies.clone();
        sorted.sort_by(f64::total_cmp);
        let reject = if !ready {
            Some("readiness")
        } else if m.failed > 0 {
            Some("operation_error")
        } else if m.stuck {
            Some("drain_timeout")
        } else if sorted.is_empty() {
            Some("empty")
        } else {
            None
        };
        let raw = lane != "sdk";
        self.emit(json!({
            "type": "cell", "region": region, "repeat": repeat, "lane": lane, "payload_bytes": seed.size,
            "concurrency": concurrency, "ready": ready, "operations_started": m.started, "ok": m.ok,
            "operation_failures": m.failed, "first_error": m.error, "stuck": m.stuck, "sampled": sorted.len(),
            "elapsed_s": ready.then_some(m.elapsed_s), "ops_per_s": ready.then(|| m.ok as f64 / m.elapsed_s),
            "p50_ms": Measured::quantile(&sorted, 0.5), "p95_ms": Measured::quantile(&sorted, 0.95),
            "p99_ms": Measured::quantile(&sorted, 0.99),
            "protocol": raw.then_some(if lane == "raw-h1" { "HTTP/1.1" } else { "HTTP/2" }),
            "attempts": raw.then_some(m.started), "tcp_connects": null, "reject": reject,
        }));
        if self.output_failed {
            return Err(Failure::Rejected("output write failed"));
        }
        reject.map_or(Ok(()), |_| Err(Failure::Rejected("cell rejected")))
    }

    /// Creates the owned all-regions database, its collection and the two ULID seeds.
    async fn setup(
        &mut self,
        admin: &Client,
        home: &str,
        created: &mut Option<String>,
    ) -> terrascale::v1::Result<Vec<Arc<Seed>>> {
        let database = admin.database(&self.db);
        let placement = DatabasePlacement {
            home_region: Some(home.into()),
            all_regions: Some(true),
            ..Default::default()
        };
        let info = database.create_with_placement(&placement).await?;
        *created = Some(info.id.clone());
        self.emit(json!({"type": "database_created", "database": info.name, "id": info.id,
            "home_region": info.home_region, "replica_regions": info.replica_regions, "all_regions": info.all_regions}));
        database.create_collection(COLLECTION).await?;
        let collection = database.collection(COLLECTION);
        let mut seeds = Vec::new();
        for size in SIZES {
            let (id, text) = (DocumentId::generate(), support::seed_body(size));
            let raw = RawValue::from_string(text.clone()).expect("seed body is JSON");
            let written = collection
                .put(id, &*raw, WriteOptions::new().if_none_match_any())
                .await?;
            let body = serde_json::from_str(&text).expect("seed body is JSON");
            seeds.push(Arc::new(Seed {
                size,
                id,
                version: written.version.as_str().to_owned(),
                body,
            }));
        }
        Ok(seeds)
    }

    /// Setup, then every region x three alternating repeats x size x concurrency x lane.
    async fn measured(
        &mut self,
        regions: &[String],
        home: &str,
        admin: &Client,
        created: &mut Option<String>,
    ) -> Result<(), Failure> {
        let stop = self.stop.clone();
        let seeds = match stop
            .guard(timeout(SETUP, self.setup(admin, home, created)))
            .await?
        {
            Ok(Ok(seeds)) => seeds,
            failed => {
                let (status, code) = match &failed {
                    Ok(Err(error)) => (error.status(), error.code().unwrap_or("error")),
                    _ => (None, "timeout"),
                };
                self.emit(json!({"type": "setup_failure", "status": status, "code": code}));
                return Err(Failure::Rejected("setup failed"));
            }
        };
        for region in regions {
            for repeat in 1..=3 {
                for seed in &seeds {
                    for concurrency in CONCURRENCY {
                        let mut lanes = LANES;
                        if repeat % 2 == 0 {
                            lanes.reverse();
                        }
                        for lane in lanes {
                            self.cell(region, repeat, lane, seed, concurrency).await?;
                        }
                    }
                }
            }
        }
        Ok(())
    }

    /// One bounded attempt to delete the exact owned database, by immutable ID once known.
    async fn cleanup(&mut self, admin: &Client, selector: &str) -> bool {
        let deleted = timeout(CLEANUP, admin.database(selector).delete_database()).await;
        let (accepted, outcome, status, code) = match deleted {
            Ok(Ok(())) => (true, "accepted", None, None),
            Ok(Err(error)) if error.status() == Some(404) => (true, "not_found", Some(404), None),
            Ok(Err(error)) => (
                false,
                "error",
                error.status(),
                error.code().map(str::to_owned),
            ),
            Err(_) => (false, "timeout", None, None),
        };
        self.emit(
            json!({"type": "database_deletion", "database": selector, "accepted": accepted,
            "outcome": outcome, "status": status, "code": code, "remote_erasure": "unverified"}),
        );
        accepted
    }
}

fn sha256(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

async fn session(credentials: &str, out_path: &str, stop: Stop) -> Result<(), Failure> {
    #[derive(Deserialize)]
    struct Credentials {
        api_key: String,
        project: String,
    }
    let unreadable = || Failure::Rejected("credentials could not be loaded");
    let text = std::fs::read(credentials).map_err(|_| unreadable())?;
    let Credentials {
        api_key: key,
        project,
    } = serde_json::from_slice(&text).map_err(|_| unreadable())?;
    if key.len() != 79 || !key.is_ascii() {
        return Err(unreadable());
    }
    let out = OpenOptions::new()
        .write(true)
        .create_new(true)
        .mode(0o600)
        .open(out_path)
        .map_err(|_| Failure::Rejected("output must be a new writable file"))?;
    let peers = std::env::var_os("TS_PEERS_FILE")
        .and_then(|path| std::fs::read_to_string(path).ok())
        .ok_or(Failure::Rejected("TS_PEERS_FILE could not be read"))?;
    let regions = support::parse_peers(&peers).map_err(Failure::Rejected)?;
    let home = regions[0].clone();
    let db = format!(
        "api-perf-{}",
        DocumentId::generate().to_string().to_lowercase()
    );
    let mut run = Run {
        out,
        output_failed: false,
        key,
        project,
        db,
        stop,
    };
    let admin = run.sdk(&home).map_err(|_| unreadable())?;
    let started = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |d| d.as_millis() as u64);
    let runner = [
        include_bytes!("main.rs").as_slice(),
        include_bytes!("support.rs"),
        include_bytes!("controls.rs"),
    ]
    .map(sha256);
    // The owned database identity is recorded before any mutation, for reconciliation.
    run.emit(json!({
        "type": "identity", "started_unix_ms": started,
        "host": std::fs::read_to_string("/proc/sys/kernel/hostname").ok().map(|h| h.trim().to_owned()),
        "terrascale": env!("CARGO_PKG_VERSION"), "sdk_rev": SDK_REV,
        "sdk_lock_sha256": sha256(include_bytes!("../../Cargo.lock")),
        "runner_sha256": runner,
        "project": run.project, "database": run.db, "regions": regions, "home": home, "lanes": LANES,
        "warmup_s": support::WARMUP.as_secs(), "window_s": support::MEASURE.as_secs(),
        "drain_s": support::DRAIN.as_secs(), "reservoir": support::RESERVOIR, "limitations": LIMITATIONS,
    }));
    if run.output_failed {
        return Err(Failure::Rejected("database identity could not be recorded"));
    }
    let mut created = None;
    let result = run.measured(&regions, &home, &admin, &mut created).await;
    let selector = created.clone().unwrap_or_else(|| run.db.clone());
    if run.cleanup(&admin, &selector).await {
        return if run.output_failed {
            Err(Failure::Rejected("output write failed"))
        } else {
            result
        };
    }
    // Retained for manual reconciliation: identity only, never credentials.
    let recovery = json!({"project": run.project, "database": run.db, "id": created, "home": home});
    let saved = OpenOptions::new()
        .write(true)
        .create_new(true)
        .mode(0o600)
        .open(format!("{out_path}.recovery.json"))
        .and_then(|mut file| writeln!(file, "{recovery}"));
    run.emit(json!({"type": "recovery", "record": recovery, "saved": saved.is_ok()}));
    Err(Failure::Rejected(
        "cleanup failed; database identity retained",
    ))
}

/// Runs the worker in its own process group with a pipe as stdin. A terminal interrupt or
/// supervisor exit closes the pipe; the worker then stops measuring and still cleans up.
fn supervise(args: &[String]) -> ExitCode {
    let spawned = std::env::current_exe().and_then(|exe| {
        Command::new(exe)
            .args(args)
            .env(WORKER, "1")
            .stdin(Stdio::piped())
            .process_group(0)
            .spawn()
    });
    let Ok(mut child) = spawned else {
        eprintln!("parity failed: worker could not start");
        return ExitCode::FAILURE;
    };
    let _pipe = child.stdin.take(); // Held open until this process exits.
    match child.wait() {
        Ok(status) => ExitCode::from(status.code().map_or(1, |code| code as u8)),
        Err(_) => ExitCode::FAILURE,
    }
}

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let [credentials_flag, credentials, out_flag, out] = args.as_slice() else {
        return usage();
    };
    if credentials_flag != "--credentials" || out_flag != "--out" {
        return usage();
    }
    if std::env::var_os(WORKER).is_none() {
        return supervise(&args);
    }
    let (interrupt, interrupted) = watch::channel(false);
    std::thread::spawn(move || {
        let mut sink = [0u8; 64];
        while matches!(std::io::stdin().read(&mut sink), Ok(n) if n > 0) {}
        let _ = interrupt.send(true);
        loop {
            std::thread::park(); // Keeps the sender alive, so the flag stays readable.
        }
    });
    let Ok(runtime) = tokio::runtime::Runtime::new() else {
        return ExitCode::FAILURE;
    };
    let stop = Stop {
        interrupt: interrupted.clone(),
        deadline: Instant::now() + RUN,
    };
    match runtime.block_on(session(credentials, out, stop)) {
        Ok(()) => ExitCode::SUCCESS,
        Err(Failure::Rejected(reason)) => {
            eprintln!("parity rejected: {reason}");
            ExitCode::FAILURE
        }
        Err(Failure::Interrupted) if *interrupted.borrow() => {
            eprintln!("parity interrupted; cleanup was attempted");
            ExitCode::from(130)
        }
        Err(Failure::Interrupted) => {
            eprintln!("parity rejected: run deadline exceeded; cleanup was attempted");
            ExitCode::FAILURE
        }
    }
}

fn usage() -> ExitCode {
    eprintln!("usage: api_parity --credentials PRIVATE.json --out NEW.jsonl");
    ExitCode::from(2)
}
