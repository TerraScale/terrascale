//! Read-only delivery evidence joined from admitted engine and worker owners.

use std::collections::BTreeSet;
use std::sync::{Arc, Mutex};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use terrabase::backup::worker::NativeBackupFleet;
use terrabase::engine::Engine;
use terrabase_cluster::production::DatabaseRouteScope;
use terrabase_tbep::catchup::CatchUpSource;
use terrabase_tbep::proto::terrabase::v1::{
    DatabaseCatchUpStatus, MaintenanceServiceCatchUpStatusResponse,
};

use crate::replication_runtime::ReplicationProgress;

use crate::replication_peers::PeerObservations;

/// One immutable database assignment held by the admitted runtime.
pub struct CatchUpDatabase {
    /// Exact tenant/project/database route scope.
    pub scope: DatabaseRouteScope,
    /// Engine bound to this same accepted scope.
    pub engine: Arc<Engine>,
    /// Fenced accepted placement generation.
    pub generation: u64,
    /// Fenced accepted replica-set epoch.
    pub epoch: u64,
    /// Exact required cross-region source peers, excluding self.
    pub peers: BTreeSet<String>,
}

/// Owns a complete immutable scope set and shares the actual running workers.
pub struct RuntimeCatchUpSource {
    revision: String,
    databases: Vec<CatchUpDatabase>,
    progress: Arc<ReplicationProgress>,
    backups: Arc<Mutex<NativeBackupFleet>>,
    repairs: PeerObservations,
}

impl RuntimeCatchUpSource {
    /// Binds the complete accepted scope set, never a request-selected subset.
    ///
    /// # Errors
    /// Refuses empty, duplicate, oversized, or mismatched engine assignments.
    pub fn new(
        revision: String,
        databases: Vec<CatchUpDatabase>,
        progress: Arc<ReplicationProgress>,
        backups: Arc<Mutex<NativeBackupFleet>>,
        repairs: PeerObservations,
    ) -> Result<Self, String> {
        if revision.is_empty()
            || revision.len() > 256
            || revision.chars().any(char::is_control)
            || databases.is_empty()
            || databases.len() > terrabase::member::MAX_DATABASES
        {
            return Err("invalid catchup assignment".into());
        }
        let mut scopes = BTreeSet::new();
        for database in &databases {
            let owner = database.engine.database_owner();
            if !scopes.insert(database.scope)
                || database.generation == 0
                || database.epoch == 0
                || owner.tenant() != database.scope.tenant()
                || owner.project() != database.scope.project()
                || owner.database() != database.scope.database()
                || database.peers.len() > 20
            {
                return Err("catchup engine differs from accepted assignment".into());
            }
        }
        Ok(Self { revision, databases, progress, backups, repairs })
    }
}

impl CatchUpSource for RuntimeCatchUpSource {
    fn observe(&self) -> Result<MaintenanceServiceCatchUpStatusResponse, String> {
        let boundaries =
            self.progress.active_boundaries().map_err(|_| "replication evidence unavailable")?;
        // Fenced, added, or removed scopes cannot be hidden by filtering a map.
        if boundaries.len() != self.databases.len()
            || self.databases.iter().any(|database| !boundaries.contains_key(&database.scope))
        {
            return Ok(MaintenanceServiceCatchUpStatusResponse::default());
        }
        let backups = self.backups.try_lock().map_err(|_| "backup worker busy")?;
        let repairs = self.repairs.try_lock().map_err(|_| "repair observations busy")?;
        let mut databases = Vec::with_capacity(self.databases.len());
        for database in &self.databases {
            let source = database
                .engine
                .published_sequence()
                .map_err(|_| "engine source unavailable")?
                .get();
            let boundary = &boundaries[&database.scope];
            let backup = backups
                .observe_protection(&database.engine.database_id().to_string())
                .map_err(|_| "backup source unavailable")?;
            let causal_measured = !database.peers.is_empty()
                && database.peers.iter().all(|peer| {
                    repairs.get(&(peer.clone(), database.scope)).is_some_and(|facts| {
                        facts.connected
                            && facts.repair_observed_at.is_some_and(|observed| {
                                observed.elapsed() <= Duration::from_secs(30)
                            })
                            && facts.repair.as_ref().is_some_and(|proof| {
                                proof.scope() == database.scope
                                    && proof.peer() == peer
                                    && proof.replica_epoch() == database.epoch
                                    && proof.placement_generation() == database.generation
                            })
                    })
                });
            let causal_caught_up = causal_measured
                && database.peers.iter().all(|peer| {
                    let Some(proof) = &repairs[&(peer.clone(), database.scope)].repair else {
                        return false;
                    };
                    proof.local_sequence() >= source
                        && boundary.regional_observed
                        && boundary.regional_sequence >= proof.local_sequence()
                });
            databases.push(DatabaseCatchUpStatus {
                tenant: database.scope.tenant().to_string(),
                project: database.scope.project().to_string(),
                database: database.scope.database().to_string(),
                source_sequence: source,
                regional_sequence: boundary.regional_sequence,
                regional_measured: boundary.regional_observed,
                causal_measured,
                causal_caught_up,
                backup_source_sequence: backup.source_sequence,
                backup_protected_sequence: backup.protected_sequence,
                backup_measured: !backup.providers.is_empty(),
                backup_caught_up: backup.protected,
            });
        }
        Ok(MaintenanceServiceCatchUpStatusResponse {
            complete: true,
            assignment_revision: self.revision.clone(),
            observed_at_unix_ms: u64::try_from(
                SystemTime::now()
                    .duration_since(UNIX_EPOCH)
                    .map_err(|_| "clock unavailable")?
                    .as_millis(),
            )
            .map_err(|_| "clock overflow")?,
            databases,
        })
    }
}
