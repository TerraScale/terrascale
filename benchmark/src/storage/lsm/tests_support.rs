//! Shared fixtures of the LSM test suites (P1).

use std::path::{Path, PathBuf};

use crate::storage::{
    StoragePort,
    intent::{CommitIntent, StagedWrite},
    lsm::LsmStorage,
    write_set_digest,
};
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, StorageSequence, TenantId,
};

/// Creates one fresh LSM location directory.
pub(crate) fn location(tag: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "tbv2-lsm-{tag}-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |elapsed| elapsed.as_nanos())
    ));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

/// Opens one LSM engine at `dir`.
pub(crate) fn open(dir: &Path) -> LsmStorage {
    LsmStorage::open(dir).expect("opens")
}

/// Removes one test location.
pub(crate) fn cleanup(dir: &Path) {
    std::fs::remove_dir_all(dir).unwrap();
}

/// Commits enough documents to rotate and flush several segments.
pub(crate) fn seed_segments(storage: &mut LsmStorage, count: u64) {
    storage.tuning.max_memtable_entries = 4;
    let tenant = TenantId::try_new("acme").expect("valid tenant");
    for index in 0..count * 4 {
        let document = Document::new(
            DocumentId::try_new(&format!("order-{index:04}")).expect("valid identifier"),
            CollectionName::try_new("orders").expect("valid identifier"),
            DocumentValue::String("payload".to_owned()),
        );
        let intent = CommitIntent {
            tenant: tenant.clone(),
            operation: OperationId::try_new(&format!("op-{index}")).expect("valid identifier"),
            digest: write_set_digest(
                &tenant,
                &[StagedWrite::Put { document: document.clone(), expected: ExpectedVersion::Any }],
            ),
            snapshot_sequence: StorageSequence::new(0),
            durability: LocalDurability::Synced,
            writes: vec![StagedWrite::Put { document, expected: ExpectedVersion::Any }],
        };
        assert!(matches!(
            storage.commit(intent),
            crate::storage::intent::CommitResult::Committed(_)
        ));
    }
}
