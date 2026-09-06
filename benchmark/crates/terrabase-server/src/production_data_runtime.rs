//! Accepted database assignments assembled into the live, lease-owning data plane.
use crate::replication_peers::{PeerDatabase, PhysicalDriver, ReplicationPeers, SnapshotReceiver};
use crate::replication_runtime::ReplicationProgress;
use std::collections::{BTreeMap, BTreeSet};
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use terrabase::encryption::DatabaseKeySource;
use terrabase_cluster::production::{DatabaseRouteScope, ProductionMemberRuntime};
use terrabase_cluster::wal_driver::{WalEngineHandle, WalWriterDriver};
use terrabase_cluster::wal_follower::WalTail;
use terrabase_cluster::wal_route::DurableWalAssignmentSource;
use terrabase_cluster::wal_snapshot::WalSnapshotSource;
use terrabase_tbep::production_causal::CausalDriver;
use terrabase_tbep::registry::BoundScope;

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
    peers: ReplicationPeers,
    progress: Arc<ReplicationProgress>,
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
    ) -> Result<Self, String> {
        let peers = build_peer_databases(&runtime, &progress, source, databases).await?;
        let peers = ReplicationPeers::start(runtime, progress.clone(), peers)?;
        Ok(Self { peers, progress })
    }

    /// Live authenticated replication observations, never configuration-derived health.
    #[must_use]
    pub fn observations(&self) -> crate::replication_peers::PeerObservations {
        self.peers.observations()
    }

    /// Fences API durability waits, then drains workers and their Engine leases.
    ///
    /// # Errors
    /// Reports failed peer shutdown.
    pub async fn drain(self) -> Result<(), String> {
        self.progress.fence();
        self.peers.drain().await
    }
}

/// Builds actual role-specific lanes against the same root Member used by the API.
///
/// # Errors
/// Refuses any incomplete accepted assignment or failed encrypted snapshot capture.
pub async fn build_peer_databases(
    runtime: &Arc<ProductionMemberRuntime>,
    progress: &Arc<ReplicationProgress>,
    source: &dyn DurableWalAssignmentSource,
    databases: Vec<ProductionDatabaseAssignment>,
) -> Result<BTreeMap<String, Vec<PeerDatabase>>, String> {
    let mut lanes: BTreeMap<String, Vec<PeerDatabase>> = BTreeMap::new();
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
        progress.admit(database.scope, database.selected_regions).map_err(failure)?;
    }
    Ok(lanes)
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
    data: Option<ProductionDataRuntime>,
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
        usage_authority: UsageAdmissionAuthority,
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
            Ok(Some(crate::production_admission::AcceptedAdmission {
                partition: usage_authority(record, assignment.epoch, &accepted.proof_digest)?
                    .partition,
                revision: accepted.proof_digest,
                origin: record.region.parse().map_err(|_| "invalid admitted Region")?,
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
            declarations
                .register_database(record_scope(record)?, &record.region)
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
        )?;
        Ok(Self {
            runtime,
            member,
            journal,
            progress,
            records,
            host: Arc::new(host),
            config,
            active: BTreeSet::new(),
            data: None,
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
        self.data.as_ref().map(ProductionDataRuntime::observations)
    }

    /// Receipt ledger shared by writes and catch-up observations.
    #[must_use]
    pub fn progress(&self) -> Arc<ReplicationProgress> {
        self.progress.clone()
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
        let accepted = match self.journal.accepted_proposal() {
            Ok(accepted) => accepted,
            Err(terrabase_tbep::placement::PlacementError::NotFound) => return Ok(()),
            Err(error) => return Err(failure(error)),
        };
        let proposal = accepted.proposal.ok_or("accepted proposal missing")?;
        self.runtime.admit_membership(&*self.journal).map_err(failure)?;
        let records = self
            .records
            .lock()
            .map_err(|_| "admission registry poisoned")?
            .values()
            .cloned()
            .collect::<Vec<_>>();
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
            self.runtime.register_route(scope, id).map_err(failure)?;
            self.host.admit(record.clone())?;
            let keys = self.config.keys.as_ref().ok_or("production key delivery missing")?;
            use terrabase::encryption::{
                DatabaseKeyVersion, KeyPurpose, KeyScope, KeyVersionMetadata, OciVaultDatabaseKeys,
                OciVaultKeyDelivery,
            };
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
            newly_active.push(record.database_id);
        }
        if databases.is_empty() {
            return Ok(());
        }
        if let Some(data) = &self.data {
            let lanes =
                build_peer_databases(&self.runtime, &self.progress, &*self.journal, databases)
                    .await?;
            data.peers.add_databases(lanes).await?;
        } else {
            self.data = Some(
                ProductionDataRuntime::start(
                    self.runtime.clone(),
                    self.progress.clone(),
                    &*self.journal,
                    databases,
                )
                .await?,
            );
        }
        self.active.extend(newly_active);
        Ok(())
    }

    /// Serves the customer API and auth routes using the exact registry and receipt ledger.
    /// The parent supplies the configured API capabilities; this rejects a second catalog.
    ///
    /// # Errors
    /// Refuses mismatched data authorities, missing identity receiver, or listener failures.
    pub async fn serve_public(
        &self,
        public: &crate::config::PublicApiConfig,
        state: Arc<crate::api::ApiState>,
        auth: Option<Arc<crate::auth_runtime::AuthRuntime>>,
        shutdown: impl std::future::Future<Output = ()> + Send + 'static,
    ) -> Result<(), String> {
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
        let publication = public
            .lifecycle_publication
            .as_ref()
            .map(|config| {
                crate::lifecycle_publication::spawn(config, self.registry.clone()).map_err(failure)
            })
            .transpose()?;
        let listener = tokio::net::TcpListener::bind(public.listen).await.map_err(failure)?;
        let dispatcher = crate::api::spawn_webhook_dispatcher(&state);
        let migrations = crate::api::spawn_schema_migrations(&state);
        let result =
            axum::serve(listener, router).with_graceful_shutdown(shutdown).await.map_err(failure);
        if let Some(publication) = publication {
            publication.shutdown().await;
        }
        if let Some(exports) = &state.exports {
            exports.stop();
        }
        if let Some(dispatcher) = dispatcher {
            dispatcher.shutdown().await;
        }
        if let Some(migrations) = migrations {
            migrations.abort();
            let _ = migrations.await;
        }
        result
    }

    /// Stops every data worker before the parent closes the root Member.
    ///
    /// # Errors
    /// Reports a failed bounded peer drain.
    pub async fn drain(mut self) -> Result<(), String> {
        if let Some(data) = self.data.take() {
            data.drain().await?;
        }
        Ok(())
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

/// Trusted billing registration matching the accepted placement before any write can mint usage.
pub struct UsageAdmission {
    /// Canonical partition assigned by the durable billing stream authority.
    pub partition: String,
}
/// Resolves the exact immutable stream registration and accepted epoch/revision.
/// Implementations must verify the full selected-Region set in the admission record.
pub type UsageAdmissionAuthority = Arc<
    dyn Fn(&crate::admission::AdmissionRecord, u64, &str) -> Result<UsageAdmission, String>
        + Send
        + Sync,
>;
