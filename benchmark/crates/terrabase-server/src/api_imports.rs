//! Resumable import execution on an admitted database.
//!
//! This module deliberately owns orchestration only. [`ImportWriteTarget`] is
//! the seam to the ordinary document write path: its implementation must use
//! the same authorization, write budget, durability, and idempotency rules as
//! a public batch write.

use super::{ApiState, attach_write_budget, document_scope};
use crate::{
    export_formats::{self, ImportRecord, TransferFormat},
    import_runtime::ImportInputReader,
    import_store::{ImportJob, ImportStore},
};
use std::sync::Arc;
use std::{
    io,
    sync::atomic::{AtomicBool, Ordering},
};
use terrabase::types::{
    CollectionName, Document, DocumentId, ExpectedVersion, LocalDurability, OperationId,
};

/// Process owner for durable import metadata and encrypted uploads.
pub struct ImportRuntime {
    pub(crate) store: Arc<ImportStore>,
    pub(crate) root: std::path::PathBuf,
    tasks: std::sync::Mutex<Vec<tokio::task::JoinHandle<()>>>,
    cancellation: Arc<AtomicBool>,
    permits: Arc<tokio::sync::Semaphore>,
}
impl ImportRuntime {
    pub(crate) fn open(root: std::path::PathBuf) -> Result<Self, String> {
        std::fs::create_dir_all(&root).map_err(|e| e.to_string())?;
        let store = Arc::new(ImportStore::open(&root).map_err(|e| e.to_string())?);
        store.recover().map_err(|e| e.to_string())?;
        for job in store.list().map_err(|e| e.to_string())? {
            if !job.input_committed {
                for suffix in ["upload.part", "upload"] {
                    let path = root.join(format!("{}.{}", job.operation_id, suffix));
                    match std::fs::remove_file(path) {
                        Ok(()) => {}
                        Err(e) if e.kind() == io::ErrorKind::NotFound => {}
                        Err(e) => return Err(e.to_string()),
                    }
                }
            }
        }
        Ok(Self {
            store,
            root,
            tasks: Default::default(),
            cancellation: Arc::new(AtomicBool::new(false)),
            permits: Arc::new(tokio::sync::Semaphore::new(4)),
        })
    }
    pub(crate) fn stop(&self) {
        self.cancellation.store(true, Ordering::Release);
    }
    pub(crate) async fn shutdown(&self) {
        self.cancellation.store(true, Ordering::Release);
        let tasks = std::mem::take(&mut *self.tasks.lock().expect("import tasks lock"));
        for task in tasks {
            let _ = task.await;
        }
    }
}

/// Maximum number of records in one admitted engine transaction.
pub(crate) const IMPORT_BATCH_RECORDS: usize = 256;

/// Result of one ordinary admitted write transaction.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum ImportCommit {
    /// The target durably committed the complete batch.
    Committed { records: u64 },
    /// The target rejected the batch before committing any records.
    Rejected,
    /// The commit may have landed. The checkpoint must not advance.
    Unknown,
}

/// Errors returned by the real engine write adapter.
pub(crate) trait ImportWriteTarget: Send + Sync {
    fn resolve_import_batch(
        &self,
        _operation: &str,
        _records: u64,
    ) -> Result<ImportCommit, String> {
        Ok(ImportCommit::Unknown)
    }
    /// Replays the original pending attempt. Only a durable rejection proves
    /// that its identity can be retired; preparation errors remain uncertain.
    fn replay_import_batch(
        &self,
        _operation: &str,
        _database: &str,
        _records: &[ImportRecord],
    ) -> Result<ImportCommit, String> {
        Ok(ImportCommit::Unknown)
    }
    /// An error proves no commit was submitted. Once a commit may have
    /// landed, return `Unknown` so the persisted identity is retained.
    fn commit_import_batch(
        &self,
        operation_id: &str,
        database_id: &str,
        records: &[ImportRecord],
    ) -> Result<ImportCommit, String>;
}

/// Production import target bound to the same Engine transaction seams as the
/// public document batch route. The import executor durably stages unique
/// operation identities; the target owns validation, metering, and durable
/// commit of each batch.
pub(crate) struct EngineImportWriteTarget {
    state: Arc<ApiState>,
    engine: Arc<terrabase_tbep::member_host::MemberHost>,
    tenant_id: String,
    project_id: String,
    headers: axum::http::HeaderMap,
    runtime: tokio::runtime::Handle,
}

impl EngineImportWriteTarget {
    pub(crate) fn new(
        state: Arc<ApiState>,
        engine: Arc<terrabase_tbep::member_host::MemberHost>,
        tenant_id: String,
        project_id: String,
        headers: axum::http::HeaderMap,
    ) -> Self {
        Self {
            state,
            engine,
            tenant_id,
            project_id,
            headers,
            runtime: tokio::runtime::Handle::current(),
        }
    }
}

impl ImportWriteTarget for EngineImportWriteTarget {
    fn resolve_import_batch(&self, operation: &str, records: u64) -> Result<ImportCommit, String> {
        let engine = self.engine.engine();
        let scope = document_scope(engine).map_err(|_| "document scope unavailable")?;
        let operation = OperationId::try_new(operation).map_err(|_| "invalid operation")?;
        match engine.resolve_commit(&scope, operation).map_err(|_| "resolution unavailable")? {
            terrabase::DurableResolution::Committed(receipt) => {
                let durability = self
                    .runtime
                    .block_on(super::WriteDurability::prepare(
                        &self.state,
                        engine,
                        &scope,
                        &self.headers,
                    ))
                    .map_err(|e| e.message)?;
                if self.runtime.block_on(durability.confirm(engine, &scope, &receipt)).is_err() {
                    return Ok(ImportCommit::Unknown);
                }
                Ok(ImportCommit::Committed { records })
            }
            terrabase::DurableResolution::Rejected(_) => Ok(ImportCommit::Rejected),
            terrabase::DurableResolution::Pending => Ok(ImportCommit::Unknown),
        }
    }

    fn replay_import_batch(
        &self,
        operation: &str,
        database: &str,
        records: &[ImportRecord],
    ) -> Result<ImportCommit, String> {
        match self.commit_import_batch(operation, database, records) {
            Ok(ImportCommit::Committed { records }) => Ok(ImportCommit::Committed { records }),
            // A changed schema or budget may refuse this replay before it can
            // observe an earlier commit. Resolve the original ledger identity
            // before retiring it, including after an operation-digest conflict.
            _ => self.resolve_import_batch(operation, records.len() as u64),
        }
    }

    fn commit_import_batch(
        &self,
        operation_id: &str,
        database_id: &str,
        records: &[ImportRecord],
    ) -> Result<ImportCommit, String> {
        if records.is_empty() {
            return Ok(ImportCommit::Committed { records: 0 });
        }
        let engine = self.engine.engine();
        if engine.database_id().to_text() != database_id {
            return Err("import database does not match the admitted engine".into());
        }
        let owner = engine.database_owner();
        if owner.tenant().to_string() != self.tenant_id
            || owner.project().to_string() != self.project_id
        {
            return Err("import owner does not match the admitted engine".into());
        }
        let scope = document_scope(engine).map_err(|_| "document scope unavailable".to_owned())?;
        let durability = self
            .runtime
            .block_on(super::WriteDurability::prepare(&self.state, engine, &scope, &self.headers))
            .map_err(|e| e.message)?;
        let operation = OperationId::try_new(operation_id)
            .map_err(|_| "import batch operation identity is invalid".to_owned())?;
        let snapshot =
            engine.read_snapshot(&scope).map_err(|_| "snapshot unavailable".to_owned())?;
        let mut transaction = engine
            .begin(&scope, snapshot, operation)
            .map_err(|_| "import transaction unavailable".to_owned())?;
        let mut units = 0_u64;
        for record in records {
            let collection = CollectionName::try_new(&record.collection)
                .map_err(|_| "import collection identifier is invalid".to_owned())?;
            let id = DocumentId::try_new(&record.id)
                .map_err(|_| "import document identifier is invalid".to_owned())?;
            let value: serde_json::Value = serde_json::from_slice(&record.value)
                .map_err(|_| "import document value is invalid JSON".to_owned())?;
            let value = super::json_value(&value);
            units = units
                .saturating_add(terrabase::usage::write_units_for_bytes(record.value.len() as u64));
            transaction
                .put(Document::new(id, collection, value), ExpectedVersion::Any)
                .map_err(|_| "import document could not stage".to_owned())?;
        }
        let operation_id = transaction.operation_id().clone();
        attach_write_budget(
            &self.state,
            &mut transaction,
            &scope,
            &self.project_id,
            database_id,
            &operation_id,
            units,
        )
        .map_err(|error| error.message)?;

        durability.guard(&mut transaction).map_err(|error| error.message)?;
        match transaction.commit(LocalDurability::Synced) {
            terrabase::CommitOutcome::Committed(receipt) => {
                if self.runtime.block_on(durability.confirm(engine, &scope, &receipt)).is_err() {
                    return Ok(ImportCommit::Unknown);
                }
                Ok(ImportCommit::Committed { records: records.len() as u64 })
            }
            terrabase::CommitOutcome::Rejected(_) => Ok(ImportCommit::Rejected),
            terrabase::CommitOutcome::Unknown(_) => Ok(ImportCommit::Unknown),
        }
    }
}

#[derive(Debug)]
pub(crate) enum ImportExecutionError {
    Input(io::Error),
    Codec(export_formats::CodecError),
    Store(String),
    Rejected(String),
    Unknown(String),
    Cancelled,
}

impl From<io::Error> for ImportExecutionError {
    fn from(error: io::Error) -> Self {
        Self::Input(error)
    }
}

/// Executes a committed upload from its durable cursor. Every batch has a
/// durably staged operation identity, and its cursor is persisted only after
/// the write target reports a proven commit. A failed or cancelled operation
/// remains resumable from its last checkpoint.
pub(crate) fn execute_import<C, T>(
    store: &ImportStore,
    job: &ImportJob,
    reader: ImportInputReader<C>,
    target: &T,
    cancellation: &AtomicBool,
) -> Result<ImportJob, ImportExecutionError>
where
    C: crate::import_runtime::ImportCipher,
    T: ImportWriteTarget,
{
    if !job.input_committed {
        return Err(ImportExecutionError::Store("import input is not committed".into()));
    }
    let digest = job
        .input_digest
        .as_deref()
        .ok_or_else(|| ImportExecutionError::Store("import digest is missing".into()))?;
    let database_id = job.database_id.as_str();
    let mut cursor = job.cursor;
    let mut committed_records = job.committed_records;
    let mut batch = Vec::with_capacity(IMPORT_BATCH_RECORDS);
    let mut batch_cursor = cursor;
    let mut batch_bytes = 0usize;
    let mut batch_error = None;
    let mut result = Ok(());
    let format = parse_format(&job.format)?;
    let mut replay = None;

    if let Some(operation) = &job.pending_operation {
        let records = job
            .pending_records
            .ok_or_else(|| ImportExecutionError::Store("missing pending count".into()))?;
        match target.resolve_import_batch(operation, records) {
            Ok(ImportCommit::Committed { records: proven }) if proven == records => {
                cursor = job
                    .pending_cursor
                    .ok_or_else(|| ImportExecutionError::Store("missing pending cursor".into()))?;
                committed_records = committed_records
                    .checked_add(records)
                    .ok_or_else(|| ImportExecutionError::Store("count overflow".into()))?;
                store
                    .checkpoint(&job.operation_id, cursor, committed_records)
                    .map_err(|e| ImportExecutionError::Store(e.to_string()))?;
            }
            Ok(ImportCommit::Rejected) => {
                store
                    .clear_rejected(&job.operation_id)
                    .map_err(|e| ImportExecutionError::Store(e.to_string()))?;
            }
            Ok(ImportCommit::Unknown) => {
                let end = job
                    .pending_cursor
                    .filter(|end| *end > cursor)
                    .ok_or_else(|| ImportExecutionError::Store("invalid pending cursor".into()))?;
                if records == 0 || records > IMPORT_BATCH_RECORDS as u64 {
                    return Err(ImportExecutionError::Store("invalid pending count".into()));
                }
                replay = Some((operation.as_str(), end, records));
            }
            _ => {
                let _ = store.hold(&job.operation_id);
                return Err(ImportExecutionError::Unknown(operation.clone()));
            }
        }
    }
    let reader = reader.verified_reader(digest, job.input_length.unwrap_or(0))?;
    let decode_result = export_formats::decode_reader(
        reader,
        format,
        1024 * 1024 + 4096,
        |source_offset, record| {
            if cancellation.load(Ordering::Acquire) {
                return Err(io::Error::new(io::ErrorKind::Interrupted, "import cancelled"));
            }
            if source_offset <= cursor {
                return Ok(());
            }
            batch_bytes = batch_bytes.saturating_add(record.value.len());
            batch.push(record);
            batch_cursor = source_offset;
            let replay_operation = if let Some((operation, end, count)) = replay {
                if source_offset > end || batch.len() as u64 > count {
                    batch_error = Some(ImportExecutionError::Unknown(operation.into()));
                    return Err(io::Error::other("invalid pending batch boundary"));
                }
                if source_offset < end {
                    return Ok(());
                }
                if batch.len() as u64 != count {
                    batch_error = Some(ImportExecutionError::Unknown(operation.into()));
                    return Err(io::Error::other("invalid pending batch count"));
                }
                Some(operation)
            } else {
                None
            };
            if replay_operation.is_some()
                || batch.len() == IMPORT_BATCH_RECORDS
                || batch_bytes >= 8 * 1024 * 1024
            {
                match commit_batch(
                    store,
                    target,
                    job,
                    database_id,
                    source_offset,
                    &mut batch,
                    committed_records,
                    replay_operation,
                ) {
                    Ok((next_cursor, next_records)) => {
                        cursor = next_cursor;
                        committed_records = next_records;
                        batch_bytes = 0;
                        replay = None;
                    }
                    Err(error) => {
                        batch_error = Some(error);
                        return Err(io::Error::other("import batch failed"));
                    }
                }
            }
            Ok(())
        },
    );
    if let Some(error) = batch_error {
        result = Err(error);
    } else if let Err(error) = decode_result {
        result = if cancellation.load(Ordering::Acquire) {
            Err(ImportExecutionError::Cancelled)
        } else {
            Err(ImportExecutionError::Codec(error))
        };
    }
    if result.is_ok()
        && let Some((operation, _, _)) = replay
    {
        result = Err(ImportExecutionError::Unknown(operation.into()));
    }
    if result.is_ok() && !batch.is_empty() {
        let offset = batch_cursor.max(job.cursor);
        match commit_batch(
            store,
            target,
            job,
            database_id,
            offset,
            &mut batch,
            committed_records,
            None,
        ) {
            Ok((next_cursor, next_records)) => {
                cursor = next_cursor;
                committed_records = next_records;
            }
            Err(error) => result = Err(error),
        }
    }
    if let Err(error) = result {
        let text: String = match &error {
            ImportExecutionError::Cancelled => "import cancelled".into(),
            ImportExecutionError::Input(_) | ImportExecutionError::Codec(_) => {
                "invalid import input".into()
            }
            ImportExecutionError::Store(_) => "import metadata unavailable".into(),
            ImportExecutionError::Rejected(_) => "import batch rejected".into(),
            ImportExecutionError::Unknown(_) => "import commit outcome unknown".into(),
        };
        if matches!(error, ImportExecutionError::Unknown(_)) {
            let _ = store.hold(&job.operation_id);
        } else {
            let _ = store.fail(&job.operation_id, &text);
        }
        return Err(error);
    }
    store
        .complete(&job.operation_id)
        .map_err(|error| ImportExecutionError::Store(error.to_string()))?;
    store
        .get(&job.operation_id)
        .map_err(|error| ImportExecutionError::Store(error.to_string()))?
        .ok_or_else(|| ImportExecutionError::Store("import operation disappeared".into()))
}

fn commit_batch<T: ImportWriteTarget>(
    store: &ImportStore,
    target: &T,
    job: &ImportJob,
    database_id: &str,
    cursor: u64,
    batch: &mut Vec<ImportRecord>,
    committed_records: u64,
    replay_operation: Option<&str>,
) -> Result<(u64, u64), ImportExecutionError> {
    // Rejected operation identities remain rejected in the engine ledger. A
    // new attempt gets a fresh identity, durably saved before submission; an
    // uncertain attempt keeps its pending identity until resolution proves
    // whether it committed or was rejected.
    let operation = replay_operation
        .map(str::to_owned)
        .unwrap_or_else(|| format!("import-batch-{}", terrabase::identity::generate()));
    let count = batch.len() as u64;
    let outcome = if replay_operation.is_some() {
        target
            .replay_import_batch(&operation, database_id, batch)
            .map_err(|_| ImportExecutionError::Unknown(operation.clone()))?
    } else {
        store
            .begin_batch(&job.operation_id, &operation, cursor, count)
            .map_err(|e| ImportExecutionError::Store(e.to_string()))?;
        target.commit_import_batch(&operation, database_id, batch).map_err(|error| {
            let _ = store.clear_rejected(&job.operation_id);
            ImportExecutionError::Rejected(error)
        })?
    };
    match outcome {
        ImportCommit::Committed { records } if records == count => {
            let total = committed_records.checked_add(records).ok_or_else(|| {
                ImportExecutionError::Store("import record count overflow".into())
            })?;
            store
                .checkpoint(&job.operation_id, cursor, total)
                .map_err(|error| ImportExecutionError::Store(error.to_string()))?;
            batch.clear();
            Ok((cursor, total))
        }
        ImportCommit::Committed { .. } => Err(ImportExecutionError::Unknown(operation)),
        ImportCommit::Rejected => {
            let _ = store.clear_rejected(&job.operation_id);
            Err(ImportExecutionError::Rejected("import batch rejected".into()))
        }
        ImportCommit::Unknown => Err(ImportExecutionError::Unknown(operation)),
    }
}

fn parse_format(value: &str) -> Result<TransferFormat, ImportExecutionError> {
    serde_json::from_str(&format!("\"{value}\""))
        .map_err(|_| ImportExecutionError::Codec(export_formats::CodecError::Malformed))
}

use crate::api_keys::{Principal, Verbs};
use axum::{
    Extension, Json,
    extract::{Path, State},
    http::{HeaderMap, StatusCode},
    response::{IntoResponse, Response},
};

#[derive(serde::Deserialize, utoipa::ToSchema)]
#[serde(deny_unknown_fields)]
pub(super) struct CreateImport {
    idempotency_key: String,
    format: TransferFormat,
}

fn refusal(status: StatusCode, message: &'static str) -> Response {
    (status, Json(serde_json::json!({"code":"import_failed", "message":message}))).into_response()
}

fn resolve(
    state: &ApiState,
    name: &str,
    principal: &Principal,
    write: bool,
) -> Result<(Arc<ImportRuntime>, Arc<terrabase_tbep::member_host::MemberHost>), Response> {
    if !principal.scope().admits_database(Verbs::ADMIN, name)
        || (write && !principal.scope().admits_database(Verbs::WRITE, name))
    {
        return Err(super::ApiError::forbidden("database import permission").into_response());
    }
    let host = super::document_host(state, name).map_err(IntoResponse::into_response)?;
    let runtime = state
        .imports
        .clone()
        .ok_or_else(|| refusal(StatusCode::SERVICE_UNAVAILABLE, "import runtime unavailable"))?;
    Ok((runtime, host))
}

fn owned_job(
    runtime: &ImportRuntime,
    host: &terrabase_tbep::member_host::MemberHost,
    id: &str,
) -> Result<ImportJob, Response> {
    let owner = host.engine().database_owner();
    runtime
        .store
        .get(id)
        .map_err(|_| refusal(StatusCode::SERVICE_UNAVAILABLE, "import metadata unavailable"))?
        .filter(|job| {
            job.tenant_id == owner.tenant().to_string()
                && job.project_id == owner.project().to_string()
                && job.database_id == owner.database().to_text()
        })
        .ok_or_else(|| refusal(StatusCode::NOT_FOUND, "import operation not found"))
}

fn cipher(
    host: &terrabase_tbep::member_host::MemberHost,
    version: &str,
) -> Result<crate::import_runtime::ImportLeaseCipher, String> {
    let engine = host.engine();
    let scope = document_scope(engine).map_err(|_| "import scope unavailable")?;
    let source = engine.artifact_key_source(&scope).map_err(|_| "import key source unavailable")?;
    let version = terrabase::encryption::DatabaseKeyVersion::from_text(version)
        .map_err(|_| "invalid import key version")?;
    let lease = source.lease(version).map_err(|_| "import key lease unavailable")?;
    Ok(crate::import_runtime::ImportLeaseCipher::new(
        lease,
        engine.database_owner().tenant().to_string(),
        engine.database_id().to_text(),
    ))
}

#[utoipa::path(post, path = "/api/v1/databases/{name}/imports", request_body = CreateImport,
    responses((status = 202, description = "Import upload admitted", body = ImportJob)))]
pub(super) async fn create_import(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
    Extension(principal): Extension<Principal>,
    headers: HeaderMap,
    Json(request): Json<CreateImport>,
) -> Response {
    let (runtime, host) = match resolve(&state, &name, &principal, true) {
        Ok(value) => value,
        Err(error) => return error,
    };
    if request.idempotency_key.is_empty() || request.idempotency_key.len() > 256 {
        return refusal(StatusCode::BAD_REQUEST, "invalid import idempotency key");
    }
    if request.format == TransferFormat::Native {
        return refusal(
            StatusCode::BAD_REQUEST,
            "native bundles require native restore; choose a document interchange format",
        );
    }
    let durability = match super::Durability::of_header(&headers) {
        Ok(super::Durability::Global) => "global",
        Ok(_) => "regional",
        Err(error) => return error.into_response(),
    };
    let key_version = match document_scope(host.engine())
        .ok()
        .and_then(|scope| host.engine().artifact_key_source(&scope).ok())
        .and_then(|source| source.current_lease().ok())
    {
        Some(lease) => lease.metadata().version().to_text(),
        None => return refusal(StatusCode::SERVICE_UNAVAILABLE, "import key lease unavailable"),
    };
    let owner = host.engine().database_owner();
    let identity = serde_json::to_vec(&(
        owner.tenant().to_string(),
        owner.project().to_string(),
        owner.database().to_text(),
        &request.idempotency_key,
    ))
    .expect("identity serializes");
    let id = format!("import-{}", blake3::hash(&identity).to_hex());
    let job = ImportJob {
        operation_id: id.clone(),
        tenant_id: owner.tenant().to_string(),
        project_id: owner.project().to_string(),
        database_id: owner.database().to_text(),
        format: serde_json::to_value(request.format)
            .expect("format serializes")
            .as_str()
            .expect("format string")
            .into(),
        durability: durability.into(),
        key_version,
        pending_operation: None,
        pending_cursor: None,
        pending_records: None,
        input_digest: None,
        input_length: None,
        input_path: None,
        input_committed: false,
        cursor: 0,
        committed_records: 0,
        state: "uploading".into(),
        error: None,
    };
    if runtime.store.admit(&job).is_err() {
        return refusal(StatusCode::SERVICE_UNAVAILABLE, "import metadata unavailable");
    }
    match owned_job(&runtime, &host, &id) {
        Ok(stored) if stored.format == job.format && stored.durability == job.durability => {
            (StatusCode::ACCEPTED, Json(stored)).into_response()
        }
        Ok(_) => {
            refusal(StatusCode::CONFLICT, "idempotency key belongs to a different import request")
        }
        Err(error) => error,
    }
}

#[utoipa::path(get, path = "/api/v1/databases/{name}/imports/{id}", responses((status = 200, description = "Import progress and resumable state", body = ImportJob)))]
pub(super) async fn get_import(
    State(state): State<Arc<ApiState>>,
    Path((name, id)): Path<(String, String)>,
    Extension(principal): Extension<Principal>,
) -> Response {
    let (runtime, host) = match resolve(&state, &name, &principal, false) {
        Ok(value) => value,
        Err(error) => return error,
    };
    match owned_job(&runtime, &host, &id) {
        Ok(job) => Json(job).into_response(),
        Err(error) => error,
    }
}

#[utoipa::path(put, path = "/api/v1/databases/{name}/imports/{id}/input", request_body(content = String, content_type = "application/octet-stream"), responses((status = 202, description = "Encrypted upload committed and import queued", body = ImportJob)))]
pub(super) async fn upload_import(
    State(state): State<Arc<ApiState>>,
    Path((name, id)): Path<(String, String)>,
    Extension(principal): Extension<Principal>,
    mut body: axum::body::Body,
) -> Response {
    use http_body_util::BodyExt;
    let (runtime, host) = match resolve(&state, &name, &principal, true) {
        Ok(value) => value,
        Err(error) => return error,
    };
    let job = match owned_job(&runtime, &host, &id) {
        Ok(job) => job,
        Err(error) => return error,
    };
    if job.input_committed {
        return refusal(StatusCode::CONFLICT, "import input already committed");
    }
    let cipher = match cipher(&host, &job.key_version) {
        Ok(cipher) => cipher,
        Err(_) => return refusal(StatusCode::SERVICE_UNAVAILABLE, "import key lease unavailable"),
    };
    let mut writer =
        match crate::import_runtime::ImportInputWriter::create(&runtime.root, &id, cipher) {
            Ok(writer) => writer,
            Err(_) => return refusal(StatusCode::CONFLICT, "import upload is already in progress"),
        };
    while let Some(chunk) = body.frame().await {
        let chunk = match chunk {
            Ok(frame) => match frame.into_data() {
                Ok(chunk) => chunk,
                Err(_) => continue,
            },
            Err(_) => return refusal(StatusCode::BAD_REQUEST, "import upload interrupted"),
        };
        for chunk in chunk.chunks(65536) {
            if writer.write_chunk(chunk).is_err() {
                return refusal(
                    StatusCode::BAD_REQUEST,
                    "import input exceeds bounds or encryption is unavailable",
                );
            }
        }
    }
    if writer.finish_and_commit(&runtime.store).is_err() {
        return refusal(
            StatusCode::SERVICE_UNAVAILABLE,
            "encrypted import upload could not commit",
        );
    }
    if launch(&state, &runtime, host, &id).is_err() {
        return refusal(
            StatusCode::SERVICE_UNAVAILABLE,
            "import queued; resume when database is available",
        );
    }
    match runtime.store.get(&id) {
        Ok(Some(job)) => (StatusCode::ACCEPTED, Json(job)).into_response(),
        _ => refusal(StatusCode::SERVICE_UNAVAILABLE, "import metadata unavailable"),
    }
}

#[utoipa::path(post, path = "/api/v1/databases/{name}/imports/{id}/resume", responses((status = 202, description = "Resume accepted; uncertain batches are resolved before writing", body = ImportJob)))]
pub(super) async fn resume_import(
    State(state): State<Arc<ApiState>>,
    Path((name, id)): Path<(String, String)>,
    Extension(principal): Extension<Principal>,
) -> Response {
    let (runtime, host) = match resolve(&state, &name, &principal, true) {
        Ok(value) => value,
        Err(error) => return error,
    };
    let job = match owned_job(&runtime, &host, &id) {
        Ok(job) => job,
        Err(error) => return error,
    };
    if !job.input_committed {
        return refusal(StatusCode::CONFLICT, "complete the input upload before resuming");
    }
    if runtime.store.queue_resume(&id).is_err() {
        return refusal(StatusCode::SERVICE_UNAVAILABLE, "import metadata unavailable");
    }
    match launch(&state, &runtime, host, &id) {
        Ok(()) => (StatusCode::ACCEPTED, Json(job)).into_response(),
        Err(_) => refusal(StatusCode::SERVICE_UNAVAILABLE, "import resume unavailable"),
    }
}

fn launch(
    state: &Arc<ApiState>,
    runtime: &Arc<ImportRuntime>,
    host: Arc<terrabase_tbep::member_host::MemberHost>,
    id: &str,
) -> Result<(), String> {
    let mut tasks = runtime.tasks.lock().map_err(|_| "import tasks unavailable")?;
    if runtime.cancellation.load(Ordering::Acquire) {
        return Err("import runtime stopping".into());
    }
    let Ok(permit) = Arc::clone(&runtime.permits).try_acquire_owned() else {
        return Ok(());
    };
    let job = runtime.store.get(id).map_err(|e| e.to_string())?.ok_or("missing job")?;
    let cipher = cipher(&host, &job.key_version)?;
    let reader =
        ImportInputReader::open(job.input_path.as_deref().ok_or("input unavailable")?, id, cipher)
            .map_err(|e| e.to_string())?;
    let mut headers = HeaderMap::new();
    headers.insert(
        "x-terrascale-durability",
        job.durability.parse().map_err(|_| "invalid durability")?,
    );
    let target = EngineImportWriteTarget::new(
        Arc::clone(state),
        host,
        job.tenant_id.clone(),
        job.project_id.clone(),
        headers,
    );
    if !runtime.store.claim(id).map_err(|e| e.to_string())? {
        return Ok(());
    }
    let job = runtime.store.get(id).map_err(|e| e.to_string())?.ok_or("missing claimed job")?;
    let owner = Arc::clone(runtime);
    let task = tokio::task::spawn_blocking(move || {
        let _permit = permit;
        if execute_import(&owner.store, &job, reader, &target, &owner.cancellation).is_err() {
            if let Ok(Some(current)) = owner.store.get(&job.operation_id) {
                if current.pending_operation.is_some() {
                    let _ = owner.store.hold(&job.operation_id);
                } else {
                    let _ = owner
                        .store
                        .fail(&job.operation_id, "import stopped; inspect input and resume");
                }
            }
        }
    });
    tasks.retain(|task| !task.is_finished());
    tasks.push(task);
    Ok(())
}

pub(crate) fn recover_imports(state: &Arc<ApiState>) -> Result<(), String> {
    let Some(runtime) = &state.imports else {
        return Ok(());
    };
    for job in runtime.store.list().map_err(|e| e.to_string())? {
        if job.state == "queued" {
            if let Ok(host) = super::document_host(state, &job.database_id) {
                let _ = launch(state, runtime, host, &job.operation_id);
            }
        }
    }
    Ok(())
}

pub(crate) fn spawn_imports(state: &Arc<ApiState>) -> Option<tokio::task::JoinHandle<()>> {
    let runtime = state.imports.clone()?;
    let state = Arc::clone(state);
    Some(tokio::spawn(async move {
        while !runtime.cancellation.load(Ordering::Acquire) {
            let _ = recover_imports(&state);
            tokio::time::sleep(std::time::Duration::from_secs(1)).await;
        }
    }))
}

#[utoipa::path(get, path = "/api/v1/databases/{name}/imports", responses((status = 200, description = "Discover admitted imports after restart", body = [ImportJob])))]
pub(super) async fn list_imports(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
    Extension(principal): Extension<Principal>,
) -> Response {
    let (runtime, host) = match resolve(&state, &name, &principal, false) {
        Ok(value) => value,
        Err(error) => return error,
    };
    let owner = host.engine().database_owner();
    match runtime.store.list() {
        Ok(jobs) => Json(
            jobs.into_iter()
                .filter(|job| {
                    job.tenant_id == owner.tenant().to_string()
                        && job.project_id == owner.project().to_string()
                        && job.database_id == owner.database().to_text()
                })
                .collect::<Vec<_>>(),
        )
        .into_response(),
        Err(_) => refusal(StatusCode::SERVICE_UNAVAILABLE, "import metadata unavailable"),
    }
}

#[cfg(all(test, target_os = "linux"))]
mod tests {
    use super::*;
    use axum::{body::Body, http::Request};
    use tower::ServiceExt;

    async fn request(
        app: &axum::Router,
        method: &str,
        path: &str,
        body: &str,
    ) -> (StatusCode, serde_json::Value) {
        let response = app
            .clone()
            .oneshot(
                Request::builder()
                    .method(method)
                    .uri(path)
                    .header("authorization", "Bearer tskey_test_abcdefghijklmnop")
                    .header("content-type", "application/json")
                    .body(Body::from(body.to_owned()))
                    .unwrap(),
            )
            .await
            .unwrap();
        let status = response.status();
        let bytes = axum::body::to_bytes(response.into_body(), 2 * 1024 * 1024).await.unwrap();
        (
            status,
            serde_json::from_slice(&bytes)
                .unwrap_or_else(|_| serde_json::json!({"raw":String::from_utf8_lossy(&bytes)})),
        )
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn http_import_encrypts_writes_and_survives_runtime_restart() {
        let (registry, _member, records, root) = crate::admitted_databases::tests::fixture();
        // The fixture accepts Writer epoch 1; mirror production admission's
        // explicit durable activation before importing through that writer.
        registry
            .resolve(&records[0].database_id)
            .unwrap()
            .unwrap()
            .host
            .engine()
            .activate_origin_epoch(1)
            .unwrap();
        let mut state = super::super::tests::state_with_keys_and_control();
        let record = &records[0];
        let runtime = Arc::new(ImportRuntime::open(root.join("imports")).unwrap());
        {
            let state = Arc::get_mut(&mut state).unwrap();
            state.keys.as_ref().unwrap().register_for_owner(
                "tskey_test_abcdefghijklmnop",
                "import",
                crate::api_keys::Scope::every_database(Verbs::ALL),
                (record.tenant_id.clone(), record.project_id.clone()),
                None,
            );
            state.admitted_databases = Some(Arc::new(registry));
            state.imports = Some(Arc::clone(&runtime));
            state.standalone_durability = true;
        }
        let app = super::super::router(Arc::clone(&state));
        let base = format!("/api/v1/databases/{}/imports", record.database_id);
        let (status, admitted) = request(
            &app,
            "POST",
            &base,
            r#"{"idempotency_key":"route-regression","format":"jsonl"}"#,
        )
        .await;
        assert_eq!(status, StatusCode::ACCEPTED, "{admitted}");
        let id = admitted["operation_id"].as_str().unwrap();
        let path = format!("{base}/{id}");
        let input = "{\"collection\":\"items\",\"id\":\"one\",\"value\":{\"secret\":\"never-plaintext-upload\"}}\n";
        let (status, uploaded) = request(&app, "PUT", &format!("{path}/input"), input).await;
        assert_eq!(status, StatusCode::ACCEPTED, "{uploaded}");
        tokio::time::timeout(std::time::Duration::from_secs(15), async {
            loop {
                let (_, job) = request(&app, "GET", &path, "").await;
                assert!(!matches!(job["state"].as_str(), Some("failed" | "unknown")), "{job}");
                if job["state"] == "completed" {
                    assert_eq!(job["committed_records"], 1);
                    break;
                }
                tokio::time::sleep(std::time::Duration::from_millis(10)).await;
            }
        })
        .await
        .unwrap();
        let job = runtime.store.get(id).unwrap().unwrap();
        let encrypted = std::fs::read(job.input_path.as_ref().unwrap()).unwrap();
        assert!(
            !encrypted
                .windows(b"never-plaintext-upload".len())
                .any(|bytes| bytes == b"never-plaintext-upload")
        );
        let (status, document) = request(
            &app,
            "GET",
            &format!("/api/v1/databases/{}/collections/items/documents/one", record.database_id),
            "",
        )
        .await;
        assert_eq!(status, StatusCode::OK, "{document}");
        runtime.shutdown().await;
        let reopened = ImportRuntime::open(root.join("imports")).unwrap();
        assert_eq!(reopened.store.get(id).unwrap().unwrap().committed_records, 1);
        let (_, retry) = request(
            &app,
            "POST",
            &base,
            r#"{"idempotency_key":"route-regression","format":"jsonl"}"#,
        )
        .await;
        assert_eq!(retry["operation_id"], id);
        assert_eq!(retry["committed_records"], 1);
    }

    struct UncertainTarget(std::sync::atomic::AtomicUsize);
    impl ImportWriteTarget for UncertainTarget {
        fn commit_import_batch(
            &self,
            _: &str,
            _: &str,
            _: &[ImportRecord],
        ) -> Result<ImportCommit, String> {
            self.0.fetch_add(1, Ordering::SeqCst);
            Ok(ImportCommit::Unknown)
        }
    }

    struct ResolvedTarget<'a>(&'a UncertainTarget);
    impl ImportWriteTarget for ResolvedTarget<'_> {
        fn resolve_import_batch(&self, _: &str, records: u64) -> Result<ImportCommit, String> {
            Ok(ImportCommit::Committed { records })
        }

        fn commit_import_batch(
            &self,
            operation: &str,
            database: &str,
            records: &[ImportRecord],
        ) -> Result<ImportCommit, String> {
            self.0.commit_import_batch(operation, database, records)
        }
    }

    struct RejectionLedgerTarget {
        attempts: std::sync::Mutex<Vec<String>>,
    }

    impl ImportWriteTarget for RejectionLedgerTarget {
        fn commit_import_batch(
            &self,
            operation: &str,
            _: &str,
            records: &[ImportRecord],
        ) -> Result<ImportCommit, String> {
            let mut attempts = self.attempts.lock().unwrap();
            attempts.push(operation.to_owned());
            // Like SQLite's durable rejection ledger, this identity can never
            // become a successful write even after the original refusal clears.
            if operation == attempts[0] {
                Ok(ImportCommit::Rejected)
            } else {
                Ok(ImportCommit::Committed { records: records.len() as u64 })
            }
        }
    }

    #[test]
    fn rejected_batch_resumes_after_restart_with_new_attempt_identity() {
        let (registry, _member, records, root) = crate::admitted_databases::tests::fixture();
        let host = &registry.resolve(&records[0].database_id).unwrap().unwrap().host;
        let lease = host
            .engine()
            .artifact_key_source(&document_scope(host.engine()).unwrap())
            .unwrap()
            .current_lease()
            .unwrap();
        let version = lease.metadata().version().to_text();
        let runtime = ImportRuntime::open(root.join("imports")).unwrap();
        let job = ImportJob {
            operation_id: "rejected-regression".into(),
            tenant_id: records[0].tenant_id.clone(),
            project_id: records[0].project_id.clone(),
            database_id: records[0].database_id.clone(),
            format: "jsonl".into(),
            durability: "regional".into(),
            key_version: version,
            pending_operation: None,
            pending_cursor: None,
            pending_records: None,
            input_digest: None,
            input_length: None,
            input_path: None,
            input_committed: false,
            cursor: 0,
            committed_records: 0,
            state: "uploading".into(),
            error: None,
        };
        runtime.store.admit(&job).unwrap();
        let mut writer = crate::import_runtime::ImportInputWriter::create(
            &runtime.root,
            &job.operation_id,
            cipher(host, &job.key_version).unwrap(),
        )
        .unwrap();
        writer.write_chunk(b"{\"collection\":\"items\",\"id\":\"one\",\"value\":{}}\n").unwrap();
        writer.finish_and_commit(&runtime.store).unwrap();
        let target = RejectionLedgerTarget { attempts: Default::default() };
        assert!(runtime.store.claim(&job.operation_id).unwrap());
        let current = runtime.store.get(&job.operation_id).unwrap().unwrap();
        let reader = ImportInputReader::open(
            current.input_path.as_ref().unwrap(),
            &current.operation_id,
            cipher(host, &current.key_version).unwrap(),
        )
        .unwrap();
        assert!(matches!(
            execute_import(&runtime.store, &current, reader, &target, &AtomicBool::new(false)),
            Err(ImportExecutionError::Rejected(_))
        ));
        let failed = runtime.store.get(&job.operation_id).unwrap().unwrap();
        assert_eq!((failed.cursor, failed.committed_records), (0, 0));
        assert!(failed.pending_operation.is_none());
        drop(runtime);
        let runtime = ImportRuntime::open(root.join("imports")).unwrap();
        runtime.store.queue_resume(&job.operation_id).unwrap();
        assert!(runtime.store.claim(&job.operation_id).unwrap());
        let current = runtime.store.get(&job.operation_id).unwrap().unwrap();
        let reader = ImportInputReader::open(
            current.input_path.as_ref().unwrap(),
            &current.operation_id,
            cipher(host, &current.key_version).unwrap(),
        )
        .unwrap();
        let completed =
            execute_import(&runtime.store, &current, reader, &target, &AtomicBool::new(false))
                .unwrap();
        assert_eq!(completed.operation_id, job.operation_id);
        assert_eq!(completed.state, "completed");
        assert_eq!(completed.committed_records, 1);
        let attempts = target.attempts.lock().unwrap();
        assert_eq!(attempts.len(), 2);
        assert_ne!(attempts[0], attempts[1]);
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn pending_before_submission_replays_original_identity_after_restart() {
        tokio::task::spawn_blocking(|| {
            let (registry, _member, records, root) = crate::admitted_databases::tests::fixture();
            let host = &registry.resolve(&records[0].database_id).unwrap().unwrap().host;
            host.engine().activate_origin_epoch(1).unwrap();
            let lease = host
                .engine()
                .artifact_key_source(&document_scope(host.engine()).unwrap())
                .unwrap()
                .current_lease()
                .unwrap();
            let version = lease.metadata().version().to_text();
            let runtime = ImportRuntime::open(root.join("imports")).unwrap();
            let job = ImportJob {
                operation_id: "presubmit-regression".into(),
                tenant_id: records[0].tenant_id.clone(),
                project_id: records[0].project_id.clone(),
                database_id: records[0].database_id.clone(),
                format: "jsonl".into(),
                durability: "regional".into(),
                key_version: version,
                pending_operation: None,
                pending_cursor: None,
                pending_records: None,
                input_digest: None,
                input_length: None,
                input_path: None,
                input_committed: false,
                cursor: 0,
                committed_records: 0,
                state: "uploading".into(),
                error: None,
            };
            runtime.store.admit(&job).unwrap();
            let mut writer = crate::import_runtime::ImportInputWriter::create(
                &runtime.root,
                &job.operation_id,
                cipher(host, &job.key_version).unwrap(),
            )
            .unwrap();
            writer
                .write_chunk(b"{\"collection\":\"items\",\"id\":\"one\",\"value\":{}}\n")
                .unwrap();
            writer.finish_and_commit(&runtime.store).unwrap();
            assert!(runtime.store.claim(&job.operation_id).unwrap());
            let current = runtime.store.get(&job.operation_id).unwrap().unwrap();
            let attempt = "persisted-before-submission";
            runtime
                .store
                .begin_batch(&job.operation_id, attempt, current.input_length.unwrap(), 1)
                .unwrap();
            drop(runtime);
            let runtime = ImportRuntime::open(root.join("imports")).unwrap();
            let recovered = runtime.store.get(&job.operation_id).unwrap().unwrap();
            assert_eq!(recovered.state, "unknown");
            assert_eq!(recovered.pending_operation.as_deref(), Some(attempt));
            assert!(runtime.store.claim(&job.operation_id).unwrap());
            let current = runtime.store.get(&job.operation_id).unwrap().unwrap();
            let reader = ImportInputReader::open(
                current.input_path.as_ref().unwrap(),
                &current.operation_id,
                cipher(host, &current.key_version).unwrap(),
            )
            .unwrap();
            let mut state = super::super::tests::state_with_keys_and_control();
            Arc::get_mut(&mut state).unwrap().standalone_durability = true;
            let target = EngineImportWriteTarget::new(
                state,
                Arc::clone(host),
                records[0].tenant_id.clone(),
                records[0].project_id.clone(),
                HeaderMap::new(),
            );
            let completed =
                execute_import(&runtime.store, &current, reader, &target, &AtomicBool::new(false))
                    .unwrap();
            assert_eq!(completed.state, "completed");
            assert_eq!(completed.committed_records, 1);
            assert!(completed.pending_operation.is_none());
            let scope = document_scope(host.engine()).unwrap();
            assert!(matches!(
                host.engine()
                    .resolve_commit(&scope, OperationId::try_new(attempt).unwrap())
                    .unwrap(),
                terrabase::DurableResolution::Committed(_)
            ));
        })
        .await
        .unwrap();
    }

    #[test]
    fn unknown_resume_resolves_before_any_second_write() {
        let (registry, _member, records, root) = crate::admitted_databases::tests::fixture();
        let host = &registry.resolve(&records[0].database_id).unwrap().unwrap().host;
        let lease = host
            .engine()
            .artifact_key_source(&document_scope(host.engine()).unwrap())
            .unwrap()
            .current_lease()
            .unwrap();
        let version = lease.metadata().version().to_text();
        let runtime = ImportRuntime::open(root.join("imports")).unwrap();
        let job = ImportJob {
            operation_id: "unknown-regression".into(),
            tenant_id: records[0].tenant_id.clone(),
            project_id: records[0].project_id.clone(),
            database_id: records[0].database_id.clone(),
            format: "jsonl".into(),
            durability: "regional".into(),
            key_version: version,
            pending_operation: None,
            pending_cursor: None,
            pending_records: None,
            input_digest: None,
            input_length: None,
            input_path: None,
            input_committed: false,
            cursor: 0,
            committed_records: 0,
            state: "uploading".into(),
            error: None,
        };
        runtime.store.admit(&job).unwrap();
        let mut writer = crate::import_runtime::ImportInputWriter::create(
            &runtime.root,
            &job.operation_id,
            cipher(host, &job.key_version).unwrap(),
        )
        .unwrap();
        writer.write_chunk(b"{\"collection\":\"items\",\"id\":\"one\",\"value\":{}}\n").unwrap();
        writer.finish_and_commit(&runtime.store).unwrap();
        let target = UncertainTarget(std::sync::atomic::AtomicUsize::new(0));
        for _ in 0..2 {
            assert!(runtime.store.claim(&job.operation_id).unwrap());
            let current = runtime.store.get(&job.operation_id).unwrap().unwrap();
            let reader = ImportInputReader::open(
                current.input_path.as_ref().unwrap(),
                &current.operation_id,
                cipher(host, &current.key_version).unwrap(),
            )
            .unwrap();
            assert!(matches!(
                execute_import(&runtime.store, &current, reader, &target, &AtomicBool::new(false)),
                Err(ImportExecutionError::Unknown(_))
            ));
        }
        assert_eq!(target.0.load(Ordering::SeqCst), 1);
        let held = runtime.store.get(&job.operation_id).unwrap().unwrap();
        assert_eq!(held.state, "unknown");
        assert_eq!((held.cursor, held.committed_records), (0, 0));
        assert!(held.pending_operation.is_some());

        // Simulate a process dying after claiming the unresolved attempt.
        assert!(runtime.store.claim(&job.operation_id).unwrap());
        let mut recovering = runtime.store.get(&job.operation_id).unwrap().unwrap();
        assert_eq!(recovering.pending_operation, held.pending_operation);
        recovering.state = "unknown".into();
        drop(runtime);
        let runtime = ImportRuntime::open(root.join("imports")).unwrap();
        assert_eq!(runtime.store.get(&job.operation_id).unwrap().unwrap(), recovering);

        // Once the durable engine receipt becomes available, recovery advances
        // the original checkpoint without submitting or billing the batch again.
        assert!(runtime.store.claim(&job.operation_id).unwrap());
        let current = runtime.store.get(&job.operation_id).unwrap().unwrap();
        let reader = ImportInputReader::open(
            current.input_path.as_ref().unwrap(),
            &current.operation_id,
            cipher(host, &current.key_version).unwrap(),
        )
        .unwrap();
        let completed = execute_import(
            &runtime.store,
            &current,
            reader,
            &ResolvedTarget(&target),
            &AtomicBool::new(false),
        )
        .unwrap();
        assert_eq!(completed.operation_id, job.operation_id);
        assert_eq!(completed.state, "completed");
        assert_eq!(completed.committed_records, 1);
        assert_eq!(completed.cursor, completed.input_length.unwrap());
        assert!(completed.pending_operation.is_none());
        assert_eq!(target.0.load(Ordering::SeqCst), 1);
    }
}
