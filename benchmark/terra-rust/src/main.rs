use std::fs::{self, File, OpenOptions};
use std::io::{BufWriter, Read, Write};
use std::path::{Path, PathBuf};
use std::time::Instant;

use anyhow::{Context, Result, bail, ensure};
use clap::{Parser, Subcommand, ValueEnum};
use rusqlite::{Connection, OptionalExtension, params};
use serde::Serialize;
use serde_json::Value;
use sha2::{Digest, Sha256};

const MATRIX_VERSION: &str = "terrabase-rust-acceptance-v1";
const SQLITE_CACHE_KIB: i64 = 65_536;
const RANGE_LIMIT: i64 = 16;

#[derive(Parser)]
#[command(about = "Pinned embedded SQLite baseline; execute inside the Firecracker guest")]
struct Cli {
    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand)]
enum Command {
    Run(RunArgs),
}

#[derive(Clone, Copy, Debug, Serialize, ValueEnum)]
#[serde(rename_all = "kebab-case")]
enum Case {
    PointHitRandom,
    PointMissRandom,
    PointHitSequential,
    IndexedEquality,
    OrderedRange,
    Insert,
    Update,
    Delete,
    Upsert,
    ReadModifyWrite,
    Mixed,
}

#[derive(Parser)]
struct RunArgs {
    #[arg(long, value_enum)]
    case: Case,
    #[arg(long)]
    rows: u64,
    #[arg(long)]
    operations: u64,
    #[arg(long)]
    body_bytes: usize,
    #[arg(long)]
    seed: u64,
    #[arg(long)]
    trial: u32,
    #[arg(long)]
    database: PathBuf,
    #[arg(long)]
    samples: PathBuf,
    #[arg(long)]
    receipt: PathBuf,
    #[arg(long)]
    guest_manifest: PathBuf,
    #[arg(long)]
    benchmark_revision: String,
    #[arg(long)]
    engine_revision: String,
}

#[derive(Serialize)]
struct Sample {
    operation: u64,
    elapsed_ns: u64,
    success: bool,
    error: Option<String>,
}

#[derive(Serialize)]
struct Latency {
    p50_ns: u64,
    p95_ns: u64,
    p99_ns: u64,
    p999_ns: u64,
}

#[derive(Serialize)]
struct ResourceSnapshot {
    peak_rss_kib: Option<u64>,
    current_rss_kib: Option<u64>,
    pss_kib: Option<u64>,
    process_read_bytes: Option<u64>,
    process_write_bytes: Option<u64>,
    database_bytes: u64,
    wal_bytes: u64,
}

#[derive(Serialize)]
struct Receipt<'a> {
    schema_version: u32,
    matrix_version: &'static str,
    engine: &'static str,
    case: Case,
    status: &'static str,
    error: Option<String>,
    rows: u64,
    operations_requested: u64,
    operations_completed: u64,
    errors: u64,
    body_bytes: usize,
    seed: u64,
    trial: u32,
    clients: u32,
    durability: &'static str,
    journal_mode: String,
    synchronous: i64,
    cache_kib: i64,
    sqlite_version: &'static str,
    rust_toolchain: &'static str,
    rusqlite_crate_version: &'static str,
    libsqlite3_sys_crate_version: &'static str,
    binary_sha256: String,
    driver_source_sha256: String,
    matrix_sha256: String,
    dependency_lock_sha256: String,
    guest_manifest_sha256: String,
    guest_manifest: &'a Value,
    benchmark_revision: &'a str,
    engine_revision: &'a str,
    fixture_sha256: String,
    observed_checksum: u64,
    elapsed_ns: u64,
    completed_ops_per_second: f64,
    latency: Latency,
    integrity_check: String,
    final_row_count: u64,
    final_revision_sum: u64,
    resources: ResourceSnapshot,
    samples_path: &'a Path,
    database_path: &'a Path,
}

fn main() -> Result<()> {
    match Cli::parse().command {
        Command::Run(args) => run(&args),
    }
}

fn run(args: &RunArgs) -> Result<()> {
    ensure!(
        args.rows >= 1024 && args.rows <= i64::MAX as u64 / 2,
        "rows must be in 1024..=i64::MAX/2"
    );
    ensure!(
        args.operations > 0 && args.operations <= 10_000_000,
        "operations must be in 1..=10000000 to bound raw latency memory"
    );
    ensure!(
        args.body_bytes > 0 && args.body_bytes <= 1_048_576,
        "body_bytes must be in 1..=1048576"
    );
    ensure!(args.trial > 0, "trial must be positive");
    ensure!(
        !args.benchmark_revision.trim().is_empty() && !args.engine_revision.trim().is_empty(),
        "source revisions are required"
    );
    if matches!(args.case, Case::Delete) {
        ensure!(
            args.operations <= args.rows,
            "delete operations cannot exceed fixture rows"
        );
    }
    ensure!(
        !args.database.exists(),
        "database path already exists: {}",
        args.database.display()
    );
    ensure!(
        !args.samples.exists(),
        "samples path already exists: {}",
        args.samples.display()
    );
    ensure!(
        !args.receipt.exists(),
        "receipt path already exists: {}",
        args.receipt.display()
    );
    ensure!(
        args.database != args.samples
            && args.database != args.receipt
            && args.samples != args.receipt,
        "database, samples and receipt paths must differ"
    );

    let manifest_bytes = fs::read(&args.guest_manifest).context("read guest manifest")?;
    let manifest: Value =
        serde_json::from_slice(&manifest_bytes).context("parse guest manifest")?;
    validate_manifest(&manifest)?;
    let manifest_hash = hex_hash(&manifest_bytes);
    let binary_hash = hash_file(&std::env::current_exe().context("resolve executable")?)?;

    let mut conn = Connection::open(&args.database).context("open new SQLite database")?;
    configure_sqlite(&conn)?;
    let journal_mode: String = conn.query_row("PRAGMA journal_mode", [], |row| row.get(0))?;
    let synchronous: i64 = conn.query_row("PRAGMA synchronous", [], |row| row.get(0))?;
    let cache_size: i64 = conn.query_row("PRAGMA cache_size", [], |row| row.get(0))?;
    ensure!(
        journal_mode.eq_ignore_ascii_case("wal")
            && synchronous == 2
            && cache_size == -SQLITE_CACHE_KIB,
        "SQLite WAL/FULL/cache tuning was not applied"
    );
    conn.execute_batch(
        "CREATE TABLE items (id INTEGER PRIMARY KEY, group_key INTEGER NOT NULL, sort_key INTEGER NOT NULL, payload BLOB NOT NULL, doc BLOB NOT NULL, rev INTEGER NOT NULL DEFAULT 0);\
         CREATE INDEX items_group_sort ON items(group_key, sort_key, id);",
    )?;
    let fixture_hash = populate(&mut conn, args)?;

    let samples_file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&args.samples)
        .context("create raw samples")?;
    let mut latency = Vec::with_capacity(args.operations as usize);
    let mut completed = 0_u64;
    let mut observed_checksum = 0_u64;
    let mut error = None;
    let mut failed_latency = None;
    let total_start = Instant::now();
    for i in 0..args.operations {
        let start = Instant::now();
        let result = operation(&conn, args, i);
        let elapsed_ns = ns(start.elapsed());
        match result {
            Ok(value) => {
                completed += 1;
                observed_checksum = observed_checksum.wrapping_add(value);
                latency.push(elapsed_ns);
            }
            Err(cause) => {
                error = Some(cause.to_string());
                failed_latency = Some(elapsed_ns);
                break;
            }
        }
    }
    let elapsed_ns = ns(total_start.elapsed());
    let mut samples = BufWriter::new(samples_file);
    for (operation, &sample_ns) in latency.iter().enumerate() {
        serde_json::to_writer(
            &mut samples,
            &Sample {
                operation: operation as u64,
                elapsed_ns: sample_ns,
                success: true,
                error: None,
            },
        )?;
        samples.write_all(b"\n")?;
    }
    if let Some(sample_ns) = failed_latency {
        serde_json::to_writer(
            &mut samples,
            &Sample {
                operation: completed,
                elapsed_ns: sample_ns,
                success: false,
                error: error.clone(),
            },
        )?;
        samples.write_all(b"\n")?;
    }
    samples.flush()?;
    latency.sort_unstable();

    let integrity_check: String = conn
        .query_row("PRAGMA integrity_check(1)", [], |row| row.get(0))
        .unwrap_or_else(|cause| format!("ERROR: {cause}"));
    let (final_row_count, final_revision_sum): (i64, i64) = conn.query_row(
        "SELECT count(*), coalesce(sum(rev),0) FROM items",
        [],
        |row| Ok((row.get(0)?, row.get(1)?)),
    )?;
    let final_row_count = u64::try_from(final_row_count)?;
    let final_revision_sum = u64::try_from(final_revision_sum)?;
    let (expected_count, expected_revisions) = expected_final(args);
    if error.is_none()
        && (integrity_check != "ok"
            || final_row_count != expected_count
            || final_revision_sum != expected_revisions)
    {
        error = Some(format!(
            "final verification failed: integrity={integrity_check}, rows={final_row_count}/{expected_count}, revisions={final_revision_sum}/{expected_revisions}"
        ));
    }
    let resources = resources(&args.database);
    let receipt = Receipt {
        schema_version: 1,
        matrix_version: MATRIX_VERSION,
        engine: "sqlite-embedded",
        case: args.case,
        status: if error.is_some() {
            "failed"
        } else {
            "sqlite_baseline_only"
        },
        error: error.clone(),
        rows: args.rows,
        operations_requested: args.operations,
        operations_completed: completed,
        errors: u64::from(error.is_some()),
        body_bytes: args.body_bytes,
        seed: args.seed,
        trial: args.trial,
        clients: 1,
        durability: "local-durable",
        journal_mode,
        synchronous,
        cache_kib: SQLITE_CACHE_KIB,
        sqlite_version: rusqlite::version(),
        rust_toolchain: "1.98.1",
        rusqlite_crate_version: "0.40.2",
        libsqlite3_sys_crate_version: "0.38.2",
        binary_sha256: binary_hash,
        driver_source_sha256: hex_hash(include_bytes!("main.rs")),
        matrix_sha256: hex_hash(include_bytes!(
            "../../bench/terrabase-rust-acceptance-v1.md"
        )),
        dependency_lock_sha256: hex_hash(include_bytes!("../Cargo.lock")),
        guest_manifest_sha256: manifest_hash,
        guest_manifest: &manifest,
        benchmark_revision: &args.benchmark_revision,
        engine_revision: &args.engine_revision,
        fixture_sha256: fixture_hash,
        observed_checksum,
        elapsed_ns,
        completed_ops_per_second: if elapsed_ns == 0 {
            0.0
        } else {
            completed as f64 * 1e9 / elapsed_ns as f64
        },
        latency: Latency {
            p50_ns: percentile(&latency, 500, 1000),
            p95_ns: percentile(&latency, 950, 1000),
            p99_ns: percentile(&latency, 990, 1000),
            p999_ns: percentile(&latency, 999, 1000),
        },
        integrity_check,
        final_row_count,
        final_revision_sum,
        resources,
        samples_path: &args.samples,
        database_path: &args.database,
    };
    let receipt_file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&args.receipt)
        .context("create receipt")?;
    serde_json::to_writer_pretty(receipt_file, &receipt)?;
    if let Some(cause) = error {
        bail!(
            "baseline failed; receipt at {}: {cause}",
            args.receipt.display()
        );
    }
    println!("{}", args.receipt.display());
    Ok(())
}

fn validate_manifest(value: &Value) -> Result<()> {
    ensure!(
        value["vcpu_count"].as_u64() == Some(4),
        "guest manifest must declare 4 vCPUs"
    );
    ensure!(
        value["mem_size_mib"].as_u64() == Some(8192),
        "guest manifest must declare 8192 MiB"
    );
    ensure!(
        value["swap_disabled"].as_bool() == Some(true),
        "guest manifest must declare swap disabled"
    );
    ensure!(
        value["firecracker_version"]
            .as_str()
            .is_some_and(|s| !s.is_empty()),
        "guest manifest requires Firecracker version"
    );
    Ok(())
}

fn configure_sqlite(conn: &Connection) -> Result<()> {
    conn.busy_timeout(std::time::Duration::from_secs(10))?;
    conn.set_prepared_statement_cache_capacity(32);
    conn.execute_batch(
        "PRAGMA page_size=4096; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;\
         PRAGMA cache_size=-65536; PRAGMA temp_store=FILE; PRAGMA mmap_size=0;\
         PRAGMA wal_autocheckpoint=1000; PRAGMA foreign_keys=ON;",
    )?;
    Ok(())
}

fn populate(conn: &mut Connection, args: &RunArgs) -> Result<String> {
    let mut digest = Sha256::new();
    let tx = conn.transaction()?;
    {
        let mut stmt = tx.prepare_cached(
            "INSERT INTO items(id,group_key,sort_key,payload,doc) VALUES (?1,?2,?3,?4,jsonb(?5))",
        )?;
        for id in 0..args.rows {
            let payload = body(args.seed, id, args.body_bytes);
            digest.update(id.to_be_bytes());
            digest.update((id % 1024).to_be_bytes());
            digest.update((id / 1024).to_be_bytes());
            digest.update(&payload);
            let doc = format!("{{\"nested\":{{\"group\":{}}}}}", id % 1024);
            stmt.execute(params![
                id as i64,
                (id % 1024) as i64,
                (id / 1024) as i64,
                payload,
                doc
            ])?;
        }
    }
    tx.commit()?;
    Ok(format!("{:x}", digest.finalize()))
}

fn operation(conn: &Connection, args: &RunArgs, i: u64) -> Result<u64> {
    let selected = splitmix64(args.seed ^ i) % args.rows;
    match args.case {
        Case::PointHitRandom | Case::PointHitSequential | Case::PointMissRandom => {
            let id = if matches!(args.case, Case::PointHitSequential) {
                i % args.rows
            } else if matches!(args.case, Case::PointMissRandom) {
                args.rows + selected
            } else {
                selected
            };
            let mut stmt = conn.prepare_cached("SELECT payload FROM items WHERE id=?1")?;
            let found: Option<Vec<u8>> =
                stmt.query_row([id as i64], |row| row.get(0)).optional()?;
            if matches!(args.case, Case::PointMissRandom) {
                ensure!(found.is_none(), "unexpected hit for missing key {id}");
            } else {
                ensure!(
                    found.as_deref() == Some(body(args.seed, id, args.body_bytes).as_slice()),
                    "wrong payload for key {id}"
                );
            }
            Ok(id)
        }
        Case::IndexedEquality => {
            let group = (selected % 1024) as i64;
            let mut stmt = conn.prepare_cached(
                "SELECT id,group_key,payload FROM items WHERE group_key=?1 ORDER BY sort_key,id LIMIT 16",
            )?;
            let mut rows = stmt.query([group])?;
            let mut count = 0_u64;
            let mut previous = None;
            let mut checksum = 0_u64;
            while let Some(row) = rows.next()? {
                let id = u64::try_from(row.get::<_, i64>(0)?)?;
                let got_group: i64 = row.get(1)?;
                let payload: Vec<u8> = row.get(2)?;
                ensure!(
                    got_group == group && id % 1024 == group as u64,
                    "wrong equality result"
                );
                ensure!(
                    previous.is_none_or(|old| id > old),
                    "equality results unordered"
                );
                ensure!(
                    payload == body(args.seed, id, args.body_bytes),
                    "wrong equality payload"
                );
                previous = Some(id);
                checksum = checksum.wrapping_add(id);
                count += 1;
            }
            ensure!(
                count > 0 && count <= RANGE_LIMIT as u64,
                "wrong equality cardinality"
            );
            Ok(checksum)
        }
        Case::OrderedRange => {
            let mut stmt = conn
                .prepare_cached("SELECT id,payload FROM items WHERE id>=?1 ORDER BY id LIMIT 16")?;
            let mut rows = stmt.query([selected as i64])?;
            let mut count = 0_u64;
            let mut checksum = 0_u64;
            while let Some(row) = rows.next()? {
                let id = u64::try_from(row.get::<_, i64>(0)?)?;
                let payload: Vec<u8> = row.get(1)?;
                ensure!(id == selected + count, "wrong ordered range key");
                ensure!(
                    payload == body(args.seed, id, args.body_bytes),
                    "wrong range payload"
                );
                checksum = checksum.wrapping_add(id);
                count += 1;
            }
            ensure!(
                count == (args.rows - selected).min(RANGE_LIMIT as u64),
                "wrong range cardinality"
            );
            Ok(checksum)
        }
        Case::Insert => {
            let id = args.rows + i;
            let payload = body(args.seed, id, args.body_bytes);
            let doc = format!("{{\"nested\":{{\"group\":{}}}}}", id % 1024);
            let mut stmt = conn.prepare_cached("INSERT INTO items(id,group_key,sort_key,payload,doc) VALUES (?1,?2,?3,?4,jsonb(?5))")?;
            ensure!(
                stmt.execute(params![
                    id as i64,
                    (id % 1024) as i64,
                    (id / 1024) as i64,
                    payload,
                    doc
                ])? == 1,
                "insert changed wrong number of rows"
            );
            Ok(id)
        }
        Case::Update => {
            let mut stmt = conn.prepare_cached("UPDATE items SET rev=rev+1 WHERE id=?1")?;
            ensure!(
                stmt.execute([selected as i64])? == 1,
                "update changed wrong number of rows"
            );
            Ok(selected)
        }
        Case::Delete => {
            let id = i;
            let mut stmt = conn.prepare_cached("DELETE FROM items WHERE id=?1")?;
            ensure!(
                stmt.execute([id as i64])? == 1,
                "delete changed wrong number of rows"
            );
            Ok(id)
        }
        Case::Upsert => {
            let id = if i % 2 == 0 { selected } else { args.rows + i };
            let payload = body(args.seed, id, args.body_bytes);
            let doc = format!("{{\"nested\":{{\"group\":{}}}}}", id % 1024);
            let mut stmt = conn.prepare_cached(
                "INSERT INTO items(id,group_key,sort_key,payload,doc) VALUES (?1,?2,?3,?4,jsonb(?5)) \
                 ON CONFLICT(id) DO UPDATE SET payload=excluded.payload,rev=items.rev+1",
            )?;
            ensure!(
                stmt.execute(params![
                    id as i64,
                    (id % 1024) as i64,
                    (id / 1024) as i64,
                    payload,
                    doc
                ])? == 1,
                "upsert changed wrong number of rows"
            );
            Ok(id)
        }
        Case::ReadModifyWrite => {
            conn.execute_batch("BEGIN IMMEDIATE")?;
            let result = (|| -> Result<()> {
                let mut read = conn.prepare_cached("SELECT rev FROM items WHERE id=?1")?;
                let rev: i64 = read.query_row([selected as i64], |row| row.get(0))?;
                drop(read);
                let mut write =
                    conn.prepare_cached("UPDATE items SET rev=?2 WHERE id=?1 AND rev=?3")?;
                ensure!(
                    write.execute(params![selected as i64, rev + 1, rev])? == 1,
                    "RMW lost update"
                );
                Ok(())
            })();
            if let Err(cause) = result {
                conn.execute_batch("ROLLBACK")?;
                return Err(cause);
            }
            conn.execute_batch("COMMIT")?;
            Ok(selected)
        }
        Case::Mixed => {
            if i % 5 == 0 {
                let mut stmt = conn.prepare_cached("UPDATE items SET rev=rev+1 WHERE id=?1")?;
                ensure!(
                    stmt.execute([selected as i64])? == 1,
                    "mixed write missed key"
                );
            } else {
                let mut stmt = conn.prepare_cached("SELECT payload FROM items WHERE id=?1")?;
                let payload: Vec<u8> = stmt.query_row([selected as i64], |row| row.get(0))?;
                ensure!(
                    payload == body(args.seed, selected, args.body_bytes),
                    "mixed read returned wrong body"
                );
            }
            Ok(selected)
        }
    }
}

fn expected_final(args: &RunArgs) -> (u64, u64) {
    match args.case {
        Case::Insert => (args.rows + args.operations, 0),
        Case::Delete => (args.rows - args.operations, 0),
        Case::Update | Case::ReadModifyWrite => (args.rows, args.operations),
        Case::Upsert => (args.rows + args.operations / 2, args.operations.div_ceil(2)),
        Case::Mixed => (args.rows, args.operations.div_ceil(5)),
        _ => (args.rows, 0),
    }
}

fn body(seed: u64, id: u64, size: usize) -> Vec<u8> {
    let mut bytes = vec![0_u8; size];
    let mut state = seed ^ id.rotate_left(17);
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

fn percentile(values: &[u64], numerator: usize, denominator: usize) -> u64 {
    if values.is_empty() {
        return 0;
    }
    values[(values.len() * numerator)
        .div_ceil(denominator)
        .saturating_sub(1)]
}

fn ns(duration: std::time::Duration) -> u64 {
    u64::try_from(duration.as_nanos()).unwrap_or(u64::MAX)
}

fn hex_hash(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

fn hash_file(path: &Path) -> Result<String> {
    let mut file = File::open(path)?;
    let mut digest = Sha256::new();
    let mut buffer = [0_u8; 64 * 1024];
    loop {
        let read = file.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        digest.update(&buffer[..read]);
    }
    Ok(format!("{:x}", digest.finalize()))
}

fn resources(database: &Path) -> ResourceSnapshot {
    ResourceSnapshot {
        peak_rss_kib: proc_key("/proc/self/status", "VmHWM:", 1),
        current_rss_kib: proc_key("/proc/self/status", "VmRSS:", 1),
        pss_kib: proc_key("/proc/self/smaps_rollup", "Pss:", 1),
        process_read_bytes: proc_key("/proc/self/io", "read_bytes:", 1),
        process_write_bytes: proc_key("/proc/self/io", "write_bytes:", 1),
        database_bytes: fs::metadata(database).map_or(0, |m| m.len()),
        wal_bytes: fs::metadata(format!("{}-wal", database.display())).map_or(0, |m| m.len()),
    }
}

fn proc_key(path: &str, key: &str, position: usize) -> Option<u64> {
    let text = fs::read_to_string(path).ok()?;
    let line = text.lines().find(|line| line.starts_with(key))?;
    line.split_ascii_whitespace().nth(position)?.parse().ok()
}

#[cfg(test)]
mod tests {
    use super::{Case, RunArgs, body, expected_final, percentile};
    use std::path::PathBuf;

    #[test]
    fn fixture_is_deterministic_and_keyed() {
        assert_eq!(body(7, 42, 31), body(7, 42, 31));
        assert_ne!(body(7, 42, 31), body(7, 43, 31));
    }

    #[test]
    fn percentile_uses_nearest_rank() {
        assert_eq!(percentile(&[1, 2, 3, 4], 500, 1000), 2);
        assert_eq!(percentile(&[1, 2, 3, 4], 950, 1000), 4);
    }

    #[test]
    fn upsert_counts_only_new_odd_keys() {
        let args = RunArgs {
            case: Case::Upsert,
            rows: 1024,
            operations: 5,
            body_bytes: 1,
            seed: 1,
            trial: 1,
            database: PathBuf::new(),
            samples: PathBuf::new(),
            receipt: PathBuf::new(),
            guest_manifest: PathBuf::new(),
            benchmark_revision: String::new(),
            engine_revision: String::new(),
        };
        assert_eq!(expected_final(&args), (1026, 3));
    }
}
