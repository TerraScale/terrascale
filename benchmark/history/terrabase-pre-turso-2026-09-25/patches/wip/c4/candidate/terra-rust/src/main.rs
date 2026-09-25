use std::fs::{self, File, OpenOptions};
use std::io::{BufWriter, Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Barrier, Condvar, Mutex, mpsc};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use anyhow::{Context, Result, bail, ensure};
use clap::{Parser, Subcommand, ValueEnum};
use rusqlite::{Connection, OptionalExtension, params};
use rustix::time::{ClockId, clock_gettime};
use serde::Serialize;
use serde_json::Value;
use sha2::{Digest, Sha256};

const MATRIX_VERSION: &str = "terrabase-rust-acceptance-v1";
const SQLITE_CACHE_KIB: i64 = 65_536;
const SQLITE_C4_CACHE_KIB: i64 = SQLITE_CACHE_KIB / 4;
const RANGE_LIMIT: i64 = 16;
const MAX_DELETED_HISTORY_SCANS: u64 = 10_000;
const DELETED_HISTORY_SCAN_TIMEOUT: Duration = Duration::from_secs(30);

mod analysis;
#[cfg(feature = "terrabase")]
mod terrabase;

#[derive(Parser)]
#[command(about = "Pinned embedded SQLite baseline; execute inside the Firecracker guest")]
struct Cli {
    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand)]
enum Command {
    Run(RunArgs),
    RunTerrabase(RunArgs),
    AnalyzeP04(analysis::AnalyzeArgs),
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
    BatchInsert,
    Update,
    Delete,
    Upsert,
    ReadModifyWrite,
    Mixed,
    Reopen,
    DeletedHistory,
}

#[derive(Clone, Parser)]
#[allow(clippy::struct_excessive_bools)] // Independent diagnostic and fixture CLI flags are recorded verbatim.
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
    #[arg(long, default_value_t = 1)]
    clients: u32,
    #[arg(long, default_value_t = false)]
    checkpoint_fixture: bool,
    #[arg(long, default_value_t = false)]
    checkpoint_before_deletes: bool,
    #[arg(long, default_value_t = false)]
    sparse_tail: bool,
    #[arg(long, default_value_t = false)]
    scan_diagnostics: bool,
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
    #[serde(skip_serializing_if = "Option::is_none")]
    scan_phase: Option<&'static str>,
}

#[derive(Serialize)]
struct C4Sample {
    operation: u64,
    client_id: u32,
    elapsed_ns: u64,
    success: bool,
    error: Option<String>,
}

struct C4Operation {
    operation: u64,
    client_id: u32,
    elapsed_ns: u64,
    result: std::result::Result<u64, String>,
}

#[derive(Serialize)]
#[allow(clippy::struct_field_names)] // Units are part of the stable JSON receipt schema.
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
struct ScanDiagnostics {
    cpu_clock: &'static str,
    cpu_runtime_ns: Option<u64>,
    cpu_bracket_elapsed_ns: u64,
    cpu_bracket_overhead_ns: Option<u64>,
    process_read_bytes: Option<u64>,
    process_write_bytes: Option<u64>,
}

struct ScanSnapshot {
    cpu_bracket_instant: Instant,
    cpu_runtime_ns: Option<u64>,
    process_read_bytes: Option<u64>,
    process_write_bytes: Option<u64>,
}

impl ScanSnapshot {
    fn capture_before() -> Self {
        let process_read_bytes = proc_key("/proc/self/io", "read_bytes:", 1);
        let process_write_bytes = proc_key("/proc/self/io", "write_bytes:", 1);
        let cpu_bracket_instant = Instant::now();
        Self {
            cpu_bracket_instant,
            cpu_runtime_ns: scan_cpu_runtime_ns(),
            process_read_bytes,
            process_write_bytes,
        }
    }

    fn capture_after() -> Self {
        let cpu_runtime_ns = scan_cpu_runtime_ns();
        let cpu_bracket_instant = Instant::now();
        Self {
            cpu_bracket_instant,
            cpu_runtime_ns,
            process_read_bytes: proc_key("/proc/self/io", "read_bytes:", 1),
            process_write_bytes: proc_key("/proc/self/io", "write_bytes:", 1),
        }
    }

    fn delta(&self, after: &Self, operation_elapsed_ns: u64) -> ScanDiagnostics {
        let cpu_bracket_elapsed_ns = ns(after
            .cpu_bracket_instant
            .duration_since(self.cpu_bracket_instant));
        ScanDiagnostics {
            cpu_clock: "CLOCK_THREAD_CPUTIME_ID",
            cpu_runtime_ns: self
                .cpu_runtime_ns
                .zip(after.cpu_runtime_ns)
                .and_then(|(before, after)| after.checked_sub(before)),
            cpu_bracket_elapsed_ns,
            cpu_bracket_overhead_ns: cpu_bracket_elapsed_ns.checked_sub(operation_elapsed_ns),
            process_read_bytes: self
                .process_read_bytes
                .zip(after.process_read_bytes)
                .and_then(|(before, after)| after.checked_sub(before)),
            process_write_bytes: self
                .process_write_bytes
                .zip(after.process_write_bytes)
                .and_then(|(before, after)| after.checked_sub(before)),
        }
    }
}

fn scan_cpu_runtime_ns() -> Option<u64> {
    let time = clock_gettime(ClockId::ThreadCPUTime);
    let seconds = u64::try_from(time.tv_sec).ok()?;
    let nanoseconds = u64::try_from(time.tv_nsec).ok()?;
    if nanoseconds >= 1_000_000_000 {
        return None;
    }
    seconds.checked_mul(1_000_000_000)?.checked_add(nanoseconds)
}

#[derive(Serialize)]
#[allow(clippy::struct_excessive_bools)] // The receipt preserves independent CLI fixture and diagnostic flags.
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
    checkpoint_fixture: bool,
    checkpoint_before_deletes: bool,
    sparse_tail: bool,
    scan_diagnostics_enabled: bool,
    clients: u32,
    durability: &'static str,
    journal_mode: String,
    synchronous: i64,
    cache_kib: i64,
    schema_kind: &'static str,
    query_plan: Option<String>,
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
    reopen_to_first_query_ns: Option<u64>,
    completed_ops_per_second: f64,
    latency: Latency,
    #[serde(skip_serializing_if = "Option::is_none")]
    warm_scan_samples: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    warm_scan_latency: Option<Latency>,
    integrity_check: String,
    final_row_count: u64,
    final_revision_sum: u64,
    final_state_sha256: Option<String>,
    resources: ResourceSnapshot,
    scan_diagnostics: Option<ScanDiagnostics>,
    samples_path: &'a Path,
    database_path: &'a Path,
}

fn main() -> Result<()> {
    match Cli::parse().command {
        Command::Run(args) => run(&args),
        Command::AnalyzeP04(args) => analysis::run(&args),
        Command::RunTerrabase(args) => {
            ensure!(args.clients == 1, "run-terrabase requires --clients 1");
            #[cfg(feature = "terrabase")]
            {
                terrabase::run(&args)
            }
            #[cfg(not(feature = "terrabase"))]
            {
                let _ = args;
                bail!("rebuild with --features terrabase for the real embedded caller")
            }
        }
    }
}

#[allow(clippy::too_many_lines)] // Keep validation, timing, and receipt emission in one audited run path.
fn run(args: &RunArgs) -> Result<()> {
    ensure!(
        matches!(args.clients, 1 | 4),
        "SQLite run supports only --clients 1 or 4"
    );
    if args.clients == 4 {
        ensure!(
            matches!(
                args.case,
                Case::PointHitRandom
                    | Case::PointMissRandom
                    | Case::Insert
                    | Case::Update
                    | Case::Delete
                    | Case::Upsert
                    | Case::ReadModifyWrite
            ),
            "SQLite --clients 4 does not support this case"
        );
    }
    ensure!(
        (1024..=10_000_000).contains(&args.rows),
        "rows must be in 1024..=10000000 to bound correctness memory"
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
    if matches!(args.case, Case::Reopen) {
        ensure!(
            args.operations == 1,
            "reopen case requires exactly one operation"
        );
    }
    if matches!(args.case, Case::DeletedHistory) {
        ensure!(
            args.operations <= MAX_DELETED_HISTORY_SCANS,
            "deleted-history supports at most {MAX_DELETED_HISTORY_SCANS} scans"
        );
    }
    if !matches!(args.case, Case::DeletedHistory) {
        ensure!(
            !args.checkpoint_before_deletes && !args.sparse_tail,
            "deleted-history flags require that case"
        );
    }
    if args.scan_diagnostics {
        ensure!(
            matches!(args.case, Case::OrderedRange | Case::DeletedHistory) && args.operations == 1,
            "scan diagnostics require one ordered-range or deleted-history operation"
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
    configure_sqlite(&conn, SQLITE_CACHE_KIB)?;
    let journal_mode: String = conn.query_row("PRAGMA journal_mode", [], |row| row.get(0))?;
    let synchronous: i64 = conn.query_row("PRAGMA synchronous", [], |row| row.get(0))?;
    let cache_size: i64 = conn.query_row("PRAGMA cache_size", [], |row| row.get(0))?;
    ensure!(
        journal_mode.eq_ignore_ascii_case("wal")
            && synchronous == 2
            && cache_size == -SQLITE_CACHE_KIB,
        "SQLite WAL/FULL/cache tuning was not applied"
    );
    if needs_secondary_index(args.case) {
        conn.execute_batch(
            "CREATE TABLE items (id INTEGER PRIMARY KEY, group_key INTEGER NOT NULL, sort_key INTEGER NOT NULL, payload BLOB NOT NULL, doc BLOB NOT NULL, rev INTEGER NOT NULL DEFAULT 0);\
             CREATE INDEX items_group_sort ON items(group_key, sort_key, id);",
        )?;
    } else {
        conn.execute_batch(
            "CREATE TABLE items (id INTEGER PRIMARY KEY, payload BLOB NOT NULL, rev INTEGER NOT NULL DEFAULT 0);",
        )?;
    }
    let fixture_hash = populate(&mut conn, args)?;
    conn.execute_batch("ANALYZE")?;
    if matches!(args.case, Case::DeletedHistory) {
        if args.checkpoint_before_deletes {
            conn.execute_batch("PRAGMA wal_checkpoint(TRUNCATE)")?;
        }
        let delete_before = if args.sparse_tail {
            args.rows - 1
        } else {
            args.rows
        };
        conn.execute(
            "DELETE FROM items WHERE id < ?1",
            [i64::try_from(delete_before)?],
        )?;
    }
    if args.checkpoint_fixture {
        conn.execute_batch("PRAGMA wal_checkpoint(TRUNCATE)")?;
    }
    let query_plan = query_plan(&conn, args.case)?;
    if matches!(args.case, Case::IndexedEquality) {
        ensure!(
            query_plan
                .as_deref()
                .is_some_and(|plan| plan.contains("items_group_sort")),
            "SQLite did not select the declared secondary index"
        );
    }

    if args.clients == 4 {
        drop(conn);
        return run_sqlite_c4(
            args,
            &manifest,
            binary_hash,
            manifest_hash,
            fixture_hash,
            query_plan,
        );
    }

    let samples_file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&args.samples)
        .context("create raw samples")?;
    let mut latency = Vec::with_capacity(usize::try_from(args.operations)?);
    let mut completed = 0_u64;
    let mut observed_checksum = 0_u64;
    let mut error = None;
    let mut failed_latency = None;
    let mut scan_diagnostics = None;
    let total_start = Instant::now();
    let scan_guard = arm_deleted_history_timeout(
        args,
        "sqlite-embedded",
        &binary_hash,
        &fixture_hash,
        &manifest_hash,
        None,
    );
    if matches!(args.case, Case::Reopen) {
        drop(conn);
        conn = Connection::open(&args.database).context("reopen SQLite database")?;
        configure_sqlite(&conn, SQLITE_CACHE_KIB)?;
    }
    for i in 0..args.operations {
        let diagnostic_before = args.scan_diagnostics.then(ScanSnapshot::capture_before);
        if let Some(guard) = &scan_guard {
            guard.arm(i, completed);
        }
        let start = if i == 0 && matches!(args.case, Case::Reopen) {
            total_start
        } else {
            Instant::now()
        };
        let result = operation(&conn, args, i);
        let elapsed_ns = ns(start.elapsed());
        if let Some(guard) = &scan_guard {
            ensure!(
                guard.disarm(),
                "deleted-history scan exceeded 30-second deadline"
            );
        }
        if let Some(before) = diagnostic_before {
            scan_diagnostics = Some(before.delta(&ScanSnapshot::capture_after(), elapsed_ns));
        }
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
    if let Some(guard) = scan_guard {
        guard.finish()?;
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
                scan_phase: sample_scan_phase(args, operation as u64),
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
                scan_phase: sample_scan_phase(args, completed),
            },
        )?;
        samples.write_all(b"\n")?;
    }
    samples.flush()?;
    let warm_samples = warm_scan_samples(args, &latency);
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
    let final_state_sha256 = match verify_final(&conn, args) {
        Ok(hash) => Some(hash),
        Err(cause) => {
            if error.is_none() {
                error = Some(format!("logical final-state verification failed: {cause}"));
            }
            None
        }
    };
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
        checkpoint_fixture: args.checkpoint_fixture,
        checkpoint_before_deletes: args.checkpoint_before_deletes,
        sparse_tail: args.sparse_tail,
        scan_diagnostics_enabled: args.scan_diagnostics,
        clients: 1,
        durability: "local-durable",
        journal_mode,
        synchronous,
        cache_kib: SQLITE_CACHE_KIB,
        schema_kind: if needs_secondary_index(args.case) {
            "indexed-document"
        } else {
            "primary-row"
        },
        query_plan,
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
        reopen_to_first_query_ns: if matches!(args.case, Case::Reopen) { latency.first().copied() } else { None },
        #[allow(clippy::cast_precision_loss)] // Floating-point precision is sufficient for a display rate; counts and time remain exact.
        completed_ops_per_second: if elapsed_ns == 0 {
            0.0
        } else {
            completed as f64 * 1e9 / elapsed_ns as f64
        },
        latency: latency_quantiles(&latency),
        warm_scan_samples: warm_samples.as_ref().map(|samples| samples.len() as u64),
        warm_scan_latency: warm_samples.as_ref().filter(|samples| !samples.is_empty()).map(|samples| latency_quantiles(samples)),
        integrity_check,
        final_row_count,
        final_revision_sum,
        final_state_sha256,
        resources,
        scan_diagnostics,
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

#[allow(clippy::too_many_lines, clippy::too_many_arguments)]
fn run_sqlite_c4(
    args: &RunArgs,
    manifest: &Value,
    binary_hash: String,
    manifest_hash: String,
    fixture_hash: String,
    query_plan: Option<String>,
) -> Result<()> {
    let mut connections = Vec::with_capacity(4);
    for client_id in 0..4 {
        let conn = Connection::open(&args.database)
            .with_context(|| format!("open SQLite client {client_id}"))?;
        configure_sqlite(&conn, SQLITE_C4_CACHE_KIB)?;
        let journal_mode: String = conn.query_row("PRAGMA journal_mode", [], |row| row.get(0))?;
        let synchronous: i64 = conn.query_row("PRAGMA synchronous", [], |row| row.get(0))?;
        let cache_size: i64 = conn.query_row("PRAGMA cache_size", [], |row| row.get(0))?;
        ensure!(
            journal_mode.eq_ignore_ascii_case("wal")
                && synchronous == 2
                && cache_size == -SQLITE_C4_CACHE_KIB,
            "SQLite client {client_id} WAL/FULL/cache tuning was not applied"
        );
        connections.push(conn);
    }

    let samples_file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&args.samples)
        .context("create raw samples")?;
    let barrier = Barrier::new(5);
    let stop = AtomicBool::new(false);
    let (sender, receiver) = mpsc::channel::<C4Operation>();
    let mut worker_errors = Vec::new();
    let elapsed_ns = thread::scope(|scope| {
        let mut workers = Vec::with_capacity(4);
        for (client_index, conn) in connections.into_iter().enumerate() {
            let client_id = u32::try_from(client_index).expect("four clients fit u32");
            let sender = sender.clone();
            let barrier = &barrier;
            let stop = &stop;
            workers.push((
                client_id,
                scope.spawn(move || {
                    barrier.wait();
                    for operation_id in (u64::from(client_id)..args.operations).step_by(4) {
                        if stop.load(Ordering::Acquire) {
                            break;
                        }
                        let start = Instant::now();
                        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                            operation(&conn, args, operation_id)
                        }))
                        .map_err(|_| {
                            format!(
                                "SQLite client {client_id} panicked on operation {operation_id}"
                            )
                        })
                        .and_then(|value| value.map_err(|cause| cause.to_string()));
                        let elapsed_ns = ns(start.elapsed());
                        let failed = result.is_err();
                        if failed {
                            stop.store(true, Ordering::Release);
                        }
                        sender
                            .send(C4Operation {
                                operation: operation_id,
                                client_id,
                                elapsed_ns,
                                result,
                            })
                            .expect("sample receiver remains open until all workers join");
                        if failed {
                            break;
                        }
                    }
                }),
            ));
        }
        drop(sender);
        let start = Instant::now();
        barrier.wait();
        for (client_id, worker) in workers {
            if worker.join().is_err() {
                stop.store(true, Ordering::Release);
                worker_errors.push(format!("SQLite client {client_id} worker panicked"));
            }
        }
        ns(start.elapsed())
    });

    let mut operations: Vec<C4Operation> = receiver.into_iter().collect();
    operations.sort_unstable_by_key(|sample| sample.operation);
    let mut error = worker_errors.first().cloned();
    let mut errors = u64::try_from(worker_errors.len())?;
    let mut completed = 0_u64;
    let mut observed_checksum = 0_u64;
    let mut latency = Vec::with_capacity(operations.len());
    let mut samples = BufWriter::new(samples_file);
    for sample in &operations {
        match &sample.result {
            Ok(value) => {
                completed += 1;
                observed_checksum = observed_checksum.wrapping_add(*value);
                latency.push(sample.elapsed_ns);
            }
            Err(cause) => {
                errors += 1;
                if error.is_none() {
                    error = Some(cause.clone());
                }
            }
        }
        serde_json::to_writer(
            &mut samples,
            &C4Sample {
                operation: sample.operation,
                client_id: sample.client_id,
                elapsed_ns: sample.elapsed_ns,
                success: sample.result.is_ok(),
                error: sample.result.as_ref().err().cloned(),
            },
        )?;
        samples.write_all(b"\n")?;
    }
    samples.flush()?;
    if error.is_none()
        && (operations.len() != usize::try_from(args.operations)?
            || operations.iter().enumerate().any(|(expected, sample)| {
                sample.operation
                    != u64::try_from(expected).expect("operation count fits u64")
                    || u64::from(sample.client_id) != sample.operation % 4
            }))
    {
        errors += 1;
        error = Some("SQLite clients did not complete each global operation exactly once".into());
    }
    latency.sort_unstable();

    // Every measured connection is closed before post-run verification opens its own connection.
    let conn = Connection::open(&args.database).context("open SQLite final verifier")?;
    let integrity_check: String =
        conn.query_row("PRAGMA integrity_check(1)", [], |row| row.get(0))?;
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
        errors += 1;
        error = Some(format!(
            "final verification failed: integrity={integrity_check}, rows={final_row_count}/{expected_count}, revisions={final_revision_sum}/{expected_revisions}"
        ));
    }
    let final_state_sha256 = match verify_final(&conn, args) {
        Ok(hash) => Some(hash),
        Err(cause) => {
            if error.is_none() {
                errors += 1;
                error = Some(format!("logical final-state verification failed: {cause}"));
            }
            None
        }
    };
    drop(conn);

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
        errors,
        body_bytes: args.body_bytes,
        seed: args.seed,
        trial: args.trial,
        checkpoint_fixture: args.checkpoint_fixture,
        checkpoint_before_deletes: args.checkpoint_before_deletes,
        sparse_tail: args.sparse_tail,
        scan_diagnostics_enabled: args.scan_diagnostics,
        clients: 4,
        durability: "local-durable",
        journal_mode: "wal".into(),
        synchronous: 2,
        cache_kib: SQLITE_C4_CACHE_KIB,
        schema_kind: "primary-row",
        query_plan,
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
        guest_manifest: manifest,
        benchmark_revision: &args.benchmark_revision,
        engine_revision: &args.engine_revision,
        fixture_sha256: fixture_hash,
        observed_checksum,
        elapsed_ns,
        reopen_to_first_query_ns: None,
        #[allow(clippy::cast_precision_loss)]
        completed_ops_per_second: if elapsed_ns == 0 {
            0.0
        } else {
            completed as f64 * 1e9 / elapsed_ns as f64
        },
        latency: latency_quantiles(&latency),
        warm_scan_samples: None,
        warm_scan_latency: None,
        integrity_check,
        final_row_count,
        final_revision_sum,
        final_state_sha256,
        resources: resources(&args.database),
        scan_diagnostics: None,
        samples_path: &args.samples,
        database_path: &args.database,
    };
    let mut receipt = serde_json::to_value(receipt)?;
    receipt["aggregate_cache_kib"] = serde_json::json!(SQLITE_CACHE_KIB);
    if error.is_some() {
        receipt["latency"] = Value::Null;
        receipt["completed_ops_per_second"] = Value::Null;
    }
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

#[derive(Clone, Copy)]
struct ActiveScan {
    operation: u64,
    completed: u64,
    generation: u64,
    deadline: Instant,
}

struct WatchdogState {
    active: Option<ActiveScan>,
    generation: u64,
    stopping: bool,
    timed_out: bool,
}

struct DeletedHistoryWatchdog {
    shared: Arc<(Mutex<WatchdogState>, Condvar)>,
    worker: Option<JoinHandle<()>>,
    timeout: Duration,
}

impl DeletedHistoryWatchdog {
    fn new(timeout: Duration, on_timeout: impl FnOnce(ActiveScan) + Send + 'static) -> Self {
        let shared = Arc::new((
            Mutex::new(WatchdogState {
                active: None,
                generation: 0,
                stopping: false,
                timed_out: false,
            }),
            Condvar::new(),
        ));
        let worker_shared = Arc::clone(&shared);
        let worker = std::thread::spawn(move || {
            let (lock, wake) = &*worker_shared;
            let mut state = lock.lock().expect("watchdog state lock");
            loop {
                while state.active.is_none() && !state.stopping {
                    state = wake.wait(state).expect("watchdog state wait");
                }
                if state.stopping {
                    return;
                }
                let active = state.active.expect("active scan after wait");
                let remaining = active.deadline.saturating_duration_since(Instant::now());
                let (next, _) = wake
                    .wait_timeout(state, remaining)
                    .expect("watchdog deadline wait");
                state = next;
                if state
                    .active
                    .is_some_and(|current| current.generation == active.generation)
                    && Instant::now() >= active.deadline
                {
                    state.stopping = true;
                    state.timed_out = true;
                    wake.notify_all();
                    drop(state);
                    on_timeout(active);
                    return;
                }
            }
        });
        Self {
            shared,
            worker: Some(worker),
            timeout,
        }
    }

    fn arm(&self, operation: u64, completed: u64) {
        let (lock, wake) = &*self.shared;
        let mut state = lock.lock().expect("watchdog state lock");
        state.generation += 1;
        state.active = Some(ActiveScan {
            operation,
            completed,
            generation: state.generation,
            deadline: Instant::now() + self.timeout,
        });
        wake.notify_one();
    }

    fn disarm(&self) -> bool {
        let (lock, wake) = &*self.shared;
        let mut state = lock.lock().expect("watchdog state lock");
        if state
            .active
            .is_some_and(|active| Instant::now() >= active.deadline)
        {
            wake.notify_one();
            while !state.timed_out {
                state = wake.wait(state).expect("watchdog timeout wait");
            }
            return false;
        }
        if state.timed_out {
            return false;
        }
        state.active = None;
        wake.notify_one();
        true
    }

    fn stop(&mut self) {
        let (lock, wake) = &*self.shared;
        let mut state = lock.lock().expect("watchdog state lock");
        state.active = None;
        state.stopping = true;
        wake.notify_one();
    }

    fn finish(mut self) -> Result<()> {
        self.stop();
        if let Some(worker) = self.worker.take() {
            worker
                .join()
                .map_err(|_| anyhow::anyhow!("deleted-history watchdog panicked"))?;
        }
        Ok(())
    }
}

impl Drop for DeletedHistoryWatchdog {
    fn drop(&mut self) {
        self.stop();
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

fn arm_deleted_history_timeout(
    args: &RunArgs,
    engine: &'static str,
    binary_sha256: &str,
    fixture_sha256: &str,
    guest_manifest_sha256: &str,
    physical_fixture_state: Option<Value>,
) -> Option<DeletedHistoryWatchdog> {
    if !matches!(args.case, Case::DeletedHistory) {
        return None;
    }
    let args = args.clone();
    let binary_sha256 = binary_sha256.to_owned();
    let fixture_sha256 = fixture_sha256.to_owned();
    let guest_manifest_sha256 = guest_manifest_sha256.to_owned();
    Some(DeletedHistoryWatchdog::new(
        DELETED_HISTORY_SCAN_TIMEOUT,
        move |active| {
            let timeout_receipt = deleted_history_timeout_receipt(
                &args,
                engine,
                &binary_sha256,
                &fixture_sha256,
                &guest_manifest_sha256,
                physical_fixture_state.as_ref(),
                active,
            );
            if let Ok(mut file) = OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(&args.receipt)
            {
                let _ = serde_json::to_writer_pretty(&mut file, &timeout_receipt);
                let _ = file.flush();
            }
            eprintln!(
                "deleted-history measured scan exceeded 30 seconds; receipt: {}",
                args.receipt.display()
            );
            std::process::exit(124);
        },
    ))
}

#[allow(clippy::too_many_arguments)] // Timeout receipt mirrors the source and fixture identity fields.
fn deleted_history_timeout_receipt(
    args: &RunArgs,
    engine: &'static str,
    binary_sha256: &str,
    fixture_sha256: &str,
    guest_manifest_sha256: &str,
    physical_fixture_state: Option<&Value>,
    active: ActiveScan,
) -> Value {
    let mut receipt = serde_json::json!({
        "schema_version": 1,
        "matrix_version": MATRIX_VERSION,
        "engine": engine,
        "case": "deleted-history",
        "status": "timed_out",
        "timeout_phase": "measured_scan",
        "timeout_seconds": 30,
        "rows": args.rows,
        "body_bytes": args.body_bytes,
        "seed": args.seed,
        "trial": args.trial,
        "operations_requested": args.operations,
        "operations_completed": active.completed,
        "timeout_operation": active.operation,
        "sparse_tail": args.sparse_tail,
        "scan_diagnostics_enabled": args.scan_diagnostics,
        "checkpoint_fixture": args.checkpoint_fixture,
        "checkpoint_before_deletes": args.checkpoint_before_deletes,
        "benchmark_revision": args.benchmark_revision,
        "engine_revision": args.engine_revision,
        "binary_sha256": binary_sha256,
        "fixture_sha256": fixture_sha256,
        "guest_manifest_sha256": guest_manifest_sha256,
        "physical_fixture_state": physical_fixture_state,
        "driver_source_sha256": hex_hash(include_bytes!("main.rs")),
        "matrix_sha256": hex_hash(include_bytes!("../../bench/terrabase-rust-acceptance-v1.md")),
        "dependency_lock_sha256": hex_hash(include_bytes!("../Cargo.lock")),
    });
    if engine == "terrabase-embedded" {
        receipt["adapter_source_sha256"] = Value::String(hex_hash(include_bytes!("terrabase.rs")));
        receipt["storage_source_sha256"] = Value::String(hex_hash(include_bytes!(
            "../../../terrabase/crates/tb-storage/src/local.rs"
        )));
        receipt["storage_run_source_sha256"] = Value::String(hex_hash(include_bytes!(
            "../../../terrabase/crates/tb-storage/src/run.rs"
        )));
        receipt["storage_work_source_sha256"] = Value::String(hex_hash(include_bytes!(
            "../../../terrabase/crates/tb-storage/src/work.rs"
        )));
        receipt["storage_floor_source_sha256"] = Value::String(hex_hash(include_bytes!(
            "../../../terrabase/crates/tb-storage/src/floor.rs"
        )));
        receipt["storage_api_sha256"] = Value::String(hex_hash(include_bytes!(
            "../../../terrabase/crates/tb-storage/src/lib.rs"
        )));
        receipt["core_types_sha256"] = Value::String(hex_hash(include_bytes!(
            "../../../terrabase/crates/tb-core/src/types.rs"
        )));
    }
    receipt
}

fn configure_sqlite(conn: &Connection, cache_kib: i64) -> Result<()> {
    conn.busy_timeout(std::time::Duration::from_secs(10))?;
    conn.set_prepared_statement_cache_capacity(32);
    conn.execute_batch(
        "PRAGMA page_size=4096; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;\
         PRAGMA temp_store=FILE; PRAGMA mmap_size=0;\
         PRAGMA wal_autocheckpoint=1000; PRAGMA foreign_keys=ON;",
    )?;
    conn.pragma_update(None, "cache_size", -cache_kib)?;
    Ok(())
}

fn needs_secondary_index(case: Case) -> bool {
    matches!(case, Case::IndexedEquality)
}

fn query_plan(conn: &Connection, case: Case) -> Result<Option<String>> {
    let sql = match case {
        Case::IndexedEquality => Some(
            "EXPLAIN QUERY PLAN SELECT id,group_key,payload FROM items WHERE group_key=?1 ORDER BY sort_key,id LIMIT 16",
        ),
        Case::OrderedRange => Some(
            "EXPLAIN QUERY PLAN SELECT id,payload FROM items WHERE id>=?1 ORDER BY id LIMIT 16",
        ),
        _ => None,
    };
    sql.map(|statement| {
        conn.query_row(statement, [0_i64], |row| row.get(3))
            .map_err(Into::into)
    })
    .transpose()
}

#[allow(clippy::cast_possible_wrap)] // run bounds rows and operations to ten million; every SQLite integer fits i64.
fn populate(conn: &mut Connection, args: &RunArgs) -> Result<String> {
    let mut digest = Sha256::new();
    let tx = conn.transaction()?;
    {
        let indexed = needs_secondary_index(args.case);
        let mut stmt = tx.prepare_cached(if indexed {
            "INSERT INTO items(id,group_key,sort_key,payload,doc) VALUES (?1,?2,?3,?4,jsonb(?5))"
        } else {
            "INSERT INTO items(id,payload) VALUES (?1,?2)"
        })?;
        for id in 0..args.rows {
            let payload = body(args.seed, id, args.body_bytes);
            digest.update(id.to_be_bytes());
            digest.update(&payload);
            if indexed {
                digest.update((id % 1024).to_be_bytes());
                digest.update((id / 1024).to_be_bytes());
                let doc = format!("{{\"nested\":{{\"group\":{}}}}}", id % 1024);
                stmt.execute(params![
                    id as i64,
                    (id % 1024) as i64,
                    (id / 1024) as i64,
                    payload,
                    doc
                ])?;
            } else {
                stmt.execute(params![id as i64, payload])?;
            }
        }
    }
    tx.commit()?;
    Ok(format!("{:x}", digest.finalize()))
}

#[allow(clippy::too_many_lines, clippy::cast_possible_wrap)] // Exhaustive per-case SQL with keys bounded by run validation.
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
            let group_u64 = selected % 1024;
            let group = i64::try_from(group_u64)?;
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
                    got_group == group && id % 1024 == group_u64,
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
            let mut stmt = conn.prepare_cached("INSERT INTO items(id,payload) VALUES (?1,?2)")?;
            ensure!(
                stmt.execute(params![id as i64, payload])? == 1,
                "insert changed wrong number of rows"
            );
            Ok(id)
        }
        Case::BatchInsert => {
            conn.execute_batch("BEGIN IMMEDIATE")?;
            let result = (|| -> Result<()> {
                let mut stmt =
                    conn.prepare_cached("INSERT INTO items(id,payload) VALUES (?1,?2)")?;
                for offset in 0..16_u64 {
                    let id = args.rows + i * 16 + offset;
                    ensure!(
                        stmt.execute(params![
                            i64::try_from(id)?,
                            body(args.seed, id, args.body_bytes)
                        ])? == 1,
                        "batch insert changed wrong number of rows"
                    );
                }
                Ok(())
            })();
            if let Err(cause) = result {
                conn.execute_batch("ROLLBACK")?;
                return Err(cause);
            }
            conn.execute_batch("COMMIT")?;
            Ok(args.rows + i * 16)
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
            let mut stmt = conn.prepare_cached(
                "INSERT INTO items(id,payload) VALUES (?1,?2) \
                 ON CONFLICT(id) DO UPDATE SET payload=excluded.payload,rev=items.rev+1",
            )?;
            ensure!(
                stmt.execute(params![id as i64, payload])? == 1,
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
        Case::Reopen => {
            let mut stmt = conn.prepare_cached("SELECT payload FROM items WHERE id=0")?;
            let payload: Vec<u8> = stmt.query_row([], |row| row.get(0))?;
            ensure!(
                payload == body(args.seed, 0, args.body_bytes),
                "wrong first query after reopen"
            );
            Ok(0)
        }
        Case::DeletedHistory => {
            let mut stmt = conn.prepare_cached("SELECT id FROM items ORDER BY id LIMIT 1")?;
            let first: Option<i64> = stmt.query_row([], |row| row.get(0)).optional()?;
            let expected = if args.sparse_tail {
                Some(i64::try_from(args.rows - 1)?)
            } else {
                None
            };
            ensure!(first == expected, "wrong deleted-history scan result");
            Ok(first.map_or(Ok(0), u64::try_from)?)
        }
    }
}

fn expected_final(args: &RunArgs) -> (u64, u64) {
    match args.case {
        Case::Insert => (args.rows + args.operations, 0),
        Case::BatchInsert => (args.rows + args.operations * 16, 0),
        Case::Delete => (args.rows - args.operations, 0),
        Case::DeletedHistory => (u64::from(args.sparse_tail), 0),
        Case::Update | Case::ReadModifyWrite => (args.rows, args.operations),
        Case::Upsert => (args.rows + args.operations / 2, args.operations.div_ceil(2)),
        Case::Mixed => (args.rows, args.operations.div_ceil(5)),
        _ => (args.rows, 0),
    }
}

fn verify_final(conn: &Connection, args: &RunArgs) -> Result<String> {
    let mut revisions = vec![0_u32; usize::try_from(args.rows)?];
    for i in 0..args.operations {
        let affects_existing = match args.case {
            Case::Update | Case::ReadModifyWrite => true,
            Case::Mixed => i % 5 == 0,
            Case::Upsert => i % 2 == 0,
            _ => false,
        };
        if affects_existing {
            let selected = splitmix64(args.seed ^ i) % args.rows;
            revisions[usize::try_from(selected)?] += 1;
        }
    }

    let mut digest = Sha256::new();
    let indexed = needs_secondary_index(args.case);
    let mut stmt = conn.prepare(if indexed {
        "SELECT id,group_key,sort_key,payload,rev FROM items ORDER BY id"
    } else {
        "SELECT id,payload,rev FROM items ORDER BY id"
    })?;
    let mut rows = stmt.query([])?;
    let mut count = 0_u64;
    while let Some(row) = rows.next()? {
        let id = u64::try_from(row.get::<_, i64>(0)?)?;
        if indexed {
            let group = u64::try_from(row.get::<_, i64>(1)?)?;
            let sort = u64::try_from(row.get::<_, i64>(2)?)?;
            ensure!(
                group == id % 1024 && sort == id / 1024,
                "wrong indexed fields for {id}"
            );
        }
        let payload: Vec<u8> = row.get(if indexed { 3 } else { 1 })?;
        let revision = u64::try_from(row.get::<_, i64>(if indexed { 4 } else { 2 })?)?;
        let expected_present = match args.case {
            Case::Delete => id >= args.operations && id < args.rows,
            Case::Insert => id < args.rows + args.operations,
            Case::BatchInsert => id < args.rows + args.operations * 16,
            Case::DeletedHistory => args.sparse_tail && id == args.rows - 1,
            Case::Upsert => {
                id < args.rows || (id < args.rows + args.operations && (id - args.rows) % 2 == 1)
            }
            _ => id < args.rows,
        };
        ensure!(expected_present, "unexpected final key {id}");
        ensure!(
            payload == body(args.seed, id, args.body_bytes),
            "wrong final payload for {id}"
        );
        let expected_revision = if id < args.rows {
            u64::from(revisions[usize::try_from(id)?])
        } else {
            0
        };
        ensure!(
            revision == expected_revision,
            "wrong final revision for {id}"
        );
        digest.update(id.to_be_bytes());
        digest.update(u64::try_from(payload.len())?.to_be_bytes());
        digest.update(&payload);
        digest.update(revision.to_be_bytes());
        count += 1;
    }
    ensure!(
        count == expected_final(args).0,
        "wrong final key cardinality"
    );
    Ok(format!("{:x}", digest.finalize()))
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

fn sample_scan_phase(args: &RunArgs, operation: u64) -> Option<&'static str> {
    if !matches!(args.case, Case::DeletedHistory) || args.operations == 1 {
        None
    } else if operation == 0 {
        Some("first_after_fixture")
    } else {
        Some("warm_repeat")
    }
}

fn warm_scan_samples(args: &RunArgs, latency: &[u64]) -> Option<Vec<u64>> {
    if !matches!(args.case, Case::DeletedHistory) || args.operations == 1 {
        return None;
    }
    let mut warm = latency.iter().skip(1).copied().collect::<Vec<_>>();
    warm.sort_unstable();
    Some(warm)
}

fn latency_quantiles(latency: &[u64]) -> Latency {
    Latency {
        p50_ns: percentile(latency, 500, 1000),
        p95_ns: percentile(latency, 950, 1000),
        p99_ns: percentile(latency, 990, 1000),
        p999_ns: percentile(latency, 999, 1000),
    }
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
    let mut buffer = [0_u8; 16 * 1024];
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
    use super::{
        Case, DeletedHistoryWatchdog, RunArgs, body, deleted_history_timeout_receipt,
        expected_final, percentile, sample_scan_phase, warm_scan_samples,
    };
    use std::path::PathBuf;
    use std::sync::mpsc;
    use std::time::{Duration, Instant};

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
            clients: 1,
            checkpoint_fixture: false,
            checkpoint_before_deletes: false,
            sparse_tail: false,
            scan_diagnostics: false,
            database: PathBuf::new(),
            samples: PathBuf::new(),
            receipt: PathBuf::new(),
            guest_manifest: PathBuf::new(),
            benchmark_revision: String::new(),
            engine_revision: String::new(),
        };
        assert_eq!(expected_final(&args), (1026, 3));
    }

    fn deleted_history_args() -> RunArgs {
        RunArgs {
            case: Case::DeletedHistory,
            rows: 1024,
            operations: 3,
            body_bytes: 1,
            seed: 7,
            trial: 1,
            clients: 1,
            checkpoint_fixture: true,
            checkpoint_before_deletes: false,
            sparse_tail: false,
            scan_diagnostics: false,
            database: PathBuf::new(),
            samples: PathBuf::new(),
            receipt: PathBuf::new(),
            guest_manifest: PathBuf::new(),
            benchmark_revision: "benchmark-revision".into(),
            engine_revision: "engine-revision".into(),
        }
    }

    #[test]
    fn repeated_deleted_history_scans_have_warm_samples_without_a_run_deadline() {
        let (tx, rx) = mpsc::channel();
        let watchdog = DeletedHistoryWatchdog::new(Duration::from_millis(250), move |active| {
            tx.send(active.operation)
                .expect("report unexpected timeout");
        });
        for operation in 0..4 {
            watchdog.arm(operation, operation);
            std::thread::sleep(Duration::from_millis(70));
            assert!(watchdog.disarm(), "scan {operation} timed out");
        }
        watchdog.finish().expect("join watchdog");
        assert!(rx.try_recv().is_err());

        let args = deleted_history_args();
        assert_eq!(sample_scan_phase(&args, 0), Some("first_after_fixture"));
        assert_eq!(sample_scan_phase(&args, 1), Some("warm_repeat"));
        assert_eq!(warm_scan_samples(&args, &[90, 10, 20]), Some(vec![10, 20]));
        let mut one_scan = args;
        one_scan.operations = 1;
        assert_eq!(sample_scan_phase(&one_scan, 0), None);
        assert_eq!(warm_scan_samples(&one_scan, &[90]), None);
    }

    #[test]
    fn timed_out_scan_reports_its_index_completed_count_and_identities() {
        let (tx, rx) = mpsc::channel();
        let watchdog = DeletedHistoryWatchdog::new(Duration::from_millis(20), move |active| {
            tx.send(active).expect("report timeout");
        });
        watchdog.arm(2, 2);
        let active = rx
            .recv_timeout(Duration::from_secs(1))
            .expect("watchdog timeout");
        assert!(!watchdog.disarm());
        watchdog.finish().expect("join watchdog");
        let args = deleted_history_args();
        let receipt = deleted_history_timeout_receipt(
            &args,
            "terrabase-embedded",
            "binary-hash",
            "fixture-hash",
            "manifest-hash",
            None,
            active,
        );
        assert_eq!(receipt["status"], "timed_out");
        assert_eq!(receipt["operations_requested"], 3);
        assert_eq!(receipt["operations_completed"], 2);
        assert_eq!(receipt["timeout_operation"], 2);
        assert_eq!(receipt["fixture_sha256"], "fixture-hash");
        assert_eq!(receipt["guest_manifest_sha256"], "manifest-hash");
        assert_eq!(receipt["binary_sha256"], "binary-hash");
        assert!(receipt["adapter_source_sha256"].as_str().is_some());
        assert!(receipt["storage_source_sha256"].as_str().is_some());
        assert!(receipt["storage_run_source_sha256"].as_str().is_some());
        assert!(receipt["storage_work_source_sha256"].as_str().is_some());
        assert!(receipt["storage_floor_source_sha256"].as_str().is_some());
        assert!(receipt["storage_api_sha256"].as_str().is_some());
        assert!(receipt["core_types_sha256"].as_str().is_some());
    }

    #[test]
    fn disarm_at_expired_deadline_cannot_report_scan_success() {
        let (tx, rx) = mpsc::channel();
        let watchdog = DeletedHistoryWatchdog::new(Duration::from_secs(30), move |active| {
            tx.send(active.operation).expect("report timeout");
        });
        watchdog.arm(1, 1);
        let (lock, wake) = &*watchdog.shared;
        let mut state = lock.lock().expect("watchdog state lock");
        state.active.as_mut().expect("armed scan").deadline = Instant::now()
            .checked_sub(Duration::from_nanos(1))
            .expect("monotonic clock supports one nanosecond subtraction");
        wake.notify_one();
        drop(state);
        assert!(!watchdog.disarm());
        assert_eq!(rx.recv_timeout(Duration::from_secs(1)).unwrap(), 1);
        watchdog.finish().expect("join watchdog");
    }
}
