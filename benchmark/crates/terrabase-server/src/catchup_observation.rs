//! Read-only delivery evidence joined from admitted engine and worker owners.

use std::collections::BTreeSet;
use std::sync::{Arc, Mutex};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use terrabase::backup::worker::NativeBackupFleet;
use terrabase_cluster::production::DatabaseRouteScope;
use terrabase_cluster::wal_driver::WalEngineHandle;
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
    pub engine: Arc<dyn WalEngineHandle>,
    /// Immutable residency selection from the accepted assignment.
    pub selected_regions: BTreeSet<String>,
    /// This host's authenticated Region.
    pub local_region: String,
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
    /// Composes observations from the running root's verified acceptance and leases.
    /// The caller supplies the actual attached backup scheduler owned by that root.
    ///
    /// # Errors
    /// Refuses incomplete accepted coverage or roles without a live engine owner.
    pub fn from_admission(
        admission: &crate::production_data_runtime::ProductionAdmissionRuntime,
        backups: Arc<Mutex<NativeBackupFleet>>,
    ) -> Result<Self, String> {
        use terrabase_cluster::wal_route::DurableWalAssignmentSource;
        let journal = admission.journal();
        let accepted = journal.accepted_proposal().map_err(|_| "accepted placement unavailable")?;
        let proposal = accepted.proposal.ok_or("accepted placement has no proposal")?;
        let runtime = admission.member_runtime();
        let transport = runtime.transport().config();
        let local = transport.local_identity();
        let local_region = transport.local_region().as_str().to_owned();
        let records = admission.admitted_records()?;
        let mut databases = Vec::with_capacity(records.len());
        for record in records {
            let scope = DatabaseRouteScope::try_new(
                &record.tenant_id,
                &record.project_id,
                &record.database_id,
            )
            .map_err(|_| "invalid admitted catchup scope")?;
            let assignment =
                journal.load_assignment(scope).map_err(|_| "accepted writer role unavailable")?;
            if assignment.writer != *local {
                return Err("follower catchup requires remote writer and backup observation".into());
            }
            let selected_regions: BTreeSet<_> = record.regions.into_iter().collect();
            let mut regions = BTreeSet::new();
            let mut peers = BTreeSet::new();
            for entry in &proposal.assignments {
                if entry.tenant == record.tenant_id
                    && entry.project == record.project_id
                    && entry.database == record.database_id
                {
                    if !regions.insert(entry.region.clone()) {
                        return Err("duplicate accepted catchup region".into());
                    }
                    if entry.region != local_region {
                        peers.insert(entry.writer_member.clone());
                    }
                }
            }
            if regions != selected_regions {
                return Err("accepted catchup residency coverage incomplete".into());
            }
            databases.push(CatchUpDatabase {
                scope,
                engine: Arc::new(
                    runtime
                        .acquire_replication(&scope)
                        .map_err(|_| "accepted writer lease unavailable")?,
                ),
                selected_regions,
                local_region: local_region.clone(),
                generation: assignment.generation,
                epoch: assignment.epoch,
                peers,
            });
        }
        Self::new(
            format!("{}:{}:{}", accepted.revision, proposal.generation, proposal.epoch),
            databases,
            admission.progress(),
            backups,
            admission.observations().ok_or("replication workers are not running")?,
        )
    }

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
            let owner = database.engine.engine().database_owner();
            if !scopes.insert(database.scope)
                || database.generation == 0
                || database.epoch == 0
                || owner.tenant() != database.scope.tenant()
                || owner.project() != database.scope.project()
                || owner.database() != database.scope.database()
                || database.peers.len() > 20
                || !database.selected_regions.contains(&database.local_region)
                || database.selected_regions.len() > 20
                || database.peers.len() + 1 != database.selected_regions.len()
                || owner.region().as_str() != database.local_region
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
                .engine()
                .published_sequence()
                .map_err(|_| "engine source unavailable")?
                .get();
            let boundary = &boundaries[&database.scope];
            let backup = backups
                .observe_protection(&database.engine.engine().database_id().to_string())
                .map_err(|_| "backup source unavailable")?;
            let causal_measured = database.peers.iter().all(|peer| {
                repairs.get(&(peer.clone(), database.scope)).is_some_and(|facts| {
                    facts.connected
                        && facts.region != database.local_region
                        && database.selected_regions.contains(&facts.region)
                        && facts
                            .repair_observed_at
                            .is_some_and(|observed| observed.elapsed() <= Duration::from_secs(30))
                        && facts.repair.as_ref().is_some_and(|proof| {
                            proof.scope() == database.scope
                                && proof.peer() == peer
                                && proof.replica_epoch() == database.epoch
                                && proof.placement_generation() == database.generation
                        })
                })
            });
            let measured_regions: BTreeSet<_> = database
                .peers
                .iter()
                .filter_map(|peer| {
                    repairs.get(&(peer.clone(), database.scope)).map(|facts| facts.region.clone())
                })
                .chain(std::iter::once(database.local_region.clone()))
                .collect();
            let causal_measured = causal_measured && measured_regions == database.selected_regions;
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
