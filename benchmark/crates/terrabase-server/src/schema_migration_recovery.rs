//! Authenticated, durable rollback admission and bounded restart recovery.

use super::{ApiState, Arc, IntoResponse, Path, Response, State, document_host, document_scope};
use crate::schema_api::{
    DurableSchemaRecord, MigrationState, SchemaError, SchemaRollback, SchemaTarget, header_text,
    parse_if_match, schema_response, validate_durable_record, validate_idempotency_key,
};
use axum::http::{HeaderMap, StatusCode};
use terrabase::families::migrations::{self, MigrationStatus, RollbackStep};

#[utoipa::path(post, path = "/api/v1/databases/{name}/collections/{collection}/schema/migration/rollback",
    params(("Idempotency-Key" = String, Header, description = "Required rollback request identity"),
        ("If-Match" = String, Header, description = "Required strong ETag from GET schema")),
    responses((status = 202, description = "Rollback admitted; poll GET schema until migration is absent", body = crate::schema_api::SchemaDocument),
        (status = 200, description = "Idempotent replay of completed rollback", body = crate::schema_api::SchemaDocument),
        (status = 409, description = "Migration cannot be rolled back"),
        (status = 412, description = "Schema generation changed")))]
pub(super) async fn rollback_schema(
    State(state): State<Arc<ApiState>>,
    Path((name, collection)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    tokio::task::spawn_blocking(move || match admit(&state, &name, &collection, &headers) {
        Ok(record) => schema_response(
            if record.previous_schema.is_some() { StatusCode::ACCEPTED } else { StatusCode::OK },
            &record.document,
            true,
        ),
        Err(error) => error.into_response(),
    })
    .await
    .unwrap_or_else(|_| {
        error(503, "rollback_unavailable", "rollback worker could not complete").into_response()
    })
}

fn error(status: u16, code: &str, message: &str) -> SchemaError {
    SchemaError::new(
        status,
        code,
        message,
        "GET the schema and inspect migration status before retrying",
    )
}

fn admit(
    state: &ApiState,
    name: &str,
    collection: &str,
    headers: &HeaderMap,
) -> Result<DurableSchemaRecord, SchemaError> {
    if header_text(headers, "x-terrascale-branch")?.is_some_and(|branch| branch != "main") {
        return Err(error(400, "invalid_schema_target", "use the branch database's main schema"));
    }
    let key = header_text(headers, "idempotency-key")?.ok_or_else(|| {
        error(400, "idempotency_key_required", "rollback requires Idempotency-Key")
    })?;
    validate_idempotency_key(&key)?;
    let generation = parse_if_match(headers)?
        .ok_or_else(|| error(428, "schema_precondition_required", "rollback requires If-Match"))?;
    let target = SchemaTarget {
        database: name.to_owned(),
        branch: "main".into(),
        collection: collection.to_owned(),
    };
    crate::schema_api::validate_target(&target)?;
    let host = document_host(state, name)
        .map_err(|_| error(503, "database_unavailable", "database host is unavailable"))?;
    let engine = host.engine();
    let expected = engine
        .schema_metadata_load(name, "main", collection)
        .map_err(|_| error(503, "schema_unavailable", "schema metadata is unavailable"))?
        .ok_or_else(|| error(404, "schema_not_found", "collection has no schema"))?;
    let mut record: DurableSchemaRecord = serde_json::from_slice(&expected)
        .map_err(|_| error(503, "schema_unavailable", "schema metadata is malformed"))?;
    validate_durable_record(&target, &record)?;
    if record.document.identity.database_id != engine.database_owner().database().to_text() {
        return Err(error(409, "database_identity_changed", "schema database identity changed"));
    }
    if let Some(rollback) = &record.rollback {
        if rollback.idempotency_key == key && rollback.generation == generation {
            return Ok(record);
        }
        if record.previous_schema.is_some() || rollback.idempotency_key == key {
            return Err(error(
                409,
                "rollback_conflict",
                "another rollback request owns this migration",
            ));
        }
    }
    if record.document.generation != generation {
        return Err(error(412, "generation_mismatch", "schema generation changed"));
    }
    if record.idempotency.contains_key(&key) {
        return Err(error(
            409,
            "idempotency_conflict",
            "request key was used by another schema operation",
        ));
    }
    if record.previous_schema.is_none()
        || !record
            .document
            .migration
            .as_ref()
            .is_some_and(|migration| migration.state == MigrationState::Failed)
    {
        return Err(error(
            409,
            "rollback_not_available",
            "only a failed reversible migration can be rolled back",
        ));
    }
    // The core marker is staged by the same transaction as this intent. A
    // stale forward worker cannot commit after rollback ownership changes.
    record.rollback = Some(SchemaRollback { idempotency_key: key, generation });
    let migration = record.document.migration.as_mut().expect("checked migration");
    migration.state = MigrationState::RollingBack;
    migration.message =
        "rollback admitted; previous schema will be restored in bounded pages".into();
    match super::schema_migration_runtime::persist_with_rollback(engine, &expected, &record, true) {
        Ok(true) => Ok(record),
        Ok(false) => {
            Err(error(409, "generation_conflict", "schema or migration changed during admission"))
        }
        Err(_) => Err(error(
            409,
            "rollback_not_available",
            "migration is completed, irreversible, or unavailable",
        )),
    }
}

pub(super) fn advance(
    engine: &terrabase::Engine,
    record: &mut DurableSchemaRecord,
) -> Result<bool, String> {
    let contract = super::schema_migration_runtime::contract(record)?;
    let scope = document_scope(engine).map_err(|()| "database scope unavailable")?;
    match migrations::status(engine, &scope, contract.collection())
        .map_err(|error| format!("rollback status unavailable: {error:?}"))?
    {
        MigrationStatus::RollingBack { .. } => {
            if let RollbackStep::PageRestored { .. } =
                migrations::rollback(engine, &scope, &contract).map_err(|error| {
                    format!("rollback page refused; repair conflicting documents: {error:?}")
                })?
            {
                return Ok(false);
            }
        }
        // The final rollback page clears the core marker atomically with
        // restored documents. The envelope intent survives that crash gap.
        MigrationStatus::None => {}
        _ => return Err("rollback ownership differs from the durable recovery intent".into()),
    }
    let previous = record.previous_schema.take().ok_or("rollback source schema missing")?;
    let version = previous.version.ok_or("rollback source version missing")?;
    let generation =
        record.document.generation.checked_add(1).ok_or("schema generation exhausted")?;
    record.document.schema = previous;
    record.document.schema_version = version;
    record.document.generation = generation;
    record.document.migration = None;
    record.migration_plan = None;
    Ok(true)
}

#[cfg(test)]
#[path = "schema_migration_recovery_tests.rs"]
mod tests;
