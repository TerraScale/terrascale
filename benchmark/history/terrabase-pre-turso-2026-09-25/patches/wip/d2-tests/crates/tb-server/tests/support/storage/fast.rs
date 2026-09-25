use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use tb_core::{
    BudgetConfig, Key, KeyRange, MemoryBudget, ReadSnapshot, TenantId, TransactionId, WorkerId,
};
use tb_storage::{Mutation, Storage, StorageConfig, StorageError};

static NEXT_DIRECTORY: AtomicU64 = AtomicU64::new(0);

struct TempShard(PathBuf);

impl TempShard {
    fn new() -> Self {
        let tick = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("clock")
            .as_nanos();
        let ordinal = NEXT_DIRECTORY.fetch_add(1, Ordering::Relaxed);
        Self(std::env::temp_dir().join(format!(
            "terrabase-fast-storage-{}-{tick}-{ordinal}",
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

fn config(path: &Path) -> StorageConfig {
    let mut config = StorageConfig::local(path, TenantId::from_bytes([1; 16]), WorkerId(1))
        .with_clock(Arc::new(|| 1_000));
    config.max_scan_rows = 2;
    config
}

fn budget() -> MemoryBudget {
    MemoryBudget::new(BudgetConfig::new(128 * 1024 * 1024, 0).expect("budget"))
}

fn key(bytes: &[u8]) -> Key {
    Key::new(bytes.to_vec()).expect("key")
}

fn token(index: u64) -> TransactionId {
    let mut nonce = [0; 16];
    nonce[..8].copy_from_slice(&index.to_be_bytes());
    TransactionId::new(50_000, nonce)
}

fn put(bytes: &[u8], value: &[u8]) -> Mutation {
    Mutation::Put {
        key: key(bytes),
        value: value.to_vec(),
    }
}

fn rows(storage: &Storage, snapshot: ReadSnapshot) -> Vec<(Vec<u8>, Vec<u8>)> {
    storage
        .scan(snapshot, &KeyRange::all(), 2)
        .expect("scan")
        .rows()
        .iter()
        .map(|row| (row.key.as_bytes().to_vec(), row.value.clone()))
        .collect()
}

#[test]
fn commit_scan_checkpoint_reopen_and_conflict_are_atomic() {
    let directory = TempShard::new();
    let config = config(directory.path());
    let storage = Storage::open(config.clone(), budget()).expect("open shard");
    let empty = storage.snapshot();
    assert!(rows(&storage, empty).is_empty());

    storage
        .commit_batch(
            storage.snapshot(),
            token(1),
            &[put(b"a", b"first"), put(b"b", b"second")],
        )
        .expect("commit two keys atomically");
    let before_update = storage.snapshot();
    let original = vec![
        (b"a".to_vec(), b"first".to_vec()),
        (b"b".to_vec(), b"second".to_vec()),
    ];
    assert_eq!(rows(&storage, before_update), original);
    storage.checkpoint().expect("checkpoint");
    drop(storage);

    let storage = Storage::open(config, budget()).expect("reopen checkpointed shard");
    assert_eq!(rows(&storage, storage.snapshot()), original);
    assert_eq!(rows(&storage, before_update), original);
    let wal_before_rejected_batch = storage.stats().wal_bytes;
    assert!(matches!(
        storage.commit_batch(
            storage.snapshot(),
            token(2),
            &[put(b"c", b"first"), put(b"c", b"second")]
        ),
        Err(StorageError::InvalidBatch("duplicate key"))
    ));
    assert_eq!(storage.stats().wal_bytes, wal_before_rejected_batch);
    assert_eq!(rows(&storage, storage.snapshot()), original);

    storage
        .commit_batch(storage.snapshot(), token(3), &[put(b"b", b"updated")])
        .expect("update b");
    let before_conflict = storage.stats();
    assert!(matches!(
        storage.commit_batch(before_update, token(4), &[put(b"b", b"stale")]),
        Err(StorageError::Conflict)
    ));
    assert_eq!(storage.stats(), before_conflict);
    assert_eq!(rows(&storage, before_update), original);
    assert_eq!(
        rows(&storage, storage.snapshot()),
        vec![
            (b"a".to_vec(), b"first".to_vec()),
            (b"b".to_vec(), b"updated".to_vec())
        ]
    );
}

#[test]
fn scan_limit_and_checked_run_corruption_fail_closed() {
    let directory = TempShard::new();
    let storage = Storage::open(config(directory.path()), budget()).expect("open shard");
    storage
        .commit_batch(storage.snapshot(), token(1), &[put(b"a", b"value")])
        .expect("commit a");
    storage.checkpoint().expect("checkpoint");
    assert!(matches!(
        storage.scan(storage.snapshot(), &KeyRange::all(), 3),
        Err(StorageError::Capacity("scan row limit"))
    ));

    let run_path = directory.path().join("run-00000000000000000001.seg");
    let mut run = fs::read(&run_path).expect("read checkpointed run");
    run[50] ^= 1;
    fs::write(run_path, run).expect("corrupt checked run block");
    assert!(matches!(
        storage.scan(storage.snapshot(), &KeyRange::all(), 1),
        Err(StorageError::Format(_))
    ));
}

#[path = "archive_contract.rs"]
mod archive_contract;
