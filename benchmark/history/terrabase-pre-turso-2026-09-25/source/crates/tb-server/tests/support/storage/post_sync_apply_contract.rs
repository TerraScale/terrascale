use std::fs;
use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};

use tb_core::{BudgetConfig, CommitSeq, Key, MemoryBudget, TenantId, TransactionId, WorkerId};
use tb_storage::{Mutation, PreparedBatchOutcome, Storage, StorageConfig, StorageError};

#[test]
fn synced_wal_apply_failure_poisons_until_reopen_then_replays_once() {
    let tick = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("clock")
        .as_nanos();
    let directory = std::env::temp_dir().join(format!(
        "terrabase-post-sync-apply-{}-{tick}",
        std::process::id()
    ));
    let config = StorageConfig::local(&directory, TenantId::from_bytes([1; 16]), WorkerId(1))
        .with_clock(Arc::new(|| 100));
    let budget = || MemoryBudget::new(BudgetConfig::new(64 * 1024 * 1024, 0).expect("budget"));
    let key = Key::new(b"recovered".to_vec()).expect("key");
    let mutation = Mutation::Put {
        key: key.clone(),
        value: b"value".to_vec(),
    };
    let transaction_id = TransactionId::new(500, [1; 16]);
    let storage = Storage::open(config.clone(), budget()).expect("open shard");
    let mut ready = match storage
        .prepare_batch(
            storage.snapshot(),
            transaction_id,
            std::slice::from_ref(&mutation),
        )
        .expect("prepare batch")
    {
        PreparedBatchOutcome::Ready(ready) => ready,
        PreparedBatchOutcome::AlreadyCommitted(_) => panic!("new token already committed"),
    };
    ready.inject_post_sync_apply_failure_for_test();
    assert!(matches!(ready.commit(), Err(StorageError::Poisoned)));
    assert!(matches!(
        storage.commit_batch(
            storage.snapshot(),
            TransactionId::new(500, [2; 16]),
            &[Mutation::Delete { key: key.clone() }]
        ),
        Err(StorageError::Poisoned)
    ));
    drop(storage);

    let reopened = Storage::open(config, budget()).expect("replay synced frame");
    assert_eq!(reopened.stats().visible, CommitSeq::new(1));
    assert_eq!(
        reopened
            .get(reopened.snapshot(), &key)
            .expect("read")
            .expect("replayed value")
            .bytes(),
        b"value"
    );
    assert_eq!(
        reopened
            .commit_batch(
                reopened.snapshot(),
                transaction_id,
                std::slice::from_ref(&mutation)
            )
            .expect("retry original token")
            .commit_seq,
        CommitSeq::new(1)
    );
    assert_eq!(reopened.stats().visible, CommitSeq::new(1));
    drop(reopened);
    fs::remove_dir_all(directory).expect("remove shard");
}
