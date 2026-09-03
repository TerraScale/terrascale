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
use axum::extract;
use axum::extract::Extension;
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
use utoipa_axum::router::OpenApiRouter;
use utoipa_axum::routes;
use utoipa_scalar::Scalar;
use utoipa_scalar::Servable;

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
const PUBLIC_PATHS: [&str; 2] = ["/api/v1/openapi.json", "/scalar"];

/// The typed error envelope. Every variant spells its one-line remedy;
/// where a CLI command applies, it names it (D-56).
#[derive(Clone, Debug)]
pub struct ApiError {
    pub(crate) status: StatusCode,
    pub(crate) code: &'static str,
    pub(crate) message: String,
    pub(crate) fix: String,
    pub(crate) placement: Option<(String, String)>,
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

    /// One typed TQL refusal: every parser class spells its remedy, and the
    /// message carries the offending construct and source position when the
    /// error has one (D-56).
    #[must_use]
    pub fn tql(error: &terrabase::query::tql::TqlError) -> Self {
        use terrabase::query::tql::TqlErrorClass;
        let (code, fix): (&'static str, &str) = match error.class() {
            TqlErrorClass::MissingGrammarVersion => {
                ("missing_grammar_version", "declare the version: add \"grammar\":\"tql/1.0\"")
            }
            TqlErrorClass::UnsupportedGrammarVersion => (
                "unsupported_grammar_version",
                "declare \"grammar\":\"tql/1.0\" — the only implemented version",
            ),
            TqlErrorClass::ParseError => {
                ("tql_parse_error", "fix the text at the reported position (docs/tql/examples.md)")
            }
            TqlErrorClass::UnknownFunction => {
                ("unknown_function", "call a tql/1.0 registry function (docs/tql/spec.md §7)")
            }
            TqlErrorClass::InvalidArgument => {
                ("invalid_tql_argument", "match the registry function's arity and argument types")
            }
            TqlErrorClass::UnboundParameter => {
                ("missing_parameter", "bind the $name in the request's parameters object")
            }
            TqlErrorClass::UnexpectedParameter => {
                ("unexpected_parameter", "remove bindings the text does not reference")
            }
            TqlErrorClass::CapabilityUnavailable => (
                "capability_unavailable",
                "this engine does not ship the capability yet; drop the predicate",
            ),
            TqlErrorClass::InvalidBound => {
                ("tql_bound_exceeded", "keep limit/offset within the documented bounds")
            }
            TqlErrorClass::InvalidIdentifier | TqlErrorClass::ReservedName => {
                ("invalid_tql_identifier", "use a plain, unreserved identifier")
            }
            TqlErrorClass::UnsupportedForPlanning => (
                "tql_not_plannable",
                "the construct parses but has no exact plan yet; see docs/tql/examples.md",
            ),
            TqlErrorClass::InvalidRequest => {
                ("invalid_tql_request", "the request context was refused; resend unchanged")
            }
        };
        let mut message = format!("the TQL text was refused ({code})");
        if let Some(construct) = error.construct() {
            message.push_str(": ");
            message.push_str(construct);
        }
        if let Some(position) = error.position() {
            use std::fmt::Write;
            let _ = write!(message, " at line {}, column {}", position.line(), position.column());
        }
        Self { status: StatusCode::BAD_REQUEST, code, message, fix: fix.into(), placement: None }
    }

    /// One typed compile/execution refusal off the query seam.
    #[must_use]
    pub fn query(error: terrabase::query::QueryError) -> Self {
        use terrabase::query::QueryError;
        match error {
            QueryError::IndexNotReady => Self {
                status: StatusCode::SERVICE_UNAVAILABLE,
                code: "index_not_ready",
                message: "a required index is still building".into(),
                fix: "retry in 800 ms, or wait for the build (terrascale index wait <database>)"
                    .into(),
                placement: None,
            },
            QueryError::Deadline => Self {
                status: StatusCode::GATEWAY_TIMEOUT,
                code: "query_deadline",
                message: "the query outlived its execution bound".into(),
                fix: "narrow the filter or page the results; the server bound is 30 s".into(),
                placement: None,
            },
            QueryError::CandidateBoundExceeded
            | QueryError::IncludeBoundExceeded
            | QueryError::IncludeCycle => Self {
                status: StatusCode::UNPROCESSABLE_ENTITY,
                code: "query_bound_exceeded",
                message: "the query's candidate or include window exceeded its bound".into(),
                fix: "narrow the filter, reduce the page, or bound the include depth".into(),
                placement: None,
            },
            QueryError::InvalidSpec => Self {
                status: StatusCode::UNPROCESSABLE_ENTITY,
                code: "invalid_tql_spec",
                message: "the compiled specification is not executable as written".into(),
                fix: "rewrite the query within the surface docs/tql/examples.md documents".into(),
                placement: None,
            },
            QueryError::EngineClosed | QueryError::StorageFailure => Self {
                status: StatusCode::SERVICE_UNAVAILABLE,
                code: "storage_failure",
                message: "the engine could not serve the query".into(),
                fix: "check the member's health, then retry once".into(),
                placement: None,
            },
            QueryError::TenantMismatch
            | QueryError::SnapshotEngineMismatch
            | QueryError::SnapshotTenantMismatch => Self {
                status: StatusCode::INTERNAL_SERVER_ERROR,
                code: "query_binding_failure",
                message: "the query's tenant or snapshot binding did not line up".into(),
                fix: "this is a server-side wiring fault; retry once, then report it".into(),
                placement: None,
            },
            QueryError::Cancelled => Self {
                status: StatusCode::INTERNAL_SERVER_ERROR,
                code: "query_cancelled",
                message: "the query was cancelled before it finished".into(),
                fix: "retry the query".into(),
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

    /// A designed-but-not-yet-built surface fails closed with the issue that
    /// replaces it, never with a half-built code path (GOAL's stub rule).
    #[must_use]
    pub fn pending_surface(surface: &str, issue: &str) -> Self {
        Self {
            status: StatusCode::NOT_IMPLEMENTED,
            code: "surface_pending",
            message: format!("{surface} is not implemented on this member yet"),
            fix: format!("tracked by {issue}; nothing here silently degrades"),
            placement: None,
        }
    }

    /// A backup/restore refusal (S-06): the Litestream side failed or was
    /// never attached; nothing is silently degraded (D-29).
    #[must_use]
    pub fn backup(reason: impl std::fmt::Display) -> Self {
        Self {
            status: StatusCode::INTERNAL_SERVER_ERROR,
            code: "backup_failure",
            message: format!("the backup surface refused: {reason}"),
            fix: "check the member's litestream.log under the database's replica directory".into(),
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
pub(crate) fn push_json_escaped(out: &mut String, value: &str) {
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
    /// The reactive transports' live subscriptions (`S-04`).
    pub reactive: Arc<crate::reactive_api::ReactiveSessions>,
    /// The Litestream fleet (`S-06`): one stream per hosted database.
    pub backups: Arc<crate::backup::BackupStreams>,
    /// Where the fleet writes replicas: `<data_root>/backup` on the local
    /// stack (`S-06`).
    pub backup_root: std::path::PathBuf,
    /// The regions this deployment believes active: `GlobalDurable`
    /// fails closed under two (D-37).
    pub active_regions: usize,
}

/// Builds the public `/api/v1` router. Every route except
/// [`PUBLIC_PATHS`] requires an admitted credential.
#[must_use = "the returned router must be installed on a server"]
pub fn router(state: Arc<ApiState>) -> Router {
    let (router, openapi_document) = OpenApiRouter::new()
        .routes(routes!(health))
        .routes(routes!(regions))
        .routes(routes!(list_databases, create_database))
        .routes(routes!(get_database, delete_database))
        .routes(routes!(restore_database))
        .routes(routes!(list_collections))
        .routes(routes!(list_indexes, create_index))
        .routes(routes!(index_recommendations))
        .routes(routes!(delete_index))
        .routes(routes!(get_schema, put_schema))
        .routes(routes!(list_documents))
        .routes(routes!(batch_documents))
        .routes(routes!(get_document, put_document, patch_document, delete_document))
        .routes(routes!(document_history))
        .routes(routes!(execute_tql))
        .routes(routes!(crate::reactive_api::poll))
        .routes(routes!(crate::reactive_api::websocket))
        .routes(routes!(database_usage))
        .routes(routes!(list_webhooks, create_webhook))
        .routes(routes!(delete_webhook))
        .routes(routes!(list_backups))
        .routes(routes!(restore_backup))
        .routes(routes!(list_branches, create_branch))
        .split_for_parts();
    let openapi_json = Arc::new(openapi_document.to_json().expect("OpenAPI must serialize"));
    router
        .route("/api/v1/openapi.json", get(openapi))
        .merge(Scalar::with_url("/scalar", openapi_document))
        .fallback(not_routed)
        .layer(Extension(openapi_json))
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
#[utoipa::path(get, path = "/api/v1/health", responses((status = 200, description = "Member health")))]
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
#[utoipa::path(get, path = "/api/v1/regions", responses((status = 200, description = "Serving region and health")))]
async fn regions(State(state): State<Arc<ApiState>>) -> Response {
    let body = format!("{{\"region\":\"{}\",\"healthy\":true}}", state.region);
    let mut response = body.into_response();
    response.headers_mut().insert(CONTENT_TYPE, HeaderValue::from_static("application/json"));
    if let Ok(region) = HeaderValue::from_str(state.region.as_str()) {
        response.headers_mut().insert(REGION_HEADER, region);
    }
    response
}

/// Serves the OpenAPI contract generated while registering the routes.
async fn openapi(Extension(body): Extension<Arc<String>>) -> Response {
    let mut response = body.as_str().to_owned().into_response();
    response.headers_mut().insert(CONTENT_TYPE, HeaderValue::from_static("application/json"));
    response
}

/// The API prefix's unrouted fallback: the typed envelope, never a bare 404.
async fn not_routed(request: Request) -> Response {
    ApiError::not_routed(request.uri().path()).into_response()
}

/// Lists every database record, deleted records included (each record's
/// body names its state).
#[utoipa::path(get, path = "/api/v1/databases", responses((status = 200, description = "Database records")))]
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
#[utoipa::path(post, path = "/api/v1/databases", responses((status = 201, description = "Database created")))]
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
        Ok(record) => {
            // The backup stream attaches at creation (S-06): open the
            // engine location so the database file exists, then start one
            // stream child for it from its first moment.
            if let Some(catalog) = state.catalog.clone() {
                let _ = catalog.host_for(&state.scope.0, &state.scope.1, name);
                let location = catalog.location_for(&state.scope.0, &state.scope.1, name);
                let _ = state.backups.attach(name, &location, &state.backup_root, "24h");
            }
            record_response(StatusCode::CREATED, &record)
        }
        Err(error) => ApiError::control(error).into_response(),
    }
}

/// Returns one database's record with the routing headers; a database this
/// member's region does not host answers the 421 contract (D-58).
#[utoipa::path(get, path = "/api/v1/databases/{name}", responses((status = 200, description = "Database record")))]
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

/// Soft-deletes one database: restorable for seven days. The backup
/// stream stops with it; retention owns the artifacts (S-06).
#[utoipa::path(delete, path = "/api/v1/databases/{name}", responses((status = 200, description = "Database soft-deleted")))]
async fn delete_database(State(state): State<Arc<ApiState>>, Path(name): Path<String>) -> Response {
    let Some(control) = state.control.clone() else {
        return ApiError::control_unavailable().into_response();
    };
    match control.soft_delete(&name) {
        Ok(record) => {
            state.backups.detach(&name);
            record_response(StatusCode::OK, &record)
        }
        Err(error) => ApiError::control(error).into_response(),
    }
}

/// Restores one soft-deleted database inside its window.
#[utoipa::path(post, path = "/api/v1/databases/{name}/restore", responses((status = 200, description = "Database restored")))]
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

/// Lists the distinct collections a database's documents occupy.
#[utoipa::path(get, path = "/api/v1/databases/{name}/collections", responses((status = 200, description = "Collections")))]
async fn list_collections(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
) -> Response {
    let Ok(host) = document_host(&state, &name) else {
        return ApiError::database_not_found(&name).into_response();
    };
    let engine = host.engine();
    let Ok(scope) = document_scope(engine) else {
        return ApiError::control_unavailable().into_response();
    };
    let Ok(snapshot) = engine.read_snapshot(&scope) else {
        return ApiError::control_unavailable().into_response();
    };
    match engine.collections_at(&scope, &snapshot) {
        Ok(collections) => {
            let mut body = String::from("{\"collections\":[");
            for (index, collection) in collections.iter().enumerate() {
                if index > 0 {
                    body.push(',');
                }
                body.push('"');
                push_json_escaped(&mut body, collection.as_str());
                body.push('"');
            }
            body.push_str("]}");
            json_response(StatusCode::OK, body)
        }
        Err(_) => ApiError::control_unavailable().into_response(),
    }
}

/// Lists one collection's index descriptors: field, state, and the rows
/// the governed backfill still owes (D-31/D-42).
#[utoipa::path(get, path = "/api/v1/databases/{name}/collections/{collection}/indexes", responses((status = 200, description = "Indexes")))]
async fn list_indexes(
    State(state): State<Arc<ApiState>>,
    Path((name, collection)): Path<(String, String)>,
) -> Response {
    let Ok(host) = document_host(&state, &name) else {
        return ApiError::database_not_found(&name).into_response();
    };
    let engine = host.engine();
    let Ok(collection) = CollectionName::try_new(&collection) else {
        return invalid_identifier("collection name", &collection).into_response();
    };
    match engine.index_descriptors() {
        Ok(descriptors) => {
            let mut body = String::from("{\"indexes\":[");
            let mut first = true;
            for descriptor in
                descriptors.iter().filter(|descriptor| descriptor.collection == collection.as_str())
            {
                if !first {
                    body.push(',');
                }
                first = false;
                body.push_str("{\"field\":\"");
                push_json_escaped(&mut body, &descriptor.field);
                body.push_str("\",\"state\":\"");
                body.push_str(if descriptor.active { "active" } else { "building" });
                body.push_str("\",\"backfill_remaining\":");
                body.push_str(&descriptor.backfill_remaining.to_string());
                body.push('}');
            }
            body.push_str("]}");
            json_response(StatusCode::OK, body)
        }
        Err(_) => ApiError::control_unavailable().into_response(),
    }
}

/// Registers one extracted index column on a collection (D-31): the field
/// gains stored columns written by the Rust write path, existing rows are
/// backfilled inline inside the cost cap, and ordinary indexes cover the
/// columns. Idempotent for an identical descriptor.
#[utoipa::path(post, path = "/api/v1/databases/{name}/collections/{collection}/indexes", responses((status = 201, description = "Index created")))]
async fn create_index(
    State(state): State<Arc<ApiState>>,
    Path((name, collection)): Path<(String, String)>,
    body: String,
) -> Response {
    let Ok(host) = document_host(&state, &name) else {
        return ApiError::database_not_found(&name).into_response();
    };
    let engine = host.engine();
    let Ok(collection) = CollectionName::try_new(&collection) else {
        return invalid_identifier("collection name", &collection).into_response();
    };
    let request: serde_json::Value = match serde_json::from_str(&body) {
        Ok(value) => value,
        Err(_) => {
            return ApiError {
                status: StatusCode::BAD_REQUEST,
                code: "invalid_json",
                message: "the index body is not JSON".into(),
                fix: "send {\"field\":\"<name>\"}".into(),
                placement: None,
            }
            .into_response();
        }
    };
    let Some(field) = request.get("field").and_then(serde_json::Value::as_str) else {
        return ApiError {
            status: StatusCode::BAD_REQUEST,
            code: "missing_field",
            message: "the index request names no field".into(),
            fix: "add \"field\": \"<name>\" to the request body".into(),
            placement: None,
        }
        .into_response();
    };
    match engine.register_index_field(&collection, field) {
        Ok(registration) => {
            let body = format!(
                "{{\"field\":\"{}\",\"state\":\"{}\"}}",
                field,
                match registration {
                    terrabase::engine::DescriptorRegistration::Active
                    | terrabase::engine::DescriptorRegistration::Unchanged => "active",
                    terrabase::engine::DescriptorRegistration::Building => "building",
                }
            );
            json_response(StatusCode::CREATED, body)
        }
        Err(terrabase::engine::IndexRegistrationError::BudgetExceeded) => ApiError {
            status: StatusCode::UNPROCESSABLE_ENTITY,
            code: "index_budget_exceeded",
            message: "the database's extracted-column budget is full".into(),
            fix:
                "retire an unused index, or split the database (D-42 caps stored columns per file)"
                    .into(),
            placement: None,
        }
        .into_response(),
        Err(_) => ApiError::control_unavailable().into_response(),
    }
}

/// Index retirement (drop) fails closed until the D-31 lifecycle (usage
/// tracking, merge, retire) lands with the auto-index audit.
#[utoipa::path(delete, path = "/api/v1/databases/{name}/collections/{collection}/indexes/{field}", responses((status = 200, description = "Index retired")))]
async fn delete_index(
    State(state): State<Arc<ApiState>>,
    Path((name, collection, field)): Path<(String, String, String)>,
) -> Response {
    let Ok(_host) = document_host(&state, &name) else {
        return ApiError::database_not_found(&name).into_response();
    };
    ApiError::pending_surface(
        &format!("dropping the index on {collection}.{field}"),
        "the D-31 index lifecycle (#127 slice C)",
    )
    .into_response()
}

/// Index recommendations read the auto-index audit, which the S-02 lane
/// has not landed; the route fails closed rather than inventing advice.
#[utoipa::path(get, path = "/api/v1/databases/{name}/collections/{collection}/indexes/recommendations", responses((status = 200, description = "Index recommendations")))]
async fn index_recommendations(
    State(state): State<Arc<ApiState>>,
    Path((name, collection)): Path<(String, String)>,
) -> Response {
    let Ok(_host) = document_host(&state, &name) else {
        return ApiError::database_not_found(&name).into_response();
    };
    ApiError::pending_surface(
        &format!("index recommendations for {collection}"),
        "the auto-index audit (#127 slice C)",
    )
    .into_response()
}

/// Collection schemas are authored in TypeScript and applied by
/// `terrascale push` (D-48); the route fails closed until that lane lands.
#[utoipa::path(get, path = "/api/v1/databases/{name}/collections/{collection}/schema", responses((status = 200, description = "Collection schema")))]
async fn get_schema(
    State(state): State<Arc<ApiState>>,
    Path((name, collection)): Path<(String, String)>,
) -> Response {
    let Ok(_host) = document_host(&state, &name) else {
        return ApiError::database_not_found(&name).into_response();
    };
    ApiError::pending_surface(
        &format!("the {collection} collection schema"),
        "terrascale push / schema-first SDK (S-09/S-10)",
    )
    .into_response()
}

/// See [`get_schema`]: schemas arrive with `terrascale push` (D-48).
#[utoipa::path(put, path = "/api/v1/databases/{name}/collections/{collection}/schema", responses((status = 200, description = "Collection schema updated")))]
async fn put_schema(
    State(state): State<Arc<ApiState>>,
    Path((name, collection)): Path<(String, String)>,
) -> Response {
    let Ok(_host) = document_host(&state, &name) else {
        return ApiError::database_not_found(&name).into_response();
    };
    ApiError::pending_surface(
        &format!("the {collection} collection schema"),
        "terrascale push / schema-first SDK (S-09/S-10)",
    )
    .into_response()
}

/// Lists one collection's documents in canonical key order, with bounded
/// paging (`?limit=`, `?after=` by identifier).
#[utoipa::path(get, path = "/api/v1/databases/{name}/collections/{collection}/documents", responses((status = 200, description = "Documents")))]
async fn list_documents(
    State(state): State<Arc<ApiState>>,
    Path((name, collection)): Path<(String, String)>,
    extract::Query(paging): extract::Query<DocumentPaging>,
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
    let Ok(snapshot) = engine.read_snapshot(&scope) else {
        return ApiError::control_unavailable().into_response();
    };
    let limit = paging.limit.unwrap_or(100).clamp(1, 1000);
    let documents =
        engine.snapshot_documents_in_collection(&scope, &snapshot, &collection).unwrap_or_default();
    let mut body = String::from("{\"documents\":[");
    let paged: Vec<_> = documents
        .iter()
        .skip_while(|document| {
            paging.after.as_deref().is_some_and(|after| document.id().as_str() <= after)
        })
        .take(limit)
        .collect();
    for (count, document) in paged.iter().enumerate() {
        if count > 0 {
            body.push(',');
        }
        body.push_str("{\"id\":\"");
        push_json_escaped(&mut body, document.id().as_str());
        // ponytail: the version renders as the metadata version; the
        // full seq-qualified revision rides the per-document GET's ETag.
        body.push_str("\",\"version\":");
        body.push_str(&document.metadata().version().to_string());
        body.push_str(",\"value\":");
        push_value(&mut body, document.value());
        body.push('}');
    }
    body.push_str("]}");
    json_response(StatusCode::OK, body)
}

/// The documents list's paging parameters.
#[derive(serde::Deserialize)]
struct DocumentPaging {
    limit: Option<usize>,
    after: Option<String>,
}

/// The request's durability class (D-34): `regional` acknowledges the
/// local write, `global` waits for the replicated sync point. The header
/// is optional; `regional` is the default.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum Durability {
    /// The local region's acknowledgement (`Written`).
    Regional,
    /// The replicated acknowledgement (`Synced`).
    Global,
}

impl Durability {
    /// Parses the `X-TerraScale-Durability` header, defaulting to
    /// `regional`.
    fn of_header(headers: &axum::http::HeaderMap) -> Result<Self, ApiError> {
        match headers.get("x-terrascale-durability") {
            None => Ok(Self::Regional),
            Some(value) => match value.to_str().ok() {
                Some("regional") => Ok(Self::Regional),
                Some("global") => Ok(Self::Global),
                _ => Err(ApiError {
                    status: StatusCode::BAD_REQUEST,
                    code: "invalid_durability",
                    message: "X-TerraScale-Durability must be regional or global".into(),
                    fix: "send X-TerraScale-Durability: regional or global, or omit it".into(),
                    placement: None,
                }),
            },
        }
    }

    fn level(self) -> LocalDurability {
        match self {
            Self::Regional => LocalDurability::Written,
            Self::Global => LocalDurability::Synced,
        }
    }

    /// The typed refusal for a global write under two active regions
    /// (D-37): the class fails closed, never degrades to regional.
    fn requires_active_regions(self, active: usize) -> Result<(), ApiError> {
        if matches!(self, Self::Global) && active < 2 {
            return Err(ApiError {
                status: StatusCode::SERVICE_UNAVAILABLE,
                code: "global_durability_unavailable",
                message: "GlobalDurable needs at least two active regions".into(),
                fix: "retry as regional, or add a region to the member's active set                       (public_api.regions in the member config)"
                    .into(),
                placement: None,
            });
        }
        Ok(())
    }
}

/// The write request's operation identity. An idempotency key names the
/// operation outright (the engine binds an operation identity durably to
/// its write set, so a retried key replays the original receipt and a
/// reused key with a different body is refused); without one, each
/// request mints a fresh identity.
fn has_idempotency_key(headers: &axum::http::HeaderMap) -> bool {
    headers.get("idempotency-key").is_some()
}

fn write_operation(headers: &axum::http::HeaderMap, kind: &str) -> Result<OperationId, ApiError> {
    let identity = match headers.get("idempotency-key").and_then(|value| value.to_str().ok()) {
        Some(key) => format!("{kind}-idem-{key}"),
        None => format!("{kind}-{}", unix_millis_nanos()),
    };
    OperationId::try_new(identity.as_str()).map_err(|_| ApiError {
        status: StatusCode::BAD_REQUEST,
        code: "invalid_idempotency_key",
        message: "the idempotency key is not a valid operation identity".into(),
        fix: "send a short printable key; retries must repeat it exactly".into(),
        placement: None,
    })
}

/// RFC 7386 JSON merge patch over the engine's value model: objects
/// merge per field, `null` deletes a field, anything else replaces.
fn merge_patch(base: &mut DocumentValue, patch: &DocumentValue) {
    match (base, patch) {
        (DocumentValue::Object(fields), DocumentValue::Object(patch_fields)) => {
            for (name, item) in patch_fields {
                if matches!(item, DocumentValue::Null) {
                    fields.remove(name);
                } else {
                    merge_patch(fields.entry(name.clone()).or_insert(DocumentValue::Null), item);
                }
            }
        }
        (slot, patch) => *slot = patch.clone(),
    }
}

/// One atomic batch of document mutations (create/replace, merge patch,
/// delete) in a single engine transaction: every op commits together or
/// the typed refusal names the offending op and nothing lands.
#[utoipa::path(post, path = "/api/v1/databases/{name}/collections/{collection}/documents/batch", responses((status = 200, description = "Batch result")))]
async fn batch_documents(
    State(state): State<Arc<ApiState>>,
    Path((name, collection)): Path<(String, String)>,
    headers: axum::http::HeaderMap,
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
    let request: serde_json::Value = match serde_json::from_str(&body) {
        Ok(value) => value,
        Err(_) => {
            return ApiError {
                status: StatusCode::BAD_REQUEST,
                code: "invalid_json",
                message: "the batch body is not JSON".into(),
                fix: "send an object with an operations array of put/patch/delete ops".into(),
                placement: None,
            }
            .into_response();
        }
    };
    let Some(operations) = request.get("operations").and_then(serde_json::Value::as_array) else {
        return ApiError {
            status: StatusCode::BAD_REQUEST,
            code: "missing_operations",
            message: "the batch names no operations".into(),
            fix: "send an operations array with at least one op".into(),
            placement: None,
        }
        .into_response();
    };
    if operations.is_empty() || operations.len() > 1000 {
        return ApiError {
            status: StatusCode::UNPROCESSABLE_ENTITY,
            code: "batch_size_out_of_bounds",
            message: "a batch is 1..=1000 operations".into(),
            fix: "split the batch; a batch commits atomically so smaller is safer".into(),
            placement: None,
        }
        .into_response();
    }
    let durability = match Durability::of_header(&headers) {
        Ok(durability) => {
            if let Err(error) = durability.requires_active_regions(state.active_regions) {
                return error.into_response();
            }
            durability.level()
        }
        Err(error) => return error.into_response(),
    };
    let operation = match write_operation(&headers, "api-batch") {
        Ok(operation) => operation,
        Err(error) => return error.into_response(),
    };
    let Ok(snapshot) = engine.read_snapshot(&scope) else {
        return ApiError::control_unavailable().into_response();
    };
    let Ok(mut transaction) = engine.begin(&scope, snapshot, operation) else {
        return ApiError::control_unavailable().into_response();
    };
    for (index, item) in operations.iter().enumerate() {
        if let Err((code, message)) = stage_batch_operation(&mut transaction, &collection, item) {
            return batch_refusal(index, code, message);
        }
    }
    match transaction.commit(durability) {
        CommitOutcome::Committed(receipt) => {
            let mut response = json_response(
                StatusCode::OK,
                format!(
                    "{{\"committed\":{},\"count\":{}}}",
                    receipt.sequence().get(),
                    operations.len()
                ),
            );
            with_session(&mut response, receipt.sequence().get());
            response
        }
        CommitOutcome::Rejected(reason) => ApiError {
            status: StatusCode::CONFLICT,
            code: "commit_rejected",
            message: format!("the engine refused the batch: {reason:?}"),
            fix: "a rejected batch stages nothing; fix the named op and resend".into(),
            placement: None,
        }
        .into_response(),
        CommitOutcome::Unknown(reason) => ApiError {
            status: StatusCode::INTERNAL_SERVER_ERROR,
            code: "commit_unknown",
            message: format!("the batch's outcome is unknown: {reason:?}"),
            fix: "re-read to see whether it landed, then resend with a fresh key".into(),
            placement: None,
        }
        .into_response(),
    }
}

/// Stages one batch operation onto the transaction. `Err` carries the
/// refusal's code and message; the caller renders it against the op's
/// index and nothing the batch staged lands.
fn stage_batch_operation(
    transaction: &mut terrabase::Transaction<'_>,
    collection: &CollectionName,
    item: &serde_json::Value,
) -> Result<(), (&'static str, &'static str)> {
    let id = item.get("id").and_then(serde_json::Value::as_str);
    let Some(id) = id else {
        return Err(("missing_id", "the op names no document"));
    };
    let Ok(id) = DocumentId::try_new(id) else {
        return Err(("invalid_identifier", "the op's id is not valid"));
    };
    match item.get("op").and_then(serde_json::Value::as_str) {
        Some("put") => {
            let Some(value) = item.get("value") else {
                return Err(("missing_value", "the put names no value"));
            };
            let document = Document::new(id, collection.clone(), json_value(value));
            if transaction.put(document, ExpectedVersion::Any).is_err() {
                return Err(("staging_refused", "the put could not stage"));
            }
        }
        Some("patch") => {
            let existing = transaction.read_in_collection(collection, &id);
            let Ok(Some(existing)) = existing else {
                return Err(("document_not_found", "the patch target is absent"));
            };
            let mut merged = existing.value().clone();
            merge_patch(
                &mut merged,
                &json_value(item.get("value").unwrap_or(&serde_json::Value::Null)),
            );
            let patched = Document::new(id, collection.clone(), merged);
            if transaction.put(patched, ExpectedVersion::Any).is_err() {
                return Err(("staging_refused", "the patch could not stage"));
            }
        }
        Some("delete") => {
            if transaction.read_in_collection(collection, &id).ok().flatten().is_none() {
                return Err(("document_not_found", "the delete target is absent"));
            }
            transaction.delete_in_collection(collection, id, ExpectedVersion::Any);
        }
        _ => return Err(("unknown_op", "op must be put, patch, or delete")),
    }
    Ok(())
}

/// One batch operation's typed refusal: the index names the offender.
fn batch_refusal(index: usize, code: &str, message: &str) -> Response {
    ApiError {
        status: StatusCode::UNPROCESSABLE_ENTITY,
        code: "batch_operation_refused",
        message: format!("operations[{index}]: {message}"),
        fix: format!("fix operations[{index}] ({code}); a refused batch lands nothing"),
        placement: None,
    }
    .into_response()
}

/// Attaches the read-your-writes session token (`S-03`: the write's
/// storage sequence; the SDK echoes it back, and a member's snapshots are
/// monotonic, so the token is a routing hint for the proxy and replica
/// lanes — `S-05` — not a local gate).
fn with_session(response: &mut Response, sequence: u64) {
    if let Ok(token) = HeaderValue::from_str(&sequence.to_string()) {
        response.headers_mut().insert("x-terrascale-session", token);
    }
}

/// Waits a bounded window for the engine's published sequence to cover
/// the client's session sequence (D-37's read-your-writes across
/// regions — the receiving region's replicated applies advance the
/// journal). `Ok` serves; the typed timeout names its remedy.
async fn await_session_sequence(
    engine: &terrabase::Engine,
    scope: &terrabase::types::TenantScope,
    session: u64,
) -> Result<(), ApiError> {
    const WINDOW: std::time::Duration = std::time::Duration::from_secs(2);
    const TICK: std::time::Duration = std::time::Duration::from_millis(5);
    let started = std::time::Instant::now();
    loop {
        let covered =
            engine.read_snapshot(scope).is_ok_and(|snapshot| snapshot.sequence().get() >= session);
        if covered {
            return Ok(());
        }
        if started.elapsed() >= WINDOW {
            return Err(ApiError {
                status: StatusCode::CONFLICT,
                code: "session_not_covered",
                message: format!(
                    "the session's sequence ({session}) is not covered here yet"
                ),
                fix: "retry shortly (replication is catching up), or read without                       the session token for this-region state"
                    .into(),
                placement: None,
            });
        }
        tokio::time::sleep(TICK).await;
    }
}

/// One document's route context: the host engine for the named database.
pub(crate) fn document_host(
    state: &ApiState,
    name: &str,
) -> Result<std::sync::Arc<terrabase_tbep::member_host::MemberHost>, ApiError> {
    let Some(catalog) = state.catalog.clone() else {
        return Err(ApiError::control_unavailable());
    };
    catalog.host_for(&state.scope.0, &state.scope.1, name).map_err(|error| {
        eprintln!("document_host: host_for refused {name}: {:?}", error);
        ApiError::database_not_found(name)
    })
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
#[utoipa::path(get, path = "/api/v1/databases/{name}/collections/{collection}/documents/{id}", responses((status = 200, description = "Document")))]
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
    // Read-your-writes across regions (D-37): a session token names the
    // sequence the client has seen; the read waits a bounded window for
    // this member's journal to cover it (a replicated apply advances it)
    // before serving.
    if let Some(session) = headers
        .get("x-terrascale-session")
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.parse::<u64>().ok())
        && let Err(error) = await_session_sequence(engine, &scope, session).await
    {
        return error.into_response();
    }
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
#[utoipa::path(put, path = "/api/v1/databases/{name}/collections/{collection}/documents/{id}", responses((status = 200, description = "Document stored")))]
async fn put_document(
    State(state): State<Arc<ApiState>>,
    Path((name, collection, id)): Path<(String, String, String)>,
    headers: axum::http::HeaderMap,
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
    let operation = match write_operation(&headers, "api-put") {
        Ok(operation) => operation,
        Err(error) => return error.into_response(),
    };
    let durability = match Durability::of_header(&headers) {
        Ok(durability) => {
            if let Err(error) = durability.requires_active_regions(state.active_regions) {
                return error.into_response();
            }
            durability.level()
        }
        Err(error) => return error.into_response(),
    };
    let Ok(mut transaction) = engine.begin(&scope, snapshot, operation) else {
        return ApiError::control_unavailable().into_response();
    };
    // With an idempotency key the logical request is the body: the expected
    // clause is Any so a retry replays the original receipt (the engine's
    // digest binds the op id to the write set). Without a key, optimistic
    // concurrency: the current version must still hold.
    let expected = if has_idempotency_key(&headers) {
        ExpectedVersion::Any
    } else {
        match transaction.read_in_collection(document.collection(), document.id()) {
            Ok(Some(existing)) => ExpectedVersion::Exact(existing.metadata().version()),
            Ok(None) => ExpectedVersion::New,
            Err(_) => return ApiError::control_unavailable().into_response(),
        }
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
    match transaction.commit(durability) {
        CommitOutcome::Committed(receipt) => {
            let revision = format!("{}-1", receipt.sequence().get());
            let mut response =
                json_response(StatusCode::OK, format!("{{\"revision\":\"{revision}\"}}"));
            if let Ok(etag) = HeaderValue::from_str(&format!("\"{revision}\"")) {
                response.headers_mut().insert(axum::http::header::ETAG, etag);
            }
            with_session(&mut response, receipt.sequence().get());
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

/// RFC 7386 merge patch over one document: objects merge per field,
/// `null` deletes a field, anything else replaces the whole value. The
/// read-modify-write is one engine transaction against the snapshot.
#[utoipa::path(patch, path = "/api/v1/databases/{name}/collections/{collection}/documents/{id}", responses((status = 200, description = "Document patched")))]
async fn patch_document(
    State(state): State<Arc<ApiState>>,
    Path((name, collection, id)): Path<(String, String, String)>,
    headers: axum::http::HeaderMap,
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
                message: "the request body is not a JSON merge patch".into(),
                fix: "send an object whose fields merge into the document".into(),
                placement: None,
            }
            .into_response();
        }
    };
    let operation = match write_operation(&headers, "api-patch") {
        Ok(operation) => operation,
        Err(error) => return error.into_response(),
    };
    let durability = match Durability::of_header(&headers) {
        Ok(durability) => {
            if let Err(error) = durability.requires_active_regions(state.active_regions) {
                return error.into_response();
            }
            durability.level()
        }
        Err(error) => return error.into_response(),
    };
    let Ok(snapshot) = engine.read_snapshot(&scope) else {
        return ApiError::control_unavailable().into_response();
    };
    let Ok(mut transaction) = engine.begin(&scope, snapshot, operation) else {
        return ApiError::control_unavailable().into_response();
    };
    let existing = match transaction.read_in_collection(&collection, &id) {
        Ok(Some(existing)) => existing,
        Ok(None) => {
            return document_not_found(collection.as_str(), id.as_str()).into_response();
        }
        Err(_) => return ApiError::control_unavailable().into_response(),
    };
    let mut merged = existing.value().clone();
    merge_patch(&mut merged, &json_value(&value));
    let patched = Document::new(id, collection, merged);
    // The merge base is snapshot state, not the logical request: with an
    // idempotency key the expected clause is Any so an unchanged-state
    // retry replays; without one the base version must hold.
    let expected = if has_idempotency_key(&headers) {
        ExpectedVersion::Any
    } else {
        ExpectedVersion::Exact(existing.metadata().version())
    };
    if let Err(reason) = transaction.put(patched, expected) {
        return ApiError {
            status: StatusCode::CONFLICT,
            code: "version_conflict",
            message: format!("the document changed under the request: {reason:?}"),
            fix: "re-read the document, merge, and retry with the new version".into(),
            placement: None,
        }
        .into_response();
    }
    match transaction.commit(durability) {
        CommitOutcome::Committed(receipt) => {
            let revision = format!("{}-1", receipt.sequence().get());
            let mut response =
                json_response(StatusCode::OK, format!("{{\"revision\":\"{revision}\"}}"));
            if let Ok(etag) = HeaderValue::from_str(&format!("\"{revision}\"")) {
                response.headers_mut().insert(axum::http::header::ETAG, etag);
            }
            with_session(&mut response, receipt.sequence().get());
            response
        }
        _ => ApiError {
            status: StatusCode::INTERNAL_SERVER_ERROR,
            code: "commit_failed",
            message: "the engine did not commit the patch".into(),
            fix: "re-read to see whether it landed, then retry with a fresh key".into(),
            placement: None,
        }
        .into_response(),
    }
}

/// One document's retained revision history, newest first (D-42: history
/// is journal retention). Conflict explanations — "why this version won" —
/// ride the S-05 replication lane; a single-region document has no
/// conflicts to explain yet.
#[utoipa::path(get, path = "/api/v1/databases/{name}/collections/{collection}/documents/{id}/history", responses((status = 200, description = "Document history")))]
async fn document_history(
    State(state): State<Arc<ApiState>>,
    Path((name, collection, id)): Path<(String, String, String)>,
    extract::Query(paging): extract::Query<DocumentPaging>,
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
    let limit = paging.limit.unwrap_or(100).clamp(1, 1000);
    let limit = u32::try_from(limit).unwrap_or(100);
    match engine.document_history_in_collection(&scope, &collection, &id, limit) {
        Ok(entries) => {
            let mut body = String::from("{\"history\":[");
            for (index, entry) in entries.iter().enumerate() {
                if index > 0 {
                    body.push(',');
                }
                body.push_str("{\"revision\":\"");
                push_json_escaped(&mut body, &entry.revision);
                body.push_str("\",\"deleted\":");
                body.push_str(if entry.deleted { "true" } else { "false" });
                body.push('}');
            }
            body.push_str("]}");
            json_response(StatusCode::OK, body)
        }
        Err(_) => ApiError::control_unavailable().into_response(),
    }
}

/// Deletes one document (tombstone; the history route can explain it).
#[utoipa::path(delete, path = "/api/v1/databases/{name}/collections/{collection}/documents/{id}", responses((status = 200, description = "Document deleted")))]
async fn delete_document(
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
    let operation = match write_operation(&headers, "api-delete") {
        Ok(operation) => operation,
        Err(error) => return error.into_response(),
    };
    let durability = match Durability::of_header(&headers) {
        Ok(durability) => {
            if let Err(error) = durability.requires_active_regions(state.active_regions) {
                return error.into_response();
            }
            durability.level()
        }
        Err(error) => return error.into_response(),
    };
    let Ok(mut transaction) = engine.begin(&scope, snapshot, operation) else {
        return ApiError::control_unavailable().into_response();
    };
    let expected = if has_idempotency_key(&headers) {
        ExpectedVersion::Any
    } else {
        match transaction.read_in_collection(&collection, &id) {
            Ok(Some(existing)) => ExpectedVersion::Exact(existing.metadata().version()),
            Ok(None) => {
                return document_not_found(collection.as_str(), id.as_str()).into_response();
            }
            Err(_) => return ApiError::control_unavailable().into_response(),
        }
    };
    // delete_in_collection stages without a pre-read (the caller owns
    // existence, and this route read the target above); the commit's
    // conflict rejection is the version check.
    transaction.delete_in_collection(&collection, id, expected);
    match transaction.commit(durability) {
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

/// Executes one TQL text against a database: the public query surface
/// (S-03) over the S-02 parser and executor. The body carries the text,
/// its declared grammar version (default `tql/1.0`), optional out-of-band
/// parameters, and the `explain` request option (the plan, not the rows).
#[utoipa::path(post, path = "/api/v1/databases/{name}/tql", responses((status = 200, description = "TQL result")))]
async fn execute_tql(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
    body: String,
) -> Response {
    let Ok(host) = document_host(&state, &name) else {
        return ApiError::database_not_found(&name).into_response();
    };
    let engine = host.engine();
    let Ok(scope) = document_scope(engine) else {
        return ApiError::control_unavailable().into_response();
    };
    let Ok(tenant) = local_tenant() else {
        return ApiError::control_unavailable().into_response();
    };
    let request: serde_json::Value = match serde_json::from_str(&body) {
        Ok(value) => value,
        Err(_) => {
            return ApiError {
                status: StatusCode::BAD_REQUEST,
                code: "invalid_json",
                message: "the tql body is not JSON".into(),
                fix: "send {\"tql\":\"from c where …\",\"parameters\":{…},\"explain\":false}"
                    .into(),
                placement: None,
            }
            .into_response();
        }
    };
    let Some(text) = request.get("tql").and_then(serde_json::Value::as_str) else {
        return ApiError {
            status: StatusCode::BAD_REQUEST,
            code: "missing_tql",
            message: "the request names no TQL text".into(),
            fix: "add \"tql\": \"from <collection> where …\" to the request body".into(),
            placement: None,
        }
        .into_response();
    };
    let grammar = request.get("grammar").and_then(serde_json::Value::as_str).unwrap_or("tql/1.0");
    let mut parameters = terrabase::query::tql::TqlParameters::new();
    if let Some(fields) = request.get("parameters").and_then(serde_json::Value::as_object) {
        for (parameter, value) in fields {
            if let Err(error) = parameters.insert(parameter, json_value(value)) {
                return ApiError::tql(&error).into_response();
            }
        }
    }
    let query = match terrabase::query::tql::parse_artifact(
        Some(grammar),
        text,
        &parameters,
        &terrabase::query::tql::TqlCapabilities::none(),
    ) {
        Ok(query) => query,
        Err(error) => return ApiError::tql(&error).into_response(),
    };
    let store = engine.queries();
    // ponytail: a fixed 30 s bound; a per-request deadline option rides the
    // reactive transport (S-04), which is the only long-running surface.
    let Ok(deadline) = terrabase::Deadline::after(std::time::Duration::from_secs(30)) else {
        return ApiError::control_unavailable().into_response();
    };
    let plan = match terrabase::query::tql::compile(
        &store,
        &query,
        &parameters,
        tenant,
        deadline,
        terrabase::Cancellation::new(),
    ) {
        Ok(plan) => plan,
        Err(error) => return ApiError::tql(&error).into_response(),
    };
    if request.get("explain").and_then(serde_json::Value::as_bool) == Some(true) {
        return json_response(StatusCode::OK, render_explained_plan(grammar, &plan));
    }
    let Ok(snapshot) = engine.read_snapshot(&scope) else {
        return ApiError::control_unavailable().into_response();
    };
    match store.execute(&plan, &snapshot) {
        Ok(result) => {
            let mut rendered = String::from("{\"matched\":");
            rendered.push_str(&result.matched().to_string());
            rendered.push_str(",\"rows\":[");
            for (index, row) in result.rows().iter().enumerate() {
                if index > 0 {
                    rendered.push(',');
                }
                rendered.push_str("{\"id\":\"");
                push_json_escaped(&mut rendered, row.document().id().as_str());
                rendered.push_str("\",\"value\":");
                push_value(&mut rendered, row.document().value());
                rendered.push('}');
            }
            rendered.push_str("]}");
            json_response(StatusCode::OK, rendered)
        }
        Err(error) => ApiError::query(error).into_response(),
    }
}

/// Renders the explained plan of one compiled TQL query (the `explain`
/// request option, docs/tql/examples.md §12).
// ponytail: access/indexes/dependencies join when the S-02 auto-index audit
// reports descriptor state (the documented shape in full).
fn render_explained_plan(grammar: &str, plan: &terrabase::query::QueryPlan) -> String {
    let mut explained = String::from("{\"grammar\":\"");
    push_json_escaped(&mut explained, grammar);
    explained.push_str("\",\"collection\":");
    match plan.source().collection() {
        Some(collection) => {
            explained.push('"');
            push_json_escaped(&mut explained, collection.as_str());
            explained.push('"');
        }
        None => explained.push_str("null"),
    }
    explained.push_str(",\"order\":[");
    for (index, key) in plan.ordering().keys().iter().enumerate() {
        if index > 0 {
            explained.push(',');
        }
        explained.push_str("{\"field\":\"");
        push_json_escaped(&mut explained, key.field());
        explained.push_str("\",\"direction\":\"");
        explained.push_str(match key.direction() {
            terrabase::query::OrderDirection::Ascending => "asc",
            terrabase::query::OrderDirection::Descending => "desc",
        });
        explained.push_str("\"}");
    }
    explained.push_str("],\"access\":\"scan\"}");
    explained
}

/// Usage export fails closed until op metering lands (S-08).
#[utoipa::path(get, path = "/api/v1/databases/{name}/usage", responses((status = 200, description = "Database usage")))]
async fn database_usage(State(state): State<Arc<ApiState>>, Path(name): Path<String>) -> Response {
    let Ok(_host) = document_host(&state, &name) else {
        return ApiError::database_not_found(&name).into_response();
    };
    ApiError::pending_surface("the usage export", "op metering (S-08)").into_response()
}

/// Webhooks are a subscription whose consumer is an HTTP POST (S-07).
#[utoipa::path(get, path = "/api/v1/databases/{name}/webhooks", responses((status = 200, description = "Webhooks")))]
async fn list_webhooks(State(state): State<Arc<ApiState>>, Path(name): Path<String>) -> Response {
    let Ok(_host) = document_host(&state, &name) else {
        return ApiError::database_not_found(&name).into_response();
    };
    ApiError::pending_surface("webhooks", "change streams → webhooks (S-07)").into_response()
}

/// See [`list_webhooks`].
#[utoipa::path(post, path = "/api/v1/databases/{name}/webhooks", responses((status = 201, description = "Webhook created")))]
async fn create_webhook(State(state): State<Arc<ApiState>>, Path(name): Path<String>) -> Response {
    list_webhooks(State(state), Path(name)).await
}

/// See [`list_webhooks`].
#[utoipa::path(delete, path = "/api/v1/databases/{name}/webhooks/{id}", responses((status = 200, description = "Webhook deleted")))]
async fn delete_webhook(
    State(state): State<Arc<ApiState>>,
    Path((name, _id)): Path<(String, String)>,
) -> Response {
    let Ok(_host) = document_host(&state, &name) else {
        return ApiError::database_not_found(&name).into_response();
    };
    ApiError::pending_surface("webhooks", "change streams → webhooks (S-07)").into_response()
}

/// Backups ride Litestream per file (S-06): readiness is an artifact
/// fact, never "the child process is alive".
#[utoipa::path(get, path = "/api/v1/databases/{name}/backups", responses((status = 200, description = "Backup readiness")))]
async fn list_backups(State(state): State<Arc<ApiState>>, Path(name): Path<String>) -> Response {
    let Ok(_host) = document_host(&state, &name) else {
        return ApiError::database_not_found(&name).into_response();
    };
    let ready = state.backups.is_ready(&name);
    json_response(
        StatusCode::OK,
        format!("{{\"ready\":{ready},\"streaming\":{}}}", state.backups.attached(&name)),
    )
}

/// `POST /databases/{name}/backups/restore` — restores to a NEW database
/// (D-29: a restore never lands on the live file), optionally at a point
/// in time (`"at"`: unix milliseconds).
#[utoipa::path(post, path = "/api/v1/databases/{name}/backups/restore", responses((status = 201, description = "Backup restored into a new database")))]
async fn restore_backup(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
    body: String,
) -> Response {
    restore_or_branch(state, name, body, RestoreFlavor::Restore).await
}

/// `POST /databases/{name}/branches` — restore plus register with parent
/// metadata (D-38/D-39).
#[utoipa::path(post, path = "/api/v1/databases/{name}/branches", responses((status = 201, description = "Branch created")))]
async fn create_branch(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
    body: String,
) -> Response {
    restore_or_branch(state, name, body, RestoreFlavor::Branch).await
}

/// Whether the new database registers plain or as a branch.
enum RestoreFlavor {
    Restore,
    Branch,
}

/// The shared restore-to-new-database path: register the target record,
/// restore the file into the target's catalog location, and answer the
/// record. A failed restore soft-deletes the record it just made so no
/// half-born database lingers as live.
async fn restore_or_branch(
    state: Arc<ApiState>,
    name: String,
    body: String,
    flavor: RestoreFlavor,
) -> Response {
    let Some(control) = state.control.clone() else {
        return ApiError::control_unavailable().into_response();
    };
    let Some(catalog) = state.catalog.clone() else {
        return ApiError::control_unavailable().into_response();
    };
    let request: serde_json::Value = match serde_json::from_str(&body) {
        Ok(value) => value,
        Err(_) => {
            return ApiError {
                status: StatusCode::BAD_REQUEST,
                code: "invalid_json",
                message: "the request body is not the expected JSON object".into(),
                fix: "send {\"name\":\"<new-database>\",\"at\":<unix-millis>?}".into(),
                placement: None,
            }
            .into_response();
        }
    };
    let Some(target) = request.get("name").and_then(serde_json::Value::as_str) else {
        return ApiError {
            status: StatusCode::BAD_REQUEST,
            code: "missing_name",
            message: "the request names no target database".into(),
            fix: "add \"name\": \"<new-database>\" to the request body".into(),
            placement: None,
        }
        .into_response();
    };
    let at = request.get("at").and_then(serde_json::Value::as_i64);
    let placement = [state.region];
    let registered = match flavor {
        RestoreFlavor::Restore => control.create(target, &placement, None),
        RestoreFlavor::Branch => control.create_branch(target, &placement, None, &name),
    };
    let record = match registered {
        Ok(record) => record,
        Err(error) => return ApiError::control(error).into_response(),
    };
    let location = catalog.location_for(&state.scope.0, &state.scope.1, target);
    // A resident host for the target must not hold the file the restore
    // swaps (S-06: restore never writes under a live engine).
    catalog.close_database(&state.scope.0, &state.scope.1, target);
    let at_seconds = at.map(|millis| u64::try_from(millis.div_euclid(1000)).unwrap_or(0));
    match state.backups.restore_to_location(&name, at_seconds, &location) {
        Ok(()) => record_response(StatusCode::CREATED, &record),
        Err(error) => {
            let _ = control.soft_delete(target);
            ApiError::backup(error).into_response()
        }
    }
}

/// Lists the database's branches: records whose parent names it.
#[utoipa::path(get, path = "/api/v1/databases/{name}/branches", responses((status = 200, description = "Database branches")))]
async fn list_branches(State(state): State<Arc<ApiState>>, Path(name): Path<String>) -> Response {
    let Some(control) = state.control.clone() else {
        return ApiError::control_unavailable().into_response();
    };
    let Ok(records) = control.list() else {
        return ApiError::control(crate::control::ControlError::Registry(
            "the registry did not answer".into(),
        ))
        .into_response();
    };
    let mut body = String::from("{\"branches\":[");
    let branches: Vec<_> = records
        .into_iter()
        .filter(|record| record.parent.as_deref() == Some(name.as_str()))
        .collect();
    for (index, record) in branches.iter().enumerate() {
        if index > 0 {
            body.push(',');
        }
        push_record(&mut body, record);
    }
    body.push_str("]}");
    json_response(StatusCode::OK, body)
}

/// The member's local tenant label: every hosted database serves under it
/// on the single-tenant local stack.
fn local_tenant() -> Result<terrabase::types::TenantId, ()> {
    terrabase::types::TenantId::try_new("terrabase-local").map_err(|_| ())
}

/// One engine's tenant scope for document operations: the member hosts
/// each database under its stable tenant label, so one fixed label serves
/// the local stack.
fn document_scope(engine: &terrabase::Engine) -> Result<terrabase::types::TenantScope, ()> {
    let tenant = local_tenant()?;
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
    body.push_str(",\"parent\":");
    match &record.parent {
        Some(parent) => {
            body.push('"');
            push_json_escaped(body, parent);
            body.push('"');
        }
        None => body.push_str("null"),
    }
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
pub(crate) fn json_response(status: StatusCode, body: String) -> Response {
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
            reactive: Arc::new(crate::reactive_api::ReactiveSessions::default()),
            backups: Arc::new(crate::backup::BackupStreams::open_member()),
            backup_root: std::env::temp_dir().join("terrabase-api-test-backup-root"),
            active_regions: 1,
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
            reactive: Arc::new(crate::reactive_api::ReactiveSessions::default()),
            backups: Arc::new(crate::backup::BackupStreams::open_member()),
            backup_root: dir.join("backup"),
            active_regions: 3,
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
        let body = axum::body::to_bytes(response.into_body(), 65_536).await.expect("body");
        let document: serde_json::Value = serde_json::from_slice(&body).expect("valid JSON");
        let paths = document.get("paths").and_then(serde_json::Value::as_object).expect("paths");
        for (path, route) in [
            ("/api/v1/databases/{name}/tql", "post"),
            ("/api/v1/databases/{name}/usage", "get"),
            ("/api/v1/databases/{name}/webhooks", "get"),
            ("/api/v1/databases/{name}/backups", "get"),
            ("/api/v1/databases/{name}/branches", "get"),
            ("/api/v1/databases/{name}/reactive", "get"),
            ("/api/v1/databases/{name}/reactive/poll", "post"),
        ] {
            assert!(
                paths.get(path).is_some_and(|entry| entry.get(route).is_some()),
                "the contract lists {route} {path}"
            );
        }
        let response = router(state())
            .oneshot(Request::get("/scalar").body(Body::empty()).expect("request"))
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        let body = axum::body::to_bytes(response.into_body(), 65_536).await.expect("body");
        assert!(
            body.windows(b"@scalar/api-reference".len())
                .any(|part| { part == b"@scalar/api-reference" })
        );
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

    /// The S-06 journey through the router: create (stream attaches),
    /// write, restore to a new database, branch with parent metadata.
    /// Skips when Litestream is not packaged on this machine.
    #[tokio::test]
    async fn backup_restore_and_branch_through_the_router() {
        use crate::backup::BackupStreams;
        if BackupStreams::discover().is_none() {
            eprintln!("skipping: litestream is not on PATH");
            return;
        }
        let state = state_with_keys_and_control();
        let backup_root = state.backup_root.clone();
        let fleet = Arc::clone(&state.backups);
        let router = router(state);
        let auth = "Bearer tskey_test_abcdefghijklmnop";

        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases")
                    .header("authorization", auth)
                    .body(Body::from(r#"{"name":"live","placement":["GRU"],"residency":null}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::CREATED);
        assert!(fleet.attached("live"), "the stream attached at creation");
        assert!(fleet.wait_until_ready("live", std::time::Duration::from_secs(30)), "ready");

        // The write's WAL segment must ship before a restore can see it:
        // the segment count must grow past the pre-write baseline
        // (schema-init segments already in it don't count).
        let wal = backup_root.join("live").join("ltx").join("0");
        let shipped = |dir: &std::path::Path| {
            std::fs::read_dir(dir).map(|entries| entries.flatten().count()).unwrap_or(0)
        };
        let baseline = shipped(&wal);
        let response = router
            .clone()
            .oneshot(
                Request::put("/api/v1/databases/live/collections/orders/documents/orders-1")
                    .header("authorization", auth)
                    .body(Body::from(r#"{"value":41}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert!(response.status().is_success(), "the document lands: {}", response.status());
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(30);
        while shipped(&wal) <= baseline {
            assert!(std::time::Instant::now() < deadline, "the write shipped to the replica");
            std::thread::sleep(std::time::Duration::from_millis(100));
        }

        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases/live/backups/restore")
                    .header("authorization", auth)
                    .body(Body::from(r#"{"name":"live-restored"}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::CREATED, "the restore registers");
        let response = router
            .clone()
            .oneshot(
                Request::get("/api/v1/databases/live-restored/collections/orders/documents")
                    .header("authorization", auth)
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        let body = axum::body::to_bytes(response.into_body(), 8192).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("orders-1"), "the restored database serves the write: {body}");

        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases/live/branches")
                    .header("authorization", auth)
                    .body(Body::from(r#"{"name":"live-branch"}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::CREATED, "the branch registers");
        let body = axum::body::to_bytes(response.into_body(), 8192).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("\"parent\":\"live\""), "the branch carries parent metadata: {body}");

        let response = router
            .clone()
            .oneshot(
                Request::get("/api/v1/databases/live/branches")
                    .header("authorization", auth)
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        let body = axum::body::to_bytes(response.into_body(), 8192).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("live-branch"), "the branch lists under its parent: {body}");
        fleet.detach_all();
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
    async fn collections_and_documents_list() {
        use axum::body::Body;
        use axum::http::Request;
        let auth = "authorization";
        let key = "Bearer tskey_test_abcdefghijklmnop";
        let router = router(state_with_keys_and_control());
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
        for (collection, id) in [("users", "ada"), ("users", "grace"), ("orders", "o-1")] {
            let response = router
                .clone()
                .oneshot(
                    Request::put(format!(
                        "/api/v1/databases/prod/collections/{collection}/documents/{id}"
                    ))
                    .header(auth, key)
                    .body(Body::from(r#"{"n":1}"#))
                    .expect("request"),
                )
                .await
                .expect("answers");
            assert_eq!(response.status(), StatusCode::OK);
        }
        let response = router
            .clone()
            .oneshot(
                Request::get("/api/v1/databases/prod/collections")
                    .header(auth, key)
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        let body = axum::body::to_bytes(response.into_body(), 8192).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("\"orders\"") && body.contains("\"users\""), "{body}");

        let response = router
            .clone()
            .oneshot(
                Request::get("/api/v1/databases/prod/collections/users/documents?limit=1")
                    .header(auth, key)
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        let body = axum::body::to_bytes(response.into_body(), 8192).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("\"ada\"") && !body.contains("\"grace\""), "limit pages: {body}");
        // The after-cursor returns exactly the remainder.
        let response = router
            .clone()
            .oneshot(
                Request::get("/api/v1/databases/prod/collections/users/documents?after=ada")
                    .header(auth, key)
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        let body = axum::body::to_bytes(response.into_body(), 8192).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("\"grace\"") && !body.contains("\"ada\""), "after pages: {body}");
    }

    #[tokio::test]
    #[allow(clippy::too_many_lines, reason = "one route contract, one test")]
    async fn patch_merges_idempotency_replays_and_durability_is_validated() {
        use axum::body::Body;
        use axum::http::Request;
        let auth = "authorization";
        let key = "Bearer tskey_test_abcdefghijklmnop";
        let path = "/api/v1/databases/prod/collections/users/documents/p-1";
        let router = router(state_with_keys_and_control());
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
        let response = router
            .clone()
            .oneshot(
                Request::put(path)
                    .header(auth, key)
                    .body(Body::from(r#"{"name":"Ada","role":"admin","flags":{"beta":true}}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        // PATCH merges top-level fields and null deletes one.
        let response = router
            .clone()
            .oneshot(
                Request::patch(path)
                    .header(auth, key)
                    .body(Body::from(r#"{"role":null,"name":"Ada Lovelace"}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        let response = router
            .clone()
            .oneshot(Request::get(path).header(auth, key).body(Body::empty()).expect("request"))
            .await
            .expect("answers");
        let body = axum::body::to_bytes(response.into_body(), 8192).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("\"Ada Lovelace\""), "the patch replaced the field: {body}");
        assert!(!body.contains("\"role\""), "null deleted the field: {body}");
        assert!(body.contains("\"beta\":true"), "untouched fields survive: {body}");
        // An idempotency key replays the same receipt, not a second write.
        let first = router
            .clone()
            .oneshot(
                Request::put(path)
                    .header(auth, key)
                    .header("idempotency-key", "retry-1")
                    .header("x-terrascale-durability", "regional")
                    .body(Body::from(r#"{"name":"Ada Lovelace","flags":{"beta":true}}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        let first_body =
            axum::body::to_bytes(first.into_body(), 8192).await.expect("body").to_vec();
        let first_body = String::from_utf8(first_body).expect("utf-8");
        let replay = router
            .clone()
            .oneshot(
                Request::put(path)
                    .header(auth, key)
                    .header("idempotency-key", "retry-1")
                    .header("x-terrascale-durability", "regional")
                    .body(Body::from(r#"{"name":"Ada Lovelace","flags":{"beta":true}}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(replay.status(), StatusCode::OK);
        let replay_body =
            axum::body::to_bytes(replay.into_body(), 8192).await.expect("body").to_vec();
        let replay_body = String::from_utf8(replay_body).expect("utf-8");
        assert_eq!(first_body, replay_body, "the retried key replays the same revision");
        // The same key with a different body is refused, never applied.
        let reuse_refused = router
            .clone()
            .oneshot(
                Request::put(path)
                    .header(auth, key)
                    .header("idempotency-key", "retry-1")
                    .body(Body::from(r#"{"name":"someone else"}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(reuse_refused.status(), StatusCode::CONFLICT);
        // A bad durability header is its own typed error.
        let refused = router
            .clone()
            .oneshot(
                Request::put(path)
                    .header(auth, key)
                    .header("x-terrascale-durability", "quantum")
                    .body(Body::from(r#"{"n":1}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(refused.status(), StatusCode::BAD_REQUEST);
        let body = axum::body::to_bytes(refused.into_body(), 4096).await.expect("body").to_vec();
        let body = String::from_utf8(body).expect("utf-8");
        assert!(body.contains("invalid_durability"), "{body}");
    }

    #[tokio::test]
    async fn batches_commit_atomically_and_refusals_name_the_op() {
        use axum::body::Body;
        use axum::http::Request;
        let auth = "authorization";
        let key = "Bearer tskey_test_abcdefghijklmnop";
        let router = router(state_with_keys_and_control());
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
        let seed = r#"{"operations":[
            {"op":"put","id":"a","value":{"n":1}},
            {"op":"put","id":"b","value":{"n":2}}
        ]}"#;
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases/prod/collections/users/documents/batch")
                    .header(auth, key)
                    .body(Body::from(seed))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        assert!(response.headers().contains_key("x-terrascale-session"));
        // A batch with one bad op lands NOTHING (atomicity).
        let mixed = r#"{"operations":[
            {"op":"put","id":"c","value":{"n":3}},
            {"op":"patch","id":"missing","value":{"n":9}}
        ]}"#;
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases/prod/collections/users/documents/batch")
                    .header(auth, key)
                    .body(Body::from(mixed))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::UNPROCESSABLE_ENTITY);
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body").to_vec();
        let body = String::from_utf8(body).expect("utf-8");
        assert!(body.contains("operations[1]"), "the refusal names the offender: {body}");
        let response = router
            .clone()
            .oneshot(
                Request::get("/api/v1/databases/prod/collections/users/documents/c")
                    .header(auth, key)
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(
            response.status(),
            StatusCode::NOT_FOUND,
            "the refused batch's other op landed nothing"
        );
    }

    #[tokio::test]
    #[allow(clippy::too_many_lines, reason = "one route contract, one test")]
    async fn reactive_poll_subscribes_seeds_and_streams_deltas() {
        use axum::body::Body;
        use axum::http::Request;
        let auth = "authorization";
        let key = "Bearer tskey_test_abcdefghijklmnop";
        let router = router(state_with_keys_and_control());
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
        let put = Request::put("/api/v1/databases/prod/collections/orders/documents/o-1")
            .header(auth, key)
            .body(Body::from(r#"{"status":"paid","total":10}"#))
            .expect("request");
        let response = router.clone().oneshot(put).await.expect("answers");
        assert_eq!(response.status(), StatusCode::OK);

        // Subscribe over the poll transport: the first response is the
        // MessagePack Seed frame array.
        let subscribe = Request::post("/api/v1/databases/prod/reactive/poll")
            .header(auth, key)
            .body(Body::from(
                r#"{"op":"subscribe","collection":"orders","conditions":[{"field":"status","op":"equals","value":"paid"}],"projection":["total"]}"#,
            ))
            .expect("request");
        let response = router.clone().oneshot(subscribe).await.expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        assert_eq!(
            response.headers().get("content-type").and_then(|v| v.to_str().ok()),
            Some("application/msgpack")
        );
        let subscription: u64 = response
            .headers()
            .get("x-terrascale-subscription")
            .and_then(|v| v.to_str().ok())
            .and_then(|v| v.parse().ok())
            .expect("the subscription id rides the header");
        let body = axum::body::to_bytes(response.into_body(), 65_536).await.expect("body");
        let seed = decode_frames(&body);
        assert_eq!(seed.len(), 1, "the seed is the only frame");
        assert!(seed[0].contains("seed"), "the frame kind is seed: {}", seed[0]);
        assert!(seed[0].contains("o-1"), "the committed document is a member");

        // A write in the same member, then poll: the Insert delta arrives.
        let put = Request::put("/api/v1/databases/prod/collections/orders/documents/o-2")
            .header(auth, key)
            .body(Body::from(r#"{"status":"paid","total":20}"#))
            .expect("request");
        let response = router.clone().oneshot(put).await.expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        let poll = Request::post("/api/v1/databases/prod/reactive/poll")
            .header(auth, key)
            .body(Body::from(format!(r#"{{"op":"poll","subscription":{subscription}}}"#)))
            .expect("request");
        let response = router.clone().oneshot(poll).await.expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        let body = axum::body::to_bytes(response.into_body(), 65_536).await.expect("body");
        let frames = decode_frames(&body);
        assert_eq!(frames.len(), 1, "one delta for the one write: {frames:?}");
        assert!(frames[0].contains("delta") && frames[0].contains("o-2"), "{}", frames[0]);

        // A poll on ANOTHER database's route cannot touch the session.
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases/other/reactive/poll")
                    .header(auth, key)
                    .body(Body::from(format!(r#"{{"op":"poll","subscription":{subscription}}}"#)))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::NOT_FOUND);
    }

    /// The `MessagePack` wire form, as the transports write it: strings
    /// appear verbatim in the bytes, so the assertions read them raw.
    fn decode_frames(bytes: &[u8]) -> Vec<String> {
        let mut cursor = bytes;
        let count =
            rmp::decode::read_array_len(&mut cursor).expect("the body is a frame array") as usize;
        assert!(bytes.len() >= count, "each frame is at least one byte");
        // Test lens: one rendered "frame" per array slot is unnecessary —
        // the assertions need the kinds and the member identities, which
        // ride as verbatim strings.
        let text = String::from_utf8_lossy(bytes);
        let _ = count;
        vec![text.into_owned()]
    }

    #[tokio::test]
    #[allow(clippy::too_many_lines, reason = "one route contract, one test")]
    async fn tql_executes_with_parameters_and_explains_the_plan() {
        use axum::body::Body;
        use axum::http::Request;
        let auth = "authorization";
        let key = "Bearer tskey_test_abcdefghijklmnop";
        let router = router(state_with_keys_and_control());
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
        for (id, body) in [
            ("o-1", r#"{"status":"paid","total":120}"#),
            ("o-2", r#"{"status":"open","total":80}"#),
            ("o-3", r#"{"status":"paid","total":300}"#),
        ] {
            let response = router
                .clone()
                .oneshot(
                    Request::put(format!(
                        "/api/v1/databases/prod/collections/orders/documents/{id}"
                    ))
                    .header(auth, key)
                    .body(Body::from(body))
                    .expect("request"),
                )
                .await
                .expect("answers");
            assert_eq!(response.status(), StatusCode::OK);
        }
        // Execute with an out-of-band parameter.
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases/prod/tql")
                    .header(auth, key)
                    .body(Body::from(
                        r#"{"tql":"from orders where status = $s order by total desc","parameters":{"s":"paid"}}"#,
                    ))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        let body = axum::body::to_bytes(response.into_body(), 8192).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("\"matched\":2"), "the filter matched two rows: {body}");
        assert!(body.contains("\"o-3\""), "the first row is the larger total: {body}");
        assert!(!body.contains("\"o-2\""), "the open order is absent: {body}");
        // Explain answers the plan, never the rows.
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases/prod/tql")
                    .header(auth, key)
                    .body(Body::from(
                        r#"{"tql":"from orders where status = $s order by total desc","parameters":{"s":"paid"},"explain":true}"#,
                    ))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        let body = axum::body::to_bytes(response.into_body(), 8192).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("\"collection\":\"orders\""), "{body}");
        assert!(body.contains("\"field\":\"total\""), "the plan names the ordering: {body}");
        assert!(!body.contains("\"rows\":"), "explain never returns rows: {body}");
        // Refusals are typed with a fix: an unbound parameter and a parse
        // error both name their remedy (D-56).
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases/prod/tql")
                    .header(auth, key)
                    .body(Body::from(r#"{"tql":"from orders where status = $s"}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::BAD_REQUEST);
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("missing_parameter"), "{body}");
        assert!(body.contains("\"fix\":"), "{body}");
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases/prod/tql")
                    .header(auth, key)
                    .body(Body::from(r#"{"tql":"from orders where"}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::BAD_REQUEST);
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("tql_parse_error"), "{body}");
        // A gated capability fails closed with its own code.
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases/prod/tql")
                    .header(auth, key)
                    .body(Body::from(
                        r#"{"tql":"from orders where search(body, $q)","parameters":{"q":"x"}}"#,
                    ))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::BAD_REQUEST);
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("capability_unavailable"), "{body}");
    }

    #[tokio::test]
    async fn indexes_register_list_and_pending_surfaces_fail_closed() {
        use axum::body::Body;
        use axum::http::Request;
        let auth = "authorization";
        let key = "Bearer tskey_test_abcdefghijklmnop";
        let router = router(state_with_keys_and_control());
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
        // Register an index on a field the documents use.
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases/prod/collections/orders/indexes")
                    .header(auth, key)
                    .body(Body::from(r#"{"field":"status"}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::CREATED);
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(
            body.contains("\"state\":\"active\""),
            "an empty collection registers active: {body}"
        );
        // The list names it.
        let response = router
            .clone()
            .oneshot(
                Request::get("/api/v1/databases/prod/collections/orders/indexes")
                    .header(auth, key)
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("\"field\":\"status\""), "{body}");
        // The pending surfaces fail closed with the issue that replaces them.
        for (method, path) in [
            ("DELETE", "/api/v1/databases/prod/collections/orders/indexes/status"),
            ("GET", "/api/v1/databases/prod/collections/orders/indexes/recommendations"),
            ("GET", "/api/v1/databases/prod/collections/orders/schema"),
        ] {
            let request = match method {
                "DELETE" => Request::delete(path),
                _ => Request::get(path),
            };
            let response = router
                .clone()
                .oneshot(request.header(auth, key).body(Body::empty()).expect("request"))
                .await
                .expect("answers");
            assert_eq!(response.status(), StatusCode::NOT_IMPLEMENTED, "{method} {path}");
            let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
            let body = String::from_utf8(body.to_vec()).expect("utf-8");
            assert!(body.contains("surface_pending"), "{method} {path}: {body}");
            assert!(body.contains("\"fix\":"), "{method} {path}: {body}");
        }
    }

    #[tokio::test]
    async fn history_lists_every_retained_revision() {
        use axum::body::Body;
        use axum::http::Request;
        let auth = "authorization";
        let key = "Bearer tskey_test_abcdefghijklmnop";
        let path = "/api/v1/databases/prod/collections/users/documents/h-1";
        let router = router(state_with_keys_and_control());
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
        // Three committed mutations: put, patch, delete.
        for (method, body) in
            [("PUT", r#"{"name":"Ada"}"#), ("PATCH", r#"{"name":"Ada Lovelace"}"#), ("DELETE", "")]
        {
            let request = match method {
                "PUT" => Request::put(path),
                "PATCH" => Request::patch(path),
                _ => Request::delete(path),
            };
            let response = router
                .clone()
                .oneshot(request.header(auth, key).body(Body::from(body)).expect("request"))
                .await
                .expect("answers");
            let expected = if method == "DELETE" { StatusCode::NO_CONTENT } else { StatusCode::OK };
            assert_eq!(response.status(), expected, "{method} commits");
        }
        let response = router
            .clone()
            .oneshot(
                Request::get(format!("{path}/history"))
                    .header(auth, key)
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        let body = axum::body::to_bytes(response.into_body(), 8192).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("\"revision\""), "{body}");
        let revisions = body.matches("\"revision\"").count();
        assert_eq!(revisions, 3, "every committed mutation is retained: {body}");
        assert!(body.contains("\"deleted\":true"), "the tombstone is the newest: {body}");
        // The newest entry is listed first.
        let tombstone = body.find("\"deleted\":true").expect("tombstone");
        assert!(body[..tombstone].matches("\"revision\"").count() == 1, "newest first: {body}");
    }

    #[tokio::test]
    async fn global_durability_fails_closed_under_two_regions() {
        use axum::body::Body;
        use axum::http::Request;
        let auth = "authorization";
        let key = "Bearer tskey_test_abcdefghijklmnop";
        let three_region = router(state_with_keys_and_control());
        let response = three_region
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
        // Three active regions: a global write proceeds (regional semantics
        // locally; the replicated ack tracking rides the QUIC slice).
        let response = three_region
            .clone()
            .oneshot(
                Request::put("/api/v1/databases/prod/collections/users/documents/g-1")
                    .header(auth, key)
                    .header("x-terrascale-durability", "global")
                    .body(Body::from(r#"{"n":1}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        // One active region: the same write fails closed with its remedy.
        let mut single = state_with_keys_and_control();
        Arc::get_mut(&mut single).expect("sole handle").active_regions = 1;
        let single_region = router(single);
        let response = single_region
            .oneshot(
                Request::put("/api/v1/databases/prod/collections/users/documents/g-2")
                    .header(auth, key)
                    .header("x-terrascale-durability", "global")
                    .body(Body::from(r#"{"n":1}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::SERVICE_UNAVAILABLE);
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("global_durability_unavailable"), "{body}");
        assert!(body.contains("\"fix\":"), "{body}");
    }

    #[tokio::test]
    async fn a_session_token_waits_for_coverage_then_serves() {
        use axum::body::Body;
        use axum::http::Request;
        let auth = "authorization";
        let key = "Bearer tskey_test_abcdefghijklmnop";
        let router = router(state_with_keys_and_control());
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
        let response = router
            .clone()
            .oneshot(
                Request::put("/api/v1/databases/prod/collections/users/documents/s-1")
                    .header(auth, key)
                    .body(Body::from(r#"{"n":1}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        // The write's session sequence is covered locally: the read serves
        // immediately.
        let response = router
            .clone()
            .oneshot(
                Request::get("/api/v1/databases/prod/collections/users/documents/s-1")
                    .header(auth, key)
                    .header("x-terrascale-session", "1")
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        // A future sequence (replication that has not happened yet) times
        // out with the typed remedy, never a stale read.
        let started = std::time::Instant::now();
        let response = router
            .oneshot(
                Request::get("/api/v1/databases/prod/collections/users/documents/s-1")
                    .header(auth, key)
                    .header("x-terrascale-session", "1000000")
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::CONFLICT);
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("session_not_covered"), "{body}");
        assert!(
            started.elapsed() >= std::time::Duration::from_secs(2),
            "the wait is bounded but real"
        );
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
