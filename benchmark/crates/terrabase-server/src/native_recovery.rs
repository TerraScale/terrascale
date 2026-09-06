//! Durable native recovery-point operations bound to the running member fleet.

use crate::admitted_databases::AdmittedDatabaseRegistry;
use rusqlite::{Connection, OptionalExtension, params};
use serde::{Deserialize, Serialize};
use std::path::Path;
use std::sync::{Arc, Mutex};
use terrabase::backup::worker::NativeBackupFleet;
use terrabase_tbep::member_host::MemberHost;
use tokio::sync::Notify;

mod admission;

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
    join: tokio::task::JoinHandle<()>,
}

impl NativeRecoveryWorker {
    /// Signals the worker and waits for its owned task to finish.
    pub async fn shutdown(self) -> Result<(), NativeRecoveryError> {
        *self.runtime.lifecycle.lock().map_err(|_| NativeRecoveryError::Unavailable)? = true;
        let _ = self.stop.send(());
        self.join.await.map_err(|_| NativeRecoveryError::Unavailable)
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
    notify: Notify,
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
                record BLOB NOT NULL CHECK(length(record)<=262144)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS native_recovery_source ON native_recovery_operations(source_database_id,operation_id);")
            .map_err(|_| NativeRecoveryError::Unavailable)?;
        // Reject unsupported ledgers without inventing execution phases for old rows.
        connection
            .prepare("SELECT operation_id, source_database_id, phase, record FROM native_recovery_operations LIMIT 0")
            .map_err(|_| NativeRecoveryError::Unavailable)?;
        for parent in root.ancestors().filter(|path| !path.as_os_str().is_empty()) {
            std::fs::File::open(parent)
                .and_then(|file| file.sync_all())
                .map_err(|_| NativeRecoveryError::Unavailable)?;
        }
        let runtime = Arc::new(Self {
            fleet,
            operations: Mutex::new(connection),
            registry,
            limits,
            lifecycle: Mutex::new(false),
            notify: Notify::new(),
        });
        let (stop, mut stopped) = tokio::sync::oneshot::channel();
        let worker_runtime = Arc::clone(&runtime);
        let join = executor.spawn(async move {
            loop {
                tokio::select! {
                    _ = &mut stopped => break,
                    _ = worker_runtime.notify.notified() => {}
                }
                if worker_runtime.process_pending().await.is_err() {
                    break;
                }
            }
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
        connection.query_row("SELECT length(record),record FROM native_recovery_operations WHERE source_database_id=?1 AND operation_id=?2",params![source,operation],|row| {
            let size:i64=row.get(0)?;
            if !(0..=262144).contains(&size) { return Err(rusqlite::Error::InvalidQuery); }
            row.get::<_,Vec<u8>>(1)
        }).optional().map_err(|_|NativeRecoveryError::Unavailable)?
            .map(|bytes| serde_json::from_slice(&bytes).map_err(|_|NativeRecoveryError::Unavailable)).transpose()
    }

    async fn process_pending(self: &Arc<Self>) -> Result<(), NativeRecoveryError> {
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
            for row in rows {
                let (source, operation, phase, bytes) =
                    row.map_err(|_| NativeRecoveryError::Unavailable)?;
                let state: NativeRecoveryOperation =
                    serde_json::from_slice(&bytes).map_err(|_| NativeRecoveryError::Unavailable)?;
                if matches!(phase.as_str(), "accepted" | "measuring") {
                    found = Some((source, operation, state));
                    break;
                }
            }
            found
        };
        let Some((source, operation, state)) = pending else {
            return Ok(());
        };
        let admitted = self
            .registry
            .resolve(&source)
            .map_err(|_| NativeRecoveryError::Unavailable)?
            .ok_or(NativeRecoveryError::Unavailable)?;
        if admitted.host.engine().database_id().to_text() != source {
            return Err(NativeRecoveryError::VerificationFailed);
        }
        let placement = admitted.record.regions.clone();
        let runtime = Arc::clone(self);
        let result = tokio::task::spawn_blocking(move || {
            runtime.run_capture(&admitted.host, placement, &source, &operation)
        })
        .await
        .map_err(|_| NativeRecoveryError::Unavailable)?;
        if result.is_err() {
            let mut failed = state;
            failed.phase = NativeRecoveryPhase::Failed;
            failed.error =
                Some("native recovery verification failed; retry the existing operation".into());
            self.save(&failed)?;
        }
        self.notify.notify_one();
        Ok(())
    }

    fn save(&self, operation: &NativeRecoveryOperation) -> Result<(), NativeRecoveryError> {
        let bytes = serde_json::to_vec(operation).map_err(|_| NativeRecoveryError::Unavailable)?;
        if bytes.len() > 262144 {
            return Err(NativeRecoveryError::Unavailable);
        }
        self.operations
            .lock()
            .map_err(|_| NativeRecoveryError::Unavailable)?
            .execute(
                "UPDATE native_recovery_operations SET phase=?2, record=?3 WHERE operation_id=?1",
                params![
                    operation.operation_id,
                    serde_json::to_string(&operation.phase)
                        .map_err(|_| NativeRecoveryError::Unavailable)?
                        .trim_matches('"'),
                    bytes
                ],
            )
            .map_err(|_| NativeRecoveryError::Unavailable)?;
        Ok(())
    }

    fn run_capture(
        &self,
        host: &MemberHost,
        placement: Vec<String>,
        source: &str,
        operation: &str,
    ) -> Result<(), NativeRecoveryError> {
        let mut state = self.status(source, operation)?.ok_or(NativeRecoveryError::Unavailable)?;
        state.phase = NativeRecoveryPhase::Capturing;
        state.error = None;
        self.save(&state)?;
        let manifest = {
            let mut fleet = self.fleet.lock().map_err(|_| NativeRecoveryError::Unavailable)?;
            tokio::runtime::Handle::current()
                .block_on(fleet.capture_snapshot(source))
                .map_err(|_| NativeRecoveryError::VerificationFailed)?
        };
        state.phase = NativeRecoveryPhase::Measuring;
        self.save(&state)?;
        let tenant = terrabase::types::TenantId::try_new(host.tenant())
            .map_err(|_| NativeRecoveryError::InvalidRequest)?;
        let scope = host.engine().scope_for(&tenant);
        let stats = host
            .engine()
            .recovery_statistics(
                &scope,
                manifest.boundary_sequence().get(),
                manifest.captured_unix_millis(),
                None,
            )
            .map_err(|_| NativeRecoveryError::VerificationFailed)?;
        state.facts = Some(RecoveryPointFacts {
            recovery_point_id: manifest.identity().into(),
            source_database_id: source.into(),
            journal_cut: stats.journal_cut,
            captured_at_millis: stats.captured_at_millis,
            checksum: blake3::Hash::from(stats.checksum).to_hex().to_string(),
            row_count: stats.row_count,
            placement,
            retained_until_millis: manifest.retain_until_unix_millis(),
        });
        state.phase = NativeRecoveryPhase::Complete;
        self.save(&state)
    }
}
