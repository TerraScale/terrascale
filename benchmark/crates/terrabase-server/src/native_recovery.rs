//! Durable native recovery-point operations bound to the running member fleet.

use crate::admitted_databases::AdmittedDatabaseRegistry;
use rusqlite::{Connection, OptionalExtension};
use serde::{Deserialize, Serialize};
use std::future::Future;
use std::pin::Pin;
use std::sync::{Arc, Mutex};
use std::{fs::File, path::Path};
use terrabase::backup::worker::NativeBackupFleet;
use terrabase_tbep::member_host::MemberHost;
use tokio::sync::Notify;

mod admission;
mod checkpoint;
mod discovery;
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
    stop: tokio::sync::oneshot::Sender<tokio::time::Instant>,
    join: tokio::task::JoinHandle<bool>,
}

impl NativeRecoveryWorker {
    /// Signals the worker and waits without detaching its join handle.
    ///
    /// This is reserved for owned shutdown paths, where releasing the Member
    /// lease must wait for any uncancellable provider operation to finish.
    pub async fn await_completion(self) -> Result<(), NativeRecoveryError> {
        *self.runtime.lifecycle.lock().map_err(|_| NativeRecoveryError::Unavailable)? = true;
        let _ = self.stop.send(tokio::time::Instant::now());
        self.join.await.map_err(|_| NativeRecoveryError::Unavailable)?;
        Ok(())
    }

    /// Signals the worker and waits for its owned task to finish.
    pub async fn shutdown(self) -> Result<(), NativeRecoveryError> {
        *self.runtime.lifecycle.lock().map_err(|_| NativeRecoveryError::Unavailable)? = true;
        let deadline = tokio::time::Instant::now() + std::time::Duration::from_secs(5);
        let _ = self.stop.send(deadline);
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

/// Internal durable restore request; target admission occurs upstream.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct NativeRestoreRequest {
    /// Durable restore operation identity.
    pub operation_id: String,
    /// Authenticated source owner.
    pub source_owner: String,
    /// Authenticated source database identity.
    pub source_database_id: String,
    /// Selected recovery point identity.
    pub recovery_point_id: String,
    /// Selected backup manifest identity digest (SHA-256), distinct from the
    /// logical content BLAKE3 checksum measured after restore.
    pub recovery_point_digest: String,
    /// Authenticated target owner.
    pub target_owner: String,
    /// Upstream-admitted paused target identity.
    pub target_database_id: String,
    /// Source journal boundary.
    pub journal_cut: String,
    /// Restore epoch.
    pub restore_epoch: u64,
    /// Authority epoch retained from the verified lifecycle context.
    pub authority_epoch: u64,
    /// Authority lease retained from the verified lifecycle context.
    pub authority_lease: String,
    /// Accepted placement proof identity retained from the verified context.
    pub accepted_proof_digest: String,
    /// Original signed lifecycle mutation authorization, retained across observation renewals.
    pub operation_grant: Vec<u8>,
    /// Selected provider name.
    pub provider: String,
    /// Point or exact millisecond selection.
    pub selection: NativeRestoreSelection,
    /// Inherited immutable residency, equal for source and target at admission.
    pub residency: String,
    /// Inherited immutable placement.
    pub placement: Vec<String>,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
/// Inclusive selection bound to a retained recovery point.
pub enum NativeRestoreSelection {
    /// Named retained recovery point.
    PointId(String),
    /// Inclusive Unix millisecond cutoff.
    AtUnixMillis(u64),
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
/// Durable restore execution and reconciliation phase.
#[serde(rename_all = "snake_case")]
pub enum NativeRestorePhase {
    /// Request is durably admitted.
    Accepted,
    /// Provider restore is in progress.
    Restoring,
    /// Returned receipt is being checked.
    Verifying,
    /// Target marker and receipt are verified.
    Complete,
    /// Provider outcome is unknown and requires readback.
    Uncertain,
    /// Terminal failure requiring explicit retry.
    Failed,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
/// Durable request, phase and verified restore outcome.
pub struct NativeRestoreOperation {
    /// Immutable restore request identity.
    pub request: NativeRestoreRequest,
    /// Durable execution phase.
    pub phase: NativeRestorePhase,
    /// Secret-safe rendered receipt after successful restore.
    pub receipt: Option<NativeRestoreReceipt>,
    /// Safe failure classification.
    pub error: Option<String>,
}

/// Structured facts produced only after native restore verification.
#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub struct NativeRestoreReceipt {
    /// Authenticated selected manifest identity.
    pub selected_identity: String,
    /// Logical BLAKE3 checksum of restored content.
    pub logical_content_checksum: String,
    /// Number of restored logical documents.
    pub restored_documents: u64,
    /// Selected source journal boundary.
    pub journal_cut: u64,
}

#[derive(Clone, Debug, Eq, PartialEq)]
/// Verified result of native restore execution.
pub struct NativeRestoreOutcome {
    /// Authenticated native restore facts.
    pub receipt: NativeRestoreReceipt,
}

/// Production restore owner. Implementations reserve the target and publish
/// only after provider installation and authorized reacquisition.
pub trait RestoreExecutor: Send + Sync {
    /// Rebinds suspended workers before a published marker is made serving.
    fn reconcile<'a>(
        &'a self,
        request: &'a NativeRestoreRequest,
    ) -> Pin<Box<dyn Future<Output = Result<(), String>> + Send + 'a>>;
    /// Installs the selected recovery point under exclusive target admission.
    fn execute<'a>(
        &'a self,
        request: &'a NativeRestoreRequest,
    ) -> Pin<Box<dyn Future<Output = Result<NativeRestoreOutcome, String>> + Send + 'a>>;
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
    restore_executor: Arc<dyn RestoreExecutor>,
}

impl NativeRecoveryRuntime {
    /// Resolves an authenticated retained manifest without creating recovery state.
    pub fn resolve_recovery_point(
        &self,
        source: &str,
        selection_kind: &str,
        point_id: &str,
        placement: &[String],
    ) -> Result<(terrabase::backup::BackupManifest, terrabase::backup::object_store::Provider), NativeRecoveryError> {
        let database = self
            .registry
            .resolve(source)
            .map_err(|_| NativeRecoveryError::Unavailable)?
            .ok_or(NativeRecoveryError::VerificationFailed)?;
        if database.record.regions != placement || database.record.database_id != source {
            return Err(NativeRecoveryError::VerificationFailed);
        }
        let (manifest, providers) = {
            let fleet = self.fleet.lock().map_err(|_| NativeRecoveryError::Unavailable)?;
            let mut points = fleet.retained_points(source).map_err(map_fleet_error)?;
            let providers = fleet.configured_providers(source).map_err(map_fleet_error)?;
            let now = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_err(|_| NativeRecoveryError::Unavailable)?
                .as_millis() as u64;
            points.retain(|point| point.retain_until_unix_millis() > now);
            let point = match selection_kind {
                "latest" if point_id.is_empty() => points
                    .into_iter()
                    .max_by_key(|point| (point.captured_unix_millis(), point.boundary_sequence())),
                "point_id" => points.into_iter().find(|point| point.identity() == point_id),
                _ => None,
            }
            .ok_or(NativeRecoveryError::VerificationFailed)?;
            (point, providers)
        };
        let provider = providers
            .into_iter()
            .next()
            .ok_or(NativeRecoveryError::Unavailable)?;
        Ok((manifest, provider))
    }

    fn restore_residency_matches(
        &self,
        request: &NativeRestoreRequest,
    ) -> Result<bool, NativeRecoveryError> {
        let source = self
            .registry
            .resolve(&request.source_database_id)
            .map_err(|_| NativeRecoveryError::Unavailable)?
            .ok_or(NativeRecoveryError::VerificationFailed)?;
        let target = self
            .registry
            .retained_snapshot()
            .map_err(|_| NativeRecoveryError::Unavailable)?
            .into_iter()
            .find(|entry| entry.database.record.database_id == request.target_database_id)
            .ok_or(NativeRecoveryError::VerificationFailed)?;
        Ok(!request.residency.is_empty()
            && source.record.residency == request.residency
            && target.database.record.residency == request.residency)
    }

    /// Opens durable native recovery operations for the admitted worker fleet.
    ///
    /// # Errors
    /// Refuses unsafe paths and inaccessible SQLite state; callers supply the real fleet.
    pub fn new(
        root: &Path,
        fleet: Arc<Mutex<NativeBackupFleet>>,
        registry: Arc<AdmittedDatabaseRegistry>,
        limits: NativeRecoveryLimits,
        restore_executor: Arc<dyn RestoreExecutor>,
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
        connection
            .execute_batch("CREATE TABLE IF NOT EXISTS native_restore_operations(operation_id TEXT PRIMARY KEY NOT NULL, source_database_id TEXT NOT NULL, target_database_id TEXT NOT NULL, phase TEXT NOT NULL, record BLOB NOT NULL CHECK(length(record)<=262144)) WITHOUT ROWID; CREATE INDEX IF NOT EXISTS native_restore_source ON native_restore_operations(source_database_id,operation_id);")
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
            restore_executor,
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
                        let result = runtime.process_pending().await;
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
                // Validate retained checkpoint/facts before admission can replay or retry a row.
                status::read(&connection, &source, &operation_id)?;
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

    /// Atomically admits or replays one restore identity. Provider effects are
    /// performed only by the restore worker after target resolution.
    pub fn admit_restore(
        &self,
        request: NativeRestoreRequest,
    ) -> Result<NativeRestoreOperation, NativeRecoveryError> {
        if request.operation_id.is_empty()
            || request.source_owner.is_empty()
            || request.source_database_id.is_empty()
            || request.recovery_point_id.is_empty()
            || request.recovery_point_digest.len() != 64
            || request.target_owner.is_empty()
            || request.target_database_id.is_empty()
            || request.source_database_id == request.target_database_id
            || request.journal_cut.is_empty()
            || request.restore_epoch == 0
            || request.authority_epoch == 0
            || request.authority_lease.is_empty()
            || request.accepted_proof_digest.len() != 64
            || request.operation_grant.is_empty()
            || !matches!(request.provider.as_str(), "oci" | "tigris")
            || request.residency.is_empty()
            || request.placement.is_empty()
            || request
                .placement
                .iter()
                .any(|region| region.is_empty() || region.chars().any(char::is_control))
        {
            return Err(NativeRecoveryError::InvalidRequest);
        }
        let record = NativeRestoreOperation {
            request: request.clone(),
            phase: NativeRestorePhase::Accepted,
            receipt: None,
            error: None,
        };
        let bytes = serde_json::to_vec(&record).map_err(|_| NativeRecoveryError::Unavailable)?;
        let mut connection =
            self.operations.lock().map_err(|_| NativeRecoveryError::Unavailable)?;
        let tx = connection.transaction().map_err(|_| NativeRecoveryError::Unavailable)?;
        if let Some(existing) = tx
            .query_row(
                "SELECT record FROM native_restore_operations WHERE operation_id=?1",
                [&request.operation_id],
                |row| row.get::<_, Vec<u8>>(0),
            )
            .optional()
            .map_err(|_| NativeRecoveryError::Unavailable)?
        {
            let existing: NativeRestoreOperation = serde_json::from_slice(&existing)
                .map_err(|_| NativeRecoveryError::VerificationFailed)?;
            if existing.request != request {
                return Err(NativeRecoveryError::VerificationFailed);
            }
            tx.commit().map_err(|_| NativeRecoveryError::Unavailable)?;
            return Ok(existing);
        }
        if !self.restore_residency_matches(&request)? {
            return Err(NativeRecoveryError::VerificationFailed);
        }
        tx.execute(
            "INSERT INTO native_restore_operations(operation_id,source_database_id,target_database_id,phase,record) VALUES(?1,?2,?3,'accepted',?4)",
            rusqlite::params![request.operation_id, request.source_database_id, request.target_database_id, bytes],
        )
        .map_err(|_| NativeRecoveryError::Unavailable)?;
        tx.commit().map_err(|_| NativeRecoveryError::Unavailable)?;
        self.notify.notify_one();
        Ok(record)
    }

    /// Reads one durable restore operation without starting provider work.
    pub fn restore_status(
        &self,
        operation_id: &str,
    ) -> Result<Option<NativeRestoreOperation>, NativeRecoveryError> {
        let connection = self.operations.lock().map_err(|_| NativeRecoveryError::Unavailable)?;
        connection
            .query_row(
                "SELECT record FROM native_restore_operations WHERE operation_id=?1",
                [operation_id],
                |row| row.get::<_, Vec<u8>>(0),
            )
            .optional()
            .map_err(|_| NativeRecoveryError::Unavailable)?
            .map(|bytes| {
                serde_json::from_slice(&bytes).map_err(|_| NativeRecoveryError::VerificationFailed)
            })
            .transpose()
    }

    /// Returns the original admitted request after checking its target grant.
    pub fn restore_request(
        &self,
        operation_id: &str,
        target: &str,
        target_owner: &str,
    ) -> Result<Option<NativeRestoreRequest>, NativeRecoveryError> {
        let Some(operation) = self.restore_status(operation_id)? else { return Ok(None) };
        if operation.request.target_database_id != target
            || operation.request.target_owner != target_owner
        {
            return Err(NativeRecoveryError::VerificationFailed);
        }
        Ok(Some(operation.request))
    }

    /// Explicitly retries authenticated target readback for an uncertain
    /// restore. Provider execution is retried only after authenticated readback
    /// proves the paused target has no publication marker.
    pub fn retry_restore_readback(
        &self,
        operation_id: &str,
    ) -> Result<NativeRestoreOperation, NativeRecoveryError> {
        let mut operation =
            self.restore_status(operation_id)?.ok_or(NativeRecoveryError::InvalidRequest)?;
        if operation.phase != NativeRestorePhase::Uncertain {
            return Ok(operation);
        }
        operation.phase = NativeRestorePhase::Verifying;
        operation.error = None;
        self.save_restore(&operation)?;
        self.notify.notify_one();
        Ok(operation)
    }

    /// Reads source facts from the authenticated selected archive manifest.
    /// No target measurements or current source snapshot are substituted.
    pub async fn verified_restore_source_facts(
        &self,
        request: &NativeRestoreRequest,
    ) -> Result<RecoveryPointFacts, NativeRecoveryError> {
        let operation =
            self.restore_status(&request.operation_id)?.ok_or(NativeRecoveryError::Unavailable)?;
        if operation.request != *request || operation.phase != NativeRestorePhase::Complete {
            return Err(NativeRecoveryError::VerificationFailed);
        }
        let cut = request
            .journal_cut
            .parse::<u64>()
            .map_err(|_| NativeRecoveryError::VerificationFailed)?;
        let marker =
            self.read_published_marker(&operation)?.ok_or(NativeRecoveryError::Unavailable)?;
        Ok(RecoveryPointFacts {
            recovery_point_id: request.recovery_point_id.clone(),
            source_database_id: request.source_database_id.clone(),
            journal_cut: cut,
            captured_at_millis: marker.source_captured_unix_millis,
            checksum: marker.source_logical_checksum,
            row_count: marker.source_logical_documents,
            placement: request.placement.clone(),
            retained_until_millis: marker.source_retain_until_unix_millis,
        })
    }

    /// Returns facts only for a complete restore with exact identity binding.
    pub fn verified_restore_stats(
        &self,
        operation_id: &str,
        target: &str,
        target_owner: &str,
        journal_cut: &str,
        restore_epoch: u64,
    ) -> Result<Option<NativeRestoreReceipt>, NativeRecoveryError> {
        let Some(operation) = self.restore_status(operation_id)? else { return Ok(None) };
        if operation.phase != NativeRestorePhase::Complete
            || operation.request.target_database_id != target
            || operation.request.target_owner != target_owner
            || operation.request.journal_cut != journal_cut
            || operation.request.restore_epoch != restore_epoch
        {
            return Ok(None);
        }
        Ok(operation.receipt)
    }

    async fn process_pending(self: &Arc<Self>) -> Result<bool, NativeRecoveryError> {
        if self.process_restore_pending().await? {
            return Ok(true);
        }
        let pending = {
            let connection =
                self.operations.lock().map_err(|_| NativeRecoveryError::Unavailable)?;
            let mut statement = connection.prepare("SELECT source_database_id, operation_id FROM native_recovery_operations ORDER BY operation_id").map_err(|_| NativeRecoveryError::Unavailable)?;
            let rows = statement
                .query_map([], |row| Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?)))
                .map_err(|_| NativeRecoveryError::Unavailable)?;
            let mut found = None;
            for row in rows {
                let (source, operation) = row.map_err(|_| NativeRecoveryError::Unavailable)?;
                // Corruption is an error, never a fabricated terminal transition.
                let state = status::read(&connection, &source, &operation)?
                    .ok_or(NativeRecoveryError::VerificationFailed)?;
                if found.is_none()
                    && matches!(
                        state.phase,
                        NativeRecoveryPhase::Accepted
                            | NativeRecoveryPhase::Capturing
                            | NativeRecoveryPhase::Measuring
                    )
                {
                    found = Some((source, operation));
                }
            }
            found
        };
        let Some((source, operation)) = pending else {
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
        let runtime = Arc::clone(self);
        let capture_source = source.clone();
        let capture_operation = operation.clone();
        let captured = tokio::task::spawn_blocking(move || {
            runtime.run_capture(&admitted.host, placement, &capture_source, &capture_operation)
        })
        .await
        .map_err(|_| NativeRecoveryError::Unavailable)?;
        match captured {
            Ok(()) => {}
            Err(NativeRecoveryError::Unavailable) => return Err(NativeRecoveryError::Unavailable),
            Err(error) => {
                let Some(mut latest) = self.status(&source, &operation)? else {
                    return Err(NativeRecoveryError::Unavailable);
                };
                if latest.phase == NativeRecoveryPhase::Capturing
                    && self.load_checkpoint(&source, &operation)?.is_none()
                {
                    return Err(error);
                }
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

    async fn process_restore_pending(&self) -> Result<bool, NativeRecoveryError> {
        let row = {
            let connection =
                self.operations.lock().map_err(|_| NativeRecoveryError::Unavailable)?;
            connection
                .query_row(
                    "SELECT record FROM native_restore_operations WHERE phase IN ('accepted','restoring','verifying') ORDER BY operation_id LIMIT 1",
                    [],
                    |row| row.get::<_, Vec<u8>>(0),
                )
                .optional()
                .map_err(|_| NativeRecoveryError::Unavailable)?
        };
        let Some(bytes) = row else { return Ok(false) };
        let mut operation: NativeRestoreOperation =
            serde_json::from_slice(&bytes).map_err(|_| NativeRecoveryError::VerificationFailed)?;
        let request = &operation.request;
        if matches!(
            operation.phase,
            NativeRestorePhase::Restoring
                | NativeRestorePhase::Verifying
                | NativeRestorePhase::Uncertain
        ) {
            if let Some(receipt) = self.read_published_restore(&operation)? {
                self.restore_executor
                    .reconcile(&operation.request)
                    .await
                    .map_err(|_| NativeRecoveryError::Unavailable)?;
                self.registry
                    .resume(&operation.request.target_database_id, &operation.request.operation_id)
                    .map_err(|_| NativeRecoveryError::Unavailable)?;
                operation.receipt = Some(receipt);
                operation.phase = NativeRestorePhase::Complete;
                operation.error = None;
            } else if operation.phase == NativeRestorePhase::Verifying
                && operation.receipt.is_none()
            {
                // Explicit readback proved the retained target is paused and
                // active with no publication marker. No executor receipt claims
                // an effect, so the same identity can execute on the next turn.
                operation.phase = NativeRestorePhase::Accepted;
                operation.error = None;
            } else {
                operation.phase = NativeRestorePhase::Uncertain;
                operation.error =
                    Some("native restore outcome requires authenticated target readback".into());
            }
            self.save_restore(&operation)?;
            return Ok(true);
        }
        // Fresh provider execution requires current source admission. Published
        // marker reconciliation above remains possible after the source moves.
        let admitted = self
            .registry
            .resolve(&request.source_database_id)
            .map_err(|_| NativeRecoveryError::Unavailable)?
            .ok_or(NativeRecoveryError::Unavailable)?;
        if admitted.host.engine().database_id().to_text() != request.source_database_id
            || admitted.record.regions != request.placement
            || format!("{}/{}", admitted.record.tenant_id, admitted.record.project_id)
                != request.source_owner
            || !self.restore_residency_matches(request)?
        {
            return Err(NativeRecoveryError::VerificationFailed);
        }
        operation.phase = NativeRestorePhase::Restoring;
        self.save_restore(&operation)?;
        let receipt = match self.restore_executor.execute(request).await {
            Ok(outcome) => outcome.receipt,
            Err(_) => {
                operation.phase = NativeRestorePhase::Uncertain;
                operation.error =
                    Some("native restore remains repairable; retry the operation".into());
                self.save_restore(&operation)?;
                return Ok(true);
            }
        };
        operation.phase = NativeRestorePhase::Verifying;
        operation.receipt = Some(receipt);
        self.save_restore(&operation)?;
        Ok(true)
    }

    fn read_published_restore(
        &self,
        operation: &NativeRestoreOperation,
    ) -> Result<Option<NativeRestoreReceipt>, NativeRecoveryError> {
        let Some(marker) = self.read_published_marker(operation)? else {
            return Ok(None);
        };
        Ok(Some(NativeRestoreReceipt {
            selected_identity: operation.request.recovery_point_id.clone(),
            logical_content_checksum: marker.logical_checksum,
            restored_documents: marker
                .restored_documents
                .checked_add(marker.restored_family_records)
                .ok_or(NativeRecoveryError::VerificationFailed)?,
            journal_cut: marker.identity.expected_journal_boundary,
        }))
    }

    fn read_published_marker(
        &self,
        operation: &NativeRestoreOperation,
    ) -> Result<Option<terrabase::backup::RestoreMarkerFacts>, NativeRecoveryError> {
        // Read through the retained admitted MemberHost lease. Opening a
        // second Engine by path would bypass restore exclusion.
        let request = &operation.request;
        let target = self
            .registry
            .retained_snapshot()
            .map_err(|_| NativeRecoveryError::Unavailable)?
            .into_iter()
            .find(|entry| entry.database.record.database_id == request.target_database_id)
            .ok_or(NativeRecoveryError::Unavailable)?;
        if format!("{}/{}", target.database.record.tenant_id, target.database.record.project_id)
            != request.target_owner
            || target.database.record.regions != request.placement
            || request.residency.is_empty()
            || target.database.record.residency != request.residency
        {
            return Err(NativeRecoveryError::VerificationFailed);
        }
        let engine = target.database.host.engine();
        if engine.database_id().to_text() != request.target_database_id {
            return Err(NativeRecoveryError::VerificationFailed);
        }
        if let NativeRestoreSelection::PointId(point) = &request.selection {
            if point != &request.recovery_point_id {
                return Err(NativeRecoveryError::VerificationFailed);
            }
        }
        let journal_cut =
            request.journal_cut.parse().map_err(|_| NativeRecoveryError::VerificationFailed)?;
        let identity = terrabase::backup::RestoreIdentity {
            operation_id: request.operation_id.clone(),
            source_database_id: request.source_database_id.clone(),
            target_database_id: request.target_database_id.clone(),
            provider: request.provider.clone(),
            selection: match &request.selection {
                NativeRestoreSelection::PointId(value) => value.clone(),
                NativeRestoreSelection::AtUnixMillis(value) => value.to_string(),
            },
            expected_manifest_digest: request.recovery_point_digest.clone(),
            expected_journal_boundary: journal_cut,
            restore_epoch: request.restore_epoch,
        };
        let Some(marker) = terrabase::backup::read_restore_marker(engine, &identity)
            .map_err(|_| NativeRecoveryError::VerificationFailed)?
        else {
            let tenant = terrabase::types::TenantId::try_new(&target.database.record.tenant_id)
                .map_err(|_| NativeRecoveryError::VerificationFailed)?;
            let state = engine
                .lifecycle_state(&engine.scope_for(&tenant))
                .map_err(|_| NativeRecoveryError::Unavailable)?;
            if !state.paused || state.access_revoked() {
                return Err(NativeRecoveryError::VerificationFailed);
            }
            return Ok(None);
        };
        Ok(Some(marker))
    }

    fn save_restore(&self, operation: &NativeRestoreOperation) -> Result<(), NativeRecoveryError> {
        let bytes = serde_json::to_vec(operation).map_err(|_| NativeRecoveryError::Unavailable)?;
        let phase = serde_json::to_string(&operation.phase)
            .map_err(|_| NativeRecoveryError::Unavailable)?
            .trim_matches('"')
            .to_owned();
        let connection = self.operations.lock().map_err(|_| NativeRecoveryError::Unavailable)?;
        if connection
            .execute(
                "UPDATE native_restore_operations SET phase=?2, record=?3 WHERE operation_id=?1",
                rusqlite::params![operation.request.operation_id, phase, bytes],
            )
            .map_err(|_| NativeRecoveryError::Unavailable)?
            != 1
        {
            return Err(NativeRecoveryError::Unavailable);
        }
        Ok(())
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
        if state.operation_id != operation || state.source_database_id != source {
            return Err(NativeRecoveryError::VerificationFailed);
        }
        let checkpoint = self.load_checkpoint(source, operation)?;
        let capture = || {
            let manifest = {
                let mut fleet = self.fleet.lock().map_err(|_| NativeRecoveryError::Unavailable)?;
                tokio::task::block_in_place(|| {
                    tokio::runtime::Handle::current().block_on(fleet.capture_snapshot(source))
                })
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

    struct RejectRestore;
    impl RestoreExecutor for RejectRestore {
        fn reconcile<'a>(
            &'a self,
            _: &'a NativeRestoreRequest,
        ) -> Pin<Box<dyn Future<Output = Result<(), String>> + Send + 'a>> {
            Box::pin(async { Ok(()) })
        }
        fn execute<'a>(
            &'a self,
            _request: &'a NativeRestoreRequest,
        ) -> Pin<Box<dyn Future<Output = Result<NativeRestoreOutcome, String>> + Send + 'a>>
        {
            Box::pin(async { Err("restore target unavailable in capture fixture".into()) })
        }
    }

    struct CountingReject(std::sync::Arc<std::sync::atomic::AtomicUsize>);
    impl RestoreExecutor for CountingReject {
        fn reconcile<'a>(
            &'a self,
            _: &'a NativeRestoreRequest,
        ) -> Pin<Box<dyn Future<Output = Result<(), String>> + Send + 'a>> {
            Box::pin(async { Ok(()) })
        }
        fn execute<'a>(
            &'a self,
            _request: &'a NativeRestoreRequest,
        ) -> Pin<Box<dyn Future<Output = Result<NativeRestoreOutcome, String>> + Send + 'a>>
        {
            let calls = Arc::clone(&self.0);
            Box::pin(async move {
                calls.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
                Err("provider must not be retried".into())
            })
        }
    }

    struct GatedRestore {
        entered: Arc<Notify>,
        release: Arc<Notify>,
        _dependency: Arc<()>,
    }
    impl RestoreExecutor for GatedRestore {
        fn reconcile<'a>(
            &'a self,
            _: &'a NativeRestoreRequest,
        ) -> Pin<Box<dyn Future<Output = Result<(), String>> + Send + 'a>> {
            Box::pin(async { Ok(()) })
        }
        fn execute<'a>(
            &'a self,
            _: &'a NativeRestoreRequest,
        ) -> Pin<Box<dyn Future<Output = Result<NativeRestoreOutcome, String>> + Send + 'a>>
        {
            Box::pin(async move {
                self.entered.notify_one();
                self.release.notified().await;
                Err("controlled unknown provider outcome".into())
            })
        }
    }

    #[tokio::test(start_paused = true)]
    async fn owned_completion_waits_for_inflight_provider_before_releasing_dependencies() {
        let (registry, member, records, root) = crate::admitted_databases::tests::fixture();
        let registry = Arc::new(registry);
        let runtime_root = root.join("native-recovery-owned-drain");
        let entered = Arc::new(Notify::new());
        let release = Arc::new(Notify::new());
        let dependency = Arc::new(());
        let dependency_lifetime = Arc::downgrade(&dependency);
        let (runtime, worker) = NativeRecoveryRuntime::new(
            &runtime_root,
            fleet(),
            registry.clone(),
            limits(),
            Arc::new(GatedRestore {
                entered: entered.clone(),
                release: release.clone(),
                _dependency: dependency,
            }),
        )
        .unwrap();
        registry.pause(&records[1].database_id, "owned-drain").unwrap();
        runtime
            .admit_restore(NativeRestoreRequest {
                operation_id: "owned-drain".into(),
                source_owner: format!("{}/{}", records[0].tenant_id, records[0].project_id),
                source_database_id: records[0].database_id.clone(),
                recovery_point_id: "point".into(),
                recovery_point_digest: "a".repeat(64),
                target_owner: format!("{}/{}", records[1].tenant_id, records[1].project_id),
                target_database_id: records[1].database_id.clone(),
                journal_cut: "1".into(),
                restore_epoch: 2,
                authority_epoch: 7,
                authority_lease: "lease".into(),
                accepted_proof_digest: "a".repeat(64),
                operation_grant: vec![1],
                provider: "oci".into(),
                selection: NativeRestoreSelection::PointId("point".into()),
                residency: records[0].residency.clone(),
                placement: records[0].regions.clone(),
            })
            .unwrap();
        tokio::time::timeout(std::time::Duration::from_secs(1), entered.notified())
            .await
            .expect("admitted restore reaches the real provider executor");
        drop(runtime);
        let completion = tokio::spawn(worker.await_completion());
        tokio::task::yield_now().await;
        tokio::time::advance(std::time::Duration::from_secs(6)).await;
        assert!(!completion.is_finished(), "owned drain must not detach after five seconds");
        assert!(dependency_lifetime.upgrade().is_some(), "provider dependencies must remain owned");
        let lease =
            File::options().write(true).open(runtime_root.join("recovery.worker.lock")).unwrap();
        assert!(
            lease.try_lock().is_err(),
            "inflight worker must retain exclusive ledger ownership"
        );
        release.notify_one();
        tokio::time::timeout(std::time::Duration::from_secs(1), completion)
            .await
            .expect("released provider allows owned completion")
            .unwrap()
            .unwrap();
        assert!(dependency_lifetime.upgrade().is_none(), "provider completion releases its owner");
        lease.try_lock().expect("completed worker releases its ledger lock");
        drop(lease);
        let (reopened, worker) = NativeRecoveryRuntime::new(
            &runtime_root,
            fleet(),
            registry.clone(),
            limits(),
            Arc::new(RejectRestore),
        )
        .expect("actual constructor can reopen after owned completion");
        worker.await_completion().await.unwrap();
        drop((reopened, registry, member));
        let _ = std::fs::remove_dir_all(root);
    }

    #[tokio::test]
    async fn real_constructor_idle_shutdown_closes_admission_fence() {
        let (registry, member, records, root) = crate::admitted_databases::tests::fixture();
        let registry = Arc::new(registry);
        let runtime_root = root.join("native-recovery-idle");
        let calls = Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let (runtime, worker) = NativeRecoveryRuntime::new(
            &runtime_root,
            fleet(),
            Arc::clone(&registry),
            limits(),
            Arc::new(CountingReject(Arc::clone(&calls))),
        )
        .unwrap();
        worker.shutdown().await.unwrap();
        assert_eq!(calls.load(std::sync::atomic::Ordering::SeqCst), 0);
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
        let (runtime, worker) = NativeRecoveryRuntime::new(
            &runtime_root,
            fleet(),
            Arc::clone(&registry),
            limits(),
            Arc::new(RejectRestore),
        )
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
        assert_eq!(worker.shutdown().await, Err(NativeRecoveryError::Unavailable));
        let result = runtime.status(&state.source_database_id, &state.operation_id).unwrap();
        assert!(matches!(result.map(|value| value.phase), Some(NativeRecoveryPhase::Capturing)));
        drop(member);
        let _ = std::fs::remove_dir_all(root);
    }

    #[tokio::test]
    async fn uncertain_restore_requires_explicit_readback_retry() {
        let (registry, member, records, root) = crate::admitted_databases::tests::fixture();
        let registry = Arc::new(registry);
        let runtime_root = root.join("native-recovery-uncertain");
        let calls = Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let (runtime, worker) = NativeRecoveryRuntime::new(
            &runtime_root,
            fleet(),
            Arc::clone(&registry),
            limits(),
            Arc::new(CountingReject(Arc::clone(&calls))),
        )
        .unwrap();
        worker.shutdown().await.unwrap();
        registry.pause(&records[1].database_id, "pause-uncertain").unwrap();
        let request = NativeRestoreRequest {
            operation_id: "uncertain-operation".into(),
            source_owner: format!("{}/{}", records[0].tenant_id, records[0].project_id),
            source_database_id: records[0].database_id.clone(),
            recovery_point_id: "point".into(),
            recovery_point_digest: "a".repeat(64),
            target_owner: format!("{}/{}", records[1].tenant_id, records[1].project_id),
            target_database_id: records[1].database_id.clone(),
            journal_cut: "1".into(),
            restore_epoch: 2,
            authority_epoch: 7,
            authority_lease: "lease".into(),
            accepted_proof_digest: "a".repeat(64),
            operation_grant: vec![1],
            provider: "oci".into(),
            selection: NativeRestoreSelection::PointId("point".into()),
            residency: records[0].residency.clone(),
            placement: records[0].regions.clone(),
        };
        let mut wrong_residency = request.clone();
        wrong_residency.residency.push_str("-mismatch");
        assert_eq!(
            runtime.admit_restore(wrong_residency),
            Err(NativeRecoveryError::VerificationFailed)
        );
        // A paused target is retained but deliberately absent from resolve().
        assert!(registry.resolve(&request.target_database_id).unwrap().is_none());
        runtime.admit_restore(request.clone()).unwrap();
        let mut altered_authority = request.clone();
        altered_authority.authority_epoch = 8;
        assert_eq!(
            runtime.admit_restore(altered_authority),
            Err(NativeRecoveryError::VerificationFailed)
        );
        let mut altered_grant = request.clone();
        altered_grant.operation_grant.push(2);
        assert_eq!(
            runtime.admit_restore(altered_grant),
            Err(NativeRecoveryError::VerificationFailed)
        );
        let mut altered_restore = request.clone();
        altered_restore.restore_epoch = 3;
        assert_eq!(
            runtime.admit_restore(altered_restore),
            Err(NativeRecoveryError::VerificationFailed)
        );
        let operation = NativeRestoreOperation {
            request,
            phase: NativeRestorePhase::Uncertain,
            receipt: None,
            error: Some("unknown".into()),
        };
        runtime.save_restore(&operation).unwrap();
        let retried = runtime.retry_restore_readback("uncertain-operation").unwrap();
        assert_eq!(retried.phase, NativeRestorePhase::Verifying);
        assert!(retried.receipt.is_none());
        assert_eq!(calls.load(std::sync::atomic::Ordering::SeqCst), 0);
        assert!(runtime.process_restore_pending().await.unwrap());
        assert_eq!(
            runtime.restore_status("uncertain-operation").unwrap().unwrap().phase,
            NativeRestorePhase::Accepted
        );
        assert_eq!(calls.load(std::sync::atomic::Ordering::SeqCst), 0);
        assert!(runtime.process_restore_pending().await.unwrap());
        assert_eq!(calls.load(std::sync::atomic::Ordering::SeqCst), 1);
        assert_eq!(
            runtime.restore_status("uncertain-operation").unwrap().unwrap().phase,
            NativeRestorePhase::Uncertain
        );
        // A successful executor receipt claims an effect. Missing publication
        // is inconsistent evidence, not permission to execute again.
        let mut claimed = operation.clone();
        claimed.phase = NativeRestorePhase::Verifying;
        claimed.error = None;
        claimed.receipt = Some(NativeRestoreReceipt {
            selected_identity: claimed.request.recovery_point_id.clone(),
            logical_content_checksum: blake3::hash(b"claimed").to_hex().to_string(),
            restored_documents: 0,
            journal_cut: 1,
        });
        runtime.save_restore(&claimed).unwrap();
        assert!(runtime.process_restore_pending().await.unwrap());
        assert_eq!(
            runtime.restore_status("uncertain-operation").unwrap().unwrap().phase,
            NativeRestorePhase::Uncertain
        );
        assert!(!runtime.process_restore_pending().await.unwrap());
        assert_eq!(calls.load(std::sync::atomic::Ordering::SeqCst), 1);
        drop(member);
        let _ = std::fs::remove_dir_all(root);
    }

    #[tokio::test]
    async fn original_restore_rpc_reaches_readback_without_source_and_rejects_changed_request() {
        use crate::production_lifecycle_control::*;
        use terrabase_tbep::lifecycle::LifecycleGrantAction;
        use terrabase_tbep::proto::terrabase::v1::{
            LifecycleControlContext, RestoreProvider, RestoreToTargetRequest,
        };
        struct Authority;
        impl LifecycleAuthority for Authority {
            fn verify(
                &self,
                _: &AcceptedLifecycleAssignment,
                _: LifecycleGrantAction,
                _: &str,
                _: &[u8],
                _: &[u8],
                digest: Option<&str>,
            ) -> Result<(), String> {
                if digest.is_some_and(|value| value.len() == 64) {
                    Ok(())
                } else {
                    Err("missing request binding".into())
                }
            }
        }
        let (registry, member, records, root) = crate::admitted_databases::tests::fixture();
        let registry = Arc::new(registry);
        let (runtime, worker) = NativeRecoveryRuntime::new(
            &root.join("rpc-retry"),
            fleet(),
            Arc::clone(&registry),
            limits(),
            Arc::new(RejectRestore),
        )
        .unwrap();
        worker.shutdown().await.unwrap();
        let target = &records[1];
        let source = records[0].database_id.clone();
        let owner = format!("{}/{}", target.tenant_id, target.project_id);
        let request = NativeRestoreRequest {
            operation_id: "published-before-receipt-save".into(),
            source_owner: owner.clone(),
            source_database_id: source.clone(),
            recovery_point_id: "a".repeat(64),
            recovery_point_digest: "a".repeat(64),
            target_owner: owner.clone(),
            target_database_id: target.database_id.clone(),
            journal_cut: "1".into(),
            restore_epoch: 1,
            authority_epoch: 7,
            authority_lease: "lease".into(),
            accepted_proof_digest: "a".repeat(64),
            operation_grant: vec![1],
            provider: "oci".into(),
            selection: NativeRestoreSelection::PointId("a".repeat(64)),
            residency: target.residency.clone(),
            placement: target.regions.clone(),
        };
        runtime.admit_restore(request.clone()).unwrap();
        registry.pause(&source, "source-moved").unwrap();
        assert!(registry.resolve(&source).unwrap().is_none());
        runtime
            .save_restore(&NativeRestoreOperation {
                request: request.clone(),
                phase: NativeRestorePhase::Uncertain,
                receipt: None,
                error: Some("receipt persistence interrupted".into()),
            })
            .unwrap();
        drop(runtime);
        let (runtime, worker) = NativeRecoveryRuntime::new(
            &root.join("rpc-retry"),
            fleet(),
            Arc::clone(&registry),
            limits(),
            Arc::new(RejectRestore),
        )
        .unwrap();
        worker.shutdown().await.unwrap();
        assert_eq!(
            runtime
                .restore_request(
                    &request.operation_id,
                    &request.target_database_id,
                    &request.target_owner
                )
                .unwrap(),
            Some(request.clone())
        );
        let der = aws_lc_rs::signature::Ed25519KeyPair::generate_pkcs8(
            &aws_lc_rs::rand::SystemRandom::new(),
        )
        .unwrap();
        let signer = crate::native_recovery_receipt::NativeRecoveryReceiptSigner::from_pkcs8(
            "test".into(),
            der.as_ref(),
        )
        .unwrap();
        let receipts = Arc::new(
            crate::native_recovery_receipt::NativeRecoveryReceiptStore::open(
                &root.join("rpc-receipts.sqlite"),
                signer,
            )
            .unwrap(),
        );
        let rpc = TbepLifecycleControl::new(Arc::new(ProductionLifecycleControl {
            worker: Arc::new(RegistryLifecycleWorker {
                registry: Arc::clone(&registry),
                backups: None,
                recovery: Some(Arc::clone(&runtime)),
                receipts,
                journal: None,
            }),
            authority: Arc::new(Authority),
        }));
        let wire = RestoreToTargetRequest {
            context: Some(LifecycleControlContext {
                database: target.database_id.clone(),
                tenant: target.tenant_id.clone(),
                project: target.project_id.clone(),
                authority_epoch: 9,
                authority_lease: "renewed-observation".into(),
                operation_grant: vec![9],
                ..Default::default()
            }),
            operation_id: request.operation_id.clone(),
            source_owner: owner.clone(),
            source_database: source,
            recovery_point_id: request.recovery_point_id.clone(),
            recovery_point_digest: request.recovery_point_digest.clone(),
            target_owner: owner,
            target_database: target.database_id.clone(),
            journal_cut: "1".into(),
            restore_epoch: 1,
            provider: RestoreProvider::Oci as i32,
        };
        rpc.restore_to_target(&wire)
            .await
            .expect("original RPC reaches durable readback without source");
        assert_eq!(
            runtime.restore_status(&request.operation_id).unwrap().unwrap().phase,
            NativeRestorePhase::Verifying
        );
        assert_eq!(
            runtime
                .restore_request(
                    &request.operation_id,
                    &request.target_database_id,
                    &request.target_owner
                )
                .unwrap(),
            Some(request.clone())
        );
        let mut changed = wire.clone();
        changed.recovery_point_digest = "b".repeat(64);
        assert!(rpc.restore_to_target(&changed).await.is_err());
        let mut changed = wire.clone();
        changed.source_owner.push_str("-changed");
        assert!(rpc.restore_to_target(&changed).await.is_err());
        let mut changed = wire;
        changed.restore_epoch += 1;
        assert!(rpc.restore_to_target(&changed).await.is_err());
        drop(rpc);
        drop(runtime);
        drop(registry);
        drop(member);
        std::fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test]
    async fn readback_uses_retained_active_host_without_reopening_target() {
        let (registry, member, records, root) = crate::admitted_databases::tests::fixture();
        let registry = Arc::new(registry);
        let runtime_root = root.join("native-recovery-active-readback");
        let (runtime, worker) = NativeRecoveryRuntime::new(
            &runtime_root,
            fleet(),
            Arc::clone(&registry),
            limits(),
            Arc::new(RejectRestore),
        )
        .unwrap();
        let record = &records[0];
        let operation = NativeRestoreOperation {
            request: NativeRestoreRequest {
                operation_id: "active-readback".into(),
                source_owner: format!("{}/{}", record.tenant_id, record.project_id),
                source_database_id: terrabase::identity::DatabaseUlid::generate().to_string(),
                recovery_point_id: "point".into(),
                recovery_point_digest: "a".repeat(64),
                target_owner: format!("{}/{}", record.tenant_id, record.project_id),
                target_database_id: record.database_id.clone(),
                journal_cut: "1".into(),
                restore_epoch: 1,
                authority_epoch: 7,
                authority_lease: "lease".into(),
                accepted_proof_digest: "a".repeat(64),
                operation_grant: vec![1],
                provider: "oci".into(),
                selection: NativeRestoreSelection::PointId("point".into()),
                residency: record.residency.clone(),
                placement: record.regions.clone(),
            },
            phase: NativeRestorePhase::Verifying,
            receipt: None,
            error: None,
        };
        assert!(registry.resolve(&operation.request.source_database_id).unwrap().is_none());
        assert_eq!(
            runtime.read_published_restore(&operation),
            Err(NativeRecoveryError::VerificationFailed)
        );
        registry.pause(&record.database_id, "pause-readback").unwrap();
        assert!(runtime.read_published_restore(&operation).unwrap().is_none());
        let mut mismatched = operation.clone();
        mismatched.request.residency.push_str("-mismatch");
        assert_eq!(
            runtime.read_published_restore(&mismatched),
            Err(NativeRecoveryError::VerificationFailed)
        );
        worker.shutdown().await.unwrap();
        drop(member);
        let _ = std::fs::remove_dir_all(root);
    }

    #[tokio::test]
    async fn runtime_complete_status_and_capture_replay_validate_checkpoint_without_mutation() {
        let (registry, member, records, root) = crate::admitted_databases::tests::fixture();
        let registry = Arc::new(registry);
        let (runtime, worker) = NativeRecoveryRuntime::new(
            &root.join("complete-validation"),
            fleet(),
            Arc::clone(&registry),
            limits(),
            Arc::new(RejectRestore),
        )
        .unwrap();
        worker.shutdown().await.unwrap();
        // Keep the public admission path available with the worker stopped so
        // the durable replay can be examined without background interference.
        *runtime.lifecycle.lock().unwrap() = false;
        let source = records[0].database_id.clone();
        let key = "complete-replay";
        let accepted = runtime.capture(source.clone(), key.into()).await.unwrap();
        let point = Checkpoint {
            recovery_point_id: "point".into(),
            source_database_id: source.clone(),
            journal_cut: 1,
            captured_at_millis: 2,
            retained_until_millis: 3,
            placement: records[0].regions.clone(),
        };
        let complete = NativeRecoveryOperation {
            phase: NativeRecoveryPhase::Complete,
            facts: Some(RecoveryPointFacts {
                recovery_point_id: point.recovery_point_id.clone(),
                source_database_id: source.clone(),
                journal_cut: 1,
                captured_at_millis: 2,
                retained_until_millis: 3,
                placement: point.placement.clone(),
                checksum: blake3::hash(b"facts").to_hex().to_string(),
                row_count: 0,
            }),
            ..accepted
        };
        let mut mismatched = point.clone();
        mismatched.journal_cut += 1;
        for checkpoint in [
            None,
            Some(b"{malformed checkpoint".to_vec()),
            Some(mismatched.encode().unwrap()),
            Some(point.encode().unwrap()),
        ] {
            let bytes = serde_json::to_vec(&complete).unwrap();
            runtime.operations.lock().unwrap().execute(
                "UPDATE native_recovery_operations SET phase='complete',record=?2,checkpoint=?3 WHERE operation_id=?1",
                rusqlite::params![complete.operation_id, bytes, checkpoint],
            ).unwrap();
            let valid = checkpoint == Some(point.encode().unwrap());
            if valid {
                assert_eq!(
                    runtime.status(&source, &complete.operation_id).unwrap(),
                    Some(complete.clone())
                );
                assert_eq!(runtime.capture(source.clone(), key.into()).await.unwrap(), complete);
                assert!(!runtime.process_pending().await.unwrap());
            } else {
                assert_eq!(
                    runtime.status(&source, &complete.operation_id),
                    Err(NativeRecoveryError::VerificationFailed)
                );
                assert_eq!(
                    runtime.capture(source.clone(), key.into()).await,
                    Err(NativeRecoveryError::VerificationFailed)
                );
                assert_eq!(
                    runtime.process_pending().await,
                    Err(NativeRecoveryError::VerificationFailed)
                );
            }
            let stored: (String, Vec<u8>, Option<Vec<u8>>) = runtime.operations.lock().unwrap().query_row(
                "SELECT phase,record,checkpoint FROM native_recovery_operations WHERE operation_id=?1",
                [&complete.operation_id], |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?)),
            ).unwrap();
            assert_eq!(stored, ("complete".into(), bytes, checkpoint));
        }
        drop(member);
        let _ = std::fs::remove_dir_all(root);
    }

    #[tokio::test]
    async fn runtime_capturing_checkpoint_enters_measurement_and_unknown_capture_cannot_retry() {
        let (registry, member, records, root) = crate::admitted_databases::tests::fixture();
        let registry = Arc::new(registry);
        let (runtime, worker) = NativeRecoveryRuntime::new(
            &root.join("capturing-resume"),
            fleet(),
            Arc::clone(&registry),
            limits(),
            Arc::new(RejectRestore),
        )
        .unwrap();
        worker.shutdown().await.unwrap();
        *runtime.lifecycle.lock().unwrap() = false;
        let source = records[0].database_id.clone();
        let accepted = runtime.capture(source.clone(), "resume".into()).await.unwrap();
        let capturing =
            NativeRecoveryOperation { phase: NativeRecoveryPhase::Capturing, ..accepted };
        let point = Checkpoint {
            recovery_point_id: "point".into(),
            source_database_id: source.clone(),
            journal_cut: 1,
            captured_at_millis: 2,
            retained_until_millis: 3,
            placement: records[0].regions.clone(),
        };
        runtime.save(&capturing, None).unwrap();
        assert_eq!(runtime.process_pending().await, Err(NativeRecoveryError::VerificationFailed));
        assert_eq!(runtime.capture(source.clone(), "resume".into()).await.unwrap(), capturing);
        runtime.save(&capturing, Some(&point)).unwrap();
        runtime.operations.lock().unwrap().execute_batch(
            "CREATE TABLE observed_phases(phase TEXT);
             CREATE TRIGGER observe_phase AFTER UPDATE ON native_recovery_operations BEGIN INSERT INTO observed_phases VALUES(NEW.phase); END;"
        ).unwrap();
        assert!(runtime.process_pending().await.unwrap());
        let connection = runtime.operations.lock().unwrap();
        let phases: Vec<String> = connection
            .prepare("SELECT phase FROM observed_phases")
            .unwrap()
            .query_map([], |row| row.get(0))
            .unwrap()
            .map(Result::unwrap)
            .collect();
        // The empty fleet cannot verify the retained artifact, so measurement
        // fails. Reaching Measuring proves the actual dispatcher consumed the
        // checkpoint; any recapture would persist Capturing first.
        assert_eq!(phases, vec!["measuring", "failed"]);
        drop(connection);
        drop(member);
        let _ = std::fs::remove_dir_all(root);
    }
}
