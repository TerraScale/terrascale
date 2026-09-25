use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, mpsc};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use tb_core::{
    BudgetConfig, CommitSeq, Key, MemoryBudget, MemoryCategory, ReadSnapshot, TenantId,
    TransactionId, WorkerId,
};
use tb_storage::{Mutation, PreparedBatchOutcome, Storage, StorageConfig, StorageError};

static NEXT_DIRECTORY: AtomicU64 = AtomicU64::new(0);

struct TempShard(PathBuf);

impl TempShard {
    fn new() -> Self {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock")
            .as_nanos();
        let ordinal = NEXT_DIRECTORY.fetch_add(1, Ordering::Relaxed);
        Self(std::env::temp_dir().join(format!(
            "terrabase-prepared-{}-{nanos}-{ordinal}",
            std::process::id()
        )))
    }

    fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for TempShard {
    fn drop(&mut self) {
        if self.0.exists() {
            fs::remove_dir_all(&self.0).expect("remove shard");
        }
    }
}

fn config(path: &Path, now: &Arc<AtomicU64>) -> StorageConfig {
    let clock = Arc::clone(now);
    StorageConfig::local(path, TenantId::from_bytes([1; 16]), WorkerId(1))
        .with_clock(Arc::new(move || clock.load(Ordering::SeqCst)))
}

fn budget() -> MemoryBudget {
    MemoryBudget::new(BudgetConfig::new(64 * 1024 * 1024, 0).expect("budget"))
}

fn token(nonce: u8, deadline: u64) -> TransactionId {
    TransactionId::new(deadline, [nonce; 16])
}

fn put(key: &[u8], value: &[u8]) -> Mutation {
    Mutation::Put {
        key: Key::new(key.to_vec()).expect("key"),
        value: value.to_vec(),
    }
}

fn read(storage: &Storage, key: &[u8]) -> Option<Vec<u8>> {
    storage
        .get(storage.snapshot(), &Key::new(key.to_vec()).expect("key"))
        .expect("point read")
        .map(|value| value.bytes().to_vec())
}

#[test]
fn dropping_ready_releases_reservations_without_target_commit() {
    let directory = TempShard::new();
    let now = Arc::new(AtomicU64::new(100));
    let budget = budget();
    let storage = Storage::open(config(directory.path(), &now), budget.clone()).expect("open");
    let baseline = budget.snapshot();
    let original = [put(b"d5-like", b"original")];
    let ready = match storage
        .prepare_batch(storage.snapshot(), token(1, 500), &original)
        .expect("prepare")
    {
        PreparedBatchOutcome::Ready(ready) => ready,
        PreparedBatchOutcome::AlreadyCommitted(_) => panic!("new token was already committed"),
    };
    assert!(ready.encoded_len() > 0);
    assert!(budget.snapshot().reserved_bytes > baseline.reserved_bytes);
    assert!(budget.snapshot().reserved_items > baseline.reserved_items);
    drop(ready);
    assert_eq!(budget.snapshot().reserved_bytes, baseline.reserved_bytes);
    assert_eq!(budget.snapshot().reserved_items, baseline.reserved_items);
    assert_eq!(storage.stats().visible, CommitSeq::ZERO);
    assert_eq!(storage.stats().wal_bytes, 0);
    assert_eq!(read(&storage, b"d5-like"), None);
    assert!(
        storage
            .committed_batch_for_transaction(token(1, 500))
            .expect("uncommitted token lookup")
            .is_none()
    );

    let mut mutations = vec![put(b"d5-like", b"frozen")];
    let ready = match storage
        .prepare_batch(storage.snapshot(), token(2, 500), &mutations)
        .expect("prepare owned frame")
    {
        PreparedBatchOutcome::Ready(ready) => ready,
        PreparedBatchOutcome::AlreadyCommitted(_) => panic!("new token was already committed"),
    };
    mutations[0] = put(b"other", b"changed");
    drop(mutations);
    let receipt = ready.commit().expect("commit frozen frame");
    assert_eq!(receipt.commit_seq, CommitSeq::new(1));
    assert_eq!(read(&storage, b"d5-like").as_deref(), Some(&b"frozen"[..]));
    assert_eq!(read(&storage, b"other"), None);
}

#[test]
fn matching_retry_returns_original_receipt_before_stale_key_conflict() {
    let directory = TempShard::new();
    let now = Arc::new(AtomicU64::new(100));
    let config = config(directory.path(), &now);
    let budget = budget();
    let storage = Storage::open(config.clone(), budget.clone()).expect("open");
    let old = ReadSnapshot::new(CommitSeq::ZERO);
    let id = token(3, 500);
    let mutation = [put(b"key", b"original")];
    let first = storage
        .commit_batch(old, id, &mutation)
        .expect("original commit");
    storage.checkpoint().expect("checkpoint");
    drop(storage);

    let storage = Storage::open(config, budget).expect("reopen");
    match storage
        .prepare_batch(old, id, &mutation)
        .expect("matching retry")
    {
        PreparedBatchOutcome::AlreadyCommitted(receipt) => assert_eq!(receipt, first),
        PreparedBatchOutcome::Ready(_) => panic!("retry acquired a new sequence"),
    }
    assert!(matches!(
        storage.prepare_batch(old, id, &[put(b"key", b"changed")]),
        Err(StorageError::IdempotencyConflict)
    ));
    assert!(matches!(
        storage.prepare_batch(old, token(4, 500), &mutation),
        Err(StorageError::Conflict)
    ));
    assert_eq!(storage.stats().visible, first.commit_seq);
}

#[test]
fn held_ready_excludes_other_writer_until_drop() {
    let directory = TempShard::new();
    let now = Arc::new(AtomicU64::new(100));
    let storage = Storage::open(config(directory.path(), &now), budget()).expect("open");
    let snapshot = storage.snapshot();
    let ready = match storage
        .prepare_batch(snapshot, token(5, 500), &[put(b"first", b"aborted")])
        .expect("prepare first")
    {
        PreparedBatchOutcome::Ready(ready) => ready,
        PreparedBatchOutcome::AlreadyCommitted(_) => panic!("new token was already committed"),
    };
    let (started_tx, started_rx) = mpsc::channel();
    let (done_tx, done_rx) = mpsc::channel();
    std::thread::scope(|scope| {
        let storage = &storage;
        scope.spawn(move || {
            started_tx.send(()).expect("signal start");
            let result = storage.commit_batch(snapshot, token(6, 500), &[put(b"second", b"kept")]);
            done_tx.send(result).expect("signal completion");
        });
        started_rx.recv().expect("writer started");
        assert!(done_rx.recv_timeout(Duration::from_millis(50)).is_err());
        drop(ready);
        done_rx
            .recv_timeout(Duration::from_secs(5))
            .expect("writer released")
            .expect("second commit");
    });
    assert_eq!(read(&storage, b"first"), None);
    assert_eq!(read(&storage, b"second").as_deref(), Some(&b"kept"[..]));
}

#[test]
fn expiry_while_ready_is_held_aborts_without_wal_append() {
    let directory = TempShard::new();
    let now = Arc::new(AtomicU64::new(100));
    let storage = Storage::open(config(directory.path(), &now), budget()).expect("open");
    let ready = match storage
        .prepare_batch(storage.snapshot(), token(7, 200), &[put(b"late", b"no")])
        .expect("prepare before expiry")
    {
        PreparedBatchOutcome::Ready(ready) => ready,
        PreparedBatchOutcome::AlreadyCommitted(_) => panic!("new token was already committed"),
    };
    now.store(300, Ordering::SeqCst);
    assert!(matches!(
        ready.commit(),
        Err(StorageError::IdempotencyExpired)
    ));
    assert_eq!(storage.stats().visible, CommitSeq::ZERO);
    assert_eq!(storage.stats().wal_bytes, 0);
    assert_eq!(read(&storage, b"late"), None);
}

#[test]
fn deterministic_admission_failures_precede_external_marker() {
    let directory = TempShard::new();
    let now = Arc::new(AtomicU64::new(100));
    let storage = Storage::open(config(directory.path(), &now), budget()).expect("open");
    let snapshot = storage.snapshot();
    let duplicate = [put(b"same", b"one"), put(b"same", b"two")];
    assert!(matches!(
        storage.prepare_batch(snapshot, token(8, 500), &duplicate),
        Err(StorageError::InvalidBatch("duplicate key"))
    ));
    let too_many: Vec<_> = (0..1_025)
        .map(|index| put(format!("key-{index}").as_bytes(), b"x"))
        .collect();
    let max_operations = match storage
        .prepare_batch(snapshot, token(15, 500), &too_many[..1_024])
        .expect("1,024 operations fit")
    {
        PreparedBatchOutcome::Ready(ready) => ready,
        PreparedBatchOutcome::AlreadyCommitted(_) => panic!("new token was already committed"),
    };
    drop(max_operations);
    assert!(matches!(
        storage.prepare_batch(snapshot, token(9, 500), &too_many),
        Err(StorageError::InvalidBatch("operation count"))
    ));
    assert!(
        storage
            .prepare_batch(
                snapshot,
                token(10, 500),
                &[put(b"max", &vec![0; 1_048_576])]
            )
            .is_ok()
    );
    assert!(matches!(
        storage.prepare_batch(
            snapshot,
            token(11, 500),
            &[put(b"oversized", &vec![0; 1_048_577])]
        ),
        Err(StorageError::InvalidBatch("value too large"))
    ));
    assert!(matches!(
        storage.prepare_batch(
            snapshot,
            token(12, 500),
            &[
                put(b"a", &vec![0; 1_048_576]),
                put(b"b", &vec![0; 1_048_576])
            ],
        ),
        Err(StorageError::Capacity("WAL frame bytes"))
    ));
    let exact_frame = [
        put(b"a", &vec![0; 1_048_576]),
        put(b"b", &vec![0; 1_048_496]),
    ];
    let exact = match storage
        .prepare_batch(snapshot, token(16, 500), &exact_frame)
        .expect("exact 2-MiB frame fits")
    {
        PreparedBatchOutcome::Ready(ready) => ready,
        PreparedBatchOutcome::AlreadyCommitted(_) => panic!("new token was already committed"),
    };
    assert_eq!(exact.encoded_len(), 2_097_152);
    drop(exact);
    assert!(matches!(
        storage.prepare_batch(
            snapshot,
            token(17, 500),
            &[
                put(b"a", &vec![0; 1_048_576]),
                put(b"b", &vec![0; 1_048_497])
            ],
        ),
        Err(StorageError::Capacity("WAL frame bytes"))
    ));
    assert_eq!(storage.stats().wal_bytes, 0);
    assert_eq!(storage.stats().visible, CommitSeq::ZERO);
}

#[test]
fn item_and_disk_admission_fail_before_target_wal() {
    let now = Arc::new(AtomicU64::new(100));
    let item_directory = TempShard::new();
    let item_budget = MemoryBudget::new(
        BudgetConfig::new(64 * 1024 * 1024, 0)
            .expect("budget")
            .with_category_item_limit(MemoryCategory::TransactionVersion, 1),
    );
    let item_storage = Storage::open(config(item_directory.path(), &now), item_budget)
        .expect("open item-limited shard");
    assert!(matches!(
        item_storage.prepare_batch(
            item_storage.snapshot(),
            token(13, 500),
            &[put(b"a", b"1"), put(b"b", b"2")],
        ),
        Err(StorageError::Admission(_))
    ));
    assert_eq!(item_storage.stats().wal_bytes, 0);

    let dedup_directory = TempShard::new();
    let dedup_budget = MemoryBudget::new(
        BudgetConfig::new(64 * 1024 * 1024, 0)
            .expect("budget")
            .with_category_item_limit(MemoryCategory::DatabaseState, 0),
    );
    let dedup_storage = Storage::open(config(dedup_directory.path(), &now), dedup_budget.clone())
        .expect("open dedup-limited shard");
    let baseline = dedup_budget.snapshot();
    assert!(matches!(
        dedup_storage.prepare_batch(
            dedup_storage.snapshot(),
            token(18, 500),
            &[put(b"dedup", b"rejected")],
        ),
        Err(StorageError::Admission(_))
    ));
    assert_eq!(dedup_storage.stats().wal_bytes, 0);
    assert_eq!(
        dedup_budget.snapshot().reserved_bytes,
        baseline.reserved_bytes
    );
    assert_eq!(
        dedup_budget.snapshot().reserved_items,
        baseline.reserved_items
    );

    let disk_directory = TempShard::new();
    let mut disk_config = config(disk_directory.path(), &now);
    disk_config.max_disk_bytes = disk_config.format.max_frame_bytes as u64;
    let disk_storage = Storage::open(disk_config, budget()).expect("open disk-limited shard");
    assert!(matches!(
        disk_storage.prepare_batch(
            disk_storage.snapshot(),
            token(14, 500),
            &[put(b"large", &vec![0; 1_048_576])],
        ),
        Err(StorageError::Capacity("SSD bytes and checkpoint headroom"))
    ));
    assert_eq!(disk_storage.stats().wal_bytes, 0);
}
