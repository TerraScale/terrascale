//! Authenticated native regional usage closure for the billing authority.
//!
//! `storage_intervals` contains `1..=65_536` contiguous half-open levels covering
//! exactly the frozen receipt window, including zero levels. `logical_bytes`
//! is a canonical unsigned decimal u128 string, measured in this Region once;
//! timestamps are safe JSON integers. The receipt checksum binds these levels
//! to the frozen native ledger seal. Consumers pool each admitted inventory
//! scope once without multiplying storage by `selected_regions` again.
//!
//! Authentication and regional durability prove this scoped historical receipt.
//! Echoed cycle/inventory checksums do not attest organization-wide completeness,
//! current allowance coverage, or pricing policy; the billing authority owns
//! those checks against its authoritative inventory and current policy.

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

// Timestamps are JSON numbers consumed by JavaScript billing authorities.
const MAX_WIRE_MILLIS: u64 = 9_007_199_254_740_991;
const MAX_STORAGE_INTERVALS: usize = 65_536;

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

/// A successful receipt contains the entire frozen native window. Never emit
/// a partial timeline that a consumer could mistake for zero storage coverage.
fn storage_intervals_value(
    start: u64,
    end: u64,
    intervals: impl IntoIterator<Item = (u64, u64, u128)>,
) -> Result<Vec<serde_json::Value>, ()> {
    if start >= end || end > MAX_WIRE_MILLIS {
        return Err(());
    }
    let mut cursor = start;
    let mut output = Vec::new();
    for (from, to, bytes) in intervals {
        if output.len() >= MAX_STORAGE_INTERVALS || from != cursor || from >= to || to > end {
            return Err(());
        }
        output.push(serde_json::json!({
            "start_millis":from,"end_millis":to,"logical_bytes":bytes.to_string()
        }));
        cursor = to;
    }
    if output.is_empty() || cursor != end {
        return Err(());
    }
    Ok(output)
}

async fn close(
    State(service): State<Arc<Service>>,
    Path(name): Path<String>,
    headers: HeaderMap,
    body: Result<Json<Request>, axum::extract::rejection::JsonRejection>,
) -> Response {
    let mut supplied_headers = headers.get_all("x-terrascale-billing-key").iter();
    let Some(supplied_header) = supplied_headers.next() else {
        return response(StatusCode::UNAUTHORIZED, serde_json::json!({"code":"unauthorized"}));
    };
    // A duplicated credential is ambiguous at the HTTP boundary. Do not let
    // HeaderMap's first-value behavior turn it into an authentication bypass.
    if supplied_headers.next().is_some() {
        return response(StatusCode::UNAUTHORIZED, serde_json::json!({"code":"unauthorized"}));
    }
    let supplied = supplied_header.as_bytes();
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
        || request.end_millis > MAX_WIRE_MILLIS
    {
        return response(StatusCode::BAD_REQUEST, serde_json::json!({"code":"invalid_request"}));
    }
    // A receipt is a regional attestation. The admitted record alone is not
    // sufficient: a stale or misbound registry must never let one member
    // attest another region's usage.
    if service.api.region.as_str() != request.region {
        return response(StatusCode::NOT_FOUND, serde_json::json!({"code":"not_found"}));
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
    let expected_scope = placement.scope.clone();
    let Ok(expected_regions) = u32::try_from(placement.selected_regions.len()) else {
        return unavailable();
    };
    let Ok(Ok(receipt)) = tokio::task::spawn_blocking(move || {
        let engine = close_host.host.engine();
        let scope = engine.scope_for(&tenant);
        engine.close_storage_usage(&scope, window)
    })
    .await
    else {
        return unavailable();
    };
    // The closure is an attestation for the frozen placement. Refuse to emit
    // a receipt if a storage replay or stale placement returns a different
    // scope/window/Region count, since the consumer would otherwise pool the
    // wrong local stream or multiply it under another placement.
    if receipt.scope() != &expected_scope
        || receipt.window() != window
        || receipt.selected_regions() != expected_regions
    {
        return unavailable();
    }
    if progress.wait_regional(route, receipt.sequence(), Duration::from_secs(5)).await.is_err() {
        return unavailable();
    }
    let Ok(storage_intervals) = storage_intervals_value(
        receipt.window().start_millis(),
        receipt.window().end_millis(),
        receipt.storage_intervals().iter().map(|interval| {
            (interval.start_millis(), interval.end_millis(), interval.logical_bytes())
        }),
    ) else {
        return unavailable();
    };
    response(
        StatusCode::OK,
        serde_json::json!({
            "schema_version":1,"organization_id":request.organization_id,"cycle_id":request.cycle_id,
            "cycle_checksum":request.cycle_checksum,"inventory_checksum":request.inventory_checksum,
            "scope":{"tenant_id":receipt.scope().tenant_id().as_str(),"project_id":receipt.scope().project_id().as_str(),"database_id":receipt.scope().database_id().as_str(),"partition_id":receipt.scope().partition_id().as_str(),"region_id":receipt.scope().region_id().as_str()},
            "start_millis":receipt.window().start_millis(),"end_millis":receipt.window().end_millis(),
            "sequence":receipt.sequence().to_string(),"selected_regions":receipt.selected_regions(),
            "storage_byte_seconds":receipt.byte_seconds().to_string(),"storage_intervals":storage_intervals,"write_units":receipt.write_units().to_string(),
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
            imports: None,
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

    #[tokio::test]
    async fn duplicate_billing_keys_and_foreign_regions_are_rejected() {
        let mut api = state();
        Arc::get_mut(&mut api).unwrap().region = terrabase::regions::RegionCode::SFO;
        let app = Router::new()
            .route("/api/internal/databases/{name}/usage/close", post(close))
            .with_state(Arc::new(Service {
                api,
                key: vec![b'x'; 32],
                inflight: tokio::sync::Semaphore::new(8),
            }));
        let payload = serde_json::json!({
            "organization_id":"tenant",
            "cycle_id":"a".repeat(64),
            "cycle_checksum":"b".repeat(64),
            "inventory_checksum":"c".repeat(64),
            // The member serves SFO; GRU must be rejected before authority
            // lookup as a foreign regional attestation.
            "region":"GRU",
            "partition_id":"partition",
            "start_millis":1,
            "end_millis":2
        });
        let mut duplicate = HttpRequest::builder()
            .method("POST")
            .uri("/api/internal/databases/secret/usage/close")
            .header("content-type", "application/json")
            .header("x-terrascale-billing-key", "x".repeat(32))
            .body(Body::from(payload.to_string()))
            .unwrap();
        duplicate.headers_mut().append(
            "x-terrascale-billing-key",
            axum::http::HeaderValue::from_static("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"),
        );
        assert_eq!(
            app.clone().oneshot(duplicate).await.unwrap().status(),
            StatusCode::UNAUTHORIZED
        );

        let foreign = HttpRequest::builder()
            .method("POST")
            .uri("/api/internal/databases/secret/usage/close")
            .header("content-type", "application/json")
            .header("x-terrascale-billing-key", "x".repeat(32))
            .body(Body::from(payload.to_string()))
            .unwrap();
        assert_eq!(app.oneshot(foreign).await.unwrap().status(), StatusCode::NOT_FOUND);
    }

    #[cfg(target_os = "linux")]
    #[tokio::test]
    async fn authenticated_closure_exposes_exact_frozen_timeline_after_later_writes() {
        use terrabase::types::{
            CollectionName, Document, DocumentId, DocumentValue, LocalDurability, OperationId,
        };
        let (registry, _member, records, root) = crate::admitted_databases::tests::fixture();
        let record = &records[0];
        let admitted = registry.resolve(&record.database_id).unwrap().unwrap();
        let engine = admitted.host.engine();
        let tenant = terrabase::types::TenantId::try_new(&record.tenant_id).unwrap();
        let scope = engine.scope_for(&tenant);
        engine
            .configure_usage_placement(
                terrabase::usage::UsagePlacement::try_new(
                    tenant,
                    &record.project_id,
                    &record.database_id,
                    "partition",
                    engine.database_owner().region(),
                    vec![engine.database_owner().region()],
                    "accepted-placement",
                )
                .unwrap(),
            )
            .unwrap();
        let now = || {
            u64::try_from(
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap()
                    .as_millis(),
            )
            .unwrap()
        };
        let start = now();
        while now() <= start {
            tokio::time::sleep(Duration::from_millis(1)).await;
        }
        let write = |id: &str| {
            let mut transaction = engine
                .begin(
                    &scope,
                    engine.read_snapshot(&scope).unwrap(),
                    OperationId::try_new(&format!("closure-{id}")).unwrap(),
                )
                .unwrap();
            transaction
                .put(
                    Document::new(
                        DocumentId::try_new(id).unwrap(),
                        CollectionName::try_new("items").unwrap(),
                        DocumentValue::String("retained logical bytes".into()),
                    ),
                    terrabase::types::ExpectedVersion::New,
                )
                .unwrap();
            assert!(matches!(
                transaction.commit(LocalDurability::Synced),
                terrabase::CommitOutcome::Committed(_)
            ));
        };
        write("before-close");
        let written = now();
        while now() <= written {
            tokio::time::sleep(Duration::from_millis(1)).await;
        }
        let end = now();
        let receipt = engine
            .close_storage_usage(
                &scope,
                terrabase::usage::MeasurementWindow::interval(start, end).unwrap(),
            )
            .unwrap();

        // Establish the frozen sequence on an actual physical follower before
        // allowing the authenticated regional receipt endpoint to report it.
        let follower_path = root.join("closure-follower");
        let (_follower, _) =
            terrabase_cluster::wal_follower::WalFollower::bootstrap_engine(engine, &follower_path)
                .unwrap();
        let follower = terrabase::storage::open_physical_sqlite(
            &follower_path.join("terrabase.sqlite3"),
            rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY,
        )
        .unwrap();
        let copied: i64 = follower
            .query_row("SELECT MAX(seq) FROM storage_usage_closures", [], |row| row.get(0))
            .unwrap();
        assert!(u64::try_from(copied).unwrap() >= receipt.sequence());
        let progress = Arc::new(crate::replication_runtime::ReplicationProgress::default());
        let owner = engine.database_owner();
        let route = terrabase_cluster::production::DatabaseRouteScope::new(
            owner.tenant(),
            owner.project(),
            owner.database(),
        );
        progress.admit(route, [record.region.clone()].into_iter().collect()).unwrap();
        progress.advance_regional(route, receipt.sequence());
        let mut api = state();
        let mutable = Arc::get_mut(&mut api).unwrap();
        mutable.admitted_databases = Some(Arc::new(registry));
        mutable.replication_progress = Some(progress);
        let app = router(api, vec![b'x'; 32]).unwrap();
        let payload = serde_json::json!({
            "organization_id":record.tenant_id,"cycle_id":"a".repeat(64),"cycle_checksum":"b".repeat(64),
            "inventory_checksum":"c".repeat(64),"region":record.region,"partition_id":"partition",
            "start_millis":start,"end_millis":end
        });
        let request = |payload: &serde_json::Value| {
            HttpRequest::builder()
                .method("POST")
                .uri(format!("/api/internal/databases/{}/usage/close", record.database_id))
                .header("content-type", "application/json")
                .header("x-terrascale-billing-key", "x".repeat(32))
                .body(Body::from(payload.to_string()))
                .unwrap()
        };
        let first = app.clone().oneshot(request(&payload)).await.unwrap();
        assert_eq!(first.status(), StatusCode::OK);
        let first: serde_json::Value = serde_json::from_slice(
            &axum::body::to_bytes(first.into_body(), 16 * 1024 * 1024).await.unwrap(),
        )
        .unwrap();
        assert_eq!(first["scope"]["tenant_id"], record.tenant_id);
        assert_eq!(first["scope"]["project_id"], record.project_id);
        assert_eq!(first["scope"]["database_id"], record.database_id);
        assert_eq!(first["scope"]["partition_id"], "partition");
        assert_eq!(first["scope"]["region_id"], record.region);
        assert_eq!(first["start_millis"], start);
        assert_eq!(first["end_millis"], end);
        assert_eq!(first["selected_regions"], 1);
        assert!(first["checksum"].as_str().is_some_and(|value| {
            value.len() == 64
                && value.bytes().all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
        }));
        assert_eq!(
            first["storage_intervals"],
            serde_json::Value::Array(
                storage_intervals_value(
                    start,
                    end,
                    receipt.storage_intervals().iter().map(|v| (
                        v.start_millis(),
                        v.end_millis(),
                        v.logical_bytes()
                    ))
                )
                .unwrap()
            )
        );
        assert!(receipt.storage_intervals().iter().any(|v| v.logical_bytes() == 0));
        assert!(receipt.storage_intervals().iter().any(|v| v.logical_bytes() > 0));
        write("after-close");
        let replay = app.clone().oneshot(request(&payload)).await.unwrap();
        assert_eq!(replay.status(), StatusCode::OK);
        let replay: serde_json::Value = serde_json::from_slice(
            &axum::body::to_bytes(replay.into_body(), 16 * 1024 * 1024).await.unwrap(),
        )
        .unwrap();
        assert_eq!(replay, first, "later writes cannot rewrite the frozen interval or checksum");
        let mut mismatch = payload.clone();
        mismatch["partition_id"] = "other".into();
        assert_eq!(
            app.clone().oneshot(request(&mismatch)).await.unwrap().status(),
            StatusCode::CONFLICT
        );
        let mut overlap = payload;
        overlap["start_millis"] = (start + 1).into();
        assert_eq!(
            app.oneshot(request(&overlap)).await.unwrap().status(),
            StatusCode::SERVICE_UNAVAILABLE
        );
        drop(follower);
        drop(_follower);
        drop(admitted);
        _member.close(Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn timelines_require_complete_nonoverlapping_coverage_and_preserve_large_quantities() {
        for intervals in [
            vec![],
            vec![(11, 20, 1)],
            vec![(10, 19, 1)],
            vec![(10, 14, 1), (15, 20, 2)],
            vec![(10, 16, 1), (15, 20, 2)],
            vec![(10, 10, 0), (10, 20, 1)],
            vec![(10, 21, 1)],
        ] {
            assert!(storage_intervals_value(10, 20, intervals).is_err());
        }
        let valid = storage_intervals_value(10, 20, [(10, 15, 0), (15, 20, u128::MAX)]).unwrap();
        assert_eq!(valid[0]["logical_bytes"], "0");
        assert_eq!(valid[1]["logical_bytes"], u128::MAX.to_string());
        assert!(
            storage_intervals_value(0, MAX_WIRE_MILLIS + 1, [(0, MAX_WIRE_MILLIS + 1, 0)]).is_err()
        );
        assert!(
            storage_intervals_value(
                0,
                MAX_STORAGE_INTERVALS as u64 + 1,
                (0..=MAX_STORAGE_INTERVALS as u64).map(|at| (at, at + 1, 0))
            )
            .is_err()
        );
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
