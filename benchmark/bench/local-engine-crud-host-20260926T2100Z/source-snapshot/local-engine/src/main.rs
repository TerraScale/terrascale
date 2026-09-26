//! One bounded, deterministic embedded-SQL CRUD trace for SQLite and pinned Turso.

use std::array;
use std::fs::{self, File, OpenOptions};
use std::io::{BufWriter, Read, Write};
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

use anyhow::{Context, Result, bail, ensure};
use clap::{Parser, ValueEnum};
use rusqlite::{Connection, OptionalExtension, params};
use serde::Serialize;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use turso_pinned::Builder;

const PINNED_TURSO: &str = "64b8ef5742fc18937f9c89806c81e3f6475dc7a3";
const CACHE_KIB: i64 = 65_536;
const PAGE_SIZE: i64 = 4_096;
const SQLITE_WAL_AUTOCHECKPOINT: i64 = 1_000;
const STEPS: [StepKind; 7] = [
    StepKind::Insert,
    StepKind::ReadInserted,
    StepKind::Update,
    StepKind::ReadUpdated,
    StepKind::Delete,
    StepKind::ReadMissing,
    StepKind::Upsert,
];
const SCHEMA: &str =
    "CREATE TABLE items(id INTEGER PRIMARY KEY, payload BLOB NOT NULL, rev INTEGER NOT NULL)";

#[derive(Clone, Copy, Debug, Serialize, ValueEnum)]
#[serde(rename_all = "kebab-case")]
enum EngineChoice {
    Sqlite,
    Turso,
}

#[derive(Clone, Copy, Debug, Serialize)]
#[serde(rename_all = "kebab-case")]
enum StepKind {
    Insert,
    ReadInserted,
    Update,
    ReadUpdated,
    Delete,
    ReadMissing,
    Upsert,
}

impl StepKind {
    const fn index(self) -> usize {
        self as usize
    }

    const fn sql(self) -> &'static str {
        match self {
            Self::Insert => "INSERT INTO items(id,payload,rev) VALUES (?1,?2,?3)",
            Self::ReadInserted | Self::ReadUpdated | Self::ReadMissing => {
                "SELECT payload,rev FROM items WHERE id=?1"
            }
            Self::Update => "UPDATE items SET payload=?2,rev=?3 WHERE id=?1",
            Self::Delete => "DELETE FROM items WHERE id=?1",
            Self::Upsert => {
                "INSERT INTO items(id,payload,rev) VALUES (?1,?2,?3) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload,rev=excluded.rev"
            }
        }
    }

    const fn is_write(self) -> bool {
        matches!(
            self,
            Self::Insert | Self::Update | Self::Delete | Self::Upsert
        )
    }
}

#[derive(Parser)]
#[command(about = "Pinned Turso vs bundled SQLite raw local-engine CRUD scorecard")]
struct Args {
    #[arg(long, value_enum)]
    engine: EngineChoice,
    #[arg(long)]
    database: PathBuf,
    #[arg(long)]
    output: PathBuf,
    #[arg(long)]
    environment_manifest: PathBuf,
    #[arg(long, default_value_t = 1_000)]
    cycles: u32,
    #[arg(long, default_value_t = 256)]
    body_bytes: usize,
    #[arg(long, default_value_t = 1)]
    seed: u32,
    #[arg(long, default_value_t = 1)]
    trial: u32,
    #[arg(long)]
    benchmark_revision: String,
    #[arg(long)]
    terrabase_revision: String,
    #[arg(long)]
    turso_revision: String,
}

#[derive(Serialize)]
struct Sample {
    operation: u64,
    cycle: u32,
    kind: StepKind,
    elapsed_ns: u64,
}

#[derive(Serialize)]
struct StepSummary {
    kind: StepKind,
    samples: usize,
    min_ns: u64,
    p50_ns: u64,
    p95_ns: u64,
    p99_ns: u64,
    max_ns: u64,
}

#[derive(Serialize)]
struct EngineConfig {
    engine_package_version: &'static str,
    sqlite_compatibility_version: String,
    page_size: i64,
    journal_mode: String,
    synchronous: i64,
    cache_kib: i64,
    wal_autocheckpoint_pages_readback: Option<i64>,
    checkpoint_policy: &'static str,
    schema: &'static str,
    prepared_statements: bool,
    write_transaction: &'static str,
}

#[derive(Serialize)]
#[allow(clippy::struct_field_names)] // The receipt field names state their byte units.
struct StorageFiles {
    database_bytes: u64,
    wal_bytes: u64,
    log_bytes: u64,
    shm_bytes: u64,
}

#[derive(Serialize)]
struct Receipt {
    schema_version: u32,
    workload: &'static str,
    scope: &'static str,
    engine: EngineChoice,
    status: &'static str,
    cycles: u32,
    operations_completed: u64,
    body_bytes: usize,
    seed: u32,
    trial: u32,
    measurement_boundary: &'static str,
    wall_elapsed_ns: u64,
    summaries: Vec<StepSummary>,
    final_rows: u32,
    final_state_sha256: String,
    trace_sha256: String,
    integrity_check: String,
    storage_files_post_reopen: StorageFiles,
    binary_sha256: String,
    driver_source_sha256: String,
    manifest_sha256: String,
    dependency_lock_sha256: String,
    environment_manifest_sha256: String,
    environment_manifest: Value,
    benchmark_revision: String,
    terrabase_revision: String,
    pinned_turso_revision: &'static str,
    turso_tracked_diff_sha256: &'static str,
    turso_tracked_dirty: &'static str,
    turso_tracked_file_inventory: &'static str,
    configuration: EngineConfig,
}

enum OpenEngine {
    Sqlite(Connection),
    Turso {
        _database: turso_pinned::Database,
        connection: turso_pinned::Connection,
    },
}

fn main() -> Result<()> {
    let args = Args::parse();
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()?;
    let result = runtime.block_on(run(&args));
    if args.output.is_dir()
        && let Err(error) = &result
    {
        let marker = args.output.join("failure.json");
        if !marker.exists() {
            let _ = fs::write(
                marker,
                serde_json::to_vec_pretty(&json!({
                    "status": "failed",
                    "engine": args.engine,
                    "error": format!("{error:#}"),
                }))?,
            );
        }
    }
    result
}

#[allow(clippy::too_many_lines)] // The trace and receipt lifecycle is kept in one place.
async fn run(args: &Args) -> Result<()> {
    ensure!(
        (1..=10_000).contains(&args.cycles),
        "cycles must be 1..=10000"
    );
    ensure!(
        (1..=4_096).contains(&args.body_bytes),
        "body-bytes must be 1..=4096"
    );
    ensure!(args.trial != 0, "trial must be nonzero");
    ensure!(
        !args.benchmark_revision.is_empty() && !args.terrabase_revision.is_empty(),
        "source revisions are required"
    );
    ensure!(
        args.turso_revision == PINNED_TURSO && env!("PINNED_TURSO_REVISION") == PINNED_TURSO,
        "Turso gitlink does not match the pinned source"
    );
    ensure!(!args.database.exists(), "database path must be fresh");
    ensure!(!args.output.exists(), "output path must be fresh");
    ensure!(
        args.database.parent().is_some_and(Path::is_dir)
            && args.output.parent().is_some_and(Path::is_dir),
        "database and output parents must already exist"
    );
    let environment_manifest_bytes = fs::read(&args.environment_manifest)?;
    let environment_manifest: Value = serde_json::from_slice(&environment_manifest_bytes)?;
    validate_environment(&environment_manifest, &args.database)?;

    fs::create_dir(&args.output)?;
    let (engine, configuration) = open_engine(args).await?;
    let samples_path = args.output.join("samples.jsonl");
    let mut samples = BufWriter::new(
        OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&samples_path)?,
    );
    let mut latencies: [Vec<u64>; 7] = array::from_fn(|_| Vec::with_capacity(args.cycles as usize));
    let mut expected = Vec::with_capacity(args.cycles as usize);
    let mut trace = Sha256::new();
    trace.update(b"TS-BENCH-LOCAL-CRUD-v1\0");
    trace.update(args.cycles.to_be_bytes());
    trace.update((args.body_bytes as u64).to_be_bytes());
    trace.update(args.seed.to_be_bytes());
    let wall_start = Instant::now();
    for cycle in 0..args.cycles {
        // Odd multiplication permutes the 32-bit key space; bounded cycles stay unique.
        let key = i64::from(cycle.wrapping_mul(0x9e37_79b1).wrapping_add(args.seed));
        let initial = body(args.seed, cycle, 0, args.body_bytes);
        let updated = body(args.seed, cycle, 1, args.body_bytes);
        let final_value = body(args.seed, cycle, 2, args.body_bytes);
        for kind in STEPS {
            let (value, revision): (&[u8], i64) = match kind {
                StepKind::Insert | StepKind::ReadInserted => (initial.as_slice(), 0),
                StepKind::Update | StepKind::ReadUpdated => (updated.as_slice(), 1),
                StepKind::Delete | StepKind::ReadMissing => (&[][..], 0),
                StepKind::Upsert => (final_value.as_slice(), 2),
            };
            trace.update([kind as u8]);
            trace.update(key.to_be_bytes());
            trace.update(revision.to_be_bytes());
            trace.update((value.len() as u64).to_be_bytes());
            trace.update(value);
            let (elapsed_ns, observed) = execute_step(&engine, kind, key, value, revision).await?;
            match kind {
                StepKind::ReadInserted | StepKind::ReadUpdated => {
                    ensure!(
                        observed
                            .as_ref()
                            .is_some_and(|(bytes, rev)| bytes == value && *rev == revision),
                        "point read differs after committed write"
                    );
                }
                StepKind::ReadMissing => {
                    ensure!(observed.is_none(), "deleted row remains visible");
                }
                _ => ensure!(observed.is_none(), "write returned an unexpected row"),
            }
            latencies[kind.index()].push(elapsed_ns);
            let operation = u64::from(cycle) * STEPS.len() as u64 + kind.index() as u64;
            serde_json::to_writer(
                &mut samples,
                &Sample {
                    operation,
                    cycle,
                    kind,
                    elapsed_ns,
                },
            )?;
            samples.write_all(b"\n")?;
        }
        expected.push((key, final_value));
    }
    let wall_elapsed_ns = ns(wall_start.elapsed());
    samples.flush()?;
    drop(samples);
    drop(engine);
    expected.sort_by_key(|(key, _)| *key);
    let (final_state_sha256, integrity_check) = verify_reopen(args, &expected).await?;
    let storage_files_post_reopen = file_sizes(&args.database)?;
    let summaries = STEPS
        .into_iter()
        .map(|kind| summarize(kind, &mut latencies[kind.index()]))
        .collect();
    let receipt = Receipt {
        schema_version: 1,
        workload: "embedded-key-value-crud-v1",
        scope: "single-client-local-rust-sql-api",
        engine: args.engine,
        status: "passed",
        cycles: args.cycles,
        operations_completed: u64::from(args.cycles) * STEPS.len() as u64,
        body_bytes: args.body_bytes,
        seed: args.seed,
        trial: args.trial,
        measurement_boundary: "writes: BEGIN IMMEDIATE + prepared execution + COMMIT; reads: prepared point query, row materialization, statement completion and cleanup; validation and JSON emission outside each latency sample",
        wall_elapsed_ns,
        summaries,
        final_rows: args.cycles,
        final_state_sha256,
        trace_sha256: format!("{:x}", trace.finalize()),
        integrity_check,
        storage_files_post_reopen,
        binary_sha256: hash_file(&std::env::current_exe()?)?,
        driver_source_sha256: hash_bytes(include_bytes!("main.rs")),
        manifest_sha256: hash_bytes(include_bytes!("../Cargo.toml")),
        dependency_lock_sha256: hash_bytes(include_bytes!("../Cargo.lock")),
        environment_manifest_sha256: hash_bytes(&environment_manifest_bytes),
        environment_manifest,
        benchmark_revision: args.benchmark_revision.clone(),
        terrabase_revision: args.terrabase_revision.clone(),
        pinned_turso_revision: PINNED_TURSO,
        turso_tracked_diff_sha256: env!("TURSO_TRACKED_DIFF_SHA256"),
        turso_tracked_dirty: env!("TURSO_TRACKED_DIRTY"),
        turso_tracked_file_inventory: env!("TURSO_TRACKED_FILE_INVENTORY"),
        configuration,
    };
    let receipt_path = args.output.join("receipt.json");
    serde_json::to_writer_pretty(
        OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&receipt_path)?,
        &receipt,
    )?;
    println!("{}", receipt_path.display());
    Ok(())
}

fn validate_environment(manifest: &Value, database: &Path) -> Result<()> {
    match manifest["kind"].as_str() {
        Some("host-process") => {
            let status = fs::read_to_string("/proc/self/status")?;
            let actual_affinity = status
                .lines()
                .find_map(|line| line.strip_prefix("Cpus_allowed_list:"))
                .map(str::trim)
                .context("host CPU affinity is unavailable")?;
            ensure!(
                manifest["cpu_affinity"] == actual_affinity,
                "host manifest CPU affinity differs from the running process"
            );
            let database_parent = database
                .parent()
                .context("database path has no parent")?
                .canonicalize()?;
            let database_parent = database_parent
                .to_str()
                .context("database parent is not UTF-8")?;
            ensure!(
                manifest["database_parent"].as_str() == Some(database_parent),
                "host manifest database parent differs from the database path"
            );
            for field in ["cpu_model", "kernel_release", "filesystem"] {
                ensure!(
                    manifest[field]
                        .as_str()
                        .is_some_and(|value| !value.is_empty()),
                    "host manifest lacks {field}"
                );
            }
            ensure!(
                manifest["mem_total_kib"]
                    .as_u64()
                    .is_some_and(|value| value > 0)
                    && manifest["swap_total_kib"].as_u64().is_some(),
                "host manifest lacks memory or swap observations"
            );
        }
        Some("firecracker-guest") => {
            ensure!(manifest["vcpu_count"] == 4, "expected four guest CPUs");
            ensure!(
                manifest["mem_size_mib"] == 8192,
                "expected 8192 MiB guest RAM"
            );
            ensure!(manifest["swap_disabled"] == true, "guest swap must be off");
        }
        _ => bail!("environment manifest has an unsupported kind"),
    }
    Ok(())
}

async fn open_engine(args: &Args) -> Result<(OpenEngine, EngineConfig)> {
    match args.engine {
        EngineChoice::Sqlite => {
            let conn = Connection::open(&args.database)?;
            conn.set_prepared_statement_cache_capacity(16);
            conn.execute_batch(
                "PRAGMA page_size=4096; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA cache_size=-65536; PRAGMA wal_autocheckpoint=1000;",
            )?;
            let config = EngineConfig {
                engine_package_version: "rusqlite 0.40.2 + bundled SQLite",
                sqlite_compatibility_version: conn.query_row(
                    "SELECT sqlite_version()",
                    [],
                    |row| row.get(0),
                )?,
                page_size: conn.query_row("PRAGMA page_size", [], |row| row.get(0))?,
                journal_mode: conn.query_row("PRAGMA journal_mode", [], |row| row.get(0))?,
                synchronous: conn.query_row("PRAGMA synchronous", [], |row| row.get(0))?,
                cache_kib: conn.query_row("PRAGMA cache_size", [], |row| row.get(0))?,
                wal_autocheckpoint_pages_readback: Some(conn.query_row(
                    "PRAGMA wal_autocheckpoint",
                    [],
                    |row| row.get(0),
                )?),
                checkpoint_policy: "SQLite WAL autocheckpoint at configured 1000 pages; runtime readback",
                schema: SCHEMA,
                prepared_statements: true,
                write_transaction: "BEGIN IMMEDIATE / COMMIT",
            };
            validate_config(&config)?;
            conn.execute_batch(SCHEMA)?;
            Ok((OpenEngine::Sqlite(conn), config))
        }
        EngineChoice::Turso => {
            let path = args
                .database
                .to_str()
                .context("Turso database path is not UTF-8")?;
            let database = Builder::new_local(path).build().await?;
            let connection = database.connect()?;
            connection.pragma_update("page_size", PAGE_SIZE).await?;
            connection.pragma_update("journal_mode", "'wal'").await?;
            connection.pragma_update("synchronous", "'full'").await?;
            connection.pragma_update("cache_size", -CACHE_KIB).await?;
            let config = EngineConfig {
                engine_package_version: "turso 0.8.0-pre.13",
                sqlite_compatibility_version: turso_text(&connection, "SELECT sqlite_version()")
                    .await?,
                page_size: turso_int(&connection, "PRAGMA page_size").await?,
                journal_mode: turso_text(&connection, "PRAGMA journal_mode").await?,
                synchronous: turso_int(&connection, "PRAGMA synchronous").await?,
                cache_kib: turso_int(&connection, "PRAGMA cache_size").await?,
                wal_autocheckpoint_pages_readback: None,
                checkpoint_policy: "pinned Turso WAL source defaults to 1000 unbackfilled frames; threshold is not exposed by wal_autocheckpoint PRAGMA",
                schema: SCHEMA,
                prepared_statements: true,
                write_transaction: "BEGIN IMMEDIATE / COMMIT",
            };
            validate_config(&config)?;
            connection.execute(SCHEMA, ()).await?;
            Ok((
                OpenEngine::Turso {
                    _database: database,
                    connection,
                },
                config,
            ))
        }
    }
}

fn validate_config(config: &EngineConfig) -> Result<()> {
    ensure!(
        config.page_size == PAGE_SIZE
            && config.journal_mode.eq_ignore_ascii_case("wal")
            && config.synchronous == 2
            && config.cache_kib == -CACHE_KIB,
        "engine durability or cache PRAGMAs differ from the registered workload"
    );
    if let Some(pages) = config.wal_autocheckpoint_pages_readback {
        ensure!(
            pages == SQLITE_WAL_AUTOCHECKPOINT,
            "SQLite WAL autocheckpoint differs from the registered workload"
        );
    }
    Ok(())
}

async fn turso_int(connection: &turso_pinned::Connection, sql: &str) -> Result<i64> {
    let mut rows = connection.query(sql, ()).await?;
    let row = rows.next().await?.context("Turso integer query is empty")?;
    Ok(row.get::<i64>(0)?)
}

async fn turso_text(connection: &turso_pinned::Connection, sql: &str) -> Result<String> {
    let mut rows = connection.query(sql, ()).await?;
    let row = rows.next().await?.context("Turso text query is empty")?;
    match row.get_value(0)? {
        turso_pinned::Value::Text(text) => Ok(text),
        _ => bail!("Turso text query returned a non-text value"),
    }
}

async fn execute_step(
    engine: &OpenEngine,
    kind: StepKind,
    key: i64,
    payload: &[u8],
    revision: i64,
) -> Result<(u64, Option<(Vec<u8>, i64)>)> {
    match engine {
        OpenEngine::Sqlite(conn) => {
            let mut statement = conn.prepare_cached(kind.sql())?;
            let start = Instant::now();
            if kind.is_write() {
                conn.execute_batch("BEGIN IMMEDIATE")?;
                let changed = match kind {
                    StepKind::Delete => statement.execute([key])?,
                    _ => statement.execute(params![key, payload, revision])?,
                };
                conn.execute_batch("COMMIT")?;
                let elapsed_ns = ns(start.elapsed());
                ensure!(changed == 1, "SQLite write changed {changed} rows");
                Ok((elapsed_ns, None))
            } else {
                let found = statement
                    .query_row([key], |row| {
                        Ok((row.get::<_, Vec<u8>>(0)?, row.get::<_, i64>(1)?))
                    })
                    .optional()?;
                Ok((ns(start.elapsed()), found))
            }
        }
        OpenEngine::Turso { connection, .. } => {
            let mut statement = connection.prepare_cached(kind.sql()).await?;
            let start = Instant::now();
            if kind.is_write() {
                connection.execute_batch("BEGIN IMMEDIATE").await?;
                let changed = match kind {
                    StepKind::Delete => statement.execute([key]).await?,
                    _ => statement.execute((key, payload, revision)).await?,
                };
                connection.execute_batch("COMMIT").await?;
                let elapsed_ns = ns(start.elapsed());
                ensure!(changed == 1, "Turso write changed {changed} rows");
                Ok((elapsed_ns, None))
            } else {
                let mut rows = statement.query([key]).await?;
                let found = rows.next().await?.map(|row| {
                    let turso_pinned::Value::Blob(value) = row.get_value(0)? else {
                        bail!("Turso point read returned a non-BLOB payload");
                    };
                    Ok::<_, anyhow::Error>((value, row.get::<i64>(1)?))
                });
                let found = found.transpose()?;
                let extra = rows.next().await?;
                drop(rows);
                statement.reset()?;
                let elapsed_ns = ns(start.elapsed());
                ensure!(extra.is_none(), "Turso point read returned multiple rows");
                Ok((elapsed_ns, found))
            }
        }
    }
}

async fn verify_reopen(args: &Args, expected: &[(i64, Vec<u8>)]) -> Result<(String, String)> {
    let mut digest = Sha256::new();
    digest.update(b"TS-BENCH-LOCAL-STATE-v1\0");
    match args.engine {
        EngineChoice::Sqlite => {
            let conn = Connection::open(&args.database)?;
            let integrity: String =
                conn.query_row("PRAGMA integrity_check", [], |row| row.get(0))?;
            ensure!(
                integrity == "ok",
                "SQLite integrity check failed: {integrity}"
            );
            let mut stmt = conn.prepare("SELECT id,payload,rev FROM items ORDER BY id")?;
            let mut rows = stmt.query([])?;
            let mut index = 0;
            while let Some(row) = rows.next()? {
                let key = row.get::<_, i64>(0)?;
                let payload = row.get::<_, Vec<u8>>(1)?;
                let revision = row.get::<_, i64>(2)?;
                let (expected_key, expected_payload) = expected
                    .get(index)
                    .context("SQLite has an extra final row")?;
                ensure!(
                    key == *expected_key && payload == *expected_payload && revision == 2,
                    "SQLite final row differs after reopen"
                );
                append_state(&mut digest, key, &payload, revision);
                index += 1;
            }
            ensure!(index == expected.len(), "SQLite final rows are missing");
            Ok((format!("{:x}", digest.finalize()), integrity))
        }
        EngineChoice::Turso => {
            let path = args
                .database
                .to_str()
                .context("Turso database path is not UTF-8")?;
            let database = Builder::new_local(path).build().await?;
            let conn = database.connect()?;
            let integrity = turso_text(&conn, "PRAGMA integrity_check").await?;
            ensure!(
                integrity == "ok",
                "Turso integrity check failed: {integrity}"
            );
            let mut stmt = conn
                .prepare_cached("SELECT id,payload,rev FROM items ORDER BY id")
                .await?;
            let mut rows = stmt.query(()).await?;
            let mut index = 0;
            while let Some(row) = rows.next().await? {
                let key = row.get::<i64>(0)?;
                let turso_pinned::Value::Blob(payload) = row.get_value(1)? else {
                    bail!("Turso final row has a non-BLOB payload");
                };
                let revision = row.get::<i64>(2)?;
                let (expected_key, expected_payload) = expected
                    .get(index)
                    .context("Turso has an extra final row")?;
                ensure!(
                    key == *expected_key && payload == *expected_payload && revision == 2,
                    "Turso final row differs after reopen"
                );
                append_state(&mut digest, key, &payload, revision);
                index += 1;
            }
            ensure!(index == expected.len(), "Turso final rows are missing");
            Ok((format!("{:x}", digest.finalize()), integrity))
        }
    }
}

fn append_state(digest: &mut Sha256, key: i64, payload: &[u8], revision: i64) {
    digest.update(key.to_be_bytes());
    digest.update((payload.len() as u64).to_be_bytes());
    digest.update(payload);
    digest.update(revision.to_be_bytes());
}

fn body(seed: u32, cycle: u32, revision: u64, len: usize) -> Vec<u8> {
    let mut state = u64::from(seed) ^ (u64::from(cycle) << 32) ^ revision;
    let mut bytes = Vec::with_capacity(len);
    while bytes.len() < len {
        state = splitmix64(state);
        let take = (len - bytes.len()).min(8);
        bytes.extend_from_slice(&state.to_be_bytes()[..take]);
    }
    bytes
}

fn splitmix64(mut value: u64) -> u64 {
    value = value.wrapping_add(0x9e37_79b9_7f4a_7c15);
    value = (value ^ (value >> 30)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
    value = (value ^ (value >> 27)).wrapping_mul(0x94d0_49bb_1331_11eb);
    value ^ (value >> 31)
}

fn summarize(kind: StepKind, latencies: &mut [u64]) -> StepSummary {
    latencies.sort_unstable();
    StepSummary {
        kind,
        samples: latencies.len(),
        min_ns: latencies[0],
        p50_ns: percentile(latencies, 50),
        p95_ns: percentile(latencies, 95),
        p99_ns: percentile(latencies, 99),
        max_ns: latencies[latencies.len() - 1],
    }
}

fn percentile(sorted: &[u64], percentile: usize) -> u64 {
    let rank = (sorted.len() * percentile).div_ceil(100);
    sorted[rank.saturating_sub(1)]
}

fn ns(duration: Duration) -> u64 {
    u64::try_from(duration.as_nanos()).unwrap_or(u64::MAX)
}

fn hash_bytes(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

fn hash_file(path: &Path) -> Result<String> {
    let mut file = File::open(path)?;
    let mut digest = Sha256::new();
    let mut buffer = [0_u8; 8192];
    loop {
        let count = file.read(&mut buffer)?;
        if count == 0 {
            break;
        }
        digest.update(&buffer[..count]);
    }
    Ok(format!("{:x}", digest.finalize()))
}

fn file_sizes(path: &Path) -> Result<StorageFiles> {
    fn sidecar(path: &Path, suffix: &str) -> u64 {
        fs::metadata(format!("{}{suffix}", path.display())).map_or(0, |meta| meta.len())
    }
    Ok(StorageFiles {
        database_bytes: fs::metadata(path)?.len(),
        wal_bytes: sidecar(path, "-wal"),
        log_bytes: sidecar(path, "-log"),
        shm_bytes: sidecar(path, "-shm"),
    })
}
