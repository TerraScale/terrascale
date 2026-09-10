//! Accepted database assignments assembled into the live, lease-owning data plane.
use crate::production_readiness::ReplicationReadinessFacts;
use crate::replication_peers::{
    PeerDatabase, PeerDatabaseBatch, PhysicalDriver, ReplicationPeers, SnapshotReceiver,
};
use crate::replication_runtime::ReplicationProgress;
use std::collections::{BTreeMap, BTreeSet};
use std::path::PathBuf;
use std::sync::OnceLock;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use terrabase::backup::worker::FleetLimits;
use terrabase::encryption::{
    CustomerKmsDatabaseKeys, CustomerKmsKeyDelivery, DatabaseKeySource, DatabaseKeyVersion,
    KeyPurpose, KeyScope, KeyVersionMetadata, OciVaultDatabaseKeys, OciVaultKeyDelivery,
};
use terrabase_cluster::production::{DatabaseRouteScope, ProductionMemberRuntime};
use terrabase_cluster::wal_driver::{WalEngineHandle, WalWriterDriver};
use terrabase_cluster::wal_follower::WalTail;
use terrabase_cluster::wal_route::{DurableWalAssignmentSource, WalAssignment};
use terrabase_cluster::wal_snapshot::WalSnapshotSource;
use terrabase_tbep::production_causal::CausalDriver;
use terrabase_tbep::registry::BoundScope;

pub(crate) mod host_readiness;
mod public_server;
mod readiness_inventory;
mod replication_retirement;
mod transition_retirement;
#[cfg(test)]
mod transition_retirement_tests;

pub use host_readiness::WriterHostReadiness;
pub use public_server::{PreparedProductionPublic, PublicServingStatus};
pub use replication_retirement::{RetiredReplicationFrontiers, RetiredWriterFrontier};
pub use transition_retirement::TransitionWriterFences;

/// Filesystem and residency bounds of an authenticated admission record.
/// Writer identity is deliberately obtained from the durable assignment source.
pub struct ProductionDatabaseAssignment {
    /// Exact immutable owner scope.
    pub scope: DatabaseRouteScope,
    /// Exclusive physical database directory under the Member root.
    pub location: PathBuf,
    /// Fresh snapshot directory whose parent already exists.
    pub snapshot_staging: PathBuf,
    /// Maximum allowed snapshot bytes.
    pub snapshot_max_bytes: u64,
    /// OCI delivery source bound to this exact owner and key version.
    pub keys: Arc<dyn DatabaseKeySource>,
    /// Exact accepted residency selection.
    pub selected_regions: BTreeSet<String>,
    /// Accepted remote regional writer identities, keyed by Region.
    pub remote_writers: BTreeMap<String, String>,
}

/// Owns the running replication workers and their retained Member leases.
pub struct ProductionDataRuntime {
    runtime: std::sync::Weak<ProductionMemberRuntime>,
    peers: ReplicationPeers,
    progress: Arc<ReplicationProgress>,
}

/// Shared owner for the one process-wide accepted replication runtime.
pub struct ProductionDataLifecycle {
    data: OnceLock<ProductionDataRuntime>,
    startup: Arc<tokio::sync::Mutex<()>>,
    retirement: tokio::sync::Mutex<replication_retirement::RetirementState>,
    retired_frontiers: OnceLock<Arc<RetiredReplicationFrontiers>>,
    draining: AtomicBool,
    progress: Arc<ReplicationProgress>,
    registry: Arc<crate::admitted_databases::AdmittedDatabaseRegistry>,
    expected:
        Mutex<Option<BTreeMap<DatabaseRouteScope, crate::production_readiness::ExpectedScope>>>,
}

impl ProductionDataLifecycle {
    /// Creates an empty lifecycle owner for one shared progress ledger.
    #[must_use]
    pub fn new(
        progress: Arc<ReplicationProgress>,
        registry: Arc<crate::admitted_databases::AdmittedDatabaseRegistry>,
    ) -> Self {
        Self {
            data: OnceLock::new(),
            startup: Arc::new(tokio::sync::Mutex::new(())),
            retirement: tokio::sync::Mutex::new(replication_retirement::RetirementState::default()),
            retired_frontiers: OnceLock::new(),
            draining: AtomicBool::new(false),
            progress,
            registry,
            expected: Mutex::new(None),
        }
    }

    /// Durably pauses one admitted database while retaining its identity and data.
    pub fn pause_database(
        &self,
        database: &str,
        operation_id: &str,
    ) -> Result<terrabase::engine::lifecycle_state::LifecycleOperationOutcome, String> {
        if self.is_draining() {
            return Err("production data lifecycle is draining".into());
        }
        self.registry.pause(database, operation_id)
    }

    /// Resumes one previously paused admitted database using its retained identity.
    pub fn reactivate_database(
        &self,
        database: &str,
        operation_id: &str,
    ) -> Result<terrabase::engine::lifecycle_state::LifecycleOperationOutcome, String> {
        if self.is_draining() {
            return Err("production data lifecycle is draining".into());
        }
        self.registry.resume(database, operation_id)
    }

    /// Returns the installed runtime without transferring ownership.
    #[must_use]
    pub fn get(&self) -> Option<&ProductionDataRuntime> {
        self.data.get()
    }

    /// Returns whether startup or serving has been fenced.
    #[must_use]
    pub fn is_draining(&self) -> bool {
        self.draining.load(Ordering::Acquire)
    }

    /// Evaluates every accepted database lane using current durable receipts.
    /// Missing inventory, failed workers, or draining never report readiness.
    #[must_use]
    pub fn replication_readiness(&self) -> ReplicationReadinessFacts {
        if self.is_draining() {
            return ReplicationReadinessFacts::default();
        }
        let Some(data) = self.get() else {
            return ReplicationReadinessFacts::default();
        };
        if data.ensure_running().is_err() {
            return ReplicationReadinessFacts::default();
        }
        let Ok(expected) = self.expected.lock().map(|expected| expected.clone()) else {
            return ReplicationReadinessFacts::default();
        };
        let Ok(boundaries) = self.progress.active_boundaries() else {
            return ReplicationReadinessFacts::default();
        };
        let facts = crate::production_readiness::evaluate(
            expected.as_ref(),
            &data.observations(),
            &boundaries,
        );
        if self.is_draining() || data.ensure_running().is_err() {
            ReplicationReadinessFacts::default()
        } else {
            facts
        }
    }

    /// Checks activated writer hosts against the accepted assignment inventory.
    /// Uses retained leases without reentering the public readiness callback.
    /// Physical follower storage is qualified separately from writer journals.
    /// This performs journal and key-lease I/O; async callers should use a blocking worker.
    #[must_use]
    pub fn writer_host_readiness(&self) -> WriterHostReadiness {
        let Some(data) = self.get() else {
            return WriterHostReadiness::default();
        };
        if self.is_draining() || data.ensure_running().is_err() {
            return WriterHostReadiness::default();
        }
        let Ok(Some(expected)) = self.expected.lock().map(|expected| expected.clone()) else {
            return WriterHostReadiness::default();
        };
        let Ok(snapshot) = self.registry.retained_snapshot() else {
            return WriterHostReadiness::default();
        };
        let facts = host_readiness::evaluate_writer_hosts(&expected, &snapshot);
        if self.is_draining() || data.ensure_running().is_err() {
            WriterHostReadiness::default()
        } else {
            facts
        }
    }

    /// Validates storage authorization across every activated retained host.
    /// Followers are included even though writer readiness has a separate role.
    #[must_use]
    pub fn all_hosts_encrypted(&self) -> bool {
        let Ok(snapshots) = self.registry.retained_snapshot() else {
            return false;
        };
        host_readiness::all_activated_hosts_encrypted(&snapshots)
    }

    /// Reports whether the live worker and progress ledger can persist a drain frontier.
    #[must_use]
    pub fn frontiers_persistable(&self) -> bool {
        self.data
            .get()
            .is_some_and(|data| data.ensure_running().is_ok() && self.progress.boundaries().is_ok())
    }

    fn record_inventory(
        &self,
        inventory: Result<
            BTreeMap<DatabaseRouteScope, crate::production_readiness::ExpectedScope>,
            readiness_inventory::InventoryError,
        >,
    ) -> Result<(), String> {
        let mut expected = self.expected.lock().map_err(|_| "readiness inventory unavailable")?;
        match inventory {
            Ok(inventory) => {
                *expected = Some(inventory);
                Ok(())
            }
            Err(readiness_inventory::InventoryError::Unavailable) => {
                Err("accepted inventory temporarily unavailable".into())
            }
            Err(readiness_inventory::InventoryError::Invalid) => {
                *expected = None;
                Err("accepted inventory is invalid".into())
            }
        }
    }

    /// Fences new progress and lane startup without consuming the owner.
    ///
    /// # Errors
    /// Reports unavailable registry fencing state after signaling workers.
    pub fn begin_drain(&self) -> Result<(), String> {
        let admission = self.fence_admission();
        if let Some(data) = self.data.get() {
            data.begin_drain();
        }
        admission
    }

    fn fence_admission(&self) -> Result<(), String> {
        self.draining.store(true, Ordering::Release);
        self.progress.fence();
        self.registry.begin_drain()
    }

    /// Starts the one runtime, serializing concurrent startup attempts.
    ///
    /// # Errors
    /// Refuses a fenced lifecycle or failed worker startup.
    pub async fn start(
        &self,
        runtime: Arc<ProductionMemberRuntime>,
        source: &dyn DurableWalAssignmentSource,
        databases: Vec<ProductionDatabaseAssignment>,
        dispatcher: Arc<dyn crate::replication_peers::PeerControlDispatcher>,
    ) -> Result<(), String> {
        if self.is_draining() {
            return Err("production data lifecycle is draining".into());
        }
        let _startup = self.startup.lock().await;
        if self.is_draining() {
            return Err("production data lifecycle is draining".into());
        }
        if self.data.get().is_some() {
            return Err("production data lifecycle is already initialized".into());
        }
        let data = ProductionDataRuntime::start(
            runtime,
            self.progress.clone(),
            source,
            databases,
            dispatcher,
        )
        .await?;
        if let Err(data) = self.data.set(data) {
            let _ = self.begin_drain();
            data.begin_drain();
            let _ = data.drain().await;
            return Err("production data lifecycle was initialized twice".into());
        }
        if self.is_draining() {
            let _ = self.begin_drain();
            return Err("production data lifecycle began draining during startup".into());
        }
        Ok(())
    }

    /// Fences startup and existing workers, then joins the retained runtime.
    ///
    /// # Errors
    /// Reports worker startup, join, or cached drain failures.
    pub async fn drain(&self) -> Result<(), String> {
        let begin_result = self.begin_drain();
        let _startup = self.startup.lock().await;
        let data_result = match self.data.get() {
            Some(data) => data.drain().await,
            None => Ok(()),
        };
        let release_result = if self.data.get().is_none_or(|data| data.peers.drain_completed()) {
            self.registry.release_drained_hosts()
        } else {
            Err("replication worker cleanup has not completed".into())
        };
        begin_result.and(data_result).and(release_result)
    }
}

impl ProductionDataRuntime {
    /// Shared durable progress ledger used by proof producers.
    #[must_use]
    pub fn progress(&self) -> Arc<ReplicationProgress> {
        self.progress.clone()
    }

    /// Starts physical and causal workers only from verified accepted assignments.
    /// A follower opens no Engine: the snapshot receiver owns its physical files.
    ///
    /// # Errors
    /// Refuses invalid membership, scope, residency, role, encryption, or snapshots.
    pub async fn start(
        runtime: Arc<ProductionMemberRuntime>,
        progress: Arc<ReplicationProgress>,
        source: &dyn DurableWalAssignmentSource,
        databases: Vec<ProductionDatabaseAssignment>,
        dispatcher: Arc<dyn crate::replication_peers::PeerControlDispatcher>,
    ) -> Result<Self, String> {
        let owner = Arc::downgrade(&runtime);
        let peers = build_peer_databases(&runtime, source, databases).await?;
        let peers =
            ReplicationPeers::start_with_dispatch(runtime, progress.clone(), peers, dispatcher)?;
        Ok(Self { runtime: owner, peers, progress })
    }

    /// Live authenticated replication observations, never configuration-derived health.
    #[must_use]
    pub fn observations(&self) -> crate::replication_peers::PeerObservations {
        self.peers.observations()
    }

    /// Refuses use after the peer owner has stopped or begun draining.
    ///
    /// # Errors
    /// Returns the durable peer-owner failure state.
    pub fn ensure_running(&self) -> Result<(), String> {
        self.peers.ensure_running()
    }

    /// Fences progress and new replication lanes, and signals workers to stop
    /// without consuming their owner. Already accepted callbacks may finish;
    /// [`Self::drain`] joins workers and reports completion failures.
    pub fn begin_drain(&self) {
        self.progress.fence();
        self.peers.begin_drain();
    }

    /// Fences API durability waits, then drains workers and their Engine leases.
    ///
    /// # Errors
    /// Reports failed peer shutdown.
    pub async fn drain(&self) -> Result<(), String> {
        self.begin_drain();
        self.peers.drain().await
    }
}

/// Builds actual role-specific lanes against the same root Member used by the API.
///
/// # Errors
/// Refuses any incomplete accepted assignment or failed encrypted snapshot capture.
pub async fn build_peer_databases(
    runtime: &Arc<ProductionMemberRuntime>,
    source: &dyn DurableWalAssignmentSource,
    databases: Vec<ProductionDatabaseAssignment>,
) -> Result<PeerDatabaseBatch, String> {
    let mut lanes: BTreeMap<String, Vec<PeerDatabase>> = BTreeMap::new();
    let mut selected_regions = BTreeMap::new();
    let mut seen = BTreeSet::new();
    for database in databases {
        if !seen.insert(database.scope) || database.snapshot_max_bytes == 0 {
            return Err("duplicate database or invalid snapshot bound".into());
        }
        let assignment = source.load_assignment(database.scope).map_err(failure)?;
        let binding = runtime.bind_wal_replica(database.scope, source).map_err(failure)?;
        let transport = runtime.transport().config();
        let local = transport.local_identity();
        let local_region = transport.local_region().as_str().to_owned();
        if !database.selected_regions.contains(&local_region)
            || database.remote_writers.keys().any(|region| {
                region == &local_region || !database.selected_regions.contains(region)
            })
            || database.remote_writers.len() + 1 != database.selected_regions.len()
        {
            return Err("accepted regional writer coverage is incomplete".into());
        }
        let is_writer = &assignment.writer == local;
        if !is_writer && &assignment.follower != local {
            return Err("local member has no accepted physical seat".into());
        }
        let physical_peer = if is_writer { &assignment.follower } else { &assignment.writer };
        let physical =
            build_physical_driver(runtime, binding, &assignment, &database, is_writer).await?;
        lanes.entry(physical_peer.member().as_str().to_owned()).or_default().push(PeerDatabase {
            scope: database.scope,
            physical: Some(physical),
            causal: None,
        });
        if is_writer {
            for (region, member) in database.remote_writers {
                let peer = terrabase_member_transport::identity::MemberId::try_new(&member)
                    .map_err(failure)?;
                let admitted = transport.peer(&peer).ok_or("remote writer transport missing")?;
                if admitted.region().as_str() != region {
                    return Err("remote writer Region mismatch".into());
                }
                let causal = CausalDriver::new(
                    local.member().clone(),
                    peer,
                    region,
                    local_region.clone(),
                    BoundScope {
                        principal: local.member().as_str().to_owned(),
                        tenant: database.scope.tenant().to_text(),
                        project: database.scope.project().to_text(),
                        database: database.scope.database().to_text(),
                    },
                    assignment.generation,
                    assignment.epoch,
                )
                .map_err(failure)?;
                lanes.entry(member).or_default().push(PeerDatabase {
                    scope: database.scope,
                    physical: None,
                    causal: Some(Arc::new(Mutex::new(causal))),
                });
            }
        }
        selected_regions.insert(database.scope, database.selected_regions);
    }
    Ok(PeerDatabaseBatch { lanes, selected_regions })
}

async fn build_physical_driver(
    runtime: &Arc<ProductionMemberRuntime>,
    binding: terrabase_cluster::wal_route::WalReplicaBinding,
    assignment: &WalAssignment,
    database: &ProductionDatabaseAssignment,
    is_writer: bool,
) -> Result<PhysicalDriver, String> {
    if is_writer {
        let lease: Arc<dyn WalEngineHandle> =
            Arc::new(runtime.acquire_replication(&database.scope).map_err(failure)?);
        lease.engine().activate_origin_epoch(assignment.epoch).map_err(failure)?;
        let snapshot = WalSnapshotSource::capture(
            binding.clone(),
            lease.clone(),
            database.snapshot_staging.clone(),
        )
        .await
        .map_err(failure)?;
        let snapshot_bytes = (0..3)
            .try_fold(0u64, |total, slot| {
                total.checked_add(snapshot.manifest().file_bytes(slot).unwrap_or(0))
            })
            .ok_or("snapshot size overflow")?;
        if snapshot_bytes > database.snapshot_max_bytes {
            return Err("encrypted snapshot exceeds admission bound".into());
        }
        let checkpoint = snapshot.manifest().checkpoint();
        let tail_lease = lease.clone();
        let tail = tokio::task::spawn_blocking(move || {
            WalTail::resume_engine(tail_lease.engine(), checkpoint)
        })
        .await
        .map_err(failure)?
        .map_err(failure)?;
        Ok(PhysicalDriver::BootstrapWriter {
            writer: WalWriterDriver::new(binding, lease, tail),
            source: Box::new(snapshot),
        })
    } else {
        let mut receiver =
            SnapshotReceiver::new(binding, database.location.clone(), database.snapshot_max_bytes);
        receiver.keys = Some(database.keys.clone());
        if receiver.destination.join("terrabase.sqlite3").try_exists().map_err(failure)? {
            receiver.previous = Some(
                terrabase_cluster::wal_driver::WalFollowerDriver::recover(
                    receiver.binding.clone(),
                    receiver.destination.clone(),
                    receiver.keys.clone(),
                )
                .await
                .map_err(failure)?,
            );
        }
        Ok(PhysicalDriver::BootstrapFollower(Box::new(receiver)))
    }
}

fn failure(error: impl std::fmt::Debug) -> String {
    format!("production data activation: {error:?}")
}

/// Persistent admission and accepted-placement activation on one root Member.
/// The local receiver is available while membership is pending; its records only
/// install closed encrypted entries until reconciliation verifies acceptance.
pub struct ProductionAdmissionRuntime {
    runtime: Arc<ProductionMemberRuntime>,
    member: terrabase::member::Member,
    journal: Arc<terrabase_tbep::placement::PlacementJournal>,
    progress: Arc<ReplicationProgress>,
    records: Arc<Mutex<BTreeMap<String, crate::admission::AdmissionRecord>>>,
    host: Arc<crate::admission::AdmissionHost>,
    config: crate::config::ProductionConfig,
    activation: tokio::sync::Mutex<AdmissionActivation>,
    lifecycle: Arc<ProductionDataLifecycle>,
    dispatcher: Arc<dyn crate::replication_peers::PeerControlDispatcher>,
    usage: Arc<crate::usage_admission::UsageAdmissionJournal>,
    snapshot_max_bytes: u64,
    registry: Arc<crate::admitted_databases::AdmittedDatabaseRegistry>,
    replaying: Arc<AtomicBool>,
}

#[derive(Default)]
struct AdmissionActivation {
    active: BTreeSet<String>,
    pending_publication: BTreeMap<DatabaseRouteScope, Option<String>>,
}

/// Bridges the durable recovery worker to the production data owner. The
/// worker never receives a target resolver or fleet handle directly.
pub struct ProductionRestoreExecutor {
    runtime: Arc<ProductionAdmissionRuntime>,
    backups: Arc<crate::native_backup_runtime::NativeBackupRuntime>,
}

impl ProductionRestoreExecutor {
    /// Connects the admitted target owner and native backup provider fleet.
    #[must_use]
    pub fn new(
        runtime: Arc<ProductionAdmissionRuntime>,
        backups: Arc<crate::native_backup_runtime::NativeBackupRuntime>,
    ) -> Self {
        Self { runtime, backups }
    }
}

fn verify_restore_authority(
    journal: &terrabase_tbep::placement::PlacementJournal,
    request: &crate::native_recovery::NativeRestoreRequest,
) -> Result<(), String> {
    let grant = journal
        .verify_admitted_lifecycle_operation(&request.operation_grant)
        .map_err(|_| "restore operation authorization is unavailable")?;
    let digest = native_restore_request_digest(request)?;
    let placement = journal.accepted().map_err(|_| "accepted placement unavailable")?;
    if grant.action != terrabase_tbep::lifecycle::LifecycleGrantAction::RestoreToTarget
        || grant.operation_id != request.operation_id
        || grant.database != request.target_database_id
        || format!("{}/{}", grant.tenant, grant.project) != request.target_owner
        || grant.authority_epoch != request.authority_epoch
        || grant.authority_lease != request.authority_lease
        || grant.accepted_restore_request_digest != digest
        || grant.accepted_proof_digest != request.accepted_proof_digest
        || placement.proof_digest != request.accepted_proof_digest
        || placement.proposal_revision != grant.accepted_placement_revision
    {
        return Err("restore authority pins are stale or mismatched".into());
    }
    Ok(())
}

fn native_restore_request_digest(
    request: &crate::native_recovery::NativeRestoreRequest,
) -> Result<String, String> {
    let provider = match request.provider.as_str() {
        "oci" => 1,
        "tigris" => 2,
        "customer-s3" => 3,
        _ => return Err("restore provider is not approved".into()),
    };
    crate::production_lifecycle_control::restore_request_digest(
        &terrabase_tbep::proto::terrabase::v1::RestoreToTargetRequest {
            source_owner: request.source_owner.clone(),
            source_database: request.source_database_id.clone(),
            recovery_point_id: request.recovery_point_id.clone(),
            recovery_point_digest: request.recovery_point_digest.clone(),
            target_owner: request.target_owner.clone(),
            target_database: request.target_database_id.clone(),
            journal_cut: request.journal_cut.clone(),
            restore_epoch: request.restore_epoch,
            provider,
            ..Default::default()
        },
    )
}

impl crate::native_recovery::RestoreExecutor for ProductionRestoreExecutor {
    fn reconcile<'a>(
        &'a self,
        request: &'a crate::native_recovery::NativeRestoreRequest,
    ) -> std::pin::Pin<Box<dyn std::future::Future<Output = Result<(), String>> + Send + 'a>> {
        let runtime = Arc::clone(&self.runtime);
        let backups = Arc::clone(&self.backups);
        let target = request.target_database_id.clone();
        Box::pin(async move {
            tokio::task::spawn_blocking(move || {
                tokio::runtime::Handle::current()
                    .block_on(runtime.resume_restore_workers(&backups, &target, true))
            })
            .await
            .map_err(|_| "restore worker reconciliation unavailable")?
        })
    }
    fn execute<'a>(
        &'a self,
        request: &'a crate::native_recovery::NativeRestoreRequest,
    ) -> std::pin::Pin<
        Box<
            dyn std::future::Future<
                    Output = Result<crate::native_recovery::NativeRestoreOutcome, String>,
                > + Send
                + 'a,
        >,
    > {
        let request = request.clone();
        let runtime = Arc::clone(&self.runtime);
        let backups = Arc::clone(&self.backups);
        Box::pin(async move {
            if request.source_owner.is_empty()
                || request.target_owner.is_empty()
                || request.recovery_point_id.is_empty()
                || request.recovery_point_digest.len() != 64
                || request.journal_cut.is_empty()
                || request.restore_epoch == 0
                || request.authority_epoch == 0
                || request.authority_lease.is_empty()
                || request.accepted_proof_digest.len() != 64
            {
                return Err("restore request facts are incomplete".into());
            }
            let admitted = runtime
                .registry
                .resolve(&request.source_database_id)
                .map_err(|_| "source admission unavailable")?
                .ok_or("source database is not admitted")?;
            if admitted.record.regions != request.placement
                || admitted.record.database_id != request.source_database_id
                || format!("{}/{}", admitted.record.tenant_id, admitted.record.project_id)
                    != request.source_owner
            {
                return Err("restore source facts do not match accepted admission".into());
            }
            verify_restore_authority(&runtime.journal, &request)?;
            let target = runtime
                .registry
                .retained_snapshot()
                .map_err(|_| "target admission unavailable")?
                .into_iter()
                .find(|entry| entry.database.record.database_id == request.target_database_id)
                .ok_or("target database is not admitted")?;
            if format!("{}/{}", target.database.record.tenant_id, target.database.record.project_id)
                != request.target_owner
                || target.database.record.regions != request.placement
                || target.database.record.residency != admitted.record.residency
            {
                return Err(
                    "restore target facts do not match source residency or admission".into()
                );
            }
            let provider = match request.provider.as_str() {
                "oci" => terrabase::backup::object_store::Provider::Oci,
                "tigris" => terrabase::backup::object_store::Provider::Tigris,
                "customer-s3" => terrabase::backup::object_store::Provider::CustomerS3,
                _ => return Err("restore provider is not approved".into()),
            };
            let (point, timestamp) = match &request.selection {
                crate::native_recovery::NativeRestoreSelection::PointId(point) => {
                    (Some(point.clone()), None)
                }
                crate::native_recovery::NativeRestoreSelection::AtUnixMillis(value) => {
                    (None, Some(*value))
                }
            };
            let expected_cut =
                request.journal_cut.parse::<u64>().map_err(|_| "restore journal cut is invalid")?;
            // Release authorization observations before transferring the target's sole lease.
            drop(target);
            drop(admitted);
            let receipt = tokio::task::spawn_blocking(move || {
                tokio::runtime::Handle::current().block_on(runtime.restore_native_with_workers(
                    &backups,
                    &request.source_database_id,
                    &request.target_database_id,
                    provider,
                    point.as_deref(),
                    timestamp,
                    &request.operation_id,
                    &request.recovery_point_digest,
                    expected_cut,
                    request.restore_epoch,
                ))
            })
            .await
            .map_err(|_| "restore executor unavailable")??;
            if receipt.selected_identity != request.recovery_point_id
                || receipt.journal_cut != expected_cut
            {
                return Err("authenticated recovery manifest facts do not match request".into());
            }
            Ok(crate::native_recovery::NativeRestoreOutcome { receipt })
        })
    }
}

impl ProductionAdmissionRuntime {
    /// Opens and replays the authenticated admission journal against the existing
    /// Member. No accepted role is inferred from configuration or a staged proposal.
    ///
    /// # Errors
    /// Refuses missing encryption delivery, conflicting records, or unsafe storage.
    #[allow(clippy::too_many_arguments, reason = "explicit production dependency ownership")]
    pub fn new(
        runtime: Arc<ProductionMemberRuntime>,
        member: terrabase::member::Member,
        config: crate::config::ProductionConfig,
        journal: Arc<terrabase_tbep::placement::PlacementJournal>,
        registry: Arc<crate::admitted_databases::AdmittedDatabaseRegistry>,
        progress: Arc<ReplicationProgress>,
        per_database: usize,
        instance: u64,
        snapshot_max_bytes: u64,
        dispatcher: Arc<dyn crate::replication_peers::PeerControlDispatcher>,
    ) -> Result<Self, String> {
        use terrabase::encryption::OciVaultKeyDelivery;
        let keys = config.keys.as_ref().ok_or("production key delivery missing")?;
        let provider =
            OciVaultKeyDelivery::new(keys.socket.clone(), keys.orchestrator_uid, keys.socket_gid)
                .map_err(failure)?;
        let records = Arc::new(Mutex::new(BTreeMap::new()));
        let admitted = records.clone();
        let declarations = journal.clone();
        let authority_journal = journal.clone();
        let local = config.environment.member.to_text();
        let mut usage = crate::usage_admission::UsageAdmissionJournal::open(
            member.root(),
            config.environment.name.as_str().to_owned(),
            local.clone(),
            runtime.transport().config().local_region().as_str().to_owned(),
            journal.clone(),
        )?;
        if let Some(budget) = config.budget.as_ref() {
            let public_key_spki = std::fs::read(&budget.public_key_spki_file)
                .map_err(|_| "budget signer key unavailable")?;
            let resolver =
                Arc::new(crate::production_budget::AcceptedAdmissionBindingResolver::new(
                    records.clone(),
                    journal.clone(),
                ));
            usage = usage.with_allocation_verifier(
                crate::usage_admission::AllocationVerifierConfig {
                    key_id: budget.key_id.clone(),
                    public_key_spki,
                    policy_epoch: budget.policy_epoch,
                    consent_epoch: budget.consent_epoch,
                    resolver,
                },
            )?;
            let budget_runtime = runtime.clone();
            let budget_records = records.clone();
            let listed_records = records.clone();
            usage = usage.with_pool_runtime(crate::usage_admission::PoolRuntime {
                engines: Arc::new(move |database| {
                    let record = budget_records
                        .lock()
                        .map_err(|_| "admission registry poisoned")?
                        .get(database)
                        .cloned()
                        .ok_or("database admission unavailable")?;
                    let scope = record_scope(&record)?;
                    Ok(Arc::new(budget_runtime.acquire_replication(&scope).map_err(failure)?)
                        as Arc<dyn terrabase_cluster::wal_driver::WalEngineHandle>)
                }),
                databases: Arc::new(move || {
                    Ok(listed_records
                        .lock()
                        .map_err(|_| "admission registry poisoned")?
                        .keys()
                        .cloned()
                        .collect())
                }),
                limits: crate::usage_admission::PoolLimits {
                    maximum: budget.pool_max_local_units,
                    low_water: budget.pool_low_water_local_units,
                    lifetime_seconds: budget.pool_lifetime_seconds,
                },
            })?;
        }
        let usage = Arc::new(usage);
        let authority = build_admission_authority(
            journal.clone(),
            usage.clone(),
            member.clone(),
            runtime.clone(),
            local.clone(),
        );
        let replaying = Arc::new(AtomicBool::new(true));
        let customer_configs = config.databases.clone();
        let customer_keys = config.keys.clone().ok_or("production key delivery missing")?;
        let key_source = Arc::new(move |record: &crate::admission::AdmissionRecord| {
            let configured = customer_configs
                .iter()
                .find(|entry| entry.database.to_text() == record.database_id)
                .ok_or("configured database unavailable")?;
            let scope = KeyScope::new_with_project(
                &record.environment,
                terrabase::identity::TenantUlid::from_text(&record.tenant_id).map_err(failure)?,
                terrabase::identity::ProjectUlid::from_text(&record.project_id).map_err(failure)?,
                terrabase::identity::DatabaseUlid::from_text(&record.database_id)
                    .map_err(failure)?,
                KeyPurpose::Data,
            )
            .map_err(failure)?;
            let version = DatabaseKeyVersion::from_text(&record.key_version).map_err(failure)?;
            let region = record.region.parse().map_err(|_| "invalid admitted Region")?;
            match configured.encryption_provider {
                terrabase::encryption::KeyProviderIdentity::OciVault
                    if configured.byok.is_none() =>
                {
                    Ok(Arc::new(
                        OciVaultDatabaseKeys::new(
                            OciVaultKeyDelivery::new(
                                customer_keys.socket.clone(),
                                customer_keys.orchestrator_uid,
                                customer_keys.socket_gid,
                            )
                            .map_err(|_| "OCI Vault adapter unavailable")?,
                            KeyVersionMetadata::new(scope, version),
                            region,
                        )
                        .map_err(|_| "key authorization unavailable")?,
                    ) as Arc<dyn DatabaseKeySource>)
                }
                terrabase::encryption::KeyProviderIdentity::CustomerKms => {
                    let byok =
                        configured.byok.as_ref().ok_or("customer KMS configuration missing")?;
                    if byok.key_provider != "aws-kms" {
                        return Err("customer KMS provider disagrees with configuration".into());
                    }
                    let adapter = CustomerKmsKeyDelivery::new(
                        customer_keys.socket.clone(),
                        customer_keys.orchestrator_uid,
                        customer_keys.socket_gid,
                        byok.key_provider.clone(),
                        byok.key_id.clone(),
                        byok.key_region.clone(),
                        byok.key_endpoint.clone().ok_or("customer KMS endpoint unavailable")?,
                    )
                    .map_err(|_| "customer KMS adapter unavailable")?;
                    Ok(Arc::new(
                        CustomerKmsDatabaseKeys::new(
                            adapter,
                            KeyVersionMetadata::new(scope, version),
                            region,
                        )
                        .map_err(|_| "customer key authorization unavailable")?,
                    ) as Arc<dyn DatabaseKeySource>)
                }
                _ => Err("encryption provider and BYOK configuration disagree".into()),
            }
        });
        let storage = crate::production_admission::storage_admission_with_replay_and_key_source(
            member.clone(),
            registry.clone(),
            provider,
            authority,
            per_database,
            instance,
            Arc::clone(&replaying),
            key_source,
        );
        let callback: crate::admission::StorageAdmission = Arc::new(move |record| {
            storage(record)?;
            let selected_regions = record.regions.iter().cloned().collect::<BTreeSet<_>>();
            declarations
                .register_database(record_scope(record)?, &record.region, &selected_regions)
                .map_err(failure)?;
            admitted
                .lock()
                .map_err(|_| "admission registry poisoned")?
                .insert(record.database_id.clone(), record.clone());
            Ok(())
        });
        let host = crate::admission::AdmissionHost::open(
            member.root(),
            config.environment.name.as_str().to_owned(),
            runtime
                .transport()
                .config()
                .local_region()
                .as_str()
                .parse()
                .map_err(|_| "invalid local Region")?,
            config.environment.member,
            callback,
        )?
        .with_usage_delivery(usage.clone());
        records.lock().map_err(|_| "admission registry poisoned")?.extend(host.records_snapshot()?);
        Ok(Self {
            runtime,
            member,
            journal,
            progress: progress.clone(),
            records,
            host: Arc::new(host),
            config,
            activation: tokio::sync::Mutex::new(AdmissionActivation::default()),
            lifecycle: Arc::new(ProductionDataLifecycle::new(progress, registry.clone())),
            dispatcher,
            usage,
            snapshot_max_bytes,
            registry,
            replaying,
        })
    }

    /// Attaches the receiver sharing the public API's exact identity verifier.
    /// Call before exposing the admission host to the listener.
    ///
    /// # Errors
    /// Refuses attachment after the host has been shared.
    pub fn with_identity_delivery(
        mut self,
        receiver: Arc<crate::identity_secret_delivery::IdentitySecretDelivery>,
    ) -> Result<Self, String> {
        let host = Arc::try_unwrap(self.host).map_err(|_| "admission host already shared")?;
        self.host = Arc::new(host.with_identity_delivery(receiver));
        Ok(self)
    }

    /// Attaches native deletion preflight, preparation, and final durable stop.
    /// The fleet supplies actual retained object horizons for this exact database.
    ///
    /// # Errors
    /// Refuses attachment after the admission receiver has been shared.
    pub fn with_native_deletion(
        mut self,
        backups: Arc<Mutex<terrabase::backup::worker::NativeBackupFleet>>,
        backup_gate: Arc<tokio::sync::Mutex<()>>,
    ) -> Result<Self, String> {
        let host = Arc::try_unwrap(self.host).map_err(|_| "admission host already shared")?;
        let preflight = self.registry.clone();
        let prepare = self.registry.clone();
        let stop = self.registry.clone();
        self.host = Arc::new(
            host.with_storage_stop_preflight(Arc::new(move |record, _request| {
                let protected = preflight.stop_preflight(record)?;
                let _gate = backup_gate.try_lock().map_err(|_| "native backup fleet busy")?;
                let retained = backups
                    .lock()
                    .map_err(|_| "native backup fleet poisoned")?
                    .retained_until_unix_millis(&record.database_id)
                    .map_err(failure)?;
                Ok((protected, retained))
            }))
            .with_storage_prepare_stop(Arc::new(move |record, request| {
                prepare.prepare_stop(record, request)
            }))
            .with_storage_stop(Arc::new(move |record, request| stop.stop(record, request))),
        );
        Ok(self)
    }

    /// The exact receiver whose storage callback declares future placement scopes.
    #[must_use]
    pub fn admission_host(&self) -> Arc<crate::admission::AdmissionHost> {
        self.host.clone()
    }

    /// Shares the existing root catalog; callers must retain leases for Engine access.
    #[must_use]
    pub fn member(&self) -> terrabase::member::Member {
        self.member.clone()
    }

    /// Returns immutable persisted admissions without opening any database.
    ///
    /// # Errors
    /// Refuses a poisoned admission registry.
    pub fn admitted_records(&self) -> Result<Vec<crate::admission::AdmissionRecord>, String> {
        Ok(self
            .records
            .lock()
            .map_err(|_| "admission registry poisoned")?
            .values()
            .cloned()
            .collect())
    }

    /// Returns the runtime used to acquire accepted writer leases for observations.
    #[must_use]
    pub fn member_runtime(&self) -> Arc<ProductionMemberRuntime> {
        self.runtime.clone()
    }

    /// Current live peer observations, absent until the data workers start.
    #[must_use]
    pub fn observations(&self) -> Option<crate::replication_peers::PeerObservations> {
        self.lifecycle.get().map(ProductionDataRuntime::observations)
    }

    /// Receipt ledger shared by writes and catch-up observations.
    #[must_use]
    pub fn progress(&self) -> Arc<ReplicationProgress> {
        self.progress.clone()
    }

    /// Returns the shared data lifecycle owner for the production adapter.
    #[must_use]
    pub fn lifecycle(&self) -> Arc<ProductionDataLifecycle> {
        self.lifecycle.clone()
    }

    /// Verified accepted placement journal used to determine local writer roles.
    #[must_use]
    pub fn journal(&self) -> Arc<terrabase_tbep::placement::PlacementJournal> {
        self.journal.clone()
    }

    /// Returns the write-budget binder backed by this runtime's persisted
    /// delivery journal. Production composition must pass this same owner to
    /// the public API; a missing verifier intentionally yields no binder.
    #[must_use]
    pub fn budget_runtime(&self) -> Option<crate::production_budget::ProductionBudgetRuntime> {
        self.config
            .budget
            .as_ref()
            .map(|_| crate::production_budget::ProductionBudgetRuntime::new(self.usage.clone()))
    }

    /// Composes native backup streams from the retained serving hosts.
    ///
    /// Every input is resolved from the admitted registry and the effective
    /// per-database policy; no engine or destination is opened by location.
    pub fn compose_native_backup(
        &self,
        limits: FleetLimits,
    ) -> Result<crate::native_backup_runtime::NativeBackupRuntime, String> {
        let snapshots = self.registry.retained_snapshot()?;
        let mut inputs = Vec::new();
        for snapshot in snapshots {
            let database = snapshot.database.record.database_id.clone();
            let input = if snapshot.activated {
                self.resolve_native_backup_input(&database)?
            } else {
                let tenant =
                    terrabase::types::TenantId::try_new(&snapshot.database.record.tenant_id)
                        .map_err(|_| "invalid admitted tenant")?;
                let lifecycle = snapshot
                    .database
                    .host
                    .engine()
                    .lifecycle_state(&snapshot.database.host.engine().scope_for(&tenant))
                    .map_err(|_| "database lifecycle unavailable")?;
                if !lifecycle.paused {
                    continue;
                }
                self.resolve_native_backup_input_for_recovery(&database)?
            };
            inputs.push(input);
        }
        crate::production_backup::compose_native_backup(limits, inputs)
            .map_err(|error| error.to_string())
    }

    /// Resolves one already-serving database into the native backup owner input.
    /// The caller must supply the accepted database identity; pending entries
    /// and unknown identities fail closed.
    pub fn resolve_native_backup_input(
        &self,
        database: &str,
    ) -> Result<crate::native_backup_runtime::NativeBackupInput, String> {
        self.resolve_native_backup_input_with_mode(database, false)
    }

    /// Resolves a retained paused database for startup recovery. This path is
    /// intentionally private to composition: a paused target has no public
    /// read cut, but its durable owner, policy, keys, and placement still
    /// require the same admission checks as an active stream.
    fn resolve_native_backup_input_for_recovery(
        &self,
        database: &str,
    ) -> Result<crate::native_backup_runtime::NativeBackupInput, String> {
        self.resolve_native_backup_input_with_mode(database, true)
    }

    fn resolve_native_backup_input_with_mode(
        &self,
        database: &str,
        allow_paused: bool,
    ) -> Result<crate::native_backup_runtime::NativeBackupInput, String> {
        use crate::native_backup_runtime::{AdmittedBackupEngine, NativeBackupInput};
        use terrabase::backup::worker::CustomerKmsBackupKeys;
        use terrabase::encryption::{
            CustomerKmsDatabaseKeys, KeyPurpose, KeyScope, KeyVersionMetadata,
        };
        let snapshot = self
            .registry
            .retained_snapshot()?
            .into_iter()
            .find(|snapshot| snapshot.database.record.database_id == database)
            .ok_or("database is not retained")?;
        let tenant = terrabase::types::TenantId::try_new(&snapshot.database.record.tenant_id)
            .map_err(|_| "invalid admitted tenant")?;
        let lifecycle = snapshot
            .database
            .host
            .engine()
            .lifecycle_state(&snapshot.database.host.engine().scope_for(&tenant))
            .map_err(|_| "database lifecycle unavailable")?;
        if lifecycle.access_revoked() {
            return Err("database access is revoked".into());
        }
        if snapshot.activated {
            if lifecycle.paused {
                return Err("active database lifecycle is paused".into());
            }
            let read_cut = snapshot
                .database
                .host
                .engine_host()
                .admitted_shard_read_cut()
                .map_err(|_| "admitted shard inventory unavailable")?;
            ensure_native_backup_shard_capture_supported(
                read_cut.as_ref().and_then(|cut| cut.route_generation),
            )?;
        } else if !allow_paused || !lifecycle.paused {
            return Err("database is not actively admitted".into());
        }
        let record = self
            .admitted_records()?
            .into_iter()
            .find(|record| record.database_id == database)
            .ok_or("admitted backup record unavailable")?;
        let owner = snapshot.database.host.engine().database_owner();
        if owner.database().to_text() != record.database_id
            || owner.tenant().to_text() != record.tenant_id
            || owner.project().to_text() != record.project_id
            || owner.region().as_str() != record.region
        {
            return Err("retained engine owner disagrees with admitted record".into());
        }
        let configured = self
            .config
            .databases
            .iter()
            .find(|entry| entry.database.to_text() == database)
            .ok_or("configured database unavailable")?;
        if configured.tenant.to_text() != record.tenant_id
            || configured.project.to_text() != record.project_id
            || configured.region.as_str() != record.region
            || configured.selected_regions
                != record
                    .regions
                    .iter()
                    .map(|region| region.parse())
                    .collect::<Result<BTreeSet<_>, _>>()
                    .map_err(|_| "invalid admitted placement")?
            || configured.encryption_key.to_string() != record.key_version
        {
            return Err("configured backup identity disagrees with admitted record".into());
        }
        let policy = configured.backup.as_ref().ok_or("native backup policy missing")?;
        let key_config = self.config.keys.as_ref().ok_or("production key delivery missing")?;
        let scope = KeyScope::new_with_project(
            &record.environment,
            terrabase::identity::TenantUlid::from_text(&record.tenant_id).map_err(failure)?,
            terrabase::identity::ProjectUlid::from_text(&record.project_id).map_err(failure)?,
            terrabase::identity::DatabaseUlid::from_text(&record.database_id).map_err(failure)?,
            KeyPurpose::Backup,
        )
        .map_err(failure)?;
        let region = record.region.parse().map_err(|_| "invalid admitted Region")?;
        let (keys, destinations, mut historical_destinations) = if let Some(byok) = &configured.byok
        {
            if configured.encryption_provider
                != terrabase::encryption::KeyProviderIdentity::CustomerKms
                || !matches!(byok.key_provider.as_str(), "aws-kms")
            {
                return Err("customer BYOK provider disagrees with admitted configuration".into());
            }
            let endpoint = byok.key_endpoint.clone().ok_or("customer KMS endpoint unavailable")?;
            let adapter = terrabase::encryption::CustomerKmsKeyDelivery::new(
                key_config.socket.clone(),
                key_config.orchestrator_uid,
                key_config.socket_gid,
                byok.key_provider.clone(),
                byok.key_id.clone(),
                byok.key_region.clone(),
                endpoint,
            )
            .map_err(|_| "customer KMS adapter unavailable")?;
            let source = CustomerKmsDatabaseKeys::new(
                adapter,
                KeyVersionMetadata::new(
                    scope,
                    DatabaseKeyVersion::from_text(&record.key_version).map_err(failure)?,
                ),
                region,
            )
            .map_err(|_| "customer key authorization unavailable")?;
            let destination_policy =
                policy.destinations.first().ok_or("customer backup policy metadata unavailable")?;
            if destination_policy.provider != terrabase::backup::object_store::Provider::CustomerS3
            {
                return Err("customer backup policy provider unavailable".into());
            }
            let identity =
                terrabase::backup::workload_identity_delivery::UnixWorkloadIdentityProvider::new(
                    key_config.socket.clone(),
                    terrabase::backup::workload_identity_delivery::WorkloadIdentityScope {
                        role: byok.backup_role_arn.clone(),
                        external_id: byok.backup_external_id.clone(),
                        endpoint: byok.backup_endpoint.clone(),
                        bucket: byok.backup_bucket.clone(),
                        region: byok.backup_region.clone(),
                        placement_revision: destination_policy.placement_revision.clone(),
                    },
                )
                .map_err(|_| "customer S3 workload identity unavailable")?;
            let destination = crate::production_backup::customer_destination_from_config(
                byok,
                destination_policy,
                region,
                Arc::new(identity),
            )
            .map_err(|_| "customer backup destination unavailable")?;
            (
                Arc::new(
                    CustomerKmsBackupKeys::new(Arc::new(source))
                        .map_err(|_| "backup key authorization unavailable")?,
                ) as Arc<dyn terrabase::backup::worker::BackupKeys>,
                vec![destination.clone()],
                vec![destination],
            )
        } else {
            let provider = OciVaultKeyDelivery::new(
                key_config.socket.clone(),
                key_config.orchestrator_uid,
                key_config.socket_gid,
            )
            .map_err(failure)?;
            let source = OciVaultDatabaseKeys::new(
                provider,
                KeyVersionMetadata::new(
                    scope,
                    DatabaseKeyVersion::from_text(&record.key_version).map_err(failure)?,
                ),
                region,
            )
            .map_err(failure)?;
            let destinations =
                crate::production_backup::destinations_from_config(&policy.destinations, region)
                    .map_err(|_| "native backup destination unavailable")?;
            let historical = crate::production_backup::destinations_from_config(
                &policy.historical_destinations,
                region,
            )
            .map_err(|_| "native backup historical destination unavailable")?;
            (
                Arc::new(
                    CustomerKmsBackupKeys::new(Arc::new(source))
                        .map_err(|_| "backup key authorization unavailable")?,
                ) as Arc<dyn terrabase::backup::worker::BackupKeys>,
                destinations,
                historical,
            )
        };
        historical_destinations.extend(destinations.iter().cloned());
        let placement = terrabase::backup::PlacementLock::explicit(
            record
                .regions
                .iter()
                .map(|region| region.parse())
                .collect::<Result<Vec<_>, _>>()
                .map_err(|_| "invalid admitted placement")?,
        )
        .map_err(|_| "invalid admitted placement")?;
        Ok(NativeBackupInput {
            engine: Arc::new(AdmittedBackupEngine::new(snapshot.database.host.clone())),
            keys,
            destinations,
            historical_destinations,
            policy_revision: policy.policy_revision.clone(),
            retention_days: policy.retention_days,
            staging: configured.backup_root.clone(),
            placement,
        })
    }

    /// Returns the narrow source-bound resolver used by native backup
    /// composition. The closure retains this admission authority so callers
    /// cannot construct backup inputs from configuration alone.
    pub fn native_backup_input_resolver(
        self: &Arc<Self>,
    ) -> Arc<
        dyn Fn(&str) -> Result<crate::native_backup_runtime::NativeBackupInput, String>
            + Send
            + Sync,
    > {
        let admission = Arc::clone(self);
        Arc::new(move |database| admission.resolve_native_backup_input(database))
    }

    /// Returns the narrow resolver for an already admitted paused target.
    pub fn native_restore_target_resolver(
        self: &Arc<Self>,
    ) -> Arc<
        dyn Fn(
                &str,
            ) -> Result<
                (terrabase::engine::EngineConfig, terrabase::backup::PlacementLock),
                String,
            > + Send
            + Sync,
    > {
        let admission = Arc::clone(self);
        Arc::new(move |database| admission.resolve_native_restore_target(database))
    }

    /// Resolves an already admitted target for native restore readback.
    ///
    /// The target must be retained by admission; its owner,
    /// location, placement, and data key version all come from the durable
    /// admission/configuration pair. No source key or generated identity is
    /// reused.
    pub fn resolve_native_restore_target(
        &self,
        database: &str,
    ) -> Result<(terrabase::engine::EngineConfig, terrabase::backup::PlacementLock), String> {
        let snapshot = self
            .registry
            .retained_snapshot()?
            .into_iter()
            .find(|snapshot| snapshot.database.record.database_id == database)
            .ok_or("native restore target is not retained")?;
        let tenant = terrabase::types::TenantId::try_new(&snapshot.database.record.tenant_id)
            .map_err(|_| "invalid restore target tenant")?;
        let lifecycle = snapshot
            .database
            .host
            .engine()
            .lifecycle_state(&snapshot.database.host.engine().scope_for(&tenant))
            .map_err(|_| "restore target lifecycle unavailable")?;
        if lifecycle.access_revoked() {
            return Err("native restore target access is revoked".into());
        }
        let record = self
            .admitted_records()?
            .into_iter()
            .find(|record| record.database_id == database)
            .ok_or("native restore target admission is unavailable")?;
        let configured = self
            .config
            .databases
            .iter()
            .find(|entry| entry.database.to_text() == database)
            .ok_or("native restore target configuration is unavailable")?;
        let region = record.region.parse().map_err(|_| "invalid restore target Region")?;
        let owner = terrabase::storage::DatabaseOwner::try_new(
            &record.environment,
            configured.tenant,
            configured.project,
            configured.database,
            region,
        )
        .map_err(failure)?;
        // Reuse the source admitted into Member. Constructing a second
        // OciVault source here gives encrypted VFS a different provider
        // identity and breaks lazy reopen after eviction/restart.
        let id = terrabase::member::DatabaseId::try_new(database).map_err(failure)?;
        let admitted_keys =
            self.member.storage_key_source(&id).ok_or("production key delivery missing")?;
        let target = terrabase::engine::EngineConfig::durable(configured.location.clone())
            .map_err(failure)?
            .with_owner(owner)
            .with_storage_keys(admitted_keys);
        let placement = terrabase::backup::PlacementLock::explicit(
            record
                .regions
                .iter()
                .map(|value| value.parse())
                .collect::<Result<Vec<_>, _>>()
                .map_err(|_| "invalid restore target placement")?,
        )
        .map_err(|_| "invalid restore target placement")?;
        Ok((target, placement))
    }

    async fn restore_native_with_workers(
        &self,
        backups: &crate::native_backup_runtime::NativeBackupRuntime,
        source: &str,
        target: &str,
        provider: terrabase::backup::object_store::Provider,
        point: Option<&str>,
        timestamp: Option<u64>,
        operation_id: &str,
        expected_manifest_digest: &str,
        expected_journal_boundary: u64,
        restore_epoch: u64,
    ) -> Result<crate::native_recovery::NativeRestoreReceipt, String> {
        // The ProductionRestoreExecutor blocking owner retains this complete
        // workflow when the requesting future is cancelled.
        let record = self
            .admitted_records()?
            .into_iter()
            .find(|record| record.database_id == target)
            .ok_or("restore target admission unavailable")?;
        let scope = record_scope(&record)?;
        let data = self.lifecycle.get().ok_or("replication lifecycle unavailable")?;
        let _suspension = data.peers.suspend_database(scope).await?;
        let result = match backups.suspend_for_restore_async(target).await {
            Ok(()) => {
                self.restore_native_to_target(
                    backups,
                    source,
                    target,
                    provider,
                    point,
                    timestamp,
                    operation_id,
                    expected_manifest_digest,
                    expected_journal_boundary,
                    restore_epoch,
                )
                .await
            }
            Err(_) => Err("native backup restore suspension unavailable".into()),
        };
        // Both success and rollback rebind through current admitted authority.
        // A failure here remains discoverable through the retained suspension;
        // marker reconciliation retries it before serving publication.
        self.resume_restore_workers(backups, target, result.is_ok()).await?;
        if result.is_ok() {
            self.registry.resume(target, operation_id)?;
        }
        result
    }

    async fn resume_restore_workers(
        &self,
        backups: &crate::native_backup_runtime::NativeBackupRuntime,
        target: &str,
        resume_capture: bool,
    ) -> Result<(), String> {
        let record = self
            .admitted_records()?
            .into_iter()
            .find(|record| record.database_id == target)
            .ok_or("restore target admission unavailable")?;
        let scope = record_scope(&record)?;
        let data = self.lifecycle.get().ok_or("replication lifecycle unavailable")?;
        if let Some(suspension) = data.peers.pending_suspension(scope)? {
            let (proposal, digest) =
                self.refresh_inventory()?.ok_or("restore placement unavailable")?;
            let (assignment, _) = self
                .build_database_assignment(
                    &record,
                    &proposal,
                    &digest,
                    &self.config.environment.member.to_text(),
                )?
                .ok_or("restore assignment unavailable")?;
            let lanes =
                build_peer_databases(&self.runtime, &*self.journal, vec![assignment]).await?;
            data.peers.resume_database(&suspension, lanes).await?;
        }
        let target = self
            .registry
            .retained_snapshot()?
            .into_iter()
            .find(|entry| entry.database.record.database_id == target)
            .ok_or("restore target host unavailable")?;
        backups
            .resume_after_restore_async(
                &record.database_id,
                Arc::clone(&target.database.host),
                resume_capture,
            )
            .await
            .map_err(|_| "native backup restore rebind unavailable".to_owned())
    }

    /// Executes a native restore only after the target has been admitted and
    /// durably paused. The caller supplies the already authenticated source
    /// fleet and operation identity; this method never opens a source engine
    /// by path or falls back to a local provider.
    async fn restore_native_to_target(
        &self,
        backups: &crate::native_backup_runtime::NativeBackupRuntime,
        source: &str,
        target: &str,
        provider: terrabase::backup::object_store::Provider,
        point: Option<&str>,
        timestamp: Option<u64>,
        operation_id: &str,
        expected_manifest_digest: &str,
        expected_journal_boundary: u64,
        restore_epoch: u64,
    ) -> Result<crate::native_recovery::NativeRestoreReceipt, String> {
        let reservation = self
            .registry
            .reserve_restore(target)?
            .rollback_on_drop(self.member.clone(), Arc::clone(&self.registry));
        let id = terrabase::member::DatabaseId::try_new(target).map_err(failure)?;
        self.member.close_database(&id).map_err(failure)?;
        let record = reservation.record().clone();
        let target_location = reservation.location().to_path_buf();
        let recover = |reservation: crate::admitted_databases::RestoreReservation,
                       error: String| {
            let lease = reservation.reacquire(&self.member).map_err(failure)?;
            reservation.complete(&self.registry, lease, 1).map_err(failure)?;
            Err(error)
        };
        let configured = self
            .config
            .databases
            .iter()
            .find(|entry| entry.database.to_text() == target)
            .ok_or_else(|| "native restore target configuration is unavailable".to_owned());
        let configured = match configured {
            Ok(configured) => configured,
            Err(error) => return recover(reservation, error),
        };
        let region =
            match record.region.parse().map_err(|_| "invalid restore target Region".to_owned()) {
                Ok(region) => region,
                Err(error) => return recover(reservation, error),
            };
        let owner = match terrabase::storage::DatabaseOwner::try_new(
            &record.environment,
            configured.tenant,
            configured.project,
            configured.database,
            region,
        )
        .map_err(failure)
        {
            Ok(owner) => owner,
            Err(error) => return recover(reservation, failure(error)),
        };
        let keys = match self
            .config
            .keys
            .as_ref()
            .ok_or_else(|| "production key delivery missing".to_owned())
        {
            Ok(keys) => keys,
            Err(error) => return recover(reservation, error),
        };
        let provider_key = match OciVaultKeyDelivery::new(
            keys.socket.clone(),
            keys.orchestrator_uid,
            keys.socket_gid,
        ) {
            Ok(provider_key) => provider_key,
            Err(error) => return recover(reservation, failure(error)),
        };
        let scope = match KeyScope::new_with_project(
            &record.environment,
            configured.tenant,
            configured.project,
            configured.database,
            KeyPurpose::Data,
        ) {
            Ok(scope) => scope,
            Err(error) => return recover(reservation, failure(error)),
        };
        let target_keys = match OciVaultDatabaseKeys::new(
            provider_key,
            KeyVersionMetadata::new(scope, configured.encryption_key),
            region,
        ) {
            Ok(keys) => keys,
            Err(error) => return recover(reservation, failure(error)),
        };
        let target_config = match terrabase::engine::EngineConfig::durable(target_location) {
            Ok(config) => config.with_owner(owner).with_storage_keys(Arc::new(target_keys)),
            Err(error) => return recover(reservation, failure(error)),
        };
        let regions = match record
            .regions
            .iter()
            .map(|value| value.parse())
            .collect::<Result<Vec<_>, _>>()
            .map_err(|_| "invalid restore target placement".to_owned())
        {
            Ok(regions) => regions,
            Err(error) => return recover(reservation, error),
        };
        let placement = match terrabase::backup::PlacementLock::explicit(regions)
            .map_err(|_| "invalid restore target placement".to_owned())
        {
            Ok(placement) => placement,
            Err(error) => return recover(reservation, error),
        };
        let selection = point
            .map_or_else(|| timestamp.expect("timestamp selection").to_string(), str::to_owned);
        let identity = terrabase::backup::RestoreIdentity {
            operation_id: operation_id.to_owned(),
            source_database_id: source.to_owned(),
            target_database_id: target.to_owned(),
            provider: provider.as_str().to_owned(),
            selection,
            expected_manifest_digest: expected_manifest_digest.to_owned(),
            expected_journal_boundary,
            restore_epoch,
        };
        let restored = if let Some(timestamp) = timestamp {
            backups
                .restore_at_millis(
                    source,
                    provider,
                    timestamp,
                    &target_config,
                    operation_id,
                    &placement,
                    &identity,
                )
                .await
                .map(|receipt| crate::native_backup_runtime::native_restore_receipt(&receipt))
        } else {
            backups
                .restore_logical_from_provider(
                    source,
                    provider,
                    point.ok_or("restore point is missing")?,
                    &target_config,
                    &placement,
                    &identity,
                )
                .await
        };
        let restored = restored
            .map_err(|_| "native restore failed or target authority was unavailable".to_owned());
        let lease = reservation.reacquire(&self.member).map_err(failure)?;
        reservation.complete(&self.registry, lease, 1)?;
        match restored {
            Ok(receipt) => Ok(receipt),
            Err(error) => Err(error),
        }
    }

    /// Reconciles retained acceptance and starts the actual role-specific workers.
    ///
    /// # Errors
    /// Refuses changed authority, missing admitted records, or failed worker startup.
    pub async fn reconcile(&self) -> Result<(), String> {
        let mut activation = self.activation.lock().await;
        if self.lifecycle.is_draining() {
            return Err("production admission runtime is draining".into());
        }
        if let Some(data) = self.lifecycle.get() {
            data.ensure_running()?;
        }
        let Some((proposal, proof_digest)) = self.refresh_inventory()? else {
            return Ok(());
        };
        // Auth groups need the shared accept owner even before a database lane is admitted.
        if self.lifecycle.get().is_none() {
            self.lifecycle
                .start(self.runtime.clone(), &*self.journal, Vec::new(), self.dispatcher.clone())
                .await?;
        }
        let records = self
            .records
            .lock()
            .map_err(|_| "admission registry poisoned")?
            .values()
            .cloned()
            .collect::<Vec<_>>();
        self.retire_stopped_writers(&records, &mut activation).await?;
        self.publish_pending(&mut activation)?;
        let local_member = self.config.environment.member.to_text();
        let mut databases = Vec::new();
        let mut newly_active = Vec::new();
        for record in records {
            if let Some((database, active)) = self.prepare_active_database(
                &record,
                &proposal,
                &proof_digest,
                &local_member,
                &activation,
            )? {
                databases.push(database);
                newly_active.push(active);
            }
        }
        if databases.is_empty() {
            return Ok(());
        }
        let data = self.lifecycle.get().ok_or("shared member receiver is unavailable")?;
        let lanes = build_peer_databases(&self.runtime, &*self.journal, databases).await?;
        data.peers.add_databases(lanes).await?;
        // The queue commit cannot be canceled halfway. Record its phase before
        // attempting fallible public publication, so retries never reinstall lanes.
        for (scope, database, writer) in newly_active {
            activation.active.insert(database.clone());
            activation.pending_publication.insert(scope, writer.then_some(database));
        }
        let result = self.publish_pending(&mut activation);
        if result.is_ok() {
            self.replaying.store(false, Ordering::Release);
        }
        result
    }

    fn prepare_active_database(
        &self,
        record: &crate::admission::AdmissionRecord,
        proposal: &terrabase_tbep::proto::terrabase::v1::PlacementProposal,
        proof_digest: &str,
        local_member: &str,
        activation: &AdmissionActivation,
    ) -> Result<Option<(ProductionDatabaseAssignment, (DatabaseRouteScope, String, bool))>, String>
    {
        if activation.active.contains(&record.database_id) {
            return Ok(None);
        }
        self.build_database_assignment(record, proposal, proof_digest, local_member)
    }

    fn build_database_assignment(
        &self,
        record: &crate::admission::AdmissionRecord,
        proposal: &terrabase_tbep::proto::terrabase::v1::PlacementProposal,
        proof_digest: &str,
        local_member: &str,
    ) -> Result<Option<(ProductionDatabaseAssignment, (DatabaseRouteScope, String, bool))>, String>
    {
        let scope = record_scope(record)?;
        let selected: BTreeSet<_> = record.regions.iter().cloned().collect();
        let assignments: Vec<_> = proposal
            .assignments
            .iter()
            .filter(|assignment| {
                assignment.tenant == record.tenant_id
                    && assignment.project == record.project_id
                    && assignment.database == record.database_id
            })
            .collect();
        if assignments.is_empty() {
            return Ok(None);
        }
        if assignments.iter().map(|assignment| assignment.region.clone()).collect::<BTreeSet<_>>()
            != selected
        {
            return Err("accepted database residency differs from admission".into());
        }
        let id = terrabase::member::DatabaseId::try_new(&record.database_id).map_err(failure)?;
        self.host.admit(record)?;
        if self.usage.resolve(record, proposal.epoch, proof_digest)?.is_none() {
            return Ok(None);
        }
        if let Some(assignment) =
            assignments.iter().find(|assignment| assignment.region == record.region)
            && assignment.writer_member == local_member
        {
            let lease = self.member.acquire(&id).map_err(failure)?;
            let tenant = terrabase::types::TenantId::try_new(&record.tenant_id).map_err(failure)?;
            if lease
                .engine()
                .lifecycle_state(&lease.engine().scope_for(&tenant))
                .map_err(failure)?
                .access_revoked()
            {
                return Ok(None);
            }
        }
        self.runtime.register_route(scope, id.clone()).map_err(failure)?;
        let source =
            self.member.storage_key_source(&id).ok_or("production key delivery missing")?;
        let snapshots = self.member.root().join("replication-snapshots");
        std::fs::create_dir_all(&snapshots).map_err(failure)?;
        let staging = snapshots.join(format!(
            "{}-{}-{}",
            record.database_id,
            proposal.epoch,
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_err(failure)?
                .as_nanos()
        ));
        let local_writer = assignments.iter().any(|assignment| {
            assignment.region == record.region && assignment.writer_member == local_member
        });
        Ok(Some((
            ProductionDatabaseAssignment {
                scope,
                location: self.member.root().join(&record.database_id),
                snapshot_staging: staging,
                snapshot_max_bytes: self.snapshot_max_bytes,
                keys: source,
                selected_regions: selected,
                remote_writers: assignments
                    .iter()
                    .filter(|a| a.region != record.region)
                    .map(|a| (a.region.clone(), a.writer_member.clone()))
                    .collect(),
            },
            (scope, record.database_id.clone(), local_writer),
        )))
    }

    async fn retire_stopped_writers(
        &self,
        records: &[crate::admission::AdmissionRecord],
        activation: &mut AdmissionActivation,
    ) -> Result<(), String> {
        let local_member = self.config.environment.member.to_text();
        for record in records {
            if !activation.active.contains(&record.database_id) {
                continue;
            }
            let scope = record_scope(record)?;
            let assignment = match self.journal.load_assignment(scope) {
                Ok(assignment) => assignment,
                Err(error @ terrabase_cluster::wal_route::WalAssignmentError::InvalidProof) => {
                    let _ = self
                        .lifecycle
                        .record_inventory(Err(readiness_inventory::InventoryError::Invalid));
                    return Err(failure(error));
                }
                Err(error) => return Err(failure(error)),
            };
            if assignment.writer.member().as_str() != local_member {
                continue;
            }
            let id =
                terrabase::member::DatabaseId::try_new(&record.database_id).map_err(failure)?;
            let lease = self.member.acquire(&id).map_err(failure)?;
            let tenant = terrabase::types::TenantId::try_new(&record.tenant_id).map_err(failure)?;
            let lifecycle = lease
                .engine()
                .lifecycle_state(&lease.engine().scope_for(&tenant))
                .map_err(failure)?;
            if !lifecycle.access_revoked() {
                continue;
            }
            if let Some(data) = self.lifecycle.get() {
                data.peers.remove_database(scope).await?;
            }
            self.runtime.retire_route(scope).map_err(failure)?;
            activation.active.remove(&record.database_id);
            activation.pending_publication.remove(&scope);
        }
        Ok(())
    }

    fn refresh_inventory(
        &self,
    ) -> Result<Option<(terrabase_tbep::proto::terrabase::v1::PlacementProposal, String)>, String>
    {
        use readiness_inventory::InventoryError;
        use terrabase_tbep::placement::PlacementError;
        let classify = |error| match error {
            PlacementError::Storage => InventoryError::Unavailable,
            _ => InventoryError::Invalid,
        };
        let loaded = (|| {
            let accepted = match self.journal.accepted_proposal() {
                Ok(accepted) => accepted,
                Err(PlacementError::NotFound) => return Ok(None),
                Err(error) => return Err(classify(error)),
            };
            let proposal = accepted.proposal.ok_or(InventoryError::Invalid)?;
            let proof = self.journal.accepted().map_err(classify)?;
            if proof.proposal_revision != accepted.revision {
                return Err(InventoryError::Invalid);
            }
            self.runtime.admit_membership(&*self.journal).map_err(|error| match error {
                terrabase_cluster::production::ProductionMemberError::MembershipSource(
                    terrabase_cluster::production::MembershipSourceError::Unavailable,
                ) => InventoryError::Unavailable,
                _ => InventoryError::Invalid,
            })?;
            let inventory =
                readiness_inventory::accepted_inventory(&self.runtime, &*self.journal, &proposal)?;
            Ok(Some((proposal, proof.proof_digest, inventory)))
        })();
        match loaded {
            Ok(Some((proposal, proof_digest, inventory))) => {
                self.lifecycle.record_inventory(Ok(inventory))?;
                Ok(Some((proposal, proof_digest)))
            }
            Ok(None) => {
                let _ = self.lifecycle.record_inventory(Err(InventoryError::Invalid));
                Ok(None)
            }
            Err(error) => self.lifecycle.record_inventory(Err(error)).map(|()| None),
        }
    }

    fn publish_pending(&self, activation: &mut AdmissionActivation) -> Result<(), String> {
        if activation.pending_publication.is_empty() {
            return Ok(());
        }
        let scopes = activation.pending_publication.keys().copied().collect::<Vec<_>>();
        let writers =
            activation.pending_publication.values().filter_map(Clone::clone).collect::<Vec<_>>();
        self.runtime.activate_routes(&scopes).map_err(failure)?;
        self.registry.activate(&writers)?;
        activation.pending_publication.clear();
        Ok(())
    }

    /// Validates and binds the customer API without starting background workers.
    ///
    /// # Errors
    /// Refuses mismatched data authorities, missing identity receiver, or listener failures.
    pub async fn prepare_public(
        &self,
        public: &crate::config::PublicApiConfig,
        state: Arc<crate::api::ApiState>,
        auth: Option<Arc<crate::auth_runtime::AuthRuntime>>,
    ) -> Result<PreparedProductionPublic, String> {
        if state.catalog.is_some()
            || state.control.is_some()
            || state
                .admitted_databases
                .as_ref()
                .is_none_or(|registry| !Arc::ptr_eq(registry, &self.registry))
            || state
                .replication_progress
                .as_ref()
                .is_none_or(|progress| !Arc::ptr_eq(progress, &self.progress))
        {
            return Err("public API does not share production data authority".into());
        }
        let mut router = crate::api::router(state.clone());
        if let Some(auth) = auth {
            router = router.merge(crate::auth_runtime::router(auth));
        }
        if let Some(closure) = &public.billing_closure {
            let key = crate::runtime::read_secret_key(&closure.key_file).map_err(failure)?;
            router = router.merge(
                crate::usage_closure_http::router(state.clone(), key).map_err(str::to_owned)?,
            );
        }
        let listener = tokio::net::TcpListener::bind(public.listen).await.map_err(failure)?;
        Ok(PreparedProductionPublic::new(
            listener,
            router,
            state,
            public.lifecycle_publication.clone(),
            self.registry.clone(),
        ))
    }

    /// Stops every data worker before the parent closes the root Member.
    ///
    /// # Errors
    /// Reports a failed bounded peer drain.
    pub async fn drain(&self) -> Result<(), String> {
        // Fence first, then join any retained activation before releasing data owners.
        self.lifecycle.begin_drain()?;
        let _activation = self.activation.lock().await;
        self.lifecycle.drain().await
    }
}

/// Keep routed streams fail-closed until the real production lifecycle test
/// proves envelope capture and restore end-to-end.
fn ensure_native_backup_shard_capture_supported(
    route_generation: Option<u64>,
) -> Result<(), String> {
    if route_generation.is_some() {
        return Err("native backup shard envelope capture is unavailable".into());
    }
    Ok(())
}

fn record_scope(record: &crate::admission::AdmissionRecord) -> Result<DatabaseRouteScope, String> {
    DatabaseRouteScope::try_new(&record.tenant_id, &record.project_id, &record.database_id)
        .map_err(failure)
}

fn build_admission_authority(
    journal: Arc<terrabase_tbep::placement::PlacementJournal>,
    usage: Arc<crate::usage_admission::UsageAdmissionJournal>,
    member: terrabase::member::Member,
    runtime: Arc<ProductionMemberRuntime>,
    local: String,
) -> crate::production_admission::AdmissionAuthority {
    Arc::new(move |record| {
        let accepted = match journal.accepted() {
            Ok(accepted) => accepted,
            Err(terrabase_tbep::placement::PlacementError::NotFound) => return Ok(None),
            Err(error) => return Err(failure(error)),
        };
        let scope = record_scope(record)?;
        let assignment = match journal.load_assignment(scope) {
            Ok(assignment) => assignment,
            Err(terrabase_cluster::wal_route::WalAssignmentError::InvalidProof) => return Ok(None),
            Err(error) => return Err(failure(error)),
        };
        runtime.admit_membership(&*journal).map_err(failure)?;
        let id = terrabase::member::DatabaseId::try_new(&record.database_id).map_err(failure)?;
        let role = if assignment.writer.member().as_str() == local {
            terrabase::member::DatabaseRole::Writer
        } else if assignment.follower.member().as_str() == local {
            terrabase::member::DatabaseRole::Follower
        } else {
            return Err("local member absent from accepted database assignment".into());
        };
        let origin = record.region.parse().map_err(|_| "invalid admitted Region")?;
        let registration =
            usage.resolve_with_residency(record, assignment.epoch, &accepted.proof_digest)?;
        if registration.is_none() && assignment.writer.member().as_str() != local {
            return Ok(None);
        }
        member
            .accept_database_role(
                &id,
                &VerifiedRole {
                    database: id.clone(),
                    accepted: terrabase::member::AcceptedDatabaseRole {
                        epoch: assignment.epoch,
                        role,
                    },
                },
            )
            .map_err(failure)?;
        if assignment.writer.member().as_str() == local {
            let lease = if registration.is_none() {
                let location = member.root().join(&record.database_id);
                match std::fs::symlink_metadata(&location) {
                    Ok(metadata) if metadata.file_type().is_dir() => {}
                    Ok(_) => return Err("existing database location is not a directory".into()),
                    Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
                    Err(error) => return Err(failure(error)),
                }
                member.acquire_existing(&id).map_err(failure)?
            } else {
                member.acquire(&id).map_err(failure)?
            };
            let tenant = terrabase::types::TenantId::try_new(&record.tenant_id).map_err(failure)?;
            if registration.is_none()
                && !crate::production_admission::lifecycle_allows_origin_activation(
                    &lease, &tenant,
                )?
            {
                return Ok(Some(crate::production_admission::AcceptedAdmission::Maintenance {
                    origin,
                    residency: record.residency.clone(),
                    epoch: assignment.epoch,
                    local_is_writer: true,
                }));
            }
        }
        let Some((partition, residency)) = registration else {
            return Ok(None);
        };
        Ok(Some(crate::production_admission::AcceptedAdmission::Active {
            partition,
            revision: accepted.proof_digest,
            origin,
            residency,
            epoch: assignment.epoch,
            local_is_writer: assignment.writer.member().as_str() == local,
        }))
    })
}

struct VerifiedRole {
    database: terrabase::member::DatabaseId,
    accepted: terrabase::member::AcceptedDatabaseRole,
}
impl terrabase::member::DatabaseRoleAuthority for VerifiedRole {
    fn accepted_role(
        &self,
        database: &terrabase::member::DatabaseId,
    ) -> Option<terrabase::member::AcceptedDatabaseRole> {
        (database == &self.database).then_some(self.accepted)
    }
}

#[cfg(test)]
mod lifecycle_tests {
    use super::*;

    #[test]
    fn native_backup_startup_rejects_installed_shard_routes() {
        assert_eq!(ensure_native_backup_shard_capture_supported(None), Ok(()));
        assert_eq!(
            ensure_native_backup_shard_capture_supported(Some(7)),
            Err("native backup shard envelope capture is unavailable".into())
        );
    }

    #[tokio::test]
    async fn readiness_inventory_preserves_outages_but_revokes_invalid_authority() {
        use crate::production::tests::{
            NoControlFrames, NoDatabaseAssignments, runtime_for_readiness,
        };
        use readiness_inventory::InventoryError;
        let (runtime, root, _) = runtime_for_readiness("readiness-cache");
        let runtime = Arc::new(runtime);
        let registry =
            Arc::new(crate::admitted_databases::AdmittedDatabaseRegistry::new(Arc::new(|| false)));
        let lifecycle =
            ProductionDataLifecycle::new(Arc::new(ReplicationProgress::default()), registry);
        lifecycle
            .start(runtime.clone(), &NoDatabaseAssignments, Vec::new(), Arc::new(NoControlFrames))
            .await
            .unwrap();
        assert!(!lifecycle.replication_readiness().accepted);
        assert_eq!(lifecycle.writer_host_readiness(), WriterHostReadiness::default());
        lifecycle.record_inventory(Ok(BTreeMap::new())).unwrap();
        let ready = lifecycle.replication_readiness();
        let hosts = lifecycle.writer_host_readiness();
        assert!(hosts.admitted && hosts.encrypted && hosts.journal_available);
        assert!(ready.accepted && ready.physical_ready && ready.causal_ready && ready.repair_ready);
        assert!(lifecycle.record_inventory(Err(InventoryError::Unavailable)).is_err());
        assert_eq!(lifecycle.replication_readiness(), ready);
        assert_eq!(lifecycle.writer_host_readiness(), hosts);
        assert!(lifecycle.record_inventory(Err(InventoryError::Invalid)).is_err());
        assert!(!lifecycle.replication_readiness().accepted);
        assert_eq!(lifecycle.writer_host_readiness(), WriterHostReadiness::default());
        lifecycle.drain().await.unwrap();
        assert!(!lifecycle.replication_readiness().accepted);
        assert_eq!(lifecycle.writer_host_readiness(), WriterHostReadiness::default());
        runtime.drain().await.storage.unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test]
    async fn terminal_worker_failure_releases_registry_leases_and_stays_failed() {
        let (registry, member, records, root) = crate::admitted_databases::tests::fixture();
        let registry = Arc::new(registry);
        let progress = Arc::new(ReplicationProgress::default());
        let lifecycle = ProductionDataLifecycle::new(progress.clone(), registry.clone());
        let failure = "terminal peer failure";
        let data = ProductionDataRuntime {
            runtime: std::sync::Weak::new(),
            peers: crate::replication_peers::failed_peer_owner(failure),
            progress,
        };
        assert!(lifecycle.data.set(data).is_ok());
        assert_eq!(registry.retained_snapshot().unwrap().len(), records.len());
        assert_eq!(lifecycle.drain().await.unwrap_err(), failure);
        assert!(registry.retained_snapshot().unwrap().is_empty());
        assert!(registry.resolve(&records[0].database_id).is_err());
        member.close(std::time::Duration::from_secs(1)).expect("all registry leases released");
        assert_eq!(lifecycle.drain().await.unwrap_err(), failure);
        std::fs::remove_dir_all(root).unwrap();
    }
}
