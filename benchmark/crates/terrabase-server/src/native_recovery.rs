//! Durable native recovery-point operations bound to the running member fleet.

use crate::admitted_databases::AdmittedDatabaseRegistry;
use rusqlite::Connection;
use serde::{Deserialize, Serialize};
use std::sync::{Arc, Mutex};
use std::{fs::File, path::Path};
use terrabase::backup::worker::NativeBackupFleet;
use terrabase_tbep::member_host::MemberHost;
use tokio::sync::Notify;

mod admission;
mod checkpoint;
mod lifecycle;
mod persistence;
mod status;
use checkpoint::Checkpoint;

fn map_fleet_error(error: terrabase::backup::worker::FleetError) -> NativeRecoveryError {
    match error {
        terrabase::backup::worker::FleetError::Configuration => {
            NativeRecoveryError::VerificationFailed
        }
        terrabase::backup::worker::FleetError::Capture
        | terrabase::backup::worker::FleetError::Upload
        | terrabase::backup::worker::FleetError::StagingFull => NativeRecoveryError::Unavailable,
    }
}

/// Finite bounds for the durable recovery ledger.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct NativeRecoveryLimits {
    /// Maximum number of unfinished operations.
    pub pending_operations: usize,
    /// Maximum number of retained ledger rows.
    pub retained_operations: usize,
}

impl NativeRecoveryLimits {
    /// Validates finite bounds accepted by the worker.
    pub fn validate(self) -> Result<Self, NativeRecoveryError> {
        if !(1..=10_000).contains(&self.pending_operations)
            || self.pending_operations > self.retained_operations
            || self.retained_operations > 10_000
        {
            return Err(NativeRecoveryError::InvalidRequest);
        }
        Ok(self)
    }
}

/// Owner handle for the single serial recovery worker.
pub struct NativeRecoveryWorker {
    runtime: Arc<NativeRecoveryRuntime>,
    stop: tokio::sync::oneshot::Sender<()>,
    join: tokio::task::JoinHandle<bool>,
}

impl NativeRecoveryWorker {
    /// Signals the worker and waits for its owned task to finish.
    pub async fn shutdown(self) -> Result<(), NativeRecoveryError> {
        *self.runtime.lifecycle.lock().map_err(|_| NativeRecoveryError::Unavailable)? = true;
        let _ = self.stop.send(());
        let join = tokio::spawn(async move { self.join.await });
        let deadline = match tokio::time::timeout(std::time::Duration::from_secs(5), join).await {
            Ok(result) => result
                .map_err(|_| NativeRecoveryError::Unavailable)?
                .map_err(|_| NativeRecoveryError::Unavailable)?,
            Err(_) => {
                // The waiter retains the JoinHandle and worker lease while
                // an uncancellable provider call is still active.
                *self
                    .runtime
                    .worker_error
                    .lock()
                    .map_err(|_| NativeRecoveryError::Unavailable)? =
                    Some(NativeRecoveryError::Unavailable);
                return Err(NativeRecoveryError::Unavailable);
            }
        };
        if deadline {
            *self.runtime.worker_error.lock().map_err(|_| NativeRecoveryError::Unavailable)? =
                Some(NativeRecoveryError::Unavailable);
        }
        if self.runtime.worker_error.lock().map_err(|_| NativeRecoveryError::Unavailable)?.is_some()
        {
            return Err(NativeRecoveryError::Unavailable);
        }
        Ok(())
    }
}

/// Source content facts measured at an immutable, provider-verified recovery point.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize, utoipa::ToSchema)]
#[serde(deny_unknown_fields)]
pub struct RecoveryPointFacts {
    /// Authenticated archive identity; never inferred from a sequence alone.
    pub recovery_point_id: String,
    /// Exact owning source database incarnation.
    pub source_database_id: String,
    /// Immutable source journal boundary.
    #[schema(format = "uint64", minimum = 0)]
    pub journal_cut: u64,
    /// Clock used for fixed TTL visibility and matching restore verification.
    #[schema(format = "uint64", minimum = 0)]
    pub captured_at_millis: u64,
    /// Canonical logical BLAKE3 digest; excludes document revision counters.
    pub checksum: String,
    /// Current customer documents at the captured cut.
    #[schema(format = "uint64", minimum = 0)]
    pub row_count: u64,
    /// Immutable placement admitted by the database's member assignment.
    pub placement: Vec<String>,
    /// Original artifact-bound retention deadline, unaffected by plan downgrades.
    #[schema(format = "uint64", minimum = 0)]
    pub retained_until_millis: u64,
}

/// Safe progress state for a native recovery-point operation.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize, utoipa::ToSchema)]
#[serde(rename_all = "snake_case")]
pub enum NativeRecoveryPhase {
    /// The durable operation exists; no result has been returned.
    Accepted,
    /// Native capture and independently selected providers are being verified.
    Capturing,
    /// Canonical logical state is being measured at the captured boundary.
    Measuring,
    /// All facts are durable and available to authorized consumers.
    Complete,
    /// Durable work remains discoverable for an explicit retry.
    Failed,
}

/// Compact authoritative operation record. It contains no customer values or keys.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize, utoipa::ToSchema)]
#[serde(deny_unknown_fields)]
pub struct NativeRecoveryOperation {
    /// Stable operation identity, scoped to one source and retry key.
    pub operation_id: String,
    /// Exact source database incarnation.
    pub source_database_id: String,
    /// Current execution phase.
    pub phase: NativeRecoveryPhase,
    /// Verified facts only after completion.
    pub facts: Option<RecoveryPointFacts>,
    /// Safe failure classification, never provider error text.
    pub error: Option<String>,
}

/// Native recovery dependency or execution refusal, with no provider details.
#[derive(Debug, Clone, Copy, Eq, PartialEq)]
pub enum NativeRecoveryError {
    /// The admitted native fleet or its durable catalog cannot be reached.
    Unavailable,
    /// The requested source or retry identity is malformed or mismatched.
    InvalidRequest,
    /// The immutable archive or source boundary cannot be proved.
    VerificationFailed,
}

/// Durable source operations around the actual native provider-backed fleet.
/// The constructor never creates a mock fleet, local key, or reduced-security fallback.
pub struct NativeRecoveryRuntime {
    fleet: Arc<Mutex<NativeBackupFleet>>,
    operations: Mutex<Connection>,
    registry: Arc<AdmittedDatabaseRegistry>,
    limits: NativeRecoveryLimits,
    lifecycle: Mutex<bool>,
    notify: Arc<Notify>,
    worker_error: Mutex<Option<NativeRecoveryError>>,
    _worker_lease: Arc<File>,
}

impl NativeRecoveryRuntime {
    /// Opens durable native recovery operations for the admitted worker fleet.
    ///
    /// # Errors
    /// Refuses unsafe paths and inaccessible SQLite state; callers supply the real fleet.
    pub fn new(
        root: &Path,
        fleet: Arc<Mutex<NativeBackupFleet>>,
        registry: Arc<AdmittedDatabaseRegistry>,
        limits: NativeRecoveryLimits,
    ) -> Result<(Arc<Self>, NativeRecoveryWorker), NativeRecoveryError> {
        let limits = limits.validate()?;
        let executor =
            tokio::runtime::Handle::try_current().map_err(|_| NativeRecoveryError::Unavailable)?;
        crate::export_api::ensure_no_symlink_components(root)
            .map_err(|_| NativeRecoveryError::Unavailable)?;
        std::fs::create_dir_all(root).map_err(|_| NativeRecoveryError::Unavailable)?;
        let path = root.join("recovery.sqlite");
        crate::export_api::ensure_no_symlink_components(&path)
            .map_err(|_| NativeRecoveryError::Unavailable)?;
        let connection = Connection::open(path).map_err(|_| NativeRecoveryError::Unavailable)?;
        connection
            .busy_timeout(std::time::Duration::from_secs(5))
            .map_err(|_| NativeRecoveryError::Unavailable)?;
        connection.execute_batch("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS native_recovery_operations(
                operation_id TEXT PRIMARY KEY NOT NULL,
                source_database_id TEXT NOT NULL,
                phase TEXT NOT NULL DEFAULT 'accepted',
                record BLOB NOT NULL CHECK(length(record)<=262144),
                checkpoint BLOB CHECK(checkpoint IS NULL OR length(checkpoint)<=262144)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS native_recovery_source ON native_recovery_operations(source_database_id,operation_id);")
            .map_err(|_| NativeRecoveryError::Unavailable)?;
        // Reject unsupported ledgers without inventing execution phases for old rows.
        connection
            .prepare("SELECT operation_id, source_database_id, phase, record, checkpoint FROM native_recovery_operations LIMIT 0")
            .map_err(|_| NativeRecoveryError::Unavailable)?;
        for parent in root.ancestors().filter(|path| !path.as_os_str().is_empty()) {
            std::fs::File::open(parent)
                .and_then(|file| file.sync_all())
                .map_err(|_| NativeRecoveryError::Unavailable)?;
        }
        // A live worker owns this ledger exclusively.  The marker remains in
        // place while an uncancellable provider call outlives shutdown.
        let worker_lease = File::options()
            .create(true)
            .write(true)
            .open(root.join("recovery.worker.lock"))
            .map_err(|_| NativeRecoveryError::Unavailable)?;
        worker_lease.try_lock().map_err(|_| NativeRecoveryError::Unavailable)?;
        let runtime = Arc::new(Self {
            fleet,
            operations: Mutex::new(connection),
            registry,
            limits,
            lifecycle: Mutex::new(false),
            notify: Arc::new(Notify::new()),
            worker_error: Mutex::new(None),
            _worker_lease: Arc::new(worker_lease),
        });
        let (stop, stopped) = tokio::sync::oneshot::channel();
        let worker_runtime = Arc::clone(&runtime);
        let notify = Arc::clone(&runtime.notify);
        let join = executor.spawn(async move {
            let result =
                lifecycle::supervise::<NativeRecoveryError, _, _>(stopped, notify, move || {
                    let runtime = Arc::clone(&worker_runtime);
                    let observed = Arc::clone(&runtime);
                    async move {
                        let result = tokio::task::spawn_blocking(move || runtime.process_pending())
                            .await
                            .map_err(|_| NativeRecoveryError::Unavailable)?;
                        if let Ok(_) = result {
                            *observed
                                .worker_error
                                .lock()
                                .map_err(|_| NativeRecoveryError::Unavailable)? = None;
                        } else if let Err(error) = result {
                            *observed
                                .worker_error
                                .lock()
                                .map_err(|_| NativeRecoveryError::Unavailable)? = Some(error);
                        }
                        result
                    }
                })
                .await;
            result
        });
        runtime.notify.notify_one();
        Ok((runtime.clone(), NativeRecoveryWorker { runtime, stop, join }))
    }

    /// Durably admits one scoped capture and continues after request disconnection.
    ///
    /// # Errors
    /// Refuses invalid identity, full worker capacity, and durable admission failures.
    pub async fn capture(
        self: &Arc<Self>,
        database: String,
        idempotency_key: String,
    ) -> Result<NativeRecoveryOperation, NativeRecoveryError> {
        if database.is_empty()
            || database.len() > 256
            || database.chars().any(char::is_control)
            || idempotency_key.is_empty()
            || idempotency_key.len() > 256
            || idempotency_key.chars().any(char::is_control)
        {
            return Err(NativeRecoveryError::InvalidRequest);
        }
        let admitted = self
            .registry
            .resolve(&database)
            .map_err(|_| NativeRecoveryError::Unavailable)?
            .ok_or(NativeRecoveryError::InvalidRequest)?;
        let source = admitted.host.engine().database_id().to_text();
        if source != database {
            return Err(NativeRecoveryError::InvalidRequest);
        }
        let mut digest = blake3::Hasher::new();
        digest.update(b"terrabase/native-recovery-operation/v1\0");
        for value in [&source, &idempotency_key] {
            digest.update(&(value.len() as u64).to_be_bytes());
            digest.update(value.as_bytes());
        }
        let operation_id = digest.finalize().to_hex().to_string();
        let state = tokio::task::spawn_blocking({
            let runtime = Arc::clone(self);
            move || {
                let proposed = NativeRecoveryOperation {
                    operation_id: operation_id.clone(),
                    source_database_id: source.clone(),
                    phase: NativeRecoveryPhase::Accepted,
                    facts: None,
                    error: None,
                };
                let _lifecycle =
                    runtime.lifecycle.lock().map_err(|_| NativeRecoveryError::Unavailable)?;
                if *_lifecycle {
                    return Err(NativeRecoveryError::Unavailable);
                }
                let mut connection =
                    runtime.operations.lock().map_err(|_| NativeRecoveryError::Unavailable)?;
                let admitted =
                    admission::admit_operation(&mut connection, runtime.limits, proposed)?;
                runtime.notify.notify_one();
                Ok(admitted)
            }
        })
        .await
        .map_err(|_| NativeRecoveryError::Unavailable)??;
        Ok(state)
    }

    /// Reads compact state without starting or cancelling work.
    ///
    /// # Errors
    /// Refuses invalid rows or unavailable durable storage.
    pub fn status(
        &self,
        source: &str,
        operation: &str,
    ) -> Result<Option<NativeRecoveryOperation>, NativeRecoveryError> {
        let connection = self.operations.lock().map_err(|_| NativeRecoveryError::Unavailable)?;
        status::read(&connection, source, operation)
    }

    fn process_pending(self: &Arc<Self>) -> Result<bool, NativeRecoveryError> {
        let pending = {
            let connection =
                self.operations.lock().map_err(|_| NativeRecoveryError::Unavailable)?;
            let mut statement = connection.prepare("SELECT source_database_id, operation_id, phase, record FROM native_recovery_operations ORDER BY operation_id").map_err(|_| NativeRecoveryError::Unavailable)?;
            let rows = statement
                .query_map([], |row| {
                    Ok((
                        row.get::<_, String>(0)?,
                        row.get::<_, String>(1)?,
                        row.get::<_, String>(2)?,
                        row.get::<_, Vec<u8>>(3)?,
                    ))
                })
                .map_err(|_| NativeRecoveryError::Unavailable)?;
            let mut found = None;
            let mut malformed = None;
            for row in rows {
                let (source, operation, phase, bytes) =
                    row.map_err(|_| NativeRecoveryError::Unavailable)?;
                let state = match serde_json::from_slice::<NativeRecoveryOperation>(&bytes) {
                    Ok(state) => state,
                    Err(_) => {
                        malformed = Some((source, operation));
                        break;
                    }
                };
                if status::validate(&state, &source, &operation, &phase).is_err() {
                    malformed = Some((source, operation));
                    break;
                }
                if matches!(phase.as_str(), "accepted" | "capturing" | "measuring") {
                    found = Some((source, operation, state));
                    break;
                }
            }
            (found, malformed)
        };
        let (pending, malformed) = pending;
        if let Some((source, operation)) = malformed {
            let connection =
                self.operations.lock().map_err(|_| NativeRecoveryError::Unavailable)?;
            persistence::fail_row(
                &connection,
                &operation,
                &source,
                "native recovery record verification failed; retry the existing operation",
            )?;
            self.notify.notify_one();
            return Ok(true);
        }
        let Some((source, operation, _state)) = pending else {
            return Ok(false);
        };
        let admitted = match self.registry.resolve(&source) {
            Ok(Some(admitted)) => admitted,
            Ok(None) | Err(_) => return Err(NativeRecoveryError::Unavailable),
        };
        if admitted.host.engine().database_id().to_text() != source {
            let connection =
                self.operations.lock().map_err(|_| NativeRecoveryError::Unavailable)?;
            persistence::fail_row(
                &connection,
                &operation,
                &source,
                "native recovery record verification failed; retry the existing operation",
            )?;
            return Ok(true);
        }
        let placement = admitted.record.regions.clone();
        match self.run_capture(&admitted.host, placement, &source, &operation) {
            Ok(()) => {}
            Err(NativeRecoveryError::Unavailable) => return Err(NativeRecoveryError::Unavailable),
            Err(error) => {
                let Some(mut latest) = self.status(&source, &operation)? else {
                    return Err(NativeRecoveryError::Unavailable);
                };
                if !matches!(
                    latest.phase,
                    NativeRecoveryPhase::Complete | NativeRecoveryPhase::Failed
                ) {
                    latest.phase = NativeRecoveryPhase::Failed;
                    latest.error = Some(
                        "native recovery capture verification failed; retry the existing operation"
                            .into(),
                    );
                    self.save_record_only(&latest)?;
                }
                if !matches!(
                    latest.phase,
                    NativeRecoveryPhase::Failed | NativeRecoveryPhase::Complete
                ) {
                    return Err(error);
                }
            }
        }
        self.notify.notify_one();
        Ok(true)
    }

    fn save(
        &self,
        operation: &NativeRecoveryOperation,
        checkpoint: Option<&Checkpoint>,
    ) -> Result<(), NativeRecoveryError> {
        let connection = self.operations.lock().map_err(|_| NativeRecoveryError::Unavailable)?;
        persistence::save(&connection, operation, checkpoint)
    }

    fn run_capture(
        &self,
        host: &MemberHost,
        placement: Vec<String>,
        source: &str,
        operation: &str,
    ) -> Result<(), NativeRecoveryError> {
        let mut state = self.status(source, operation)?.ok_or(NativeRecoveryError::Unavailable)?;
        if matches!(state.phase, NativeRecoveryPhase::Complete) {
            return Ok(());
        }
        if state.operation_id != operation || state.source_database_id != source {
            return Err(NativeRecoveryError::VerificationFailed);
        }
        let checkpoint = match self.load_checkpoint(source, operation) {
            Ok(value) => value,
            Err(error) => {
                state.phase = NativeRecoveryPhase::Failed;
                state.error = Some(
                    "native recovery verification failed; retry the existing operation".into(),
                );
                self.save_record_only(&state)?;
                return Err(error);
            }
        };
        let capture = || {
            let manifest = {
                let mut fleet = self.fleet.lock().map_err(|_| NativeRecoveryError::Unavailable)?;
                tokio::runtime::Handle::current()
                    .block_on(fleet.capture_snapshot(source))
                    .map_err(map_fleet_error)?
            };
            Ok(Checkpoint::from_manifest(&manifest, source, &placement))
        };
        let persist =
            |operation: &NativeRecoveryOperation, cp: Option<&Checkpoint>| self.save(operation, cp);
        let measure = |point: &Checkpoint| {
            let fleet = self.fleet.lock().map_err(|_| NativeRecoveryError::Unavailable)?;
            fleet
                .retained_points(source)
                .map_err(map_fleet_error)?
                .into_iter()
                .find(|manifest| point.matches_manifest(manifest))
                .ok_or(NativeRecoveryError::VerificationFailed)?;
            drop(fleet);
            let tenant = terrabase::types::TenantId::try_new(host.tenant())
                .map_err(|_| NativeRecoveryError::InvalidRequest)?;
            let stats = host
                .engine()
                .recovery_statistics(
                    &host.engine().scope_for(&tenant),
                    point.journal_cut,
                    point.captured_at_millis,
                    None,
                )
                .map_err(|_| NativeRecoveryError::VerificationFailed)?;
            if stats.journal_cut != point.journal_cut
                || stats.captured_at_millis != point.captured_at_millis
            {
                return Err(NativeRecoveryError::VerificationFailed);
            }
            Ok(RecoveryPointFacts {
                recovery_point_id: point.recovery_point_id.clone(),
                source_database_id: source.to_owned(),
                journal_cut: point.journal_cut,
                captured_at_millis: point.captured_at_millis,
                checksum: blake3::Hash::from(stats.checksum).to_hex().to_string(),
                row_count: stats.row_count,
                placement: placement.clone(),
                retained_until_millis: point.retained_until_millis,
            })
        };
        checkpoint::advance(&mut state, checkpoint, &placement, capture, persist, measure)
    }

    fn load_checkpoint(
        &self,
        source: &str,
        operation: &str,
    ) -> Result<Option<Checkpoint>, NativeRecoveryError> {
        let connection = self.operations.lock().map_err(|_| NativeRecoveryError::Unavailable)?;
        persistence::load_checkpoint(&connection, source, operation)
    }

    fn save_record_only(
        &self,
        operation: &NativeRecoveryOperation,
    ) -> Result<(), NativeRecoveryError> {
        let connection = self.operations.lock().map_err(|_| NativeRecoveryError::Unavailable)?;
        persistence::save_record_only(&connection, operation)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use terrabase::backup::worker::{FleetLimits, NativeBackupFleet};

    fn limits() -> NativeRecoveryLimits {
        NativeRecoveryLimits { pending_operations: 2, retained_operations: 4 }
    }

    fn fleet() -> Arc<Mutex<NativeBackupFleet>> {
        Arc::new(Mutex::new(
            NativeBackupFleet::new(FleetLimits {
                streams: 2,
                staging_bytes_per_stream: 3 * 1024 * 1024,
                points_per_stream: 2,
            })
            .unwrap(),
        ))
    }

    #[tokio::test]
    async fn real_constructor_idle_shutdown_closes_admission_fence() {
        let (registry, member, records, root) = crate::admitted_databases::tests::fixture();
        let registry = Arc::new(registry);
        let runtime_root = root.join("native-recovery-idle");
        let (runtime, worker) =
            NativeRecoveryRuntime::new(&runtime_root, fleet(), Arc::clone(&registry), limits())
                .unwrap();
        worker.shutdown().await.unwrap();
        assert_eq!(
            runtime.capture(records[0].database_id.clone(), "retry".into()).await,
            Err(NativeRecoveryError::Unavailable)
        );
        drop(member);
        let _ = std::fs::remove_dir_all(root);
    }

    #[tokio::test]
    async fn real_constructor_shutdown_drains_committed_accepted_row() {
        let (registry, member, records, root) = crate::admitted_databases::tests::fixture();
        let registry = Arc::new(registry);
        let runtime_root = root.join("native-recovery-drain");
        let (runtime, worker) =
            NativeRecoveryRuntime::new(&runtime_root, fleet(), Arc::clone(&registry), limits())
                .unwrap();
        let source = records[0].database_id.clone();
        let state = NativeRecoveryOperation {
            operation_id: "drain-operation".into(),
            source_database_id: source.clone(),
            phase: NativeRecoveryPhase::Accepted,
            facts: None,
            error: None,
        };
        {
            let connection = runtime.operations.lock().unwrap();
            connection
                .execute(
                    "INSERT INTO native_recovery_operations(operation_id,source_database_id,phase,record) VALUES(?1,?2,'accepted',?3)",
                    rusqlite::params![state.operation_id, source, serde_json::to_vec(&state).unwrap()],
                )
                .unwrap();
        }
        runtime.notify.notify_one();
        worker.shutdown().await.unwrap();
        let result = runtime.status(&state.source_database_id, &state.operation_id).unwrap();
        assert!(matches!(result.map(|value| value.phase), Some(NativeRecoveryPhase::Failed)));
        drop(member);
        let _ = std::fs::remove_dir_all(root);
    }
}
