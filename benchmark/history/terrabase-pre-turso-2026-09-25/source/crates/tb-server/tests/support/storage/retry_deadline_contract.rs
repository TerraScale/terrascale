use std::fs;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use tb_core::{
    BudgetConfig, CommitSeq, CoreError, Key, MemoryBudget, TenantId, TransactionId, WorkerId,
};
use tb_storage::{Mutation, Storage, StorageConfig, StorageError};

#[test]
fn retry_window_boundaries_leave_rejected_batches_unwritten() {
    let tick = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("clock")
        .as_nanos();
    let directory = std::env::temp_dir().join(format!(
        "terrabase-retry-window-{}-{tick}",
        std::process::id()
    ));
    let now = Arc::new(AtomicU64::new(100));
    let clock = Arc::clone(&now);
    let mut config = StorageConfig::local(&directory, TenantId::from_bytes([1; 16]), WorkerId(1))
        .with_clock(Arc::new(move || clock.load(Ordering::SeqCst)));
    config.max_retry_window_ms = 50;
    let budget = MemoryBudget::new(BudgetConfig::new(64 * 1024 * 1024, 0).expect("budget"));
    let storage = Storage::open(config, budget).expect("open shard");
    let key = Key::new(b"deadline".to_vec()).expect("key");
    let mutation = Mutation::Put {
        key: key.clone(),
        value: b"accepted".to_vec(),
    };
    let before = storage.stats();

    let expired = TransactionId::new(100, [1; 16]);
    assert!(matches!(
        storage.prepare_batch(storage.snapshot(), expired, std::slice::from_ref(&mutation)),
        Err(StorageError::IdempotencyExpired)
    ));
    assert!(matches!(
        storage.commit_batch(storage.snapshot(), expired, std::slice::from_ref(&mutation)),
        Err(StorageError::IdempotencyExpired)
    ));
    let too_far = TransactionId::new(151, [2; 16]);
    assert!(matches!(
        storage.prepare_batch(storage.snapshot(), too_far, std::slice::from_ref(&mutation)),
        Err(StorageError::Core(CoreError::InvalidTransactionExpiry))
    ));
    assert!(matches!(
        storage.commit_batch(storage.snapshot(), too_far, std::slice::from_ref(&mutation)),
        Err(StorageError::Core(CoreError::InvalidTransactionExpiry))
    ));
    assert_eq!(storage.stats(), before);
    assert!(
        storage
            .get(storage.snapshot(), &key)
            .expect("read")
            .is_none()
    );

    storage
        .commit_batch(
            storage.snapshot(),
            TransactionId::new(150, [3; 16]),
            std::slice::from_ref(&mutation),
        )
        .expect("inclusive retry deadline");
    assert_eq!(storage.stats().durable, CommitSeq::new(1));
    now.store(120, Ordering::SeqCst);
    storage.checkpoint().expect("advance durable expiry floor");
    assert_eq!(storage.stats().expired_floor_ms, 120);
    now.store(110, Ordering::SeqCst);
    assert!(matches!(
        storage.prepare_batch(
            storage.snapshot(),
            TransactionId::new(120, [4; 16]),
            std::slice::from_ref(&mutation)
        ),
        Err(StorageError::IdempotencyExpired)
    ));
    assert!(matches!(
        storage.commit_batch(
            storage.snapshot(),
            TransactionId::new(120, [4; 16]),
            &[Mutation::Delete { key: key.clone() }]
        ),
        Err(StorageError::IdempotencyExpired)
    ));
    assert_eq!(
        storage
            .get(storage.snapshot(), &key)
            .expect("read")
            .expect("accepted value")
            .bytes(),
        b"accepted"
    );
    drop(storage);
    fs::remove_dir_all(directory).expect("remove shard");
}
