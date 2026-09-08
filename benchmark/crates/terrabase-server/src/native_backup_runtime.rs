//! Production owner for the shared native backup fleet.
//!
//! The startup composition supplies already admitted engines, database-bound
//! key providers, independently configured object destinations, and placement
//! locks.  This module owns only fleet admission and lifecycle fencing.  It
//! intentionally has no default provider, key, filesystem, or subprocess
//! fallback.

use std::collections::BTreeMap;
use std::sync::{Arc, Mutex};

use terrabase::backup::object_store::Provider;
pub use terrabase::backup::worker::FleetError;
use terrabase::backup::worker::{
    BackupEngineHandle, BackupKeys, FleetLimits, NativeBackupFleet, StreamConfig,
};
use terrabase::backup::{
    BackupEnvelope, BackupEnvelopeParticipant, BackupManifest, PlacementLock,
    object_store::ObjectDestination,
};
use terrabase::engine::Engine;
use terrabase_tbep::member_host::MemberHost;

const MAX_RETAINED_INCIDENTS: usize = 1024;

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
    /// Every destination retained by the durable policy history, including
    /// destinations retired from the current policy.
    pub historical_destinations: Vec<(Provider, Arc<ObjectDestination>)>,
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
    /// A multi-participant envelope failed identity or manifest validation.
    Envelope,
}

impl std::fmt::Display for NativeBackupRuntimeError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Fleet(error) => write!(formatter, "native backup fleet: {error:?}"),
            Self::Poisoned => formatter.write_str("native backup fleet lock poisoned"),
            Self::Unavailable => formatter.write_str("native backup database unavailable"),
            Self::Envelope => formatter.write_str("native backup envelope validation failed"),
        }
    }
}

impl std::error::Error for NativeBackupRuntimeError {}

/// Shared production owner for all admitted database backup streams.
pub struct NativeBackupRuntime {
    fleet: Arc<Mutex<NativeBackupFleet>>,
    databases: Mutex<BTreeMap<String, PlacementLock>>,
    incidents: Mutex<Vec<(String, FleetError)>>,
}

impl NativeBackupRuntime {
    /// Binds ordered participant manifests to one admitted shard read cut.
    ///
    /// The caller must capture every manifest at the same common cut before
    /// calling this method. This seam performs no publication: the caller
    /// must invoke [`BackupEnvelope::verify_staged`] for every participant and
    /// atomically publish the envelope only after all staged files verify.
    pub fn assemble_shard_envelope(
        &self,
        cut: &terrabase::member::AdmittedShardReadCut,
        manifests: Vec<BackupManifest>,
    ) -> Result<BackupEnvelope, NativeBackupRuntimeError> {
        let route_generation = cut.route_generation.ok_or(NativeBackupRuntimeError::Envelope)?;
        if cut.participants.len() != manifests.len() || cut.participants.is_empty() {
            return Err(NativeBackupRuntimeError::Envelope);
        }
        let participants = cut
            .participants
            .iter()
            .zip(manifests)
            .map(|(participant, manifest)| BackupEnvelopeParticipant {
                participant_id: format!("shard-{:016x}", participant.identity.shard_id),
                database_id: participant.engine.database_id().to_string(),
                database_incarnation: cut.database_incarnation,
                route_generation,
                boundary: participant.snapshot.sequence().get(),
                manifest,
            })
            .collect();
        BackupEnvelope::assemble(
            cut.database_id.clone(),
            cut.database_incarnation,
            route_generation,
            participants,
        )
        .map_err(|_| NativeBackupRuntimeError::Envelope)
    }

    /// Captures every admitted physical participant at its retained serving
    /// cut and publishes one durable envelope catalog entry. Participant
    /// points are never reported as a complete logical point independently.
    pub async fn capture_shard_envelope(
        &self,
        cut: &terrabase::member::AdmittedShardReadCut,
    ) -> Result<BackupEnvelope, NativeBackupRuntimeError> {
        if cut.participants.is_empty() || cut.route_generation.is_none() {
            return Err(NativeBackupRuntimeError::Envelope);
        }
        let requested = cut
            .participants
            .iter()
            .map(|participant| {
                (participant.engine.database_id().to_string(), participant.snapshot.sequence())
            })
            .collect::<Vec<_>>();
        let mut fleet = self.fleet.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?;
        let manifests = fleet
            .capture_participants_at(&requested)
            .await
            .map_err(NativeBackupRuntimeError::Fleet)?;
        let envelope = self.assemble_shard_envelope(cut, manifests)?;
        fleet.publish_envelope(&envelope).await.map_err(NativeBackupRuntimeError::Fleet)?;
        Ok(envelope)
    }

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
        let mut databases = BTreeMap::new();
        for input in inputs {
            let database = input.engine.engine().database_id().to_string();
            let placement = input.placement.clone();
            fleet
                .attach(StreamConfig {
                    engine: input.engine,
                    keys: input.keys,
                    destinations: input.destinations,
                    historical_destinations: input.historical_destinations,
                    policy_revision: input.policy_revision,
                    retention_days: input.retention_days,
                    staging: input.staging,
                    placement: input.placement,
                })
                .map_err(NativeBackupRuntimeError::Fleet)?;
            databases.insert(database.clone(), placement);
        }
        Ok(Self {
            fleet: Arc::new(Mutex::new(fleet)),
            databases: Mutex::new(databases),
            incidents: Mutex::new(Vec::new()),
        })
    }

    /// Returns the shared fleet for observations and native recovery lanes.
    #[must_use]
    pub fn fleet(&self) -> Arc<Mutex<NativeBackupFleet>> {
        Arc::clone(&self.fleet)
    }

    /// Executes one bounded asynchronous fleet tick.
    pub async fn tick(&self) -> Result<Vec<(String, FleetError)>, NativeBackupRuntimeError> {
        let mut fleet = self.fleet.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?;
        let errors = fleet.sync_once().await;
        if !errors.is_empty() {
            let mut incidents =
                self.incidents.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?;
            incidents.extend(errors.iter().cloned());
            if incidents.len() > MAX_RETAINED_INCIDENTS {
                let excess = incidents.len() - MAX_RETAINED_INCIDENTS;
                incidents.drain(..excess);
            }
        }
        Ok(errors)
    }

    /// Runs recurring fleet synchronization until the caller signals shutdown.
    /// Each tick is serialized by the fleet owner; provider failures are
    /// retained as incidents and do not alter admitted write ownership.
    pub async fn supervise(
        self: Arc<Self>,
        period: std::time::Duration,
        mut shutdown: tokio::sync::watch::Receiver<bool>,
    ) -> Result<(), NativeBackupRuntimeError> {
        let mut interval = tokio::time::interval(period);
        loop {
            if *shutdown.borrow() {
                return Ok(());
            }
            tokio::select! {
                biased;
                changed = shutdown.changed() => {
                    if changed.is_err() || *shutdown.borrow() { return Ok(()); }
                }
                _ = interval.tick() => {
                    if *shutdown.borrow() {
                        return Ok(());
                    }
                    let runtime = Arc::clone(&self);
                    tokio::task::spawn_blocking(move || {
                        tokio::runtime::Handle::current().block_on(runtime.tick())
                    })
                    .await
                    .map_err(|_| NativeBackupRuntimeError::Unavailable)??;
                }
            }
        }
    }

    /// Returns provider failures observed by recurring synchronization.
    pub fn incidents(&self) -> Result<Vec<(String, FleetError)>, NativeBackupRuntimeError> {
        self.incidents
            .lock()
            .map(|incidents| incidents.clone())
            .map_err(|_| NativeBackupRuntimeError::Poisoned)
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
        self.validate_restore_placement(database, placement)?;
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
        self.validate_restore_placement(database, placement)?;
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
        self.validate_restore_placement(database, placement)?;
        let mut fleet = self.fleet.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?;
        fleet
            .restore_at_from_provider(database, provider, at_millis, target, identity)
            .await
            .map_err(NativeBackupRuntimeError::Fleet)
    }

    /// Dynamically admits one accepted stream under the shared fleet lock.
    pub fn attach(&self, input: NativeBackupInput) -> Result<(), NativeBackupRuntimeError> {
        let database = input.engine.engine().database_id().to_string();
        let placement = input.placement.clone();
        let mut fleet = self.fleet.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?;
        fleet
            .attach(StreamConfig {
                engine: input.engine,
                keys: input.keys,
                destinations: input.destinations,
                historical_destinations: input.historical_destinations,
                policy_revision: input.policy_revision,
                retention_days: input.retention_days,
                staging: input.staging,
                placement: input.placement,
            })
            .map_err(NativeBackupRuntimeError::Fleet)?;
        self.databases
            .lock()
            .map_err(|_| NativeBackupRuntimeError::Poisoned)?
            .insert(database, placement);
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
        self.databases
            .lock()
            .map_err(|_| NativeBackupRuntimeError::Poisoned)?
            .remove(database);
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
        for database in databases.keys() {
            fleet.detach(database);
        }
        self.databases.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?.clear();
        Ok(())
    }

    /// Returns whether this owner still has an admitted stream.
    #[must_use]
    pub fn contains(&self, database: &str) -> bool {
        self.databases.lock().map(|databases| databases.contains_key(database)).unwrap_or(false)
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
            .contains_key(database))
    }

    /// Reads current protection from every configured provider and the source
    /// journal frontier; cached scheduler health is never sufficient.
    pub fn is_ready(&self, database: &str) -> Result<bool, NativeBackupRuntimeError> {
        if !self
            .databases
            .lock()
            .map_err(|_| NativeBackupRuntimeError::Poisoned)?
            .contains_key(database)
        {
            return Err(NativeBackupRuntimeError::Unavailable);
        }
        let fleet = self.fleet.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?;
        Ok(fleet.observe_protection(database).map_err(NativeBackupRuntimeError::Fleet)?.protected)
    }

    fn validate_restore_placement(
        &self,
        database: &str,
        placement: &PlacementLock,
    ) -> Result<(), NativeBackupRuntimeError> {
        let databases = self.databases.lock().map_err(|_| NativeBackupRuntimeError::Poisoned)?;
        if databases.get(database) != Some(placement) {
            return Err(NativeBackupRuntimeError::Unavailable);
        }
        Ok(())
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
    use object_store::ObjectStore;
    use std::sync::atomic::{AtomicUsize, Ordering};

    fn commit_marker(host: &crate::admitted_databases::AdmittedDatabaseSnapshot, marker: &str) {
        let engine = host.database.host.engine();
        let tenant = terrabase::types::TenantId::try_new(&host.database.record.tenant_id).unwrap();
        let scope = engine.scope_for(&tenant);
        let snapshot = engine.read_snapshot(&scope).unwrap();
        let operation = format!("native-pitr-{marker}");
        let mut transaction = engine
            .begin(&scope, snapshot, terrabase::types::OperationId::try_new(&operation).unwrap())
            .unwrap();
        transaction
            .put(
                terrabase::types::Document::new(
                    terrabase::types::DocumentId::try_new("native-pitr-doc").unwrap(),
                    terrabase::types::CollectionName::try_new("native-pitr").unwrap(),
                    terrabase::types::DocumentValue::String(marker.into()),
                ),
                terrabase::types::ExpectedVersion::Any,
            )
            .unwrap();
        assert!(matches!(
            transaction.commit(terrabase::types::LocalDurability::Synced),
            terrabase::CommitOutcome::Committed(_)
        ));
    }

    #[derive(Debug)]
    struct BlockingStore {
        inner: Arc<object_store::memory::InMemory>,
        entered: tokio::sync::Notify,
        release: tokio::sync::Notify,
        puts: AtomicUsize,
    }

    impl std::fmt::Display for BlockingStore {
        fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
            formatter.write_str("blocking test object store")
        }
    }

    #[async_trait::async_trait]
    impl object_store::ObjectStore for BlockingStore {
        async fn put_opts(
            &self,
            location: &object_store::path::Path,
            payload: object_store::PutPayload,
            opts: object_store::PutOptions,
        ) -> object_store::Result<object_store::PutResult> {
            let put_number = self.puts.fetch_add(1, Ordering::Relaxed);
            if put_number == 0 {
                self.entered.notify_one();
                self.release.notified().await;
            }
            self.inner.put_opts(location, payload, opts).await
        }

        async fn put_multipart_opts(
            &self,
            location: &object_store::path::Path,
            opts: object_store::PutMultipartOptions,
        ) -> object_store::Result<Box<dyn object_store::MultipartUpload>> {
            self.inner.put_multipart_opts(location, opts).await
        }

        async fn get_opts(
            &self,
            location: &object_store::path::Path,
            opts: object_store::GetOptions,
        ) -> object_store::Result<object_store::GetResult> {
            self.inner.get_opts(location, opts).await
        }

        async fn delete(&self, location: &object_store::path::Path) -> object_store::Result<()> {
            self.inner.delete(location).await
        }

        fn list(
            &self,
            prefix: Option<&object_store::path::Path>,
        ) -> futures_util::stream::BoxStream<'static, object_store::Result<object_store::ObjectMeta>>
        {
            self.inner.list(prefix)
        }

        async fn list_with_delimiter(
            &self,
            prefix: Option<&object_store::path::Path>,
        ) -> object_store::Result<object_store::ListResult> {
            self.inner.list_with_delimiter(prefix).await
        }

        async fn copy(
            &self,
            from: &object_store::path::Path,
            to: &object_store::path::Path,
        ) -> object_store::Result<()> {
            self.inner.copy(from, to).await
        }

        async fn copy_if_not_exists(
            &self,
            from: &object_store::path::Path,
            to: &object_store::path::Path,
        ) -> object_store::Result<()> {
            self.inner.copy_if_not_exists(from, to).await
        }
    }

    fn runtime_fixture(
        label: &str,
    ) -> (
        Arc<NativeBackupRuntime>,
        Arc<crate::admitted_databases::AdmittedDatabaseRegistry>,
        terrabase::member::Member,
        std::path::PathBuf,
        String,
    ) {
        runtime_fixture_with_store(label, Arc::new(object_store::memory::InMemory::new()))
    }

    fn runtime_fixture_with_store(
        label: &str,
        store: Arc<dyn object_store::ObjectStore>,
    ) -> (
        Arc<NativeBackupRuntime>,
        Arc<crate::admitted_databases::AdmittedDatabaseRegistry>,
        terrabase::member::Member,
        std::path::PathBuf,
        String,
    ) {
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
        let runtime = NativeBackupRuntime::new(
            FleetLimits {
                streams: 1,
                staging_bytes_per_stream: 16 * 1024 * 1024,
                inventory_bytes_per_stream: 16 * 1024 * 1024,
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
                        store,
                        4 * 1024 * 1024,
                        8 * 1024 * 1024,
                    )),
                )],
                historical_destinations: Vec::new(),
                policy_revision: "supervisor-policy".into(),
                retention_days: 1,
                staging: root.join(label),
                placement: PlacementLock::explicit([record.region.parse().unwrap()]).unwrap(),
            }],
        )
        .unwrap();
        let database = record.database_id.clone();
        (Arc::new(runtime), registry, member, root, database)
    }

    #[tokio::test]
    async fn pre_signalled_supervisor_does_not_tick_and_drain_releases_host_lease() {
        let (runtime, registry, member, root, database) = runtime_fixture("supervisor-pre-stop");
        let (shutdown, receiver) = tokio::sync::watch::channel(true);
        Arc::clone(&runtime)
            .supervise(std::time::Duration::from_millis(1), receiver)
            .await
            .unwrap();
        let staging = root.join("supervisor-pre-stop");
        assert!(staging.exists());
        assert!(std::fs::read_dir(staging).unwrap().next().is_none());
        assert!(runtime.contains(&database));

        registry.pause(&database, "supervisor-drain").unwrap();
        runtime.drain().unwrap();
        assert!(!runtime.contains(&database));
        assert!(registry.reserve_restore(&database).is_ok());
        drop(shutdown);
        drop(registry);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test]
    async fn shutdown_waits_for_in_flight_provider_tick() {
        let store = Arc::new(BlockingStore {
            inner: Arc::new(object_store::memory::InMemory::new()),
            entered: tokio::sync::Notify::new(),
            release: tokio::sync::Notify::new(),
            puts: AtomicUsize::new(0),
        });
        let (runtime, registry, member, root, database) = runtime_fixture_with_store(
            "supervisor-blocked",
            Arc::clone(&store) as Arc<dyn object_store::ObjectStore>,
        );
        let (shutdown, receiver) = tokio::sync::watch::channel(false);
        let task = tokio::spawn(
            Arc::clone(&runtime).supervise(std::time::Duration::from_millis(1), receiver),
        );
        store.entered.notified().await;
        assert_eq!(store.puts.load(Ordering::Relaxed), 1);
        shutdown.send(true).unwrap();
        assert!(!task.is_finished());
        assert!(runtime.contains(&database));
        store.release.notify_one();
        task.await.unwrap().unwrap();
        let puts_after_tick = store.puts.load(Ordering::Relaxed);
        tokio::task::yield_now().await;
        assert_eq!(store.puts.load(Ordering::Relaxed), puts_after_tick);
        runtime.drain().unwrap();
        assert!(!runtime.contains(&database));
        drop(registry);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test]
    async fn one_native_fleet_captures_two_encrypted_databases_and_restores_content() {
        use terrabase::operations::{QueryInput, SqlQueryText};
        let (registry, member, records, root) = crate::admitted_databases::tests::fixture();
        let registry = Arc::new(registry);
        let snapshots = registry.retained_snapshot().unwrap();
        assert_eq!(snapshots.len(), 2);
        for snapshot in &snapshots {
            commit_marker(snapshot, &snapshot.database.record.database_id);
        }
        let store = Arc::new(object_store::memory::InMemory::new());
        let mut inputs = Vec::new();
        for snapshot in &snapshots {
            let record = &snapshot.database.record;
            let scope = terrabase::member::DatabaseScope::new(
                &record.environment,
                record.tenant_id.parse().unwrap(),
                record.project_id.parse().unwrap(),
                record.database_id.parse().unwrap(),
                record.region.parse().unwrap(),
            )
            .unwrap();
            inputs.push(NativeBackupInput {
                engine: Arc::new(AdmittedBackupEngine::new(Arc::clone(&snapshot.database.host))),
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
                        Arc::clone(&store) as Arc<dyn ObjectStore>,
                        4 * 1024 * 1024,
                        8 * 1024 * 1024,
                    )),
                )],
                historical_destinations: Vec::new(),
                policy_revision: "native-pitr-fixture".into(),
                retention_days: 1,
                staging: root.join(&record.database_id),
                placement: PlacementLock::explicit([record.region.parse().unwrap()]).unwrap(),
            });
        }
        let runtime = NativeBackupRuntime::new(
            FleetLimits {
                streams: 2,
                staging_bytes_per_stream: 16 * 1024 * 1024,
                inventory_bytes_per_stream: 16 * 1024 * 1024,
                points_per_stream: 4,
            },
            inputs,
        )
        .unwrap();
        assert!(runtime.attached(&records[0].database_id).unwrap());
        assert!(runtime.attached(&records[1].database_id).unwrap());
        for record in &records {
            runtime
                .capture(&record.database_id, &format!("capture-{}", record.database_id))
                .await
                .unwrap();
        }
        let points = runtime.retained_points(&records[0].database_id).unwrap();
        assert_eq!(points.len(), 1);
        let target = &snapshots[1];
        registry.pause(&target.database.record.database_id, "native-pitr-target").unwrap();
        runtime.suspend_for_restore(&target.database.record.database_id).unwrap();
        let target_path = root.join("native-pitr-restored-target");
        let target_tenant =
            terrabase::types::TenantId::try_new(&target.database.record.tenant_id).unwrap();
        let target_owner = target.database.host.engine().database_owner().clone();
        let target_scope = terrabase::member::DatabaseScope::new(
            &target.database.record.environment,
            target.database.record.tenant_id.parse().unwrap(),
            target.database.record.project_id.parse().unwrap(),
            target.database.record.database_id.parse().unwrap(),
            target.database.record.region.parse().unwrap(),
        )
        .unwrap();
        let target_config = terrabase::engine::EngineConfig::durable(&target_path)
            .unwrap()
            .with_owner(target_owner)
            .with_storage_keys(
                crate::test_keys::storage_keys(&target_scope)
                    .remove(
                        &terrabase::member::DatabaseId::try_new(
                            &target.database.record.database_id,
                        )
                        .unwrap(),
                    )
                    .unwrap(),
            );
        let placement =
            PlacementLock::explicit([target.database.record.region.parse().unwrap()]).unwrap();
        let point = &points[0];
        let identity = terrabase::backup::RestoreIdentity {
            operation_id: "native-pitr-content".into(),
            source_database_id: records[0].database_id.clone(),
            target_database_id: records[1].database_id.clone(),
            provider: "oci".into(),
            selection: point.identity().to_owned(),
            expected_manifest_digest: point.identity().to_owned(),
            expected_journal_boundary: point.boundary_sequence().get(),
            restore_epoch: 17,
        };
        let wrong_placement =
            PlacementLock::explicit([terrabase::regions::RegionCode::FRA]).unwrap();
        assert_eq!(
            runtime
                .restore_from_provider(
                    &records[0].database_id,
                    terrabase::backup::object_store::Provider::Oci,
                    Some(point.identity()),
                    &target_config,
                    "native-pitr-wrong-placement",
                    &wrong_placement,
                    &identity,
                )
                .await,
            Err(NativeBackupRuntimeError::Unavailable)
        );
        let receipt = runtime
            .restore_from_provider(
                &records[0].database_id,
                terrabase::backup::object_store::Provider::Oci,
                Some(point.identity()),
                &target_config,
                "native-pitr-content",
                &placement,
                &identity,
            )
            .await
            .unwrap();
        assert_eq!(receipt.selected_identity(), point.identity());
        let restored = terrabase::Engine::open(target_config).unwrap();
        let scope = restored.scope_for(&target_tenant);
        let snapshot = restored.read_snapshot(&scope).unwrap();
        let plan = restored
            .queries()
            .compile(QueryInput::sql(
                SqlQueryText::try_new(
                    "SELECT value FROM native-pitr WHERE _id = 'native-pitr-doc'",
                )
                .unwrap(),
            ))
            .unwrap();
        let row = restored.queries().execute(&plan, &snapshot).unwrap();
        assert_eq!(row.rows().len(), 1);
        drop(restored);
        runtime.drain().unwrap();
        drop(registry);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }
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
                staging_bytes_per_stream: 16 * 1024 * 1024,
                inventory_bytes_per_stream: 16 * 1024 * 1024,
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
                historical_destinations: Vec::new(),
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
