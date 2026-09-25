//! Real embedded local-durable storage caller for the registered primary cases.

use std::fs::{self, OpenOptions};
use std::io::{BufWriter, Write};
use std::time::{Instant, SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result, bail, ensure};
use serde::Serialize;
use serde_json::Value;
use sha2::{Digest, Sha256};
use tb_core::{
    BudgetConfig, Key, KeyBound, KeyRange, MemoryBudget, TenantId, TransactionId, WorkerId,
};
use tb_storage::{Mutation, ScanWork, Storage, StorageConfig};

use super::{
    Case, Latency, MATRIX_VERSION, MAX_DELETED_HISTORY_SCANS, RunArgs, Sample, ScanDiagnostics,
    ScanSnapshot, arm_deleted_history_timeout, body, expected_final, hash_file, hex_hash,
    latency_quantiles, ns, proc_key, sample_scan_phase, splitmix64, validate_manifest,
    warm_scan_samples,
};

const BUDGET_BYTES: usize = 128 * 1024 * 1024;
const BUDGET_HEADROOM_BYTES: usize = 32 * 1024 * 1024;
const FIXTURE_BATCH: u64 = 128;
const RANGE_LIMIT: usize = 16;

#[derive(Serialize)]
struct EngineCounters {
    visible_sequence: u64,
    durable_sequence: u64,
    checkpoint_sequence: u64,
    wal_bytes: u64,
    immutable_runs: usize,
    mutable_bytes: usize,
    live_transaction_ids: usize,
    disk_bytes: u64,
    admitted_bytes: usize,
    admitted_items: usize,
    process_budget_bytes: usize,
    process_budget_headroom_bytes: usize,
    peak_rss_kib: Option<u64>,
    current_rss_kib: Option<u64>,
    pss_kib: Option<u64>,
    process_read_bytes: Option<u64>,
    process_write_bytes: Option<u64>,
}

#[derive(Serialize)]
struct ScanWorkDelta {
    run_file_opens: Option<u64>,
    run_metadata_calls: Option<u64>,
    run_header_bytes_read: Option<u64>,
    run_footer_bytes_read: Option<u64>,
    run_directory_bytes_read: Option<u64>,
    run_block_bytes_read: Option<u64>,
    run_block_cache_evictions: Option<u64>,
    run_block_rereads: Option<u64>,
    mutable_keys_examined: Option<u64>,
    next_key_descriptors_examined: Option<u64>,
    next_key_records_examined: Option<u64>,
    point_lookup_calls: Option<u64>,
    point_descriptors_examined: Option<u64>,
    point_records_examined: Option<u64>,
}

impl ScanWorkDelta {
    fn between(before: ScanWork, after: ScanWork) -> Self {
        Self {
            run_file_opens: after.run_file_opens.checked_sub(before.run_file_opens),
            run_metadata_calls: after
                .run_metadata_calls
                .checked_sub(before.run_metadata_calls),
            run_header_bytes_read: after
                .run_header_bytes_read
                .checked_sub(before.run_header_bytes_read),
            run_footer_bytes_read: after
                .run_footer_bytes_read
                .checked_sub(before.run_footer_bytes_read),
            run_directory_bytes_read: after
                .run_directory_bytes_read
                .checked_sub(before.run_directory_bytes_read),
            run_block_bytes_read: after
                .run_block_bytes_read
                .checked_sub(before.run_block_bytes_read),
            run_block_cache_evictions: after
                .run_block_cache_evictions
                .checked_sub(before.run_block_cache_evictions),
            run_block_rereads: after
                .run_block_rereads
                .checked_sub(before.run_block_rereads),
            mutable_keys_examined: after
                .mutable_keys_examined
                .checked_sub(before.mutable_keys_examined),
            next_key_descriptors_examined: after
                .next_key_descriptors_examined
                .checked_sub(before.next_key_descriptors_examined),
            next_key_records_examined: after
                .next_key_records_examined
                .checked_sub(before.next_key_records_examined),
            point_lookup_calls: after
                .point_lookup_calls
                .checked_sub(before.point_lookup_calls),
            point_descriptors_examined: after
                .point_descriptors_examined
                .checked_sub(before.point_descriptors_examined),
            point_records_examined: after
                .point_records_examined
                .checked_sub(before.point_records_examined),
        }
    }
}

#[derive(Serialize)]
struct FixtureState {
    label: &'static str,
    immutable_runs: usize,
    checkpoint_sequence: u64,
    mutable_bytes: usize,
    wal_bytes: u64,
    visible_sequence: u64,
}

impl FixtureState {
    fn capture(storage: &Storage) -> Self {
        let stats = storage.stats();
        let label = match (stats.immutable_runs, stats.mutable_bytes > 0) {
            (0, true) if stats.checkpoint.get() == 0 => "mutable_only",
            (0, _) => "no_immutable_runs",
            (1, false) => "one_immutable_run",
            (1, true) => "one_immutable_run_plus_mutable",
            (_, false) => "multiple_immutable_runs",
            (_, true) => "multiple_immutable_runs_plus_mutable",
        };
        Self {
            label,
            immutable_runs: stats.immutable_runs,
            checkpoint_sequence: stats.checkpoint.get(),
            mutable_bytes: stats.mutable_bytes,
            wal_bytes: stats.wal_bytes,
            visible_sequence: stats.visible.get(),
        }
    }
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
    clients: u32,
    durability: &'static str,
    schema_kind: &'static str,
    checkpoint_fixture: bool,
    checkpoint_before_deletes: bool,
    sparse_tail: bool,
    scan_diagnostics_enabled: bool,
    binary_sha256: String,
    driver_source_sha256: String,
    matrix_sha256: String,
    dependency_lock_sha256: String,
    adapter_source_sha256: String,
    storage_source_sha256: String,
    storage_run_source_sha256: String,
    storage_work_source_sha256: String,
    storage_floor_source_sha256: String,
    storage_api_sha256: String,
    core_types_sha256: String,
    guest_manifest_sha256: String,
    guest_manifest: &'a Value,
    benchmark_revision: &'a str,
    engine_revision: &'a str,
    fixture_sha256: String,
    physical_fixture_state: FixtureState,
    observed_checksum: u64,
    elapsed_ns: u64,
    reopen_to_first_query_ns: Option<u64>,
    completed_ops_per_second: f64,
    latency: Latency,
    #[serde(skip_serializing_if = "Option::is_none")]
    warm_scan_samples: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    warm_scan_latency: Option<Latency>,
    final_row_count: Option<u64>,
    final_revision_sum: Option<u64>,
    final_state_sha256: Option<String>,
    counters: EngineCounters,
    scan_diagnostics: Option<ScanDiagnostics>,
    scan_work: Option<ScanWorkDelta>,
    samples_path: &'a std::path::Path,
    database_path: &'a std::path::Path,
}

#[allow(clippy::too_many_lines)] // Preserve an auditable setup/measure/verify/receipt sequence.
pub(super) fn run(args: &RunArgs) -> Result<()> {
    ensure!(
        (1024..=10_000_000).contains(&args.rows),
        "rows must be in 1024..=10000000"
    );
    ensure!(
        (1..=10_000_000).contains(&args.operations),
        "operations must be in 1..=10000000"
    );
    ensure!(
        (1..=1_048_568).contains(&args.body_bytes),
        "body_bytes must be in 1..=1048568 to include logical revision"
    );
    ensure!(args.trial > 0, "trial must be positive");
    ensure!(
        !args.benchmark_revision.trim().is_empty() && !args.engine_revision.trim().is_empty(),
        "source revisions are required"
    );
    ensure!(
        !args.database.exists() && !args.samples.exists() && !args.receipt.exists(),
        "database, samples and receipt paths must be fresh"
    );
    ensure!(
        args.database != args.samples
            && args.database != args.receipt
            && args.samples != args.receipt,
        "output paths must differ"
    );
    if matches!(args.case, Case::IndexedEquality) {
        bail!(
            "indexed-equality is unsupported by the current storage API; no candidate receipt created"
        );
    }
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
    if matches!(
        args.case,
        Case::Insert
            | Case::BatchInsert
            | Case::Update
            | Case::Delete
            | Case::Upsert
            | Case::ReadModifyWrite
            | Case::Mixed
    ) {
        ensure!(
            args.operations <= 50_000,
            "current local engine retry table supports at most 50000 measured mutations in this driver"
        );
    }

    let manifest_bytes = fs::read(&args.guest_manifest).context("read guest manifest")?;
    let manifest: Value =
        serde_json::from_slice(&manifest_bytes).context("parse guest manifest")?;
    validate_manifest(&manifest)?;
    let manifest_sha256 = hex_hash(&manifest_bytes);
    let binary_sha256 = hash_file(&std::env::current_exe()?)?;
    let budget = MemoryBudget::new(BudgetConfig::new(BUDGET_BYTES, BUDGET_HEADROOM_BYTES)?);
    let config = StorageConfig::local(&args.database, TenantId::from_bytes([1; 16]), WorkerId(1));
    let mut storage =
        Storage::open(config, budget.clone()).context("open embedded TerraBase shard")?;
    let expires_at = now_ms()?
        .checked_add(3_600_000)
        .context("transaction expiry overflow")?;
    let fixture_sha256 = populate(&storage, args, expires_at)?;
    if matches!(args.case, Case::DeletedHistory) {
        if args.checkpoint_before_deletes {
            storage
                .checkpoint()
                .context("checkpoint inserted history")?;
        }
        let delete_before = if args.sparse_tail {
            args.rows - 1
        } else {
            args.rows
        };
        let mut start = 0_u64;
        while start < delete_before {
            let end = (start + FIXTURE_BATCH).min(delete_before);
            let mut mutations = Vec::with_capacity(usize::try_from(end - start)?);
            for id in start..end {
                mutations.push(Mutation::Delete { key: key(id)? });
            }
            checked_commit(
                &storage,
                storage.snapshot(),
                token(args.seed, 500_000 + start / FIXTURE_BATCH, expires_at),
                &mutations,
            )?;
            start = end;
        }
    }
    if args.checkpoint_fixture {
        storage.checkpoint().context("checkpoint fixture")?;
    }
    let fixture_state = FixtureState::capture(&storage);

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
    let mut scan_work = None;
    let total_start = Instant::now();
    let scan_guard = arm_deleted_history_timeout(
        args,
        "terrabase-embedded",
        &binary_sha256,
        &fixture_sha256,
        &manifest_sha256,
        Some(serde_json::to_value(&fixture_state)?),
    );
    if matches!(args.case, Case::Reopen) {
        drop(storage);
        storage = Storage::open(
            StorageConfig::local(&args.database, TenantId::from_bytes([1; 16]), WorkerId(1)),
            budget.clone(),
        )
        .context("reopen embedded TerraBase shard")?;
    }
    for i in 0..args.operations {
        let work_before = args.scan_diagnostics.then(|| storage.stats().scan_work);
        let diagnostic_before = args.scan_diagnostics.then(ScanSnapshot::capture_before);
        if let Some(guard) = &scan_guard {
            guard.arm(i, completed);
        }
        let start = if i == 0 && matches!(args.case, Case::Reopen) {
            total_start
        } else {
            Instant::now()
        };
        let result = operation(&storage, args, i, expires_at);
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
        if let Some(before) = work_before {
            scan_work = Some(ScanWorkDelta::between(before, storage.stats().scan_work));
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

    let final_state = verify_final(&storage, args);
    let (final_row_count, final_revision_sum, final_state_sha256) = match final_state {
        Ok((count, revisions, hash)) => {
            let expected = expected_final(args);
            if error.is_none() && (count, revisions) != expected {
                error = Some(format!(
                    "final count/revisions {count}/{revisions}, expected {}/{}",
                    expected.0, expected.1
                ));
            }
            (Some(count), Some(revisions), Some(hash))
        }
        Err(cause) => {
            if error.is_none() {
                error = Some(format!("logical final-state verification failed: {cause}"));
            }
            (None, None, None)
        }
    };
    let stats = storage.stats();
    let admission = budget.snapshot();
    let counters = EngineCounters {
        visible_sequence: stats.visible.get(),
        durable_sequence: stats.durable.get(),
        checkpoint_sequence: stats.checkpoint.get(),
        wal_bytes: stats.wal_bytes,
        immutable_runs: stats.immutable_runs,
        mutable_bytes: stats.mutable_bytes,
        live_transaction_ids: stats.live_transaction_ids,
        disk_bytes: stats.disk_bytes,
        admitted_bytes: admission.reserved_bytes,
        admitted_items: admission.reserved_items,
        process_budget_bytes: BUDGET_BYTES,
        process_budget_headroom_bytes: BUDGET_HEADROOM_BYTES,
        peak_rss_kib: proc_key("/proc/self/status", "VmHWM:", 1),
        current_rss_kib: proc_key("/proc/self/status", "VmRSS:", 1),
        pss_kib: proc_key("/proc/self/smaps_rollup", "Pss:", 1),
        process_read_bytes: proc_key("/proc/self/io", "read_bytes:", 1),
        process_write_bytes: proc_key("/proc/self/io", "write_bytes:", 1),
    };
    let receipt = Receipt {
        schema_version: 1, matrix_version: MATRIX_VERSION, engine: "terrabase-embedded",
        case: args.case, status: if error.is_some() { "failed" } else { "terrabase_candidate_only" },
        error: error.clone(), rows: args.rows, operations_requested: args.operations,
        operations_completed: completed, errors: u64::from(error.is_some()), body_bytes: args.body_bytes,
        seed: args.seed, trial: args.trial, clients: 1, durability: "local-durable",
        schema_kind: "primary-row", checkpoint_fixture: args.checkpoint_fixture,
        checkpoint_before_deletes: args.checkpoint_before_deletes, sparse_tail: args.sparse_tail,
        scan_diagnostics_enabled: args.scan_diagnostics,
        binary_sha256, driver_source_sha256: hex_hash(include_bytes!("main.rs")),
        matrix_sha256: hex_hash(include_bytes!("../../bench/terrabase-rust-acceptance-v1.md")),
        dependency_lock_sha256: hex_hash(include_bytes!("../Cargo.lock")),
        adapter_source_sha256: hex_hash(include_bytes!("terrabase.rs")),
        storage_source_sha256: hex_hash(include_bytes!("../../../terrabase/crates/tb-storage/src/local.rs")),
        storage_run_source_sha256: hex_hash(include_bytes!("../../../terrabase/crates/tb-storage/src/run.rs")),
        storage_work_source_sha256: hex_hash(include_bytes!("../../../terrabase/crates/tb-storage/src/work.rs")),
        storage_floor_source_sha256: hex_hash(include_bytes!("../../../terrabase/crates/tb-storage/src/floor.rs")),
        storage_api_sha256: hex_hash(include_bytes!("../../../terrabase/crates/tb-storage/src/lib.rs")),
        core_types_sha256: hex_hash(include_bytes!("../../../terrabase/crates/tb-core/src/types.rs")),
        guest_manifest_sha256: manifest_sha256, guest_manifest: &manifest,
        benchmark_revision: &args.benchmark_revision, engine_revision: &args.engine_revision,
        fixture_sha256, physical_fixture_state: fixture_state, observed_checksum, elapsed_ns,
        reopen_to_first_query_ns: if matches!(args.case, Case::Reopen) { latency.first().copied() } else { None },
        #[allow(clippy::cast_precision_loss)] // The exact count and elapsed nanoseconds are also retained.
        completed_ops_per_second: if elapsed_ns == 0 { 0.0 } else { completed as f64 * 1e9 / elapsed_ns as f64 },
        latency: latency_quantiles(&latency),
        warm_scan_samples: warm_samples.as_ref().map(|samples| samples.len() as u64),
        warm_scan_latency: warm_samples.as_ref().filter(|samples| !samples.is_empty()).map(|samples| latency_quantiles(samples)),
        final_row_count, final_revision_sum, final_state_sha256, counters, scan_diagnostics, scan_work,
        samples_path: &args.samples, database_path: &args.database,
    };
    let receipt_file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&args.receipt)
        .context("create TerraBase receipt")?;
    serde_json::to_writer_pretty(receipt_file, &receipt)?;
    if let Some(cause) = error {
        bail!(
            "TerraBase candidate failed; receipt at {}: {cause}",
            args.receipt.display()
        );
    }
    println!("{}", args.receipt.display());
    Ok(())
}

fn now_ms() -> Result<u64> {
    Ok(u64::try_from(
        SystemTime::now().duration_since(UNIX_EPOCH)?.as_millis(),
    )?)
}

fn token(seed: u64, serial: u64, expires_at: u64) -> TransactionId {
    let mut nonce = [0_u8; 16];
    nonce[..8].copy_from_slice(&seed.to_be_bytes());
    nonce[8..].copy_from_slice(&serial.to_be_bytes());
    TransactionId::new(expires_at, nonce)
}

fn key(id: u64) -> Result<Key> {
    Ok(Key::new(id.to_be_bytes().to_vec())?)
}

fn decoded_id(key: &Key) -> Result<u64> {
    let bytes: [u8; 8] = key.as_bytes().try_into().context("unexpected key width")?;
    Ok(u64::from_be_bytes(bytes))
}

fn value(seed: u64, id: u64, size: usize, revision: u64) -> Vec<u8> {
    let mut value = Vec::with_capacity(size + 8);
    value.extend_from_slice(&revision.to_be_bytes());
    value.extend_from_slice(&body(seed, id, size));
    value
}

fn decode_value(bytes: &[u8], size: usize) -> Result<(u64, &[u8])> {
    ensure!(bytes.len() == size + 8, "unexpected encoded value length");
    let revision = u64::from_be_bytes(bytes[..8].try_into()?);
    Ok((revision, &bytes[8..]))
}

fn checked_commit(
    storage: &Storage,
    snapshot: tb_core::ReadSnapshot,
    id: TransactionId,
    mutations: &[Mutation],
) -> Result<()> {
    let receipt = storage.commit_batch(snapshot, id, mutations)?;
    ensure!(
        receipt.frontiers.durable == Some(receipt.commit_seq)
            && receipt.frontiers.applied == Some(receipt.commit_seq),
        "commit did not cross local durable/visible barrier"
    );
    Ok(())
}

fn populate(storage: &Storage, args: &RunArgs, expires_at: u64) -> Result<String> {
    let mut digest = Sha256::new();
    let mut start = 0_u64;
    while start < args.rows {
        let end = (start + FIXTURE_BATCH).min(args.rows);
        let mut mutations = Vec::with_capacity(usize::try_from(end - start)?);
        for id in start..end {
            let payload = body(args.seed, id, args.body_bytes);
            digest.update(id.to_be_bytes());
            digest.update(&payload);
            mutations.push(Mutation::Put {
                key: key(id)?,
                value: value(args.seed, id, args.body_bytes, 0),
            });
        }
        let snapshot = storage.snapshot();
        checked_commit(
            storage,
            snapshot,
            token(args.seed, start / FIXTURE_BATCH, expires_at),
            &mutations,
        )?;
        start = end;
    }
    Ok(format!("{:x}", digest.finalize()))
}

#[allow(clippy::too_many_lines)] // Direct mapping of registered logical operations to the embedded API.
fn operation(storage: &Storage, args: &RunArgs, i: u64, expires_at: u64) -> Result<u64> {
    let selected = splitmix64(args.seed ^ i) % args.rows;
    let snapshot = storage.snapshot();
    let txid = token(args.seed, 1_000_000 + i, expires_at);
    match args.case {
        Case::PointHitRandom | Case::PointHitSequential | Case::PointMissRandom => {
            let id = if matches!(args.case, Case::PointHitSequential) {
                i % args.rows
            } else if matches!(args.case, Case::PointMissRandom) {
                args.rows + selected
            } else {
                selected
            };
            let found = storage.get(snapshot, &key(id)?)?;
            if matches!(args.case, Case::PointMissRandom) {
                ensure!(found.is_none(), "unexpected hit for missing key {id}");
            } else {
                let found = found.context("missing point hit")?;
                let (revision, payload) = decode_value(found.bytes(), args.body_bytes)?;
                ensure!(
                    revision == 0 && payload == body(args.seed, id, args.body_bytes),
                    "wrong point value for key {id}"
                );
            }
            Ok(id)
        }
        Case::OrderedRange => {
            let range = KeyRange::new(KeyBound::Included(key(selected)?), KeyBound::Unbounded)?;
            let page = storage.scan(snapshot, &range, RANGE_LIMIT)?;
            let mut checksum = 0_u64;
            for (offset, row) in page.rows().iter().enumerate() {
                let id = decoded_id(&row.key)?;
                ensure!(id == selected + offset as u64, "wrong ordered range key");
                let (revision, payload) = decode_value(&row.value, args.body_bytes)?;
                ensure!(
                    revision == 0 && payload == body(args.seed, id, args.body_bytes),
                    "wrong range value"
                );
                checksum = checksum.wrapping_add(id);
            }
            ensure!(
                page.rows().len() as u64 == (args.rows - selected).min(RANGE_LIMIT as u64),
                "wrong range cardinality"
            );
            Ok(checksum)
        }
        Case::Insert => {
            let id = args.rows + i;
            checked_commit(
                storage,
                snapshot,
                txid,
                &[Mutation::Put {
                    key: key(id)?,
                    value: value(args.seed, id, args.body_bytes, 0),
                }],
            )?;
            Ok(id)
        }
        Case::BatchInsert => {
            let mut mutations = Vec::with_capacity(16);
            for offset in 0..16_u64 {
                let id = args.rows + i * 16 + offset;
                mutations.push(Mutation::Put {
                    key: key(id)?,
                    value: value(args.seed, id, args.body_bytes, 0),
                });
            }
            checked_commit(storage, snapshot, txid, &mutations)?;
            Ok(args.rows + i * 16)
        }
        Case::Update | Case::ReadModifyWrite => {
            let old = storage
                .get(snapshot, &key(selected)?)?
                .context("missing update key")?;
            let (revision, payload) = decode_value(old.bytes(), args.body_bytes)?;
            ensure!(
                payload == body(args.seed, selected, args.body_bytes),
                "update read wrong body"
            );
            checked_commit(
                storage,
                snapshot,
                txid,
                &[Mutation::Put {
                    key: key(selected)?,
                    value: value(args.seed, selected, args.body_bytes, revision + 1),
                }],
            )?;
            Ok(selected)
        }
        Case::Delete => {
            checked_commit(
                storage,
                snapshot,
                txid,
                &[Mutation::Delete { key: key(i)? }],
            )?;
            Ok(i)
        }
        Case::Upsert => {
            let id = if i % 2 == 0 { selected } else { args.rows + i };
            let revision = storage
                .get(snapshot, &key(id)?)?
                .map(|old| {
                    decode_value(old.bytes(), args.body_bytes).map(|(revision, _)| revision + 1)
                })
                .transpose()?
                .unwrap_or(0);
            checked_commit(
                storage,
                snapshot,
                txid,
                &[Mutation::Put {
                    key: key(id)?,
                    value: value(args.seed, id, args.body_bytes, revision),
                }],
            )?;
            Ok(id)
        }
        Case::Mixed => {
            let found = storage
                .get(snapshot, &key(selected)?)?
                .context("missing mixed key")?;
            let (revision, payload) = decode_value(found.bytes(), args.body_bytes)?;
            ensure!(
                payload == body(args.seed, selected, args.body_bytes),
                "mixed read wrong body"
            );
            if i % 5 == 0 {
                checked_commit(
                    storage,
                    snapshot,
                    txid,
                    &[Mutation::Put {
                        key: key(selected)?,
                        value: value(args.seed, selected, args.body_bytes, revision + 1),
                    }],
                )?;
            }
            Ok(selected)
        }
        Case::Reopen => {
            let found = storage
                .get(snapshot, &key(0)?)?
                .context("missing first key after reopen")?;
            let (revision, payload) = decode_value(found.bytes(), args.body_bytes)?;
            ensure!(
                revision == 0 && payload == body(args.seed, 0, args.body_bytes),
                "wrong first query after reopen"
            );
            Ok(0)
        }
        Case::DeletedHistory => {
            let page = storage.scan(snapshot, &KeyRange::all(), 1)?;
            let first = page
                .rows()
                .first()
                .map(|row| decoded_id(&row.key))
                .transpose()?;
            let expected = if args.sparse_tail {
                Some(args.rows - 1)
            } else {
                None
            };
            ensure!(first == expected, "wrong deleted-history scan result");
            Ok(first.unwrap_or(0))
        }
        Case::IndexedEquality => bail!("indexed equality is unsupported by storage API"),
    }
}

fn verify_final(storage: &Storage, args: &RunArgs) -> Result<(u64, u64, String)> {
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
    let mut count = 0_u64;
    let mut revision_sum = 0_u64;
    let mut start = KeyBound::Unbounded;
    let page_limit = (8 * 1024 * 1024 / (args.body_bytes + 256)).clamp(1, 4096);
    loop {
        let range = KeyRange::new(start, KeyBound::Unbounded)?;
        let page = storage.scan(storage.snapshot(), &range, page_limit)?;
        if page.rows().is_empty() {
            break;
        }
        for row in page.rows() {
            let id = decoded_id(&row.key)?;
            let expected_present = match args.case {
                Case::Delete => id >= args.operations && id < args.rows,
                Case::Insert => id < args.rows + args.operations,
                Case::BatchInsert => id < args.rows + args.operations * 16,
                Case::DeletedHistory => args.sparse_tail && id == args.rows - 1,
                Case::Upsert => {
                    id < args.rows
                        || (id < args.rows + args.operations && (id - args.rows) % 2 == 1)
                }
                _ => id < args.rows,
            };
            ensure!(expected_present, "unexpected final key {id}");
            let (revision, payload) = decode_value(&row.value, args.body_bytes)?;
            ensure!(
                payload == body(args.seed, id, args.body_bytes),
                "wrong final body for {id}"
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
            digest.update(payload);
            digest.update(revision.to_be_bytes());
            count += 1;
            revision_sum += revision;
        }
        let last_key = page
            .rows()
            .last()
            .context("nonempty page has no last key")?
            .key
            .clone();
        start = KeyBound::Excluded(last_key);
    }
    Ok((count, revision_sum, format!("{:x}", digest.finalize())))
}
