//! Encrypted immutable-cut import into a new owned database.

use std::{fs, path::Path};
use super::{Engine, EngineConfig, EngineOpenError};
use crate::{storage::DatabaseOwner, types::{OperationId, StorageSequence}};

impl Engine {
    /// Installs an immutable encrypted snapshot as a fresh branch database.
    /// Source and target stay in the same tenant, project, environment and
    /// Region. The source registration must remain alive for this call.
    /// The operation marker makes a completed retry return the installed cut.
    ///
    /// # Errors
    /// Refuses foreign owners, plaintext sources, reused target operations,
    /// invalid history/schema transforms, or unavailable encryption keys.
    pub fn restore_encrypted_sqlite_branch(
        source: &Path,
        source_owner: &DatabaseOwner,
        config: EngineConfig,
        operation: &OperationId,
    ) -> Result<StorageSequence, EngineOpenError> {
        let child = config.owner.as_ref().ok_or(EngineOpenError::InvalidConfig)?;
        let keys = config.storage_keys.as_ref().ok_or(EngineOpenError::InvalidConfig)?;
        if child.database() == source_owner.database()
            || child.tenant() != source_owner.tenant()
            || child.project() != source_owner.project()
            || child.environment() != source_owner.environment()
            || child.region() != source_owner.region()
        { return Err(EngineOpenError::InvalidConfig); }
        let target = &config.location;
        let marker = format!("{}:{}:{}", operation.as_str(), source_owner.database(), child.database());
        if fs::read_dir(target).map_err(|_| EngineOpenError::InvalidConfig)?.next().is_some() {
            let engine = Self::open(config.clone())?;
            let connection = engine.open_sqlite_read_connection().map_err(|_| EngineOpenError::StorageUnavailable)?;
            let found: String = connection.query_row("SELECT value FROM meta WHERE key='branch_restore_operation'", [], |row| row.get(0)).map_err(|_| EngineOpenError::InvalidConfig)?;
            if found != marker { return Err(EngineOpenError::InvalidConfig); }
            let sequence: i64 = connection.query_row("SELECT value FROM meta WHERE key='branch_restore_cut'", [], |row| row.get(0)).map_err(|_| EngineOpenError::FormatMismatch)?;
            return Ok(StorageSequence::new(u64::try_from(sequence).map_err(|_| EngineOpenError::FormatMismatch)?));
        }
        let parent = target.parent().ok_or(EngineOpenError::InvalidConfig)?;
        let staging = parent.join(format!(".branch-restore-{}", crate::identity::process_generator().generate().to_text()));
        fs::create_dir(&staging).map_err(|_| EngineOpenError::StorageUnavailable)?;
        let result = (|| {
            crate::storage::load_or_create(&staging, Some(child), crate::storage::OwnershipMode::Production).map_err(|_| EngineOpenError::FormatMismatch)?;
            let registration = crate::storage::copy_encrypted_snapshot(source, &staging, source_owner, keys.clone()).map_err(|()| EngineOpenError::StorageUnavailable)?;
            let mut connection = crate::storage::open_physical_sqlite(&crate::storage::database_file(&staging), rusqlite::OpenFlags::SQLITE_OPEN_READ_WRITE).map_err(|_| EngineOpenError::StorageUnavailable)?;
            connection.execute_batch("PRAGMA synchronous=FULL;").map_err(|_| EngineOpenError::StorageUnavailable)?;
            let transaction = connection.transaction().map_err(|_| EngineOpenError::StorageUnavailable)?;
            let cut = crate::storage::transform_provenance(&transaction, child).map_err(|()| EngineOpenError::FormatMismatch)?;
            crate::storage::transform_branch_schema(&transaction, source_owner, child).map_err(|()| EngineOpenError::FormatMismatch)?;
            crate::storage::rebind_owner(&transaction, source, &staging).map_err(|()| EngineOpenError::FormatMismatch)?;
            let origin = config.replication_origin.as_ref().map_or_else(|| child.region().as_str().to_owned(), |origin| origin.as_str().to_owned());
            transaction.execute("INSERT INTO meta(key,value) VALUES('local_origin',?1) ON CONFLICT(key) DO UPDATE SET value=excluded.value", [origin]).map_err(|_| EngineOpenError::StorageUnavailable)?;
            let cut = i64::try_from(cut).map_err(|_| EngineOpenError::FormatMismatch)?;
            transaction.execute("INSERT INTO meta(key,value) VALUES('branch_restore_operation',?1)", [&marker]).map_err(|_| EngineOpenError::StorageUnavailable)?;
            transaction.execute("INSERT INTO meta(key,value) VALUES('branch_restore_cut',?1)", [cut]).map_err(|_| EngineOpenError::StorageUnavailable)?;
            transaction.commit().map_err(|_| EngineOpenError::StorageUnavailable)?;
            connection.execute_batch("PRAGMA wal_checkpoint(TRUNCATE)").map_err(|_| EngineOpenError::StorageUnavailable)?;
            drop(connection); drop(registration);
            let mut staged = config.clone(); staged.location.clone_from(&staging);
            drop(Self::open(staged)?);
            fs::File::open(&staging).and_then(|directory| directory.sync_all()).map_err(|_| EngineOpenError::StorageUnavailable)?;
            fs::rename(&staging, target).map_err(|_| EngineOpenError::StorageUnavailable)?;
            fs::File::open(parent).and_then(|directory| directory.sync_all()).map_err(|_| EngineOpenError::StorageUnavailable)?;
            Ok(StorageSequence::new(u64::try_from(cut).map_err(|_| EngineOpenError::FormatMismatch)?))
        })();
        if result.is_err() { let _ = fs::remove_dir_all(&staging); }
        result
    }
}
