//! Encrypted immutable-cut import into a new owned database.

use super::{Engine, EngineConfig, EngineOpenError};
use crate::{
    storage::DatabaseOwner,
    types::{OperationId, StorageSequence, TenantId},
    usage::UsagePlacement,
};
use std::{
    collections::BTreeMap,
    fs,
    path::{Path, PathBuf},
    sync::{Arc, Condvar, LazyLock, Mutex},
};

/// A cross-process reservation for one branch target.
///
/// The lock file is intentionally retained after the operation. Removing a
/// lock file while another process has it open would allow a third process to
/// create a second inode and enter a different lock domain. `flock` releases
/// the reservation automatically when the owning process exits.
pub(super) struct BranchRestoreReservation {
    #[cfg(unix)]
    file: fs::File,
    _process_guard: ProcessTargetGuard,
}

struct ProcessTargetLock {
    held: Mutex<bool>,
    wake: Condvar,
}

struct ProcessTargetGuard {
    lock: Arc<ProcessTargetLock>,
}

impl ProcessTargetLock {
    fn acquire(self: &Arc<Self>) -> ProcessTargetGuard {
        let mut held = self.held.lock().expect("branch target lock poisoned");
        while *held {
            held = self.wake.wait(held).expect("branch target lock poisoned");
        }
        *held = true;
        ProcessTargetGuard { lock: Arc::clone(self) }
    }
}

impl Drop for ProcessTargetGuard {
    fn drop(&mut self) {
        let mut held = self.lock.held.lock().expect("branch target lock poisoned");
        *held = false;
        self.lock.wake.notify_one();
    }
}

static PROCESS_TARGET_LOCKS: LazyLock<Mutex<BTreeMap<PathBuf, Arc<ProcessTargetLock>>>> =
    LazyLock::new(|| Mutex::new(BTreeMap::new()));

impl BranchRestoreReservation {
    pub(super) fn acquire(target: &Path) -> Result<Self, EngineOpenError> {
        let parent = target.parent().ok_or(EngineOpenError::InvalidConfig)?;
        let name = target.file_name().ok_or(EngineOpenError::InvalidConfig)?;
        let lock_path = parent.join(format!(".{}.branch-restore.lock", name.to_string_lossy()));
        let process_lock = PROCESS_TARGET_LOCKS
            .lock()
            .map_err(|_| EngineOpenError::StorageUnavailable)?
            .entry(lock_path.clone())
            .or_insert_with(|| {
                Arc::new(ProcessTargetLock { held: Mutex::new(false), wake: Condvar::new() })
            })
            .clone();
        let process_guard = process_lock.acquire();
        #[cfg(unix)]
        {
            use std::os::unix::fs::OpenOptionsExt;
            let file = fs::OpenOptions::new()
                .create(true)
                .read(true)
                .write(true)
                .mode(0o600)
                .open(&lock_path)
                .map_err(|_| EngineOpenError::StorageUnavailable)?;
            rustix::fs::flock(&file, rustix::fs::FlockOperation::LockExclusive)
                .map_err(|_| EngineOpenError::StorageUnavailable)?;
            return Ok(Self { file, _process_guard: process_guard });
        }
        #[cfg(not(unix))]
        {
            let _ = (lock_path, process_guard);
            Err(EngineOpenError::StorageUnavailable)
        }
    }
}

#[cfg(unix)]
impl Drop for BranchRestoreReservation {
    fn drop(&mut self) {
        let _ = rustix::fs::flock(&self.file, rustix::fs::FlockOperation::Unlock);
    }
}

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
        let source_tenant = TenantId::try_new(source_owner.tenant().to_text().as_str())
            .map_err(|_| EngineOpenError::InvalidConfig)?;
        let source_scope = source.scope_for(&source_tenant);
        let source_placement = source
            .usage_placement(&source_scope)
            .map_err(|_| EngineOpenError::StorageUnavailable)?
            .ok_or(EngineOpenError::InvalidConfig)?;
        let child_placement = UsagePlacement::try_new(
            source_placement.scope.tenant_id().clone(),
            source_placement.scope.project_id().as_str(),
            child.database().to_text().as_str(),
            source_placement.scope.partition_id().as_str(),
            *source_placement.scope.region_id(),
            source_placement.selected_regions.clone(),
            &source_placement.revision,
        )
        .map_err(|_| EngineOpenError::InvalidConfig)?;
        let target = &config.location;
        let _restore_reservation = BranchRestoreReservation::acquire(target)?;
        if let Ok(metadata) = fs::symlink_metadata(target)
            && (!metadata.is_dir() || metadata.file_type().is_symlink())
        {
            return Err(EngineOpenError::InvalidConfig);
        }
        let marker =
            format!("{}:{}:{}", operation.as_str(), source_owner.database(), child.database());
        if fs::read_dir(target).ok().and_then(|entries| entries.into_iter().next()).is_some() {
            return read_installed_branch_cut(config, target, &marker);
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
        builder.create(&staging).map_err(|error| {
            EngineOpenError::StorageUnavailable
        })?;
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
                &child_placement,
            )?;
            let mut staged = config.clone();
            staged.location.clone_from(&staging);
            drop(Self::open(staged)?);
            drop(registration);
            fs::File::open(&staging)
                .and_then(|directory| directory.sync_all())
                .map_err(|_| EngineOpenError::StorageUnavailable)?;
            if let Err(error) = fs::rename(&staging, target) {
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

fn read_installed_branch_cut(
    config: &EngineConfig,
    target: &Path,
    marker: &str,
) -> Result<StorageSequence, EngineOpenError> {
    let engine = Engine::open(config.clone())?;
    let connection =
        engine.open_sqlite_read_connection().map_err(|_| EngineOpenError::StorageUnavailable)?;
    let found: String = connection
        .query_row("SELECT value FROM meta WHERE key='branch_restore_operation'", [], |row| {
            row.get(0)
        })
        .map_err(|_| EngineOpenError::InvalidConfig)?;
    if found != marker {
        return Err(EngineOpenError::InvalidConfig);
    }
    let sequence: i64 = connection
        .query_row("SELECT value FROM meta WHERE key='branch_restore_cut'", [], |row| row.get(0))
        .map_err(|_| EngineOpenError::FormatMismatch)?;
    sync_parent(target)?;
    Ok(StorageSequence::new(u64::try_from(sequence).map_err(|_| EngineOpenError::FormatMismatch)?))
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
    placement: &UsagePlacement,
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
    let selected_regions = placement
        .selected_regions
        .iter()
        .map(crate::regions::RegionCode::as_str)
        .collect::<Vec<_>>()
        .join(",");
    transaction
        .execute(
            "INSERT INTO usage_placement(singleton,tenant,project,database,partition,region,selected_regions,revision) VALUES(1,?1,?2,?3,?4,?5,?6,?7)
             ON CONFLICT(singleton) DO UPDATE SET tenant=excluded.tenant, project=excluded.project,
             database=excluded.database, partition=excluded.partition, region=excluded.region,
             selected_regions=excluded.selected_regions, revision=excluded.revision",
            rusqlite::params![
                placement.scope.tenant_id().as_str(),
                placement.scope.project_id().as_str(),
                placement.scope.database_id().as_str(),
                placement.scope.partition_id().as_str(),
                placement.scope.region_id().as_str(),
                selected_regions,
                placement.revision,
            ],
        )
        .map_err(|_| EngineOpenError::StorageUnavailable)?;
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
    connection.execute_batch("PRAGMA wal_checkpoint(TRUNCATE)")
        .map_err(|_| EngineOpenError::StorageUnavailable)?;
    Ok(cut)
}
