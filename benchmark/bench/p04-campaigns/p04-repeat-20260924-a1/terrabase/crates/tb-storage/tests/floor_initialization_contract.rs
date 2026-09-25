use std::fs::{self, File, OpenOptions};
use std::io::Write;
use std::os::unix::fs::symlink;
use std::os::unix::net::UnixListener;
use std::path::{Path, PathBuf};
#[cfg(feature = "test-hooks")]
use std::process::Command;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use tb_core::{BudgetConfig, Key, MemoryBudget, TenantId, TransactionId, WorkerId};
use tb_storage::{Mutation, Storage, StorageConfig, StorageError};

static NEXT_DIRECTORY: AtomicU64 = AtomicU64::new(0);

struct TempShard(PathBuf);

impl TempShard {
    fn new() -> Self {
        let tick = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock")
            .as_nanos();
        let ordinal = NEXT_DIRECTORY.fetch_add(1, Ordering::Relaxed);
        Self(std::env::temp_dir().join(format!(
            "terrabase-floor-init-{}-{tick}-{ordinal}",
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
            fs::remove_dir_all(&self.0).expect("remove temporary shard");
        }
    }
}

fn config(path: &Path, now: &Arc<AtomicU64>) -> StorageConfig {
    let clock = Arc::clone(now);
    StorageConfig::local(path, TenantId::from_bytes([1; 16]), WorkerId(7))
        .with_clock(Arc::new(move || clock.load(Ordering::SeqCst)))
}

fn budget() -> MemoryBudget {
    MemoryBudget::new(BudgetConfig::new(64 * 1024 * 1024, 0).expect("budget"))
}

fn initial_slot() -> Vec<u8> {
    let control = TempShard::new();
    let zero = Arc::new(AtomicU64::new(0));
    let storage = Storage::open(config(control.path(), &zero), budget()).expect("control shard");
    drop(storage);
    let first = fs::read(control.path().join("floor.0")).expect("initial floor.0");
    assert_eq!(
        first,
        fs::read(control.path().join("floor.1")).expect("initial floor.1")
    );
    first
}

fn write_durable_slot(directory: &Path, name: &str, bytes: &[u8]) {
    fs::create_dir_all(directory).expect("create partial shard directory");
    let mut file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(directory.join(name))
        .expect("create floor slot");
    file.write_all(bytes).expect("write floor slot");
    file.sync_all().expect("sync floor slot");
    File::open(directory)
        .expect("open shard directory")
        .sync_all()
        .expect("sync shard directory");
}

fn write_durable_genesis_marker(directory: &Path) {
    fs::create_dir_all(directory).expect("create partial shard directory");
    let marker = directory.join("genesis.pending");
    fs::create_dir(&marker).expect("create genesis marker");
    File::open(&marker)
        .expect("open genesis marker")
        .sync_all()
        .expect("sync genesis marker");
    File::open(directory)
        .expect("open shard directory")
        .sync_all()
        .expect("sync marker parent");
}

#[cfg(feature = "test-hooks")]
const GENESIS_CHILD_ENV: &str = "TB_STORAGE_GENESIS_CHILD";
#[cfg(feature = "test-hooks")]
const GENESIS_DIRECTORY_ENV: &str = "TB_STORAGE_GENESIS_DIRECTORY";

#[cfg(feature = "test-hooks")]
#[test]
fn genesis_crash_child_process() {
    if std::env::var_os(GENESIS_CHILD_ENV).is_none() {
        return;
    }
    let directory = std::env::var(GENESIS_DIRECTORY_ENV).expect("genesis directory");
    let zero = Arc::new(AtomicU64::new(0));
    Storage::open(config(Path::new(&directory), &zero), budget())
        .expect("crash hook should terminate during open");
    panic!("configured genesis crash hook did not terminate the child");
}

#[cfg(feature = "test-hooks")]
fn crash_open_at(directory: &TempShard, point: &str) {
    let status = Command::new(std::env::current_exe().expect("locate floor test binary"))
        .arg("--exact")
        .arg("genesis_crash_child_process")
        .env(GENESIS_CHILD_ENV, "1")
        .env(GENESIS_DIRECTORY_ENV, directory.path())
        .env("TB_STORAGE_CRASH_POINT", point)
        .status()
        .expect("run genesis crash child");
    assert_eq!(status.code(), Some(86), "crash point {point}");
}

#[cfg(feature = "test-hooks")]
fn assert_recovered_floor(directory: &TempShard) {
    let now = Arc::new(AtomicU64::new(300));
    let config = config(directory.path(), &now);
    let storage = Storage::open(config.clone(), budget()).expect("recover interrupted genesis");
    assert_eq!(storage.stats().expired_floor_ms, 300);
    assert!(!directory.path().join("genesis.pending").exists());
    assert!(directory.path().join("wal.log").exists());
    drop(storage);

    now.store(150, Ordering::SeqCst);
    let storage = Storage::open(config, budget()).expect("reopen after clock rollback");
    assert_eq!(storage.stats().expired_floor_ms, 300);
    let expired = TransactionId::new(200, [3; 16]);
    assert!(matches!(
        storage.commit_batch(
            storage.snapshot(),
            expired,
            &[Mutation::Put {
                key: Key::new(b"expired".to_vec()).expect("key"),
                value: b"must not commit".to_vec(),
            }]
        ),
        Err(StorageError::IdempotencyExpired)
    ));
}

#[cfg(feature = "test-hooks")]
#[test]
fn every_genesis_publication_cut_recovers_before_wal() {
    const CUTS: [&str; 17] = [
        "after_genesis_marker_create",
        "after_genesis_marker_contents_sync",
        "after_genesis_marker_sync",
        "after_genesis_slot0_temp_create",
        "after_genesis_slot0_temp_prefix",
        "after_genesis_slot0_temp_sync",
        "after_genesis_slot0_rename",
        "after_genesis_slot0_parent_sync",
        "after_genesis_slot1_temp_create",
        "after_genesis_slot1_temp_prefix",
        "after_genesis_slot1_temp_sync",
        "after_genesis_slot1_rename",
        "after_genesis_slot1_parent_sync",
        "after_genesis_slots_sync",
        "after_genesis_marker_remove",
        "after_genesis_marker_remove_sync",
        "after_genesis_wal_sync",
    ];
    for cut in CUTS {
        let directory = TempShard::new();
        crash_open_at(&directory, cut);
        assert_recovered_floor(&directory);
    }
}

#[cfg(feature = "test-hooks")]
#[test]
fn legacy_first_slot_transition_recovers_at_each_cut() {
    const CUTS: [&str; 11] = [
        "after_genesis_marker_create",
        "after_genesis_marker_contents_sync",
        "after_genesis_marker_sync",
        "after_genesis_slot1_temp_create",
        "after_genesis_slot1_temp_prefix",
        "after_genesis_slot1_temp_sync",
        "after_genesis_slot1_rename",
        "after_genesis_slot1_parent_sync",
        "after_genesis_slots_sync",
        "after_genesis_marker_remove",
        "after_genesis_marker_remove_sync",
    ];
    let first = initial_slot();
    for cut in CUTS {
        let directory = TempShard::new();
        write_durable_slot(directory.path(), "floor.0", &first);
        crash_open_at(&directory, cut);
        assert_recovered_floor(&directory);
    }
}

#[test]
fn valid_first_genesis_slot_is_completed_before_wal_and_floor_survives_clock_rollback() {
    let directory = TempShard::new();
    write_durable_slot(directory.path(), "floor.0", &initial_slot());
    assert!(!directory.path().join("floor.1").exists());
    assert!(!directory.path().join("wal.log").exists());

    let now = Arc::new(AtomicU64::new(300));
    let config = config(directory.path(), &now);
    let storage = Storage::open(config.clone(), budget()).expect("complete interrupted genesis");
    assert!(directory.path().join("floor.1").exists());
    assert_eq!(storage.stats().expired_floor_ms, 300);
    drop(storage);

    now.store(150, Ordering::SeqCst);
    let storage = Storage::open(config, budget()).expect("reopen after clock rollback");
    assert_eq!(storage.stats().expired_floor_ms, 300);
    let expired = TransactionId::new(200, [1; 16]);
    let mutations = [Mutation::Put {
        key: Key::new(b"expired".to_vec()).expect("key"),
        value: b"must not commit".to_vec(),
    }];
    assert!(matches!(
        storage.commit_batch(storage.snapshot(), expired, &mutations),
        Err(StorageError::IdempotencyExpired)
    ));
}

#[test]
fn marked_genesis_repairs_only_missing_or_torn_owned_slots() {
    let initial = initial_slot();
    let half = &initial[..initial.len() / 2];
    for case in 0..5 {
        let directory = TempShard::new();
        write_durable_genesis_marker(directory.path());
        match case {
            0 => {}
            1 => write_durable_slot(directory.path(), "floor.0", half),
            2 => {
                write_durable_slot(directory.path(), "floor.0", &initial);
                write_durable_slot(directory.path(), "floor.1", half);
            }
            3 => {
                write_durable_slot(directory.path(), "floor.0.genesis.tmp", half);
                write_durable_slot(directory.path(), "floor.1.genesis.tmp", &initial);
            }
            4 => {
                write_durable_slot(directory.path(), "floor.0", &initial);
                write_durable_slot(directory.path(), "floor.1", &initial);
            }
            _ => unreachable!(),
        }
        let now = Arc::new(AtomicU64::new(300));
        let storage = Storage::open(config(directory.path(), &now), budget())
            .expect("recover marked pre-WAL genesis");
        assert_eq!(storage.stats().expired_floor_ms, 300);
        assert!(!directory.path().join("genesis.pending").exists());
        for slot in 0..2 {
            assert!(directory.path().join(format!("floor.{slot}")).exists());
            assert!(
                !directory
                    .path()
                    .join(format!("floor.{slot}.genesis.tmp"))
                    .exists()
            );
        }
    }
}

#[test]
fn marker_never_authorizes_repair_beside_other_shard_state() {
    let initial = initial_slot();
    for name in [
        "wal.log",
        "manifest.0",
        "manifest.pending",
        "run-00000000000000000001.dat",
        "run-00000000000000000001.pending",
        "floor.pending",
        "unexpected",
    ] {
        let directory = TempShard::new();
        write_durable_genesis_marker(directory.path());
        write_durable_slot(directory.path(), "floor.0", &initial[..8]);
        fs::write(directory.path().join(name), b"").expect("create disqualifying state");
        let zero = Arc::new(AtomicU64::new(0));
        assert!(
            matches!(
                Storage::open(config(directory.path(), &zero), budget()),
                Err(StorageError::Corrupt(_))
            ),
            "unexpected acceptance with {name}"
        );
        assert_eq!(
            fs::read(directory.path().join("floor.0")).unwrap(),
            &initial[..8]
        );
        assert!(directory.path().join("genesis.pending").exists());
        assert!(!directory.path().join("floor.1").exists());
    }
}

#[test]
fn marker_rejects_nonzero_authority_and_unsafe_file_types_without_mutation() {
    let initial = initial_slot();
    let now = Arc::new(AtomicU64::new(300));
    let established = TempShard::new();
    drop(Storage::open(config(established.path(), &now), budget()).expect("advanced floor"));
    let advanced = fs::read(established.path().join("floor.0")).expect("advanced slot");
    assert_ne!(advanced, initial);

    let nonzero = TempShard::new();
    write_durable_genesis_marker(nonzero.path());
    write_durable_slot(nonzero.path(), "floor.0", &advanced);
    assert!(matches!(
        Storage::open(config(nonzero.path(), &now), budget()),
        Err(StorageError::Corrupt(_))
    ));
    assert_eq!(fs::read(nonzero.path().join("floor.0")).unwrap(), advanced);
    assert!(!nonzero.path().join("floor.1").exists());

    let sentinel = TempShard::new();
    fs::create_dir_all(sentinel.path()).expect("create sentinel directory");
    let sentinel_file = sentinel.path().join("sentinel");
    fs::write(&sentinel_file, b"unchanged").expect("write sentinel");
    for name in [
        "floor.0",
        "floor.1",
        "floor.0.genesis.tmp",
        "floor.1.genesis.tmp",
    ] {
        for linked in [false, true] {
            let directory = TempShard::new();
            write_durable_genesis_marker(directory.path());
            if linked {
                fs::hard_link(&sentinel_file, directory.path().join(name)).expect("hardlink");
            } else {
                symlink(&sentinel_file, directory.path().join(name)).expect("symlink");
            }
            assert!(Storage::open(config(directory.path(), &now), budget()).is_err());
            assert!(directory.path().join("genesis.pending").exists());
            assert!(!directory.path().join("wal.log").exists());
        }
        let directory = TempShard::new();
        write_durable_genesis_marker(directory.path());
        let _socket = UnixListener::bind(directory.path().join(name)).expect("special file");
        assert!(Storage::open(config(directory.path(), &now), budget()).is_err());
        assert!(!directory.path().join("wal.log").exists());
    }
    assert_eq!(fs::read(&sentinel_file).unwrap(), b"unchanged");
}

#[test]
fn invalid_marker_and_unmarked_torn_slots_fail_closed() {
    let zero = Arc::new(AtomicU64::new(0));
    let initial = initial_slot();
    for case in 0..3 {
        let directory = TempShard::new();
        fs::create_dir_all(directory.path()).expect("create marker test directory");
        match case {
            0 => {
                fs::write(directory.path().join("genesis.pending"), b"bad")
                    .expect("regular marker");
            }
            1 => {
                let target = TempShard::new();
                fs::create_dir_all(target.path()).expect("create marker target");
                symlink(target.path(), directory.path().join("genesis.pending"))
                    .expect("linked marker");
                assert!(Storage::open(config(directory.path(), &zero), budget()).is_err());
                assert!(!directory.path().join("floor.0").exists());
                continue;
            }
            2 => {
                fs::create_dir(directory.path().join("genesis.pending")).expect("marker");
                fs::write(directory.path().join("genesis.pending/foreign"), b"bad")
                    .expect("nonempty marker");
            }
            _ => unreachable!(),
        }
        assert!(Storage::open(config(directory.path(), &zero), budget()).is_err());
        assert!(!directory.path().join("floor.0").exists());
    }
    for second_slot in [false, true] {
        let directory = TempShard::new();
        if second_slot {
            write_durable_slot(directory.path(), "floor.0", &initial);
            write_durable_slot(directory.path(), "floor.1", &initial[..8]);
        } else {
            write_durable_slot(directory.path(), "floor.0", &initial[..8]);
        }
        assert!(matches!(
            Storage::open(config(directory.path(), &zero), budget()),
            Err(StorageError::Corrupt(_))
        ));
        assert!(!directory.path().join("genesis.pending").exists());
        assert!(!directory.path().join("wal.log").exists());
    }
}

#[test]
fn missing_or_invalid_initial_authority_never_repairs_a_non_genesis_shard() {
    let initial = initial_slot();
    let now = Arc::new(AtomicU64::new(100));

    let established = TempShard::new();
    let storage = Storage::open(config(established.path(), &now), budget()).expect("open shard");
    storage
        .commit_batch(
            storage.snapshot(),
            TransactionId::new(1_000, [2; 16]),
            &[Mutation::Put {
                key: Key::new(b"durable".to_vec()).expect("key"),
                value: b"value".to_vec(),
            }],
        )
        .expect("commit WAL-bearing shard");
    drop(storage);
    let advanced = fs::read(established.path().join("floor.0")).expect("advanced floor.0");
    fs::remove_file(established.path().join("floor.1")).expect("remove older slot");
    assert!(matches!(
        Storage::open(config(established.path(), &now), budget()),
        Err(StorageError::Corrupt(_))
    ));
    assert!(!established.path().join("floor.1").exists());

    let advanced_only = TempShard::new();
    write_durable_slot(advanced_only.path(), "floor.0", &advanced);
    assert!(matches!(
        Storage::open(config(advanced_only.path(), &now), budget()),
        Err(StorageError::Corrupt(_))
    ));
    assert!(!advanced_only.path().join("floor.1").exists());

    let corrupt = TempShard::new();
    let mut damaged = initial.clone();
    damaged[20] ^= 1;
    write_durable_slot(corrupt.path(), "floor.0", &damaged);
    assert!(matches!(
        Storage::open(config(corrupt.path(), &now), budget()),
        Err(StorageError::Corrupt(_))
    ));
    assert!(!corrupt.path().join("floor.1").exists());

    let wrong_slot = TempShard::new();
    write_durable_slot(wrong_slot.path(), "floor.1", &initial);
    assert!(matches!(
        Storage::open(config(wrong_slot.path(), &now), budget()),
        Err(StorageError::Corrupt(_))
    ));
    assert!(!wrong_slot.path().join("floor.0").exists());

    let unexpected_file = TempShard::new();
    write_durable_slot(unexpected_file.path(), "floor.0", &initial);
    File::create(unexpected_file.path().join("manifest.pending"))
        .expect("create disqualifying shard file");
    assert!(matches!(
        Storage::open(config(unexpected_file.path(), &now), budget()),
        Err(StorageError::Corrupt(_))
    ));
    assert!(!unexpected_file.path().join("floor.1").exists());
}

#[test]
fn authority_slots_must_be_independent_regular_files_and_lock_must_not_follow_symlink() {
    let zero = Arc::new(AtomicU64::new(0));

    let linked = TempShard::new();
    let storage = Storage::open(config(linked.path(), &zero), budget()).expect("initial slots");
    drop(storage);
    fs::remove_file(linked.path().join("floor.1")).expect("remove second slot");
    fs::hard_link(linked.path().join("floor.0"), linked.path().join("floor.1"))
        .expect("alias floor slots");
    assert!(matches!(
        Storage::open(config(linked.path(), &zero), budget()),
        Err(StorageError::Corrupt(_))
    ));

    let symlinked = TempShard::new();
    let storage = Storage::open(config(symlinked.path(), &zero), budget()).expect("initial slots");
    drop(storage);
    fs::remove_file(symlinked.path().join("floor.1")).expect("remove second slot");
    symlink("floor.0", symlinked.path().join("floor.1")).expect("link second slot");
    assert!(Storage::open(config(symlinked.path(), &zero), budget()).is_err());
    let slot_link = fs::read_link(symlinked.path().join("floor.1")).expect("unchanged slot link");
    assert_eq!(slot_link.as_path(), Path::new("floor.0"));

    let sentinel_dir = TempShard::new();
    fs::create_dir_all(sentinel_dir.path()).expect("create sentinel directory");
    let sentinel = sentinel_dir.path().join("sentinel");
    fs::write(&sentinel, b"unchanged").expect("write sentinel");
    let linked_lock = TempShard::new();
    fs::create_dir_all(linked_lock.path()).expect("create lock-symlink shard");
    symlink(&sentinel, linked_lock.path().join("shard.lock")).expect("link shard lock");
    assert!(Storage::open(config(linked_lock.path(), &zero), budget()).is_err());
    assert_eq!(
        fs::read(&sentinel).expect("read sentinel").as_slice(),
        b"unchanged"
    );
    assert!(!linked_lock.path().join("floor.0").exists());
    assert!(!linked_lock.path().join("wal.log").exists());

    let hardlinked_lock = TempShard::new();
    fs::create_dir_all(hardlinked_lock.path()).expect("create hardlinked-lock shard");
    fs::hard_link(&sentinel, hardlinked_lock.path().join("shard.lock"))
        .expect("hardlink shard lock");
    assert!(matches!(
        Storage::open(config(hardlinked_lock.path(), &zero), budget()),
        Err(StorageError::Corrupt(_))
    ));
    assert!(!hardlinked_lock.path().join("floor.0").exists());
}
