use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use tb_core::{
    AckProfile, BudgetConfig, CommitSeq, Key, KeyRange, MemoryBudget, ReadSnapshot, TenantId,
    TransactionId, WorkerId,
};
use tb_format::{FormatLimits, WalOperation, encode_wal_frame_into};
use tb_storage::{Mutation, Storage, StorageConfig, StorageError};

static NEXT_TEMP_DIRECTORY: AtomicU64 = AtomicU64::new(0);
const POST_ACK_CHILD_ENV: &str = "TB_STORAGE_POST_ACK_CHILD";
#[cfg(feature = "slow-tests")]
const CRASH_CHILD_ENV: &str = "TB_STORAGE_CRASH_CHILD";
const CRASH_DIRECTORY_ENV: &str = "TB_STORAGE_PROCESS_TEST_DIRECTORY";
#[cfg(feature = "slow-tests")]
const CRASH_POINT_ENV: &str = "TB_STORAGE_CRASH_POINT";

struct TempShard {
    path: PathBuf,
}

impl TempShard {
    fn new() -> Self {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock is after the Unix epoch")
            .as_nanos();
        let ordinal = NEXT_TEMP_DIRECTORY.fetch_add(1, Ordering::Relaxed);
        let path = std::env::temp_dir().join(format!(
            "terrabase-storage-{}-{nanos}-{ordinal}",
            std::process::id()
        ));
        Self { path }
    }

    fn path(&self) -> &Path {
        &self.path
    }
}

impl Drop for TempShard {
    fn drop(&mut self) {
        if self.path.exists() {
            fs::remove_dir_all(&self.path).expect("remove temporary shard directory");
        }
    }
}

fn config(path: &Path) -> StorageConfig {
    StorageConfig::local(path, TenantId::from_bytes([1; 16]), WorkerId(7))
        .with_clock(Arc::new(|| 1_000))
}

fn budget() -> MemoryBudget {
    MemoryBudget::new(BudgetConfig::new(64 * 1024 * 1024, 0).expect("valid memory budget"))
}

fn key(bytes: &[u8]) -> Key {
    Key::new(bytes.to_vec()).expect("valid test key")
}

fn put(key_bytes: &[u8], value: &[u8]) -> Mutation {
    Mutation::Put {
        key: key(key_bytes),
        value: value.to_vec(),
    }
}

fn token(nonce: u8, expires_at_unix_ms: u64) -> TransactionId {
    TransactionId::new(expires_at_unix_ms, [nonce; 16])
}

fn read(storage: &Storage, snapshot: ReadSnapshot, key_bytes: &[u8]) -> Option<Vec<u8>> {
    storage
        .get(snapshot, &key(key_bytes))
        .expect("read from shard")
        .map(|version| version.bytes().to_vec())
}

fn append(path: &Path, bytes: &[u8]) {
    let mut wal = OpenOptions::new()
        .append(true)
        .open(path.join("wal.log"))
        .expect("open WAL for tail fixture");
    wal.write_all(bytes).expect("append WAL fixture bytes");
    wal.sync_all().expect("sync WAL fixture bytes");
}

#[test]
fn child_process_exits_immediately_after_local_durable_ack() {
    if std::env::var_os(POST_ACK_CHILD_ENV).is_none() {
        return;
    }
    let path = std::env::var(CRASH_DIRECTORY_ENV).expect("parent sets shard directory");
    let storage = Storage::open(config(Path::new(&path)), budget()).expect("open child shard");
    let receipt = storage
        .commit_batch(
            storage.snapshot(),
            token(13, 50_000),
            &[put(b"process-exit", b"durable")],
        )
        .expect("receive local durable acknowledgment");
    assert_eq!(receipt.commit_seq, CommitSeq::new(1));

    // Exit without running Storage or File destructors, as an abrupt process stop.
    std::process::exit(73);
}

#[cfg(feature = "slow-tests")]
#[test]
fn crash_boundary_child_process() {
    if std::env::var_os(CRASH_CHILD_ENV).is_none() {
        return;
    }
    let point = std::env::var(CRASH_POINT_ENV).expect("parent sets crash point");
    let path = std::env::var(CRASH_DIRECTORY_ENV).expect("parent sets shard directory");
    let storage = Storage::open(config(Path::new(&path)), budget()).expect("open crash-test shard");
    let transaction_id = token(16, 50_000);
    let mutations = [put(b"crash-boundary", b"durable")];

    if point == "after_wal_sync_before_apply" {
        storage
            .commit_batch(storage.snapshot(), transaction_id, &mutations)
            .expect("crash hook should run before commit returns");
    } else {
        storage
            .commit_batch(storage.snapshot(), transaction_id, &mutations)
            .expect("commit before checkpoint crash point");
        storage
            .checkpoint()
            .expect("crash hook should run during checkpoint publication");
    }
    panic!("configured storage crash hook did not terminate the child");
}

#[cfg(feature = "slow-tests")]
fn run_crash_child(directory: &TempShard, point: &str) -> Option<i32> {
    let executable = std::env::current_exe().expect("locate integration test binary");
    Command::new(executable)
        .arg("--exact")
        .arg("storage_slow::storage_contract::crash_boundary_child_process")
        .env(CRASH_CHILD_ENV, "1")
        .env(CRASH_DIRECTORY_ENV, directory.path())
        .env(CRASH_POINT_ENV, point)
        .status()
        .expect("run crash-boundary child process")
        .code()
}

#[test]
fn acknowledged_batch_recovers_after_process_exits_without_destructors() {
    let directory = TempShard::new();
    let executable = std::env::current_exe().expect("locate integration test binary");
    let status = Command::new(executable)
        .arg("--exact")
        .arg("storage_slow::storage_contract::child_process_exits_immediately_after_local_durable_ack")
        .env(POST_ACK_CHILD_ENV, "1")
        .env(CRASH_DIRECTORY_ENV, directory.path())
        .status()
        .expect("run child integration-test process");
    assert_eq!(status.code(), Some(73));

    let storage = Storage::open(config(directory.path()), budget()).expect("recover child WAL");
    assert_eq!(storage.stats().visible, CommitSeq::new(1));
    assert_eq!(
        read(&storage, storage.snapshot(), b"process-exit").as_deref(),
        Some(&b"durable"[..])
    );
}

#[cfg(feature = "slow-tests")]
#[test]
fn synced_wal_recovers_and_reconciles_id_after_crash_before_apply_or_receipt() {
    let directory = TempShard::new();
    assert_eq!(
        run_crash_child(&directory, "after_wal_sync_before_apply"),
        Some(86)
    );

    let storage = Storage::open(config(directory.path()), budget()).expect("recover synced WAL");
    assert_eq!(storage.stats().visible, CommitSeq::new(1));
    assert_eq!(
        read(&storage, storage.snapshot(), b"crash-boundary").as_deref(),
        Some(&b"durable"[..])
    );
    let receipt = storage
        .commit_batch(
            storage.snapshot(),
            token(16, 50_000),
            &[put(b"crash-boundary", b"durable")],
        )
        .expect("retry reconciles the committed but unacknowledged ID");
    assert_eq!(receipt.commit_seq, CommitSeq::new(1));
}

#[cfg(feature = "slow-tests")]
#[test]
fn synced_run_without_manifest_publication_recovers_from_retained_wal() {
    let directory = TempShard::new();
    assert_eq!(
        run_crash_child(&directory, "after_run_sync_before_manifest"),
        Some(86)
    );

    let storage = Storage::open(config(directory.path()), budget()).expect("recover old authority");
    assert_eq!(storage.stats().checkpoint, CommitSeq::ZERO);
    assert_eq!(storage.stats().visible, CommitSeq::new(1));
    assert_eq!(
        read(&storage, storage.snapshot(), b"crash-boundary").as_deref(),
        Some(&b"durable"[..])
    );
}

#[cfg(feature = "slow-tests")]
#[test]
fn synced_manifest_publication_is_authoritative_after_process_exit() {
    let directory = TempShard::new();
    assert_eq!(
        run_crash_child(&directory, "after_manifest_sync_before_apply"),
        Some(86)
    );

    let storage =
        Storage::open(config(directory.path()), budget()).expect("recover published manifest");
    assert_eq!(storage.stats().checkpoint, CommitSeq::new(1));
    assert_eq!(storage.stats().visible, CommitSeq::new(1));
    assert_eq!(
        read(&storage, storage.snapshot(), b"crash-boundary").as_deref(),
        Some(&b"durable"[..])
    );
}

#[test]
fn local_durable_batch_survives_reopen_as_one_committed_cursor_record() {
    let directory = TempShard::new();
    let config = config(directory.path());
    let budget = budget();
    let transaction_id = token(1, 50_000);

    let receipt = {
        let storage = Storage::open(config.clone(), budget.clone()).expect("open new shard");
        let receipt = storage
            .commit_batch(
                storage.snapshot(),
                transaction_id,
                &[put(b"left", b"L"), put(b"right", b"R")],
            )
            .expect("commit complete two-key batch");
        assert_eq!(receipt.commit_seq, CommitSeq::new(1));
        assert_eq!(receipt.frontiers.durable, Some(receipt.commit_seq));
        assert_eq!(receipt.frontiers.applied, Some(receipt.commit_seq));
        receipt
    };

    let storage = Storage::open(config, budget).expect("reopen shard");
    let snapshot = storage.snapshot();
    assert_eq!(snapshot.seq, receipt.commit_seq);
    assert_eq!(
        read(&storage, snapshot, b"left").as_deref(),
        Some(&b"L"[..])
    );
    assert_eq!(
        read(&storage, snapshot, b"right").as_deref(),
        Some(&b"R"[..])
    );

    let mut cursor = storage
        .committed_cursor()
        .expect("open committed WAL cursor");
    let batch = cursor
        .next(CommitSeq::ZERO, FormatLimits::local_v1().max_frame_bytes)
        .expect("read committed batch")
        .expect("one committed WAL batch");
    assert_eq!(batch.sequence, receipt.commit_seq);
    assert_eq!(batch.transaction_id, transaction_id);
    assert_eq!(
        batch
            .frame(&FormatLimits::local_v1())
            .expect("decode batch")
            .operation_count(),
        2
    );
    assert!(
        cursor
            .next(batch.sequence, FormatLimits::local_v1().max_frame_bytes)
            .expect("reach durable frontier")
            .is_none()
    );
}

#[test]
fn incomplete_uncommitted_wal_tail_is_truncated_but_complete_corruption_fails_open() {
    let directory = TempShard::new();
    let config = config(directory.path());
    let budget = budget();
    let storage = Storage::open(config.clone(), budget.clone()).expect("open new shard");
    storage
        .commit_batch(
            storage.snapshot(),
            token(2, 50_000),
            &[put(b"durable", b"kept")],
        )
        .expect("commit seed batch");
    drop(storage);
    let wal_path = directory.path().join("wal.log");
    let committed_bytes = fs::read(&wal_path).expect("read committed WAL");
    let committed_len = committed_bytes.len();

    let mut uncommitted_frame = Vec::new();
    encode_wal_frame_into(
        &mut uncommitted_frame,
        2,
        token(3, 50_000),
        &[WalOperation::Put {
            key: b"uncommitted",
            value: b"must not become visible",
        }],
        &FormatLimits::local_v1(),
    )
    .expect("encode uncommitted tail fixture");
    append(
        &directory.path,
        &uncommitted_frame[..uncommitted_frame.len() - 8],
    );

    let storage = Storage::open(config.clone(), budget.clone()).expect("recover incomplete tail");
    assert_eq!(
        storage.stats().wal_bytes,
        u64::try_from(committed_len).expect("WAL length fits u64")
    );
    let snapshot = storage.snapshot();
    assert_eq!(
        read(&storage, snapshot, b"durable").as_deref(),
        Some(&b"kept"[..])
    );
    assert_eq!(read(&storage, snapshot, b"uncommitted"), None);
    drop(storage);

    append(&directory.path, b"TBWA");
    let storage = Storage::open(config.clone(), budget.clone()).expect("recover partial header");
    assert_eq!(
        storage.stats().wal_bytes,
        u64::try_from(committed_len).expect("WAL length fits u64")
    );
    assert_eq!(
        read(&storage, storage.snapshot(), b"durable").as_deref(),
        Some(&b"kept"[..])
    );
    drop(storage);

    let mut corrupted = fs::read(&wal_path).expect("read recovered WAL");
    let checksum_offset = corrupted.len() - 1;
    corrupted[checksum_offset] ^= 1;
    fs::write(&wal_path, corrupted).expect("corrupt complete WAL checksum");
    let result = Storage::open(config, budget);
    assert!(matches!(
        result,
        Err(StorageError::Format(
            tb_format::FormatError::ChecksumMismatch { .. }
        ))
    ));
}

#[test]
fn snapshots_keep_overwritten_and_deleted_versions_visible_across_checkpoint_and_reopen() {
    let directory = TempShard::new();
    let config = config(directory.path());
    let budget = budget();
    let storage = Storage::open(config.clone(), budget.clone()).expect("open new shard");
    storage
        .commit_batch(
            storage.snapshot(),
            token(4, 50_000),
            &[put(b"a", b"old-a"), put(b"z", b"old-z")],
        )
        .expect("commit initial versions");
    let old_snapshot = storage.snapshot();
    storage
        .commit_batch(
            storage.snapshot(),
            token(5, 50_000),
            &[
                put(b"a", b"new-a"),
                Mutation::Delete { key: key(b"z") },
                put(b"b", b"new-b"),
            ],
        )
        .expect("commit overwrite, delete, and insert");
    storage.checkpoint().expect("publish immutable checkpoint");
    assert_eq!(storage.stats().checkpoint, CommitSeq::new(2));
    drop(storage);

    let storage = Storage::open(config, budget).expect("reopen checkpointed shard");
    assert_eq!(
        read(&storage, old_snapshot, b"a").as_deref(),
        Some(&b"old-a"[..])
    );
    assert_eq!(
        read(&storage, old_snapshot, b"z").as_deref(),
        Some(&b"old-z"[..])
    );
    assert_eq!(read(&storage, old_snapshot, b"b"), None);

    let old_page = storage
        .scan(old_snapshot, &KeyRange::all(), 10)
        .expect("scan old snapshot");
    let old_rows = old_page.rows();
    assert_eq!(
        old_rows
            .iter()
            .map(|row| (row.key.as_bytes(), row.value.as_slice()))
            .collect::<Vec<_>>(),
        vec![
            (b"a".as_slice(), b"old-a".as_slice()),
            (b"z".as_slice(), b"old-z".as_slice())
        ]
    );

    let current_page = storage
        .scan(storage.snapshot(), &KeyRange::all(), 10)
        .expect("scan current snapshot");
    let current_rows = current_page.rows();
    assert_eq!(
        current_rows
            .iter()
            .map(|row| (row.key.as_bytes(), row.value.as_slice()))
            .collect::<Vec<_>>(),
        vec![
            (b"a".as_slice(), b"new-a".as_slice()),
            (b"b".as_slice(), b"new-b".as_slice())
        ]
    );
}

#[test]
fn corrupt_newer_checkpoint_falls_back_and_replays_retained_wal() {
    let directory = TempShard::new();
    let config = config(directory.path());
    let budget = budget();
    let storage = Storage::open(config.clone(), budget.clone()).expect("open new shard");
    storage
        .commit_batch(
            storage.snapshot(),
            token(14, 50_000),
            &[put(b"checkpointed", b"generation-one")],
        )
        .expect("commit generation one");
    storage.checkpoint().expect("publish first checkpoint");
    let generation_one = storage.snapshot();

    storage
        .commit_batch(
            storage.snapshot(),
            token(15, 50_000),
            &[put(b"checkpointed", b"generation-two")],
        )
        .expect("commit generation two");
    storage.checkpoint().expect("publish second checkpoint");
    assert_eq!(storage.stats().checkpoint, CommitSeq::new(2));
    drop(storage);

    let newer_run = directory.path().join("run-00000000000000000002.seg");
    let mut corrupted = fs::read(&newer_run).expect("read second generation run");
    let last_byte = corrupted.len() - 1;
    corrupted[last_byte] ^= 1;
    fs::write(&newer_run, corrupted).expect("corrupt second generation run");

    let storage = Storage::open(config, budget).expect("fall back to valid checkpoint");
    assert_eq!(storage.stats().checkpoint, CommitSeq::new(1));
    assert_eq!(storage.stats().visible, CommitSeq::new(2));
    assert_eq!(storage.stats().immutable_runs, 1);
    assert_eq!(
        read(&storage, storage.snapshot(), b"checkpointed").as_deref(),
        Some(&b"generation-two"[..])
    );
    assert_eq!(
        read(&storage, generation_one, b"checkpointed").as_deref(),
        Some(&b"generation-one"[..])
    );
}

#[test]
fn retry_token_digest_returns_original_receipt_and_rejects_changed_operations_after_reopen() {
    let directory = TempShard::new();
    let config = config(directory.path());
    let budget = budget();
    let transaction_id = token(6, 50_000);
    let mutations = [put(b"idempotent", b"original")];
    let storage = Storage::open(config.clone(), budget.clone()).expect("open new shard");
    let original = storage
        .commit_batch(storage.snapshot(), transaction_id, &mutations)
        .expect("commit original batch");
    storage.checkpoint().expect("checkpoint retry resolution");
    drop(storage);

    let storage = Storage::open(config, budget).expect("reopen retry resolution");
    let retry = storage
        .commit_batch(storage.snapshot(), transaction_id, &mutations)
        .expect("same token and canonical operations return original receipt");
    assert_eq!(retry, original);
    let changed = [put(b"idempotent", b"changed")];
    assert!(matches!(
        storage.commit_batch(storage.snapshot(), transaction_id, &changed),
        Err(StorageError::IdempotencyConflict)
    ));
    assert_eq!(storage.stats().visible, original.commit_seq);
    assert_eq!(
        read(&storage, storage.snapshot(), b"idempotent").as_deref(),
        Some(&b"original"[..])
    );
}

#[test]
fn stale_snapshot_conflict_and_duplicate_key_batch_leave_no_partial_mutation() {
    let directory = TempShard::new();
    let config = config(directory.path());
    let budget = budget();
    let storage = Storage::open(config, budget).expect("open new shard");
    let stale = storage.snapshot();

    assert!(matches!(
        storage.commit_batch(
            stale,
            token(7, 50_000),
            &[put(b"duplicate", b"first"), put(b"duplicate", b"second")]
        ),
        Err(StorageError::InvalidBatch("duplicate key"))
    ));
    assert_eq!(storage.stats().visible, CommitSeq::ZERO);
    assert_eq!(storage.stats().wal_bytes, 0);
    assert_eq!(read(&storage, storage.snapshot(), b"duplicate"), None);

    storage
        .commit_batch(stale, token(8, 50_000), &[put(b"conflict", b"current")])
        .expect("commit after rejected batch");
    assert!(matches!(
        storage.commit_batch(stale, token(9, 50_000), &[put(b"conflict", b"stale")]),
        Err(StorageError::Conflict)
    ));
    assert_eq!(storage.stats().visible, CommitSeq::new(1));
    assert_eq!(
        read(&storage, storage.snapshot(), b"conflict").as_deref(),
        Some(&b"current"[..])
    );
}

#[test]
fn checkpoint_expired_floor_survives_reopen_and_rejects_a_backward_clock_retry() {
    let directory = TempShard::new();
    let now = Arc::new(AtomicU64::new(100));
    let clock = Arc::clone(&now);
    let config = StorageConfig::local(directory.path(), TenantId::from_bytes([1; 16]), WorkerId(7))
        .with_clock(Arc::new(move || clock.load(Ordering::SeqCst)));
    let budget = budget();
    let transaction_id = token(10, 200);
    let mutations = [put(b"expired", b"still durable")];
    let storage = Storage::open(config.clone(), budget.clone()).expect("open new shard");
    storage
        .commit_batch(storage.snapshot(), transaction_id, &mutations)
        .expect("commit token before its deadline");

    now.store(300, Ordering::SeqCst);
    storage.checkpoint().expect("persist advanced expiry floor");
    assert_eq!(storage.stats().expired_floor_ms, 300);
    drop(storage);

    now.store(150, Ordering::SeqCst);
    let storage = Storage::open(config, budget).expect("reopen after clock moves backward");
    assert_eq!(storage.stats().expired_floor_ms, 300);
    assert!(matches!(
        storage.commit_batch(storage.snapshot(), transaction_id, &mutations),
        Err(StorageError::IdempotencyExpired)
    ));
    assert_eq!(storage.stats().visible, CommitSeq::new(1));
    assert_eq!(
        read(&storage, storage.snapshot(), b"expired").as_deref(),
        Some(&b"still durable"[..])
    );
}

#[test]
fn newer_floor_survives_fallback_from_a_corrupt_floor_only_manifest() {
    let directory = TempShard::new();
    let now = Arc::new(AtomicU64::new(100));
    let clock = Arc::clone(&now);
    let config = StorageConfig::local(directory.path(), TenantId::from_bytes([1; 16]), WorkerId(7))
        .with_clock(Arc::new(move || clock.load(Ordering::SeqCst)));
    let budget = budget();
    let storage = Storage::open(config.clone(), budget.clone()).expect("open new shard");

    storage
        .checkpoint()
        .expect("publish generation one with floor 100");
    assert_eq!(storage.stats().expired_floor_ms, 100);
    now.store(300, Ordering::SeqCst);
    storage
        .checkpoint()
        .expect("publish floor-only generation two with floor 300");
    assert_eq!(storage.stats().expired_floor_ms, 300);
    drop(storage);

    // Generation two has no run, so corrupt its manifest to force fallback to
    // generation one while leaving the independent expiry-floor authority intact.
    let newer_manifest = directory.path().join("manifest.0");
    let mut corrupted = fs::read(&newer_manifest).expect("read newer manifest");
    let checksum_offset = corrupted.len() - 1;
    corrupted[checksum_offset] ^= 1;
    fs::write(&newer_manifest, corrupted).expect("corrupt newer manifest");

    now.store(150, Ordering::SeqCst);
    let storage = Storage::open(config, budget).expect("recover floor across manifest fallback");
    assert_eq!(storage.stats().checkpoint, CommitSeq::ZERO);
    assert_eq!(storage.stats().expired_floor_ms, 300);
    assert!(matches!(
        storage.commit_batch(
            storage.snapshot(),
            token(24, 200),
            &[put(b"never-committed", b"must stay expired")],
        ),
        Err(StorageError::IdempotencyExpired)
    ));
    assert_eq!(storage.stats().visible, CommitSeq::ZERO);
}

#[test]
fn newer_floor_survives_fallback_from_a_corrupt_run_and_wal_replay() {
    let directory = TempShard::new();
    let now = Arc::new(AtomicU64::new(100));
    let clock = Arc::clone(&now);
    let config = StorageConfig::local(directory.path(), TenantId::from_bytes([1; 16]), WorkerId(7))
        .with_clock(Arc::new(move || clock.load(Ordering::SeqCst)));
    let budget = budget();
    let storage = Storage::open(config.clone(), budget.clone()).expect("open new shard");

    storage
        .commit_batch(
            storage.snapshot(),
            token(25, 1_000),
            &[put(b"generation-one", b"retained")],
        )
        .expect("commit data for generation one");
    storage
        .checkpoint()
        .expect("publish generation one with floor 100");
    assert_eq!(storage.stats().checkpoint, CommitSeq::new(1));
    assert_eq!(storage.stats().expired_floor_ms, 100);

    now.store(200, Ordering::SeqCst);
    storage
        .commit_batch(
            storage.snapshot(),
            token(26, 2_000),
            &[put(b"generation-two", b"retained from WAL")],
        )
        .expect("commit data for generation two");
    now.store(300, Ordering::SeqCst);
    storage
        .checkpoint()
        .expect("publish generation two with floor 300");
    assert_eq!(storage.stats().checkpoint, CommitSeq::new(2));
    assert_eq!(storage.stats().expired_floor_ms, 300);
    drop(storage);

    let newer_run = directory.path().join("run-00000000000000000002.seg");
    let mut corrupted = fs::read(&newer_run).expect("read generation two run");
    let checksum_offset = corrupted.len() - 1;
    corrupted[checksum_offset] ^= 1;
    fs::write(&newer_run, corrupted).expect("corrupt generation two run");

    now.store(150, Ordering::SeqCst);
    let storage = Storage::open(config, budget).expect("recover from older run and retained WAL");
    assert_eq!(storage.stats().checkpoint, CommitSeq::new(1));
    assert_eq!(storage.stats().visible, CommitSeq::new(2));
    assert_eq!(storage.stats().immutable_runs, 1);
    assert_eq!(storage.stats().expired_floor_ms, 300);
    assert!(matches!(
        storage.commit_batch(
            storage.snapshot(),
            token(27, 200),
            &[put(b"never-committed", b"must stay expired")],
        ),
        Err(StorageError::IdempotencyExpired)
    ));
    assert_eq!(
        read(&storage, storage.snapshot(), b"generation-two").as_deref(),
        Some(&b"retained from WAL"[..])
    );
}

#[test]
fn memory_and_disk_admission_fail_before_wal_or_visible_frontier_changes() {
    const CONSTRAINED_PROCESS_BUDGET_BYTES: usize = 24 * 1024;
    const OVERSIZED_WRITE_VALUE_BYTES: usize = 4 * 1024;

    let memory_directory = TempShard::new();
    let memory_config = config(memory_directory.path());
    // This envelope admits Storage::open's per-shard state but leaves less than
    // one 4 KiB value available for the write frame once that reservation is held.
    let tiny_budget = MemoryBudget::new(
        BudgetConfig::new(CONSTRAINED_PROCESS_BUDGET_BYTES, 0)
            .expect("valid constrained memory budget"),
    );
    let memory_storage =
        Storage::open(memory_config.clone(), tiny_budget.clone()).expect("open under memory cap");
    let memory_open_reservation = tiny_budget.snapshot();
    assert!(
        CONSTRAINED_PROCESS_BUDGET_BYTES - memory_open_reservation.reserved_bytes
            < OVERSIZED_WRITE_VALUE_BYTES
    );
    let memory_result = memory_storage.commit_batch(
        memory_storage.snapshot(),
        token(11, 50_000),
        &[put(
            b"too-large-for-budget",
            &[b'x'; OVERSIZED_WRITE_VALUE_BYTES],
        )],
    );
    assert!(matches!(memory_result, Err(StorageError::Admission(_))));
    assert_eq!(memory_storage.stats().visible, CommitSeq::ZERO);
    assert_eq!(memory_storage.stats().wal_bytes, 0);
    assert_eq!(
        tiny_budget.snapshot().reserved_bytes,
        memory_open_reservation.reserved_bytes
    );
    assert_eq!(
        tiny_budget.snapshot().reserved_items,
        memory_open_reservation.reserved_items
    );
    assert_eq!(
        fs::metadata(memory_directory.path().join("wal.log"))
            .expect("stat WAL after memory rejection")
            .len(),
        0
    );
    drop(memory_storage);
    assert_eq!(tiny_budget.snapshot().reserved_bytes, 0);
    assert_eq!(tiny_budget.snapshot().reserved_items, 0);
    let memory_reopened =
        Storage::open(memory_config, tiny_budget).expect("reopen memory rejection");
    assert_eq!(memory_reopened.stats().visible, CommitSeq::ZERO);
    assert_eq!(memory_reopened.stats().wal_bytes, 0);

    let disk_directory = TempShard::new();
    let mut disk_config = config(disk_directory.path());
    disk_config.max_disk_bytes =
        u64::try_from(FormatLimits::local_v1().max_frame_bytes).expect("frame limit fits u64");
    let disk_budget = budget();
    let disk_storage =
        Storage::open(disk_config.clone(), disk_budget.clone()).expect("open at disk cap");
    let disk_open_reservation = disk_budget.snapshot();
    let maximum_value = vec![b'x'; tb_core::MAX_VALUE_BYTES];
    let disk_result = disk_storage.commit_batch(
        disk_storage.snapshot(),
        token(12, 50_000),
        &[put(b"disk-cap", &maximum_value)],
    );
    assert!(matches!(
        disk_result,
        Err(StorageError::Capacity("SSD bytes and checkpoint headroom"))
    ));
    assert_eq!(disk_storage.stats().visible, CommitSeq::ZERO);
    assert_eq!(disk_storage.stats().wal_bytes, 0);
    assert_eq!(
        disk_budget.snapshot().reserved_bytes,
        disk_open_reservation.reserved_bytes
    );
    assert_eq!(
        disk_budget.snapshot().reserved_items,
        disk_open_reservation.reserved_items
    );
    assert_eq!(
        read(&disk_storage, disk_storage.snapshot(), b"disk-cap"),
        None
    );
    drop(disk_storage);
    assert_eq!(disk_budget.snapshot().reserved_bytes, 0);
    assert_eq!(disk_budget.snapshot().reserved_items, 0);
    let disk_reopened = Storage::open(disk_config, disk_budget).expect("reopen disk rejection");
    assert_eq!(disk_reopened.stats().visible, CommitSeq::ZERO);
    assert_eq!(
        read(&disk_reopened, disk_reopened.snapshot(), b"disk-cap"),
        None
    );
}

#[test]
fn unsupported_ack_profile_is_rejected_before_creating_a_shard_directory() {
    let directory = TempShard::new();
    let mut config = config(directory.path());
    config.profile = AckProfile::ObjectDurable;

    assert!(matches!(
        Storage::open(config, budget()),
        Err(StorageError::UnsupportedProfile)
    ));
    assert!(!directory.path().exists());
}
