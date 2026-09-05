//! Verified physical backup import into a fresh database identity.

use std::{fs, path::Path};

use super::{Engine, EngineConfig, EngineOpenError};

impl Engine {
    /// Restores a complete `SQLite` backup of a local-stack database into an empty target.
    ///
    /// The source location supplies the authoritative owner binding. Scoped
    /// production locations are refused: their tenant data and metering require
    /// remapping to the new database before physical import can serve them. The backup must be a closed, complete database
    /// file, including any WAL contents already checkpointed by the producer.
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
        crate::storage::validate_local_restore_source(source)
            .map_err(|_| EngineOpenError::InvalidConfig)?;
        let parent = target.parent().ok_or(EngineOpenError::InvalidConfig)?;
        let staging = parent.join(format!(
            ".terrabase-restore-{}",
            crate::identity::process_generator().generate().to_text()
        ));
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
            drop(Self::open(staged_config.clone())?);
            crate::storage::import_backup_file(source, backup, &staging)
                .map_err(|()| EngineOpenError::FormatMismatch)?;
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

    #[test]
    fn physical_restore_verifies_source_and_preserves_existing_targets() {
        let root = std::env::temp_dir().join(format!(
            "terrabase-physical-restore-{}",
            crate::identity::process_generator().generate().to_text()
        ));
        let source = root.join("source");
        let engine = Engine::open(EngineConfig::durable(&source).expect("config")).expect("source");
        let source_id = engine.database_id();
        let tenant = TenantId::try_new("tenant-a").expect("tenant");
        let scope = engine.scope_for(&tenant);
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
        assert_eq!(fs::read(&backup).expect("backup"), original, "source backup is immutable");
        fs::remove_dir_all(root).expect("cleanup");
    }
}
