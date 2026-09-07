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
pub(crate) struct ImportRuntime {
    pub(crate) store: Arc<ImportStore>,
    pub(crate) root: std::path::PathBuf,
    tasks: std::sync::Mutex<Vec<tokio::task::JoinHandle<()>>>,
    cancellation: Arc<AtomicBool>,
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
                    match std::fs::remove_file(path) { Ok(()) => {}, Err(e) if e.kind() == io::ErrorKind::NotFound => {}, Err(e) => return Err(e.to_string()) }
                }
            }
        }
        Ok(Self { store, root, tasks: Default::default(), cancellation: Arc::new(AtomicBool::new(false)) })
    }
    pub(crate) async fn shutdown(&self) {
        self.cancellation.store(true, Ordering::Release);
        let tasks = std::mem::take(&mut *self.tasks.lock().expect("import tasks lock"));
        for task in tasks { let _ = task.await; }
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
    fn resolve_import_batch(&self, _operation: &str, _records: u64) -> Result<ImportCommit, String> { Ok(ImportCommit::Unknown) }
    fn commit_import_batch(
        &self,
        operation_id: &str,
        database_id: &str,
        records: &[ImportRecord],
    ) -> Result<ImportCommit, String>;
}

/// Production import target bound to the same Engine transaction seams as the
/// public document batch route. The import executor supplies deterministic
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
        Self { state, engine, tenant_id, project_id, headers, runtime: tokio::runtime::Handle::current() }
    }
}

impl ImportWriteTarget for EngineImportWriteTarget {
    fn resolve_import_batch(&self, operation: &str, records: u64) -> Result<ImportCommit, String> {
        let engine = self.engine.engine();
        let scope = document_scope(engine).map_err(|_| "document scope unavailable")?;
        let operation = OperationId::try_new(operation).map_err(|_| "invalid operation")?;
        match engine.resolve_commit(&scope, operation).map_err(|_| "resolution unavailable")? {
            terrabase::DurableResolution::Committed(receipt) => {
                let durability = self.runtime.block_on(super::WriteDurability::prepare(&self.state, engine, &scope, &self.headers)).map_err(|e| e.message)?;
                if self.runtime.block_on(durability.confirm(engine, &scope, &receipt)).is_err() { return Ok(ImportCommit::Unknown); }
                Ok(ImportCommit::Committed { records })
            }
            terrabase::DurableResolution::Rejected(_) => Ok(ImportCommit::Rejected),
            terrabase::DurableResolution::Pending => Ok(ImportCommit::Unknown),
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
        let durability = self.runtime.block_on(super::WriteDurability::prepare(&self.state, engine, &scope, &self.headers)).map_err(|e| e.message)?;
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
/// deterministic operation identity, and its cursor is persisted only after
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
    let mut ordinal = committed_records / IMPORT_BATCH_RECORDS as u64;
    let mut batch = Vec::with_capacity(IMPORT_BATCH_RECORDS);
    let mut batch_cursor = cursor;
    let mut batch_error = None;
    let mut result = Ok(());
    let format = parse_format(&job.format)?;

    if let Some(operation) = &job.pending_operation {
        let records = job.pending_records.ok_or_else(|| ImportExecutionError::Store("missing pending count".into()))?;
        match target.resolve_import_batch(operation, records) {
            Ok(ImportCommit::Committed { records: proven }) if proven == records => {
                cursor = job.pending_cursor.ok_or_else(|| ImportExecutionError::Store("missing pending cursor".into()))?;
                committed_records = committed_records.checked_add(records).ok_or_else(|| ImportExecutionError::Store("count overflow".into()))?;
                store.checkpoint(&job.operation_id, cursor, committed_records).map_err(|e| ImportExecutionError::Store(e.to_string()))?;
                ordinal = committed_records / IMPORT_BATCH_RECORDS as u64;
            }
            Ok(ImportCommit::Rejected) => { store.clear_rejected(&job.operation_id).map_err(|e| ImportExecutionError::Store(e.to_string()))?; }
            _ => { let _ = store.hold(&job.operation_id); return Err(ImportExecutionError::Unknown(operation.clone())); }
        }
    }
    let reader = reader.verified_reader(digest, job.input_length.unwrap_or(0))?;
    let decode_result = export_formats::decode_reader(reader, format, 1024 * 1024, |source_offset, record| {
        if cancellation.load(Ordering::Acquire) {
            return Err(io::Error::new(io::ErrorKind::Interrupted, "import cancelled"));
        }
        if source_offset <= cursor { return Ok(()); }
        batch.push(record);
        batch_cursor = source_offset;
        if batch.len() == IMPORT_BATCH_RECORDS {
            match commit_batch(store, target, job, digest, database_id, ordinal, source_offset, &mut batch, committed_records) {
                Ok((next_cursor, next_records)) => { cursor = next_cursor; committed_records = next_records; ordinal += 1; }
                Err(error) => { batch_error = Some(error); return Err(io::Error::other("import batch failed")); }
            }
        }
        Ok(())
    });
    if let Some(error) = batch_error { result = Err(error); }
    else if let Err(error) = decode_result { result = if cancellation.load(Ordering::Acquire) { Err(ImportExecutionError::Cancelled) } else { Err(ImportExecutionError::Codec(error)) }; }
    if result.is_ok() && !batch.is_empty() {
        let offset = batch_cursor.max(job.cursor);
        match commit_batch(
            store,
            target,
            job,
            digest,
            database_id,
            ordinal,
            offset,
            &mut batch,
            committed_records,
        ) {
            Ok((next_cursor, next_records)) => {
                cursor = next_cursor;
                committed_records = next_records;
            }
            Err(error) => result = Err(error),
        }
    }
    if let Err(error) = result {
        let text = match &error {
            ImportExecutionError::Cancelled => "import cancelled".into(),
            ImportExecutionError::Input(_) | ImportExecutionError::Codec(_) => {
                "invalid import input".into()
            }
            ImportExecutionError::Store(_) => "import metadata unavailable".into(),
            ImportExecutionError::Rejected(_) => "import batch rejected".into(),
            ImportExecutionError::Unknown(_) => "import commit outcome unknown".into(),
        };
        if matches!(error, ImportExecutionError::Unknown(_)) { let _ = store.hold(&job.operation_id); }
        else { let _ = store.fail(&job.operation_id, &text); }
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
    digest: &str,
    database_id: &str,
    ordinal: u64,
    cursor: u64,
    batch: &mut Vec<ImportRecord>,
    committed_records: u64,
) -> Result<(u64, u64), ImportExecutionError> {
    let operation = export_formats::batch_operation_id(&job.operation_id, digest, ordinal);
    let count = batch.len() as u64;
    store.begin_batch(&job.operation_id, &operation, cursor, count).map_err(|e| ImportExecutionError::Store(e.to_string()))?;
    let outcome = target
        .commit_import_batch(&operation, database_id, batch)
        .map_err(|error| { let _ = store.clear_rejected(&job.operation_id); ImportExecutionError::Rejected(error) })?;
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


use axum::{extract::{Path, State}, Extension, Json, response::{IntoResponse, Response}, http::{HeaderMap, StatusCode}};
use crate::api_keys::{Principal, Verbs};

#[derive(serde::Deserialize, utoipa::ToSchema)]
#[serde(deny_unknown_fields)]
pub(super) struct CreateImport {
    idempotency_key: String,
    format: TransferFormat,
}

fn refusal(status: StatusCode, message: &'static str) -> Response {
    (status, Json(serde_json::json!({"code":"import_failed", "message":message}))).into_response()
}

fn resolve(state: &ApiState, name: &str, principal: &Principal, write: bool) -> Result<(Arc<ImportRuntime>, Arc<terrabase_tbep::member_host::MemberHost>), Response> {
    if !principal.scope().admits_database(Verbs::ADMIN, name)
        || (write && !principal.scope().admits_database(Verbs::WRITE, name)) {
        return Err(super::ApiError::forbidden("database import permission").into_response());
    }
    let host = super::document_host(state, name).map_err(IntoResponse::into_response)?;
    let runtime = state.imports.clone().ok_or_else(|| refusal(StatusCode::SERVICE_UNAVAILABLE, "import runtime unavailable"))?;
    Ok((runtime, host))
}

fn owned_job(runtime: &ImportRuntime, host: &terrabase_tbep::member_host::MemberHost, id: &str) -> Result<ImportJob, Response> {
    let owner = host.engine().database_owner();
    runtime.store.get(id).map_err(|_| refusal(StatusCode::SERVICE_UNAVAILABLE, "import metadata unavailable"))?
        .filter(|job| job.tenant_id == owner.tenant().to_string() && job.project_id == owner.project().to_string() && job.database_id == owner.database().to_text())
        .ok_or_else(|| refusal(StatusCode::NOT_FOUND, "import operation not found"))
}

fn cipher(host: &terrabase_tbep::member_host::MemberHost) -> Result<crate::import_runtime::ImportLeaseCipher, String> {
    let engine = host.engine();
    let scope = document_scope(engine).map_err(|_| "import scope unavailable")?;
    let lease = engine.artifact_key_lease(&scope).map_err(|_| "import key lease unavailable")?;
    Ok(crate::import_runtime::ImportLeaseCipher::new(lease, engine.database_owner().tenant().to_string(), engine.database_id().to_text()))
}

#[utoipa::path(post, path = "/api/v1/databases/{name}/imports", request_body = CreateImport,
    responses((status = 202, description = "Import upload admitted")))]
pub(super) async fn create_import(State(state): State<Arc<ApiState>>, Path(name): Path<String>, Extension(principal): Extension<Principal>, headers: HeaderMap, Json(request): Json<CreateImport>) -> Response {
    let (runtime, host) = match resolve(&state, &name, &principal, true) { Ok(value) => value, Err(error) => return error };
    if request.idempotency_key.is_empty() || request.idempotency_key.len() > 256 {
        return refusal(StatusCode::BAD_REQUEST, "invalid import idempotency key");
    }
    if request.format == TransferFormat::Native { return refusal(StatusCode::BAD_REQUEST, "native bundles require native restore; choose a document interchange format"); }
    let durability = match super::Durability::of_header(&headers) { Ok(super::Durability::Global) => "global", Ok(_) => "regional", Err(error) => return error.into_response() };
    if cipher(&host).is_err() { return refusal(StatusCode::SERVICE_UNAVAILABLE, "import key lease unavailable"); }
    let owner = host.engine().database_owner();
    let identity = serde_json::to_vec(&(owner.tenant().to_string(), owner.project().to_string(), owner.database().to_text(), &request.idempotency_key)).expect("identity serializes");
    let id = format!("import-{}", blake3::hash(&identity).to_hex());
    let job = ImportJob { operation_id: id.clone(), tenant_id: owner.tenant().to_string(), project_id: owner.project().to_string(), database_id: owner.database().to_text(), format: serde_json::to_value(request.format).expect("format serializes").as_str().expect("format string").into(), durability: durability.into(), pending_operation: None, pending_cursor: None, pending_records: None, input_digest: None, input_length: None, input_path: None, input_committed: false, cursor: 0, committed_records: 0, state: "uploading".into(), error: None };
    if runtime.store.admit(&job).is_err() { return refusal(StatusCode::SERVICE_UNAVAILABLE, "import metadata unavailable"); }
    match owned_job(&runtime, &host, &id) {
        Ok(stored) if stored.format == job.format && stored.durability == job.durability => (StatusCode::ACCEPTED, Json(stored)).into_response(),
        Ok(_) => refusal(StatusCode::CONFLICT, "idempotency key belongs to a different import request"),
        Err(error) => error,
    }
}

#[utoipa::path(get, path = "/api/v1/databases/{name}/imports/{id}", responses((status = 200, description = "Import progress and resumable state")))]
pub(super) async fn get_import(State(state): State<Arc<ApiState>>, Path((name, id)): Path<(String, String)>, Extension(principal): Extension<Principal>) -> Response {
    let (runtime, host) = match resolve(&state, &name, &principal, false) { Ok(value) => value, Err(error) => return error };
    match owned_job(&runtime, &host, &id) { Ok(job) => Json(job).into_response(), Err(error) => error }
}

#[utoipa::path(put, path = "/api/v1/databases/{name}/imports/{id}/input", request_body(content = String, content_type = "application/octet-stream"), responses((status = 202, description = "Encrypted upload committed and import queued")))]
pub(super) async fn upload_import(State(state): State<Arc<ApiState>>, Path((name, id)): Path<(String, String)>, Extension(principal): Extension<Principal>, body: axum::body::Body) -> Response {
    use futures_util::StreamExt;
    let (runtime, host) = match resolve(&state, &name, &principal, true) { Ok(value) => value, Err(error) => return error };
    let job = match owned_job(&runtime, &host, &id) { Ok(job) => job, Err(error) => return error };
    if job.input_committed { return refusal(StatusCode::CONFLICT, "import input already committed"); }
    let cipher = match cipher(&host) { Ok(cipher) => cipher, Err(_) => return refusal(StatusCode::SERVICE_UNAVAILABLE, "import key lease unavailable") };
    let mut writer = match crate::import_runtime::ImportInputWriter::create(&runtime.root, &id, cipher) { Ok(writer) => writer, Err(_) => return refusal(StatusCode::CONFLICT, "import upload is already in progress") };
    let mut stream = body.into_data_stream();
    while let Some(chunk) = stream.next().await {
        let chunk = match chunk { Ok(chunk) => chunk, Err(_) => return refusal(StatusCode::BAD_REQUEST, "import upload interrupted") };
        for chunk in chunk.chunks(65536) {
            if writer.write_chunk(chunk).is_err() { return refusal(StatusCode::BAD_REQUEST, "import input exceeds bounds or encryption is unavailable"); }
        }
    }
    if writer.finish_and_commit(&runtime.store).is_err() { return refusal(StatusCode::SERVICE_UNAVAILABLE, "encrypted import upload could not commit"); }
    if launch(&state, &runtime, host, &id).is_err() { return refusal(StatusCode::SERVICE_UNAVAILABLE, "import queued; resume when database is available"); }
    match runtime.store.get(&id) { Ok(Some(job)) => (StatusCode::ACCEPTED, Json(job)).into_response(), _ => refusal(StatusCode::SERVICE_UNAVAILABLE, "import metadata unavailable") }
}

#[utoipa::path(post, path = "/api/v1/databases/{name}/imports/{id}/resume", responses((status = 202, description = "Resume accepted; uncertain batches are resolved before writing")))]
pub(super) async fn resume_import(State(state): State<Arc<ApiState>>, Path((name, id)): Path<(String, String)>, Extension(principal): Extension<Principal>) -> Response {
    let (runtime, host) = match resolve(&state, &name, &principal, true) { Ok(value) => value, Err(error) => return error };
    let job = match owned_job(&runtime, &host, &id) { Ok(job) => job, Err(error) => return error };
    if !job.input_committed { return refusal(StatusCode::CONFLICT, "complete the input upload before resuming"); }
    match launch(&state, &runtime, host, &id) {
        Ok(()) => (StatusCode::ACCEPTED, Json(job)).into_response(),
        Err(_) => refusal(StatusCode::SERVICE_UNAVAILABLE, "import resume unavailable"),
    }
}

fn launch(state: &Arc<ApiState>, runtime: &Arc<ImportRuntime>, host: Arc<terrabase_tbep::member_host::MemberHost>, id: &str) -> Result<(), String> {
    if runtime.cancellation.load(Ordering::Acquire) { return Err("import runtime stopping".into()); }
    let job = runtime.store.get(id).map_err(|e| e.to_string())?.ok_or("missing job")?;
    let cipher = cipher(&host)?;
    let reader = ImportInputReader::open(job.input_path.as_deref().ok_or("input unavailable")?, id, cipher).map_err(|e| e.to_string())?;
    let mut headers = HeaderMap::new();
    headers.insert("x-terrascale-durability", job.durability.parse().map_err(|_| "invalid durability")?);
    let target = EngineImportWriteTarget::new(Arc::clone(state), host, job.tenant_id.clone(), job.project_id.clone(), headers);
    if !runtime.store.claim(id).map_err(|e| e.to_string())? { return Ok(()); }
    let owner = Arc::clone(runtime);
    let task = tokio::task::spawn_blocking(move || {
        if execute_import(&owner.store, &job, reader, &target, &owner.cancellation).is_err() {
            if let Ok(Some(current)) = owner.store.get(&job.operation_id) {
                if current.pending_operation.is_some() { let _ = owner.store.hold(&job.operation_id); }
                else { let _ = owner.store.fail(&job.operation_id, "import stopped; inspect input and resume"); }
            }
        }
    });
    let mut tasks = runtime.tasks.lock().map_err(|_| "import tasks unavailable")?;
    tasks.retain(|task| !task.is_finished());
    tasks.push(task);
    Ok(())
}

pub(crate) fn recover_imports(state: &Arc<ApiState>) -> Result<(), String> {
    let Some(runtime) = &state.imports else { return Ok(()); };
    for job in runtime.store.list().map_err(|e| e.to_string())? {
        if job.state == "queued" {
            if let Ok(host) = super::document_host(state, &job.database_id) { launch(state, runtime, host, &job.operation_id)?; }
        }
    }
    Ok(())
}
