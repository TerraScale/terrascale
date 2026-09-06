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
use terrabase::encryption::{
    DatabaseKeySource, DatabaseKeyVersion, KeyPurpose, KeyScope, KeyVersionMetadata,
    OciVaultDatabaseKeys, OciVaultKeyDelivery,
};
use terrabase_cluster::production::{DatabaseRouteScope, ProductionMemberRuntime};
use terrabase_cluster::wal_driver::{WalEngineHandle, WalWriterDriver};
use terrabase_cluster::wal_follower::WalTail;
use terrabase_cluster::wal_route::DurableWalAssignmentSource;
use terrabase_cluster::wal_snapshot::WalSnapshotSource;
use terrabase_tbep::production_causal::CausalDriver;
use terrabase_tbep::registry::BoundScope;

mod host_readiness;
mod public_server;
mod readiness_inventory;
mod transition_retirement;
#[cfg(test)]
mod transition_retirement_tests;

pub use host_readiness::WriterHostReadiness;
pub use public_server::{PreparedProductionPublic, PublicServingStatus};
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
            draining: AtomicBool::new(false),
            progress,
            registry,
            expected: Mutex::new(None),
        }
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
        self.draining.store(true, Ordering::Release);
        self.progress.fence();
        if let Some(data) = self.data.get() {
            data.begin_drain();
        }
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
        let physical = if is_writer {
            let lease: Arc<dyn WalEngineHandle> =
                Arc::new(runtime.acquire_replication(&database.scope).map_err(failure)?);
            lease.engine().activate_origin_epoch(assignment.epoch).map_err(failure)?;
            let snapshot = WalSnapshotSource::capture(
                binding.clone(),
                lease.clone(),
                database.snapshot_staging,
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
            PhysicalDriver::BootstrapWriter {
                writer: WalWriterDriver::new(binding, lease, tail),
                source: snapshot,
            }
        } else {
            let mut receiver =
                SnapshotReceiver::new(binding, database.location, database.snapshot_max_bytes);
            receiver.keys = Some(database.keys);
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
            PhysicalDriver::BootstrapFollower(receiver)
        };
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
    active: BTreeSet<String>,
    pending_publication: BTreeMap<DatabaseRouteScope, Option<String>>,
    lifecycle: Arc<ProductionDataLifecycle>,
    dispatcher: Arc<dyn crate::replication_peers::PeerControlDispatcher>,
    usage: Arc<crate::usage_admission::UsageAdmissionJournal>,
    snapshot_max_bytes: u64,
    registry: Arc<crate::admitted_databases::AdmittedDatabaseRegistry>,
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
        let usage = Arc::new(crate::usage_admission::UsageAdmissionJournal::open(
            member.root(),
            config.environment.name.as_str().to_owned(),
            local.clone(),
            runtime.transport().config().local_region().as_str().to_owned(),
            journal.clone(),
        )?);
        let usage_authority = usage.clone();
        let role_member = member.clone();
        let role_runtime = runtime.clone();
        let authority: crate::production_admission::AdmissionAuthority = Arc::new(move |record| {
            let accepted = match authority_journal.accepted() {
                Ok(accepted) => accepted,
                Err(terrabase_tbep::placement::PlacementError::NotFound) => return Ok(None),
                Err(error) => return Err(failure(error)),
            };
            let scope = record_scope(record)?;
            let assignment = match authority_journal.load_assignment(scope) {
                Ok(assignment) => assignment,
                Err(terrabase_cluster::wal_route::WalAssignmentError::InvalidProof) => {
                    return Ok(None);
                }
                Err(error) => return Err(failure(error)),
            };
            role_runtime.admit_membership(&*authority_journal).map_err(failure)?;
            let id =
                terrabase::member::DatabaseId::try_new(&record.database_id).map_err(failure)?;
            let role = if assignment.writer.member().as_str() == local {
                terrabase::member::DatabaseRole::Writer
            } else if assignment.follower.member().as_str() == local {
                terrabase::member::DatabaseRole::Follower
            } else {
                return Err("local member absent from accepted database assignment".into());
            };
            let origin = record.region.parse().map_err(|_| "invalid admitted Region")?;
            let partition =
                usage_authority.resolve(record, assignment.epoch, &accepted.proof_digest)?;
            if partition.is_none() && assignment.writer.member().as_str() != local {
                return Ok(None);
            }
            role_member
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
                let lease = if partition.is_none() {
                    let location = role_member.root().join(&record.database_id);
                    match std::fs::symlink_metadata(&location) {
                        Ok(metadata) if metadata.file_type().is_dir() => {}
                        Ok(_) => return Err("existing database location is not a directory".into()),
                        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                            return Ok(None);
                        }
                        Err(error) => return Err(failure(error)),
                    }
                    role_member.acquire_existing(&id).map_err(failure)?
                } else {
                    role_member.acquire(&id).map_err(failure)?
                };
                let tenant =
                    terrabase::types::TenantId::try_new(&record.tenant_id).map_err(failure)?;
                if partition.is_none()
                    && !crate::production_admission::lifecycle_allows_origin_activation(
                        &lease, &tenant,
                    )?
                {
                    return Ok(Some(crate::production_admission::AcceptedAdmission::Maintenance {
                        origin,
                        epoch: assignment.epoch,
                        local_is_writer: true,
                    }));
                }
            }
            let Some(partition) = partition else {
                // The private delivery listener must remain available while this
                // accepted scope waits for its durable billing registration.
                return Ok(None);
            };
            Ok(Some(crate::production_admission::AcceptedAdmission::Active {
                partition,
                revision: accepted.proof_digest,
                origin,
                epoch: assignment.epoch,
                local_is_writer: assignment.writer.member().as_str() == local,
            }))
        });
        let storage = crate::production_admission::storage_admission(
            member.clone(),
            registry.clone(),
            provider,
            authority,
            per_database,
            instance,
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
        Ok(Self {
            runtime,
            member,
            journal,
            progress: progress.clone(),
            records,
            host: Arc::new(host),
            config,
            active: BTreeSet::new(),
            pending_publication: BTreeMap::new(),
            lifecycle: Arc::new(ProductionDataLifecycle::new(progress, registry.clone())),
            dispatcher,
            usage,
            snapshot_max_bytes,
            registry,
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
    ) -> Result<Self, String> {
        let host = Arc::try_unwrap(self.host).map_err(|_| "admission host already shared")?;
        let preflight = self.registry.clone();
        let prepare = self.registry.clone();
        let stop = self.registry.clone();
        self.host = Arc::new(
            host.with_storage_stop_preflight(Arc::new(move |record, _request| {
                let protected = preflight.stop_preflight(record)?;
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

    /// Reconciles retained acceptance and starts the actual role-specific workers.
    ///
    /// # Errors
    /// Refuses changed authority, missing admitted records, or failed worker startup.
    pub async fn reconcile(&mut self) -> Result<(), String> {
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
        // A stopped writer remains in the immutable admission journal for
        // deletion/retry bookkeeping, but its live replication lane and
        // customer route must be retired after the durable lifecycle fence.
        // Do this before the active fast path below, otherwise a restart
        // reconciliation would leave the old lane serving indefinitely.
        let local_member = self.config.environment.member.to_text();
        for record in &records {
            if !self.active.contains(&record.database_id) {
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
                // Followers retain their physical maintenance ownership here;
                // follower retirement is a separate exclusive-file workflow.
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
            self.active.remove(&record.database_id);
            self.pending_publication.remove(&scope);
        }
        self.publish_pending()?;
        let mut databases = Vec::new();
        let mut newly_active = Vec::new();
        for record in records {
            if self.active.contains(&record.database_id) {
                continue;
            }
            let scope = record_scope(&record)?;
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
                continue;
            }
            if assignments
                .iter()
                .map(|assignment| assignment.region.clone())
                .collect::<BTreeSet<_>>()
                != selected
            {
                return Err("accepted database residency differs from admission".into());
            }
            let id =
                terrabase::member::DatabaseId::try_new(&record.database_id).map_err(failure)?;
            // Replay the durable record first. The callback independently
            // classifies revoked storage as maintenance-only and publishes its
            // retained lease without creating a route or writer epoch.
            self.host.admit(record.clone())?;
            if self.usage.resolve(&record, proposal.epoch, &proof_digest)?.is_none() {
                // Missing current usage keeps nonrevoked storage closed; a
                // revoked replay was already published by the callback above.
                continue;
            }
            // A deletion preparation or acceptance is a durable local fence.
            // Keep the admission manifest so the control plane can retry the
            // operation, but do not construct a normal replication lane for
            // this database.  The admission callback has already retained it
            // in the Member catalog without publishing it to customer routes.
            if let Some(assignment) =
                assignments.iter().find(|assignment| assignment.region == record.region)
                && assignment.writer_member == self.config.environment.member.to_text()
            {
                let lease = self.member.acquire(&id).map_err(failure)?;
                let tenant =
                    terrabase::types::TenantId::try_new(&record.tenant_id).map_err(failure)?;
                let lifecycle = lease
                    .engine()
                    .lifecycle_state(&lease.engine().scope_for(&tenant))
                    .map_err(failure)?;
                if lifecycle.access_revoked() {
                    continue;
                }
            }
            self.runtime.register_route(scope, id).map_err(failure)?;
            let keys = self.config.keys.as_ref().ok_or("production key delivery missing")?;
            let provider = OciVaultKeyDelivery::new(
                keys.socket.clone(),
                keys.orchestrator_uid,
                keys.socket_gid,
            )
            .map_err(failure)?;
            let key_scope = KeyScope::new_with_project(
                &record.environment,
                scope.tenant(),
                scope.project(),
                scope.database(),
                KeyPurpose::Data,
            )
            .map_err(failure)?;
            let source = OciVaultDatabaseKeys::new(
                provider,
                KeyVersionMetadata::new(
                    key_scope,
                    DatabaseKeyVersion::from_text(&record.key_version).map_err(failure)?,
                ),
                record.region.parse().map_err(|_| "invalid admitted Region")?,
            )
            .map_err(failure)?;
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
            databases.push(ProductionDatabaseAssignment {
                scope,
                location: self.member.root().join(&record.database_id),
                snapshot_staging: staging,
                snapshot_max_bytes: self.snapshot_max_bytes,
                keys: Arc::new(source),
                selected_regions: selected,
                remote_writers: assignments
                    .iter()
                    .filter(|assignment| assignment.region != record.region)
                    .map(|assignment| (assignment.region.clone(), assignment.writer_member.clone()))
                    .collect(),
            });
            let local_writer = assignments.iter().any(|assignment| {
                assignment.region == record.region && assignment.writer_member == local_member
            });
            newly_active.push((scope, record.database_id, local_writer));
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
            self.active.insert(database.clone());
            self.pending_publication.insert(scope, writer.then_some(database));
        }
        self.publish_pending()
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

    fn publish_pending(&mut self) -> Result<(), String> {
        if self.pending_publication.is_empty() {
            return Ok(());
        }
        let scopes = self.pending_publication.keys().copied().collect::<Vec<_>>();
        let writers =
            self.pending_publication.values().filter_map(Clone::clone).collect::<Vec<_>>();
        self.runtime.activate_routes(&scopes).map_err(failure)?;
        self.registry.activate(&writers)?;
        self.pending_publication.clear();
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
        self.lifecycle.drain().await
    }
}

fn record_scope(record: &crate::admission::AdmissionRecord) -> Result<DatabaseRouteScope, String> {
    DatabaseRouteScope::try_new(&record.tenant_id, &record.project_id, &record.database_id)
        .map_err(failure)
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
