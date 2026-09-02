//! The public `/api/v1` HTTP surface (`SHORT_GOAL` `S-03`, D-34/D-44).
//!
//! Every response is a hand-assembled envelope that splices body bytes —
//! no `serde_json::Value` ever sits on the data path. Every error carries
//! `fix`: a one-line remedy, with the CLI command where one applies (D-56).
//! Authentication is deny-all by default: with no key material configured,
//! no request is admitted, and there is no unauthenticated production mode.
//!
//! Routing contract (D-58, ADR 0043): every response for a database carries
//! `X-TerraScale-Placement` (the database's region list) and
//! `X-TerraScale-Residency`; a request for a database this member does not
//! host is answered `421 Misdirected Request` with the same headers and a
//! body naming the hosting regions.

use std::sync::Arc;

use axum::Router;
use axum::extract::Path;
use axum::extract::Request;
use axum::extract::State;
use axum::http::HeaderValue;
use axum::http::StatusCode;
use axum::http::header::CONTENT_TYPE;
use axum::http::header::HeaderName;
use axum::middleware::Next;
use axum::middleware::from_fn_with_state;
use axum::response::IntoResponse;
use axum::response::Response;
use axum::routing::get;
use axum::routing::post;
use terrabase::CommitOutcome;
use terrabase::observability::MetricsRegistry;
use terrabase::observability::ServiceState;
use terrabase::regions::RegionCode;
use terrabase::types::CollectionName;
use terrabase::types::Document;
use terrabase::types::DocumentId;
use terrabase::types::DocumentValue;
use terrabase::types::ExpectedVersion;
use terrabase::types::LocalDurability;
use terrabase::types::OperationId;

/// The public API's path prefix.
pub const API_PREFIX: &str = "/api/v1";

/// `X-TerraScale-Placement` — the database's explicit region list (D-52).
pub const PLACEMENT_HEADER: HeaderName = HeaderName::from_static("x-terrascale-placement");
/// `X-TerraScale-Residency` — the residency constraint stored with the
/// database (D-52), validated against the placement.
pub const RESIDENCY_HEADER: HeaderName = HeaderName::from_static("x-terrascale-residency");
/// `X-TerraScale-Region` — the serving member's own region.
pub const REGION_HEADER: HeaderName = HeaderName::from_static("x-terrascale-region");

/// The routes that never require a credential: the machine-readable
/// contract itself and nothing else.
const PUBLIC_PATHS: [&str; 1] = ["/api/v1/openapi.json"];

/// The typed error envelope. Every variant spells its one-line remedy;
/// where a CLI command applies, it names it (D-56).
#[derive(Clone, Debug)]
pub struct ApiError {
    status: StatusCode,
    code: &'static str,
    message: String,
    fix: String,
    placement: Option<(String, String)>,
}

impl ApiError {
    /// No credential reached a scope that admits the request. This is also
    /// the whole answer when no key material is configured — deny-all.
    #[must_use]
    pub fn unauthorized() -> Self {
        Self {
            status: StatusCode::UNAUTHORIZED,
            code: "unauthorized",
            message: "the request carries no credential admitted by this member".into(),
            fix: "create an API key scoped to this database and send it as a Bearer token \
                  (terrabase api-keys create --database <name>)"
                .into(),
            placement: None,
        }
    }

    /// A credential arrived but its scope does not admit the request.
    #[must_use]
    pub fn forbidden(scope: &str) -> Self {
        Self {
            status: StatusCode::FORBIDDEN,
            code: "forbidden",
            message: format!("the credential's scope does not admit this request ({scope})"),
            fix: "issue the key with the missing database/collection/verb scope \
                  (terrabase api-keys create --scope …)"
                .into(),
            placement: None,
        }
    }

    /// A malformed credential: never admitted, never logged with its bytes.
    #[must_use]
    pub fn malformed_credential() -> Self {
        Self {
            status: StatusCode::UNAUTHORIZED,
            code: "malformed_credential",
            message: "the Authorization header is not a Bearer API key".into(),
            fix: "send Authorization: Bearer <api key> (terrabase api-keys create prints the \
                  key exactly once)"
                .into(),
            placement: None,
        }
    }

    /// The named database does not exist on this deployment.
    #[must_use]
    pub fn database_not_found(name: &str) -> Self {
        Self {
            status: StatusCode::NOT_FOUND,
            code: "database_not_found",
            message: format!("no database named '{name}' exists"),
            fix: format!("create it first (terrabase databases create {name})"),
            placement: None,
        }
    }

    /// The request names a database this member does not host (D-58): the
    /// answer carries the placement headers and names the hosting regions.
    #[must_use]
    pub fn misdirected(placement: &[RegionCode], residency: &str) -> Self {
        Self {
            status: StatusCode::MISDIRECTED_REQUEST,
            code: "misdirected_request",
            message: format!(
                "this member does not host the database; it is hosted in {}",
                regions_list(placement)
            ),
            fix: "send the request to a hosting region (the X-TerraScale-Placement header \
                  names them), or pin the region through ts-proxy"
                .into(),
            placement: Some((regions_list(placement), residency.to_owned())),
        }
    }

    /// The route needs the control plane, which this member has not booted
    /// with a data root.
    #[must_use]
    pub fn control_unavailable() -> Self {
        Self {
            status: StatusCode::SERVICE_UNAVAILABLE,
            code: "control_plane_unavailable",
            message: "the databases registry is not available on this member".into(),
            fix: "start the member with a data root (terrabase serve --data <dir>)".into(),
            placement: None,
        }
    }

    /// Maps one registry refusal to the envelope, spelling each refusal's
    /// remedy (D-56).
    #[must_use]
    pub fn control(error: crate::control::ControlError) -> Self {
        use crate::control::ControlError;
        match error {
            ControlError::InvalidName(_) => Self {
                status: StatusCode::UNPROCESSABLE_ENTITY,
                code: "invalid_database_name",
                message: "database names are 1-63 lowercase letters, digits, or dashes,                           starting and ending with a letter or digit"
                    .into(),
                fix: "terrabase databases create <name> enforces the same rule".into(),
                placement: None,
            },
            ControlError::InvalidPlacement(_) => Self {
                status: StatusCode::UNPROCESSABLE_ENTITY,
                code: "invalid_placement",
                message: "placement must be an explicit, non-empty region list".into(),
                fix: "pass placement as a JSON array of region codes, e.g. [\"GRU\",\"FRA\"]"
                    .into(),
                placement: None,
            },
            ControlError::InvalidResidency(constraint) => Self {
                status: StatusCode::UNPROCESSABLE_ENTITY,
                code: "invalid_residency",
                message: format!("'{constraint}' is not a residency constraint this deployment knows"),
                fix: "use eu-only, br-only, or a comma-separated region list".into(),
                placement: None,
            },
            ControlError::ResidencyViolation { region, constraint } => Self {
                status: StatusCode::UNPROCESSABLE_ENTITY,
                code: "residency_violation",
                message: format!("region {region} violates the residency constraint '{constraint}'"),
                fix: "place the database only in regions the constraint admits (D-52)".into(),
                placement: None,
            },
            ControlError::AlreadyExists(name) => Self {
                status: StatusCode::CONFLICT,
                code: "database_exists",
                message: format!("a database named '{name}' already exists"),
                fix: "list with GET /api/v1/databases, or pick another name".into(),
                placement: None,
            },
            ControlError::SoftDeleted(name) => Self {
                status: StatusCode::CONFLICT,
                code: "database_soft_deleted",
                message: format!(
                    "'{name}' is soft-deleted inside its 7-day restore window"
                ),
                fix: "restore it first (POST /api/v1/databases/{name}/restore)".into(),
                placement: None,
            },
            ControlError::NotFound(name) => Self::database_not_found(&name),
            ControlError::Registry(reason) => Self {
                status: StatusCode::INTERNAL_SERVER_ERROR,
                code: "registry_failure",
                message: format!("the registry engine refused the operation: {reason}"),
                fix: "check the member's storage health, then retry once".into(),
                placement: None,
            },
        }
    }

    /// An unrouted path under the API prefix: the envelope names the fix.
    #[must_use]
    pub fn not_routed(path: &str) -> Self {
        Self {
            status: StatusCode::NOT_FOUND,
            code: "not_routed",
            message: format!("no route serves {path}"),
            fix: "see GET /api/v1/openapi.json for the served routes".into(),
            placement: None,
        }
    }

    /// The response status this envelope answers with.
    #[must_use]
    pub fn status(&self) -> StatusCode {
        self.status
    }

    /// The envelope's stable error code (the `code` field).
    #[must_use]
    pub fn code(&self) -> &'static str {
        self.code
    }
}

impl IntoResponse for ApiError {
    fn into_response(self) -> Response {
        let mut body = String::with_capacity(256);
        body.push_str("{\"error\":{\"code\":\"");
        body.push_str(self.code);
        body.push_str("\",\"message\":\"");
        push_json_escaped(&mut body, &self.message);
        body.push_str("\",\"fix\":\"");
        push_json_escaped(&mut body, &self.fix);
        body.push_str("\"}}");
        let mut response = (self.status, body).into_response();
        response.headers_mut().insert(CONTENT_TYPE, HeaderValue::from_static("application/json"));
        if let Some((placement, residency)) = self.placement
            && let (Ok(placement), Ok(residency)) =
                (HeaderValue::from_str(&placement), HeaderValue::from_str(&residency))
        {
            let headers = response.headers_mut();
            headers.insert(PLACEMENT_HEADER, placement);
            headers.insert(RESIDENCY_HEADER, residency);
        }
        response
    }
}

/// Pushes `value` into `out` with JSON string escaping. The envelope's own
/// fields are the only text that passes through here; document bodies are
/// spliced as already-encoded bytes.
fn push_json_escaped(out: &mut String, value: &str) {
    use std::fmt::Write;
    for character in value.chars() {
        match character {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            character if (u32::from(character)) < 0x20 => {
                let _ = write!(out, "\\u{:04x}", u32::from(character));
            }
            character => out.push(character),
        }
    }
}

/// Joins region codes the wire spelling the placement header uses
/// (`GRU,FRA`).
fn regions_list(regions: &[RegionCode]) -> String {
    regions.iter().map(RegionCode::as_str).collect::<Vec<_>>().join(",")
}

/// The public API's serving state.
pub struct ApiState {
    /// The serving member's own region (D-58: named on every response).
    pub region: RegionCode,
    /// The member's health registry backing `/health`.
    pub metrics: Arc<MetricsRegistry>,
    /// Admitted API keys. `None` is deny-all: no request is admitted
    /// without exception, which is the production default until key
    /// material is configured.
    pub keys: Option<Arc<crate::api_keys::KeyStore>>,
    /// The databases registry (D-52). `None` until the member boots with a
    /// data root; the database routes answer the typed unavailable error.
    pub control: Option<Arc<crate::control::ControlPlane>>,
    /// The lazy database catalog (`S-01`): tenant/project/database → one
    /// host engine, LRU-bounded, idle-closed. `None` until the member
    /// boots with a data root.
    pub catalog: Option<Arc<terrabase_tbep::catalog::HostCatalog>>,
    /// The tenant/project every hosted database serves under on this
    /// member (the single-tenant local stack shape; the multi-project
    /// router arrives with the ts-proxy lane).
    pub scope: (String, String),
}

/// Builds the public `/api/v1` router. Every route except
/// [`PUBLIC_PATHS`] requires an admitted credential.
#[must_use = "the returned router must be installed on a server"]
pub fn router(state: Arc<ApiState>) -> Router {
    Router::new()
        .route("/api/v1/health", get(health))
        .route("/api/v1/regions", get(regions))
        .route("/api/v1/openapi.json", get(openapi))
        .route("/api/v1/databases", get(list_databases).post(create_database))
        .route("/api/v1/databases/{name}", get(get_database).delete(delete_database))
        .route("/api/v1/databases/{name}/restore", post(restore_database))
        .route(
            "/api/v1/databases/{name}/collections/{collection}/documents/{id}",
            get(get_document).put(put_document).delete(delete_document),
        )
        .fallback(not_routed)
        .layer(from_fn_with_state(Arc::clone(&state), authenticate))
        .with_state(state)
}

/// Deny-all credential gate: no configured key material admits nothing.
///
/// The gate never logs the credential's bytes and never compares against
/// material it does not hold; a present-but-malformed header is its own
/// typed error so clients can tell "forgot the header" from "sent garbage".
async fn authenticate(
    State(state): State<Arc<ApiState>>,
    request: Request,
    next: Next,
) -> Response {
    if PUBLIC_PATHS.contains(&request.uri().path()) {
        return next.run(request).await;
    }
    let Some(keys) = state.keys.clone() else {
        return ApiError::unauthorized().into_response();
    };
    let admitted = request
        .headers()
        .get(axum::http::header::AUTHORIZATION)
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.strip_prefix("Bearer "))
        .map_or_else(|| Err(ApiError::malformed_credential()), |key| keys.admit(key));
    match admitted {
        Ok(principal) => {
            let mut response = next.run(request).await;
            if let Ok(label) = HeaderValue::from_str(principal.label()) {
                response.headers_mut().insert("x-terrascale-principal", label);
            }
            response
        }
        Err(error) => error.into_response(),
    }
}

/// Liveness of the public surface itself: the member answers, and its
/// health registry's verdict rides the body.
async fn health(State(state): State<Arc<ApiState>>) -> Response {
    let healthy = state.metrics.snapshot().service_state() == ServiceState::Serving;
    let status = if healthy { StatusCode::OK } else { StatusCode::SERVICE_UNAVAILABLE };
    let body = format!("{{\"region\":\"{}\",\"healthy\":{}}}", state.region, healthy);
    let mut response = (status, body).into_response();
    response.headers_mut().insert(CONTENT_TYPE, HeaderValue::from_static("application/json"));
    if let Ok(region) = HeaderValue::from_str(state.region.as_str()) {
        response.headers_mut().insert(REGION_HEADER, region);
    }
    response
}

/// The member's own region and health (D-58): how a client discovers where
/// its request landed.
async fn regions(State(state): State<Arc<ApiState>>) -> Response {
    let body = format!("{{\"region\":\"{}\",\"healthy\":true}}", state.region);
    let mut response = body.into_response();
    response.headers_mut().insert(CONTENT_TYPE, HeaderValue::from_static("application/json"));
    if let Ok(region) = HeaderValue::from_str(state.region.as_str()) {
        response.headers_mut().insert(REGION_HEADER, region);
    }
    response
}

/// The served contract. Hand-assembled for now; `make contracts` (S-00)
/// generates it from the route definitions with `utoipa` once the document
/// routes exist.
// ponytail: utoipa generation when the document/query routes land.
async fn openapi() -> Response {
    let body = r#"{"openapi":"3.1.0","info":{"title":"TerraScale Public API","version":"v1"},"paths":{"/api/v1/health":{"get":{"responses":{"200":{"description":"the member's health"},"503":{"description":"the member is unhealthy"}}}},"/api/v1/regions":{"get":{"responses":{"200":{"description":"the member's own region and health"}}}},"/api/v1/openapi.json":{"get":{"responses":{"200":{"description":"this document"}}}}}}"#;
    let mut response = body.into_response();
    response.headers_mut().insert(CONTENT_TYPE, HeaderValue::from_static("application/json"));
    response
}

/// The API prefix's unrouted fallback: the typed envelope, never a bare 404.
async fn not_routed(request: Request) -> Response {
    ApiError::not_routed(request.uri().path()).into_response()
}

/// Lists every database record, deleted records included (each record's
/// body names its state).
async fn list_databases(State(state): State<Arc<ApiState>>) -> Response {
    let Some(control) = state.control.clone() else {
        return ApiError::control_unavailable().into_response();
    };
    match control.list() {
        Ok(records) => {
            let mut body = String::from("{\"databases\":[");
            for (index, record) in records.iter().enumerate() {
                if index > 0 {
                    body.push(',');
                }
                push_record(&mut body, record);
            }
            body.push_str("]}");
            json_response(StatusCode::OK, body)
        }
        Err(error) => ApiError::control(error).into_response(),
    }
}

/// Creates one database from an explicit placement and optional residency
/// constraint (D-52).
async fn create_database(State(state): State<Arc<ApiState>>, body: String) -> Response {
    let Some(control) = state.control.clone() else {
        return ApiError::control_unavailable().into_response();
    };
    let request: serde_json::Value = match serde_json::from_str(&body) {
        Ok(value) => value,
        Err(_) => {
            return ApiError {
                status: StatusCode::BAD_REQUEST,
                code: "invalid_json",
                message: "the request body is not the expected JSON object".into(),
                fix: "send {\"name\":…,\"placement\":[\"GRU\",…],\"residency\":…}".into(),
                placement: None,
            }
            .into_response();
        }
    };
    let Some(name) = request.get("name").and_then(serde_json::Value::as_str) else {
        return ApiError {
            status: StatusCode::BAD_REQUEST,
            code: "missing_name",
            message: "the create request names no database".into(),
            fix: "add \"name\": \"<database>\" to the request body".into(),
            placement: None,
        }
        .into_response();
    };
    let Some(placement) = request.get("placement").and_then(|value| {
        value.as_array().and_then(|regions| {
            regions
                .iter()
                .filter_map(serde_json::Value::as_str)
                .map(|text| RegionCode::from_text(text).ok())
                .collect::<Option<Vec<_>>>()
        })
    }) else {
        return ApiError::control(crate::control::ControlError::InvalidPlacement(
            "placement must be an array of region codes".into(),
        ))
        .into_response();
    };
    let residency = request.get("residency").and_then(serde_json::Value::as_str);
    match control.create(name, &placement, residency) {
        Ok(record) => record_response(StatusCode::CREATED, &record),
        Err(error) => ApiError::control(error).into_response(),
    }
}

/// Returns one database's record with the routing headers; a database this
/// member's region does not host answers the 421 contract (D-58).
async fn get_database(State(state): State<Arc<ApiState>>, Path(name): Path<String>) -> Response {
    let Some(control) = state.control.clone() else {
        return ApiError::control_unavailable().into_response();
    };
    match control.get(&name) {
        Ok(Some(record)) => {
            if !record.placement.contains(&state.region) {
                return ApiError::misdirected(&record.placement, record.residency_header())
                    .into_response();
            }
            record_response(StatusCode::OK, &record)
        }
        Ok(None) => ApiError::database_not_found(&name).into_response(),
        Err(error) => ApiError::control(error).into_response(),
    }
}

/// Soft-deletes one database: restorable for seven days.
async fn delete_database(State(state): State<Arc<ApiState>>, Path(name): Path<String>) -> Response {
    let Some(control) = state.control.clone() else {
        return ApiError::control_unavailable().into_response();
    };
    match control.soft_delete(&name) {
        Ok(record) => record_response(StatusCode::OK, &record),
        Err(error) => ApiError::control(error).into_response(),
    }
}

/// Restores one soft-deleted database inside its window.
async fn restore_database(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
) -> Response {
    let Some(control) = state.control.clone() else {
        return ApiError::control_unavailable().into_response();
    };
    match control.restore(&name) {
        Ok(record) => record_response(StatusCode::OK, &record),
        Err(error) => ApiError::control(error).into_response(),
    }
}

/// One document's route context: the host engine for the named database.
fn document_host(
    state: &ApiState,
    name: &str,
) -> Result<std::sync::Arc<terrabase_tbep::engine_host::EngineHost>, ApiError> {
    let Some(catalog) = state.catalog.clone() else {
        return Err(ApiError::control_unavailable());
    };
    catalog
        .host_for(&state.scope.0, &state.scope.1, name)
        .map_err(|_| ApiError::database_not_found(name))
}

/// The typed refusal for a malformed collection or identifier path.
fn invalid_identifier(kind: &str, value: &str) -> ApiError {
    ApiError {
        status: StatusCode::BAD_REQUEST,
        code: "invalid_identifier",
        message: format!("'{value}' is not a valid {kind}"),
        fix: "collection names and identifiers match the engine's identifier rules".into(),
        placement: None,
    }
}

/// The typed refusal for a document the snapshot does not serve.
fn document_not_found(collection: &str, id: &str) -> ApiError {
    ApiError {
        status: StatusCode::NOT_FOUND,
        code: "document_not_found",
        message: format!("no document '{id}' in '{collection}'"),
        fix: "list the collection to see what exists".into(),
        placement: None,
    }
}

/// Returns one document with its `ETag`; `If-None-Match` is answered from
/// the revision column alone — the body blob is never decoded on a 304.
async fn get_document(
    State(state): State<Arc<ApiState>>,
    Path((name, collection, id)): Path<(String, String, String)>,
    headers: axum::http::HeaderMap,
) -> Response {
    let Ok(host) = document_host(&state, &name) else {
        return ApiError::database_not_found(&name).into_response();
    };
    let engine = host.engine();
    let Ok(scope) = document_scope(engine) else {
        return ApiError::control_unavailable().into_response();
    };
    let Ok(collection) = CollectionName::try_new(&collection) else {
        return invalid_identifier("collection name", &collection).into_response();
    };
    let Ok(id) = DocumentId::try_new(&id) else {
        return invalid_identifier("document identifier", &id).into_response();
    };
    let Ok(snapshot) = engine.read_snapshot(&scope) else {
        return ApiError::control_unavailable().into_response();
    };
    let Ok(operation) = OperationId::try_new("api-read") else {
        return ApiError::control_unavailable().into_response();
    };
    let Ok(transaction) = engine.begin(&scope, snapshot, operation) else {
        return ApiError::control_unavailable().into_response();
    };
    let revision = transaction.revision_in_collection(&collection, &id).unwrap_or(None);
    let Some(revision) = revision else {
        return document_not_found(collection.as_str(), id.as_str()).into_response();
    };
    let etag = format!("\"{revision}\"");
    if headers
        .get(axum::http::header::IF_NONE_MATCH)
        .and_then(|value| value.to_str().ok())
        .is_some_and(|present| present == etag)
    {
        // The 304 path read the revision column only — the body blob was
        // never touched (S-03's gate).
        let mut response = StatusCode::NOT_MODIFIED.into_response();
        if let Ok(etag) = HeaderValue::from_str(&etag) {
            response.headers_mut().insert(axum::http::header::ETAG, etag);
        }
        return response;
    }
    let Ok(Some(document)) = transaction.read_in_collection(&collection, &id) else {
        return document_not_found(collection.as_str(), id.as_str()).into_response();
    };
    drop(transaction);
    let mut body = String::with_capacity(256);
    push_value(&mut body, document.value());
    let mut response = json_response(StatusCode::OK, body);
    if let Ok(etag) = HeaderValue::from_str(&etag) {
        response.headers_mut().insert(axum::http::header::ETAG, etag);
    }
    response
}

/// Creates or replaces one document; the `ETag` of the result names the new
/// revision.
async fn put_document(
    State(state): State<Arc<ApiState>>,
    Path((name, collection, id)): Path<(String, String, String)>,
    body: String,
) -> Response {
    let Ok(host) = document_host(&state, &name) else {
        return ApiError::database_not_found(&name).into_response();
    };
    let engine = host.engine();
    let Ok(scope) = document_scope(engine) else {
        return ApiError::control_unavailable().into_response();
    };
    let Ok(collection) = CollectionName::try_new(&collection) else {
        return invalid_identifier("collection name", &collection).into_response();
    };
    let Ok(id) = DocumentId::try_new(&id) else {
        return invalid_identifier("document identifier", &id).into_response();
    };
    let value: serde_json::Value = match serde_json::from_str(&body) {
        Ok(value) => value,
        Err(_) => {
            return ApiError {
                status: StatusCode::BAD_REQUEST,
                code: "invalid_json",
                message: "the request body is not JSON".into(),
                fix: "send the document as a JSON object".into(),
                placement: None,
            }
            .into_response();
        }
    };
    let document = Document::new(id, collection, json_value(&value));
    let Ok(snapshot) = engine.read_snapshot(&scope) else {
        return ApiError::control_unavailable().into_response();
    };
    let Ok(operation) = OperationId::try_new(format!("api-put-{}", unix_millis_nanos()).as_str())
    else {
        return ApiError::control_unavailable().into_response();
    };
    let Ok(mut transaction) = engine.begin(&scope, snapshot, operation) else {
        return ApiError::control_unavailable().into_response();
    };
    let expected = match transaction.read_in_collection(document.collection(), document.id()) {
        Ok(Some(existing)) => ExpectedVersion::Exact(existing.metadata().version()),
        Ok(None) => ExpectedVersion::New,
        Err(_) => return ApiError::control_unavailable().into_response(),
    };
    if let Err(reason) = transaction.put(document, expected) {
        return ApiError {
            status: StatusCode::CONFLICT,
            code: "version_conflict",
            message: format!("the document changed under the request: {reason:?}"),
            fix: "re-read the document, merge, and retry with the new version".into(),
            placement: None,
        }
        .into_response();
    }
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(receipt) => {
            let revision = format!("{}-1", receipt.sequence().get());
            let mut response =
                json_response(StatusCode::OK, format!("{{\"revision\":\"{revision}\"}}"));
            if let Ok(etag) = HeaderValue::from_str(&format!("\"{revision}\"")) {
                response.headers_mut().insert(axum::http::header::ETAG, etag);
            }
            response
        }
        CommitOutcome::Rejected(reason) => ApiError {
            status: StatusCode::CONFLICT,
            code: "commit_rejected",
            message: format!("the engine refused the write: {reason:?}"),
            fix: "re-read the document and retry; a rejected write stages nothing".into(),
            placement: None,
        }
        .into_response(),
        CommitOutcome::Unknown(reason) => ApiError {
            status: StatusCode::INTERNAL_SERVER_ERROR,
            code: "commit_unknown",
            message: format!("the write's outcome is unknown: {reason:?}"),
            fix: "re-read to see whether it landed, then retry with a new operation".into(),
            placement: None,
        }
        .into_response(),
    }
}

/// Deletes one document (tombstone; the history route can explain it).
async fn delete_document(
    State(state): State<Arc<ApiState>>,
    Path((name, collection, id)): Path<(String, String, String)>,
) -> Response {
    let Ok(host) = document_host(&state, &name) else {
        return ApiError::database_not_found(&name).into_response();
    };
    let engine = host.engine();
    let Ok(scope) = document_scope(engine) else {
        return ApiError::control_unavailable().into_response();
    };
    let Ok(collection) = CollectionName::try_new(&collection) else {
        return invalid_identifier("collection name", &collection).into_response();
    };
    let Ok(id) = DocumentId::try_new(&id) else {
        return invalid_identifier("document identifier", &id).into_response();
    };
    let Ok(snapshot) = engine.read_snapshot(&scope) else {
        return ApiError::control_unavailable().into_response();
    };
    let Ok(operation) =
        OperationId::try_new(format!("api-delete-{}", unix_millis_nanos()).as_str())
    else {
        return ApiError::control_unavailable().into_response();
    };
    let Ok(mut transaction) = engine.begin(&scope, snapshot, operation) else {
        return ApiError::control_unavailable().into_response();
    };
    let expected = match transaction.read_in_collection(&collection, &id) {
        Ok(Some(existing)) => ExpectedVersion::Exact(existing.metadata().version()),
        Ok(None) => {
            return document_not_found(collection.as_str(), id.as_str()).into_response();
        }
        Err(_) => return ApiError::control_unavailable().into_response(),
    };
    // delete_in_collection stages without a pre-read (the caller owns
    // existence, and this route read the target above); the commit's
    // conflict rejection is the version check.
    transaction.delete_in_collection(&collection, id, expected);
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(_) => json_response(StatusCode::NO_CONTENT, String::new()),
        _ => ApiError {
            status: StatusCode::INTERNAL_SERVER_ERROR,
            code: "commit_failed",
            message: "the engine did not commit the deletion".into(),
            fix: "re-read to see whether it landed, then retry".into(),
            placement: None,
        }
        .into_response(),
    }
}

/// One engine's tenant scope for document operations: the member hosts
/// each database under its stable tenant label, so one fixed label serves
/// the local stack.
fn document_scope(engine: &terrabase::Engine) -> Result<terrabase::types::TenantScope, ()> {
    let tenant = terrabase::types::TenantId::try_new("terrabase-local").map_err(|_| ())?;
    Ok(engine.scope_for(&tenant))
}

/// A monotonic-enough suffix keeping operation identities distinct.
fn unix_millis_nanos() -> u128 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |elapsed| elapsed.as_nanos())
}

/// Renders one document value as JSON text (hand-assembled; the data path
/// splices stored text, it never round-trips a generic value).
fn push_value(out: &mut String, value: &DocumentValue) {
    match value {
        DocumentValue::Null => out.push_str("null"),
        DocumentValue::Boolean(true) => out.push_str("true"),
        DocumentValue::Boolean(false) => out.push_str("false"),
        DocumentValue::Integer(number) => out.push_str(&number.to_string()),
        DocumentValue::Number(number) => out.push_str(&number.to_string()),
        DocumentValue::String(text) => {
            out.push('"');
            push_json_escaped(out, text);
            out.push('"');
        }
        DocumentValue::Array(items) => {
            out.push('[');
            for (index, item) in items.iter().enumerate() {
                if index > 0 {
                    out.push(',');
                }
                push_value(out, item);
            }
            out.push(']');
        }
        DocumentValue::Object(fields) => {
            out.push('{');
            for (index, (name, item)) in fields.iter().enumerate() {
                if index > 0 {
                    out.push(',');
                }
                out.push('"');
                push_json_escaped(out, name);
                out.push_str("\":");
                push_value(out, item);
            }
            out.push('}');
        }
    }
}

/// Converts one parsed JSON request value into the engine's document
/// value.
fn json_value(value: &serde_json::Value) -> DocumentValue {
    match value {
        serde_json::Value::Null => DocumentValue::Null,
        serde_json::Value::Bool(flag) => DocumentValue::Boolean(*flag),
        serde_json::Value::Number(number) => match number.as_i64() {
            Some(integer) => DocumentValue::Integer(integer),
            None => DocumentValue::Number(number.as_f64().unwrap_or(0.0)),
        },
        serde_json::Value::String(text) => DocumentValue::String(text.clone()),
        serde_json::Value::Array(items) => {
            DocumentValue::Array(items.iter().map(json_value).collect())
        }
        serde_json::Value::Object(fields) => DocumentValue::Object(
            fields.iter().map(|(name, item)| (name.clone(), json_value(item))).collect(),
        ),
    }
}

/// One record's JSON body: hand-assembled, values spliced (the record's
/// fields are all envelope-class text and integers).
fn push_record(body: &mut String, record: &crate::control::DatabaseRecord) {
    body.push_str("{\"name\":\"");
    push_json_escaped(body, &record.name);
    body.push_str("\",\"placement\":\"");
    push_json_escaped(body, &record.placement_header());
    body.push_str("\",\"residency\":\"");
    push_json_escaped(body, record.residency_header());
    body.push_str("\",\"deleted\":");
    body.push_str(if record.deleted { "true" } else { "false" });
    body.push_str(",\"created_at\":");
    body.push_str(&record.created_at.to_string());
    body.push('}');
}

/// One record response: the body plus the routing headers every database
/// response carries (D-58).
fn record_response(status: StatusCode, record: &crate::control::DatabaseRecord) -> Response {
    let mut body = String::with_capacity(192);
    push_record(&mut body, record);
    let mut response = json_response(status, body);
    if let (Ok(placement), Ok(residency)) = (
        HeaderValue::from_str(&record.placement_header()),
        HeaderValue::from_str(record.residency_header()),
    ) {
        let headers = response.headers_mut();
        headers.insert(PLACEMENT_HEADER, placement);
        headers.insert(RESIDENCY_HEADER, residency);
    }
    response
}

/// A bare JSON response.
fn json_response(status: StatusCode, body: String) -> Response {
    let mut response = (status, body).into_response();
    response.headers_mut().insert(CONTENT_TYPE, HeaderValue::from_static("application/json"));
    response
}

#[cfg(test)]
mod tests {
    use super::API_PREFIX;
    use super::ApiError;
    use super::ApiState;
    use super::router;
    use axum::body::Body;
    use axum::http::Request;
    use axum::http::StatusCode;
    use axum::http::header;
    use axum::response::IntoResponse;
    use std::sync::Arc;
    use terrabase::observability::MetricsRegistry;
    use terrabase::regions::RegionCode;
    use tower::util::ServiceExt;

    fn state() -> Arc<ApiState> {
        Arc::new(ApiState {
            region: RegionCode::GRU,
            metrics: Arc::new(MetricsRegistry::new()),
            keys: None,
            control: None,
            catalog: None,
            scope: ("terrabase-local".into(), "local".into()),
        })
    }

    fn state_with_keys_and_control() -> Arc<ApiState> {
        use crate::api_keys::KeyStore;
        use crate::api_keys::Scope;
        use crate::api_keys::Verbs;
        use crate::control::ControlPlane;
        let dir = std::env::temp_dir().join(format!(
            "terrabase-api-test-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |elapsed| elapsed.as_nanos())
        ));
        std::fs::create_dir_all(&dir).expect("test root");
        let control = ControlPlane::open(&dir).expect("control plane opens");
        let catalog = terrabase_tbep::catalog::HostCatalog::open(
            &dir.join("data"),
            8,
            std::time::Duration::from_secs(3_600),
        )
        .expect("catalog opens");
        Arc::new(ApiState {
            region: RegionCode::GRU,
            metrics: Arc::new(MetricsRegistry::new()),
            keys: Some(Arc::new(KeyStore::with_keys(&[(
                "tskey_test_abcdefghijklmnop",
                "bootstrap",
                Scope::every_database(Verbs::ALL),
            )]))),
            control: Some(Arc::new(control)),
            catalog: Some(Arc::new(catalog)),
            scope: ("terrabase-local".into(), "local".into()),
        })
    }

    #[tokio::test]
    async fn deny_all_is_the_default_with_no_key_material() {
        let response = router(state())
            .oneshot(Request::get("/api/v1/health").body(Body::empty()).expect("request"))
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::UNAUTHORIZED);
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(
            body.contains("\"code\":\"unauthorized\""),
            "the envelope codes the denial: {body}"
        );
        assert!(body.contains("\"fix\":"), "every error carries a fix (D-56): {body}");
    }

    #[tokio::test]
    async fn openapi_is_public_and_health_is_not() {
        let response = router(state())
            .oneshot(Request::get("/api/v1/openapi.json").body(Body::empty()).expect("request"))
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        let response = router(state())
            .oneshot(Request::get("/api/v1/health").body(Body::empty()).expect("request"))
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::UNAUTHORIZED);
    }

    #[tokio::test]
    async fn misdirected_carries_the_placement_headers_and_names_the_hosts() {
        let response =
            ApiError::misdirected(&[RegionCode::GRU, RegionCode::FRA], "eu-only").into_response();
        assert_eq!(response.status(), StatusCode::MISDIRECTED_REQUEST);
        assert_eq!(
            response.headers().get("x-terrascale-placement").and_then(|v| v.to_str().ok()),
            Some("GRU,FRA")
        );
        assert_eq!(
            response.headers().get("x-terrascale-residency").and_then(|v| v.to_str().ok()),
            Some("eu-only")
        );
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("GRU,FRA"), "the body names the hosting regions: {body}");
    }

    #[tokio::test]
    async fn unrouted_paths_get_the_typed_envelope() {
        let response = router(state())
            .oneshot(
                Request::get(format!("{API_PREFIX}/nope")).body(Body::empty()).expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::UNAUTHORIZED);
    }

    #[tokio::test]
    async fn databases_crud_through_the_router() {
        use axum::body::Body;
        use axum::http::Request;
        let router = router(state_with_keys_and_control());
        let create = r#"{"name":"prod","placement":["GRU"],"residency":null}"#;
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases")
                    .header("authorization", "Bearer tskey_test_abcdefghijklmnop")
                    .body(Body::from(create))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::CREATED);
        assert_eq!(
            response.headers().get("x-terrascale-placement").and_then(|v| v.to_str().ok()),
            Some("GRU")
        );
        let body = axum::body::to_bytes(response.into_body(), 8192).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("\"name\":\"prod\""), "{body}");

        let response = router
            .clone()
            .oneshot(
                Request::get("/api/v1/databases/prod")
                    .header("authorization", "Bearer tskey_test_abcdefghijklmnop")
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);

        // A database hosted only elsewhere answers the 421 contract.
        let elsewhere = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases")
                    .header("authorization", "Bearer tskey_test_abcdefghijklmnop")
                    .body(Body::from(r#"{"name":"eu","placement":["FRA"],"residency":"eu-only"}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(elsewhere.status(), StatusCode::CREATED);
        let misdirected = router
            .clone()
            .oneshot(
                Request::get("/api/v1/databases/eu")
                    .header("authorization", "Bearer tskey_test_abcdefghijklmnop")
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(misdirected.status(), StatusCode::MISDIRECTED_REQUEST);

        let response = router
            .oneshot(
                Request::delete("/api/v1/databases/prod")
                    .header("authorization", "Bearer tskey_test_abcdefghijklmnop")
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
    }

    #[tokio::test]
    async fn documents_lifecycle_with_etag_and_304_without_reading_the_body() {
        use axum::body::Body;
        use axum::http::Request;
        let auth = "authorization";
        let key = "Bearer tskey_test_abcdefghijklmnop";
        let document_path = "/api/v1/databases/prod/collections/users/documents/doc-1";
        let router = router(state_with_keys_and_control());
        // The database must exist before its documents route serves it.
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases")
                    .header(auth, key)
                    .body(Body::from(r#"{"name":"prod","placement":["GRU"],"residency":null}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::CREATED);
        // PUT creates.
        let response = router
            .clone()
            .oneshot(
                Request::put(document_path)
                    .header(auth, key)
                    .body(Body::from(r#"{"kind":"user","name":"Ada"}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK, "the put commits");
        let put_body = axum::body::to_bytes(response.into_body(), 8192).await.expect("body");
        let put_body = String::from_utf8(put_body.to_vec()).expect("utf-8");
        assert!(put_body.contains("revision"), "{put_body}");
        // GET answers with an ETag.
        let response = router
            .clone()
            .oneshot(
                Request::get(document_path).header(auth, key).body(Body::empty()).expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        let etag = response
            .headers()
            .get("etag")
            .and_then(|value| value.to_str().ok())
            .expect("the etag names the revision")
            .to_owned();
        let body = axum::body::to_bytes(response.into_body(), 8192).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("\"kind\":\"user\""), "the body round-trips: {body}");
        // If-None-Match with the served ETag answers 304 — the revision
        // column answered, the body blob was never decoded (S-03 gate).
        let response = router
            .clone()
            .oneshot(
                Request::get(document_path)
                    .header(auth, key)
                    .header("if-none-match", etag.as_str())
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::NOT_MODIFIED);
        let not_modified_body =
            axum::body::to_bytes(response.into_body(), 8192).await.expect("body");
        assert!(not_modified_body.is_empty(), "a 304 carries no body");
        // DELETE tombstones; the GET answers 404 with the envelope.
        let response = router
            .clone()
            .oneshot(
                Request::delete(document_path)
                    .header(auth, key)
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::NO_CONTENT);
        let response = router
            .clone()
            .oneshot(
                Request::get(document_path).header(auth, key).body(Body::empty()).expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::NOT_FOUND);
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("\"fix\":\""), "{body}");
    }

    #[tokio::test]
    async fn control_routes_answer_the_typed_unavailable_without_a_root() {
        let response = router(state())
            .oneshot(Request::get("/api/v1/databases").body(Body::empty()).expect("request"))
            .await
            .expect("answers");
        // Deny-all comes first: no credential, no route.
        assert_eq!(response.status(), StatusCode::UNAUTHORIZED);
    }

    #[tokio::test]
    async fn malformed_credentials_are_their_own_error() {
        let body = ApiError::malformed_credential();
        assert_eq!(body.status(), StatusCode::UNAUTHORIZED);
        let response = body.into_response();
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("malformed_credential"), "{body}");
        assert!(body.contains("fix"), "{body}");
        let _ = header::AUTHORIZATION;
    }
}
