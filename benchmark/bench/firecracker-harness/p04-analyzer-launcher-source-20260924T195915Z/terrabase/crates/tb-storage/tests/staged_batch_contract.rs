use std::fs::{self, OpenOptions};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use tb_core::{BudgetConfig, Key, MemoryBudget, MemoryCategory, TenantId, TransactionId, WorkerId};
use tb_storage::{
    Mutation, PlannedBatch, PreparedBatchOutcome, StageWorkPlan, Storage, StorageConfig,
    StorageError,
};

static NEXT_DIRECTORY: AtomicU64 = AtomicU64::new(0);

struct TempShard(PathBuf);

impl TempShard {
    fn new() -> Self {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let ordinal = NEXT_DIRECTORY.fetch_add(1, Ordering::Relaxed);
        Self(std::env::temp_dir().join(format!(
            "terrabase-staged-{}-{nanos}-{ordinal}",
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
            fs::remove_dir_all(&self.0).unwrap();
        }
    }
}

fn config(path: &Path) -> StorageConfig {
    let mut config = StorageConfig::local(path, TenantId::from_bytes([1; 16]), WorkerId(1))
        .with_clock(Arc::new(|| 100));
    config.format.max_frame_bytes = 4096;
    config.max_mutable_bytes = 4096 + 128;
    config.max_disk_bytes = 16_000;
    config.max_runs = 6;
    config
}

fn budget(bytes: usize) -> MemoryBudget {
    MemoryBudget::new(BudgetConfig::new(bytes, 0).unwrap())
}

fn token(nonce: u8) -> TransactionId {
    TransactionId::new(1_000, [nonce; 16])
}

fn put(key: &[u8], value: &[u8]) -> Mutation {
    Mutation::Put {
        key: Key::new(key.to_vec()).unwrap(),
        value: value.to_vec(),
    }
}

fn plan(config: &StorageConfig, stage: &[Mutation], final_batch: &[Mutation]) -> StageWorkPlan {
    plan_with_tokens(config, stage, final_batch, 2, 3)
}

fn plan_with_tokens(
    config: &StorageConfig,
    stage: &[Mutation],
    final_batch: &[Mutation],
    stage_nonce: u8,
    final_nonce: u8,
) -> StageWorkPlan {
    StageWorkPlan::new(
        vec![PlannedBatch::exact(&config.format, token(stage_nonce), stage).unwrap()],
        PlannedBatch::exact(&config.format, token(final_nonce), final_batch).unwrap(),
    )
}

#[test]
fn exact_disk_and_memory_claim_survive_stage_checkpoint_and_final_guard() {
    let directory = TempShard::new();
    let config = config(directory.path());
    let large = vec![put(b"old", &vec![7; 4_010])];
    let stage = vec![put(b"stage", b"one")];
    let final_batch = vec![put(b"final", b"two")];
    let generous = budget(128 * 1024 * 1024);
    let storage = Storage::open(config.clone(), generous.clone()).unwrap();
    storage
        .commit_batch(storage.snapshot(), token(1), &large)
        .unwrap();

    let stage_size = PlannedBatch::exact(&config.format, token(2), &stage)
        .unwrap()
        .footprint();
    let final_size = PlannedBatch::exact(&config.format, token(3), &final_batch)
        .unwrap()
        .footprint();
    let manifest_bytes = 44 + config.max_runs * 48;
    let claim = stage_size.frame_bytes
        + final_size.frame_bytes
        + storage.stats().mutable_bytes
        + stage_size.mutable_bytes
        + final_size.mutable_bytes
        + (2 * b"old".len() + 24)
        + stage_size.directory_bytes
        + final_size.directory_bytes
        + 76 * 3
        + manifest_bytes * 3
        + 32;
    let pad = config.max_disk_bytes - storage.stats().disk_bytes - claim as u64;
    OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(directory.path().join("quota.pad"))
        .unwrap()
        .set_len(pad)
        .unwrap();

    let first = storage
        .reserve_stage_work(plan(&config, &stage, &final_batch))
        .unwrap();
    let exact_memory = generous.snapshot().reserved_bytes;
    drop(first);
    drop(storage);
    let exact_budget = budget(exact_memory);
    let storage = Storage::open(config.clone(), exact_budget.clone()).unwrap();
    let mut work = storage
        .reserve_stage_work(plan(&config, &stage, &final_batch))
        .unwrap();
    assert_eq!(exact_budget.snapshot().reserved_bytes, exact_memory);
    assert_eq!(
        storage.stats().disk_bytes + claim as u64,
        config.max_disk_bytes
    );
    work.commit_stage(storage.snapshot(), token(2), &stage)
        .unwrap();
    assert_eq!(storage.stats().immutable_runs, 1);
    assert_eq!(storage.stats().checkpoint.get(), 1);
    let ready = match work
        .prepare_final(storage.snapshot(), token(3), &final_batch)
        .unwrap()
    {
        PreparedBatchOutcome::Ready(ready) => ready,
        PreparedBatchOutcome::AlreadyCommitted(_) => panic!("new final batch"),
    };
    assert!(ready.encoded_len() > 0);
    ready.commit().unwrap();
    assert_eq!(storage.stats().visible.get(), 3);
    assert!(storage.stats().disk_bytes <= config.max_disk_bytes);
    fs::remove_file(directory.path().join("quota.pad")).unwrap();
    let _next = storage
        .reserve_stage_work(StageWorkPlan::new(
            vec![],
            PlannedBatch::exact(&config.format, token(4), &[put(b"next", b"ok")]).unwrap(),
        ))
        .unwrap();
}

#[test]
fn stage_reservation_refuses_one_byte_under_disk_claim_before_any_stage_write() {
    let directory = TempShard::new();
    let config = config(directory.path());
    let stage = vec![put(b"stage", b"one")];
    let final_batch = vec![put(b"final", b"two")];
    let stage_size = PlannedBatch::exact(&config.format, token(2), &stage)
        .unwrap()
        .footprint();
    let final_size = PlannedBatch::exact(&config.format, token(3), &final_batch)
        .unwrap()
        .footprint();
    let manifest_bytes = 44 + config.max_runs * 48;
    let disk_claim = stage_size.frame_bytes
        + final_size.frame_bytes
        + stage_size.mutable_bytes
        + final_size.mutable_bytes
        + stage_size.directory_bytes
        + final_size.directory_bytes
        + 76 * 3
        + manifest_bytes * 3
        + 32;
    let storage = Storage::open(config.clone(), budget(128 * 1024 * 1024)).unwrap();
    let baseline_disk = storage.stats().disk_bytes;
    let pad_bytes = config.max_disk_bytes - baseline_disk - disk_claim as u64 + 1;
    assert!(pad_bytes > 0);
    let pad_path = directory.path().join("quota.pad");
    OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(&pad_path)
        .unwrap()
        .set_len(pad_bytes)
        .unwrap();
    let before = storage.stats();
    assert_eq!(
        before.disk_bytes + disk_claim as u64,
        config.max_disk_bytes + 1
    );
    assert!(matches!(
        storage.reserve_stage_work(plan(&config, &stage, &final_batch)),
        Err(StorageError::Capacity("SSD bytes and stage headroom"))
    ));
    let after_refusal = storage.stats();
    assert_eq!(after_refusal.visible, before.visible);
    assert_eq!(after_refusal.wal_bytes, before.wal_bytes);
    assert_eq!(after_refusal.immutable_runs, before.immutable_runs);
    assert_eq!(after_refusal.disk_bytes, before.disk_bytes);
    assert!(
        storage
            .get(storage.snapshot(), &Key::new(b"stage".to_vec()).unwrap())
            .unwrap()
            .is_none()
    );

    OpenOptions::new()
        .write(true)
        .open(&pad_path)
        .unwrap()
        .set_len(pad_bytes - 1)
        .unwrap();
    assert_eq!(
        storage.stats().disk_bytes + disk_claim as u64,
        config.max_disk_bytes
    );
    let mut work = storage
        .reserve_stage_work(plan(&config, &stage, &final_batch))
        .unwrap();
    work.commit_stage(storage.snapshot(), token(2), &stage)
        .unwrap();
    let ready = match work
        .prepare_final(storage.snapshot(), token(3), &final_batch)
        .unwrap()
    {
        PreparedBatchOutcome::Ready(ready) => ready,
        PreparedBatchOutcome::AlreadyCommitted(_) => panic!("new final batch"),
    };
    ready.commit().unwrap();
    assert_eq!(storage.stats().visible.get(), 2);
}

#[test]
fn dedup_capacity_checkpoints_old_state_then_refuses_without_appending() {
    let directory = TempShard::new();
    let mut config = config(directory.path());
    config.max_live_transactions = 1;
    let storage = Storage::open(config, budget(128 * 1024 * 1024)).unwrap();
    let old = vec![put(b"old", b"retained")];
    storage
        .commit_batch(storage.snapshot(), token(1), &old)
        .unwrap();
    let before = storage.stats();

    assert!(matches!(
        storage.commit_batch(storage.snapshot(), token(2), &[put(b"new", b"rejected")]),
        Err(StorageError::Capacity("live transaction resolution table"))
    ));

    let after = storage.stats();
    assert_eq!(after.checkpoint.get(), 1);
    assert_eq!(after.immutable_runs, 1);
    assert_eq!(after.visible, before.visible);
    assert_eq!(after.wal_bytes, before.wal_bytes);
    assert_eq!(after.live_transaction_ids, 1);
    assert_eq!(
        storage
            .get(storage.snapshot(), &Key::new(b"old".to_vec()).unwrap())
            .unwrap()
            .unwrap()
            .bytes(),
        b"retained"
    );
    assert!(
        storage
            .get(storage.snapshot(), &Key::new(b"new".to_vec()).unwrap())
            .unwrap()
            .is_none()
    );
}

#[test]
fn stage_reserves_dedup_slots_from_raw_prepare_until_final_commit() {
    let directory = TempShard::new();
    let mut config = config(directory.path());
    config.max_live_transactions = 2;
    let storage = Storage::open(config.clone(), budget(128 * 1024 * 1024)).unwrap();
    let stage = vec![put(b"stage", b"one")];
    let final_batch = vec![put(b"final", b"two")];
    let mut work = storage
        .reserve_stage_work(plan(&config, &stage, &final_batch))
        .unwrap();

    assert!(matches!(
        storage.prepare_batch(storage.snapshot(), token(4), &[put(b"raw", b"rejected")]),
        Err(StorageError::Capacity("stage run or dedup slots"))
    ));
    assert_eq!(storage.stats().visible.get(), 0);
    assert_eq!(storage.stats().wal_bytes, 0);
    assert_eq!(storage.stats().live_transaction_ids, 0);

    work.commit_stage(storage.snapshot(), token(2), &stage)
        .unwrap();
    let ready = match work
        .prepare_final(storage.snapshot(), token(3), &final_batch)
        .unwrap()
    {
        PreparedBatchOutcome::Ready(ready) => ready,
        PreparedBatchOutcome::AlreadyCommitted(_) => panic!("new final batch"),
    };
    ready.commit().unwrap();
    assert_eq!(storage.stats().visible.get(), 2);
    assert_eq!(storage.stats().live_transaction_ids, 2);
}

#[test]
fn stage_reserves_run_slots_from_raw_checkpoint_until_final_commit() {
    let directory = TempShard::new();
    let mut config = config(directory.path());
    config.max_runs = 3;
    let storage = Storage::open(config.clone(), budget(128 * 1024 * 1024)).unwrap();
    let stage = vec![put(b"stage", b"one")];
    let final_batch = vec![put(b"final", b"two")];
    let mut work = storage
        .reserve_stage_work(plan(&config, &stage, &final_batch))
        .unwrap();
    storage
        .commit_batch(storage.snapshot(), token(4), &[put(b"raw", b"retained")])
        .unwrap();
    let before_checkpoint = storage.stats();
    let next_run = directory.path().join("run-00000000000000000001.seg");

    assert!(matches!(
        storage.checkpoint(),
        Err(StorageError::Capacity(
            "immutable run count and stage slots"
        ))
    ));
    let after_checkpoint = storage.stats();
    assert_eq!(after_checkpoint.checkpoint, before_checkpoint.checkpoint);
    assert_eq!(after_checkpoint.immutable_runs, 0);
    assert_eq!(after_checkpoint.visible, before_checkpoint.visible);
    assert_eq!(after_checkpoint.wal_bytes, before_checkpoint.wal_bytes);
    assert!(!next_run.exists());

    work.commit_stage(storage.snapshot(), token(2), &stage)
        .unwrap();
    let ready = match work
        .prepare_final(storage.snapshot(), token(3), &final_batch)
        .unwrap()
    {
        PreparedBatchOutcome::Ready(ready) => ready,
        PreparedBatchOutcome::AlreadyCommitted(_) => panic!("new final batch"),
    };
    ready.commit().unwrap();
    assert_eq!(storage.stats().visible.get(), 3);
    assert_eq!(storage.stats().immutable_runs, 0);
}

#[test]
fn directory_threshold_checkpoints_before_stage_commit_and_preserves_ledger() {
    let directory = TempShard::new();
    let mut config = config(directory.path());
    config.format.max_key_bytes = 4;
    config.format.max_directory_bytes = 50;
    let memory = budget(128 * 1024 * 1024);
    let storage = Storage::open(config.clone(), memory).unwrap();
    let old = vec![put(b"a", b"old")];
    let stage = vec![put(b"b", b"stage")];
    let final_batch = vec![put(b"c", b"final")];
    storage
        .commit_batch(storage.snapshot(), token(1), &old)
        .unwrap();
    let mut work = storage
        .reserve_stage_work(plan(&config, &stage, &final_batch))
        .unwrap();

    work.commit_stage(storage.snapshot(), token(2), &stage)
        .unwrap();
    let stats = storage.stats();
    assert_eq!(stats.checkpoint.get(), 1);
    assert_eq!(stats.immutable_runs, 1);
    assert_eq!(stats.visible.get(), 2);
    assert_eq!(
        storage
            .get(storage.snapshot(), &Key::new(b"a".to_vec()).unwrap())
            .unwrap()
            .unwrap()
            .bytes(),
        b"old"
    );
    assert_eq!(
        storage
            .get(storage.snapshot(), &Key::new(b"b".to_vec()).unwrap())
            .unwrap()
            .unwrap()
            .bytes(),
        b"stage"
    );
    drop(work);
}

#[test]
fn raw_item_retry_checkpoints_without_consuming_reserved_stage_claims() {
    let directory = TempShard::new();
    let config = config(directory.path());
    let budget_config = BudgetConfig::new(128 * 1024 * 1024, 0)
        .unwrap()
        .with_category_item_limit(MemoryCategory::TransactionVersion, 4);
    let memory = MemoryBudget::new(budget_config);
    let storage = Storage::open(config.clone(), memory.clone()).unwrap();
    storage
        .commit_batch(storage.snapshot(), token(1), &[put(b"old", b"value")])
        .unwrap();

    let stage = vec![put(b"stage", b"value")];
    let final_batch = vec![put(b"final", b"value")];
    let mut work = storage
        .reserve_stage_work(plan(&config, &stage, &final_batch))
        .unwrap();
    let raw_batch = vec![put(b"raw-a", b"a"), put(b"raw-b", b"b")];
    let ready = match storage
        .prepare_batch(storage.snapshot(), token(4), &raw_batch)
        .unwrap()
    {
        PreparedBatchOutcome::Ready(ready) => ready,
        PreparedBatchOutcome::AlreadyCommitted(_) => panic!("new raw transaction"),
    };
    ready.commit().unwrap();
    assert_eq!(storage.stats().checkpoint.get(), 1);
    assert_eq!(storage.stats().immutable_runs, 1);

    work.commit_stage(storage.snapshot(), token(2), &stage)
        .unwrap();
    let ready = match work
        .prepare_final(storage.snapshot(), token(3), &final_batch)
        .unwrap()
    {
        PreparedBatchOutcome::Ready(ready) => ready,
        PreparedBatchOutcome::AlreadyCommitted(_) => panic!("new final transaction"),
    };
    ready.commit().unwrap();
    assert_eq!(storage.stats().visible.get(), 4);
    assert_eq!(
        memory.snapshot().category_items[MemoryCategory::TransactionVersion as usize],
        4
    );
    drop(storage);
    assert_eq!(
        memory.snapshot().category_items[MemoryCategory::TransactionVersion as usize],
        0
    );
}

#[test]
fn dropping_stage_token_while_ready_holds_guard_is_bounded_and_releases_claims() {
    for commit_ready in [false, true] {
        stage_token_drop_handoff_case(commit_ready);
    }
}

fn stage_token_drop_handoff_case(commit_ready: bool) {
    let directory = TempShard::new();
    let path = directory.path().to_path_buf();
    let (sender, receiver) = std::sync::mpsc::sync_channel(1);
    std::thread::spawn(move || {
        let config = config(&path);
        let storage = Storage::open(config.clone(), budget(128 * 1024 * 1024)).unwrap();
        let stage = vec![put(b"stage", b"value")];
        let final_batch = vec![put(b"final", b"value")];
        let work = storage
            .reserve_stage_work(plan(&config, &stage, &final_batch))
            .unwrap();
        let ready = match storage
            .prepare_batch(storage.snapshot(), token(4), &[put(b"raw", b"value")])
            .unwrap()
        {
            PreparedBatchOutcome::Ready(ready) => ready,
            PreparedBatchOutcome::AlreadyCommitted(_) => panic!("new raw transaction"),
        };

        // Dropping the live reservation must not wait on Ready's held guard.
        drop(work);
        if commit_ready {
            ready.commit().unwrap();
            assert_eq!(storage.stats().visible.get(), 1);
        } else {
            drop(ready);
            assert_eq!(storage.stats().visible.get(), 0);
            assert_eq!(storage.stats().wal_bytes, 0);
        }
        let next = storage
            .reserve_stage_work(plan(&config, &stage, &final_batch))
            .unwrap();
        drop(next);
        drop(storage);
        sender.send(()).unwrap();
    });
    receiver
        .recv_timeout(Duration::from_secs(5))
        .expect("stage-token drop or Ready release deadlocked");
}

#[test]
fn raw_checkpoint_quota_refusal_preserves_next_run_orphan_until_positive_control() {
    let directory = TempShard::new();
    let config = config(directory.path());
    let storage = Storage::open(config.clone(), budget(128 * 1024 * 1024)).unwrap();
    storage
        .commit_batch(storage.snapshot(), token(1), &[put(b"old", b"value")])
        .unwrap();
    assert_eq!(storage.stats().checkpoint.get(), 0);

    let next_run = directory.path().join("run-00000000000000000001.seg");
    let orphan = b"known orphan at the exact next checkpoint run path";
    fs::write(&next_run, orphan).unwrap();
    let before_padding = storage.stats().disk_bytes;
    assert!(before_padding < config.max_disk_bytes);
    let padding = OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(directory.path().join("quota.pad"))
        .unwrap();
    padding
        .set_len(config.max_disk_bytes - before_padding)
        .unwrap();
    let before = storage.stats();
    assert_eq!(before.disk_bytes, config.max_disk_bytes);

    assert!(matches!(
        storage.checkpoint(),
        Err(StorageError::Capacity(_))
    ));
    assert_eq!(fs::read(&next_run).unwrap(), orphan);
    assert!(
        !directory
            .path()
            .join("run-00000000000000000001.pending")
            .exists()
    );
    let after = storage.stats();
    assert_eq!(after.checkpoint, before.checkpoint);
    assert_eq!(after.immutable_runs, before.immutable_runs);
    assert_eq!(after.visible, before.visible);
    assert_eq!(after.wal_bytes, before.wal_bytes);
    assert_eq!(after.disk_bytes, before.disk_bytes);

    // With quota released, the same next run path is replaced by a valid run.
    fs::remove_file(directory.path().join("quota.pad")).unwrap();
    storage.checkpoint().unwrap();
    assert_eq!(storage.stats().checkpoint.get(), 1);
    assert_eq!(storage.stats().immutable_runs, 1);
    assert_ne!(fs::read(&next_run).unwrap(), orphan);
    assert!(
        !directory
            .path()
            .join("run-00000000000000000001.pending")
            .exists()
    );
}

#[cfg(feature = "test-hooks")]
#[test]
fn uncertain_stage_sync_poisons_retires_and_replans_after_reopen() {
    let directory = TempShard::new();
    let config = config(directory.path());
    let memory = budget(128 * 1024 * 1024);
    let stage = vec![put(b"stage-before-reopen", b"retained")];
    let abandoned_final = vec![put(b"abandoned-final", b"absent")];
    let storage = Storage::open(config.clone(), memory.clone()).unwrap();
    let mut work = storage
        .reserve_stage_work(plan(&config, &stage, &abandoned_final))
        .unwrap();
    storage.inject_wal_fault_before_sync_for_test();

    assert!(matches!(
        work.commit_stage(storage.snapshot(), token(2), &stage),
        Err(StorageError::Io(_))
    ));
    assert!(matches!(
        work.commit_stage(storage.snapshot(), token(2), &stage),
        Err(StorageError::Capacity("stage work is no longer active"))
    ));
    assert!(matches!(
        storage.commit_batch(storage.snapshot(), token(4), &[put(b"poisoned", b"write")]),
        Err(StorageError::Poisoned)
    ));
    drop(work);
    drop(storage);

    let reopened = Storage::open(config.clone(), memory).unwrap();
    assert_eq!(reopened.stats().visible.get(), 1);
    assert_eq!(
        reopened
            .get(
                reopened.snapshot(),
                &Key::new(b"stage-before-reopen".to_vec()).unwrap()
            )
            .unwrap()
            .unwrap()
            .bytes(),
        b"retained"
    );
    assert!(
        reopened
            .get(
                reopened.snapshot(),
                &Key::new(b"abandoned-final".to_vec()).unwrap()
            )
            .unwrap()
            .is_none()
    );

    let replanned_stage = vec![put(b"stage-after-reopen", b"new-stage")];
    let replanned_final = vec![put(b"final-after-reopen", b"new-final")];
    let mut replanned = reopened
        .reserve_stage_work(plan_with_tokens(
            &config,
            &replanned_stage,
            &replanned_final,
            5,
            6,
        ))
        .unwrap();
    replanned
        .commit_stage(reopened.snapshot(), token(5), &replanned_stage)
        .unwrap();
    let ready = match replanned
        .prepare_final(reopened.snapshot(), token(6), &replanned_final)
        .unwrap()
    {
        PreparedBatchOutcome::Ready(ready) => ready,
        PreparedBatchOutcome::AlreadyCommitted(_) => panic!("new replanned final"),
    };
    ready.commit().unwrap();
    assert_eq!(reopened.stats().visible.get(), 3);
    assert_eq!(
        reopened
            .get(
                reopened.snapshot(),
                &Key::new(b"final-after-reopen".to_vec()).unwrap()
            )
            .unwrap()
            .unwrap()
            .bytes(),
        b"new-final"
    );
}

#[cfg(feature = "test-hooks")]
#[test]
fn partial_stage_write_retains_physical_prefix_until_reopen_then_replans() {
    const PREFIX_BYTES: usize = 8;

    let directory = TempShard::new();
    let config = config(directory.path());
    let memory = budget(128 * 1024 * 1024);
    let stage = vec![put(b"partial-stage", b"discarded")];
    let abandoned_final = vec![put(b"abandoned-final", b"absent")];
    let storage = Storage::open(config.clone(), memory.clone()).unwrap();
    let disk_before = storage.stats().disk_bytes;
    let mut work = storage
        .reserve_stage_work(plan(&config, &stage, &abandoned_final))
        .unwrap();
    storage.inject_partial_wal_write_for_test(PREFIX_BYTES);

    assert!(matches!(
        work.commit_stage(storage.snapshot(), token(2), &stage),
        Err(StorageError::Io(_))
    ));
    assert!(matches!(
        work.commit_stage(storage.snapshot(), token(2), &stage),
        Err(StorageError::Capacity("stage work is no longer active"))
    ));
    assert!(matches!(
        storage.commit_batch(storage.snapshot(), token(4), &[put(b"poisoned", b"write")]),
        Err(StorageError::Poisoned)
    ));
    let before_reopen = storage.stats();
    assert_eq!(before_reopen.visible.get(), 0);
    assert_eq!(before_reopen.wal_bytes, 0);
    assert_eq!(before_reopen.disk_bytes, disk_before + PREFIX_BYTES as u64);
    drop(work);
    drop(storage);

    let reopened = Storage::open(config.clone(), memory).unwrap();
    assert_eq!(reopened.stats().visible.get(), 0);
    assert_eq!(reopened.stats().wal_bytes, 0);
    assert_eq!(reopened.stats().disk_bytes, disk_before);

    let replanned_stage = vec![put(b"stage-after-partial", b"retained")];
    let replanned_final = vec![put(b"final-after-partial", b"retained")];
    let mut replanned = reopened
        .reserve_stage_work(plan_with_tokens(
            &config,
            &replanned_stage,
            &replanned_final,
            5,
            6,
        ))
        .unwrap();
    replanned
        .commit_stage(reopened.snapshot(), token(5), &replanned_stage)
        .unwrap();
    let ready = match replanned
        .prepare_final(reopened.snapshot(), token(6), &replanned_final)
        .unwrap()
    {
        PreparedBatchOutcome::Ready(ready) => ready,
        PreparedBatchOutcome::AlreadyCommitted(_) => panic!("new replanned final"),
    };
    ready.commit().unwrap();
    assert_eq!(reopened.stats().visible.get(), 2);
    assert_eq!(
        reopened
            .get(
                reopened.snapshot(),
                &Key::new(b"stage-after-partial".to_vec()).unwrap()
            )
            .unwrap()
            .unwrap()
            .bytes(),
        b"retained"
    );
    assert!(
        reopened
            .get(
                reopened.snapshot(),
                &Key::new(b"partial-stage".to_vec()).unwrap()
            )
            .unwrap()
            .is_none()
    );
}

#[test]
fn mismatched_stage_never_appends_and_drop_releases_claims() {
    let directory = TempShard::new();
    let config = config(directory.path());
    let memory = budget(128 * 1024 * 1024);
    let storage = Storage::open(config.clone(), memory.clone()).unwrap();
    let stage = vec![put(b"stage", b"one")];
    let final_batch = vec![put(b"final", b"two")];
    let baseline = memory.snapshot();
    let mut work = storage
        .reserve_stage_work(plan(&config, &stage, &final_batch))
        .unwrap();
    assert!(matches!(
        work.commit_stage(storage.snapshot(), token(2), &[put(b"changed", b"one")]),
        Err(StorageError::InvalidBatch("stage plan mismatch"))
    ));
    assert_eq!(storage.stats().wal_bytes, 0);
    drop(work);
    assert_eq!(memory.snapshot().reserved_bytes, baseline.reserved_bytes);
    assert_eq!(memory.snapshot().reserved_items, baseline.reserved_items);
    storage
        .commit_batch(storage.snapshot(), token(4), &stage)
        .unwrap();
}

#[test]
fn post_split_conflict_retires_stage_token_and_releases_claims() {
    let directory = TempShard::new();
    let config = config(directory.path());
    let memory = budget(128 * 1024 * 1024);
    let storage = Storage::open(config.clone(), memory.clone()).unwrap();
    let stale_snapshot = storage.snapshot();
    let stage = vec![put(b"conflicting-key", b"stage")];
    let final_batch = vec![put(b"final", b"two")];
    let memory_before_stage = memory.snapshot();
    let mut work = storage
        .reserve_stage_work(plan(&config, &stage, &final_batch))
        .unwrap();
    let memory_with_stage = memory.snapshot();
    storage
        .commit_batch(
            storage.snapshot(),
            token(4),
            &[put(b"conflicting-key", b"raw")],
        )
        .unwrap();
    let after_raw = storage.stats();
    let memory_after_raw = memory.snapshot();

    assert!(matches!(
        work.commit_stage(stale_snapshot, token(2), &stage),
        Err(StorageError::Conflict)
    ));
    assert_eq!(storage.stats().visible, after_raw.visible);
    assert_eq!(storage.stats().wal_bytes, after_raw.wal_bytes);
    assert!(matches!(
        work.commit_stage(storage.snapshot(), token(2), &stage),
        Err(StorageError::Capacity("stage work is no longer active"))
    ));
    let after_retirement = memory.snapshot();
    for category in [
        MemoryCategory::WriteBuffer,
        MemoryCategory::TransactionVersion,
        MemoryCategory::DatabaseState,
        MemoryCategory::Compaction,
        MemoryCategory::ReadCache,
    ] {
        let index = category as usize;
        assert!(memory_with_stage.categories[index] > memory_before_stage.categories[index]);
        assert!(after_retirement.categories[index] < memory_after_raw.categories[index]);
    }
    for category in [
        MemoryCategory::TransactionVersion,
        MemoryCategory::DatabaseState,
    ] {
        let index = category as usize;
        assert!(after_retirement.category_items[index] < memory_after_raw.category_items[index]);
    }

    let fresh_stage = vec![put(b"fresh-stage", b"one")];
    let fresh_final = vec![put(b"fresh-final", b"two")];
    let fresh = storage
        .reserve_stage_work(plan_with_tokens(&config, &fresh_stage, &fresh_final, 5, 6))
        .unwrap();
    drop(fresh);
}

#[test]
fn intervening_raw_write_uses_only_unclaimed_headroom() {
    let directory = TempShard::new();
    let config = config(directory.path());
    let storage = Storage::open(config.clone(), budget(128 * 1024 * 1024)).unwrap();
    let stage = vec![put(b"stage", b"one")];
    let final_batch = vec![put(b"final", b"two")];
    let stage_size = PlannedBatch::exact(&config.format, token(2), &stage)
        .unwrap()
        .footprint();
    let final_size = PlannedBatch::exact(&config.format, token(3), &final_batch)
        .unwrap()
        .footprint();
    let manifest_bytes = 44 + config.max_runs * 48;
    let claim = stage_size.frame_bytes
        + final_size.frame_bytes
        + stage_size.mutable_bytes
        + final_size.mutable_bytes
        + stage_size.directory_bytes
        + final_size.directory_bytes
        + 76 * 3
        + manifest_bytes * 3
        + 32;
    let slack = 300_u64;
    let pad = config.max_disk_bytes - storage.stats().disk_bytes - claim as u64 - slack;
    OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(directory.path().join("quota.pad"))
        .unwrap()
        .set_len(pad)
        .unwrap();
    let mut work = storage
        .reserve_stage_work(plan(&config, &stage, &final_batch))
        .unwrap();
    storage
        .commit_batch(storage.snapshot(), token(4), &[put(b"raw", b"x")])
        .unwrap();
    let after_fit = storage.stats();
    assert!(matches!(
        storage.commit_batch(
            storage.snapshot(),
            token(5),
            &[put(b"large", &vec![1; 1000])]
        ),
        Err(StorageError::Capacity("SSD bytes and stage headroom"))
    ));
    assert_eq!(storage.stats().wal_bytes, after_fit.wal_bytes);
    assert!(matches!(
        storage.checkpoint(),
        Err(StorageError::Capacity("SSD bytes and stage headroom"))
    ));
    assert_eq!(storage.stats().immutable_runs, 0);
    work.commit_stage(storage.snapshot(), token(2), &stage)
        .unwrap();
    let ready = match work
        .prepare_final(storage.snapshot(), token(3), &final_batch)
        .unwrap()
    {
        PreparedBatchOutcome::Ready(ready) => ready,
        PreparedBatchOutcome::AlreadyCommitted(_) => panic!("new final batch"),
    };
    ready.commit().unwrap();
    assert_eq!(storage.stats().visible.get(), 3);
}

#[test]
fn zero_stage_matching_raw_retry_returns_receipt_and_releases_ledger() {
    let directory = TempShard::new();
    let mut config = config(directory.path());
    config.max_live_transactions = 3;
    let storage = Storage::open(config.clone(), budget(128 * 1024 * 1024)).unwrap();
    let final_batch = vec![put(b"final", b"same-bytes")];
    let work = storage
        .reserve_stage_work(StageWorkPlan::new(
            vec![],
            PlannedBatch::exact(&config.format, token(3), &final_batch).unwrap(),
        ))
        .unwrap();
    let original = storage
        .commit_batch(storage.snapshot(), token(3), &final_batch)
        .unwrap();

    match work
        .prepare_final(storage.snapshot(), token(3), &final_batch)
        .unwrap()
    {
        PreparedBatchOutcome::AlreadyCommitted(receipt) => assert_eq!(receipt, original),
        PreparedBatchOutcome::Ready(_) => panic!("matching retry must not acquire a new sequence"),
    }
    assert_eq!(storage.stats().visible, original.commit_seq);
    assert_eq!(storage.stats().live_transaction_ids, 1);

    let fresh_stage = vec![put(b"fresh-stage", b"one")];
    let fresh_final = vec![put(b"fresh-final", b"two")];
    let fresh = storage
        .reserve_stage_work(plan_with_tokens(&config, &fresh_stage, &fresh_final, 5, 6))
        .unwrap();
    drop(fresh);
}

#[test]
fn zero_stage_final_and_orphaned_stage_reopen() {
    let directory = TempShard::new();
    let config = config(directory.path());
    let memory = budget(128 * 1024 * 1024);
    let storage = Storage::open(config.clone(), memory).unwrap();
    let final_batch = vec![put(b"final", b"one")];
    let work = storage
        .reserve_stage_work(StageWorkPlan::new(
            vec![],
            PlannedBatch::exact(&config.format, token(3), &final_batch).unwrap(),
        ))
        .unwrap();
    let ready = match work
        .prepare_final(storage.snapshot(), token(3), &final_batch)
        .unwrap()
    {
        PreparedBatchOutcome::Ready(ready) => ready,
        PreparedBatchOutcome::AlreadyCommitted(_) => panic!("new final batch"),
    };
    drop(ready);
    assert_eq!(storage.stats().visible.get(), 0);
    let stage = vec![put(b"orphan", b"retained")];
    let mut work = storage
        .reserve_stage_work(plan(&config, &stage, &final_batch))
        .unwrap();
    work.commit_stage(storage.snapshot(), token(2), &stage)
        .unwrap();
    drop(work);
    assert_eq!(storage.stats().visible.get(), 1);
    drop(storage);
    let reopened = Storage::open(config, budget(128 * 1024 * 1024)).unwrap();
    assert_eq!(reopened.stats().visible.get(), 1);
    assert!(
        reopened
            .get(reopened.snapshot(), &Key::new(b"orphan".to_vec()).unwrap())
            .unwrap()
            .is_some()
    );
    assert!(
        reopened
            .get(reopened.snapshot(), &Key::new(b"final".to_vec()).unwrap())
            .unwrap()
            .is_none()
    );
}
