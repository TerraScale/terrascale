use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use tb_core::{BudgetConfig, CommitSeq, Key, MemoryBudget, TenantId, TransactionId, WorkerId};
use tb_storage::{Mutation, Storage, StorageConfig, StorageError};

struct TempShard(PathBuf);

impl TempShard {
    fn new() -> Self {
        let tick = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("clock")
            .as_nanos();
        Self(std::env::temp_dir().join(format!("terrabase-retained-{}-{tick}", std::process::id())))
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

#[test]
fn indexed_lookup_keeps_just_synced_frame_across_deadline_and_releases_bytes() {
    let directory = TempShard::new();
    let now = Arc::new(AtomicU64::new(100));
    let budget = budget();
    let storage = Storage::open(config(directory.path(), &now), budget.clone()).expect("open");
    let id = token(1, 101);
    let mutation = [put(b"a", b"value")];
    let receipt = storage
        .commit_batch(storage.snapshot(), id, &mutation)
        .expect("commit before expiry");
    now.store(102, Ordering::SeqCst);

    let before = budget.snapshot().reserved_bytes;
    let frame = storage
        .committed_batch_for_transaction(id)
        .expect("indexed lookup")
        .expect("retained frame");
    assert_eq!(frame.sequence, receipt.commit_seq);
    assert_eq!(frame.transaction_id, id);
    assert!(budget.snapshot().reserved_bytes > before);
    drop(frame);
    assert_eq!(budget.snapshot().reserved_bytes, before);
    assert!(matches!(
        storage.commit_batch(storage.snapshot(), id, &mutation),
        Err(StorageError::IdempotencyExpired)
    ));
    assert!(
        storage
            .committed_batch_for_transaction(token(2, 50_000))
            .expect("live miss")
            .is_none()
    );
    assert!(matches!(
        storage.committed_batch_for_transaction(token(3, 102)),
        Err(StorageError::IdempotencyExpired)
    ));
}

#[test]
fn expired_at_open_cannot_revive_after_clock_rollback_and_manifest_fallback() {
    let directory = TempShard::new();
    let now = Arc::new(AtomicU64::new(100));
    let config = config(directory.path(), &now);
    let budget = budget();
    let id = token(4, 200);
    let mutation = [put(b"a", b"durable")];
    let storage = Storage::open(config.clone(), budget.clone()).expect("open");
    storage
        .commit_batch(storage.snapshot(), id, &mutation)
        .expect("initial commit");
    storage.checkpoint().expect("older manifest");
    now.store(150, Ordering::SeqCst);
    storage.checkpoint().expect("newer manifest");
    drop(storage);

    now.store(300, Ordering::SeqCst);
    let storage = Storage::open(config.clone(), budget.clone()).expect("reopen after expiry");
    assert_eq!(storage.stats().expired_floor_ms, 300);
    assert!(matches!(
        storage.committed_batch_for_transaction(id),
        Err(StorageError::IdempotencyExpired)
    ));
    drop(storage);

    let newer_manifest = directory.path().join("manifest.0");
    let mut bytes = fs::read(&newer_manifest).expect("newer manifest");
    let last = bytes.last_mut().expect("manifest checksum");
    *last ^= 1;
    fs::write(&newer_manifest, bytes).expect("force older manifest fallback");

    now.store(150, Ordering::SeqCst);
    let storage = Storage::open(config, budget).expect("reopen with backward clock");
    assert_eq!(storage.stats().expired_floor_ms, 300);
    assert_eq!(storage.stats().visible, CommitSeq::new(1));
    let wal_path = directory.path().join("wal.log");
    let before = fs::metadata(&wal_path).expect("WAL metadata").len();
    assert!(matches!(
        storage.commit_batch(storage.snapshot(), id, &mutation),
        Err(StorageError::IdempotencyExpired)
    ));
    assert_eq!(fs::metadata(wal_path).expect("WAL metadata").len(), before);
    assert_eq!(storage.stats().visible, CommitSeq::new(1));
}

#[test]
fn checkpoint_during_commit_rejects_token_expired_after_initial_check() {
    let directory = TempShard::new();
    let clock_phase = Arc::new(AtomicU64::new(0));
    let phase_for_clock = Arc::clone(&clock_phase);
    let mut config =
        StorageConfig::local(directory.path(), TenantId::from_bytes([1; 16]), WorkerId(1))
            .with_clock(Arc::new(move || {
                match phase_for_clock.load(Ordering::SeqCst) {
                    1 => {
                        // The second commit's initial check sees 100; its checkpoint sees 300.
                        phase_for_clock.store(2, Ordering::SeqCst);
                        100
                    }
                    2 => 300,
                    _ => 100,
                }
            }));
    config.max_live_transactions = 1;
    let budget = budget();
    let storage = Storage::open(config.clone(), budget.clone()).expect("open");
    storage
        .commit_batch(storage.snapshot(), token(9, 150), &[put(b"first", b"kept")])
        .expect("first commit fills dedup table");
    let first_wal_len = storage.stats().wal_bytes;

    clock_phase.store(1, Ordering::SeqCst);
    assert!(matches!(
        storage.commit_batch(
            storage.snapshot(),
            token(10, 200),
            &[put(b"second", b"rejected")]
        ),
        Err(StorageError::IdempotencyExpired)
    ));
    assert_eq!(clock_phase.load(Ordering::SeqCst), 2);
    assert_eq!(storage.stats().expired_floor_ms, 300);
    assert_eq!(storage.stats().visible, CommitSeq::new(1));
    assert_eq!(storage.stats().wal_bytes, first_wal_len);
    assert_eq!(
        fs::metadata(directory.path().join("wal.log"))
            .expect("WAL metadata")
            .len(),
        first_wal_len
    );
    assert!(
        storage
            .get(
                storage.snapshot(),
                &Key::new(b"second".to_vec()).expect("key")
            )
            .expect("read rejected key")
            .is_none()
    );
    drop(storage);

    clock_phase.store(3, Ordering::SeqCst); // Roll back below the durable floor.
    let storage = Storage::open(config, budget).expect("reopen after rollback");
    assert_eq!(storage.stats().expired_floor_ms, 300);
    assert_eq!(storage.stats().visible, CommitSeq::new(1));
    assert_eq!(storage.stats().wal_bytes, first_wal_len);
    assert!(
        storage
            .get(
                storage.snapshot(),
                &Key::new(b"second".to_vec()).expect("key")
            )
            .expect("read rejected key after reopen")
            .is_none()
    );
    assert!(matches!(
        storage.commit_batch(
            storage.snapshot(),
            token(10, 200),
            &[put(b"second", b"rejected")]
        ),
        Err(StorageError::IdempotencyExpired)
    ));
}

#[test]
fn indexed_lookup_reads_only_target_frame_and_corruption_fails_closed() {
    let directory = TempShard::new();
    let now = Arc::new(AtomicU64::new(100));
    let storage = Storage::open(config(directory.path(), &now), budget()).expect("open");
    let first = token(5, 50_000);
    let second = token(6, 50_000);
    storage
        .commit_batch(storage.snapshot(), first, &[put(b"a", b"x")])
        .expect("first commit");
    storage
        .commit_batch(storage.snapshot(), second, &[put(b"b", b"y")])
        .expect("second commit");
    let first_len = storage
        .committed_batch_for_transaction(first)
        .expect("first lookup")
        .expect("first frame")
        .encoded_bytes()
        .len();
    let wal_path = directory.path().join("wal.log");
    let mut bytes = fs::read(&wal_path).expect("read WAL");
    bytes[first_len - 1] ^= 1;
    fs::write(&wal_path, bytes).expect("corrupt unrelated frame");
    assert!(
        storage
            .committed_batch_for_transaction(second)
            .expect("bounded second lookup")
            .is_some()
    );
    assert!(matches!(
        storage.committed_batch_for_transaction(first),
        Err(StorageError::Corrupt(_))
    ));
}

#[test]
fn indexed_lookup_rejects_valid_frame_at_wrong_offset() {
    let directory = TempShard::new();
    let now = Arc::new(AtomicU64::new(100));
    let storage = Storage::open(config(directory.path(), &now), budget()).expect("open");
    let first = token(7, 50_000);
    let second = token(8, 50_000);
    storage
        .commit_batch(storage.snapshot(), first, &[put(b"a", b"x")])
        .expect("first commit");
    storage
        .commit_batch(storage.snapshot(), second, &[put(b"b", b"y")])
        .expect("second commit");
    let first_len = storage
        .committed_batch_for_transaction(first)
        .expect("first lookup")
        .expect("first frame")
        .encoded_bytes()
        .len();
    let wal_path = directory.path().join("wal.log");
    let mut bytes = fs::read(&wal_path).expect("read WAL");
    assert_eq!(bytes.len(), first_len * 2);
    let (left, right) = bytes.split_at_mut(first_len);
    left.swap_with_slice(right);
    fs::write(&wal_path, bytes).expect("swap valid frames");
    assert!(matches!(
        storage.committed_batch_for_transaction(first),
        Err(StorageError::Corrupt(_))
    ));
}
