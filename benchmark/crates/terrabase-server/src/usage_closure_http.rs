//! Authenticated native regional usage closure for the billing authority.

use crate::api::ApiState;
use axum::{
    Json, Router,
    extract::{DefaultBodyLimit, Path, State},
    http::{HeaderMap, StatusCode},
    response::{IntoResponse, Response},
    routing::post,
};
use serde::Deserialize;
use std::{sync::Arc, time::Duration};

struct Service {
    api: Arc<ApiState>,
    key: Vec<u8>,
    inflight: tokio::sync::Semaphore,
}

/// Constructs the private native receipt endpoint with a dedicated service key.
///
/// # Errors
/// Rejects weak keys or missing admitted native storage/replication authorities.
pub fn router(api: Arc<ApiState>, key: Vec<u8>) -> Result<Router, &'static str> {
    if key.len() < 32
        || key.len() > 4096
        || key.iter().any(|b| !b.is_ascii_graphic())
        || api.admitted_databases.is_none()
        || api.replication_progress.is_none()
    {
        return Err("billing closure authority unavailable");
    }
    Ok(Router::new()
        .route("/api/internal/databases/{name}/usage/close", post(close))
        .layer(DefaultBodyLimit::max(8192))
        .with_state(Arc::new(Service { api, key, inflight: tokio::sync::Semaphore::new(8) })))
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Request {
    organization_id: String,
    cycle_id: String,
    cycle_checksum: String,
    inventory_checksum: String,
    region: String,
    partition_id: String,
    start_millis: u64,
    end_millis: u64,
}

fn response(status: StatusCode, value: serde_json::Value) -> Response {
    let mut response = (status, Json(value)).into_response();
    response.headers_mut().insert(
        axum::http::header::CACHE_CONTROL,
        axum::http::HeaderValue::from_static("private, no-store"),
    );
    response
}
fn unavailable() -> Response {
    response(
        StatusCode::SERVICE_UNAVAILABLE,
        serde_json::json!({"code":"usage_closure_unavailable","retryable":true}),
    )
}
fn digest(value: &str) -> bool {
    value.len() == 64
        && value.bytes().all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

async fn close(
    State(service): State<Arc<Service>>,
    Path(name): Path<String>,
    headers: HeaderMap,
    body: Result<Json<Request>, axum::extract::rejection::JsonRejection>,
) -> Response {
    let supplied =
        headers.get("x-terrascale-billing-key").map_or(&[][..], |value| value.as_bytes());
    if aws_lc_rs::constant_time::verify_slices_are_equal(
        blake3::hash(supplied).as_bytes(),
        blake3::hash(&service.key).as_bytes(),
    )
    .is_err()
    {
        return response(StatusCode::UNAUTHORIZED, serde_json::json!({"code":"unauthorized"}));
    }
    let Ok(_permit) = service.inflight.try_acquire() else {
        return response(
            StatusCode::TOO_MANY_REQUESTS,
            serde_json::json!({"code":"closure_busy","retryable":true}),
        );
    };
    let Ok(Json(request)) = body else {
        return response(StatusCode::BAD_REQUEST, serde_json::json!({"code":"invalid_request"}));
    };
    if !digest(&request.cycle_id)
        || !digest(&request.cycle_checksum)
        || !digest(&request.inventory_checksum)
        || request.start_millis >= request.end_millis
    {
        return response(StatusCode::BAD_REQUEST, serde_json::json!({"code":"invalid_request"}));
    }
    let Some(registry) = &service.api.admitted_databases else {
        return unavailable();
    };
    let admitted = match registry.resolve(&name) {
        Ok(Some(value))
            if value.record.tenant_id == request.organization_id
                && value.record.region == request.region =>
        {
            value
        }
        Ok(_) => return response(StatusCode::NOT_FOUND, serde_json::json!({"code":"not_found"})),
        Err(_) => return unavailable(),
    };
    let engine = admitted.host.engine();
    let Ok(tenant) = terrabase::types::TenantId::try_new(&request.organization_id) else {
        return unavailable();
    };
    let scope = engine.scope_for(&tenant);
    let Ok(Some(placement)) = engine.usage_placement(&scope) else {
        return unavailable();
    };
    if placement.scope.partition_id().as_str() != request.partition_id
        || placement.scope.region_id().as_str() != request.region
        || placement.scope.database_id().as_str() != name
        || placement.scope.project_id().as_str() != admitted.record.project_id
    {
        return response(StatusCode::CONFLICT, serde_json::json!({"code":"usage_scope_mismatch"}));
    }
    let owner = engine.database_owner();
    let route = terrabase_cluster::production::DatabaseRouteScope::new(
        owner.tenant(),
        owner.project(),
        owner.database(),
    );
    let Some(progress) = &service.api.replication_progress else {
        return unavailable();
    };
    if progress.wait_regional(route, 0, Duration::from_secs(5)).await.is_err() {
        return unavailable();
    }
    let Ok(window) =
        terrabase::usage::MeasurementWindow::interval(request.start_millis, request.end_millis)
    else {
        return unavailable();
    };
    let close_host = Arc::clone(&admitted);
    let Ok(Ok(receipt)) = tokio::task::spawn_blocking(move || {
        let engine = close_host.host.engine();
        let scope = engine.scope_for(&tenant);
        engine.close_storage_usage(&scope, window)
    })
    .await
    else {
        return unavailable();
    };
    if progress.wait_regional(route, receipt.sequence(), Duration::from_secs(5)).await.is_err() {
        return unavailable();
    }
    response(
        StatusCode::OK,
        serde_json::json!({
            "schema_version":1,"organization_id":request.organization_id,"cycle_id":request.cycle_id,
            "cycle_checksum":request.cycle_checksum,"inventory_checksum":request.inventory_checksum,
            "scope":{"tenant_id":receipt.scope().tenant_id().as_str(),"project_id":receipt.scope().project_id().as_str(),"database_id":receipt.scope().database_id().as_str(),"partition_id":receipt.scope().partition_id().as_str(),"region_id":receipt.scope().region_id().as_str()},
            "start_millis":receipt.window().start_millis(),"end_millis":receipt.window().end_millis(),
            "sequence":receipt.sequence().to_string(),"selected_regions":receipt.selected_regions(),
            "storage_byte_seconds":receipt.byte_seconds().to_string(),"write_units":receipt.write_units().to_string(),
            "fact_count":receipt.fact_count().to_string(),"checksum":blake3::Hash::from(*receipt.checksum()).to_hex().to_string(),"durability":"regional"
        }),
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use axum::{body::Body, http::Request as HttpRequest};
    use tower::ServiceExt;

    fn state() -> Arc<ApiState> {
        let backups = Arc::new(crate::backup::BackupStreams::open_member());
        let backup_root = std::env::temp_dir().join("usage-closure-private-api");
        Arc::new(ApiState {
            budget: None,
            byo_jwt: None,
            native_backups: None,
            native_recovery: None,
            native_restore_target: None,
            development: Arc::default(),
            provisioning: None,
            admitted_databases: None,
            replication_progress: None,
            standalone_durability: false,
            region: terrabase::regions::RegionCode::GRU,
            member_id: "test-member".into(),
            metrics: Arc::new(terrabase::observability::MetricsRegistry::new()),
            delegated_identity: None,
            keys: None,
            control: None,
            catalog: None,
            scope: ("tenant".into(), "project".into()),
            timetravel: Some(Arc::new(crate::backup::TimeTravelCache::new(
                Arc::clone(&backups),
                &backup_root,
            ))),
            backups: Some(backups),
            backup_root,
            guards: Arc::new(crate::guards::AbuseGuards::default()),
            origin_proof_keys: Arc::new(crate::reactive_api::OriginProofKeyRing::default()),
            webhook_keys: None,
            exports: None,
            capability_keys: Arc::new(crate::reactive_api::CapabilityKeyRing::conformance()),
        })
    }

    #[tokio::test]
    async fn unauthenticated_requests_cannot_probe_authority_or_body_validation() {
        let app = Router::new()
            .route("/api/internal/databases/{name}/usage/close", post(close))
            .with_state(Arc::new(Service {
                api: state(),
                key: vec![b'x'; 32],
                inflight: tokio::sync::Semaphore::new(8),
            }));
        let response = app
            .oneshot(
                HttpRequest::builder()
                    .method("POST")
                    .uri("/api/internal/databases/secret/usage/close")
                    .header("content-type", "application/json")
                    .body(Body::from("{}"))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::UNAUTHORIZED);
        assert_eq!(response.headers()[axum::http::header::CACHE_CONTROL], "private, no-store");
        assert!(router(state(), vec![b'x'; 32]).is_err());
    }

    #[test]
    fn rejects_caller_totals_and_noncanonical_checksums() {
        assert!(
            serde_json::from_value::<Request>(serde_json::json!({
                "organization_id":"org","cycle_id":"a".repeat(64),"cycle_checksum":"b".repeat(64),
                "inventory_checksum":"c".repeat(64),"region":"GRU","partition_id":"p",
                "start_millis":1,"end_millis":2,"write_units":100
            }))
            .is_err()
        );
        assert!(!digest(&"A".repeat(64)));
    }
}
