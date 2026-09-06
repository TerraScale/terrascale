//! Public usage and index administration backed by the serving database Engine.

use super::*;
use crate::api_keys::Principal;
use crate::index_api::{EngineIndexAdapter, IndexScope, IndexService, IndexServiceError};
use crate::usage_api::UsageApiError;

/// Reports only authoritative billable meter totals and free read attribution.
#[utoipa::path(get, path = "/api/v1/databases/{name}/usage", responses((status = 200, description = "Authoritative database usage", body = crate::usage_api::UsageSummary)))]
pub(super) async fn database_usage(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
) -> Response {
    let host = match document_host(&state, &name) {
        Ok(host) => host,
        Err(error) => return error.into_response(),
    };
    if let Err(error) = check_read_guard(&state, &name, terrabase::observability::Operation::Read) {
        return error.into_response();
    }
    match crate::usage_api::engine_summary(host.engine()) {
        Ok(summary) => accounted_json(&state, &name, &summary),
        Err(error) => usage_error(error).into_response(),
    }
}

fn usage_error(error: UsageApiError) -> ApiError {
    let status = match error {
        UsageApiError::SourceUnavailable { .. } => StatusCode::SERVICE_UNAVAILABLE,
        UsageApiError::CorruptSource { .. } => StatusCode::INTERNAL_SERVER_ERROR,
        _ => StatusCode::BAD_REQUEST,
    };
    ApiError {
        status,
        code: error.code(),
        message: error.to_string(),
        fix: error.fix(),
        placement: None,
    }
}

#[derive(serde::Deserialize)]
#[serde(deny_unknown_fields)]
pub(super) struct RecommendationQuery {
    #[serde(default = "recommendation_limit")]
    limit: usize,
}

const fn recommendation_limit() -> usize {
    crate::index_api::MAX_RECOMMENDATIONS
}

/// Returns observed, collection-scoped candidates from the durable coordinator.
#[utoipa::path(get, path = "/api/v1/databases/{name}/collections/{collection}/indexes/recommendations", params(("limit" = Option<u16>, Query, minimum = 1, maximum = 64, description = "Maximum recommendations; defaults to 64")), responses((status = 200, description = "Index recommendations", body = crate::index_api::RecommendationList)))]
pub(super) async fn index_recommendations(
    State(state): State<Arc<ApiState>>,
    Path((name, collection)): Path<(String, String)>,
    axum::Extension(principal): axum::Extension<Principal>,
    query: Result<extract::Query<RecommendationQuery>, extract::rejection::QueryRejection>,
) -> Response {
    let query = match query {
        Ok(extract::Query(query)) => query,
        Err(_) => {
            return ApiError {
                status: StatusCode::BAD_REQUEST,
                code: "invalid_query",
                message: "the recommendation query is invalid".into(),
                fix: "send limit between 1 and 64".into(),
                placement: None,
            }
            .into_response();
        }
    };
    let host = match document_host(&state, &name) {
        Ok(host) => host,
        Err(error) => return error.into_response(),
    };
    if let Err(error) = check_read_guard(&state, &name, terrabase::observability::Operation::Read) {
        return error.into_response();
    }
    let scope = match IndexScope::try_new(&state.scope.0, &name, Some(collection)) {
        Ok(scope) => scope,
        Err(error) => return index_error(error).into_response(),
    };
    let service = IndexService::new(EngineIndexAdapter::new(host.engine()));
    match service.recommend(&scope, &principal, query.limit) {
        Ok(recommendations) => accounted_json(&state, &name, &recommendations),
        Err(error) => index_error(error).into_response(),
    }
}

fn index_error(error: IndexServiceError) -> ApiError {
    let status = match error {
        IndexServiceError::Forbidden { .. } | IndexServiceError::UnauthorizedTenant => {
            StatusCode::FORBIDDEN
        }
        IndexServiceError::DatabaseNotFound => StatusCode::NOT_FOUND,
        IndexServiceError::NotReady | IndexServiceError::LegacyCoordinator => {
            StatusCode::SERVICE_UNAVAILABLE
        }
        IndexServiceError::StorageFailure => StatusCode::INTERNAL_SERVER_ERROR,
        _ => StatusCode::BAD_REQUEST,
    };
    ApiError {
        status,
        code: error.code(),
        message: error.to_string(),
        fix: error.fix().into(),
        placement: None,
    }
}

/// Retires an extracted-field descriptor and releases its unused physical columns.
#[utoipa::path(delete, path = "/api/v1/databases/{name}/collections/{collection}/indexes/{field}", responses((status = 204, description = "Index retired")))]
pub(super) async fn delete_index(
    State(state): State<Arc<ApiState>>,
    Path((name, collection, field)): Path<(String, String, String)>,
) -> Response {
    let host = match document_host(&state, &name) {
        Ok(host) => host,
        Err(error) => return error.into_response(),
    };
    let collection = match CollectionName::try_new(&collection) {
        Ok(collection) => collection,
        Err(_) => return invalid_identifier("collection name", &collection).into_response(),
    };
    match host.engine().remove_index_field(&collection, &field) {
        Ok(_) => StatusCode::NO_CONTENT.into_response(),
        Err(terrabase::storage::IndexDescriptorRemovalError::InvalidField) => {
            invalid_identifier("index field", &field).into_response()
        }
        Err(terrabase::storage::IndexDescriptorRemovalError::InUse) => ApiError {
            status: StatusCode::CONFLICT,
            code: "index_in_use",
            message: "the index field is required by an active index build".into(),
            fix: "finish or retire the dependent build before deleting this field".into(),
            placement: None,
        }
        .into_response(),
        Err(terrabase::storage::IndexDescriptorRemovalError::Storage) => {
            ApiError::storage_failure().into_response()
        }
    }
}

fn accounted_json(state: &ApiState, name: &str, value: &impl serde::Serialize) -> Response {
    let body = match serde_json::to_string(value) {
        Ok(body) => body,
        Err(_) => return ApiError::storage_failure().into_response(),
    };
    if let Err(error) = account_direct_read(state, name, response_bytes(body.len())) {
        return error.into_response();
    }
    json_response(StatusCode::OK, body)
}

#[cfg(test)]
mod tests {
    use super::*;
    use axum::body::Body;
    use axum::http::Request;
    use tower::util::ServiceExt;

    const KEY: &str = "Bearer tskey_test_abcdefghijklmnop";

    async fn request(app: &Router, method: &str, path: &str, body: &str) -> Response {
        app.clone()
            .oneshot(
                Request::builder()
                    .method(method)
                    .uri(path)
                    .header("authorization", KEY)
                    .body(Body::from(body.to_owned()))
                    .unwrap(),
            )
            .await
            .unwrap()
    }

    async fn body(response: Response) -> serde_json::Value {
        serde_json::from_slice(&axum::body::to_bytes(response.into_body(), 65_536).await.unwrap())
            .unwrap()
    }

    async fn setup() -> (Arc<ApiState>, Router) {
        let state = super::super::tests::state_with_keys_and_control();
        let app = router(Arc::clone(&state));
        assert_eq!(
            request(
                &app,
                "POST",
                "/api/v1/databases",
                r#"{"name":"prod","placement":["GRU"],"residency":null}"#
            )
            .await
            .status(),
            StatusCode::CREATED
        );
        (state, app)
    }

    #[tokio::test]
    #[allow(
        clippy::too_many_lines,
        reason = "one full HTTP usage aggregation and authorization scenario"
    )]
    async fn usage_summary_reads_authoritative_facts_across_partitions_and_refuses_unknown_database()
     {
        use terrabase::usage::{UsageFactId, UsageRecordMeter, UsageRecordRequest};
        let (state, app) = setup().await;
        let host = document_host(&state, "prod").unwrap();
        let engine = host.engine();
        let scope = document_scope(engine).unwrap();
        let owner = engine.database_owner();
        for (partition, delta) in [("one", 7), ("two", 11)] {
            engine
                .record_usage(
                    &scope,
                    UsageRecordRequest::try_new(
                        UsageRecordMeter::DocumentWriteUnits,
                        &owner.project().to_text(),
                        &owner.database().to_text(),
                        partition,
                        owner.region(),
                        UsageFactId::try_new(partition).unwrap(),
                        10,
                        1,
                        delta,
                    )
                    .unwrap(),
                )
                .unwrap();
        }
        // Cross the Engine's 256-fact page boundary; later pages must contribute
        // to the same summary without changing its snapshot.
        for index in 0..255 {
            engine
                .record_usage(
                    &scope,
                    UsageRecordRequest::try_new(
                        UsageRecordMeter::DocumentWriteUnits,
                        &owner.project().to_text(),
                        &owner.database().to_text(),
                        "one",
                        owner.region(),
                        UsageFactId::try_new(&format!("page-{index}")).unwrap(),
                        10,
                        1,
                        1,
                    )
                    .unwrap(),
                )
                .unwrap();
        }
        assert_eq!(
            request(&app, "GET", "/api/v1/databases/prod/collections/orders/documents/missing", "")
                .await
                .status(),
            StatusCode::NOT_FOUND
        );
        let observed_reads = engine.metering_read_count(&scope);
        assert!(observed_reads > 0, "the served document lookup contributes aggregate evidence");
        let response = request(&app, "GET", "/api/v1/databases/prod/usage", "").await;
        assert_eq!(response.status(), StatusCode::OK);
        assert!(response.headers().contains_key(PLACEMENT_HEADER));
        let summary = body(response).await;
        assert_eq!(summary["database_id"], "prod");
        assert_eq!(summary["reads"], serde_json::json!({"total_reads": observed_reads}));
        assert_eq!(summary["meters"][0]["meter"], "document_write_units");
        assert_eq!(summary["meters"][0]["units"], "273");
        assert_eq!(summary["meters"][1]["meter"], "storage_byte_seconds");
        assert_eq!(summary["meters"][1]["units"], "0");
        assert_eq!(
            request(&app, "GET", "/api/v1/databases/missing/usage", "").await.status(),
            StatusCode::NOT_FOUND
        );
        let unauthenticated = app
            .clone()
            .oneshot(Request::get("/api/v1/databases/prod/usage").body(Body::empty()).unwrap())
            .await
            .unwrap();
        assert_eq!(unauthenticated.status(), StatusCode::UNAUTHORIZED);
    }

    #[tokio::test]
    async fn usage_unavailable_never_returns_zero_totals() {
        let mut state = super::super::tests::state_with_keys_and_control();
        Arc::get_mut(&mut state).unwrap().control = None;
        let app = router(state);
        let response = request(&app, "GET", "/api/v1/databases/prod/usage", "").await;
        assert_eq!(response.status(), StatusCode::SERVICE_UNAVAILABLE);
        let error = body(response).await;
        assert!(error.get("meters").is_none());
        assert!(error["code"].is_string());
    }

    #[tokio::test]
    #[allow(
        clippy::too_many_lines,
        reason = "one HTTP descriptor lifecycle and recommendation scenario"
    )]
    async fn index_deletion_removes_descriptor_is_idempotent_and_recommendations_validate_bounds() {
        let (state, app) = setup().await;
        let indexes = "/api/v1/databases/prod/collections/orders/indexes";
        assert_eq!(
            request(&app, "POST", indexes, r#"{"field":"status"}"#).await.status(),
            StatusCode::CREATED
        );
        assert_eq!(
            body(request(&app, "GET", indexes, "").await).await["indexes"]
                .as_array()
                .unwrap()
                .len(),
            1
        );
        for _ in 0..2 {
            assert_eq!(
                request(&app, "DELETE", &format!("{indexes}/status"), "").await.status(),
                StatusCode::NO_CONTENT
            );
        }
        assert!(
            body(request(&app, "GET", indexes, "").await).await["indexes"]
                .as_array()
                .unwrap()
                .is_empty()
        );
        let recommendations = format!("{indexes}/recommendations");
        let response = request(&app, "GET", &recommendations, "").await;
        assert_eq!(response.status(), StatusCode::OK);
        assert_eq!(body(response).await["recommendations"], serde_json::json!([]));
        assert_eq!(
            request(
                &app,
                "PUT",
                "/api/v1/databases/prod/collections/orders/documents/one",
                r#"{"status":"open"}"#
            )
            .await
            .status(),
            StatusCode::OK
        );
        let response = request(
            &app,
            "POST",
            "/api/v1/databases/prod/sql",
            r#"{"sql":"SELECT status FROM orders WHERE status = 'open'"}"#,
        )
        .await;
        assert_eq!(response.status(), StatusCode::OK);
        let response = request(&app, "GET", &recommendations, "").await;
        assert_eq!(response.status(), StatusCode::OK);
        let candidates = body(response).await;
        assert_eq!(candidates["recommendations"].as_array().unwrap().len(), 1);
        assert_eq!(candidates["recommendations"][0]["collection"], "orders");
        assert_eq!(candidates["recommendations"][0]["fields"], serde_json::json!(["status"]));
        let other = body(
            request(
                &app,
                "GET",
                "/api/v1/databases/prod/collections/other/indexes/recommendations",
                "",
            )
            .await,
        )
        .await;
        assert_eq!(other["recommendations"], serde_json::json!([]));
        for query in ["?limit=0", "?limit=65", "?limit=oops", "?cursor=unsupported"] {
            assert_eq!(
                request(&app, "GET", &format!("{recommendations}{query}"), "").await.status(),
                StatusCode::BAD_REQUEST
            );
        }
        state.keys.as_ref().unwrap().register(
            "tskey_test_reader_abcdefghijkl",
            "reader",
            crate::api_keys::Scope::database("prod", Verbs::READ),
        );
        let response = app
            .clone()
            .oneshot(
                Request::delete(format!("{indexes}/status"))
                    .header("authorization", "Bearer tskey_test_reader_abcdefghijkl")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::FORBIDDEN);
        let response = app
            .clone()
            .oneshot(
                Request::get("/api/v1/databases/other/usage")
                    .header("authorization", "Bearer tskey_test_reader_abcdefghijkl")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::FORBIDDEN);
    }
}
