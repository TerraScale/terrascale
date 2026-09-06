//! Database-scoped exports on the admitted member router.

use super::*;
use crate::export_api::{self as export, ExportService, ExportSource, ExportSourceError};
use serde::{Deserialize, Serialize};
use terrabase_tbep::member_host::MemberHost;

#[derive(Deserialize, utoipa::ToSchema)]
#[serde(deny_unknown_fields)]
pub(super) struct CreateExport {
    /// Stable retry identity. Reusing it returns the same admitted operation.
    idempotency_key: String,
}

#[derive(Deserialize, utoipa::ToSchema)]
#[serde(deny_unknown_fields)]
pub(super) struct ResumeExport {
    /// Latest opaque cursor returned by export status.
    resume_cursor: String,
}

fn export_error(error: &export::ExportError) -> ApiError {
    ApiError {
        status: export::error_status(error),
        code: "export_failed",
        message: error.public_message().into(),
        fix: "check export status; resume a failed export with its latest resume cursor".into(),
        placement: None,
    }
}

fn source_error() -> ExportSourceError {
    ExportSourceError::Storage("durable export read failed".into())
}

struct MemberSource {
    host: Arc<MemberHost>,
    database: String,
    branches: Vec<export::MetadataMember>,
}

impl ExportSource for MemberSource {
    fn snapshot(&self, database: &str) -> Result<export::ExportSnapshot, ExportSourceError> {
        if database != self.database { return Err(source_error()); }
        let engine = self.host.engine();
        let scope = document_scope(engine).map_err(|()| source_error())?;
        let captured = engine.capture_export_metadata(&scope, database, "main")
            .map_err(|_| source_error())?;
        let schemas = captured.schemas.into_iter().map(|(collection, bytes)| {
            let record: crate::schema_api::DurableSchemaRecord =
                serde_json::from_slice(&bytes).map_err(|_| source_error())?;
            Ok(export::MetadataMember {
                name: format!("{collection}.json"),
                bytes: serde_json::to_vec(&record.document).map_err(|_| source_error())?,
            })
        }).collect::<Result<Vec<_>, ExportSourceError>>()?;
        let indexes = captured.indexes.into_iter().enumerate().map(|(number, index)| {
            Ok(export::MetadataMember {
                name: format!("{number:08}.json"),
                bytes: serde_json::to_vec(&serde_json::json!({
                    "collection": index.collection, "field": index.field, "active": index.active,
                })).map_err(|_| source_error())?,
            })
        }).collect::<Result<Vec<_>, ExportSourceError>>()?;
        Ok(export::ExportSnapshot {
            boundary: captured.boundary,
            database_id: engine.database_id().to_text(),
            schemas, indexes, branches: self.branches.clone(), estimated_records: None,
        })
    }

    fn page(&self, database: &str, snapshot: &export::ExportSnapshot,
        phase: export::ExportPhase, after: Option<&[u8]>, limit: usize,
    ) -> Result<export::ExportPage, ExportSourceError> {
        if database != self.database || snapshot.database_id != self.host.engine().database_id().to_text() {
            return Err(source_error());
        }
        let engine = self.host.engine();
        let scope = document_scope(engine).map_err(|()| source_error())?;
        match phase {
            export::ExportPhase::Documents => {
                let cursor: Option<(String, String)> = after.map(serde_json::from_slice)
                    .transpose().map_err(|_| source_error())?;
                let cursor = cursor.map(|(collection, id)| Ok((
                    terrabase::types::CollectionName::try_new(collection).map_err(|_| source_error())?,
                    terrabase::types::DocumentId::try_new(id).map_err(|_| source_error())?,
                ))).transpose()?;
                // Four maximum-size documents keep materialization below the
                // export encoder's eight MiB page budget, including envelopes.
                let rows = engine.export_snapshot_records(&scope, snapshot.boundary, cursor,
                    u32::try_from(limit.min(4)).map_err(|_| source_error())?)
                    .map_err(|_| source_error())?;
                let next_cursor = rows.last().map(|row| serde_json::to_vec(&(
                    row.document().collection().as_str(), row.document().id().as_str(),
                ))).transpose().map_err(|_| source_error())?;
                let done = rows.is_empty();
                let records = rows.iter().map(|row| {
                    let document = row.document();
                    let mut body = String::new();
                    push_value(&mut body, document.value());
                    export::ExportRecord::Document {
                        collection: document.collection().as_str().into(),
                        id: document.id().as_str().into(), version: document.metadata().version(),
                        canonical_json: body.into_bytes(),
                    }
                }).collect();
                Ok(export::ExportPage { records, next_cursor, done })
            }
            export::ExportPhase::History => self.history(snapshot, after, limit),
            export::ExportPhase::Done => Ok(export::ExportPage { records: Vec::new(), next_cursor: None, done: true }),
        }
    }
}

fn service(state: &ApiState, database: &str) -> Result<(Arc<ExportService>, export::ExportDestination, export::PlacementLock), ApiError> {
    let host = document_host(state, database)?;
    let runtime = state.exports.as_ref().ok_or_else(|| export_error(&export::ExportError::DependencyUnavailable))?;
    let record = state.control.as_ref().ok_or_else(ApiError::control_unavailable)?
        .get(database).map_err(ApiError::control)?.ok_or_else(|| ApiError::database_not_found(database))?;
    let branches = vec![export::MetadataMember {
        name: "database.json".into(),
        bytes: serde_json::to_vec(&serde_json::json!({ "name": database, "parent": record.parent }))
            .map_err(|_| ApiError::storage_failure())?,
    }];
    let identity = host.engine().database_id().to_text();
    let source = Arc::new(MemberSource { host, database: database.into(), branches });
    let service = runtime.service(&identity, source).map_err(|error| export_error(&error))?;
    let placement = export::PlacementLock::new(record.placement).map_err(|error| export_error(&error))?;
    Ok((service, runtime.destination(), placement))
}

async fn resolve(state: Arc<ApiState>, name: String) -> Result<(Arc<ExportService>, export::ExportDestination, export::PlacementLock), ApiError> {
    tokio::task::spawn_blocking(move || service(&state, &name)).await
        .map_err(|_| ApiError::storage_failure())?
}

#[utoipa::path(post, path = "/api/v1/databases/{name}/exports", request_body = CreateExport,
    responses((status = 202, description = "Durably accepted export", body = export::ExportAccepted)))]
pub(super) async fn create_export(State(state): State<Arc<ApiState>>, Path(name): Path<String>, body: String) -> Response {
    let request: CreateExport = match serde_json::from_str(&body) {
        Ok(request) => request,
        Err(_) => return export_error(&export::ExportError::InvalidRequest("invalid body")).into_response(),
    };
    if request.idempotency_key.is_empty() {
        return export_error(&export::ExportError::InvalidRequest("empty identity")).into_response();
    }
    let (service, destination, placement) = match resolve(state, name.clone()).await {
        Ok(value) => value, Err(error) => return error.into_response(),
    };
    match service.submit(export::ExportRequest { database: name.clone(), destination, placement,
        idempotency_key: Some(request.idempotency_key) }).await {
        Ok(mut accepted) => {
            accepted.status_path = format!("/api/v1/databases/{name}/exports/{}", accepted.job_id);
            export::json_response(StatusCode::ACCEPTED, &accepted)
        }
        Err(error) => export_error(&error).into_response(),
    }
}

#[utoipa::path(get, path = "/api/v1/databases/{name}/exports/{job_id}",
    responses((status = 200, description = "Durable export progress", body = export::ExportStatus)))]
pub(super) async fn get_export(State(state): State<Arc<ApiState>>, Path((name, job)): Path<(String, String)>) -> Response {
    let (service, _, _) = match resolve(state, name).await {
        Ok(value) => value, Err(error) => return error.into_response(),
    };
    match service.status_async(&job).await {
        Ok(Some(status)) => export::json_response(StatusCode::OK, &status),
        Ok(None) => ApiError { status: StatusCode::NOT_FOUND, code: "export_not_found",
            message: "export job not found".into(), fix: "use an export identity from this database".into(), placement: None }.into_response(),
        Err(error) => export_error(&error).into_response(),
    }
}

#[utoipa::path(post, path = "/api/v1/databases/{name}/exports/{job_id}/resume", request_body = ResumeExport,
    responses((status = 200, description = "Resume the existing export", body = export::ExportStatus)))]
pub(super) async fn resume_export(State(state): State<Arc<ApiState>>, Path((name, job)): Path<(String, String)>, body: String) -> Response {
    let request: ResumeExport = match serde_json::from_str(&body) {
        Ok(request) => request,
        Err(_) => return export_error(&export::ExportError::InvalidRequest("invalid body")).into_response(),
    };
    let (service, _, _) = match resolve(state, name).await {
        Ok(value) => value, Err(error) => return error.into_response(),
    };
    match service.resume(&job, &request.resume_cursor).await {
        Ok(status) => export::json_response(StatusCode::OK, &status),
        Err(error) => export_error(&error).into_response(),
    }
}
