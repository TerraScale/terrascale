//! Encrypted immutable-cut import into a new owned database.

use super::{Engine, EngineConfig, EngineOpenError};
use crate::{
    storage::DatabaseOwner,
    types::{OperationId, StorageSequence},
};
use std::{fs, path::Path};

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
        source: &Engine,
        config: &EngineConfig,
        operation: &OperationId,
    ) -> Result<StorageSequence, EngineOpenError> {
        config.validate_storage_keys()?;
        let source_owner = source.database_owner();
        let child = config.owner.as_ref().ok_or(EngineOpenError::InvalidConfig)?;
        let keys = config.storage_keys.as_ref().ok_or(EngineOpenError::InvalidConfig)?;
        if child.database() == source_owner.database()
            || child.tenant() != source_owner.tenant()
            || child.project() != source_owner.project()
            || child.environment() != source_owner.environment()
            || child.region() != source_owner.region()
        {
            return Err(EngineOpenError::InvalidConfig);
        }
        let target = &config.location;
        if let Ok(metadata) = fs::symlink_metadata(target) {
            if !metadata.is_dir() || metadata.file_type().is_symlink() {
                return Err(EngineOpenError::InvalidConfig);
            }
        }
        let marker =
            format!("{}:{}:{}", operation.as_str(), source_owner.database(), child.database());
        if fs::read_dir(target).ok().and_then(|entries| entries.into_iter().next()).is_some() {
            let engine = Self::open(config.clone())?;
            let connection = engine
                .open_sqlite_read_connection()
                .map_err(|_| EngineOpenError::StorageUnavailable)?;
            let found: String = connection
                .query_row(
                    "SELECT value FROM meta WHERE key='branch_restore_operation'",
                    [],
                    |row| row.get(0),
                )
                .map_err(|_| EngineOpenError::InvalidConfig)?;
            if found != marker {
                return Err(EngineOpenError::InvalidConfig);
            }
            let sequence: i64 = connection
                .query_row("SELECT value FROM meta WHERE key='branch_restore_cut'", [], |row| {
                    row.get(0)
                })
                .map_err(|_| EngineOpenError::FormatMismatch)?;
            sync_parent(target)?;
            return Ok(StorageSequence::new(
                u64::try_from(sequence).map_err(|_| EngineOpenError::FormatMismatch)?,
            ));
        }
        // `EngineConfig::durable` creates the target directory up front. An
        // empty directory is only a reservation; remove it so the prepared
        // staging directory can be installed atomically with rename.
        match fs::remove_dir(target) {
            Ok(()) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) if error.kind() == std::io::ErrorKind::DirectoryNotEmpty => {
                if let Some(existing) = installed_cut(config, &marker) {
                    sync_parent(target)?;
                    return Ok(StorageSequence::new(existing));
                }
                return Err(EngineOpenError::InvalidConfig);
            }
            Err(_) => return Err(EngineOpenError::StorageUnavailable),
        }
        let parent = target.parent().ok_or(EngineOpenError::InvalidConfig)?;
        let staging = parent.join(format!(".branch-restore-{}", crate::identity::generate()));
        let mut builder = fs::DirBuilder::new();
        #[cfg(unix)]
        {
            use std::os::unix::fs::DirBuilderExt;
            builder.mode(0o700);
        }
        builder.create(&staging).map_err(|_| EngineOpenError::StorageUnavailable)?;
        let result = (|| {
            crate::storage::load_or_create(
                &staging,
                Some(child),
                crate::storage::OwnershipMode::Production,
            )
            .map_err(|_| EngineOpenError::FormatMismatch)?;
            let source_owner = source.database_owner();
            let mut source_path = None;
            let registration = source
                .with_quiescent_wal(|source_location| {
                    source_path = Some(source_location.to_owned());
                    crate::storage::copy_encrypted_snapshot(
                        source_location,
                        &staging,
                        source_owner,
                        keys.clone(),
                    )
                })
                .map_err(|_| EngineOpenError::StorageUnavailable)?
                .map_err(|()| EngineOpenError::StorageUnavailable)?;
            let cut = transform_snapshot(
                source_path.as_deref().ok_or(EngineOpenError::StorageUnavailable)?,
                &staging,
                source_owner,
                child,
                config,
                &marker,
            )?;
            drop(registration);
            let mut staged = config.clone();
            staged.location.clone_from(&staging);
            drop(Self::open(staged)?);
            fs::File::open(&staging)
                .and_then(|directory| directory.sync_all())
                .map_err(|_| EngineOpenError::StorageUnavailable)?;
            if fs::rename(&staging, target).is_err() {
                // Another restore may have won the target rename while this
                // source was quiesced. Re-read the durable marker to make an
                // exact same-operation loser idempotent; a different marker
                // remains a conflict.
                if let Some(existing) = installed_cut(config, &marker) {
                    fs::remove_dir_all(&staging)
                        .map_err(|_| EngineOpenError::StorageUnavailable)?;
                    sync_parent(target)?;
                    return Ok(StorageSequence::new(existing));
                }
                return Err(EngineOpenError::StorageUnavailable);
            }
            sync_parent(target)?;
            Ok(StorageSequence::new(
                u64::try_from(cut).map_err(|_| EngineOpenError::FormatMismatch)?,
            ))
        })();
        if result.is_err() {
            let _ = fs::remove_dir_all(&staging);
        }
        result
    }
}

fn sync_parent(target: &Path) -> Result<(), EngineOpenError> {
    let parent = target.parent().ok_or(EngineOpenError::InvalidConfig)?;
    fs::File::open(parent)
        .and_then(|directory| directory.sync_all())
        .map_err(|_| EngineOpenError::StorageUnavailable)
}

fn installed_cut(config: &EngineConfig, marker: &str) -> Option<u64> {
    let engine = Engine::open(config.clone()).ok()?;
    let connection = engine.open_sqlite_read_connection().ok()?;
    let found: String = connection
        .query_row("SELECT value FROM meta WHERE key='branch_restore_operation'", [], |row| {
            row.get(0)
        })
        .ok()?;
    if found != marker {
        return None;
    }
    let cut = connection
        .query_row("SELECT value FROM meta WHERE key='branch_restore_cut'", [], |row| {
            row.get::<_, i64>(0)
        })
        .ok()?;
    u64::try_from(cut).ok()
}

fn transform_snapshot(
    source: &Path,
    staging: &Path,
    source_owner: &DatabaseOwner,
    child: &DatabaseOwner,
    config: &EngineConfig,
    marker: &str,
) -> Result<i64, EngineOpenError> {
    let mut connection = crate::storage::open_physical_sqlite(
        &crate::storage::database_file(staging),
        rusqlite::OpenFlags::SQLITE_OPEN_READ_WRITE,
    )
    .map_err(|_| EngineOpenError::StorageUnavailable)?;
    connection
        .execute_batch("PRAGMA synchronous=FULL; PRAGMA temp_store=MEMORY;")
        .map_err(|_| EngineOpenError::StorageUnavailable)?;
    let transaction = connection.transaction().map_err(|_| EngineOpenError::StorageUnavailable)?;
    let cut = crate::storage::transform_provenance(&transaction, child)
        .map_err(|()| EngineOpenError::FormatMismatch)?;
    crate::storage::transform_branch_schema(&transaction, source_owner, child)
        .map_err(|()| EngineOpenError::FormatMismatch)?;
    crate::storage::rebind_owner(&transaction, source, staging)
        .map_err(|()| EngineOpenError::FormatMismatch)?;
    let origin = config
        .replication_origin
        .as_ref()
        .map_or_else(|| child.region().as_str().to_owned(), |origin| origin.as_str().to_owned());
    transaction.execute("INSERT INTO meta(key,value) VALUES('local_origin',?1) ON CONFLICT(key) DO UPDATE SET value=excluded.value", [origin]).map_err(|_| EngineOpenError::StorageUnavailable)?;
    let cut = i64::try_from(cut).map_err(|_| EngineOpenError::FormatMismatch)?;
    transaction
        .execute(
            "INSERT INTO meta(key,value) VALUES('branch_restore_operation',?1) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            [&marker],
        )
        .map_err(|_| EngineOpenError::StorageUnavailable)?;
    transaction
        .execute("INSERT INTO meta(key,value) VALUES('branch_restore_cut',?1) ON CONFLICT(key) DO UPDATE SET value=excluded.value", [cut])
        .map_err(|_| EngineOpenError::StorageUnavailable)?;
    transaction.commit().map_err(|_| EngineOpenError::StorageUnavailable)?;
    connection
        .execute_batch("PRAGMA wal_checkpoint(TRUNCATE)")
        .map_err(|_| EngineOpenError::StorageUnavailable)?;
    Ok(cut)
}
