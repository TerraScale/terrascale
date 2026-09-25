use std::collections::BTreeMap;
use std::fs::{self, File, OpenOptions};
use std::io::Write;
use std::os::unix::fs::{MetadataExt, symlink};
use std::path::{Path, PathBuf};
use std::process::Command;
#[cfg(feature = "slow-tests")]
use std::process::ExitStatus;
use std::sync::atomic::{AtomicU64, Ordering};
use std::thread;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use fs2::FileExt;
use tb_core::{
    BudgetConfig, DatabaseId, Key, MembershipEpoch, MemoryBudget, OriginId, ReplicaId, ShardId,
    TenantId, TransactionId, WorkerId,
};
use tb_storage::{DistributedDocumentMode, Mutation, Storage, StorageConfig, StorageError};

static NEXT_ROOT: AtomicU64 = AtomicU64::new(0);

const MODE_MARKER_NAME: &str = "documents.mode";
const MODE_MARKER_LEN: usize = 148;
#[cfg(feature = "slow-tests")]
const MODE_PENDING_PREFIX: &str = "documents.mode.pending.";
const HEX: &[u8; 16] = b"0123456789abcdef";
const MODE_CLUSTER: [u8; 16] = [2; 16];
const MODE_TENANT: [u8; 16] = [1; 16];
const MODE_DATABASE: [u8; 16] = [3; 16];
const MODE_SHARD: u64 = 4;
const MODE_EPOCH: u64 = 5;
const MODE_CONFIGURATION: [u8; 32] = [6; 32];
const MODE_REPLICA: [u8; 16] = [7; 16];
const MODE_INCARNATION: [u8; 16] = [8; 16];

#[cfg(feature = "slow-tests")]
const MODE_CRASH_CHILD_ENV: &str = "TB_STORAGE_MODE_CRASH_CHILD";
#[cfg(feature = "slow-tests")]
const MODE_DIRECTORY_ENV: &str = "TB_STORAGE_MODE_DIRECTORY";
#[cfg(feature = "slow-tests")]
const MODE_CRASH_POINT_ENV: &str = "TB_STORAGE_CRASH_POINT";
const MODE_LOCK_CHILD_ENV: &str = "TB_STORAGE_MODE_LOCK_CHILD";
const MODE_LOCK_DIRECTORY_ENV: &str = "TB_STORAGE_MODE_LOCK_DIRECTORY";
const MODE_LOCK_SIGNAL_ENV: &str = "TB_STORAGE_MODE_LOCK_SIGNAL";

#[cfg(feature = "slow-tests")]
const MODE_CRASH_CUTS: [&str; 6] = [
    "after_document_mode_temp_sync",
    "after_document_mode_publish_before_parent_sync",
    "after_document_mode_parent_sync",
    "after_genesis_marker_sync",
    "after_genesis_slots_sync",
    "after_genesis_wal_sync",
];

struct TempRoot(PathBuf);

impl TempRoot {
    fn new() -> Self {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock is after the Unix epoch")
            .as_nanos();
        let ordinal = NEXT_ROOT.fetch_add(1, Ordering::Relaxed);
        let path = std::env::temp_dir().join(format!(
            "terrabase-document-mode-{}-{nanos}-{ordinal}",
            std::process::id()
        ));
        fs::create_dir_all(&path).expect("create temporary data directory");
        Self(path)
    }

    fn path(&self) -> &Path {
        &self.0
    }

    fn documents(&self) -> PathBuf {
        self.0.join("documents")
    }

    fn marker(&self) -> PathBuf {
        self.0.join(MODE_MARKER_NAME)
    }
}

impl Drop for TempRoot {
    fn drop(&mut self) {
        if self.0.exists() {
            fs::remove_dir_all(&self.0).expect("remove temporary data directory");
        }
    }
}

#[derive(Debug, Eq, PartialEq)]
enum TreeEntry {
    Directory,
    File(Vec<u8>),
    Symlink(PathBuf),
    Other,
}

fn visit_tree(root: &Path, directory: &Path, entries: &mut BTreeMap<PathBuf, TreeEntry>) {
    let mut children: Vec<_> = fs::read_dir(directory)
        .expect("read tree directory")
        .map(|entry| entry.expect("read tree entry"))
        .collect();
    children.sort_by_key(std::fs::DirEntry::file_name);
    for child in children {
        let path = child.path();
        let relative = path
            .strip_prefix(root)
            .expect("tree entry is below root")
            .to_path_buf();
        let file_type = child.file_type().expect("read tree entry type");
        if file_type.is_dir() {
            entries.insert(relative, TreeEntry::Directory);
            visit_tree(root, &path, entries);
        } else if file_type.is_symlink() {
            entries.insert(
                relative,
                TreeEntry::Symlink(fs::read_link(path).expect("read tree symlink")),
            );
        } else if file_type.is_file() {
            entries.insert(
                relative,
                TreeEntry::File(fs::read(path).expect("read tree file")),
            );
        } else {
            entries.insert(relative, TreeEntry::Other);
        }
    }
}

fn tree_snapshot(root: &Path) -> BTreeMap<PathBuf, TreeEntry> {
    let mut entries = BTreeMap::new();
    if root.is_dir() {
        visit_tree(root, root, &mut entries);
    }
    entries
}

fn tenant() -> TenantId {
    TenantId::from_bytes(MODE_TENANT)
}

fn mode_with_cluster(cluster: [u8; 16]) -> DistributedDocumentMode {
    DistributedDocumentMode::new(
        cluster,
        tenant(),
        DatabaseId::from_bytes(MODE_DATABASE),
        ShardId::new(MODE_SHARD),
        MembershipEpoch::new(MODE_EPOCH),
        MODE_CONFIGURATION,
        OriginId {
            replica_id: ReplicaId::from_bytes(MODE_REPLICA),
            incarnation: MODE_INCARNATION,
        },
    )
}

fn mode() -> DistributedDocumentMode {
    mode_with_cluster(MODE_CLUSTER)
}

fn storage_config(path: &Path) -> StorageConfig {
    StorageConfig::local(path.to_path_buf(), tenant(), WorkerId(1))
}

fn budget() -> MemoryBudget {
    MemoryBudget::new(BudgetConfig::new(128 * 1024 * 1024, 0).expect("valid test budget"))
}

fn open_distributed(path: &Path, mode: DistributedDocumentMode) -> Result<Storage, StorageError> {
    Storage::open_distributed_document(storage_config(path), budget(), mode)
}

fn durable_file(path: &Path, bytes: &[u8]) {
    let mut file = OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(path)
        .expect("create fixture file");
    file.write_all(bytes).expect("write fixture file");
    file.sync_all().expect("sync fixture file");
    File::open(path.parent().expect("fixture parent"))
        .expect("open fixture parent")
        .sync_all()
        .expect("sync fixture parent");
}

fn valid_marker_fixture() -> (TempRoot, Vec<u8>) {
    let root = TempRoot::new();
    let storage = open_distributed(&root.documents(), mode()).expect("fresh distributed open");
    drop(storage);
    let bytes = fs::read(root.marker()).expect("read published mode marker");
    (root, bytes)
}

fn assert_marker_layout(bytes: &[u8]) {
    assert_eq!(bytes.len(), MODE_MARKER_LEN);
    assert_eq!(&bytes[..8], b"TBDMOD01");
    assert_eq!(u16::from_le_bytes(bytes[8..10].try_into().unwrap()), 1);
    assert_eq!(&bytes[10..12], &[0; 2]);
    assert_eq!(
        u32::from_le_bytes(bytes[12..16].try_into().unwrap()),
        u32::try_from(MODE_MARKER_LEN).expect("fixed marker length")
    );
    assert_eq!(&bytes[16..32], &MODE_CLUSTER);
    assert_eq!(&bytes[32..48], &MODE_TENANT);
    assert_eq!(&bytes[48..64], &MODE_DATABASE);
    assert_eq!(&bytes[64..72], &MODE_SHARD.to_le_bytes());
    assert_eq!(&bytes[72..80], &MODE_EPOCH.to_le_bytes());
    assert_eq!(&bytes[80..112], &MODE_CONFIGURATION);
    assert_eq!(&bytes[112..128], &MODE_REPLICA);
    assert_eq!(&bytes[128..144], &MODE_INCARNATION);
    assert_eq!(
        u32::from_le_bytes(bytes[144..148].try_into().unwrap()),
        crc32c::crc32c(&bytes[..144])
    );
}

fn repair_marker_checksum(bytes: &mut [u8]) {
    let checksum = crc32c::crc32c(&bytes[..144]);
    bytes[144..148].copy_from_slice(&checksum.to_le_bytes());
}

fn origin_component() -> String {
    let mut component = String::with_capacity(64);
    for byte in MODE_REPLICA.into_iter().chain(MODE_INCARNATION) {
        component.push(char::from(HEX[usize::from(byte >> 4)]));
        component.push(char::from(HEX[usize::from(byte & 0x0f)]));
    }
    component
}

#[test]
fn unmarked_existing_local_documents_are_rejected_without_shard_mutation() {
    let root = TempRoot::new();
    let local = Storage::open(storage_config(&root.documents()), budget())
        .expect("create ordinary local document shard");
    let transaction = TransactionId::new(local.now_unix_ms().saturating_add(30_000), [9; 16]);
    local
        .commit_batch(
            local.snapshot(),
            transaction,
            &[Mutation::Put {
                key: Key::new(b"local-state".to_vec()).expect("key"),
                value: b"must remain local".to_vec(),
            }],
        )
        .expect("commit local data");
    drop(local);
    let before = tree_snapshot(&root.documents());
    assert!(!root.marker().exists());

    assert!(matches!(
        open_distributed(&root.documents(), mode()),
        Err(StorageError::InvalidDirectory { .. })
    ));
    assert_eq!(tree_snapshot(&root.documents()), before);
    assert!(!root.marker().exists());
}

#[test]
fn empty_unmarked_documents_directory_is_rejected_without_mutation() {
    let root = TempRoot::new();
    fs::create_dir(root.documents()).expect("create empty unmarked documents directory");
    let before = tree_snapshot(&root.documents());
    assert!(before.is_empty());
    assert!(
        fs::read_dir(root.documents())
            .expect("read empty documents directory")
            .next()
            .is_none()
    );

    assert!(matches!(
        open_distributed(&root.documents(), mode()),
        Err(StorageError::InvalidDirectory { .. })
    ));
    assert!(root.documents().is_dir());
    assert_eq!(tree_snapshot(&root.documents()), before);
    assert!(
        fs::read_dir(root.documents())
            .expect("read unchanged documents directory")
            .next()
            .is_none()
    );
    assert!(!root.marker().exists());
}

#[test]
fn matching_mode_marker_allows_fresh_open_and_reopen() {
    let root = TempRoot::new();
    let first = open_distributed(&root.documents(), mode()).expect("fresh marked open");
    assert_eq!(first.stats().visible.get(), 0);
    assert!(root.documents().join("wal.log").is_file());
    drop(first);

    let marker = fs::read(root.marker()).expect("read mode marker");
    assert_marker_layout(&marker);
    let reopened = open_distributed(&root.documents(), mode()).expect("matching marker reopen");
    assert_eq!(reopened.stats().visible.get(), 0);
    assert_eq!(
        fs::read(root.marker()).expect("read reopened marker"),
        marker
    );
}

#[test]
fn wrong_trailing_linked_and_malformed_markers_are_rejected_unchanged() {
    let (golden_root, valid) = valid_marker_fixture();
    assert_marker_layout(&valid);

    for case in [
        "wrong-scope",
        "wrong-version",
        "bad-checksum",
        "trailing-byte",
        "truncated",
        "symlink",
        "hard-link",
        "directory",
    ] {
        let root = TempRoot::new();
        let marker = root.marker();
        let requested_mode = if case == "wrong-scope" {
            mode_with_cluster([10; 16])
        } else {
            mode()
        };
        match case {
            "wrong-scope" => durable_file(&marker, &valid),
            "wrong-version" => {
                let mut bytes = valid.clone();
                bytes[8..10].copy_from_slice(&2_u16.to_le_bytes());
                repair_marker_checksum(&mut bytes);
                durable_file(&marker, &bytes);
            }
            "bad-checksum" => {
                let mut bytes = valid.clone();
                bytes[80] ^= 1;
                durable_file(&marker, &bytes);
            }
            "trailing-byte" => {
                let mut bytes = valid.clone();
                bytes.push(0);
                durable_file(&marker, &bytes);
            }
            "truncated" => durable_file(&marker, &valid[..MODE_MARKER_LEN - 1]),
            "symlink" => symlink(golden_root.marker(), &marker).expect("create marker symlink"),
            "hard-link" => {
                fs::hard_link(golden_root.marker(), &marker).expect("create marker hard link");
                assert_eq!(fs::symlink_metadata(&marker).unwrap().nlink(), 2);
            }
            "directory" => fs::create_dir(&marker).expect("create directory marker"),
            _ => unreachable!("known marker case"),
        }
        let before = tree_snapshot(root.path());

        assert!(
            open_distributed(&root.documents(), requested_mode).is_err(),
            "marker case {case} must fail closed"
        );
        assert!(!root.documents().exists(), "case {case} created the shard");
        assert_eq!(
            tree_snapshot(root.path()),
            before,
            "case {case} changed evidence"
        );
    }
}

#[test]
fn raw_open_rejects_marked_documents_and_alias_but_opens_sibling_and_origin_inbox() {
    let root = TempRoot::new();
    let marked = open_distributed(&root.documents(), mode()).expect("create marked documents");
    drop(marked);
    let marker_before = fs::read(root.marker()).expect("read marker");
    let documents_before = tree_snapshot(&root.documents());

    assert!(matches!(
        Storage::open(storage_config(&root.documents()), budget()),
        Err(StorageError::InvalidDirectory { .. })
    ));
    let alias = root.path().join("other-name");
    symlink(root.documents(), &alias).expect("create documents alias");
    assert!(matches!(
        Storage::open(storage_config(&alias), budget()),
        Err(StorageError::InvalidDirectory { .. })
    ));
    assert_eq!(tree_snapshot(&root.documents()), documents_before);
    assert_eq!(
        fs::read(root.marker()).expect("marker remains"),
        marker_before
    );

    let sibling = root.path().join("other");
    let ordinary = Storage::open(storage_config(&sibling), budget())
        .expect("ordinary real sibling ignores documents marker");
    drop(ordinary);
    let inbox = root.path().join("inbox").join(origin_component());
    let journal = Storage::open(storage_config(&inbox), budget())
        .expect("ordinary origin inbox ignores documents marker");
    drop(journal);
    assert!(sibling.join("wal.log").is_file());
    assert!(inbox.join("wal.log").is_file());
}

#[test]
fn raw_open_rejects_documents_symlink_to_unmarked_directory_without_mutation() {
    let root = TempRoot::new();
    let marked = open_distributed(&root.documents(), mode()).expect("create marked documents");
    drop(marked);
    let marker_before = fs::read(root.marker()).expect("read mode marker");

    let unmarked_target = root.path().join("unmarked-target");
    let target_storage = Storage::open(storage_config(&unmarked_target), budget())
        .expect("create unmarked target shard");
    let transaction = TransactionId::new(
        target_storage.now_unix_ms().saturating_add(30_000),
        [11; 16],
    );
    target_storage
        .commit_batch(
            target_storage.snapshot(),
            transaction,
            &[Mutation::Put {
                key: Key::new(b"target-state".to_vec()).expect("key"),
                value: b"must remain unchanged".to_vec(),
            }],
        )
        .expect("commit target data");
    drop(target_storage);
    let target_before = tree_snapshot(&unmarked_target);

    fs::remove_dir_all(root.documents()).expect("remove marked documents shard");
    symlink(&unmarked_target, root.documents()).expect("replace documents with final symlink");
    let requested_link = fs::read_link(root.documents()).expect("read requested documents link");

    assert!(matches!(
        Storage::open(storage_config(&root.documents()), budget()),
        Err(StorageError::InvalidDirectory { .. })
    ));
    assert!(
        fs::symlink_metadata(root.documents())
            .expect("documents entry remains")
            .file_type()
            .is_symlink()
    );
    assert_eq!(
        fs::read_link(root.documents()).expect("documents link remains"),
        requested_link
    );
    assert_eq!(tree_snapshot(&unmarked_target), target_before);
    assert_eq!(
        fs::read(root.marker()).expect("mode marker remains"),
        marker_before
    );
}

#[test]
fn distributed_open_rejects_relative_dotdot_and_symlink_paths() {
    let relative = PathBuf::from("documents");
    assert!(open_distributed(&relative, mode()).is_err());

    let root = TempRoot::new();
    fs::create_dir(root.path().join("component")).expect("create path component");
    let dotdot = root.path().join("component").join("..").join("documents");
    assert!(open_distributed(&dotdot, mode()).is_err());
    assert!(!root.marker().exists());
    assert!(!root.documents().exists());

    let parent_alias = root.path().join("parent-alias");
    symlink(root.path(), &parent_alias).expect("create parent alias");
    let alias_path = parent_alias.join("documents");
    assert!(open_distributed(&alias_path, mode()).is_err());
    assert!(!root.marker().exists());
    assert!(!root.documents().exists());

    let outside = root.path().join("outside");
    fs::create_dir(&outside).expect("create symlink target directory");
    symlink(&outside, root.documents()).expect("create final-component symlink");
    let outside_before = tree_snapshot(&outside);
    assert!(open_distributed(&root.documents(), mode()).is_err());
    assert_eq!(tree_snapshot(&outside), outside_before);
    assert!(!root.marker().exists());
}

#[test]
fn document_mode_open_child_process() {
    if std::env::var_os(MODE_LOCK_CHILD_ENV).is_none() {
        return;
    }
    let directory = PathBuf::from(
        std::env::var_os(MODE_LOCK_DIRECTORY_ENV).expect("parent sets lock directory"),
    );
    let signal = PathBuf::from(
        std::env::var_os(MODE_LOCK_SIGNAL_ENV).expect("parent sets child signal path"),
    );
    durable_file(&signal, b"started");
    let storage = open_distributed(&directory.join("documents"), mode())
        .expect("open after parent gate becomes available");
    assert_eq!(storage.stats().visible.get(), 0);
}

#[test]
fn cooperating_document_opens_wait_for_the_parent_directory_gate() {
    let root = TempRoot::new();
    let signal = root.path().join("open-started");
    let parent = File::open(root.path()).expect("open parent directory");
    parent.lock_exclusive().expect("hold parent gate");
    let mut child = Command::new(std::env::current_exe().expect("locate test executable"))
        .arg("--exact")
        .arg("storage_slow::document_mode_contract::document_mode_open_child_process")
        .env(MODE_LOCK_CHILD_ENV, "1")
        .env(MODE_LOCK_DIRECTORY_ENV, root.path())
        .env(MODE_LOCK_SIGNAL_ENV, &signal)
        .spawn()
        .expect("start cooperating open child");

    let started_at = Instant::now();
    while !signal.exists() && started_at.elapsed() < Duration::from_secs(5) {
        thread::sleep(Duration::from_millis(10));
    }
    assert!(
        signal.exists(),
        "child did not reach the parent-gate attempt"
    );
    thread::sleep(Duration::from_millis(100));
    let child_status_while_locked = child.try_wait().expect("poll open child");
    let documents_exist_while_locked = root.documents().exists();
    let marker_exists_while_locked = root.marker().exists();
    FileExt::unlock(&parent).expect("release parent gate");
    let child_status = child.wait().expect("wait for open child");

    assert!(child_status_while_locked.is_none());
    assert!(!documents_exist_while_locked);
    assert!(!marker_exists_while_locked);
    assert!(child_status.success());
    assert!(root.documents().is_dir());
    assert!(root.marker().is_file());
}

#[cfg(feature = "slow-tests")]
#[test]
fn document_mode_crash_child_process() {
    if std::env::var_os(MODE_CRASH_CHILD_ENV).is_none() {
        return;
    }
    let directory =
        PathBuf::from(std::env::var_os(MODE_DIRECTORY_ENV).expect("parent sets crash directory"));
    let _point = std::env::var(MODE_CRASH_POINT_ENV).expect("parent sets crash point");
    let _storage = open_distributed(&directory.join("documents"), mode())
        .expect("configured crash hook should run while opening");
    panic!("configured document mode crash hook did not terminate the child");
}

#[cfg(feature = "slow-tests")]
fn run_mode_crash_child(root: &TempRoot, point: &str) -> ExitStatus {
    Command::new(std::env::current_exe().expect("locate test executable"))
        .arg("--exact")
        .arg("storage_slow::document_mode_contract::document_mode_crash_child_process")
        .env(MODE_CRASH_CHILD_ENV, "1")
        .env(MODE_DIRECTORY_ENV, root.path())
        .env(MODE_CRASH_POINT_ENV, point)
        .status()
        .expect("run document mode crash child")
}

#[cfg(feature = "slow-tests")]
#[test]
fn marker_publication_and_storage_genesis_crash_cuts_resume_safely() {
    for point in MODE_CRASH_CUTS {
        let root = TempRoot::new();
        assert_eq!(
            run_mode_crash_child(&root, point).code(),
            Some(86),
            "cut {point}"
        );

        let marker_exists = root.marker().exists();
        let documents_exist = root.documents().exists();
        if point == "after_document_mode_temp_sync" {
            assert!(!marker_exists);
            assert!(!documents_exist);
            assert!(
                fs::read_dir(root.path())
                    .unwrap()
                    .filter_map(Result::ok)
                    .any(|entry| entry
                        .file_name()
                        .to_string_lossy()
                        .starts_with(MODE_PENDING_PREFIX))
            );
        } else if point.starts_with("after_document_mode_") {
            assert!(marker_exists, "published marker missing at {point}");
            assert!(
                !documents_exist,
                "storage opened before marker sync at {point}"
            );
        } else {
            assert!(
                marker_exists,
                "marker missing at storage genesis cut {point}"
            );
            assert!(
                documents_exist,
                "storage target missing at genesis cut {point}"
            );
        }

        let marker_before = if marker_exists {
            assert_eq!(fs::symlink_metadata(root.marker()).unwrap().nlink(), 1);
            Some(fs::read(root.marker()).expect("read pre-recovery marker"))
        } else {
            None
        };
        let recovered = open_distributed(&root.documents(), mode())
            .unwrap_or_else(|error| panic!("cut {point} did not resume: {error}"));
        assert_eq!(recovered.stats().visible.get(), 0);
        assert!(root.documents().join("wal.log").is_file());
        assert_marker_layout(&fs::read(root.marker()).expect("read recovered marker"));
        if let Some(marker_before) = marker_before {
            assert_eq!(
                fs::read(root.marker()).expect("read resumed marker"),
                marker_before
            );
        }
        assert!(!root.documents().join("genesis.pending").exists());
    }
}
