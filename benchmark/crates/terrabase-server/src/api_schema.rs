//! Schema routes bound to the same admitted database lease as document operations.

use super::*;
use crate::schema_api::{
    DatabaseIdentity, DurableCommit, DurablePortError, DurableSchemaRecord, SchemaDurablePort,
    SchemaError, SchemaHttpState, SchemaIdentityPort, SchemaService, SchemaTarget,
};
use axum::http::HeaderMap;
use terrabase_tbep::member_host::MemberHost;

struct EngineSchemaStore(Arc<MemberHost>);

fn storage_error() -> DurablePortError {
    DurablePortError { detail: "durable schema metadata operation failed".into() }
}

impl SchemaDurablePort for EngineSchemaStore {
    fn load(&self, target: &SchemaTarget) -> Result<Option<DurableSchemaRecord>, DurablePortError> {
        self.0
            .engine()
            .schema_metadata_load(&target.database, &target.branch, &target.collection)
            .map_err(|_| storage_error())?
            .map(|bytes| serde_json::from_slice(&bytes).map_err(|_| storage_error()))
            .transpose()
    }

    fn commit(
        &self,
        target: &SchemaTarget,
        expected: Option<&DurableSchemaRecord>,
        record: DurableSchemaRecord,
    ) -> Result<DurableCommit, DurablePortError> {
        let expected = expected.map(serde_json::to_vec).transpose().map_err(|_| storage_error())?;
        let record = serde_json::to_vec(&record).map_err(|_| storage_error())?;
        let engine = self.0.engine();
        let scope = document_scope(engine).map_err(|()| storage_error())?;
        let snapshot = engine.read_snapshot(&scope).map_err(|_| storage_error())?;
        let operation = OperationId::try_new(&format!(
            "schema-{}", terrabase::identity::process_generator().generate().to_text(),
        )).map_err(|_| storage_error())?;
        let mut transaction = engine.begin(&scope, snapshot, operation.clone()).map_err(|_| storage_error())?;
        let decoded: DurableSchemaRecord = serde_json::from_slice(&record).map_err(|_| storage_error())?;
        if decoded.document.migration.as_ref().is_none_or(|migration| migration.state == crate::schema_api::MigrationState::Complete) {
            let canonical = crate::schema_api::canonical_schema_document(&crate::schema_api::SchemaJson {
                collections: vec![decoded.document.schema.clone()], version: None,
            }).map_err(|_| storage_error())?;
            let version = terrabase::families::SchemaVersion::try_new(decoded.document.schema_version).map_err(|_| storage_error())?;
            let validation = canonical.collections().first().ok_or_else(storage_error)?
                .validation_schema(version).map_err(|_| storage_error())?;
            let update = terrabase::families::ValidationWrite::try_new(
                scope, CollectionName::try_new(&target.collection).map_err(|_| storage_error())?,
                Some(validation), operation,
                terrabase::types::Deadline::after(std::time::Duration::from_secs(30)).map_err(|_| storage_error())?,
                terrabase::types::Cancellation::new(),
            ).map_err(|_| storage_error())?;
            engine.families().stage(&mut transaction,
                terrabase::families::FamilyWriteRequest::schema_validation_update(update))
                .map_err(|_| storage_error())?;
        }
        transaction.stage_schema_metadata(&target.database, &target.branch, &target.collection, expected.as_deref(), &record)
            .map_err(|_| storage_error())?;
        match transaction.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => Ok(DurableCommit::Committed),
            CommitOutcome::Rejected(terrabase::CommitError::Conflict) => Ok(DurableCommit::GenerationConflict),
            CommitOutcome::Rejected(_) | CommitOutcome::Unknown(_) => Err(storage_error()),
        }
    }
}

struct BoundIdentity {
    database: String,
    identity: DatabaseIdentity,
}

impl SchemaIdentityPort for BoundIdentity {
    fn resolve(&self, database: &str, branch: &str) -> Result<DatabaseIdentity, SchemaError> {
        if database != self.database || branch != "main" {
            return Err(SchemaError {
                status: 400,
                code: "invalid_schema_target".into(),
                message: "schema targets the named database's main branch".into(),
                fix: "use the cloud branch's database name and omit X-TerraScale-Branch".into(),
            });
        }
        Ok(self.identity.clone())
    }
}

fn schema_state(state: &ApiState, database: &str) -> Result<Arc<SchemaHttpState>, ApiError> {
    let host = document_host(state, database)?;
    let record = state
        .control
        .as_ref()
        .ok_or_else(ApiError::control_unavailable)?
        .get(database)
        .map_err(ApiError::control)?
        .ok_or_else(|| ApiError::database_not_found(database))?;
    let identity = BoundIdentity {
        database: database.to_owned(),
        identity: DatabaseIdentity {
            database_id: host.engine().database_owner().database().to_text(),
            placement: record.placement.iter().map(|region| region.as_str().to_owned()).collect(),
        },
    };
    Ok(Arc::new(SchemaHttpState::production(
        Arc::new(SchemaService::new(Some(Arc::new(EngineSchemaStore(host))))),
        Arc::new(identity),
    )))
}

#[utoipa::path(get, path = "/api/v1/databases/{name}/collections/{collection}/schema",
    responses((status = 200, description = "Durable collection schema", body = crate::schema_api::SchemaDocument)))]
pub(super) async fn get_schema(
    State(state): State<Arc<ApiState>>,
    Path((name, collection)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    let service = match schema_state(&state, &name) {
        Ok(service) => service,
        Err(error) => return error.into_response(),
    };
    crate::schema_api::get_schema(State(service), Path((name, collection)), headers).await
}

#[utoipa::path(put, path = "/api/v1/databases/{name}/collections/{collection}/schema",
    request_body = crate::schema_api::CollectionSchema,
    params(("Idempotency-Key" = String, Header, description = "Required unique apply key"),
        ("If-Match" = Option<String>, Header, description = "Strong ETag from GET schema")),
    responses((status = 200, description = "Applied durable collection schema", body = crate::schema_api::SchemaDocument),
        (status = 412, description = "Schema generation precondition failed"),
        (status = 413, description = "Schema exceeds 512 KiB")))]
pub(super) async fn put_schema(
    State(state): State<Arc<ApiState>>,
    Path((name, collection)): Path<(String, String)>,
    headers: HeaderMap,
    body: String,
) -> Response {
    let service = match schema_state(&state, &name) {
        Ok(service) => service,
        Err(error) => return error.into_response(),
    };
    crate::schema_api::put_schema(State(service), Path((name, collection)), headers, body).await
}

#[cfg(test)]
mod tests {
    use super::*;
    use axum::body::Body;
    use tower::ServiceExt;

    const KEY: &str = "Bearer tskey_test_abcdefghijklmnop";
    const TARGET: &str = "/api/v1/databases/demo/collections/orders/schema";

    #[tokio::test]
    async fn schema_http_persists_replays_and_checks_authorization_and_etags() {
        let state = super::super::tests::state_with_keys_and_control();
        state
            .control
            .as_ref()
            .expect("control")
            .create("demo", &[RegionCode::GRU], None)
            .expect("database");
        let app = router(Arc::clone(&state));
        let body = r#"{"name":"orders","fields":[{"name":"title","type":"string"}]}"#;
        let response = app
            .clone()
            .oneshot(Request::put(TARGET).body(Body::from(body)).expect("request"))
            .await
            .expect("response");
        assert_eq!(response.status(), StatusCode::UNAUTHORIZED);
        for _ in 0..2 {
            let response = app
                .clone()
                .oneshot(
                    Request::put(TARGET)
                        .header("authorization", KEY)
                        .header("idempotency-key", "apply-1")
                        .body(Body::from(body))
                        .expect("request"),
                )
                .await
                .expect("response");
            assert_eq!(response.status(), StatusCode::OK);
            assert_eq!(response.headers()["etag"], "\"1\"");
        }
        state.catalog.as_ref().expect("catalog").close_database(
            &state.scope.0,
            &state.scope.1,
            "demo",
        );
        let response = app
            .clone()
            .oneshot(
                Request::get(TARGET)
                    .header("authorization", KEY)
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("response");
        assert_eq!(response.status(), StatusCode::OK);
        assert_eq!(response.headers()["etag"], "\"1\"");
        let value: serde_json::Value = serde_json::from_slice(
            &axum::body::to_bytes(response.into_body(), 64 * 1024).await.expect("body"),
        )
        .expect("JSON");
        assert_eq!(value["schema"]["name"], "orders");
        let response = app
            .clone()
            .oneshot(
                Request::put(TARGET)
                    .header("authorization", KEY)
                    .header("idempotency-key", "apply-2")
                    .header("if-match", "\"0\"")
                    .body(Body::from(body))
                    .expect("request"),
            )
            .await
            .expect("response");
        assert_eq!(response.status(), StatusCode::PRECONDITION_FAILED);
        let response = app
            .oneshot(
                Request::get(TARGET)
                    .header("authorization", KEY)
                    .header("x-terrascale-branch", "other")
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("response");
        assert_eq!(response.status(), StatusCode::BAD_REQUEST);

        let mut restricted = super::super::tests::state_with_keys_and_control();
        Arc::get_mut(&mut restricted).expect("sole state").keys =
            Some(Arc::new(crate::api_keys::KeyStore::with_keys(&[(
                "tskey_test_abcdefghijklmnop",
                "read-orders",
                crate::api_keys::Scope::database_collection("demo", "orders", Verbs::READ),
            )])));
        let app = router(restricted);
        for (method, target) in [
            (Method::PUT, TARGET),
            (Method::GET, "/api/v1/databases/demo/collections/private/schema"),
            (Method::GET, "/api/v1/databases/other/collections/orders/schema"),
        ] {
            let response = app
                .clone()
                .oneshot(
                    Request::builder()
                        .method(method)
                        .uri(target)
                        .header("authorization", KEY)
                        .body(Body::from(body))
                        .expect("request"),
                )
                .await
                .expect("response");
            assert_eq!(response.status(), StatusCode::FORBIDDEN);
        }
    }
}
