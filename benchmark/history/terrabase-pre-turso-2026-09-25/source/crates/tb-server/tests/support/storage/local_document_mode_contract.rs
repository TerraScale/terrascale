use std::collections::BTreeMap;
use std::fs;
use std::os::unix::fs::symlink;
use std::path::{Path, PathBuf};
#[cfg(feature = "slow-tests")]
use std::process::{Command, ExitStatus};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use tb_core::{
    BudgetConfig, DatabaseId, Key, MembershipEpoch, MemoryBudget, OriginId, ReplicaId, ShardId,
    TenantId, TransactionId, WorkerId,
};
use tb_format::{
    ActiveSchema, FieldType, SchemaField, encode_active_key, encode_active_value,
    encode_catalog_into, encode_catalog_key, encode_schema_into,
};
use tb_storage::{
    DistributedDocumentMode, LocalDocumentMode, Mutation, Storage, StorageConfig, StorageError,
};

static NEXT_ROOT: AtomicU64 = AtomicU64::new(0);

const MODE_MARKER_NAME: &str = "local-document.mode";
const MODE_MARKER_LEN: usize = 132;
#[cfg(feature = "slow-tests")]
const MODE_PENDING_PREFIX: &str = "local-document.mode.pending.";
const TEST_TENANT: [u8; 16] = [11; 16];
const TEST_DATABASE: [u8; 16] = [12; 16];
const TEST_SHARD: u64 = 13;
const TEST_REPLICA: [u8; 16] = [14; 16];
const TEST_INCARNATION: [u8; 16] = [15; 16];
const TEST_EPOCH: u64 = 16;
const TEST_CONFIGURATION: [u8; 32] = [17; 32];
#[cfg(feature = "slow-tests")]
const MODE_CRASH_CHILD_ENV: &str = "TB_STORAGE_LOCAL_MODE_CRASH_CHILD";
#[cfg(feature = "slow-tests")]
const MODE_DIRECTORY_ENV: &str = "TB_STORAGE_LOCAL_MODE_DIRECTORY";
#[cfg(feature = "slow-tests")]
const MODE_CRASH_POINT_ENV: &str = "TB_STORAGE_CRASH_POINT";
#[cfg(feature = "slow-tests")]
const MODE_CRASH_SCOPE_ENV: &str = "TB_STORAGE_LOCAL_MODE_CRASH_SCOPE";
#[cfg(feature = "slow-tests")]
const MODE_CRASH_CUTS: [&str; 4] = [
    "after_local_document_target_create_sync",
    "after_local_document_mode_temp_sync",
    "after_local_document_mode_publish_before_target_sync",
    "after_local_document_mode_target_sync",
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
            "terrabase-local-document-mode-{}-{nanos}-{ordinal}",
            std::process::id()
        ));
        fs::create_dir_all(&path).expect("create temporary test root");
        Self(path)
    }

    fn path(&self) -> &Path {
        &self.0
    }

    fn shard(&self) -> PathBuf {
        self.0.join("documents")
    }

    fn marker(&self) -> PathBuf {
        self.shard().join(MODE_MARKER_NAME)
    }
}

impl Drop for TempRoot {
    fn drop(&mut self) {
        if self.0.exists() {
            fs::remove_dir_all(&self.0).expect("remove temporary test root");
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
        .expect("read directory")
        .map(|entry| entry.expect("read directory entry"))
        .collect();
    children.sort_by_key(std::fs::DirEntry::file_name);
    for child in children {
        let path = child.path();
        let relative = path
            .strip_prefix(root)
            .expect("entry is below snapshot root")
            .to_path_buf();
        let file_type = child.file_type().expect("read file type");
        if file_type.is_symlink() {
            entries.insert(
                relative,
                TreeEntry::Symlink(fs::read_link(path).expect("read symlink target")),
            );
        } else if file_type.is_dir() {
            entries.insert(relative, TreeEntry::Directory);
            visit_tree(root, &path, entries);
        } else if file_type.is_file() {
            entries.insert(
                relative,
                TreeEntry::File(fs::read(path).expect("read file")),
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
    TenantId::from_bytes(TEST_TENANT)
}

fn mode_with(
    tenant: [u8; 16],
    database: [u8; 16],
    shard: u64,
    replica: [u8; 16],
    incarnation: [u8; 16],
    epoch: u64,
    configuration: [u8; 32],
) -> LocalDocumentMode {
    LocalDocumentMode::new(
        TenantId::from_bytes(tenant),
        DatabaseId::from_bytes(database),
        ShardId::new(shard),
        OriginId {
            replica_id: ReplicaId::from_bytes(replica),
            incarnation,
        },
        MembershipEpoch::new(epoch),
        configuration,
    )
}

fn mode() -> LocalDocumentMode {
    mode_with(
        TEST_TENANT,
        TEST_DATABASE,
        TEST_SHARD,
        TEST_REPLICA,
        TEST_INCARNATION,
        TEST_EPOCH,
        TEST_CONFIGURATION,
    )
}

#[cfg(feature = "slow-tests")]
fn other_mode() -> LocalDocumentMode {
    mode_with(
        TEST_TENANT,
        [18; 16],
        TEST_SHARD,
        TEST_REPLICA,
        TEST_INCARNATION,
        TEST_EPOCH,
        TEST_CONFIGURATION,
    )
}

fn storage_config(path: &Path, owner: TenantId) -> StorageConfig {
    StorageConfig::local(path, owner, WorkerId(1))
}

fn budget() -> MemoryBudget {
    MemoryBudget::new(BudgetConfig::new(128 * 1024 * 1024, 0).expect("valid test budget"))
}

fn open_local(
    path: &Path,
    requested_mode: LocalDocumentMode,
    storage_tenant: TenantId,
) -> Result<Storage, StorageError> {
    Storage::open_local_document(
        storage_config(path, storage_tenant),
        budget(),
        requested_mode,
    )
}

fn bootstrap_local(
    path: &Path,
    requested_mode: LocalDocumentMode,
    storage_tenant: TenantId,
) -> Result<Storage, StorageError> {
    Storage::bootstrap_local_document(
        storage_config(path, storage_tenant),
        budget(),
        requested_mode,
    )
}

fn distributed_mode() -> DistributedDocumentMode {
    DistributedDocumentMode::new(
        [21; 16],
        tenant(),
        DatabaseId::from_bytes([22; 16]),
        ShardId::new(23),
        MembershipEpoch::new(24),
        [25; 32],
        OriginId {
            replica_id: ReplicaId::from_bytes([26; 16]),
            incarnation: [27; 16],
        },
    )
}

#[test]
fn explicit_bootstrap_accepts_absent_or_verified_empty_target_and_reopens_exactly() {
    for create_empty_first in [false, true] {
        let root = TempRoot::new();
        let shard = root.shard();
        if create_empty_first {
            fs::create_dir(&shard).expect("create verified-empty target");
            assert!(
                fs::read_dir(&shard)
                    .expect("read empty target")
                    .next()
                    .is_none()
            );
        } else {
            assert!(!shard.exists());
        }

        let before_strict_open = tree_snapshot(root.path());
        assert!(open_local(&shard, mode(), tenant()).is_err());
        assert_eq!(shard.exists(), create_empty_first);
        assert_eq!(
            tree_snapshot(root.path()),
            before_strict_open,
            "strict open mutated an unmarked target"
        );

        let first = bootstrap_local(&shard, mode(), tenant())
            .expect("explicitly bootstrap absent or verified-empty local target");
        assert_eq!(first.stats().visible.get(), 0);
        drop(first);

        let reopened = open_local(&shard, mode(), tenant()).expect("exact marker reopen");
        assert_eq!(reopened.stats().visible.get(), 0);
        drop(reopened);
    }
}

fn wrong_mode_cases() -> [(&'static str, LocalDocumentMode, TenantId); 7] {
    let wrong_tenant = mode_with(
        [31; 16],
        TEST_DATABASE,
        TEST_SHARD,
        TEST_REPLICA,
        TEST_INCARNATION,
        TEST_EPOCH,
        TEST_CONFIGURATION,
    );
    [
        ("tenant", wrong_tenant, TenantId::from_bytes([31; 16])),
        (
            "database",
            mode_with(
                TEST_TENANT,
                [32; 16],
                TEST_SHARD,
                TEST_REPLICA,
                TEST_INCARNATION,
                TEST_EPOCH,
                TEST_CONFIGURATION,
            ),
            tenant(),
        ),
        (
            "shard",
            mode_with(
                TEST_TENANT,
                TEST_DATABASE,
                TEST_SHARD + 1,
                TEST_REPLICA,
                TEST_INCARNATION,
                TEST_EPOCH,
                TEST_CONFIGURATION,
            ),
            tenant(),
        ),
        (
            "replica",
            mode_with(
                TEST_TENANT,
                TEST_DATABASE,
                TEST_SHARD,
                [33; 16],
                TEST_INCARNATION,
                TEST_EPOCH,
                TEST_CONFIGURATION,
            ),
            tenant(),
        ),
        (
            "incarnation",
            mode_with(
                TEST_TENANT,
                TEST_DATABASE,
                TEST_SHARD,
                TEST_REPLICA,
                [34; 16],
                TEST_EPOCH,
                TEST_CONFIGURATION,
            ),
            tenant(),
        ),
        (
            "epoch",
            mode_with(
                TEST_TENANT,
                TEST_DATABASE,
                TEST_SHARD,
                TEST_REPLICA,
                TEST_INCARNATION,
                TEST_EPOCH + 1,
                TEST_CONFIGURATION,
            ),
            tenant(),
        ),
        (
            "configuration",
            mode_with(
                TEST_TENANT,
                TEST_DATABASE,
                TEST_SHARD,
                TEST_REPLICA,
                TEST_INCARNATION,
                TEST_EPOCH,
                [35; 32],
            ),
            tenant(),
        ),
    ]
}

#[test]
fn strict_open_rejects_missing_and_every_wrong_mode_identity_without_mutation() {
    let root = TempRoot::new();
    let missing = root.shard();
    assert!(open_local(&missing, mode(), tenant()).is_err());
    assert!(!missing.exists(), "strict open must not create a target");

    let shard = root.shard();
    drop(bootstrap_local(&shard, mode(), tenant()).expect("bootstrap local mode"));
    let before = tree_snapshot(root.path());

    for (name, requested_mode, storage_tenant) in wrong_mode_cases() {
        assert!(
            open_local(&shard, requested_mode, storage_tenant).is_err(),
            "strict open accepted wrong {name}"
        );
        assert_eq!(
            tree_snapshot(root.path()),
            before,
            "wrong {name} changed disk"
        );
    }
}

#[test]
fn zero_identity_values_are_rejected_before_creating_any_target() {
    let invalid = [
        mode_with(
            TEST_TENANT,
            TEST_DATABASE,
            TEST_SHARD,
            TEST_REPLICA,
            [0; 16],
            TEST_EPOCH,
            TEST_CONFIGURATION,
        ),
        mode_with(
            TEST_TENANT,
            TEST_DATABASE,
            TEST_SHARD,
            TEST_REPLICA,
            TEST_INCARNATION,
            0,
            TEST_CONFIGURATION,
        ),
        mode_with(
            TEST_TENANT,
            TEST_DATABASE,
            TEST_SHARD,
            TEST_REPLICA,
            TEST_INCARNATION,
            TEST_EPOCH,
            [0; 32],
        ),
    ];

    for (index, invalid_mode) in invalid.into_iter().enumerate() {
        let root = TempRoot::new();
        let shard = root.path().join(format!("invalid-{index}"));
        let before = tree_snapshot(root.path());
        assert!(bootstrap_local(&shard, invalid_mode, tenant()).is_err());
        assert!(!shard.exists());
        assert_eq!(tree_snapshot(root.path()), before);
    }
}

#[test]
fn existing_raw_state_and_valid_catalogs_are_refused_unchanged() {
    for fixture in ["raw", "catalog_only", "catalog_and_active"] {
        let root = TempRoot::new();
        let shard = root.shard();
        match fixture {
            "raw" => {
                let raw = Storage::open(storage_config(&shard, tenant()), budget())
                    .expect("open ordinary raw shard");
                let expiry = raw.now_unix_ms().saturating_add(60_000);
                raw.commit_batch(
                    raw.snapshot(),
                    TransactionId::new(expiry, [41; 16]),
                    &[Mutation::Put {
                        key: Key::new(b"preexisting raw data".to_vec()).expect("raw key"),
                        value: b"must remain untouched".to_vec(),
                    }],
                )
                .expect("seed existing raw data");
            }
            "catalog_only" => seed_raw_catalog(&shard, false),
            "catalog_and_active" => seed_raw_catalog(&shard, true),
            _ => unreachable!("fixture list is exhaustive"),
        }

        let before = tree_snapshot(root.path());
        assert!(
            bootstrap_local(&shard, mode(), tenant()).is_err(),
            "local mode accepted preexisting {fixture} state"
        );
        assert_eq!(
            tree_snapshot(root.path()),
            before,
            "{fixture} state changed"
        );
        assert!(
            open_local(&shard, mode(), tenant()).is_err(),
            "strict local open accepted preexisting {fixture} state"
        );
        assert_eq!(
            tree_snapshot(root.path()),
            before,
            "{fixture} state changed"
        );
    }
}

fn seed_raw_catalog(path: &Path, include_active: bool) {
    let collection = tb_core::CollectionId::from_bytes([42; 16]);
    let schema = [SchemaField {
        field_id: 1,
        parent_field_id: 0,
        value_type: FieldType::Utf8,
        element_type: None,
        flags: 1,
        name: "label",
    }];
    let mut schema_bytes = Vec::new();
    encode_schema_into(&mut schema_bytes, &schema).expect("encode valid schema fixture");
    let mut catalog_bytes = Vec::new();
    let digest = encode_catalog_into(&mut catalog_bytes, collection, 1, 0, &schema_bytes)
        .expect("encode valid catalog fixture");
    let catalog_key = encode_catalog_key(collection, 1).expect("encode catalog key");
    let mut mutations = vec![Mutation::Put {
        key: Key::new(catalog_key.to_vec()).expect("catalog key"),
        value: catalog_bytes,
    }];
    if include_active {
        let active_key = encode_active_key(collection);
        let active_value = encode_active_value(ActiveSchema {
            generation: 1,
            digest,
        })
        .expect("encode active schema pointer");
        mutations.push(Mutation::Put {
            key: Key::new(active_key.to_vec()).expect("active key"),
            value: active_value.to_vec(),
        });
    }

    let raw = Storage::open(storage_config(path, tenant()), budget()).expect("open raw fixture");
    let expiry = raw.now_unix_ms().saturating_add(60_000);
    raw.commit_batch(
        raw.snapshot(),
        TransactionId::new(expiry, [43; 16]),
        &mutations,
    )
    .expect("write valid raw catalog fixture");
}

#[test]
fn raw_distributed_and_local_owners_cannot_cross_open_the_same_shard() {
    let root = TempRoot::new();
    let shard = root.shard();
    drop(bootstrap_local(&shard, mode(), tenant()).expect("bootstrap local mode"));
    let before = tree_snapshot(root.path());

    assert!(Storage::open(storage_config(&shard, tenant()), budget()).is_err());
    assert_eq!(
        tree_snapshot(root.path()),
        before,
        "raw open changed local shard"
    );

    assert!(
        Storage::open_distributed_document(
            storage_config(&shard, tenant()),
            budget(),
            distributed_mode(),
        )
        .is_err()
    );
    assert_eq!(
        tree_snapshot(root.path()),
        before,
        "distributed open changed local shard"
    );

    assert!(open_local(&shard, mode(), tenant()).is_ok());
}

#[test]
fn local_and_raw_open_refuse_an_existing_distributed_shard_unchanged() {
    let root = TempRoot::new();
    let shard = root.shard();
    let distributed = Storage::open_distributed_document(
        storage_config(&shard, tenant()),
        budget(),
        distributed_mode(),
    )
    .expect("bootstrap distributed shard");
    drop(distributed);
    let before = tree_snapshot(root.path());

    assert!(Storage::open(storage_config(&shard, tenant()), budget()).is_err());
    assert_eq!(
        tree_snapshot(root.path()),
        before,
        "raw open changed distributed shard"
    );
    assert!(open_local(&shard, mode(), tenant()).is_err());
    assert_eq!(
        tree_snapshot(root.path()),
        before,
        "strict local open changed distributed shard"
    );
    assert!(bootstrap_local(&shard, mode(), tenant()).is_err());
    assert_eq!(
        tree_snapshot(root.path()),
        before,
        "local bootstrap changed distributed shard"
    );
}

#[test]
fn local_mode_rejects_symlink_and_non_normalized_path_aliases() {
    let root = TempRoot::new();
    let shard = root.shard();
    drop(bootstrap_local(&shard, mode(), tenant()).expect("bootstrap local mode"));

    let alias_parent = root.path().join("alias-parent");
    fs::create_dir(&alias_parent).expect("create alias parent");
    let symlink_path = alias_parent.join("documents");
    symlink(&shard, &symlink_path).expect("create shard symlink");
    let before = tree_snapshot(root.path());

    assert!(open_local(&symlink_path, mode(), tenant()).is_err());
    assert_eq!(
        tree_snapshot(root.path()),
        before,
        "symlink open changed tree"
    );

    assert!(Storage::open(storage_config(&symlink_path, tenant()), budget()).is_err());
    assert_eq!(
        tree_snapshot(root.path()),
        before,
        "raw alias open changed tree"
    );

    assert!(
        Storage::open_distributed_document(
            storage_config(&symlink_path, tenant()),
            budget(),
            distributed_mode(),
        )
        .is_err()
    );
    assert_eq!(
        tree_snapshot(root.path()),
        before,
        "distributed alias open changed tree"
    );

    let non_normalized = alias_parent.join("nested").join("..").join("documents");
    fs::create_dir(alias_parent.join("nested")).expect("create normalization component");
    let before = tree_snapshot(root.path());
    assert!(open_local(&non_normalized, mode(), tenant()).is_err());
    assert_eq!(
        tree_snapshot(root.path()),
        before,
        "non-normalized alias open changed tree"
    );
}

#[test]
fn corrupt_or_linked_local_mode_markers_fail_closed_without_mutation() {
    for case in [
        "bad-magic",
        "wrong-version",
        "nonzero-flags",
        "wrong-length",
        "bad-checksum",
        "truncated",
    ] {
        let root = TempRoot::new();
        let shard = root.shard();
        drop(bootstrap_local(&shard, mode(), tenant()).expect("bootstrap local mode"));
        let mut damaged = fs::read(root.marker()).expect("read marker fixture");
        assert_eq!(damaged.len(), MODE_MARKER_LEN);
        match case {
            "bad-magic" => damaged[0] ^= 0x80,
            "wrong-version" => damaged[8..10].copy_from_slice(&2_u16.to_le_bytes()),
            "nonzero-flags" => damaged[10] = 1,
            "wrong-length" => damaged[12..16].copy_from_slice(&131_u32.to_le_bytes()),
            "bad-checksum" => damaged[128] ^= 1,
            "truncated" => {
                damaged.truncate(MODE_MARKER_LEN - 1);
            }
            _ => unreachable!("known marker case"),
        }
        if matches!(
            case,
            "bad-magic" | "wrong-version" | "nonzero-flags" | "wrong-length"
        ) {
            let checksum = crc32c::crc32c(&damaged[..128]);
            damaged[128..132].copy_from_slice(&checksum.to_le_bytes());
        }
        fs::write(root.marker(), damaged).expect("corrupt marker fixture");
        let before = tree_snapshot(root.path());
        assert!(
            open_local(&shard, mode(), tenant()).is_err(),
            "strict open accepted {case} marker"
        );
        assert_eq!(
            tree_snapshot(root.path()),
            before,
            "strict open changed {case} marker"
        );

        if case == "bad-magic" {
            assert!(Storage::open(storage_config(&shard, tenant()), budget()).is_err());
            assert_eq!(
                tree_snapshot(root.path()),
                before,
                "raw open changed marker"
            );
            assert!(
                Storage::open_distributed_document(
                    storage_config(&shard, tenant()),
                    budget(),
                    distributed_mode(),
                )
                .is_err()
            );
            assert_eq!(
                tree_snapshot(root.path()),
                before,
                "distributed open changed marker"
            );
        }
    }

    let linked_root = TempRoot::new();
    let linked_shard = linked_root.shard();
    drop(bootstrap_local(&linked_shard, mode(), tenant()).expect("bootstrap local mode"));
    let marker = linked_root.marker();
    let external = linked_root.path().join("marker-target");
    fs::rename(&marker, &external).expect("move marker payload outside target entry");
    symlink(&external, &marker).expect("link marker path");
    let before = tree_snapshot(linked_root.path());
    assert!(open_local(&linked_shard, mode(), tenant()).is_err());
    assert_eq!(
        tree_snapshot(linked_root.path()),
        before,
        "linked marker changed"
    );
    assert!(Storage::open(storage_config(&linked_shard, tenant()), budget()).is_err());
    assert_eq!(
        tree_snapshot(linked_root.path()),
        before,
        "raw open changed linked marker"
    );
    assert!(
        Storage::open_distributed_document(
            storage_config(&linked_shard, tenant()),
            budget(),
            distributed_mode(),
        )
        .is_err()
    );
    assert_eq!(
        tree_snapshot(linked_root.path()),
        before,
        "distributed open changed linked marker"
    );
}

#[cfg(feature = "slow-tests")]
#[test]
fn local_mode_crash_child_process() {
    if std::env::var_os(MODE_CRASH_CHILD_ENV).is_none() {
        return;
    }
    let directory = PathBuf::from(
        std::env::var_os(MODE_DIRECTORY_ENV).expect("parent sets local mode directory"),
    );
    let _point = std::env::var(MODE_CRASH_POINT_ENV).expect("parent sets local mode crash point");
    let requested_mode = if std::env::var_os(MODE_CRASH_SCOPE_ENV).is_some() {
        other_mode()
    } else {
        mode()
    };
    let _storage = bootstrap_local(&directory.join("documents"), requested_mode, tenant())
        .expect("configured local mode crash hook should terminate bootstrap");
    panic!("configured local mode crash hook did not terminate child");
}

#[cfg(feature = "slow-tests")]
fn run_local_mode_crash_child(root: &TempRoot, point: &str, wrong_scope: bool) -> ExitStatus {
    let mut command =
        Command::new(std::env::current_exe().expect("locate integration test executable"));
    command
        .arg("--exact")
        .arg("storage_slow::local_document_mode_contract::local_mode_crash_child_process")
        .env(MODE_CRASH_CHILD_ENV, "1")
        .env(MODE_DIRECTORY_ENV, root.path())
        .env(MODE_CRASH_POINT_ENV, point)
        .env_remove(MODE_CRASH_SCOPE_ENV);
    if wrong_scope {
        command.env(MODE_CRASH_SCOPE_ENV, "other");
    }
    command.status().expect("run local mode crash child")
}

#[cfg(feature = "slow-tests")]
#[test]
fn incomplete_local_mode_bootstrap_resumes_after_each_durable_crash_cut() {
    for point in MODE_CRASH_CUTS {
        let root = TempRoot::new();
        assert_eq!(
            run_local_mode_crash_child(&root, point, false).code(),
            Some(86),
            "crash point {point}"
        );

        let recovered = bootstrap_local(&root.shard(), mode(), tenant())
            .unwrap_or_else(|error| panic!("bootstrap did not resume after {point}: {error}"));
        assert_eq!(recovered.stats().visible.get(), 0);
        drop(recovered);

        let reopened = open_local(&root.shard(), mode(), tenant())
            .unwrap_or_else(|error| panic!("strict reopen failed after {point}: {error}"));
        assert_eq!(reopened.stats().visible.get(), 0);
    }
}

#[cfg(feature = "slow-tests")]
fn pending_marker(root: &TempRoot) -> PathBuf {
    fs::read_dir(root.shard())
        .expect("read incomplete target")
        .map(|entry| entry.expect("read target entry").path())
        .find(|path| {
            path.file_name()
                .and_then(|name| name.to_str())
                .is_some_and(|name| name.starts_with(MODE_PENDING_PREFIX))
        })
        .expect("find the hook-created pending marker")
}

#[cfg(feature = "slow-tests")]
#[test]
fn unsafe_or_mismatched_pending_markers_fail_closed_without_mutation() {
    let point = "after_local_document_mode_temp_sync";

    let corrupt_root = TempRoot::new();
    assert_eq!(
        run_local_mode_crash_child(&corrupt_root, point, false).code(),
        Some(86)
    );
    let pending = pending_marker(&corrupt_root);
    let mut bytes = fs::read(&pending).expect("read hook-created pending marker");
    let first = bytes.first_mut().expect("pending marker is nonempty");
    *first ^= 0x80;
    fs::write(&pending, bytes).expect("corrupt pending marker");
    let before = tree_snapshot(corrupt_root.path());
    assert!(bootstrap_local(&corrupt_root.shard(), mode(), tenant()).is_err());
    assert_eq!(tree_snapshot(corrupt_root.path()), before);

    let duplicate_root = TempRoot::new();
    assert_eq!(
        run_local_mode_crash_child(&duplicate_root, point, false).code(),
        Some(86)
    );
    let pending = pending_marker(&duplicate_root);
    let duplicate = duplicate_root
        .shard()
        .join("local-document.mode.pending.test-duplicate");
    fs::copy(&pending, duplicate).expect("duplicate valid pending marker");
    let before = tree_snapshot(duplicate_root.path());
    assert!(bootstrap_local(&duplicate_root.shard(), mode(), tenant()).is_err());
    assert_eq!(tree_snapshot(duplicate_root.path()), before);

    let linked_root = TempRoot::new();
    assert_eq!(
        run_local_mode_crash_child(&linked_root, point, false).code(),
        Some(86)
    );
    let pending = pending_marker(&linked_root);
    let external = linked_root.path().join("pending-target");
    fs::rename(&pending, &external).expect("move pending data outside target entry");
    symlink(&external, &pending).expect("link pending marker path");
    let before = tree_snapshot(linked_root.path());
    assert!(bootstrap_local(&linked_root.shard(), mode(), tenant()).is_err());
    assert_eq!(tree_snapshot(linked_root.path()), before);

    let wrong_scope_root = TempRoot::new();
    assert_eq!(
        run_local_mode_crash_child(&wrong_scope_root, point, true).code(),
        Some(86)
    );
    let before = tree_snapshot(wrong_scope_root.path());
    assert!(bootstrap_local(&wrong_scope_root.shard(), mode(), tenant()).is_err());
    assert_eq!(tree_snapshot(wrong_scope_root.path()), before);
}
