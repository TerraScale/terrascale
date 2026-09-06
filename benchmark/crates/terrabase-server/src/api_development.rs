//! Branch-only, bounded faults at public request admission and safe diagnostic tails.
use super::*;
use crate::api_keys::Principal;
use std::collections::BTreeMap;
use std::sync::Mutex;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

/// Per-serving-instance development faults; restart clears every active fault.
#[derive(Default)]
pub struct DevelopmentRuntime {
    faults: Mutex<BTreeMap<String, ActiveFault>>,
}
struct ActiveFault {
    fault: FaultKind,
    expires: Instant,
    remaining: u16,
    delay: Duration,
    created_at: i64,
}

#[derive(Clone, Copy, serde::Deserialize, serde::Serialize, utoipa::ToSchema)]
#[serde(rename_all = "snake_case")]
pub(super) enum FaultKind {
    Unavailable,
    Latency,
}

#[derive(serde::Deserialize, utoipa::ToSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(super) struct DevelopmentFaultRequest {
    confirm_branch: String,
    fault: FaultKind,
    duration_ms: u64,
    max_operations: u16,
    delay_ms: u64,
}
#[derive(serde::Serialize, utoipa::ToSchema)]
#[serde(rename_all = "camelCase")]
pub(super) struct DevelopmentFaultResponse {
    active: bool,
    expires_at_millis: u64,
}
#[derive(serde::Deserialize)]
#[serde(deny_unknown_fields)]
pub(super) struct LogQuery {
    #[serde(default = "default_limit")]
    limit: usize,
}
const fn default_limit() -> usize {
    100
}
#[derive(serde::Serialize, utoipa::ToSchema)]
#[serde(rename_all = "camelCase")]
pub(super) struct DevelopmentLogEntry {
    sequence: u64,
    action: String,
    at_millis: u64,
}
#[derive(serde::Serialize, utoipa::ToSchema)]
pub(super) struct DevelopmentLogResponse {
    entries: Vec<DevelopmentLogEntry>,
    truncated: bool,
}

fn refusal(code: &'static str, message: &str, fix: &str) -> ApiError {
    ApiError {
        status: StatusCode::BAD_REQUEST,
        code,
        message: message.into(),
        fix: fix.into(),
        placement: None,
    }
}
fn branch(
    state: &ApiState,
    principal: &Principal,
    name: &str,
    verb: Verbs,
) -> Result<crate::control::DatabaseRecord, ApiError> {
    if !principal.scope().admits_database(verb, name) {
        return Err(ApiError::forbidden("whole database development operations"));
    }
    let control = state.control.as_ref().ok_or_else(ApiError::control_unavailable)?;
    let record = control
        .get(name)
        .map_err(ApiError::control)?
        .filter(|record| !record.deleted)
        .ok_or_else(|| ApiError::database_not_found(name))?;
    if record.parent.is_none() {
        return Err(refusal(
            "development_branch_required",
            "development operations require a registered branch",
            "create a cloud branch and target its name",
        ));
    }
    if !record.placement.contains(&state.region) {
        return Err(ApiError::misdirected(&record.placement, record.residency_header()));
    }
    Ok(record)
}
fn now_millis() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |v| u64::try_from(v.as_millis()).unwrap_or(u64::MAX))
}
fn audit(
    state: &ApiState,
    principal: &Principal,
    name: &str,
    action: &str,
    details: serde_json::Value,
) -> Result<(), ApiError> {
    state.control.as_ref().ok_or_else(ApiError::control_unavailable)?.development_audit(name, serde_json::json!({"actor":principal.label(),"action":action,"atMillis":now_millis(),"details":details})).map_err(ApiError::control)
}

#[utoipa::path(post, path = "/api/v1/databases/{name}/dev/faults", request_body = DevelopmentFaultRequest, responses((status = 200, description = "Bounded branch admission fault enabled", body = DevelopmentFaultResponse)))]
pub(super) async fn development_fault(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
    Extension(principal): Extension<Principal>,
    body: Result<extract::Json<DevelopmentFaultRequest>, extract::rejection::JsonRejection>,
) -> Response {
    let record = match branch(&state, &principal, &name, Verbs::ADMIN) {
        Ok(v) => v,
        Err(e) => return e.into_response(),
    };
    let request = match body {
        Ok(extract::Json(v)) => v,
        Err(_) => {
            return refusal(
                "invalid_development_fault",
                "invalid fault request",
                "send confirmBranch, fault, durationMs, maxOperations and delayMs",
            )
            .into_response();
        }
    };
    if request.confirm_branch != name
        || !(1..=30_000).contains(&request.duration_ms)
        || !(1..=100).contains(&request.max_operations)
        || match request.fault {
            FaultKind::Latency => !(1..=1000).contains(&request.delay_ms),
            FaultKind::Unavailable => request.delay_ms != 0,
        }
    {
        return refusal("invalid_development_fault", "fault confirmation or bounds are invalid", "confirm the branch name; use durationMs 1..30000, maxOperations 1..100, delayMs 1..1000 for latency or 0 for unavailable").into_response();
    }
    let Ok(mut faults) = state.development.faults.lock() else {
        return ApiError::storage_failure().into_response();
    };
    faults.retain(|_, fault| fault.expires > Instant::now() && fault.remaining > 0);
    if faults.len() >= 128 && !faults.contains_key(&name) {
        return refusal(
            "development_fault_capacity",
            "the member's development fault limit is reached",
            "clear an existing fault or wait at most 30 seconds",
        )
        .into_response();
    }
    if let Err(e) = audit(
        &state,
        &principal,
        &name,
        "fault_enabled",
        serde_json::json!({"fault":request.fault,"durationMs":request.duration_ms,"maxOperations":request.max_operations,"delayMs":request.delay_ms}),
    ) {
        return e.into_response();
    }
    faults.insert(
        name,
        ActiveFault {
            fault: request.fault,
            expires: Instant::now() + Duration::from_millis(request.duration_ms),
            remaining: request.max_operations,
            delay: Duration::from_millis(request.delay_ms),
            created_at: record.created_at,
        },
    );
    extract::Json(DevelopmentFaultResponse {
        active: true,
        expires_at_millis: now_millis().saturating_add(request.duration_ms),
    })
    .into_response()
}

#[utoipa::path(delete, path = "/api/v1/databases/{name}/dev/faults", responses((status = 200, description = "Branch fault cleared", body = DevelopmentFaultResponse)))]
pub(super) async fn clear_development_fault(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
    Extension(principal): Extension<Principal>,
) -> Response {
    if let Err(e) = branch(&state, &principal, &name, Verbs::ADMIN) {
        return e.into_response();
    }
    let Ok(mut faults) = state.development.faults.lock() else {
        return ApiError::storage_failure().into_response();
    };
    if let Err(e) = audit(&state, &principal, &name, "fault_cleared", serde_json::json!({})) {
        return e.into_response();
    }
    faults.remove(&name);
    extract::Json(DevelopmentFaultResponse { active: false, expires_at_millis: 0 }).into_response()
}

#[utoipa::path(get, path = "/api/v1/databases/{name}/dev/logs", params(("limit" = Option<u16>, Query, minimum = 1, maximum = 100)), responses((status = 200, description = "Bounded branch index lifecycle diagnostics; no document or query text", body = DevelopmentLogResponse)))]
pub(super) async fn development_logs(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
    Extension(principal): Extension<Principal>,
    query: Result<extract::Query<LogQuery>, extract::rejection::QueryRejection>,
) -> Response {
    if let Err(e) = branch(&state, &principal, &name, Verbs::READ) {
        return e.into_response();
    }
    let limit = match query {
        Ok(extract::Query(q)) if (1..=100).contains(&q.limit) => q.limit,
        _ => {
            return refusal("invalid_query", "invalid log limit", "send limit between 1 and 100")
                .into_response();
        }
    };
    if let Err(e) = check_read_guard(&state, &name, terrabase::observability::Operation::Read) {
        return e.into_response();
    }
    let host = match document_host(&state, &name) {
        Ok(v) => v,
        Err(e) => return e.into_response(),
    };
    let tenant = match terrabase::types::TenantId::try_new(&state.scope.0) {
        Ok(v) => v,
        Err(_) => return ApiError::storage_failure().into_response(),
    };
    let mut entries = match host.engine().auto_index_lifecycle_audit(&tenant, None, None, limit + 1)
    {
        Ok(v) => v,
        Err(_) => return ApiError::storage_failure().into_response(),
    };
    let truncated = entries.len() > limit;
    entries.truncate(limit);
    if let Err(e) =
        audit(&state, &principal, &name, "logs_read", serde_json::json!({"limit":limit}))
    {
        return e.into_response();
    }
    let response = DevelopmentLogResponse {
        entries: entries
            .into_iter()
            .map(|e| DevelopmentLogEntry {
                sequence: e.sequence,
                action: e.action,
                at_millis: e.at_millis,
            })
            .collect(),
        truncated,
    };
    match serde_json::to_vec(&response) {
        Ok(body) => {
            if let Err(e) = account_direct_read(&state, &name, body.len() as u64) {
                return e.into_response();
            }
            ([(CONTENT_TYPE, "application/json")], body).into_response()
        }
        Err(_) => ApiError::storage_failure().into_response(),
    }
}

/// Applies faults only after normal authorization, before any document/SQL mutation.
pub(super) async fn admit(
    state: &ApiState,
    principal: &Principal,
    path: &str,
) -> Result<(), ApiError> {
    let Some(name) = database_of(path) else { return Ok(()) };
    let parts: Vec<_> = path.split('/').collect();
    let eligible = (parts.len() == 6 && parts[5] == "sql")
        || (parts.len() >= 8 && parts[5] == "collections" && parts[7] == "documents");
    if !eligible {
        return Ok(());
    }
    let effect = {
        let mut faults =
            state.development.faults.lock().map_err(|_| ApiError::storage_failure())?;
        let Some(fault) = faults.get_mut(name) else { return Ok(()) };
        let record = state
            .control
            .as_ref()
            .ok_or_else(ApiError::control_unavailable)?
            .get(name)
            .map_err(ApiError::control)?;
        if fault.expires <= Instant::now()
            || fault.remaining == 0
            || !record.is_some_and(|record| {
                !record.deleted
                    && record.parent.is_some()
                    && record.created_at == fault.created_at
                    && record.placement.contains(&state.region)
            })
        {
            faults.remove(name);
            return Ok(());
        }
        audit(state, principal, name, "fault_applied", serde_json::json!({"fault":fault.fault}))?;
        fault.remaining -= 1;
        (fault.fault, fault.delay)
    };
    match effect {
        (FaultKind::Latency, delay) => { tokio::time::sleep(delay).await; Ok(()) }
        (FaultKind::Unavailable, _) => Err(ApiError { status: StatusCode::SERVICE_UNAVAILABLE, code: "development_fault_active", message: "this branch request was rejected by an authorized development fault".into(), fix: "clear the branch fault with DELETE /api/v1/databases/{name}/dev/faults or retry after its bounded expiry".into(), placement: None }),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use axum::body::Body;
    use tower::util::ServiceExt;

    async fn request(app: &Router, method: &str, path: &str, body: &str) -> Response {
        app.clone()
            .oneshot(
                axum::http::Request::builder()
                    .method(method)
                    .uri(path)
                    .header("authorization", "Bearer tskey_test_abcdefghijklmnop")
                    .header(CONTENT_TYPE, "application/json")
                    .body(Body::from(body.to_owned()))
                    .unwrap(),
            )
            .await
            .unwrap()
    }
    fn setup() -> (Arc<ApiState>, Router) {
        let state = super::super::tests::state_with_keys_and_control();
        let control = state.control.as_ref().unwrap();
        control.create("prod", &[RegionCode::GRU], None).unwrap();
        control.create_branch("dev", &[RegionCode::GRU], None, "prod").unwrap();
        control.create_branch("other", &[RegionCode::GRU], None, "prod").unwrap();
        let app = router(Arc::clone(&state));
        (state, app)
    }
    const ENABLE: &str = r#"{"confirmBranch":"dev","fault":"unavailable","durationMs":30000,"maxOperations":1,"delayMs":0}"#;

    #[tokio::test]
    async fn actual_document_admission_is_branch_scoped_bounded_and_cancelable() {
        let (_state, app) = setup();
        let enabled = request(&app, "POST", "/api/v1/databases/dev/dev/faults", ENABLE).await;
        assert_eq!(enabled.status(), StatusCode::OK);
        let path = "/api/v1/databases/dev/collections/items/documents/missing";
        assert_eq!(
            request(&app, "GET", "/api/v1/databases/other/collections/items/documents/missing", "")
                .await
                .status(),
            StatusCode::NOT_FOUND
        );
        assert_eq!(
            request(&app, "PUT", path, r#"{"value":1}"#).await.status(),
            StatusCode::SERVICE_UNAVAILABLE
        );
        assert_eq!(request(&app, "GET", path, "").await.status(), StatusCode::NOT_FOUND);
        assert_eq!(
            request(&app, "POST", "/api/v1/databases/dev/dev/faults", ENABLE).await.status(),
            StatusCode::OK
        );
        assert_eq!(
            request(&app, "DELETE", "/api/v1/databases/dev/dev/faults", "").await.status(),
            StatusCode::OK
        );
        assert_eq!(request(&app, "GET", path, "").await.status(), StatusCode::NOT_FOUND);
    }

    #[tokio::test]
    async fn production_wrong_confirmation_and_unbounded_faults_are_refused() {
        let (_state, app) = setup();
        assert_eq!(
            request(&app, "POST", "/api/v1/databases/prod/dev/faults", ENABLE).await.status(),
            StatusCode::BAD_REQUEST
        );
        for body in [
            ENABLE.replace("\"dev\"", "\"other\""),
            ENABLE.replace("30000", "30001"),
            ENABLE.replace("\"maxOperations\":1", "\"maxOperations\":101"),
        ] {
            assert_eq!(
                request(&app, "POST", "/api/v1/databases/dev/dev/faults", &body).await.status(),
                StatusCode::BAD_REQUEST
            );
        }
        assert_eq!(
            request(&app, "GET", "/api/v1/databases/dev/dev/logs?limit=101", "").await.status(),
            StatusCode::BAD_REQUEST
        );
        assert_eq!(
            request(&app, "GET", "/api/v1/databases/prod/dev/logs", "").await.status(),
            StatusCode::BAD_REQUEST
        );
        assert_eq!(
            request(&app, "GET", "/api/v1/databases/dev/dev/logs", "").await.status(),
            StatusCode::OK
        );
    }

    #[tokio::test]
    async fn collection_admin_and_foreign_database_credentials_cannot_configure_faults() {
        use crate::api_keys::{KeyStore, Scope};
        for scope in [
            Scope::database_collection("dev", "items", Verbs::ALL),
            Scope::database("other", Verbs::ALL),
            Scope::database("dev", Verbs::READ),
        ] {
            let (mut state, app) = setup();
            drop(app);
            Arc::get_mut(&mut state).unwrap().keys = Some(Arc::new(KeyStore::with_keys(&[(
                "tskey_test_abcdefghijklmnop",
                "scoped",
                scope,
            )])));
            let app = router(state);
            assert_eq!(
                request(&app, "POST", "/api/v1/databases/dev/dev/faults", ENABLE).await.status(),
                StatusCode::FORBIDDEN
            );
        }
    }

    #[tokio::test]
    async fn latency_delays_real_request_without_replacing_its_response() {
        let (_state, app) = setup();
        let body =
            ENABLE.replace("unavailable", "latency").replace("\"delayMs\":0", "\"delayMs\":10");
        assert_eq!(
            request(&app, "POST", "/api/v1/databases/dev/dev/faults", &body).await.status(),
            StatusCode::OK
        );
        let started = Instant::now();
        assert_eq!(
            request(&app, "GET", "/api/v1/databases/dev/collections/items/documents/missing", "")
                .await
                .status(),
            StatusCode::NOT_FOUND
        );
        assert!(started.elapsed() >= Duration::from_millis(10));
    }

    #[tokio::test]
    async fn expired_fault_does_not_affect_application_requests() {
        let (state, app) = setup();
        assert_eq!(
            request(&app, "POST", "/api/v1/databases/dev/dev/faults", ENABLE).await.status(),
            StatusCode::OK
        );
        state.development.faults.lock().unwrap().get_mut("dev").unwrap().expires = Instant::now();
        assert_eq!(
            request(&app, "GET", "/api/v1/databases/dev/collections/items/documents/missing", "")
                .await
                .status(),
            StatusCode::NOT_FOUND
        );
    }
}
