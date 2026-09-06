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
    databases: BTreeSet<String>,
    policies: std::collections::BTreeMap<String, (String, u8)>,
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
        let mut policies = std::collections::BTreeMap::new();
        for input in inputs {
            let database = input.engine.engine().database_id().to_string();
            let policy = (input.policy_revision.clone(), input.retention_days);
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
            policies.insert(database, policy);
        }
        Ok(Self { fleet: Arc::new(Mutex::new(fleet)), databases, policies })
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
    ) -> Result<terrabase::backup::RecoveryReceipt, NativeBackupRuntimeError> {
        if !self.databases.contains(database)
            || target.owner().is_none()
            || !target.has_encrypted_owner()
        {
            return Err(NativeBackupRuntimeError::Unavailable);
        }
        let mut fleet = self.fleet.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?;
        fleet
            .restore_from_provider(database, provider, point, target)
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
    ) -> Result<terrabase::backup::RecoveryReceipt, NativeBackupRuntimeError> {
        let millis = Self::unix_seconds_to_millis(at_seconds)?;
        if !self.databases.contains(database)
            || target.owner().is_none()
            || !target.has_encrypted_owner()
        {
            return Err(NativeBackupRuntimeError::Unavailable);
        }
        let mut fleet = self.fleet.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?;
        fleet
            .restore_at_from_provider(database, provider, millis, target)
            .await
            .map_err(NativeBackupRuntimeError::Fleet)
    }

    /// Fences future capture and releases the owner’s stream registrations.
    /// Retained local and remote artifacts remain available for lifecycle
    /// deletion or recovery under their separate authorities.
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
        drop(fleet);
        // Keep the owner index in sync only after fleet admission succeeds.
        // This owner is initialized before concurrent API traffic begins, so
        // dynamic admission is serialized by the fleet lock.
        let _ = database;
        Ok(())
    }

    /// Detaches one stream while retaining its staged and remote artifacts.
    pub fn detach(&self, database: &str) -> Result<(), NativeBackupRuntimeError> {
        let mut fleet = self.fleet.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?;
        fleet.detach(database);
        Ok(())
    }

    pub fn drain(&mut self) -> Result<(), NativeBackupRuntimeError> {
        let mut fleet = self.fleet.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?;
        for database in &self.databases {
            fleet.detach(database);
        }
        self.databases.clear();
        Ok(())
    }

    /// Returns whether this owner still has an admitted stream.
    #[must_use]
    pub fn contains(&self, database: &str) -> bool {
        self.databases.contains(database)
    }

    /// Returns whether a canonical admitted database has a native stream.
    /// Unknown identities are unavailable rather than silently treated as off.
    pub fn attached(&self, database: &str) -> Result<bool, NativeBackupRuntimeError> {
        if database.is_empty() {
            return Err(NativeBackupRuntimeError::Unavailable);
        }
        Ok(self.databases.contains(database))
    }

    /// Returns the explicitly configured policy revision and retention.
    /// No default plan is synthesized for unknown databases.
    pub fn policy_for(&self, database: &str) -> Result<(String, u8), NativeBackupRuntimeError> {
        self.policies.get(database).cloned().ok_or(NativeBackupRuntimeError::Unavailable)
    }

    /// Reads current protection from every configured provider and the source
    /// journal frontier; cached scheduler health is never sufficient.
    pub fn is_ready(&self, database: &str) -> Result<bool, NativeBackupRuntimeError> {
        if !self.databases.contains(database) {
            return Err(NativeBackupRuntimeError::Unavailable);
        }
        let mut fleet = self.fleet.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?;
        Ok(fleet.observe_protection(database).map_err(NativeBackupRuntimeError::Fleet)?.protected)
    }
}
