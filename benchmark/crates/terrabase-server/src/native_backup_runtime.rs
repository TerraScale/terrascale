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
}

impl std::fmt::Display for NativeBackupRuntimeError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Fleet(error) => write!(formatter, "native backup fleet: {error:?}"),
            Self::Poisoned => formatter.write_str("native backup fleet lock poisoned"),
        }
    }
}

impl std::error::Error for NativeBackupRuntimeError {}

/// Shared production owner for all admitted database backup streams.
pub struct NativeBackupRuntime {
    fleet: Arc<Mutex<NativeBackupFleet>>,
    databases: BTreeSet<String>,
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
            databases.insert(database);
        }
        Ok(Self { fleet: Arc::new(Mutex::new(fleet)), databases })
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

    /// Fences future capture and releases the owner’s stream registrations.
    /// Retained local and remote artifacts remain available for lifecycle
    /// deletion or recovery under their separate authorities.
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
}
