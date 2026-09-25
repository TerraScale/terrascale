//! Matched embedded P04 diagnostic for SQLite, Turso Database, and `TerraBase`.

use std::fs::{self, File, OpenOptions};
use std::io::{BufWriter, Read, Write};
use std::path::{Path, PathBuf};
use std::time::Instant;

use anyhow::{Context, Result, bail, ensure};
use clap::{Parser, ValueEnum};
use rusqlite::{Connection, OptionalExtension, params};
use serde::Serialize;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use tb_core::{BudgetConfig, Key, KeyRange, MemoryBudget, TenantId, TransactionId, WorkerId};
use tb_storage::{Mutation, Storage, StorageConfig};
use turso::Builder;

const ROWS: u64 = 1_024;
const BODY_BYTES: usize = 1;
const SEED: u64 = 1;
const BATCH: u64 = 128;
const CACHE_KIB: i64 = 65_536;

#[derive(Clone, Copy, Debug, Serialize, ValueEnum)]
#[serde(rename_all = "kebab-case")]
enum Engine {
    Sqlite,
    Turso,
    Terrabase,
}

#[derive(Parser)]
struct Args {
    #[arg(long, value_enum)]
    engine: Engine,
    #[arg(long)]
    database: PathBuf,
    #[arg(long)]
    output: PathBuf,
    #[arg(long)]
    guest_manifest: PathBuf,
    #[arg(long)]
    benchmark_revision: String,
    #[arg(long)]
    terrabase_revision: String,
    #[arg(long, default_value_t = 1_000)]
    scans: u64,
}

#[derive(Serialize)]
struct Sample {
    operation: u64,
    elapsed_ns: u64,
    phase: &'static str,
    first_key: Option<u64>,
}

#[derive(Serialize)]
struct Receipt<'a> {
    schema_version: u32,
    workload: &'static str,
    engine: Engine,
    status: &'static str,
    rows: u64,
    body_bytes: usize,
    seed: u64,
    scans: u64,
    completed: u64,
    durability: &'static str,
    configuration: Value,
    binary_sha256: String,
    driver_source_sha256: String,
    dependency_lock_sha256: String,
    guest_manifest_sha256: String,
    guest_manifest: Value,
    benchmark_revision: &'a str,
    terrabase_revision: &'a str,
    fixture_sha256: String,
    final_state_sha256: String,
    database_bytes: u64,
    wal_bytes: u64,
    first_scan_ns: u64,
    warm_p50_ns: u64,
    warm_p95_ns: u64,
    warm_p99_ns: u64,
    elapsed_ns: u64,
}

enum OpenEngine {
    Sqlite(Connection),
    Turso {
        _database: turso::Database,
        connection: turso::Connection,
    },
    Terrabase(Box<Storage>),
}

fn main() -> Result<()> {
    let args = Args::parse();
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()?;
    runtime.block_on(run(&args))
}

async fn run(args: &Args) -> Result<()> {
    ensure!(
        (2..=10_000).contains(&args.scans),
        "scans must be 2..=10000"
    );
    ensure!(!args.database.exists(), "database path must be fresh");
    ensure!(!args.output.exists(), "output path must be fresh");
    ensure!(
        !args.benchmark_revision.is_empty() && !args.terrabase_revision.is_empty(),
        "source revisions are required"
    );
    let manifest_bytes = fs::read(&args.guest_manifest)?;
    let manifest: Value = serde_json::from_slice(&manifest_bytes)?;
    ensure!(manifest["vcpu_count"] == 4, "expected four guest CPUs");
    ensure!(
        manifest["mem_size_mib"] == 8192,
        "expected 8192 MiB guest RAM"
    );
    ensure!(manifest["swap_disabled"] == true, "guest swap must be off");
    fs::create_dir_all(&args.output)?;
    let fixture_sha256 = fixture_hash();
    let (engine, configuration) = open_and_populate(args).await?;
    let samples_path = args.output.join("samples.jsonl");
    let mut samples = BufWriter::new(
        OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(samples_path)?,
    );
    let mut latencies = Vec::with_capacity(usize::try_from(args.scans)?);
    let total_start = Instant::now();
    for operation in 0..args.scans {
        let start = Instant::now();
        let first_key = scan_one(&engine).await?;
        let elapsed_ns = ns(start.elapsed());
        ensure!(first_key.is_none(), "deleted-history scan returned a key");
        latencies.push(elapsed_ns);
        serde_json::to_writer(
            &mut samples,
            &Sample {
                operation,
                elapsed_ns,
                phase: if operation == 0 {
                    "first_after_fixture"
                } else {
                    "warm_repeat"
                },
                first_key,
            },
        )?;
        samples.write_all(b"\n")?;
    }
    let elapsed_ns = ns(total_start.elapsed());
    samples.flush()?;
    ensure!(
        count_rows(&engine).await? == 0,
        "final row count is not zero"
    );
    let first_scan_ns = latencies[0];
    let mut warm = latencies[1..].to_vec();
    warm.sort_unstable();
    let (database_bytes, wal_bytes) = file_sizes(&args.database)?;
    let receipt = Receipt {
        schema_version: 1,
        workload: "p04-deleted-history-1024-tiny-checkpoint",
        engine: args.engine,
        status: "passed",
        rows: ROWS,
        body_bytes: BODY_BYTES,
        seed: SEED,
        scans: args.scans,
        completed: args.scans,
        durability: "local-durable-requested",
        configuration,
        binary_sha256: hash_file(&std::env::current_exe()?)?,
        driver_source_sha256: hash_bytes(include_bytes!("p04_three_way.rs")),
        dependency_lock_sha256: hash_bytes(include_bytes!("../../Cargo.lock")),
        guest_manifest_sha256: hash_bytes(&manifest_bytes),
        guest_manifest: manifest,
        benchmark_revision: &args.benchmark_revision,
        terrabase_revision: &args.terrabase_revision,
        fixture_sha256,
        final_state_sha256: hash_bytes(b""),
        database_bytes,
        wal_bytes,
        first_scan_ns,
        warm_p50_ns: percentile(&warm, 50, 100),
        warm_p95_ns: percentile(&warm, 95, 100),
        warm_p99_ns: percentile(&warm, 99, 100),
        elapsed_ns,
    };
    let receipt_path = args.output.join("receipt.json");
    serde_json::to_writer_pretty(File::create(&receipt_path)?, &receipt)?;
    println!("{}", receipt_path.display());
    Ok(())
}

#[allow(clippy::too_many_lines)] // The three fixture builders are kept together for workload parity.
async fn open_and_populate(args: &Args) -> Result<(OpenEngine, Value)> {
    match args.engine {
        Engine::Sqlite => {
            let mut conn = Connection::open(&args.database)?;
            conn.set_prepared_statement_cache_capacity(32);
            conn.execute_batch("PRAGMA page_size=4096; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA cache_size=-65536; PRAGMA temp_store=FILE; PRAGMA mmap_size=0; PRAGMA wal_autocheckpoint=1000; CREATE TABLE items(id INTEGER PRIMARY KEY,payload BLOB NOT NULL,rev INTEGER NOT NULL DEFAULT 0);")?;
            let mode: String = conn.query_row("PRAGMA journal_mode", [], |row| row.get(0))?;
            let synchronous: i64 = conn.query_row("PRAGMA synchronous", [], |row| row.get(0))?;
            let cache: i64 = conn.query_row("PRAGMA cache_size", [], |row| row.get(0))?;
            ensure!(
                mode.eq_ignore_ascii_case("wal") && synchronous == 2 && cache == -CACHE_KIB,
                "SQLite durability/cache PRAGMAs differ"
            );
            let tx = conn.transaction()?;
            {
                let mut insert =
                    tx.prepare_cached("INSERT INTO items(id,payload) VALUES (?1,?2)")?;
                for id in 0..ROWS {
                    ensure!(
                        insert.execute(params![i64::try_from(id)?, body(id)])? == 1,
                        "SQLite insert did not add one row"
                    );
                }
            }
            tx.commit()?;
            ensure!(
                u64::try_from(
                    conn.execute("DELETE FROM items WHERE id < ?1", [i64::try_from(ROWS)?])?
                )? == ROWS,
                "SQLite delete count differs"
            );
            conn.execute_batch("PRAGMA wal_checkpoint(TRUNCATE)")?;
            Ok((
                OpenEngine::Sqlite(conn),
                json!({"journal_mode": mode, "synchronous": synchronous, "cache_kib": CACHE_KIB, "checkpoint": "TRUNCATE"}),
            ))
        }
        Engine::Turso => {
            let path = args
                .database
                .to_str()
                .context("Turso database path is not UTF-8")?;
            let database = Builder::new_local(path).build().await?;
            let connection = database.connect()?;
            connection.pragma_update("page_size", 4096).await?;
            connection.pragma_update("journal_mode", "'wal'").await?;
            connection.pragma_update("synchronous", "'full'").await?;
            connection.pragma_update("cache_size", -CACHE_KIB).await?;
            let mode = turso_text_pragma(&connection, "journal_mode").await?;
            let synchronous = turso_int_pragma(&connection, "synchronous").await?;
            let cache = turso_int_pragma(&connection, "cache_size").await?;
            ensure!(
                mode.eq_ignore_ascii_case("wal") && synchronous == 2 && cache == -CACHE_KIB,
                "Turso durability/cache PRAGMAs differ"
            );
            connection.execute("CREATE TABLE items(id INTEGER PRIMARY KEY,payload BLOB NOT NULL,rev INTEGER NOT NULL DEFAULT 0)", ()).await?;
            connection.execute_batch("BEGIN IMMEDIATE").await?;
            {
                let mut insert = connection
                    .prepare_cached("INSERT INTO items(id,payload) VALUES (?1,?2)")
                    .await?;
                for id in 0..ROWS {
                    ensure!(
                        insert.execute((i64::try_from(id)?, body(id))).await? == 1,
                        "Turso insert did not add one row"
                    );
                }
            }
            connection.execute_batch("COMMIT").await?;
            ensure!(
                connection
                    .execute("DELETE FROM items WHERE id < ?1", [i64::try_from(ROWS)?])
                    .await?
                    == ROWS,
                "Turso delete count differs"
            );
            let mut checkpoint = connection
                .query("PRAGMA wal_checkpoint(TRUNCATE)", ())
                .await?;
            while checkpoint.next().await?.is_some() {}
            Ok((
                OpenEngine::Turso {
                    _database: database,
                    connection,
                },
                json!({"version": "0.7.2", "journal_mode": mode, "synchronous": synchronous, "cache_kib": CACHE_KIB, "checkpoint": "TRUNCATE"}),
            ))
        }
        Engine::Terrabase => {
            let budget = MemoryBudget::new(BudgetConfig::new(128 * 1024 * 1024, 32 * 1024 * 1024)?);
            let storage = Storage::open(
                StorageConfig::local(&args.database, TenantId::from_bytes([1; 16]), WorkerId(1)),
                budget,
            )?;
            for start in (0..ROWS).step_by(usize::try_from(BATCH)?) {
                let mut mutations = Vec::with_capacity(usize::try_from(BATCH)?);
                for id in start..start + BATCH {
                    mutations.push(Mutation::Put {
                        key: key(id)?,
                        value: terrabase_value(id),
                    });
                }
                commit(&storage, SEED, start / BATCH, &mutations)?;
            }
            for start in (0..ROWS).step_by(usize::try_from(BATCH)?) {
                let mut mutations = Vec::with_capacity(usize::try_from(BATCH)?);
                for id in start..start + BATCH {
                    mutations.push(Mutation::Delete { key: key(id)? });
                }
                commit(&storage, SEED, 500_000 + start / BATCH, &mutations)?;
            }
            storage.checkpoint()?;
            let stats = storage.stats();
            ensure!(
                stats.immutable_runs == 1 && stats.mutable_bytes == 0,
                "TerraBase fixture physical state differs"
            );
            let configuration = json!({"profile": "local-durable", "checkpoint_sequence": stats.checkpoint.get(), "immutable_runs": stats.immutable_runs, "mutable_bytes": stats.mutable_bytes, "process_budget_bytes": 128 * 1024 * 1024});
            Ok((OpenEngine::Terrabase(Box::new(storage)), configuration))
        }
    }
}

async fn scan_one(engine: &OpenEngine) -> Result<Option<u64>> {
    match engine {
        OpenEngine::Sqlite(conn) => {
            let mut stmt = conn.prepare_cached("SELECT id FROM items ORDER BY id LIMIT 1")?;
            Ok(stmt
                .query_row([], |row| row.get::<_, i64>(0))
                .optional()?
                .map(u64::try_from)
                .transpose()?)
        }
        OpenEngine::Turso { connection, .. } => {
            let mut stmt = connection
                .prepare_cached("SELECT id FROM items ORDER BY id LIMIT 1")
                .await?;
            let mut rows = stmt.query(()).await?;
            Ok(rows
                .next()
                .await?
                .map(|row| row.get::<i64>(0))
                .transpose()?
                .map(u64::try_from)
                .transpose()?)
        }
        OpenEngine::Terrabase(storage) => {
            let page = storage.scan(storage.snapshot(), &KeyRange::all(), 1)?;
            Ok(page
                .rows()
                .first()
                .map(|row| -> Result<u64> {
                    let bytes: [u8; 8] = row
                        .key
                        .as_bytes()
                        .try_into()
                        .context("unexpected key width")?;
                    Ok(u64::from_be_bytes(bytes))
                })
                .transpose()?)
        }
    }
}

async fn count_rows(engine: &OpenEngine) -> Result<u64> {
    match engine {
        OpenEngine::Sqlite(conn) => Ok(u64::try_from(conn.query_row(
            "SELECT count(*) FROM items",
            [],
            |row| row.get::<_, i64>(0),
        )?)?),
        OpenEngine::Turso { connection, .. } => {
            let mut rows = connection.query("SELECT count(*) FROM items", ()).await?;
            let row = rows.next().await?.context("missing count row")?;
            Ok(u64::try_from(row.get::<i64>(0)?)?)
        }
        OpenEngine::Terrabase(storage) => Ok(storage
            .scan(storage.snapshot(), &KeyRange::all(), 1)?
            .rows()
            .len() as u64),
    }
}

async fn turso_text_pragma(conn: &turso::Connection, name: &str) -> Result<String> {
    let mut rows = conn.query(format!("PRAGMA {name}"), ()).await?;
    let row = rows.next().await?.context("missing pragma row")?;
    Ok(row.get::<String>(0)?)
}

async fn turso_int_pragma(conn: &turso::Connection, name: &str) -> Result<i64> {
    let mut rows = conn.query(format!("PRAGMA {name}"), ()).await?;
    let row = rows.next().await?.context("missing pragma row")?;
    Ok(row.get::<i64>(0)?)
}

fn key(id: u64) -> Result<Key> {
    Ok(Key::new(id.to_be_bytes().to_vec())?)
}

fn terrabase_value(id: u64) -> Vec<u8> {
    let mut value = Vec::with_capacity(8 + BODY_BYTES);
    value.extend_from_slice(&0_u64.to_be_bytes());
    value.extend_from_slice(&body(id));
    value
}

fn commit(storage: &Storage, seed: u64, serial: u64, mutations: &[Mutation]) -> Result<()> {
    let mut nonce = [0_u8; 16];
    nonce[..8].copy_from_slice(&seed.to_be_bytes());
    nonce[8..].copy_from_slice(&serial.to_be_bytes());
    let expires = u64::try_from(
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)?
            .as_millis(),
    )? + 3_600_000;
    let receipt = storage.commit_batch(
        storage.snapshot(),
        TransactionId::new(expires, nonce),
        mutations,
    )?;
    ensure!(
        receipt.frontiers.durable == Some(receipt.commit_seq),
        "TerraBase commit was not durable"
    );
    Ok(())
}

fn body(id: u64) -> Vec<u8> {
    let mut bytes = vec![0_u8; BODY_BYTES];
    let mut state = SEED ^ id.rotate_left(17);
    for chunk in bytes.chunks_mut(8) {
        state = splitmix64(state);
        chunk.copy_from_slice(&state.to_be_bytes()[..chunk.len()]);
    }
    bytes
}

fn splitmix64(mut value: u64) -> u64 {
    value = value.wrapping_add(0x9e37_79b9_7f4a_7c15);
    value = (value ^ (value >> 30)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
    value = (value ^ (value >> 27)).wrapping_mul(0x94d0_49bb_1331_11eb);
    value ^ (value >> 31)
}

fn fixture_hash() -> String {
    let mut hash = Sha256::new();
    for id in 0..ROWS {
        hash.update(id.to_be_bytes());
        hash.update(body(id));
    }
    format!("{:x}", hash.finalize())
}

fn percentile(sorted: &[u64], numerator: usize, denominator: usize) -> u64 {
    let rank = (sorted.len() * numerator).div_ceil(denominator);
    sorted[rank.saturating_sub(1)]
}

fn ns(duration: std::time::Duration) -> u64 {
    u64::try_from(duration.as_nanos()).unwrap_or(u64::MAX)
}
fn hash_bytes(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

fn hash_file(path: &Path) -> Result<String> {
    let mut file = File::open(path)?;
    let mut hasher = Sha256::new();
    let mut buffer = [0_u8; 8192];
    loop {
        let count = file.read(&mut buffer)?;
        if count == 0 {
            break;
        }
        hasher.update(&buffer[..count]);
    }
    Ok(format!("{:x}", hasher.finalize()))
}

fn file_sizes(path: &Path) -> Result<(u64, u64)> {
    if path.is_file() {
        let database_bytes = fs::metadata(path)?.len();
        let wal_bytes =
            fs::metadata(format!("{}-wal", path.display())).map_or(0, |meta| meta.len());
        Ok((database_bytes, wal_bytes))
    } else if path.is_dir() {
        let mut database_bytes = 0_u64;
        let mut wal_bytes = 0_u64;
        for entry in fs::read_dir(path)? {
            let entry = entry?;
            let len = entry.metadata()?.len();
            if entry.file_name() == "wal.log" {
                wal_bytes += len;
            } else {
                database_bytes += len;
            }
        }
        Ok((database_bytes, wal_bytes))
    } else {
        bail!("database path disappeared")
    }
}
