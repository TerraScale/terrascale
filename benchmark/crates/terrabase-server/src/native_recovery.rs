//! Durable native recovery-point operations bound to the running member fleet.

use rusqlite::{Connection, OptionalExtension, params};
use serde::{Deserialize, Serialize};
use std::path::Path;
use std::sync::{Arc, Mutex};
use terrabase::backup::worker::NativeBackupFleet;
use terrabase_tbep::member_host::MemberHost;
use crate::admitted_databases::AdmittedDatabaseRegistry;

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
        if !(1..=10_000).contains(&self.pending_operations) || self.pending_operations > self.retained_operations || self.retained_operations > 10_000 { return Err(NativeRecoveryError::InvalidRequest); }
        Ok(self)
    }
}

/// Owner handle for the single serial recovery worker.
pub struct NativeRecoveryWorker { stop: tokio::sync::oneshot::Sender<()>, join: tokio::task::JoinHandle<()> }

impl NativeRecoveryWorker {
    /// Signals the worker and waits for its owned task to finish.
    pub async fn shutdown(self) -> Result<(), NativeRecoveryError> {
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
    active: Mutex<std::collections::BTreeSet<String>>,
    registry: Arc<AdmittedDatabaseRegistry>,
    limits: NativeRecoveryLimits,
    lifecycle: Mutex<bool>,
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
    ) -> Result<Arc<Self>, NativeRecoveryError> {
        let limits = limits.validate()?;
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
                record BLOB NOT NULL CHECK(length(record)<=262144)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS native_recovery_source ON native_recovery_operations(source_database_id,operation_id);")
            .map_err(|_| NativeRecoveryError::Unavailable)?;
        for parent in root.ancestors().filter(|path| !path.as_os_str().is_empty()) {
            std::fs::File::open(parent)
                .and_then(|file| file.sync_all())
                .map_err(|_| NativeRecoveryError::Unavailable)?;
        }
        Ok(Arc::new(Self { fleet, operations: Mutex::new(connection), active: Mutex::default(), registry, limits, lifecycle: Mutex::new(false) }))
    }

    /// Durably admits one scoped capture and continues after request disconnection.
    ///
    /// # Errors
    /// Refuses invalid identity, full worker capacity, and durable admission failures.
    pub async fn capture(
        self: &Arc<Self>,
        host: Arc<MemberHost>,
        placement: Vec<String>,
        idempotency_key: String,
    ) -> Result<NativeRecoveryOperation, NativeRecoveryError> {
        if idempotency_key.is_empty()
            || idempotency_key.len() > 256
            || idempotency_key.chars().any(char::is_control)
            || placement.is_empty()
            || placement.len() > 20
        {
            return Err(NativeRecoveryError::InvalidRequest);
        }
        let source = host.engine().database_id().to_text();
        let mut digest = blake3::Hasher::new();
        digest.update(b"terrabase/native-recovery-operation/v1\0");
        for value in [&source, &idempotency_key] {
            digest.update(&(value.len() as u64).to_be_bytes());
            digest.update(value.as_bytes());
        }
        let operation_id = digest.finalize().to_hex().to_string();
        let runtime = self.clone();
        tokio::task::spawn_blocking(move || {
            let proposed = NativeRecoveryOperation { operation_id:operation_id.clone(), source_database_id:source.clone(),
                phase: NativeRecoveryPhase::Accepted, facts:None,error:None };
            let bytes = serde_json::to_vec(&proposed).map_err(|_| NativeRecoveryError::Unavailable)?;
            runtime.operations.lock().map_err(|_| NativeRecoveryError::Unavailable)?
                .execute("INSERT INTO native_recovery_operations(operation_id,source_database_id,record) VALUES(?1,?2,?3) ON CONFLICT DO NOTHING",params![operation_id,source,bytes])
                .map_err(|_| NativeRecoveryError::Unavailable)?;
            let state = runtime.status(&source,&operation_id)?.ok_or(NativeRecoveryError::Unavailable)?;
            if state.phase != NativeRecoveryPhase::Complete {
                runtime.start(host,placement,state.operation_id.clone())?;
            }
            Ok(state)
        }).await.map_err(|_| NativeRecoveryError::Unavailable)?
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

    fn save(&self, operation: &NativeRecoveryOperation) -> Result<(), NativeRecoveryError> {
        let bytes = serde_json::to_vec(operation).map_err(|_| NativeRecoveryError::Unavailable)?;
        if bytes.len() > 262144 {
            return Err(NativeRecoveryError::Unavailable);
        }
        self.operations
            .lock()
            .map_err(|_| NativeRecoveryError::Unavailable)?
            .execute(
                "UPDATE native_recovery_operations SET record=?2 WHERE operation_id=?1",
                params![operation.operation_id, bytes],
            )
            .map_err(|_| NativeRecoveryError::Unavailable)?;
        Ok(())
    }

    fn start(
        self: &Arc<Self>,
        host: Arc<MemberHost>,
        placement: Vec<String>,
        operation_id: String,
    ) -> Result<(), NativeRecoveryError> {
        let mut active = self.active.lock().map_err(|_| NativeRecoveryError::Unavailable)?;
        if active.contains(&operation_id) {
            return Ok(());
        }
        if !active.is_empty() {
            return Err(NativeRecoveryError::Unavailable);
        }
        active.insert(operation_id.clone());
        drop(active);
        let runtime = self.clone();
        tokio::task::spawn_blocking(move || {
            let source = host.engine().database_id().to_text();
            let result = runtime.run_capture(&host, placement, &source, &operation_id);
            if result.is_err() {
                if let Ok(Some(mut state)) = runtime.status(&source, &operation_id) {
                    state.phase = NativeRecoveryPhase::Failed;
                    state.error = Some(
                        "native recovery verification failed; retry the existing operation".into(),
                    );
                    let _ = runtime.save(&state);
                }
            }
            if let Ok(mut active) = runtime.active.lock() {
                active.remove(&operation_id);
            }
        });
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
