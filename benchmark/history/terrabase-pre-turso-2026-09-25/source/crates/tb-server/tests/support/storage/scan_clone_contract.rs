use std::fs;
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use tb_core::{
    BudgetConfig, Key, KeyRange, MemoryBudget, ReadSnapshot, TenantId, TransactionId, WorkerId,
};
use tb_storage::{Mutation, Storage, StorageConfig};

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
            "terrabase-scan-clone-{}-{tick}-{ordinal}",
            std::process::id()
        )))
    }
}

impl Drop for TempShard {
    fn drop(&mut self) {
        if self.0.exists() {
            fs::remove_dir_all(&self.0).expect("remove shard");
        }
    }
}

fn key(bytes: &[u8]) -> Key {
    Key::new(bytes.to_vec()).expect("key")
}

fn token(index: u64) -> TransactionId {
    let mut nonce = [0; 16];
    nonce[..8].copy_from_slice(&index.to_be_bytes());
    TransactionId::new(50_000, nonce)
}

fn commit(storage: &Storage, index: u64, mutations: &[Mutation]) {
    storage
        .commit_batch(storage.snapshot(), token(index), mutations)
        .expect("commit");
}

fn rows(storage: &Storage, snapshot: ReadSnapshot, limit: usize) -> Vec<(Vec<u8>, Vec<u8>)> {
    storage
        .scan(snapshot, &KeyRange::all(), limit)
        .expect("scan")
        .rows()
        .iter()
        .map(|row| (row.key.as_bytes().to_vec(), row.value.clone()))
        .collect()
}

#[test]
fn checkpointed_tombstones_mutable_winners_and_old_snapshot() {
    let directory = TempShard::new();
    let config = StorageConfig::local(&directory.0, TenantId::from_bytes([1; 16]), WorkerId(1))
        .with_clock(Arc::new(|| 1_000));
    let budget = MemoryBudget::new(BudgetConfig::new(128 * 1024 * 1024, 0).expect("budget"));
    let storage = Storage::open(config, budget).expect("open shard");

    commit(
        &storage,
        1,
        &[
            Mutation::Put {
                key: key(b"a"),
                value: b"old-a".to_vec(),
            },
            Mutation::Put {
                key: key(b"b"),
                value: b"old-b".to_vec(),
            },
            Mutation::Put {
                key: key(b"c"),
                value: b"old-c".to_vec(),
            },
            Mutation::Put {
                key: key(b"z"),
                value: b"old-z".to_vec(),
            },
        ],
    );
    storage.checkpoint().expect("first run");
    let old = storage.snapshot();

    commit(
        &storage,
        2,
        &[
            Mutation::Delete { key: key(b"a") },
            Mutation::Delete { key: key(b"b") },
            Mutation::Delete { key: key(b"c") },
        ],
    );
    storage.checkpoint().expect("second run");
    assert_eq!(
        rows(&storage, storage.snapshot(), 1),
        vec![(b"z".to_vec(), b"old-z".to_vec())]
    );

    commit(
        &storage,
        3,
        &[
            Mutation::Put {
                key: key(b"b"),
                value: b"new-b".to_vec(),
            },
            Mutation::Delete { key: key(b"z") },
        ],
    );
    assert_eq!(
        rows(&storage, storage.snapshot(), 10),
        vec![(b"b".to_vec(), b"new-b".to_vec())]
    );

    commit(&storage, 4, &[Mutation::Delete { key: key(b"b") }]);
    assert!(rows(&storage, storage.snapshot(), 1).is_empty());
    assert_eq!(
        rows(&storage, old, 10),
        vec![
            (b"a".to_vec(), b"old-a".to_vec()),
            (b"b".to_vec(), b"old-b".to_vec()),
            (b"c".to_vec(), b"old-c".to_vec()),
            (b"z".to_vec(), b"old-z".to_vec()),
        ]
    );
}
