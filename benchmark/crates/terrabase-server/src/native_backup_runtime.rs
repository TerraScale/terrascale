//! Production owner for the shared native backup fleet.
//!
//! The startup composition supplies already admitted engines, database-bound
//! key providers, independently configured object destinations, and placement
//! locks.  This module owns only fleet admission and lifecycle fencing.  It
//! intentionally has no default provider, key, filesystem, or subprocess
//! fallback.

use std::collections::BTreeSet;
use std::sync::{Arc, Mutex};

use terrabase::backup::object_store::Provider;
use terrabase::backup::worker::{
    BackupEngineHandle, BackupKeys, FleetError, FleetLimits, NativeBackupFleet, StreamConfig,
};
use terrabase::backup::{PlacementLock, object_store::ObjectDestination};
use terrabase::engine::Engine;
use terrabase_tbep::member_host::MemberHost;

/// Retains the exact admitted Member host for a backup stream.
///
/// The wrapper is deliberately local because `BackupEngineHandle` and
/// `MemberHost` are foreign types; extracting an `Engine` would lose the
/// lease owner that fences the public and replication planes together.
pub struct AdmittedBackupEngine {
    host: Arc<MemberHost>,
}

impl AdmittedBackupEngine {
    /// Binds a stream to an already retained admitted host.
    #[must_use]
    pub fn new(host: Arc<MemberHost>) -> Self {
        Self { host }
    }
}

impl BackupEngineHandle for AdmittedBackupEngine {
    fn engine(&self) -> &Engine {
        self.host.engine()
    }
}

/// All immutable dependencies required to admit one production stream.
///
/// The caller must resolve these values from the accepted database record and
/// its account policy.  In particular, an empty destination list is refused
/// by the native fleet rather than replaced with a local test destination.
pub struct NativeBackupInput {
    /// The retained admitted engine owner for this database.
    pub engine: Arc<dyn BackupEngineHandle>,
    /// Database-bound key source, including retained-version recovery.
    pub keys: Arc<dyn BackupKeys>,
    /// Every selected provider and its independently authenticated destination.
    pub destinations: Vec<(Provider, Arc<ObjectDestination>)>,
    /// Durable effective account-policy revision.
    pub policy_revision: String,
    /// PITR retention selected by the effective plan.
    pub retention_days: u8,
    /// Private bounded staging directory for this database.
    pub staging: std::path::PathBuf,
    /// Accepted immutable residency lock.
    pub placement: PlacementLock,
}

/// Errors from production fleet admission or lifecycle synchronization.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum NativeBackupRuntimeError {
    /// One input failed native stream admission.
    Fleet(FleetError),
    /// The owner was poisoned and cannot safely make a lifecycle decision.
    Poisoned,
    /// The requested canonical database is not admitted to this owner.
    Unavailable,
}

impl std::fmt::Display for NativeBackupRuntimeError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Fleet(error) => write!(formatter, "native backup fleet: {error:?}"),
            Self::Poisoned => formatter.write_str("native backup fleet lock poisoned"),
            Self::Unavailable => formatter.write_str("native backup database unavailable"),
        }
    }
}

impl std::error::Error for NativeBackupRuntimeError {}

/// Shared production owner for all admitted database backup streams.
pub struct NativeBackupRuntime {
    fleet: Arc<Mutex<NativeBackupFleet>>,
    databases: Mutex<BTreeSet<String>>,
}

impl NativeBackupRuntime {
    /// Creates and admits the complete accepted stream set.
    ///
    /// Admission is all-or-nothing from the caller's perspective: if any
    /// input is rejected, the partially admitted fleet is dropped and no
    /// runtime is returned.  The caller must retry after correcting the
    /// accepted assignment or provider/key state.
    pub fn new(
        limits: FleetLimits,
        inputs: impl IntoIterator<Item = NativeBackupInput>,
    ) -> Result<Self, NativeBackupRuntimeError> {
        let mut fleet = NativeBackupFleet::new(limits).map_err(NativeBackupRuntimeError::Fleet)?;
        let mut databases = BTreeSet::new();
        for input in inputs {
            let database = input.engine.engine().database_id().to_string();
            fleet
                .attach(StreamConfig {
                    engine: input.engine,
                    keys: input.keys,
                    destinations: input.destinations,
                    policy_revision: input.policy_revision,
                    retention_days: input.retention_days,
                    staging: input.staging,
                    placement: input.placement,
                })
                .map_err(NativeBackupRuntimeError::Fleet)?;
            databases.insert(database.clone());
        }
        Ok(Self { fleet: Arc::new(Mutex::new(fleet)), databases: Mutex::new(databases) })
    }

    /// Returns the shared fleet for observations and native recovery lanes.
    #[must_use]
    pub fn fleet(&self) -> Arc<Mutex<NativeBackupFleet>> {
        Arc::clone(&self.fleet)
    }

    /// Executes one bounded asynchronous fleet tick.
    pub async fn tick(&self) -> Result<Vec<(String, FleetError)>, NativeBackupRuntimeError> {
        let mut fleet = self.fleet.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?;
        Ok(fleet.sync_once().await)
    }

    /// Captures one operation-bound point and verifies its complete chain.
    pub async fn capture(
        &self,
        database: &str,
        operation_id: &str,
    ) -> Result<terrabase::backup::BackupManifest, NativeBackupRuntimeError> {
        validate_operation_id(operation_id)?;
        let mut fleet = self.fleet.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?;
        fleet.capture_snapshot(database).await.map_err(NativeBackupRuntimeError::Fleet)
    }

    /// Returns the authenticated retained chain for one admitted database.
    pub fn retained_points(
        &self,
        database: &str,
    ) -> Result<Vec<terrabase::backup::BackupManifest>, NativeBackupRuntimeError> {
        let fleet = self.fleet.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?;
        fleet.retained_points(database).map_err(NativeBackupRuntimeError::Fleet)
    }

    /// Converts a public Unix-second timestamp to the native millisecond unit.
    pub fn unix_seconds_to_millis(seconds: u64) -> Result<u64, NativeBackupRuntimeError> {
        seconds.checked_mul(1_000).ok_or(NativeBackupRuntimeError::Unavailable)
    }

    /// Restores a selected encrypted point into a caller-validated new target.
    pub async fn restore_from_provider(
        &self,
        database: &str,
        provider: Provider,
        point: Option<&str>,
        target: &terrabase::engine::EngineConfig,
        operation_id: &str,
        placement: &PlacementLock,
        identity: &terrabase::backup::RestoreIdentity,
    ) -> Result<terrabase::backup::RecoveryReceipt, NativeBackupRuntimeError> {
        validate_operation_id(operation_id)?;
        if !self
            .databases
            .lock()
            .map_err(|_| NativeBackupRuntimeError::Poisoned)?
            .contains(database)
        {
            return Err(NativeBackupRuntimeError::Unavailable);
        }
        let mut fleet = self.fleet.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?;
        fleet
            .restore_from_provider(database, provider, point, target, identity)
            .await
            .map_err(NativeBackupRuntimeError::Fleet)
    }

    /// Restores through an inclusive Unix-second timestamp using one provider.
    pub async fn restore_at_seconds(
        &self,
        database: &str,
        provider: Provider,
        at_seconds: u64,
        target: &terrabase::engine::EngineConfig,
        operation_id: &str,
        placement: &PlacementLock,
        identity: &terrabase::backup::RestoreIdentity,
    ) -> Result<terrabase::backup::RecoveryReceipt, NativeBackupRuntimeError> {
        validate_operation_id(operation_id)?;
        let millis = Self::unix_seconds_to_millis(at_seconds)?;
        if !self
            .databases
            .lock()
            .map_err(|_| NativeBackupRuntimeError::Poisoned)?
            .contains(database)
        {
            return Err(NativeBackupRuntimeError::Unavailable);
        }
        let mut fleet = self.fleet.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?;
        fleet
            .restore_at_from_provider(database, provider, millis, target, identity)
            .await
            .map_err(NativeBackupRuntimeError::Fleet)
    }

    /// Restores through an inclusive Unix-millisecond timestamp without rounding.
    pub async fn restore_at_millis(
        &self,
        database: &str,
        provider: Provider,
        at_millis: u64,
        target: &terrabase::engine::EngineConfig,
        operation_id: &str,
        placement: &PlacementLock,
        identity: &terrabase::backup::RestoreIdentity,
    ) -> Result<terrabase::backup::RecoveryReceipt, NativeBackupRuntimeError> {
        validate_operation_id(operation_id)?;
        if !self
            .databases
            .lock()
            .map_err(|_| NativeBackupRuntimeError::Poisoned)?
            .contains(database)
        {
            return Err(NativeBackupRuntimeError::Unavailable);
        }
        let mut fleet = self.fleet.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?;
        fleet
            .restore_at_from_provider(database, provider, at_millis, target, identity)
            .await
            .map_err(NativeBackupRuntimeError::Fleet)
    }

    /// Dynamically admits one accepted stream under the shared fleet lock.
    pub fn attach(&self, input: NativeBackupInput) -> Result<(), NativeBackupRuntimeError> {
        let database = input.engine.engine().database_id().to_string();
        let mut fleet = self.fleet.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?;
        fleet
            .attach(StreamConfig {
                engine: input.engine,
                keys: input.keys,
                destinations: input.destinations,
                policy_revision: input.policy_revision,
                retention_days: input.retention_days,
                staging: input.staging,
                placement: input.placement,
            })
            .map_err(NativeBackupRuntimeError::Fleet)?;
        self.databases.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?.insert(database);
        Ok(())
    }

    /// Releases target engine references under the serialized native fleet lock.
    pub fn suspend_for_restore(&self, database: &str) -> Result<(), NativeBackupRuntimeError> {
        self.fleet
            .lock()
            .map_err(|_| NativeBackupRuntimeError::Poisoned)?
            .suspend_for_restore(database)
            .map_err(NativeBackupRuntimeError::Fleet)
    }

    /// Restores the same backup policy with the reopened admitted target engine.
    pub fn resume_after_restore(
        &self,
        database: &str,
        host: Arc<MemberHost>,
        resume_capture: bool,
    ) -> Result<(), NativeBackupRuntimeError> {
        self.fleet
            .lock()
            .map_err(|_| NativeBackupRuntimeError::Poisoned)?
            .resume_after_restore(
                database,
                Arc::new(AdmittedBackupEngine::new(host)),
                resume_capture,
            )
            .map_err(NativeBackupRuntimeError::Fleet)
    }

    /// Detaches one stream while retaining its staged and remote artifacts.
    pub fn detach(&self, database: &str) -> Result<(), NativeBackupRuntimeError> {
        let mut fleet = self.fleet.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?;
        fleet.detach(database);
        self.databases.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?.remove(database);
        Ok(())
    }

    /// Fences all admitted streams and releases their scheduler registrations.
    ///
    /// # Errors
    /// Returns an error if the runtime state mutex is poisoned.
    pub fn drain(&self) -> Result<(), NativeBackupRuntimeError> {
        let mut fleet = self.fleet.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?;
        let databases =
            self.databases.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?.clone();
        for database in &databases {
            fleet.detach(database);
        }
        self.databases.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?.clear();
        Ok(())
    }

    /// Returns whether this owner still has an admitted stream.
    #[must_use]
    pub fn contains(&self, database: &str) -> bool {
        self.databases.lock().map(|databases| databases.contains(database)).unwrap_or(false)
    }

    /// Returns whether a canonical admitted database has a native stream.
    /// Unknown identities are unavailable rather than silently treated as off.
    pub fn attached(&self, database: &str) -> Result<bool, NativeBackupRuntimeError> {
        if database.is_empty() {
            return Err(NativeBackupRuntimeError::Unavailable);
        }
        Ok(self
            .databases
            .lock()
            .map_err(|_| NativeBackupRuntimeError::Poisoned)?
            .contains(database))
    }

    /// Reads current protection from every configured provider and the source
    /// journal frontier; cached scheduler health is never sufficient.
    pub fn is_ready(&self, database: &str) -> Result<bool, NativeBackupRuntimeError> {
        if !self
            .databases
            .lock()
            .map_err(|_| NativeBackupRuntimeError::Poisoned)?
            .contains(database)
        {
            return Err(NativeBackupRuntimeError::Unavailable);
        }
        let fleet = self.fleet.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?;
        Ok(fleet.observe_protection(database).map_err(NativeBackupRuntimeError::Fleet)?.protected)
    }
}

fn validate_operation_id(operation_id: &str) -> Result<(), NativeBackupRuntimeError> {
    if operation_id.is_empty()
        || operation_id.len() > 256
        || operation_id.trim() != operation_id
        || operation_id.chars().any(char::is_control)
    {
        return Err(NativeBackupRuntimeError::Unavailable);
    }
    Ok(())
}

#[cfg(test)]
mod restore_tests {
    use super::*;
    #[test]
    fn restore_suspension_releases_real_admitted_host_and_rebinds_after_cancellation() {
        let (registry, member, records, root) = crate::admitted_databases::tests::fixture();
        let registry = Arc::new(registry);
        let record = &records[0];
        let scope = terrabase::member::DatabaseScope::new(
            &record.environment,
            record.tenant_id.parse().unwrap(),
            record.project_id.parse().unwrap(),
            record.database_id.parse().unwrap(),
            record.region.parse().unwrap(),
        )
        .unwrap();
        let target = registry
            .retained_snapshot()
            .unwrap()
            .into_iter()
            .find(|entry| entry.database.record.database_id == record.database_id)
            .unwrap();
        let backups = NativeBackupRuntime::new(
            FleetLimits {
                streams: 2,
                staging_bytes_per_stream: 4 * 1024 * 1024,
                points_per_stream: 4,
            },
            [NativeBackupInput {
                engine: Arc::new(AdmittedBackupEngine::new(Arc::clone(&target.database.host))),
                keys: Arc::new(
                    terrabase::backup::worker::OciBackupKeys::new(crate::test_keys::backup_keys(
                        &scope,
                    ))
                    .unwrap(),
                ),
                destinations: vec![(
                    terrabase::backup::object_store::Provider::Oci,
                    Arc::new(terrabase::backup::object_store::ObjectDestination::for_test(
                        terrabase::backup::object_store::Provider::Oci,
                        Arc::new(object_store::memory::InMemory::new()),
                        4 * 1024 * 1024,
                        8 * 1024 * 1024,
                    )),
                )],
                policy_revision: "restore-policy".into(),
                retention_days: 1,
                staging: root.join("backup-target"),
                placement: PlacementLock::explicit([record.region.parse().unwrap()]).unwrap(),
            }],
        )
        .unwrap();
        drop(target);
        registry.pause(&record.database_id, "pause-for-restore").unwrap();
        assert!(
            registry.reserve_restore(&record.database_id).is_err(),
            "live backup owns target engine"
        );
        backups.suspend_for_restore(&record.database_id).unwrap();
        let reservation = registry
            .reserve_restore(&record.database_id)
            .unwrap()
            .rollback_on_drop(member.clone(), Arc::clone(&registry));
        member
            .close_database(&terrabase::member::DatabaseId::try_new(&record.database_id).unwrap())
            .unwrap();
        drop(reservation);
        let reopened = registry
            .retained_snapshot()
            .unwrap()
            .into_iter()
            .find(|entry| entry.database.record.database_id == record.database_id)
            .unwrap();
        backups
            .resume_after_restore(&record.database_id, Arc::clone(&reopened.database.host), false)
            .unwrap();
        drop(reopened);
        assert!(
            registry.reserve_restore(&record.database_id).is_err(),
            "rebound backup owns reopened engine"
        );
        backups.suspend_for_restore(&record.database_id).unwrap();
        drop(
            registry
                .reserve_restore(&record.database_id)
                .unwrap()
                .rollback_on_drop(member.clone(), Arc::clone(&registry)),
        );
        drop(backups);
        drop(registry);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }
}
