use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use tb_core::{
    BudgetConfig, Key, KeyBound, KeyRange, MemoryBudget, MemoryCategory, TenantId, TransactionId,
    WorkerId,
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
            "terrabase-scan-{}-{tick}-{ordinal}",
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

fn storage(path: &Path) -> Storage {
    let config = StorageConfig::local(path, TenantId::from_bytes([1; 16]), WorkerId(1))
        .with_clock(Arc::new(|| 1_000));
    let budget = MemoryBudget::new(BudgetConfig::new(128 * 1024 * 1024, 0).expect("budget"));
    Storage::open(config, budget).expect("open shard")
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

fn commit(storage: &Storage, index: u64, mutations: &[Mutation]) {
    storage
        .commit_batch(storage.snapshot(), token(index), mutations)
        .expect("commit");
}

#[test]
fn empty_and_tombstone_scans_with_tight_budget_release_reservations() {
    let directory = TempShard::new();
    let mut config =
        StorageConfig::local(directory.path(), TenantId::from_bytes([1; 16]), WorkerId(1))
            .with_clock(Arc::new(|| 1_000));
    config.max_scan_rows = 1_000_000;
    config.max_scan_bytes = 1;
    let budget = MemoryBudget::new(BudgetConfig::new(1024 * 1024, 0).expect("budget"));
    let storage = Storage::open(config.clone(), budget.clone()).expect("open empty shard");
    let before = budget.snapshot().reserved_bytes;
    let page = storage
        .scan(storage.snapshot(), &KeyRange::all(), 1_000_000)
        .expect("empty scan");
    assert!(page.rows().is_empty());
    drop(page);
    assert_eq!(budget.snapshot().reserved_bytes, before);
    drop(storage);

    let seed_budget = MemoryBudget::new(BudgetConfig::new(128 * 1024 * 1024, 0).expect("budget"));
    let seed_storage = Storage::open(config.clone(), seed_budget).expect("open fixture shard");
    commit(&seed_storage, 1, &[put(b"a", b"x")]);
    commit(&seed_storage, 2, &[Mutation::Delete { key: key(b"a") }]);
    drop(seed_storage);

    let storage = Storage::open(config, budget.clone()).expect("reopen tight-budget shard");
    let range =
        KeyRange::new(KeyBound::Included(key(b"z")), KeyBound::Unbounded).expect("empty range");
    let before = budget.snapshot().reserved_bytes;
    let page = storage
        .scan(storage.snapshot(), &range, 1_000_000)
        .expect("nonmatching range scan");
    assert!(page.rows().is_empty());
    drop(page);
    assert_eq!(budget.snapshot().reserved_bytes, before);

    let before = budget.snapshot().reserved_bytes;
    let page = storage
        .scan(storage.snapshot(), &KeyRange::all(), 1_000_000)
        .expect("tombstone-only scan");
    assert!(page.rows().is_empty());
    drop(page);
    assert_eq!(budget.snapshot().reserved_bytes, before);
}

#[test]
fn long_shard_paths_are_charged_per_run_cursor() {
    let directory = TempShard::new();
    let long_path = (0..14).fold(directory.path().to_path_buf(), |path, index| {
        path.join(format!("{index:02}{}", "x".repeat(218)))
    });
    let mut config = StorageConfig::local(&long_path, TenantId::from_bytes([1; 16]), WorkerId(1))
        .with_clock(Arc::new(|| 1_000));
    config.max_runs = 2;
    let budget = MemoryBudget::new(
        BudgetConfig::new(128 * 1024 * 1024, 0)
            .expect("budget")
            .with_category_limit_bytes(MemoryCategory::ReadCache, 37 * 1024),
    );
    let seed_budget = MemoryBudget::new(BudgetConfig::new(128 * 1024 * 1024, 0).expect("budget"));
    let seed_storage =
        Storage::open(config.clone(), seed_budget).expect("open long-path fixture shard");
    for index in 0..2_u64 {
        let suffix = u8::try_from(index).expect("two run indexes");
        commit(&seed_storage, index + 1, &[put(&[b'a' + suffix], b"x")]);
        seed_storage.checkpoint().expect("checkpoint");
    }
    drop(seed_storage);
    let storage = Storage::open(config, budget.clone()).expect("reopen limited long-path shard");
    let range = KeyRange::new(KeyBound::Included(key(b"z")), KeyBound::Unbounded)
        .expect("nonmatching range");
    let before = budget.snapshot().reserved_bytes;
    assert!(matches!(
        storage.scan(storage.snapshot(), &range, 1),
        Err(StorageError::Admission(_))
    ));
    assert_eq!(budget.snapshot().reserved_bytes, before);
}

#[test]
fn mutable_tombstone_history_advances_once_and_sparse_tail_is_visible() {
    let directory = TempShard::new();
    let storage = storage(directory.path());
    let mut puts = Vec::new();
    let mut deletes = Vec::new();
    for index in 0..1024_u64 {
        let bytes = index.to_be_bytes();
        puts.push(put(&bytes, b"x"));
        if index < 1023 {
            deletes.push(Mutation::Delete { key: key(&bytes) });
        }
    }
    for (index, batch) in puts.chunks(128).enumerate() {
        commit(&storage, index as u64 + 1, batch);
    }
    for (index, batch) in deletes.chunks(128).enumerate() {
        commit(&storage, index as u64 + 9, batch);
    }
    let before = storage.stats().scan_work;
    let page = storage
        .scan(storage.snapshot(), &KeyRange::all(), 1)
        .expect("scan sparse tail");
    assert_eq!(page.rows().len(), 1);
    assert_eq!(page.rows()[0].key.as_bytes(), &1023_u64.to_be_bytes());
    let after = storage.stats().scan_work;
    assert!(after.mutable_keys_examined - before.mutable_keys_examined <= 1024);
    assert_eq!(after.point_lookup_calls - before.point_lookup_calls, 0);
}

#[test]
fn merge_preserves_old_snapshot_and_cross_run_tombstones() {
    let directory = TempShard::new();
    let storage = storage(directory.path());
    commit(&storage, 1, &[put(b"a", b"old"), put(b"z", b"old-z")]);
    storage.checkpoint().expect("first run");
    let old = storage.snapshot();
    commit(
        &storage,
        2,
        &[
            put(b"a", b"new"),
            Mutation::Delete { key: key(b"z") },
            put(b"b", b"new-b"),
        ],
    );
    storage.checkpoint().expect("second run");
    let old_rows = storage.scan(old, &KeyRange::all(), 10).expect("old scan");
    assert_eq!(
        old_rows
            .rows()
            .iter()
            .map(|row| (row.key.as_bytes(), row.value.as_slice()))
            .collect::<Vec<_>>(),
        vec![
            (b"a".as_slice(), b"old".as_slice()),
            (b"z".as_slice(), b"old-z".as_slice())
        ]
    );
    let range =
        KeyRange::new(KeyBound::Included(key(b"a")), KeyBound::Excluded(key(b"z"))).expect("range");
    let new_rows = storage
        .scan(storage.snapshot(), &range, 10)
        .expect("new scan");
    assert_eq!(
        new_rows
            .rows()
            .iter()
            .map(|row| (row.key.as_bytes(), row.value.as_slice()))
            .collect::<Vec<_>>(),
        vec![
            (b"a".as_slice(), b"new".as_slice()),
            (b"b".as_slice(), b"new-b".as_slice())
        ]
    );
}

#[test]
fn scans_across_maximum_tiny_run_fan_in_with_bounded_cache() {
    const CHILD: &str = "TERRABASE_SCAN_NOFILE_CHILD";
    if std::env::var_os(CHILD).is_none() {
        let executable = std::env::current_exe().expect("test executable");
        let output = Command::new("/usr/bin/prlimit")
            .args(["--nofile=64:64", "--"])
            .arg(executable)
            .args([
                "--exact",
                "storage_slow::scan_contract::scans_across_maximum_tiny_run_fan_in_with_bounded_cache",
                "--nocapture",
            ])
            .env(CHILD, "1")
            .output()
            .expect("run scan fixture under guest nofile limit");
        assert!(
            output.status.success(),
            "low-FD scan child failed: {}\n{}",
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        );
        assert!(
            String::from_utf8_lossy(&output.stdout).contains("running 1 test"),
            "low-FD child did not execute its selected test: {}",
            String::from_utf8_lossy(&output.stdout)
        );
        return;
    }
    let limits = fs::read_to_string("/proc/self/limits").expect("read guest process limits");
    let open_files = limits
        .lines()
        .find(|line| line.starts_with("Max open files"))
        .expect("guest nofile row");
    let mut values = open_files.split_whitespace().skip(3);
    assert_eq!(values.next(), Some("64"), "guest soft nofile limit");
    assert_eq!(values.next(), Some("64"), "guest hard nofile limit");
    let directory = TempShard::new();
    let mut config =
        StorageConfig::local(directory.path(), TenantId::from_bytes([1; 16]), WorkerId(1))
            .with_clock(Arc::new(|| 1_000));
    config.format.max_records_per_block = 1;
    let budget = MemoryBudget::new(BudgetConfig::new(128 * 1024 * 1024, 0).expect("budget"));
    let storage = Storage::open(config, budget).expect("open shard");
    for index in 0..128_u64 {
        commit(
            &storage,
            index + 1,
            &[
                put(&index.to_be_bytes(), b"x"),
                put(&(index + 128).to_be_bytes(), b"x"),
            ],
        );
        storage.checkpoint().expect("checkpoint tiny run");
    }
    assert_eq!(storage.stats().immutable_runs, 128);
    let before = storage.stats().scan_work;
    let page = storage
        .scan(storage.snapshot(), &KeyRange::all(), 256)
        .expect("scan 128 runs");
    assert_eq!(page.rows().len(), 256);
    for (index, row) in page.rows().iter().enumerate() {
        assert_eq!(row.key.as_bytes(), &(index as u64).to_be_bytes());
    }
    let after = storage.stats().scan_work;
    assert_eq!(after.point_lookup_calls - before.point_lookup_calls, 0);
    assert!(after.run_file_opens - before.run_file_opens > 128);
}

#[test]
fn corrupt_checked_block_fails_scan() {
    let directory = TempShard::new();
    let storage = storage(directory.path());
    commit(&storage, 1, &[put(b"a", b"value")]);
    storage.checkpoint().expect("checkpoint");
    let path = directory.path().join("run-00000000000000000001.seg");
    let mut bytes = fs::read(&path).expect("read run");
    bytes[50] ^= 1;
    fs::write(&path, bytes).expect("corrupt block");
    assert!(matches!(
        storage.scan(storage.snapshot(), &KeyRange::all(), 1),
        Err(StorageError::Format(_))
    ));
}

#[test]
fn dense_run_eviction_reloads_checked_block_without_losing_position() {
    let directory = TempShard::new();
    let storage = storage(directory.path());
    let value = vec![b'x'; 512 * 1024];
    for index in 0..16_u64 {
        let first = index.to_be_bytes();
        let second = (index + 16).to_be_bytes();
        commit(
            &storage,
            index + 1,
            &[put(&first, &value), put(&second, &value)],
        );
        storage.checkpoint().expect("checkpoint dense run");
    }
    let before = storage.stats().scan_work;
    let page = storage
        .scan(storage.snapshot(), &KeyRange::all(), 1)
        .expect("scan dense runs");
    assert_eq!(page.rows().len(), 1);
    assert_eq!(page.rows()[0].key.as_bytes(), &0_u64.to_be_bytes());
    assert_eq!(page.rows()[0].value, value);
    let after = storage.stats().scan_work;
    assert!(after.run_block_cache_evictions - before.run_block_cache_evictions > 0);
    assert!(after.run_block_rereads - before.run_block_rereads > 0);
    assert_eq!(after.point_lookup_calls - before.point_lookup_calls, 0);
}
