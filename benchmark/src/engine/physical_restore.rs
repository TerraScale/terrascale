//! Verified physical backup import into a fresh database identity.

use std::{fs, path::Path};

use super::{Engine, EngineConfig, EngineOpenError, admission::EngineAdmissionGuard};
use std::sync::Arc;

impl Engine {
    /// Restores a complete `SQLite` backup of a local-stack database into an empty target.
    ///
    /// The source location supplies the authoritative owner binding. Scoped
    /// production locations are refused: their tenant data and metering require
    /// remapping to the new database before physical import can serve them.
    /// Imported receipts preserve the source cut without inheriting its billing
    /// or lifecycle state. The backup must be a closed, complete database file,
    /// including any WAL contents already checkpointed by the producer.
    /// A private staging location is verified before an atomic directory rename
    /// publishes it. Existing target data is never replaced.
    ///
    /// # Errors
    ///
    /// Returns `InvalidConfig` for a nonempty target or incompatible owner,
    /// `FormatMismatch` for a foreign or corrupt backup, and `StorageUnavailable`
    /// when staging, verification, or publication cannot complete.
    pub fn restore_local_sqlite_backup(
        source: &Path,
        backup: &Path,
        config: EngineConfig,
    ) -> Result<(), EngineOpenError> {
        let target = config.location.clone();
        let _restore_reservation =
            super::branch_restore::BranchRestoreReservation::acquire(&target)?;
        let _target_guard = Arc::new(EngineAdmissionGuard::acquire(&target)?);
        let metadata = fs::symlink_metadata(&target).map_err(|_| EngineOpenError::InvalidConfig)?;
        if !metadata.is_dir()
            || metadata.file_type().is_symlink()
            || fs::read_dir(&target).map_err(|_| EngineOpenError::InvalidConfig)?.next().is_some()
        {
            return Err(EngineOpenError::InvalidConfig);
        }
        if config.owner.is_some() {
            return Err(EngineOpenError::InvalidConfig);
        }
        let source_binding = crate::storage::validate_local_restore_source(source)
            .map_err(|_| EngineOpenError::InvalidConfig)?;
        let parent = target.parent().ok_or(EngineOpenError::InvalidConfig)?;
        let staging = parent.join(format!(".terrabase-restore-{}", crate::identity::generate()));
        let mut builder = fs::DirBuilder::new();
        #[cfg(unix)]
        {
            use std::os::unix::fs::DirBuilderExt;
            builder.mode(0o700);
        }
        builder.create(&staging).map_err(|_| EngineOpenError::StorageUnavailable)?;
        let result = (|| {
            let mut staged_config = config;
            staged_config.location.clone_from(&staging);
            let staged = Self::open(staged_config.clone())?;
            let child_owner = staged.database_owner().clone();
            drop(staged);
            crate::storage::import_backup_file(
                source,
                &source_binding,
                backup,
                &staging,
                &child_owner,
            )
            .map_err(|()| EngineOpenError::FormatMismatch)?;
            // Import rewrites the SQLite provenance, but the resulting
            // database still has to pass complete Engine admission before it
            // can be published. This also replays durable lifecycle state
            // that the low-level importer does not inspect.
            drop(Self::open(staged_config)?);
            fs::File::open(&staging)
                .and_then(|directory| directory.sync_all())
                .map_err(|_| EngineOpenError::StorageUnavailable)?;
            fs::rename(&staging, &target).map_err(|_| EngineOpenError::StorageUnavailable)?;
            fs::File::open(parent)
                .and_then(|directory| directory.sync_all())
                .map_err(|_| EngineOpenError::StorageUnavailable)
        })();
        if result.is_err() {
            let _ = fs::remove_dir_all(&staging);
        }
        result
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::types::{
        CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
        OperationId, TenantId,
    };

    fn placement(tenant: &TenantId, database: &str) -> crate::usage::UsagePlacement {
        crate::usage::UsagePlacement::try_new(
            tenant.clone(),
            "project",
            database,
            database,
            crate::RegionCode::GRU,
            vec![crate::RegionCode::GRU],
            database,
        )
        .expect("placement")
    }

    fn usage_request() -> crate::usage::UsagePageRequest {
        crate::usage::UsagePageRequest::try_new(None, crate::usage::MAX_USAGE_PAGE_SIZE, None)
            .expect("usage request")
    }

    #[test]
    fn physical_restore_verifies_source_and_preserves_existing_targets() {
        let root = std::env::temp_dir()
            .join(format!("terrabase-physical-restore-{}", crate::identity::generate()));
        let source = root.join("source");
        let engine = Engine::open(EngineConfig::durable(&source).expect("config")).expect("source");
        let source_id = engine.database_id();
        let tenant = TenantId::try_new("tenant-a").expect("tenant");
        let scope = engine.scope_for(&tenant);
        engine.configure_usage_placement(placement(&tenant, "source")).expect("source placement");
        let snapshot = engine.read_snapshot(&scope).expect("snapshot");
        let mut transaction = engine
            .begin(&scope, snapshot, OperationId::try_new("write").expect("operation"))
            .expect("begin");
        let id = DocumentId::try_new("doc").expect("id");
        let collection = CollectionName::try_new("docs").expect("collection");
        transaction
            .put(
                Document::new(id.clone(), collection.clone(), DocumentValue::Integer(42)),
                ExpectedVersion::New,
            )
            .expect("put");
        assert!(matches!(
            transaction.commit(LocalDurability::Synced),
            super::super::CommitOutcome::Committed(_)
        ));
        drop(engine);
        let backup = root.join("backup.sqlite3");
        fs::copy(super::super::database_file(&source), &backup).expect("closed backup");
        let original = fs::read(&backup).expect("backup bytes");
        let target = root.join("target");
        Engine::restore_local_sqlite_backup(
            &source,
            &backup,
            EngineConfig::durable(&target).expect("target config"),
        )
        .expect("restore");
        let restored =
            Engine::open(EngineConfig::durable(&target).expect("config")).expect("reopen");
        assert_ne!(restored.database_id(), source_id, "restore mints a new database identity");
        let scope = restored.scope_for(&tenant);
        assert!(restored.usage_placement(&scope).expect("child placement").is_none());
        assert!(
            restored.usage_page(&scope, usage_request()).expect("child usage").facts().is_empty()
        );
        restored
            .configure_usage_placement(placement(&tenant, "target"))
            .expect("fresh child placement");
        let snapshot = restored.read_snapshot(&scope).expect("snapshot");
        let transaction = restored
            .begin(&scope, snapshot, OperationId::try_new("read").expect("operation"))
            .expect("begin");
        assert_eq!(
            transaction
                .read_in_collection(&collection, &id)
                .expect("read")
                .expect("document")
                .value(),
            &DocumentValue::Integer(42)
        );
        drop(transaction);
        let snapshot = restored.read_snapshot(&scope).expect("child snapshot");
        let mut transaction = restored
            .begin(&scope, snapshot, OperationId::try_new("write").expect("operation"))
            .expect("child begin");
        transaction
            .put(Document::new(id, collection, DocumentValue::Integer(43)), ExpectedVersion::Any)
            .expect("child put");
        assert!(matches!(
            transaction.commit(LocalDurability::Synced),
            super::super::CommitOutcome::Committed(_)
        ));
        let usage = restored.usage_page(&scope, usage_request()).expect("new child usage");
        assert_eq!(usage.facts().len(), 1);
        assert_eq!(usage.facts()[0].fact().delta(), 1);
        assert_eq!(usage.facts()[0].fact().scope().database_id().as_str(), "target");
        drop(restored);
        let target_bytes = fs::read(super::super::database_file(&target)).expect("target bytes");
        assert_eq!(
            Engine::restore_local_sqlite_backup(
                &source,
                &backup,
                EngineConfig::durable(&target).expect("config")
            ),
            Err(EngineOpenError::InvalidConfig)
        );
        assert_eq!(fs::read(super::super::database_file(&target)).expect("target"), target_bytes);
        let foreign = root.join("foreign");
        drop(Engine::open(EngineConfig::durable(&foreign).expect("config")).expect("foreign"));
        let refused = root.join("refused");
        assert_eq!(
            Engine::restore_local_sqlite_backup(
                &foreign,
                &backup,
                EngineConfig::durable(&refused).expect("config")
            ),
            Err(EngineOpenError::FormatMismatch)
        );
        assert_eq!(fs::read_dir(&refused).expect("target").count(), 0);

        assert_invalid_lifecycle_backup_is_not_published(&root, &source, &backup);
        assert_eq!(fs::read(&backup).expect("backup"), original, "source backup is immutable");
        fs::remove_dir_all(root).expect("cleanup");
    }

    #[test]
    fn physical_import_removes_stale_sqlite_sidecars_before_copy() {
        let root = std::env::temp_dir()
            .join(format!("terrabase-physical-sidecars-{}", crate::identity::generate()));
        let source = root.join("source");
        let source_engine =
            Engine::open(EngineConfig::durable(&source).expect("source config")).expect("source");
        let backup = root.join("backup.sqlite3");
        drop(source_engine);
        fs::copy(super::super::database_file(&source), &backup).expect("backup");
        let target = root.join("target");
        let target_engine =
            Engine::open(EngineConfig::durable(&target).expect("target config")).expect("target");
        let child_owner = target_engine.database_owner().clone();
        drop(target_engine);
        let source_binding =
            crate::storage::validate_local_restore_source(&source).expect("source validates");
        for suffix in ["-wal", "-journal", "-shm"] {
            fs::write(target.join(format!("terrabase.sqlite3{suffix}")), b"stale")
                .expect("sidecar");
        }

        crate::storage::import_backup_file(
            &source,
            &source_binding,
            &backup,
            &target,
            &child_owner,
        )
        .expect("import");
        for suffix in ["-wal", "-journal", "-shm"] {
            assert!(!target.join(format!("terrabase.sqlite3{suffix}")).exists());
        }
        drop(Engine::open(EngineConfig::durable(&target).expect("reopen config")).expect("reopen"));
        fs::remove_dir_all(root).expect("cleanup");
    }

    #[cfg(unix)]
    #[test]
    fn physical_import_rejects_backup_symlink_without_following_it() {
        use std::os::unix::fs::symlink;

        let root = std::env::temp_dir()
            .join(format!("terrabase-physical-backup-link-{}", crate::identity::generate()));
        let source = root.join("source");
        let source_engine =
            Engine::open(EngineConfig::durable(&source).expect("source config")).expect("source");
        let backup = root.join("backup.sqlite3");
        drop(source_engine);
        fs::create_dir_all(&root).expect("root");
        let original = root.join("original.sqlite3");
        fs::copy(super::super::database_file(&source), &original).expect("backup");
        symlink(&original, &backup).expect("backup symlink");
        let target = root.join("target");
        let target_engine =
            Engine::open(EngineConfig::durable(&target).expect("target config")).expect("target");
        let child_owner = target_engine.database_owner().clone();
        drop(target_engine);
        let source_binding =
            crate::storage::validate_local_restore_source(&source).expect("source validates");

        assert!(
            crate::storage::import_backup_file(
                &source,
                &source_binding,
                &backup,
                &target,
                &child_owner,
            )
            .is_err()
        );
        fs::remove_dir_all(root).expect("cleanup");
    }

    fn assert_invalid_lifecycle_backup_is_not_published(root: &Path, source: &Path, backup: &Path) {
        // A backup can pass SQLite integrity and the importer's provenance
        // rewrite while still containing state rejected by Engine admission.
        // Such a target must never be atomically published.
        let invalid_backup = root.join("invalid-lifecycle.sqlite3");
        fs::copy(backup, &invalid_backup).expect("invalid backup");
        let connection = rusqlite::Connection::open(&invalid_backup).expect("open backup");
        connection
            .execute(
                "INSERT INTO index_lifecycle_events(sequence, payload, digest)
                 VALUES(1, ?1, zeroblob(32))",
                rusqlite::params![vec![0xff_u8]],
            )
            .expect("inject lifecycle event");
        drop(connection);
        let rejected = root.join("rejected");
        assert_eq!(
            Engine::restore_local_sqlite_backup(
                source,
                &invalid_backup,
                EngineConfig::durable(&rejected).expect("config"),
            ),
            Err(EngineOpenError::FormatMismatch)
        );
        assert_eq!(fs::read_dir(&rejected).expect("target").count(), 0);
    }
}
