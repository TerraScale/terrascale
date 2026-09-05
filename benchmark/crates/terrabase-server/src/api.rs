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
use axum::http::Method;
use axum::http::StatusCode;
use axum::http::header::CONTENT_TYPE;
use axum::http::header::HeaderName;
use axum::middleware::Next;
use axum::middleware::from_fn_with_state;
use axum::response::IntoResponse;
use axum::response::Response;
use axum::routing::get;
use terrabase::CommitOutcome;
use terrabase::Session;
use terrabase::SessionError;
use terrabase::SessionOptions;
use terrabase::SessionToken;
use terrabase::observability::MetricsRegistry;
use terrabase::observability::ServiceState;
use terrabase::regions::RegionCatalog;
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

use crate::api_keys::Verbs;

/// The public API's path prefix.
pub const API_PREFIX: &str = "/api/v1";

/// `X-TerraScale-Placement` — the database's explicit region list (D-52).
pub const PLACEMENT_HEADER: HeaderName = HeaderName::from_static("x-terrascale-placement");
/// `X-TerraScale-Residency` — the residency constraint stored with the
/// database (D-52), validated against the placement.
pub const RESIDENCY_HEADER: HeaderName = HeaderName::from_static("x-terrascale-residency");
/// `X-TerraScale-Region` — the serving member's own region.
pub const REGION_HEADER: HeaderName = HeaderName::from_static("x-terrascale-region");
/// `X-TerraScale-Session` carries a strict opaque base64url session token.
pub const SESSION_HEADER: HeaderName = HeaderName::from_static("x-terrascale-session");
/// Maximum header spelling accepted by the public session contract.
pub const MAX_SESSION_HEADER_BYTES: usize = 16 * 1024;

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

    /// A typed refusal for a header whose bytes are not valid HTTP text.
    #[must_use]
    pub fn invalid_header(name: &'static str) -> Self {
        Self {
            status: StatusCode::BAD_REQUEST,
            code: "invalid_header",
            message: format!("{name} is not valid ASCII header text"),
            fix: format!("send a valid {name} header or omit it"),
            placement: None,
        }
    }

    /// An `If-Match` precondition that no longer holds (D-20 optimistic
    /// concurrency): the client wrote against a stale revision.
    #[must_use]
    pub fn precondition_failed(detail: &str) -> Self {
        Self {
            status: StatusCode::PRECONDITION_FAILED,
            code: "precondition_failed",
            message: format!("the write's precondition failed ({detail})"),
            fix: "re-read the document and retry with the ETag from its latest GET".into(),
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
            ControlError::PlacementLockWidened { source, detail } => Self {
                status: StatusCode::UNPROCESSABLE_ENTITY,
                code: "placement_lock_widened",
                message: format!("'{source}' keeps its placement lock: {detail}"),
                fix: "keep the derived database inside its source's placement and residency (D-52)".into(),
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

    /// Durable storage could not answer the request. This is deliberately
    /// distinct from a missing document or database: a failed read must not
    /// be presented as an empty result or a not-found response.
    #[must_use]
    pub fn storage_failure() -> Self {
        Self {
            status: StatusCode::SERVICE_UNAVAILABLE,
            code: "storage_failure",
            message: "durable storage could not serve the request".into(),
            fix: "check the member's storage health, then retry once".into(),
            placement: None,
        }
    }

    /// One typed SQL refusal: every front-end class spells its remedy, and
    /// the message carries the offending construct and byte offset when the
    /// error has one (D-56).
    #[must_use]
    pub fn sql(error: &terrabase::query::sql::SqlError) -> Self {
        use terrabase::query::sql::SqlErrorClass;
        let (code, fix): (&'static str, &str) = match error.class() {
            SqlErrorClass::ParseError => (
                "sql_parse_error",
                "fix the text at the reported offset: one SELECT, WITH ... SELECT, or EXPLAIN statement",
            ),
            SqlErrorClass::UnsupportedStatement => (
                "unsupported_sql_statement",
                "send one SELECT, WITH ... SELECT, or EXPLAIN SELECT statement",
            ),
            SqlErrorClass::ForbiddenKeyword => (
                "forbidden_sql_keyword",
                "the read-only endpoint refuses DDL, DML, pragmas, and attachments",
            ),
            SqlErrorClass::ForbiddenFunction => (
                "forbidden_sql_function",
                "the function is volatile or host-sensitive; drop it from the statement",
            ),
            SqlErrorClass::ForbiddenRelation => (
                "forbidden_sql_relation",
                "the relation is qualified or SQLite-internal; read a collection instead",
            ),
            SqlErrorClass::ForbiddenTableFunction => (
                "forbidden_sql_table_function",
                "table-valued functions cannot appear in the statement",
            ),
            SqlErrorClass::UnboundParameter => {
                ("missing_parameter", "bind the parameter in the request's parameters object")
            }
            SqlErrorClass::UnexpectedParameter => {
                ("unexpected_parameter", "remove bindings the text does not reference")
            }
            SqlErrorClass::InvalidBound => {
                ("sql_bound_exceeded", "keep limit/offset within the documented bounds")
            }
            SqlErrorClass::InvalidIdentifier => {
                ("invalid_sql_identifier", "use a plain identifier for the binding name")
            }
        };
        let mut message = format!("the SQL text was refused ({code})");
        if let Some(construct) = error.construct() {
            message.push_str(": ");
            message.push_str(construct);
        }
        if let Some(position) = error.position() {
            use std::fmt::Write;
            let _ = write!(message, " at byte offset {position}");
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
                code: "invalid_sql_spec",
                message: "the SQL text could not be lowered into a query plan".into(),
                fix: "use one collection, supported predicates, and bounded paging".into(),
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

    /// One D-64 abuse-guard refusal (E-10): the request is well-formed but
    /// the database's plan budget is spent. Nothing is billed; the fix
    /// names the remedy (slow down, serve from the CDN, or raise the guard).
    #[must_use]
    pub fn abuse_guard(rejection: &crate::guards::GuardRejection) -> Self {
        Self {
            status: StatusCode::TOO_MANY_REQUESTS,
            code: "abuse_guard_exceeded",
            message: rejection.message(),
            fix: rejection.fix(),
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
    /// The Litestream fleet (`S-06`): one stream per hosted database.
    pub backups: Arc<crate::backup::BackupStreams>,
    /// Where the fleet writes replicas: `<data_root>/backup` on the local
    /// stack (`S-06`).
    pub backup_root: std::path::PathBuf,
    /// Throwaway PITR restores backing `?at=` time-travel reads (`S-06`):
    /// cached per (database, second) under the backup root with a bounded
    /// budget.
    pub timetravel: Arc<crate::backup::TimeTravelCache>,
    /// The D-64 abuse guards and per-tenant governance budgets (E-10):
    /// every read/query/transport admission on this surface checks them
    /// first, and every served direct-read byte accounts against them.
    /// Unknown databases run the Free row (fail-closed).
    pub guards: Arc<crate::guards::AbuseGuards>,
    /// HMAC key ring used to authenticate requests forwarded by the sync
    /// edge after customer credentials have been stripped.
    pub origin_proof_keys: Arc<crate::reactive_api::OriginProofKeyRing>,
    /// HMAC key ring used for view capability issuance and direct regional
    /// capability verification.
    pub capability_keys: Arc<crate::reactive_api::CapabilityKeyRing>,
}

/// The served route table as an `OpenAPI` router: the single source of both
/// the running axum router and the published contract document, so the two
/// can never drift apart.
fn openapi_router() -> OpenApiRouter<Arc<ApiState>> {
    OpenApiRouter::new()
        .routes(routes!(health))
        .routes(routes!(regions))
        .routes(routes!(list_databases, create_database))
        .routes(routes!(get_database, delete_database))
        .routes(routes!(restore_database))
        .routes(routes!(list_collections))
        .routes(routes!(list_indexes, create_index))
        .routes(routes!(list_documents))
        .routes(routes!(batch_documents))
        .routes(routes!(get_document, put_document, patch_document, delete_document))
        .routes(routes!(document_history))
        .routes(routes!(execute_sql))
        .routes(routes!(crate::reactive_api::websocket))
        .routes(routes!(list_backups))
        .routes(routes!(restore_backup))
        .routes(routes!(list_branches, create_branch))
        .routes(routes!(crate::reactive_api::attach_view))
        .routes(routes!(crate::reactive_api::snapshot))
        .routes(routes!(crate::reactive_api::view_log))
}

/// Adds the parts of the contract that cannot be inferred from axum extractors.
/// The handlers deliberately keep hand-rendering JSON on the data path, so the
/// public document describes those wire shapes here in one bounded table.
#[allow(clippy::too_many_lines, reason = "one contract table covers every route in one pass")]
fn enrich_openapi(raw: String) -> String {
    let Ok(mut document) = serde_json::from_str::<serde_json::Value>(&raw) else {
        return raw;
    };
    document["security"] = serde_json::json!([{ "bearerAuth": [] }]);
    document["components"] = serde_json::json!({
        "securitySchemes": {
            "bearerAuth": {
                "type": "http",
                "scheme": "bearer",
                "bearerFormat": "TerraScale API key"
            }
        },
        "schemas": openapi_schemas()
    });

    let Some(paths) = document.get_mut("paths").and_then(serde_json::Value::as_object_mut) else {
        return serde_json::to_string(&document).expect("OpenAPI enrichment must serialize");
    };
    for (path, item) in paths {
        let Some(item) = item.as_object_mut() else { continue };
        for method in ["get", "post", "put", "patch", "delete"] {
            let Some(operation) = item.get_mut(method).and_then(serde_json::Value::as_object_mut)
            else {
                continue;
            };
            let operation_id = operation
                .get("operationId")
                .and_then(serde_json::Value::as_str)
                .unwrap_or_default()
                .to_owned();
            operation.insert("security".to_owned(), serde_json::json!([{ "bearerAuth": [] }]));
            for variable in path_variables(path) {
                add_parameter(operation, &variable, "path", true, "string");
            }
            add_route_parameters(operation, &operation_id);
            if let Some((schema, required)) = request_schema(&operation_id) {
                set_request_body(operation, schema, required);
            }

            {
                if operation_id == "delete_document"
                    && let Some(responses) =
                        operation.get_mut("responses").and_then(serde_json::Value::as_object_mut)
                {
                    responses.remove("200");
                    responses.insert(
                        "204".into(),
                        serde_json::json!({"description": "Document deleted"}),
                    );
                }
                let success_schema = success_schema(&operation_id);
                if let Some(responses) =
                    operation.get_mut("responses").and_then(serde_json::Value::as_object_mut)
                {
                    for (status, response) in responses {
                        let Some(response) = response.as_object_mut() else { continue };
                        if status == "204" || status == "101" {
                            response.remove("content");
                        } else if status.starts_with('2') {
                            if let Some(schema) = success_schema {
                                response.insert("content".into(), json_content(schema));
                            }
                        } else if status.parse::<u16>().is_ok_and(|code| code >= 400) {
                            response.insert("content".into(), json_content("ErrorResponse"));
                        }
                    }
                }
                for status in
                    ["400", "401", "403", "404", "409", "421", "422", "429", "500", "503", "504"]
                {
                    set_json_response(operation, status, "Typed API error", "ErrorResponse");
                }
                if operation_id == "health" {
                    set_json_response(operation, "503", "Member is unhealthy", "HealthResponse");
                }
                if matches!(operation_id.as_str(), "health" | "regions") {
                    set_response_header(operation, "200", "X-TerraScale-Region", "string");
                    if operation_id == "health" {
                        set_response_header(operation, "503", "X-TerraScale-Region", "string");
                    }
                }
                if matches!(
                    operation_id.as_str(),
                    "create_database"
                        | "get_database"
                        | "delete_database"
                        | "restore_database"
                        | "restore_backup"
                        | "create_branch"
                ) {
                    for status in ["200", "201"] {
                        set_response_header(operation, status, "X-TerraScale-Placement", "string");
                        set_response_header(operation, status, "X-TerraScale-Residency", "string");
                    }
                }
                if operation_id == "get_document" {
                    set_response_header(operation, "200", "ETag", "string");
                    set_response_header(operation, "304", "ETag", "string");
                }
                if matches!(operation_id.as_str(), "put_document" | "patch_document") {
                    set_response_header(operation, "200", "ETag", "string");
                }
                if matches!(
                    operation_id.as_str(),
                    "get_document"
                        | "put_document"
                        | "patch_document"
                        | "delete_document"
                        | "batch_documents"
                ) {
                    set_response_header(operation, "200", "X-TerraScale-Session", "string");
                    set_response_header(operation, "204", "X-TerraScale-Session", "string");
                }
                if matches!(operation_id.as_str(), "snapshot" | "view_log")
                    && let Some(response) = operation
                        .get_mut("responses")
                        .and_then(serde_json::Value::as_object_mut)
                        .and_then(|responses| responses.get_mut("200"))
                        .and_then(serde_json::Value::as_object_mut)
                {
                    response.insert(
                        "content".to_owned(),
                        serde_json::json!({
                            "application/msgpack": {
                                "schema": { "$ref": "#/components/schemas/ReactiveEnvelope" }
                            }
                        }),
                    );
                }
            }
        }
    }
    serde_json::to_string(&document).expect("OpenAPI enrichment must serialize")
}

fn path_variables(path: &str) -> Vec<String> {
    path.split('{').skip(1).filter_map(|part| part.split('}').next()).map(str::to_owned).collect()
}

fn add_parameter(
    operation: &mut serde_json::Map<String, serde_json::Value>,
    name: &str,
    location: &str,
    required: bool,
    schema_type: &str,
) {
    let parameters = operation
        .entry("parameters")
        .or_insert_with(|| serde_json::json!([]))
        .as_array_mut()
        .expect("OpenAPI parameters are an array");
    if parameters.iter().any(|parameter| {
        parameter.get("name").and_then(serde_json::Value::as_str) == Some(name)
            && parameter.get("in").and_then(serde_json::Value::as_str) == Some(location)
    }) {
        return;
    }
    parameters.push(serde_json::json!({
        "name": name,
        "in": location,
        "required": required,
        "schema": { "type": schema_type }
    }));
}

fn add_route_parameters(operation: &mut serde_json::Map<String, serde_json::Value>, id: &str) {
    match id {
        "list_documents" | "document_history" => {
            add_parameter(operation, "limit", "query", false, "integer");
            add_parameter(operation, "after", "query", false, "string");
            set_parameter_bounds(operation, "limit", "query", 1, 1000);
        }
        "get_document" => {
            add_parameter(operation, "at", "query", false, "integer");
            add_parameter(operation, "if-none-match", "header", false, "string");
            add_parameter(operation, "x-terrascale-session", "header", false, "string");
            set_parameter_format(operation, "at", "query", "int64");
            set_parameter_max_length(
                operation,
                "x-terrascale-session",
                "header",
                MAX_SESSION_HEADER_BYTES,
            );
        }
        "put_document" | "patch_document" | "delete_document" => {
            add_parameter(operation, "if-match", "header", false, "string");
            add_parameter(operation, "idempotency-key", "header", false, "string");
            add_parameter(operation, "x-terrascale-durability", "header", false, "string");
            add_parameter(operation, "x-terrascale-session", "header", false, "string");
            set_parameter_max_length(
                operation,
                "x-terrascale-session",
                "header",
                MAX_SESSION_HEADER_BYTES,
            );
            set_parameter_enum(
                operation,
                "x-terrascale-durability",
                "header",
                &["regional", "global"],
            );
        }
        "batch_documents" => {
            add_parameter(operation, "idempotency-key", "header", false, "string");
            add_parameter(operation, "x-terrascale-durability", "header", false, "string");
            add_parameter(operation, "x-terrascale-session", "header", false, "string");
            set_parameter_max_length(
                operation,
                "x-terrascale-session",
                "header",
                MAX_SESSION_HEADER_BYTES,
            );
            set_parameter_enum(
                operation,
                "x-terrascale-durability",
                "header",
                &["regional", "global"],
            );
        }
        "execute_sql" => {
            add_parameter(operation, "x-terrascale-timeout-ms", "header", false, "integer");
            add_parameter(operation, "x-terrascale-cancelled", "header", false, "boolean");
            set_parameter_bounds(operation, "x-terrascale-timeout-ms", "header", 1, 30000);
        }
        "view_log" => {
            add_parameter(operation, "offset", "query", true, "integer");
            add_parameter(operation, "x-terrascale-view-capability", "header", true, "string");
            set_parameter_format(operation, "offset", "query", "int64");
        }
        "snapshot" => {
            add_parameter(operation, "cut", "query", false, "integer");
            set_parameter_format(operation, "cut", "query", "int64");
        }
        _ => {}
    }
}

fn set_parameter_enum(
    operation: &mut serde_json::Map<String, serde_json::Value>,
    name: &str,
    location: &str,
    values: &[&str],
) {
    let Some(parameters) =
        operation.get_mut("parameters").and_then(serde_json::Value::as_array_mut)
    else {
        return;
    };
    let Some(parameter) = parameters.iter_mut().find(|parameter| {
        parameter.get("name").and_then(serde_json::Value::as_str) == Some(name)
            && parameter.get("in").and_then(serde_json::Value::as_str) == Some(location)
    }) else {
        return;
    };
    parameter["schema"]["enum"] = serde_json::json!(values);
}

fn set_parameter_format(
    operation: &mut serde_json::Map<String, serde_json::Value>,
    name: &str,
    location: &str,
    format: &str,
) {
    let Some(parameters) =
        operation.get_mut("parameters").and_then(serde_json::Value::as_array_mut)
    else {
        return;
    };
    let Some(parameter) = parameters.iter_mut().find(|parameter| {
        parameter.get("name").and_then(serde_json::Value::as_str) == Some(name)
            && parameter.get("in").and_then(serde_json::Value::as_str) == Some(location)
    }) else {
        return;
    };
    let Some(schema) = parameter.get_mut("schema").and_then(serde_json::Value::as_object_mut)
    else {
        return;
    };
    schema.insert("format".to_owned(), serde_json::json!(format));
}

fn set_parameter_bounds(
    operation: &mut serde_json::Map<String, serde_json::Value>,
    name: &str,
    location: &str,
    minimum: u64,
    maximum: u64,
) {
    let Some(parameters) =
        operation.get_mut("parameters").and_then(serde_json::Value::as_array_mut)
    else {
        return;
    };
    let Some(parameter) = parameters.iter_mut().find(|parameter| {
        parameter.get("name").and_then(serde_json::Value::as_str) == Some(name)
            && parameter.get("in").and_then(serde_json::Value::as_str) == Some(location)
    }) else {
        return;
    };
    let Some(schema) = parameter.get_mut("schema").and_then(serde_json::Value::as_object_mut)
    else {
        return;
    };
    schema.insert("minimum".to_owned(), serde_json::json!(minimum));
    schema.insert("maximum".to_owned(), serde_json::json!(maximum));
}

fn set_parameter_max_length(
    operation: &mut serde_json::Map<String, serde_json::Value>,
    name: &str,
    location: &str,
    maximum: usize,
) {
    let Some(parameters) =
        operation.get_mut("parameters").and_then(serde_json::Value::as_array_mut)
    else {
        return;
    };
    let Some(parameter) = parameters.iter_mut().find(|parameter| {
        parameter.get("name").and_then(serde_json::Value::as_str) == Some(name)
            && parameter.get("in").and_then(serde_json::Value::as_str) == Some(location)
    }) else {
        return;
    };
    parameter["schema"]["maxLength"] = serde_json::json!(maximum);
}

fn set_request_body(
    operation: &mut serde_json::Map<String, serde_json::Value>,
    schema: &str,
    required: bool,
) {
    operation.insert("requestBody".to_owned(), serde_json::json!({
        "required": required,
        "content": { "application/json": { "schema": { "$ref": format!("#/components/schemas/{schema}") } } }
    }));
}

fn json_content(schema: &str) -> serde_json::Value {
    serde_json::json!({ "application/json": { "schema": { "$ref": format!("#/components/schemas/{schema}") } } })
}

fn set_json_response(
    operation: &mut serde_json::Map<String, serde_json::Value>,
    status: &str,
    description: &str,
    schema: &str,
) {
    let responses = operation
        .entry("responses")
        .or_insert_with(|| serde_json::json!({}))
        .as_object_mut()
        .expect("OpenAPI responses are an object");
    let response = responses
        .entry(status.to_owned())
        .or_insert_with(|| serde_json::json!({"description": description}));
    if let Some(response) = response.as_object_mut() {
        response.insert("description".to_owned(), serde_json::json!(description));
        response.insert("content".into(), json_content(schema));
    }
}

fn set_response_header(
    operation: &mut serde_json::Map<String, serde_json::Value>,
    status: &str,
    name: &str,
    schema_type: &str,
) {
    let Some(response) = operation
        .get_mut("responses")
        .and_then(serde_json::Value::as_object_mut)
        .and_then(|responses| responses.get_mut(status))
        .and_then(serde_json::Value::as_object_mut)
    else {
        return;
    };
    response.entry("headers").or_insert_with(|| serde_json::json!({}));
    let mut schema = serde_json::Map::new();
    schema.insert("type".to_owned(), serde_json::json!(schema_type));
    if schema_type == "integer" {
        schema.insert("format".to_owned(), serde_json::json!("int64"));
    } else if name.eq_ignore_ascii_case("X-TerraScale-Session") {
        schema.insert("maxLength".to_owned(), serde_json::json!(MAX_SESSION_HEADER_BYTES));
    }
    let headers = response
        .get_mut("headers")
        .and_then(serde_json::Value::as_object_mut)
        .expect("OpenAPI response headers are an object");
    headers.insert(name.to_owned(), serde_json::json!({ "schema": schema }));
}

fn request_schema(operation_id: &str) -> Option<(&'static str, bool)> {
    Some(match operation_id {
        "create_database" => ("CreateDatabaseRequest", true),
        "create_index" => ("CreateIndexRequest", true),
        "batch_documents" => ("BatchRequest", true),
        "put_document" | "patch_document" => ("JsonValue", true),
        "execute_sql" => ("SqlRequest", true),
        "attach_view" => ("AttachViewRequest", true),
        "restore_backup" | "create_branch" => ("RestoreRequest", true),
        _ => return None,
    })
}

fn success_schema(operation_id: &str) -> Option<&'static str> {
    match operation_id {
        "health" | "regions" => Some("HealthResponse"),
        "list_databases" => Some("DatabaseListResponse"),
        "list_branches" => Some("BranchListResponse"),
        "create_database" | "get_database" | "delete_database" | "restore_database"
        | "restore_backup" | "create_branch" => Some("DatabaseRecord"),
        "list_collections" => Some("CollectionsResponse"),
        "list_indexes" => Some("IndexListResponse"),
        "create_index" => Some("IndexCreatedResponse"),
        "list_documents" => Some("DocumentsResponse"),
        "batch_documents" => Some("BatchResponse"),
        "get_document" => Some("JsonValue"),
        "put_document" | "patch_document" => Some("DocumentWriteResponse"),
        "document_history" => Some("HistoryResponse"),
        "execute_sql" => Some("SqlResultResponse"),
        "list_backups" => Some("BackupStatusResponse"),
        "attach_view" => Some("AttachViewResponse"),
        _ => None,
    }
}

fn openapi_schemas() -> serde_json::Value {
    let json_value = serde_json::json!({
        "oneOf": [
            { "type": "null" }, { "type": "boolean" }, { "type": "integer" },
            { "type": "number" }, { "type": "string" },
            { "type": "array", "items": { "$ref": "#/components/schemas/JsonValue" } },
            { "type": "object", "additionalProperties": { "$ref": "#/components/schemas/JsonValue" } }
        ]
    });
    let mut schemas = serde_json::json!({
        "JsonValue": json_value,
        "ErrorResponse": {
            "type": "object", "required": ["error"],
            "properties": { "error": { "type": "object", "required": ["code", "message", "fix"], "properties": {
                "code": { "type": "string" }, "message": { "type": "string" }, "fix": { "type": "string" }
            } } }
        },
        "HealthResponse": { "type": "object", "required": ["region", "healthy"], "properties": { "region": { "type": "string" }, "healthy": { "type": "boolean" } } },
        "DatabaseRecord": { "type": "object", "required": ["name", "placement", "residency", "deleted", "created_at", "parent"], "properties": {
            "name": { "type": "string" }, "placement": { "type": "string" }, "residency": { "type": "string" }, "deleted": { "type": "boolean" }, "created_at": { "type": "integer", "format": "int64" }, "parent": { "type": ["string", "null"] }
        } },
        "DatabaseListResponse": { "type": "object", "required": ["databases"], "properties": { "databases": { "type": "array", "items": { "$ref": "#/components/schemas/DatabaseRecord" } } } },
        "BranchListResponse": { "type": "object", "required": ["branches"], "properties": { "branches": { "type": "array", "items": { "$ref": "#/components/schemas/DatabaseRecord" } } } },
        "CollectionsResponse": { "type": "object", "required": ["collections"], "properties": { "collections": { "type": "array", "items": { "type": "string" } } } },
        "IndexDescriptor": { "type": "object", "required": ["field", "state", "backfill_remaining"], "properties": { "field": { "type": "string" }, "state": { "type": "string", "enum": ["active", "building"] }, "backfill_remaining": { "type": "integer", "format": "int64" } } },
        "IndexListResponse": { "type": "object", "required": ["indexes"], "properties": { "indexes": { "type": "array", "items": { "$ref": "#/components/schemas/IndexDescriptor" } } } },
        "IndexCreatedResponse": { "type": "object", "required": ["field", "state"], "properties": { "field": { "type": "string" }, "state": { "type": "string", "enum": ["active", "building"] } } },
        "DocumentsResponse": { "type": "object", "required": ["documents"], "properties": { "documents": { "type": "array", "items": { "type": "object", "required": ["id", "version", "value"], "properties": { "id": { "type": "string" }, "version": { "type": "integer", "format": "int64" }, "value": { "$ref": "#/components/schemas/JsonValue" } } } } } },
        "DocumentWriteResponse": { "type": "object", "required": ["revision"], "properties": { "revision": { "type": "string" } } },
        "HistoryResponse": { "type": "object", "required": ["history"], "properties": { "history": { "type": "array", "items": { "type": "object", "required": ["revision", "deleted"], "properties": { "revision": { "type": "string" }, "deleted": { "type": "boolean" } } } } } },
        "BatchResponse": { "type": "object", "required": ["committed", "count"], "properties": { "committed": { "type": "integer", "format": "int64" }, "count": { "type": "integer" } } },
        "SqlRequest": { "type": "object", "required": ["sql"], "properties": { "sql": { "type": "string" }, "parameters": { "type": "object", "additionalProperties": { "$ref": "#/components/schemas/JsonValue" } }, "explain": { "type": "boolean" }, "timeout_ms": { "type": "integer", "minimum": 1, "maximum": 30000 }, "cancelled": { "type": "boolean" } } },
        "SqlResponse": { "type": "object", "required": ["matched", "rows"], "properties": { "matched": { "type": "integer", "format": "int64" }, "rows": { "type": "array", "items": { "type": "object", "required": ["id", "value"], "properties": { "id": { "type": "string" }, "value": { "$ref": "#/components/schemas/JsonValue" } } } } } },
        "BackupStatusResponse": { "type": "object", "required": ["ready", "streaming"], "properties": { "ready": { "type": "boolean" }, "streaming": { "type": "boolean" } } },
        "CreateDatabaseRequest": { "type": "object", "required": ["name", "placement"], "properties": { "name": { "type": "string" }, "placement": { "type": "array", "minItems": 1, "items": { "type": "string" } }, "residency": { "type": ["string", "null"] } } },
        "CreateIndexRequest": { "type": "object", "required": ["field"], "properties": { "field": { "type": "string" } } },
        "BatchRequest": { "type": "object", "required": ["operations"], "properties": { "operations": { "type": "array", "minItems": 1, "maxItems": 1000, "items": { "type": "object", "required": ["op", "id"], "properties": { "op": { "type": "string", "enum": ["put", "patch", "delete"] }, "id": { "type": "string" }, "value": { "$ref": "#/components/schemas/JsonValue" } } } } } },
        "RestoreRequest": { "type": "object", "required": ["name"], "properties": { "name": { "type": "string" }, "at": { "type": "integer", "format": "int64" } } },
        "WebhookRequest": { "oneOf": [
            { "type": "object", "required": ["endpoint"], "properties": { "endpoint": { "type": "string", "format": "uri" } } },
            { "type": "object", "required": ["url"], "properties": { "url": { "type": "string", "format": "uri" } } },
            { "type": "object", "required": ["id"], "properties": { "id": { "type": "string" } } }
        ] },
        "AttachViewRequest": { "type": "object", "required": ["databaseId", "statement"], "properties": { "databaseId": { "type": "string" }, "statement": { "type": "string" }, "parameters": { "type": "string", "description": "Base64 encoded SQL parameter bytes" }, "checkpoint": { "type": ["string", "null"], "description": "Base64 encoded opaque resume checkpoint" }, "subscription": { "type": ["string", "null"] }, "entitlement_epoch": { "type": "integer", "format": "int64" } } },
        "AttachViewResponse": { "type": "object", "required": ["view", "generation", "snapshot_chunks", "log", "checkpoint", "capability", "capability_header", "offset"], "properties": { "view": { "type": "string" }, "generation": { "type": "integer", "format": "int64" }, "snapshot_chunks": { "type": "array", "items": { "type": "string" } }, "log": { "type": "string" }, "checkpoint": { "type": "string" }, "capability": { "type": "string" }, "capability_header": { "type": "string" }, "offset": { "type": "integer", "format": "int64" } } },
    });
    let Some(schemas) = schemas.as_object_mut() else {
        return serde_json::json!({});
    };
    schemas.insert("SqlExplainResponse".to_owned(), serde_json::json!({
        "type": "object",
        "required": ["dialect", "collection", "order", "access"],
        "properties": {
            "dialect": { "type": "string", "const": "sql/sqlite-readonly" },
            "collection": { "type": ["string", "null"] },
            "order": { "type": "array", "items": {
                "type": "object", "required": ["field", "direction"],
                "properties": { "field": { "type": "string" }, "direction": { "type": "string", "enum": ["asc", "desc"] } }
            } },
            "access": { "type": "string", "const": "scan" }
        }
    }));
    schemas.insert(
        "SqlResultResponse".to_owned(),
        serde_json::json!({
            "oneOf": [
                { "$ref": "#/components/schemas/SqlResponse" },
                { "$ref": "#/components/schemas/SqlExplainResponse" }
            ]
        }),
    );
    schemas.insert("ReactiveEnvelope".to_owned(), serde_json::json!({
        "type": "array",
        "description": "One indexed sync-v1 MessagePack envelope: [major, minor, features, tag, correlation, causation, body].",
        "minItems": 7, "maxItems": 7,
        "prefixItems": [
            { "type": "integer", "minimum": 0 }, { "type": "integer", "minimum": 0 },
            { "type": "integer", "minimum": 0 }, { "type": "integer", "minimum": 0 },
            { "type": "integer", "minimum": 0 }, { "type": "integer", "minimum": 0 },
            { "oneOf": [{ "type": "array" }, { "type": "object" }] }
        ],
        "items": false
    }));
    serde_json::Value::Object(std::mem::take(schemas))
}

/// Renders the served `OpenAPI` document: exactly the bytes
/// `GET /api/v1/openapi.json` serves. `make contracts` (E-1) writes this to
/// `contracts/openapi.json`; consumers pin it via the bundle manifest's
/// contract version.
///
/// # Panics
///
/// Panics if the generated `OpenAPI` document fails to serialize (a static
/// schema — a build regression, never a runtime input).
#[must_use = "the contract document is only useful when written out"]
pub fn openapi_json() -> String {
    let (_router, openapi_document) = openapi_router().split_for_parts();
    enrich_openapi(openapi_document.to_json().expect("OpenAPI must serialize"))
}

/// Builds the public `/api/v1` router. Every route except
/// [`PUBLIC_PATHS`] requires an admitted credential.
///
/// # Panics
///
/// Panics if the generated `OpenAPI` document fails to serialize (a static
/// schema — a build regression, never a runtime input).
#[must_use = "the returned router must be installed on a server"]
pub fn router(state: Arc<ApiState>) -> Router {
    let (router, openapi_document) = openapi_router().split_for_parts();
    let openapi_json =
        Arc::new(enrich_openapi(openapi_document.to_json().expect("OpenAPI must serialize")));
    router
        .route("/api/v1/openapi.json", get(openapi))
        .merge(Scalar::with_url("/scalar", openapi_document))
        .fallback(not_routed)
        .layer(Extension(openapi_json))
        .layer(from_fn_with_state(Arc::clone(&state), placement_headers))
        .layer(from_fn_with_state(Arc::clone(&state), authenticate))
        .with_state(state)
}

/// Adds the database's placement contract to every database-scoped response.
///
/// Handlers resolve the registry before touching a database, but many of them
/// return hand-rendered success or typed error responses from several branches.
/// Keeping this at the HTTP boundary makes the D-58 requirement uniform for
/// documents, queries, indexes, and backups alike. Optional feature routes
/// are mounted only when their durable adapter is composed.
/// deleted record still has a placement record, so it remains publishable;
/// only a missing record (or unavailable control plane) leaves the response
/// unchanged.
async fn placement_headers(
    State(state): State<Arc<ApiState>>,
    request: Request,
    next: Next,
) -> Response {
    let database = database_of(request.uri().path()).map(str::to_owned);
    let mut response = next.run(request).await;
    let Some(database) = database else {
        return response;
    };
    let Some(control) = state.control.as_ref() else {
        return response;
    };
    let Ok(Some(record)) = control.get(&database) else {
        return response;
    };
    if let (Ok(placement), Ok(residency)) = (
        HeaderValue::from_str(&record.placement_header()),
        HeaderValue::from_str(record.residency_header()),
    ) {
        let headers = response.headers_mut();
        headers.entry(PLACEMENT_HEADER).or_insert(placement);
        headers.entry(RESIDENCY_HEADER).or_insert(residency);
    }
    response
}

/// Deny-all credential gate: no configured key material admits nothing.
///
/// The gate never logs the credential's bytes and never compares against
/// material it does not hold; a present-but-malformed header is its own
/// typed error so clients can tell "forgot the header" from "sent garbage".
/// Past admission, the principal's scope is enforced per request (D-44):
/// routes naming a database need the matching verb on that database, and
/// deployment-level mutations need an every-database admin scope. A
/// JWT-shaped bearer is denied unless a parent-composed BYO-JWT verifier
/// admits it; this member never parses JWT claims in its API-key gate.
async fn authenticate(
    State(state): State<Arc<ApiState>>,
    mut request: Request,
    next: Next,
) -> Response {
    if PUBLIC_PATHS.contains(&request.uri().path()) {
        return next.run(request).await;
    }
    // Only the immutable reactive delivery paths may use the edge proof.
    // Attach, REST, WebSocket, and every unknown path remain bearer-only.
    if request.method() == Method::GET
        && is_reactive_delivery_path(request.uri().path())
        && crate::reactive_api::verify_origin_proof(
            request.headers(),
            request.method(),
            request.uri(),
            &state.origin_proof_keys,
            unix_seconds(),
        )
    {
        request.extensions_mut().insert(crate::reactive_api::OriginProofVerified);
        return next.run(request).await;
    }
    let Some(keys) = state.keys.clone() else {
        return ApiError::unauthorized().into_response();
    };
    let token = request
        .headers()
        .get(axum::http::header::AUTHORIZATION)
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.strip_prefix("Bearer "));
    let Some(token) = token else {
        return ApiError::malformed_credential().into_response();
    };
    let admitted = keys.admit(token);
    match admitted {
        Ok(principal) => {
            let verb = required_verb(request.method(), request.uri().path());
            if let Some(database) = database_of(request.uri().path()) {
                if !principal.scope().admits(verb, database) {
                    return ApiError::forbidden(&format!(
                        "{} on database '{database}'",
                        verb_name(verb)
                    ))
                    .into_response();
                }
                if let Some(collection) = collection_of(request.uri().path())
                    && !principal.scope().admits_collection(verb, database, collection)
                {
                    return ApiError::forbidden(&format!(
                        "{} on collection '{collection}'",
                        verb_name(verb)
                    ))
                    .into_response();
                }
            } else if (verb == Verbs::ADMIN
                || (request.method() == Method::GET && request.uri().path() == "/api/v1/databases"))
                && !principal.scope().admits_global(Verbs::ADMIN)
            {
                return ApiError::forbidden("deployment administration").into_response();
            }
            request.extensions_mut().insert(principal.clone());
            let mut response = next.run(request).await;
            if let Ok(label) = HeaderValue::from_str(principal.label()) {
                response.headers_mut().insert("x-terrascale-principal", label);
            }
            response
        }
        Err(error) => error.into_response(),
    }
}

/// The two immutable delivery routes are the only routes on which the sync
/// edge may substitute an origin proof for a bearer API key.
fn is_reactive_delivery_path(path: &str) -> bool {
    let Some(rest) = path.strip_prefix("/api/v1/reactive/views/") else { return false };
    let fields = rest.split('/').collect::<Vec<_>>();
    match fields.as_slice() {
        [view, generation, kind] => {
            !view.is_empty() && generation.parse::<u64>().is_ok() && *kind == "log"
        }
        [view, generation, kind, chunk] => {
            !view.is_empty()
                && generation.parse::<u64>().is_ok()
                && *kind == "snapshot"
                && *chunk == "0"
        }
        _ => false,
    }
}

fn collection_of(path: &str) -> Option<&str> {
    let rest = path.strip_prefix("/api/v1/databases/")?;
    let mut fields = rest.split('/');
    let _database = fields.next()?;
    if fields.next()? != "collections" {
        return None;
    }
    let collection = fields.next()?;
    (!collection.is_empty()).then_some(collection)
}

fn unix_seconds() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |duration| duration.as_secs())
}

/// The verb one request needs: reads ride `READ`, document writes ride
/// `WRITE`, and database lifecycle, indexes, schema, webhooks, and exports
/// ride `ADMIN` (the [`crate::api_keys::Verbs`] contract). Unknown mutating
/// paths fail closed toward `ADMIN`.
fn required_verb(method: &Method, path: &str) -> Verbs {
    if *method == Method::GET || *method == Method::HEAD {
        return Verbs::READ;
    }
    // The shared view attach is every authorized client's read path (D-14).
    if path == "/api/v1/reactive/views" {
        return Verbs::READ;
    }
    if let Some(rest) = path.strip_prefix("/api/v1/databases/") {
        if rest.ends_with("/sql") {
            return Verbs::READ;
        }
        if rest.contains("/documents") {
            return Verbs::WRITE;
        }
        return Verbs::ADMIN;
    }
    Verbs::ADMIN
}

/// The database one request names, if its path carries one
/// (`/api/v1/databases/{name}[/...]`). Global routes (list, create,
/// health, regions) name none.
fn database_of(path: &str) -> Option<&str> {
    let rest = path.strip_prefix("/api/v1/databases/")?;
    let name = rest.split('/').next()?;
    if name.is_empty() { None } else { Some(name) }
}

/// The verb name the `forbidden` envelope carries.
fn verb_name(verb: Verbs) -> &'static str {
    if verb == Verbs::READ {
        "read"
    } else if verb == Verbs::WRITE {
        "write"
    } else {
        "administer"
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

/// Serves the `OpenAPI` contract generated while registering the routes.
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
                .map(|text| RegionCatalog::production().parse(text).ok())
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
            // stream child for it from its first moment. The plan comes
            // from the fleet's per-database registry — Free until the
            // billing lane supplies the database's plan (D-25 retention,
            // fail-closed).
            if record.placement.contains(&state.region)
                && let Some(catalog) = state.catalog.clone()
            {
                if catalog.host_for(&state.scope.0, &state.scope.1, name).is_err() {
                    let _ = control.soft_delete(name);
                    return ApiError::storage_failure().into_response();
                }
                let location = catalog.location_for(&state.scope.0, &state.scope.1, name);
                let plan = state.backups.plan_for(name);
                if let Err(error) = attach_backup(
                    &state.backups,
                    name,
                    &location,
                    &state.backup_root,
                    &plan,
                    state.region,
                ) {
                    let _ = control.soft_delete(name);
                    return ApiError::backup(error).into_response();
                }
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
    let host = match document_host(&state, &name) {
        Ok(host) => host,
        Err(error) => return error.into_response(),
    };
    if let Err(error) = check_read_guard(&state, &name, terrabase::observability::Operation::Read) {
        return error.into_response();
    }
    let engine = host.engine();
    let Ok(scope) = document_scope(engine) else {
        return ApiError::control_unavailable().into_response();
    };
    let Ok(snapshot) = engine.read_snapshot(&scope) else {
        return ApiError::storage_failure().into_response();
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
            if let Err(error) = account_direct_read(&state, &name, response_bytes(body.len())) {
                return error.into_response();
            }
            json_response(StatusCode::OK, body)
        }
        Err(_) => ApiError::storage_failure().into_response(),
    }
}

/// Lists one collection's index descriptors: field, state, and the rows
/// the governed backfill still owes (D-31/D-42).
#[utoipa::path(get, path = "/api/v1/databases/{name}/collections/{collection}/indexes", responses((status = 200, description = "Indexes")))]
async fn list_indexes(
    State(state): State<Arc<ApiState>>,
    Path((name, collection)): Path<(String, String)>,
) -> Response {
    let host = match document_host(&state, &name) {
        Ok(host) => host,
        Err(error) => return error.into_response(),
    };
    if let Err(error) = check_read_guard(&state, &name, terrabase::observability::Operation::Read) {
        return error.into_response();
    }
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
            if let Err(error) = account_direct_read(&state, &name, response_bytes(body.len())) {
                return error.into_response();
            }
            json_response(StatusCode::OK, body)
        }
        Err(_) => ApiError::storage_failure().into_response(),
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
    let host = match document_host(&state, &name) {
        Ok(host) => host,
        Err(error) => return error.into_response(),
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
        Err(_) => ApiError::storage_failure().into_response(),
    }
}

/// Lists one collection's documents in canonical key order, with bounded
/// paging (`?limit=`, `?after=` by identifier).
#[utoipa::path(get, path = "/api/v1/databases/{name}/collections/{collection}/documents", responses((status = 200, description = "Documents")))]
async fn list_documents(
    State(state): State<Arc<ApiState>>,
    Path((name, collection)): Path<(String, String)>,
    extract::Query(paging): extract::Query<DocumentPaging>,
) -> Response {
    let host = match document_host(&state, &name) {
        Ok(host) => host,
        Err(error) => return error.into_response(),
    };
    if let Err(error) = check_read_guard(&state, &name, terrabase::observability::Operation::Read) {
        return error.into_response();
    }
    let engine = host.engine();
    let Ok(scope) = document_scope(engine) else {
        return ApiError::control_unavailable().into_response();
    };
    let Ok(collection) = CollectionName::try_new(&collection) else {
        return invalid_identifier("collection name", &collection).into_response();
    };
    let Ok(snapshot) = engine.read_snapshot(&scope) else {
        return ApiError::storage_failure().into_response();
    };
    let limit = paging.limit.unwrap_or(100).clamp(1, 1000);
    let Ok(documents) = engine.snapshot_documents_in_collection(&scope, &snapshot, &collection)
    else {
        return ApiError::storage_failure().into_response();
    };
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
    if let Err(error) = account_direct_read(&state, &name, response_bytes(body.len())) {
        return error.into_response();
    }
    json_response(StatusCode::OK, body)
}

/// The documents list's paging parameters.
#[derive(serde::Deserialize)]
struct DocumentPaging {
    limit: Option<usize>,
    after: Option<String>,
}

/// Requested public durability. The local-stack host can fsync locally;
/// it cannot manufacture a global receipt from configured topology.
/// Production serving remains gated on the complete RF=2 Member adapter.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum Durability {
    /// The regional class; this local-stack adapter fsyncs its sole replica.
    Regional,
    /// A global receipt, requiring an installed replication coordinator.
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

    fn local_level(self) -> Result<LocalDurability, ApiError> {
        match self {
            Self::Regional => Ok(LocalDurability::Synced),
            Self::Global => Err(ApiError {
                status: StatusCode::SERVICE_UNAVAILABLE,
                code: "global_durability_unavailable",
                message: "the serving host has no global replication receipt provider".into(),
                fix: "retry when the member's replication coordinator is available".into(),
                placement: None,
            }),
        }
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
    let identity = match headers.get("idempotency-key") {
        Some(value) => {
            let key = value.to_str().map_err(|_| ApiError::invalid_header("Idempotency-Key"))?;
            format!("{kind}-idem-{key}")
        }
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
    let host = match document_host(&state, &name) {
        Ok(host) => host,
        Err(error) => return error.into_response(),
    };
    let engine = host.engine();
    let Ok(scope) = document_scope(engine) else {
        return ApiError::control_unavailable().into_response();
    };
    let Ok(collection) = CollectionName::try_new(&collection) else {
        return invalid_identifier("collection name", &collection).into_response();
    };
    let operations = match parse_batch_operations(&body) {
        Ok(operations) => operations,
        Err(error) => return error.into_response(),
    };
    let durability = match Durability::of_header(&headers).and_then(Durability::local_level) {
        Ok(durability) => durability,
        Err(error) => return error.into_response(),
    };
    let operation = match write_operation(&headers, "api-batch") {
        Ok(operation) => operation,
        Err(error) => return error.into_response(),
    };
    let mut session = match open_session(engine, &scope, &headers) {
        Ok(session) => session,
        Err(error) => return error.into_response(),
    };
    let snapshot = match session.read_snapshot() {
        Ok(snapshot) => snapshot,
        Err(error) => return session_error(error).into_response(),
    };
    let Ok(mut transaction) = engine.begin(&scope, snapshot, operation) else {
        return ApiError::storage_failure().into_response();
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
            if let Err(error) = session.read_snapshot() {
                return session_error(error).into_response();
            }
            with_session(&mut response, session.session_token());
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
            fix: "check whether it landed; retry the identical request with its original Idempotency-Key".into(),
            placement: None,
        }
        .into_response(),
    }
}

/// Decodes the bounded operation list before opening a write transaction.
fn parse_batch_operations(body: &str) -> Result<Vec<serde_json::Value>, ApiError> {
    let mut request: serde_json::Value = serde_json::from_str(body).map_err(|_| ApiError {
        status: StatusCode::BAD_REQUEST,
        code: "invalid_json",
        message: "the batch body is not JSON".into(),
        fix: "send an object with an operations array of put/patch/delete ops".into(),
        placement: None,
    })?;
    let operations = request.as_object_mut().and_then(|object| object.remove("operations"));
    let Some(serde_json::Value::Array(operations)) = operations else {
        return Err(ApiError {
            status: StatusCode::BAD_REQUEST,
            code: "missing_operations",
            message: "the batch names no operations".into(),
            fix: "send an operations array with at least one op".into(),
            placement: None,
        });
    };
    if operations.is_empty() || operations.len() > 1000 {
        return Err(ApiError {
            status: StatusCode::UNPROCESSABLE_ENTITY,
            code: "batch_size_out_of_bounds",
            message: "a batch is 1..=1000 operations".into(),
            fix: "split the batch into groups of at most 1000 operations".into(),
            placement: None,
        });
    }
    Ok(operations)
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

/// Maps an engine session failure to a secret-free HTTP refusal.
fn session_error(error: SessionError) -> ApiError {
    match error {
        SessionError::SessionToken(_) => invalid_session_token(),
        SessionError::StaleReplica(region) => ApiError {
            status: StatusCode::CONFLICT,
            code: "stale_replica",
            message: "this member has not applied the session frontier".into(),
            fix: format!("retry through the session's safe Region ({})", region.region()),
            placement: None,
        },
        _ => ApiError::storage_failure(),
    }
}

fn invalid_session_token() -> ApiError {
    ApiError {
        status: StatusCode::BAD_REQUEST,
        code: "invalid_session_token",
        message: "X-TerraScale-Session is not a valid opaque session token".into(),
        fix: "send the exact base64url token returned by a successful request".into(),
        placement: None,
    }
}

/// Decodes the unpadded base64url spelling used by the REST session header.
/// Canonical re-encoding is checked by [`decode_session_header`], so alternate
/// spellings and padding never become equivalent credentials.
fn decode_base64url(value: &str) -> Option<Vec<u8>> {
    let bytes = value.as_bytes();
    if bytes.is_empty() || bytes.len() % 4 == 1 || bytes.contains(&b'=') {
        return None;
    }
    let mut output = Vec::with_capacity(bytes.len().saturating_mul(3) / 4);
    let mut index = 0;
    while index < bytes.len() {
        let remaining = bytes.len() - index;
        let a = base64url_value(bytes[index])?;
        let b = base64url_value(bytes[index + 1])?;
        output.push((a << 2) | (b >> 4));
        if remaining > 2 {
            let c = base64url_value(bytes[index + 2])?;
            output.push((b << 4) | (c >> 2));
            if remaining > 3 {
                let d = base64url_value(bytes[index + 3])?;
                output.push((c << 6) | d);
            }
        }
        index += remaining.min(4);
    }
    Some(output)
}

fn base64url_value(byte: u8) -> Option<u8> {
    match byte {
        b'A'..=b'Z' => Some(byte - b'A'),
        b'a'..=b'z' => Some(byte - b'a' + 26),
        b'0'..=b'9' => Some(byte - b'0' + 52),
        b'-' => Some(62),
        b'_' => Some(63),
        _ => None,
    }
}

fn encode_base64url(bytes: &[u8]) -> String {
    const ALPHABET: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    let mut output = String::with_capacity(bytes.len().saturating_mul(4).div_ceil(3));
    for chunk in bytes.chunks(3) {
        let a = chunk[0];
        output.push(ALPHABET[usize::from(a >> 2)] as char);
        let b = chunk.get(1).copied().unwrap_or(0);
        output.push(ALPHABET[usize::from((a << 4 | b >> 4) & 0x3f)] as char);
        if chunk.len() > 1 {
            let c = chunk.get(2).copied().unwrap_or(0);
            output.push(ALPHABET[usize::from((b << 2 | c >> 6) & 0x3f)] as char);
        }
        if chunk.len() > 2 {
            output.push(ALPHABET[usize::from(chunk[2] & 0x3f)] as char);
        }
    }
    output
}

fn decode_session_header(
    headers: &axum::http::HeaderMap,
) -> Result<Option<SessionToken>, ApiError> {
    let Some(value) = headers.get(&SESSION_HEADER) else { return Ok(None) };
    let value = value.to_str().map_err(|_| invalid_session_token())?;
    if value.len() > MAX_SESSION_HEADER_BYTES {
        return Err(invalid_session_token());
    }
    let bytes = decode_base64url(value).ok_or_else(invalid_session_token)?;
    let token = SessionToken::decode(&bytes).map_err(|_| invalid_session_token())?;
    if encode_base64url(&token.encode()) != value {
        return Err(invalid_session_token());
    }
    Ok(Some(token))
}

fn open_session<'engine>(
    engine: &'engine terrabase::Engine,
    scope: &terrabase::types::TenantScope,
    headers: &axum::http::HeaderMap,
) -> Result<Session<'engine>, ApiError> {
    let token = decode_session_header(headers)?;
    Session::open(
        engine,
        scope,
        token.map_or_else(SessionOptions::new, |token| {
            SessionOptions::new().with_session_token(token)
        }),
    )
    .map_err(session_error)
}

/// Emits the session frontier after a successful request. The session API
/// observes the durable multi-origin frontier, so this never substitutes the
/// global storage sequence for an origin sequence.
fn with_session(response: &mut Response, token: &SessionToken) {
    let encoded = encode_base64url(&token.encode());
    if let Ok(value) = HeaderValue::from_str(&encoded) {
        response.headers_mut().insert(SESSION_HEADER.clone(), value);
    }
}

fn refresh_session(
    session: &mut Session<'_>,
    collection: &CollectionName,
    id: &DocumentId,
) -> Result<SessionToken, ApiError> {
    // Writes commit through an Engine transaction outside the read session.
    // A create may have cached this key as missing, which `refresh` cannot
    // reload because it requires a tracked document. Clear either cached
    // state and read the committed key before returning its causal frontier.
    session.evict(collection, id);
    session.load(collection, id).map_err(session_error)?;
    Ok(session.session_token().clone())
}

/// One document's route context: the host engine for the named database.
pub(crate) fn document_host(
    state: &ApiState,
    name: &str,
) -> Result<std::sync::Arc<terrabase_tbep::member_host::MemberHost>, ApiError> {
    // The catalog is an open-file cache, not the source of database
    // existence or placement. Resolve the durable registry first so an
    // unknown/deleted name can never cause `host_for` to create a local file.
    let Some(control) = state.control.clone() else {
        return Err(ApiError::control_unavailable());
    };
    let record = match control.get(name) {
        Ok(Some(record)) if record.deleted => return Err(ApiError::database_not_found(name)),
        Ok(Some(record)) => record,
        Ok(None) => return Err(ApiError::database_not_found(name)),
        Err(error) => return Err(ApiError::control(error)),
    };
    if !record.placement.contains(&state.region) {
        return Err(ApiError::misdirected(&record.placement, record.residency_header()));
    }
    let Some(catalog) = state.catalog.clone() else {
        return Err(ApiError::control_unavailable());
    };
    catalog.host_for(&state.scope.0, &state.scope.1, name).map_err(|_| ApiError::storage_failure())
}

/// Admits one read or query against the database's D-64 rate guard (E-10).
/// A spent budget answers the typed 429 with its fix and records the
/// bounded admission rejection (no per-database label, D-72).
pub(crate) fn check_read_guard(
    state: &ApiState,
    database: &str,
    operation: terrabase::observability::Operation,
) -> Result<(), ApiError> {
    state.guards.check_read_query(database).map_err(|rejection| {
        state.metrics.record_admission_rejection(
            operation,
            terrabase::observability::AdmissionReason::Capacity,
        );
        ApiError::abuse_guard(&rejection)
    })
}

/// Accounts served direct-read bytes against the D-64 monthly bandwidth
/// guard and the billable egress counter (D-66). The byte that would cross
/// the ceiling answers the typed 429 instead of the body; nothing over the
/// guard is billed.
pub(crate) fn account_direct_read(
    state: &ApiState,
    database: &str,
    bytes: u64,
) -> Result<(), ApiError> {
    if let Err(rejection) = state.guards.record_direct_read_bytes(database, bytes) {
        state.metrics.record_admission_rejection(
            terrabase::observability::Operation::Read,
            terrabase::observability::AdmissionReason::Capacity,
        );
        return Err(ApiError::abuse_guard(&rejection));
    }
    state.metrics.record_egress_bytes(
        terrabase::observability::Region::from_code(state.region.as_str()),
        terrabase::observability::EgressKind::Read,
        bytes,
    );
    Ok(())
}

/// Converts a response byte length without a truncating cast.
pub(crate) fn response_bytes(len: usize) -> u64 {
    u64::try_from(len).unwrap_or(u64::MAX)
}

/// Attaches a database stream using the member's registered Region. The
/// lower-level convenience method predates placement-aware members and
/// defaults to GRU; public API paths must carry the actual placement.
fn attach_backup(
    backups: &crate::backup::BackupStreams,
    database: &str,
    location: &std::path::Path,
    backup_root: &std::path::Path,
    plan: &str,
    region: RegionCode,
) -> std::io::Result<()> {
    use terrabase::encryption::{DatabaseKeyVersion, KeyProviderIdentity};
    let config = crate::backup::BackupAttachConfig::new(
        location.to_path_buf(),
        crate::backup::BackupDestination::File { root: backup_root.to_path_buf(), region },
        crate::backup::retention_for_plan(plan).to_owned(),
        crate::backup::BackupEncryptionConfig::new(
            KeyProviderIdentity::LocalFile,
            DatabaseKeyVersion::generate(),
        ),
    );
    backups.set_plan(database, plan);
    backups.attach_with_config(database, region, config)
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

/// Enforces `If-Match` on a single-document write against the live revision
/// (D-20 optimistic concurrency): absent means go ahead, `*` means the
/// document must exist, and a quoted revision must equal the live one. The
/// revision probe never touches the body blob.
fn check_if_match(
    headers: &axum::http::HeaderMap,
    transaction: &terrabase::Transaction<'_>,
    collection: &CollectionName,
    id: &DocumentId,
) -> Result<(), ApiError> {
    let Some(header) = headers.get(axum::http::header::IF_MATCH) else {
        return Ok(());
    };
    let expected = header.to_str().map_err(|_| ApiError::invalid_header("If-Match"))?;
    let live = transaction
        .revision_in_collection(collection, id)
        .map_err(|_| ApiError::storage_failure())?;
    if expected == "*" {
        return live.is_some().then_some(()).ok_or_else(|| {
            ApiError::precondition_failed("If-Match: * needs the document to exist")
        });
    }
    match live {
        Some(revision) if format!("\"{revision}\"") == expected => Ok(()),
        _ => Err(ApiError::precondition_failed("the ETag no longer matches the live revision")),
    }
}

/// One document's live revision for a response `ETag`: the same probe the
/// GET path answers `If-None-Match` from, so a write's `ETag` always names
/// what the next `GET` serves.
fn live_revision(
    engine: &terrabase::Engine,
    scope: &terrabase::types::TenantScope,
    collection: &CollectionName,
    id: &DocumentId,
) -> Result<String, ApiError> {
    let snapshot = engine.read_snapshot(scope).map_err(|_| ApiError::storage_failure())?;
    let operation = crate::read_operation::next().map_err(|()| ApiError::storage_failure())?;
    let transaction =
        engine.begin(scope, snapshot, operation).map_err(|_| ApiError::storage_failure())?;
    transaction
        .revision_in_collection(collection, id)
        .map_err(|_| ApiError::storage_failure())?
        .ok_or_else(ApiError::storage_failure)
}

/// Returns one document with its `ETag`; `If-None-Match` is answered from
/// the revision column alone — the body blob is never decoded on a 304.
/// `?at=<unix-millis>` reads the document as of that moment from a
/// throwaway point-in-time restore (S-06 time-travel, cached per
/// (database, second) with a bounded budget).
#[utoipa::path(get, path = "/api/v1/databases/{name}/collections/{collection}/documents/{id}", responses((status = 200, description = "Document")))]
async fn get_document(
    State(state): State<Arc<ApiState>>,
    Path((name, collection, id)): Path<(String, String, String)>,
    headers: axum::http::HeaderMap,
    extract::Query(at): extract::Query<AtQuery>,
) -> Response {
    let host = match document_host(&state, &name) {
        Ok(host) => host,
        Err(error) => return error.into_response(),
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
    if let Err(error) = check_read_guard(&state, &name, terrabase::observability::Operation::Read) {
        return error.into_response();
    }
    let mut session = if headers.contains_key(&SESSION_HEADER) {
        match open_session(engine, &scope, &headers) {
            Ok(session) => Some(session),
            Err(error) => return error.into_response(),
        }
    } else {
        None
    };
    if let Some(at_millis) = at.at {
        if let Some(session) = &mut session
            && let Err(error) = session.load(&collection, &id)
        {
            return session_error(error).into_response();
        }
        return time_travel_document(
            &state,
            &name,
            &collection,
            &id,
            at_millis,
            &headers,
            session.as_ref().map(terrabase::Session::session_token),
        );
    }
    let snapshot = match &mut session {
        Some(session) => session.read_snapshot().map_err(session_error),
        None => engine.read_snapshot(&scope).map_err(|_| ApiError::storage_failure()),
    };
    let snapshot = match snapshot {
        Ok(snapshot) => snapshot,
        Err(error) => return error.into_response(),
    };
    let Ok(operation) = crate::read_operation::next() else {
        return ApiError::storage_failure().into_response();
    };
    let Ok(transaction) = engine.begin(&scope, snapshot, operation) else {
        return ApiError::storage_failure().into_response();
    };
    let Ok(revision) = transaction.revision_in_collection(&collection, &id) else {
        return ApiError::storage_failure().into_response();
    };
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
        // never touched (S-03's gate). No body bytes leave, so only the
        // rate guard (checked above) applies here.
        let mut response = StatusCode::NOT_MODIFIED.into_response();
        if let Ok(etag) = HeaderValue::from_str(&etag) {
            response.headers_mut().insert(axum::http::header::ETAG, etag);
        }
        if let Some(session) = &session {
            with_session(&mut response, session.session_token());
        }
        return response;
    }
    let document = match transaction.read_in_collection(&collection, &id) {
        Ok(Some(document)) => document,
        Ok(None) => return document_not_found(collection.as_str(), id.as_str()).into_response(),
        Err(_) => return ApiError::storage_failure().into_response(),
    };
    let mut body = String::with_capacity(256);
    push_value(&mut body, document.value());
    if let Err(error) = account_direct_read(&state, &name, response_bytes(body.len())) {
        return error.into_response();
    }
    let mut response = json_response(StatusCode::OK, body);
    if let Ok(etag) = HeaderValue::from_str(&etag) {
        response.headers_mut().insert(axum::http::header::ETAG, etag);
    }
    if let Some(session) = &session {
        with_session(&mut response, session.session_token());
    }
    response
}

/// The time-travel query parameter: `?at=<unix-millis>` names the moment
/// the document is read as of. Absent means the live database.
#[derive(serde::Deserialize)]
struct AtQuery {
    at: Option<i64>,
}

/// Serves `GET .../documents/{id}?at=<unix-millis>` from a throwaway
/// point-in-time restore cached per (database, second) with a bounded
/// budget (S-06 (d)). The moment must sit inside the plan's PITR retention
/// (D-25); older moments name their typed remedy instead of restoring
/// bytes Litestream no longer keeps.
fn time_travel_document(
    state: &ApiState,
    name: &str,
    collection: &CollectionName,
    id: &DocumentId,
    at_millis: i64,
    headers: &axum::http::HeaderMap,
    session: Option<&SessionToken>,
) -> Response {
    if at_millis < 0 {
        return invalid_at("it is negative");
    }
    let at_seconds = u64::try_from(at_millis.div_euclid(1000)).unwrap_or(0);
    let now_seconds = unix_now_seconds();
    if at_seconds > now_seconds.saturating_add(60) {
        return invalid_at("it is in the future");
    }
    // The retention gate reads the database's plan from the fleet's
    // per-database registry — Free until the billing lane supplies it
    // (D-25 retention, unknown plans fail closed to the Free window).
    let plan = state.backups.plan_for(name);
    let retention_days = crate::backup::retention_days_for_plan(&plan);
    if at_seconds.saturating_add(retention_days.saturating_mul(86_400)) < now_seconds {
        return ApiError {
            status: StatusCode::UNPROCESSABLE_ENTITY,
            code: "pitr_outside_retention",
            message: format!(
                "?at= is older than the plan's PITR retention ({retention_days} days)"
            ),
            fix: "read a newer moment, or move to a plan with longer retention (Free 1 / \
                  Starter 7 / Pro 30 / Scale 35 days)"
                .into(),
            placement: None,
        }
        .into_response();
    }
    let Ok(tenant) = local_tenant() else {
        return ApiError::control_unavailable().into_response();
    };
    match state.timetravel.read_at(name, &tenant, at_seconds, collection, id) {
        Err(error) => ApiError::backup(error).into_response(),
        Ok(None) => document_not_found(collection.as_str(), id.as_str()).into_response(),
        Ok(Some(read)) => {
            let revision = &read.revision;
            let etag = format!("\"{revision}\"");
            if headers
                .get(axum::http::header::IF_NONE_MATCH)
                .and_then(|value| value.to_str().ok())
                .is_some_and(|present| present == etag)
            {
                let mut response = StatusCode::NOT_MODIFIED.into_response();
                if let Ok(etag) = HeaderValue::from_str(&etag) {
                    response.headers_mut().insert(axum::http::header::ETAG, etag);
                }
                if let Some(session) = session {
                    with_session(&mut response, session);
                }
                return response;
            }
            let mut body = String::with_capacity(256);
            push_value(&mut body, &read.value);
            if let Err(error) = account_direct_read(state, name, response_bytes(body.len())) {
                return error.into_response();
            }
            let mut response = json_response(StatusCode::OK, body);
            if let Ok(etag) = HeaderValue::from_str(&etag) {
                response.headers_mut().insert(axum::http::header::ETAG, etag);
            }
            if let Some(session) = session {
                with_session(&mut response, session);
            }
            response
        }
    }
}

/// The typed refusal for an unusable `?at=` timestamp.
fn invalid_at(detail: &str) -> Response {
    ApiError {
        status: StatusCode::BAD_REQUEST,
        code: "invalid_at",
        message: format!("the ?at= timestamp is unusable ({detail})"),
        fix: "send ?at=<unix-millis> naming a moment inside the plan's PITR retention".into(),
        placement: None,
    }
    .into_response()
}

/// Wall-clock unix seconds for the `?at=` retention window.
fn unix_now_seconds() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |elapsed| elapsed.as_secs())
}

/// Creates or replaces one document; the `ETag` of the result names the new
/// revision.
#[allow(clippy::too_many_lines, reason = "one document write path validates, writes, and answers")]
#[utoipa::path(put, path = "/api/v1/databases/{name}/collections/{collection}/documents/{id}", responses((status = 200, description = "Document stored")))]
async fn put_document(
    State(state): State<Arc<ApiState>>,
    Path((name, collection, id)): Path<(String, String, String)>,
    headers: axum::http::HeaderMap,
    body: String,
) -> Response {
    let host = match document_host(&state, &name) {
        Ok(host) => host,
        Err(error) => return error.into_response(),
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
    let mut session = match open_session(engine, &scope, &headers) {
        Ok(session) => session,
        Err(error) => return error.into_response(),
    };
    if let Err(error) = session.load(document.collection(), document.id()) {
        return session_error(error).into_response();
    }
    let Ok(snapshot) = engine.read_snapshot(&scope) else {
        return ApiError::storage_failure().into_response();
    };
    let operation = match write_operation(&headers, "api-put") {
        Ok(operation) => operation,
        Err(error) => return error.into_response(),
    };
    let durability = match Durability::of_header(&headers).and_then(Durability::local_level) {
        Ok(durability) => durability,
        Err(error) => return error.into_response(),
    };
    let Ok(mut transaction) = engine.begin(&scope, snapshot, operation) else {
        return ApiError::storage_failure().into_response();
    };
    if let Err(error) = check_if_match(&headers, &transaction, document.collection(), document.id())
    {
        return error.into_response();
    }
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
            Err(_) => return ApiError::storage_failure().into_response(),
        }
    };
    let probe = (document.collection().clone(), document.id().clone());
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
        CommitOutcome::Committed(_receipt) => {
            let revision = match live_revision(engine, &scope, &probe.0, &probe.1) {
                Ok(revision) => revision,
                Err(error) => return error.into_response(),
            };
            let mut response =
                json_response(StatusCode::OK, format!("{{\"revision\":\"{revision}\"}}"));
            if let Ok(etag) = HeaderValue::from_str(&format!("\"{revision}\"")) {
                response.headers_mut().insert(axum::http::header::ETAG, etag);
            }
            let token = match refresh_session(&mut session, &probe.0, &probe.1) {
                Ok(token) => token,
                Err(error) => return error.into_response(),
            };
            with_session(&mut response, &token);
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
            fix: "check whether it landed; retry the identical request with its original Idempotency-Key".into(),
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
    let host = match document_host(&state, &name) {
        Ok(host) => host,
        Err(error) => return error.into_response(),
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
    let value = match parse_merge_patch(&body) {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    };
    let mut session = match open_session(engine, &scope, &headers) {
        Ok(session) => session,
        Err(error) => return error.into_response(),
    };
    let operation = match write_operation(&headers, "api-patch") {
        Ok(operation) => operation,
        Err(error) => return error.into_response(),
    };
    let durability = match Durability::of_header(&headers).and_then(Durability::local_level) {
        Ok(durability) => durability,
        Err(error) => return error.into_response(),
    };
    let snapshot = match session.read_snapshot() {
        Ok(snapshot) => snapshot,
        Err(error) => return session_error(error).into_response(),
    };
    let Ok(mut transaction) = engine.begin(&scope, snapshot, operation) else {
        return ApiError::storage_failure().into_response();
    };
    if let Err(error) = check_if_match(&headers, &transaction, &collection, &id) {
        return error.into_response();
    }
    let existing = match transaction.read_in_collection(&collection, &id) {
        Ok(Some(existing)) => existing,
        Ok(None) => {
            return document_not_found(collection.as_str(), id.as_str()).into_response();
        }
        Err(_) => return ApiError::storage_failure().into_response(),
    };
    let mut merged = existing.value().clone();
    merge_patch(&mut merged, &json_value(&value));
    let probe = (collection.clone(), id.clone());
    let patched = Document::new(id.clone(), collection.clone(), merged);
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
        CommitOutcome::Committed(_receipt) => {
            let revision = match live_revision(engine, &scope, &probe.0, &probe.1) {
                Ok(revision) => revision,
                Err(error) => return error.into_response(),
            };
            let mut response =
                json_response(StatusCode::OK, format!("{{\"revision\":\"{revision}\"}}"));
            if let Ok(etag) = HeaderValue::from_str(&format!("\"{revision}\"")) {
                response.headers_mut().insert(axum::http::header::ETAG, etag);
            }
            let token = match refresh_session(&mut session, &collection, &id) {
                Ok(token) => token,
                Err(error) => return error.into_response(),
            };
            with_session(&mut response, &token);
            response
        }
        _ => ApiError {
            status: StatusCode::INTERNAL_SERVER_ERROR,
            code: "commit_failed",
            message: "the engine did not commit the patch".into(),
            fix: "check whether it landed; retry the identical request with its original Idempotency-Key".into(),
            placement: None,
        }
        .into_response(),
    }
}

fn parse_merge_patch(body: &str) -> Result<serde_json::Value, ApiError> {
    serde_json::from_str(body).map_err(|_| ApiError {
        status: StatusCode::BAD_REQUEST,
        code: "invalid_json",
        message: "the request body is not a JSON merge patch".into(),
        fix: "send the JSON value to merge into the document".into(),
        placement: None,
    })
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
    let host = match document_host(&state, &name) {
        Ok(host) => host,
        Err(error) => return error.into_response(),
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
    if let Err(error) = check_read_guard(&state, &name, terrabase::observability::Operation::Read) {
        return error.into_response();
    }
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
            if let Err(error) = account_direct_read(&state, &name, response_bytes(body.len())) {
                return error.into_response();
            }
            json_response(StatusCode::OK, body)
        }
        Err(_) => ApiError::storage_failure().into_response(),
    }
}

/// Deletes one document (tombstone; the history route can explain it).
#[utoipa::path(delete, path = "/api/v1/databases/{name}/collections/{collection}/documents/{id}", responses((status = 200, description = "Document deleted")))]
async fn delete_document(
    State(state): State<Arc<ApiState>>,
    Path((name, collection, id)): Path<(String, String, String)>,
    headers: axum::http::HeaderMap,
) -> Response {
    let host = match document_host(&state, &name) {
        Ok(host) => host,
        Err(error) => return error.into_response(),
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
    let mut session = match open_session(engine, &scope, &headers) {
        Ok(session) => session,
        Err(error) => return error.into_response(),
    };
    let present = match session.load(&collection, &id) {
        Ok(document) => document.is_some(),
        Err(error) => return session_error(error).into_response(),
    };
    let Ok(snapshot) = engine.read_snapshot(&scope) else {
        return ApiError::storage_failure().into_response();
    };
    let operation = match write_operation(&headers, "api-delete") {
        Ok(operation) => operation,
        Err(error) => return error.into_response(),
    };
    let durability = match Durability::of_header(&headers).and_then(Durability::local_level) {
        Ok(durability) => durability,
        Err(error) => return error.into_response(),
    };
    let Ok(mut transaction) = engine.begin(&scope, snapshot, operation) else {
        return ApiError::storage_failure().into_response();
    };
    if let Err(error) = check_if_match(&headers, &transaction, &collection, &id) {
        return error.into_response();
    }
    if !present {
        return document_not_found(collection.as_str(), id.as_str()).into_response();
    }
    let expected = if has_idempotency_key(&headers) {
        ExpectedVersion::Any
    } else {
        match transaction.read_in_collection(&collection, &id) {
            Ok(Some(existing)) => ExpectedVersion::Exact(existing.metadata().version()),
            Ok(None) => {
                return document_not_found(collection.as_str(), id.as_str()).into_response();
            }
            Err(_) => return ApiError::storage_failure().into_response(),
        }
    };
    // delete_in_collection stages without a pre-read (the caller owns
    // existence, and this route read the target above); the commit's
    // conflict rejection is the version check.
    transaction.delete_in_collection(&collection, id.clone(), expected);
    match transaction.commit(durability) {
        CommitOutcome::Committed(_) => {
            let token = match refresh_session(&mut session, &collection, &id) {
                Ok(token) => token,
                Err(error) => return error.into_response(),
            };
            let mut response = json_response(StatusCode::NO_CONTENT, String::new());
            with_session(&mut response, &token);
            response
        }
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

/// Executes one read-only SQL text against a database: the public query
/// surface (D-54/D-78) over the SQL front end and executor. The body carries
/// the text, optional out-of-band parameters, and the `explain` request
/// option (the plan, not the rows).
fn sql_controls(
    request: &serde_json::Value,
    headers: &axum::http::HeaderMap,
) -> Result<(terrabase::Deadline, terrabase::Cancellation), ApiError> {
    const DEFAULT_TIMEOUT_MS: u64 = 30_000;
    const MAX_TIMEOUT_MS: u64 = 30_000;
    let body_timeout = match request.get("timeout_ms") {
        Some(value) => Some(
            value.as_u64().ok_or_else(|| invalid_query_control("timeout_ms must be an integer"))?,
        ),
        None => None,
    };
    let timeout = headers
        .get("x-terrascale-timeout-ms")
        .and_then(|value| value.to_str().ok())
        .map(str::parse::<u64>)
        .transpose()
        .map_err(|_| invalid_query_control("X-TerraScale-Timeout-Ms must be an integer"))?
        .or(body_timeout)
        .unwrap_or(DEFAULT_TIMEOUT_MS);
    if timeout == 0 || timeout > MAX_TIMEOUT_MS {
        return Err(invalid_query_control("timeout_ms must be between 1 and 30000"));
    }
    let deadline = terrabase::Deadline::after(std::time::Duration::from_millis(timeout))
        .map_err(|_| invalid_query_control("timeout_ms is not representable"))?;
    let cancelled = headers
        .get("x-terrascale-cancelled")
        .and_then(|value| value.to_str().ok())
        .is_some_and(|value| value.eq_ignore_ascii_case("true"))
        || request.get("cancelled").and_then(serde_json::Value::as_bool).unwrap_or(false);
    let cancellation = terrabase::Cancellation::new();
    if cancelled {
        cancellation.cancel();
    }
    Ok((deadline, cancellation))
}

fn invalid_query_control(detail: &str) -> ApiError {
    ApiError {
        status: StatusCode::BAD_REQUEST,
        code: "invalid_query_control",
        message: format!("the SQL operation control is invalid ({detail})"),
        fix: "use timeout_ms from 1 through 30000 and a boolean cancelled flag".into(),
        placement: None,
    }
}

#[utoipa::path(post, path = "/api/v1/databases/{name}/sql", responses((status = 200, description = "SQL result")))]
#[allow(
    clippy::too_many_lines,
    reason = "SQL request validation and result rendering stay together"
)]
async fn execute_sql(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
    headers: axum::http::HeaderMap,
    body: String,
) -> Response {
    let host = match document_host(&state, &name) {
        Ok(host) => host,
        Err(error) => return error.into_response(),
    };
    let engine = host.engine();
    let Ok(scope) = document_scope(engine) else {
        return ApiError::control_unavailable().into_response();
    };
    if let Err(error) = check_read_guard(&state, &name, terrabase::observability::Operation::Query)
    {
        return error.into_response();
    }
    let request: serde_json::Value = match serde_json::from_str(&body) {
        Ok(value) => value,
        Err(_) => {
            return ApiError {
                status: StatusCode::BAD_REQUEST,
                code: "invalid_json",
                message: "the sql body is not JSON".into(),
                fix: "send {\"sql\":\"SELECT ...\",\"parameters\":{…},\"explain\":false}".into(),
                placement: None,
            }
            .into_response();
        }
    };
    let (deadline, cancellation) = match sql_controls(&request, &headers) {
        Ok(controls) => controls,
        Err(error) => return error.into_response(),
    };
    let Some(text) =
        request.get("sql").or_else(|| request.get("statement")).and_then(serde_json::Value::as_str)
    else {
        return ApiError {
            status: StatusCode::BAD_REQUEST,
            code: "missing_sql",
            message: "the request names no SQL text".into(),
            fix: "add \"sql\": \"SELECT ... FROM <collection> ...\" to the request body".into(),
            placement: None,
        }
        .into_response();
    };
    let validated = match terrabase::SqlQueryText::try_new(text) {
        Ok(validated) => validated,
        Err(terrabase::QueryInputError::EmptyText) => {
            return ApiError {
                status: StatusCode::BAD_REQUEST,
                code: "missing_sql",
                message: "the request names no SQL text".into(),
                fix: "add \"sql\": \"SELECT ... FROM <collection> ...\" to the request body".into(),
                placement: None,
            }
            .into_response();
        }
        Err(terrabase::QueryInputError::InvalidIdentifier) => {
            return ApiError {
                status: StatusCode::BAD_REQUEST,
                code: "invalid_sql_identifier",
                message: "a binding name is not a plain identifier".into(),
                fix: "use a plain identifier for the binding name".into(),
                placement: None,
            }
            .into_response();
        }
        Err(terrabase::QueryInputError::UnsupportedGrammar) => {
            return ApiError {
                status: StatusCode::BAD_REQUEST,
                code: "invalid_sql",
                message: "the text is not read-only SQL".into(),
                fix: "send one SELECT, WITH ... SELECT, or EXPLAIN SELECT statement".into(),
                placement: None,
            }
            .into_response();
        }
    };
    let mut parameters = terrabase::query::sql::SqlParameters::new();
    if let Some(fields) = request
        .get("parameters")
        .or_else(|| request.get("params"))
        .and_then(serde_json::Value::as_object)
    {
        for (parameter, value) in fields {
            if let Err(error) = parameters.insert(parameter, json_value(value)) {
                return ApiError::sql(&error).into_response();
            }
        }
    }
    if let Err(error) = terrabase::query::sql::validate_parameters(text, &parameters) {
        return ApiError::sql(&error).into_response();
    }
    let store = engine.queries();
    // The public SQL route carries the same bounded deadline and cancellation
    // controls as the typed query seam; the plan retains them through
    // execution, including execute-only SQL operators.
    let plan = match store.compile(terrabase::QueryInput::sql_bounded_with_parameters(
        validated,
        deadline,
        cancellation.clone(),
        parameters,
    )) {
        Ok(plan) => plan,
        Err(error) => return ApiError::query(error).into_response(),
    };
    if deadline.is_expired() {
        return ApiError::query(terrabase::query::QueryError::Deadline).into_response();
    }
    if cancellation.is_cancelled() {
        return ApiError::query(terrabase::query::QueryError::Cancelled).into_response();
    }
    if request.get("explain").and_then(serde_json::Value::as_bool) == Some(true) {
        let explained = render_explained_plan(&plan);
        if let Err(error) = account_direct_read(&state, &name, response_bytes(explained.len())) {
            return error.into_response();
        }
        return json_response(StatusCode::OK, explained);
    }
    let Ok(snapshot) = engine.read_snapshot(&scope) else {
        return ApiError::storage_failure().into_response();
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
                if plan.projection().selected().is_some() {
                    rendered.push('{');
                    for (field_index, (field, value)) in row.projected().iter().enumerate() {
                        if field_index > 0 {
                            rendered.push(',');
                        }
                        rendered.push('"');
                        push_json_escaped(&mut rendered, field);
                        rendered.push_str("\":");
                        push_value(&mut rendered, value);
                    }
                    rendered.push('}');
                } else {
                    push_value(&mut rendered, row.document().value());
                }
                rendered.push('}');
            }
            rendered.push_str("]}");
            if let Err(error) = account_direct_read(&state, &name, response_bytes(rendered.len())) {
                return error.into_response();
            }
            json_response(StatusCode::OK, rendered)
        }
        Err(error) => ApiError::query(error).into_response(),
    }
}

/// Renders the explained plan of one compiled SQL query (the `explain`
/// request option).
// ponytail: access/indexes/dependencies join when the S-02 auto-index audit
// reports descriptor state (the documented shape in full).
fn render_explained_plan(plan: &terrabase::query::QueryPlan) -> String {
    let mut explained = String::from("{\"dialect\":\"sql/sqlite-readonly\",\"collection\":");
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

/// Backups ride Litestream per file (S-06): readiness is an artifact
/// fact, never "the child process is alive".
#[utoipa::path(get, path = "/api/v1/databases/{name}/backups", responses((status = 200, description = "Backup readiness")))]
async fn list_backups(State(state): State<Arc<ApiState>>, Path(name): Path<String>) -> Response {
    if let Err(error) = document_host(&state, &name) {
        return error.into_response();
    }
    if let Err(error) = check_read_guard(&state, &name, terrabase::observability::Operation::Read) {
        return error.into_response();
    }
    let ready = state.backups.is_ready(&name);
    let body = format!("{{\"ready\":{ready},\"streaming\":{}}}", state.backups.attached(&name));
    if let Err(error) = account_direct_read(&state, &name, response_bytes(body.len())) {
        return error.into_response();
    }
    json_response(StatusCode::OK, body)
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
    restore_or_branch(state, name, body, RestoreFlavor::Restore)
}

/// `POST /databases/{name}/branches` — restore plus register with parent
/// metadata (D-38/D-39).
#[utoipa::path(post, path = "/api/v1/databases/{name}/branches", responses((status = 201, description = "Branch created")))]
async fn create_branch(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
    body: String,
) -> Response {
    restore_or_branch(state, name, body, RestoreFlavor::Branch)
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
#[allow(
    clippy::needless_pass_by_value,
    reason = "the axum handlers move owned state, name, body, and flavor into this shared path"
)]
fn restore_or_branch(
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
        // D-52: a restore is bytes at rest in a new place — the target
        // inherits the source's placement lock and cannot widen it. An
        // unknown source fails closed before any record is made.
        RestoreFlavor::Restore => {
            let inherited = match control.lock_for_derived(&name, &placement) {
                Ok(inherited) => inherited,
                Err(error) => return ApiError::control(error).into_response(),
            };
            control.create(target, &placement, inherited.as_deref())
        }
        // The branch lock check lives in `create_branch` itself (D-52).
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
        Ok(()) => {
            // The new database is a main.db from its first moment: it gets
            // its own stream under the source's plan (Free until the
            // billing lane supplies plans; D-25 retention, fail-closed).
            let plan = state.backups.plan_for(&name);
            if let Err(error) = attach_backup(
                &state.backups,
                target,
                &location,
                &state.backup_root,
                &plan,
                state.region,
            ) {
                let _ = control.soft_delete(target);
                return ApiError::backup(error).into_response();
            }
            record_response(StatusCode::CREATED, &record)
        }
        Err(error) => {
            let _ = control.soft_delete(target);
            ApiError::backup(error).into_response()
        }
    }
}

/// Lists the database's branches: records whose parent names it.
#[utoipa::path(get, path = "/api/v1/databases/{name}/branches", responses((status = 200, description = "Database branches")))]
async fn list_branches(State(state): State<Arc<ApiState>>, Path(name): Path<String>) -> Response {
    if let Err(error) = document_host(&state, &name) {
        return error.into_response();
    }
    if let Err(error) = check_read_guard(&state, &name, terrabase::observability::Operation::Read) {
        return error.into_response();
    }
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
    if let Err(error) = account_direct_read(&state, &name, response_bytes(body.len())) {
        return error.into_response();
    }
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
    use super::SESSION_HEADER;
    use super::decode_session_header;
    use super::encode_base64url;
    use super::openapi_json;
    use super::router;
    use axum::body::Body;
    use axum::http::HeaderValue;
    use axum::http::Request;
    use axum::http::StatusCode;
    use axum::http::header;
    use axum::response::IntoResponse;
    use std::sync::Arc;
    use terrabase::SessionEpoch;
    use terrabase::SessionToken;
    use terrabase::observability::MetricsRegistry;
    use terrabase::regions::RegionCode;
    use terrabase::types::RegionId;
    use tower::util::ServiceExt;

    fn state() -> Arc<ApiState> {
        let backups = Arc::new(crate::backup::BackupStreams::open_member());
        let backup_root = std::env::temp_dir().join("terrabase-api-test-backup-root");
        let timetravel =
            Arc::new(crate::backup::TimeTravelCache::new(Arc::clone(&backups), &backup_root));
        Arc::new(ApiState {
            region: RegionCode::GRU,
            metrics: Arc::new(MetricsRegistry::new()),
            keys: None,
            control: None,
            catalog: None,
            scope: ("terrabase-local".into(), "local".into()),
            backups,
            backup_root,
            timetravel,
            guards: Arc::new(crate::guards::AbuseGuards::default()),
            origin_proof_keys: Arc::new(crate::reactive_api::OriginProofKeyRing::default()),
            capability_keys: Arc::new(crate::reactive_api::CapabilityKeyRing::conformance()),
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
            std::time::Duration::from_hours(1),
        )
        .expect("catalog opens");
        let backups = Arc::new(crate::backup::BackupStreams::open_member());
        let timetravel = Arc::new(crate::backup::TimeTravelCache::new(
            Arc::clone(&backups),
            &dir.join("backup-timetravel"),
        ));
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
            backups,
            backup_root: dir.join("backup"),
            timetravel,
            guards: Arc::new(crate::guards::AbuseGuards::default()),
            origin_proof_keys: Arc::new(crate::reactive_api::OriginProofKeyRing::default()),
            capability_keys: Arc::new(crate::reactive_api::CapabilityKeyRing::conformance()),
        })
    }

    #[test]
    fn session_header_is_opaque_canonical_and_multi_origin() {
        let mut token = SessionToken::for_epoch(7).expect("epoch");
        token.advance(RegionId::try_new("GRU").expect("origin"), 11).expect("frontier");
        token.advance(RegionId::try_new("FRA").expect("origin"), 19).expect("frontier");
        let encoded = encode_base64url(&token.encode());
        let mut headers = axum::http::HeaderMap::new();
        headers.insert(SESSION_HEADER.clone(), HeaderValue::from_str(&encoded).expect("header"));
        assert_eq!(decode_session_header(&headers).expect("decode"), Some(token));

        // Decimal legacy values, padding, malformed alphabet, and tampering
        // are all rejected before a request reaches an engine.
        for value in ["1".to_owned(), format!("{encoded}="), "!!!!".to_owned()] {
            headers.insert(SESSION_HEADER.clone(), HeaderValue::from_str(&value).expect("header"));
            assert!(decode_session_header(&headers).is_err(), "rejected {value}");
        }
        let mut tampered = encoded.into_bytes();
        tampered[0] = if tampered[0] == b'A' { b'B' } else { b'A' };
        headers.insert(SESSION_HEADER.clone(), HeaderValue::from_bytes(&tampered).expect("header"));
        assert!(decode_session_header(&headers).is_err(), "checksum rejects tampering");
    }

    #[test]
    fn session_epoch_mismatch_stays_fail_closed() {
        let mut first = SessionToken::for_epoch(7).expect("epoch");
        let second = SessionToken::for_epoch(8).expect("epoch");
        assert!(first.merge(&second).is_err());
        let error = super::session_error(terrabase::SessionError::SessionToken(
            terrabase::SessionTokenError::EpochMismatch {
                expected: SessionEpoch::new(7).expect("epoch"),
                observed: SessionEpoch::new(8).expect("epoch"),
            },
        ));
        assert_eq!(error.status, StatusCode::BAD_REQUEST);
        assert_eq!(error.code, "invalid_session_token");
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
        let body = axum::body::to_bytes(response.into_body(), 1_048_576).await.expect("body");
        let document: serde_json::Value = serde_json::from_slice(&body).expect("valid JSON");
        let paths = document.get("paths").and_then(serde_json::Value::as_object).expect("paths");
        for (path, route) in [
            ("/api/v1/databases/{name}/sql", "post"),
            ("/api/v1/databases/{name}/backups", "get"),
            ("/api/v1/databases/{name}/branches", "get"),
            ("/api/v1/databases/{name}/reactive", "get"),
            ("/api/v1/reactive/views", "post"),
            ("/api/v1/reactive/views/{view}/{generation}/snapshot/{chunk}", "get"),
            ("/api/v1/reactive/views/{view}/{generation}/log", "get"),
        ] {
            assert!(
                paths.get(path).is_some_and(|entry| entry.get(route).is_some()),
                "the contract lists {route} {path}"
            );
        }
        for path in [
            "/api/v1/databases/{name}/usage",
            "/api/v1/databases/{name}/webhooks",
            "/api/v1/databases/{name}/exports",
            "/api/v1/databases/{name}/collections/{collection}/schema",
            "/api/v1/databases/{name}/collections/{collection}/indexes/recommendations",
            "/api/v1/databases/{name}/collections/{collection}/indexes/{field}",
        ] {
            assert!(!paths.contains_key(path), "unavailable route {path} is not published");
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

    #[test]
    #[allow(clippy::too_many_lines, reason = "one route contract, one test")]
    fn openapi_describes_runtime_bodies_errors_and_security() {
        let document: serde_json::Value =
            serde_json::from_str(&openapi_json()).expect("valid JSON");
        let components =
            document.get("components").and_then(serde_json::Value::as_object).expect("components");
        assert!(
            components
                .get("schemas")
                .and_then(serde_json::Value::as_object)
                .is_some_and(|schemas| !schemas.is_empty())
        );
        assert_eq!(
            components
                .get("securitySchemes")
                .and_then(serde_json::Value::as_object)
                .and_then(|schemes| schemes.get("bearerAuth"))
                .and_then(|scheme| scheme.get("scheme"))
                .and_then(serde_json::Value::as_str),
            Some("bearer")
        );
        let schemas =
            components.get("schemas").and_then(serde_json::Value::as_object).expect("schemas");
        assert!(schemas.contains_key("SqlExplainResponse"));
        assert!(schemas.contains_key("SqlResultResponse"));
        assert_eq!(schemas["ReactiveEnvelope"]["minItems"], 7);
        assert!(!schemas.contains_key("ReactivePollRequest"));
        assert!(document["paths"].get("/api/v1/databases/{name}/reactive/poll").is_none());
        let paths = document.get("paths").and_then(serde_json::Value::as_object).expect("paths");
        let body_operations = [
            "create_database",
            "create_index",
            "batch_documents",
            "put_document",
            "patch_document",
            "execute_sql",
            "attach_view",
            "restore_backup",
            "create_branch",
        ];
        let mut operation_count = 0;
        for item in paths.values() {
            let item = item.as_object().expect("path item");
            for (method, operation) in item {
                if !matches!(method.as_str(), "get" | "post" | "put" | "patch" | "delete") {
                    continue;
                }
                operation_count += 1;
                let operation = operation.as_object().expect("operation");
                let id = operation
                    .get("operationId")
                    .and_then(serde_json::Value::as_str)
                    .expect("operation id");
                assert_eq!(
                    operation.get("security"),
                    Some(&serde_json::json!([{ "bearerAuth": [] }])),
                    "security for {id}"
                );
                if body_operations.contains(&id) {
                    let body = operation
                        .get("requestBody")
                        .and_then(serde_json::Value::as_object)
                        .expect("request body");
                    let expected_required = Some(true);
                    assert_eq!(
                        body.get("required").and_then(serde_json::Value::as_bool),
                        expected_required,
                        "body required for {id}"
                    );
                    assert!(
                        body.get("content")
                            .and_then(serde_json::Value::as_object)
                            .is_some_and(|content| !content.is_empty()),
                        "body media for {id}"
                    );
                }
                if id == "view_log" {
                    let parameters = operation
                        .get("parameters")
                        .and_then(serde_json::Value::as_array)
                        .expect("view log parameters");
                    assert!(parameters.iter().any(|parameter| {
                        parameter.get("name") == Some(&serde_json::json!("offset"))
                            && parameter.get("in") == Some(&serde_json::json!("query"))
                            && parameter.get("required") == Some(&serde_json::json!(true))
                    }));
                }
                let responses = operation
                    .get("responses")
                    .and_then(serde_json::Value::as_object)
                    .expect("responses");
                for (status, response) in responses {
                    let response = response.as_object().expect("response");
                    let code = status.parse::<u16>().expect("numeric response");
                    if code >= 400 {
                        assert!(
                            response
                                .get("content")
                                .and_then(serde_json::Value::as_object)
                                .is_some_and(|content| content.contains_key("application/json")),
                            "typed error body for {id} {status}"
                        );
                    } else if code != 101 && code != 204 {
                        assert!(
                            response
                                .get("content")
                                .and_then(serde_json::Value::as_object)
                                .is_some_and(|content| !content.is_empty()),
                            "success body for {id} {status}"
                        );
                    }
                }
            }
        }
        assert_eq!(operation_count, 26, "registered route count changed");
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

    /// The E-10 envelope: a spent D-64 budget answers 429 with the stable
    /// code and a fix, never a bare status.
    #[tokio::test]
    async fn abuse_guard_refusals_answer_429_with_code_and_fix() {
        let guards = crate::guards::AbuseGuards::new();
        guards.set_plan(
            "guarded",
            crate::guards::Plan::Custom(crate::guards::AbuseLimits {
                read_query_rps: 1,
                direct_read_bandwidth_bytes_per_month: 1,
                distinct_views: 1,
                ws_connections: 1,
                webhook_endpoints: 1,
            }),
        );
        assert!(guards.check_read_query("guarded").is_ok());
        let rejection = guards.check_read_query("guarded").expect_err("the second request refuses");
        let response = ApiError::abuse_guard(&rejection).into_response();
        assert_eq!(response.status(), StatusCode::TOO_MANY_REQUESTS);
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(
            body.contains("\"code\":\"abuse_guard_exceeded\""),
            "the envelope codes the refusal: {body}"
        );
        assert!(body.contains("\"fix\":"), "every error carries a fix (D-56): {body}");
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
            std::fs::read_dir(dir).map_or(0, |entries| entries.flatten().count())
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

    /// The S-06 `?at=` retention gate reads the database's plan from the
    /// fleet registry: Free refuses a ten-day-old moment, Pro lets it
    /// through to the restore (which refuses without a stream — the gate
    /// passed), and an unknown plan fails closed to Free. No Litestream
    /// needed: the gate answers before any restore runs.
    #[test]
    fn time_travel_retention_gate_honors_the_database_plan() {
        use terrabase::types::{CollectionName, DocumentId};
        let state = state();
        let collection = CollectionName::try_new("orders").expect("the collection parses");
        let id = DocumentId::try_new("orders/first").expect("the document id parses");
        let headers = axum::http::HeaderMap::new();
        let ten_days_ago_millis =
            super::unix_now_seconds().saturating_sub(10 * 86_400).cast_signed() * 1000;

        let refused = super::time_travel_document(
            &state,
            "planned",
            &collection,
            &id,
            ten_days_ago_millis,
            &headers,
            None,
        )
        .status();
        assert_eq!(refused, StatusCode::UNPROCESSABLE_ENTITY, "Free refuses the old moment");

        state.backups.set_plan("planned", "pro");
        let gated = super::time_travel_document(
            &state,
            "planned",
            &collection,
            &id,
            ten_days_ago_millis,
            &headers,
            None,
        )
        .status();
        assert_ne!(
            gated,
            StatusCode::UNPROCESSABLE_ENTITY,
            "Pro admits the moment past the Free window"
        );

        state.backups.set_plan("planned", "team");
        let closed = super::time_travel_document(
            &state,
            "planned",
            &collection,
            &id,
            ten_days_ago_millis,
            &headers,
            None,
        )
        .status();
        assert_eq!(closed, StatusCode::UNPROCESSABLE_ENTITY, "unknown plans fail closed");
    }

    /// The journey's write step: PUT one document, answer the status.
    async fn write_document(
        router: &axum::Router,
        auth: &str,
        uri: &str,
        body: &'static str,
    ) -> StatusCode {
        router
            .clone()
            .oneshot(
                Request::put(uri)
                    .header("authorization", auth)
                    .body(Body::from(body))
                    .expect("request"),
            )
            .await
            .expect("answers")
            .status()
    }

    /// The journey's read step: GET one URI, answer the status and body.
    async fn read_document_body(
        router: &axum::Router,
        auth: &str,
        uri: String,
    ) -> (StatusCode, String) {
        let response = router
            .clone()
            .oneshot(
                Request::get(uri)
                    .header("authorization", auth)
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        let status = response.status();
        let bytes = axum::body::to_bytes(response.into_body(), 8192).await.expect("body");
        (status, String::from_utf8(bytes.to_vec()).expect("utf-8"))
    }

    /// The S-06 time-travel journey through the router: write, wait,
    /// write, then `?at=` between the writes serves the old value while
    /// the live read serves the new one. Skips when Litestream is not
    /// packaged on this machine.
    #[tokio::test]
    async fn document_time_travel_at_returns_the_old_value() {
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
        let unix_now = super::unix_now_seconds;

        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases")
                    .header("authorization", auth)
                    .body(Body::from(r#"{"name":"tt","placement":["GRU"],"residency":null}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::CREATED);
        assert!(fleet.wait_until_ready("tt", std::time::Duration::from_secs(30)), "ready");

        // The write's WAL segment must ship before a restore can see it:
        // the segment count must grow past the pre-write baseline.
        let wal = backup_root.join("tt").join("ltx").join("0");
        let shipped = |dir: &std::path::Path| {
            std::fs::read_dir(dir).map_or(0, |entries| entries.flatten().count())
        };
        let wait_for_ship = |baseline: usize, what: &str| {
            let deadline = std::time::Instant::now() + std::time::Duration::from_secs(30);
            while shipped(&wal) <= baseline {
                assert!(
                    std::time::Instant::now() < deadline,
                    "the {what} write shipped to the replica"
                );
                std::thread::sleep(std::time::Duration::from_millis(100));
            }
        };
        let baseline = shipped(&wal);
        let first = write_document(
            &router,
            auth,
            "/api/v1/databases/tt/collections/orders/documents/item-1",
            r#"{"value":1}"#,
        )
        .await;
        assert!(first.is_success(), "the first write lands: {first}");
        wait_for_ship(baseline, "first");

        // The restore point sits strictly between the writes (a write is
        // addressable a sync after it leaves the building).
        std::thread::sleep(std::time::Duration::from_millis(2_500));
        let at = unix_now() + 1;
        while unix_now() <= at {
            std::thread::sleep(std::time::Duration::from_millis(100));
        }
        let between = shipped(&wal);
        let second = write_document(
            &router,
            auth,
            "/api/v1/databases/tt/collections/orders/documents/item-1",
            r#"{"value":2}"#,
        )
        .await;
        assert!(second.is_success(), "the second write lands: {second}");
        wait_for_ship(between, "second");

        // Time-travel serves the old value; the repeat hits the cache.
        let at_millis = at * 1000;
        for _ in 0..2 {
            let (status, body) = read_document_body(
                &router,
                auth,
                format!("/api/v1/databases/tt/collections/orders/documents/item-1?at={at_millis}"),
            )
            .await;
            assert_eq!(status, StatusCode::OK, "the past serves");
            assert!(body.contains("\"value\":1"), "the old value serves: {body}");
            assert!(!body.contains("\"value\":2"), "the new value stays out: {body}");
        }

        // The live read serves the new value.
        let (status, body) = read_document_body(
            &router,
            auth,
            "/api/v1/databases/tt/collections/orders/documents/item-1".to_owned(),
        )
        .await;
        assert!(status.is_success());
        assert!(body.contains("\"value\":2"), "the live value serves: {body}");

        // A moment older than the plan's retention names its remedy.
        let (status, body) = read_document_body(
            &router,
            auth,
            "/api/v1/databases/tt/collections/orders/documents/item-1?at=1000".to_owned(),
        )
        .await;
        assert_eq!(status, StatusCode::UNPROCESSABLE_ENTITY);
        assert!(body.contains("pitr_outside_retention"), "the typed refusal: {body}");

        // A future moment is unusable.
        let future_millis = (unix_now() + 3_600) * 1000;
        let (status, _) = read_document_body(
            &router,
            auth,
            format!("/api/v1/databases/tt/collections/orders/documents/item-1?at={future_millis}"),
        )
        .await;
        assert_eq!(status, StatusCode::BAD_REQUEST);
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
        let put_status = response.status();
        let put_body = axum::body::to_bytes(response.into_body(), 8192).await.expect("body");
        let put_body = String::from_utf8(put_body.to_vec()).expect("utf-8");
        assert_eq!(put_status, StatusCode::OK, "the put commits: {put_body}");
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
    async fn scopes_bind_verb_and_database_in_the_gate() {
        use crate::api_keys::Scope;
        use crate::api_keys::Verbs;
        use axum::body::Body;
        use axum::http::Request;
        let auth = "authorization";
        let bootstrap = "Bearer tskey_test_abcdefghijklmnop";
        let state = state_with_keys_and_control();
        let keys = state.keys.clone().expect("key material");
        keys.register(
            "tskey_test_readonly_abcdefghij",
            "readonly",
            Scope::every_database(Verbs::READ),
        );
        keys.register(
            "tskey_test_prodonly_abcdefghij",
            "prod-only",
            Scope::database("prod", Verbs::ALL),
        );
        let router = router(state);
        let readonly = "Bearer tskey_test_readonly_abcdefghij";
        let prod_only = "Bearer tskey_test_prodonly_abcdefghij";
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases")
                    .header(auth, bootstrap)
                    .body(Body::from(r#"{"name":"prod","placement":["GRU"],"residency":null}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::CREATED);
        let document = "/api/v1/databases/prod/collections/users/documents/scoped-1";
        let response = router
            .clone()
            .oneshot(
                Request::put(document)
                    .header(auth, bootstrap)
                    .body(Body::from(r#"{"n":1}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        // A read-only key reads but cannot write: the gate answers the
        // typed forbidden envelope with its remedy.
        let response = router
            .clone()
            .oneshot(
                Request::get(document).header(auth, readonly).body(Body::empty()).expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        let response = router
            .clone()
            .oneshot(
                Request::put(document)
                    .header(auth, readonly)
                    .body(Body::from(r#"{"n":2}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::FORBIDDEN);
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("\"code\":\"forbidden\""), "{body}");
        assert!(body.contains("\"fix\":"), "{body}");
        // A prod-scoped key cannot touch another database's route — the
        // gate refuses before the handler can oracle its existence.
        let response = router
            .clone()
            .oneshot(
                Request::get("/api/v1/databases/other/collections")
                    .header(auth, prod_only)
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::FORBIDDEN);
        // ... but serves its own database.
        let response = router
            .clone()
            .oneshot(
                Request::get("/api/v1/databases/prod/collections")
                    .header(auth, prod_only)
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        // Database creation needs an every-database admin scope: a
        // database-scoped admin key cannot mint databases.
        let response = router
            .oneshot(
                Request::post("/api/v1/databases")
                    .header(auth, prod_only)
                    .body(Body::from(r#"{"name":"sneaky","placement":["GRU"],"residency":null}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::FORBIDDEN);
    }

    #[tokio::test]
    async fn jwt_shaped_bearers_are_denied_without_a_configured_verifier() {
        use axum::body::Body;
        use axum::http::Request;
        let router = router(state_with_keys_and_control());
        let response = router
            .oneshot(
                Request::get("/api/v1/databases/prod")
                    .header("authorization", "Bearer eyJhbGciOiJub25lIn0.e30.c2ln")
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::UNAUTHORIZED);
    }

    #[tokio::test]
    #[allow(clippy::too_many_lines, reason = "one route contract, one test")]
    async fn if_match_guards_single_document_writes() {
        use axum::body::Body;
        use axum::http::Request;
        let auth = "authorization";
        let key = "Bearer tskey_test_abcdefghijklmnop";
        let path = "/api/v1/databases/prod/collections/users/documents/m-1";
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
                    .body(Body::from(r#"{"n":1}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        let put_etag = response
            .headers()
            .get("etag")
            .and_then(|value| value.to_str().ok())
            .expect("the put names the revision")
            .to_owned();
        // The write's ETag is what the next GET serves (no stale `-1`).
        let response = router
            .clone()
            .oneshot(Request::get(path).header(auth, key).body(Body::empty()).expect("request"))
            .await
            .expect("answers");
        let get_etag = response
            .headers()
            .get("etag")
            .and_then(|value| value.to_str().ok())
            .expect("the get names the revision")
            .to_owned();
        assert_eq!(put_etag, get_etag, "the write ETag names the live revision");
        // A stale precondition fails closed with its remedy.
        let response = router
            .clone()
            .oneshot(
                Request::put(path)
                    .header(auth, key)
                    .header("if-match", "\"0-0\"")
                    .body(Body::from(r#"{"n":2}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::PRECONDITION_FAILED);
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("\"code\":\"precondition_failed\""), "{body}");
        // The matching precondition commits and re-names the live revision.
        let response = router
            .clone()
            .oneshot(
                Request::patch(path)
                    .header(auth, key)
                    .header("if-match", get_etag.as_str())
                    .body(Body::from(r#"{"n":2}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        let patched_etag = response
            .headers()
            .get("etag")
            .and_then(|value| value.to_str().ok())
            .expect("the patch names the revision")
            .to_owned();
        assert_ne!(patched_etag, get_etag, "the patch advanced the revision");
        // `If-Match: *` guards existence on delete: present deletes, absent
        // refuses instead of inventing a 404-or-success ambiguity.
        let response = router
            .clone()
            .oneshot(
                Request::delete("/api/v1/databases/prod/collections/users/documents/missing")
                    .header(auth, key)
                    .header("if-match", "*")
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::PRECONDITION_FAILED);
        let response = router
            .clone()
            .oneshot(
                Request::delete(path)
                    .header(auth, key)
                    .header("if-match", patched_etag.as_str())
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::NO_CONTENT);
        let response = router
            .oneshot(
                Request::patch(path)
                    .header(auth, key)
                    .header("if-match", "*")
                    .body(Body::from(r#"{"n":3}"#))
                    .expect("patch missing document"),
            )
            .await
            .expect("precondition response");
        assert_eq!(
            response.status(),
            StatusCode::PRECONDITION_FAILED,
            "a missing patch target must fail If-Match before returning 404"
        );
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
        let session =
            decode_session_header(response.headers()).expect("valid session").expect("token");
        assert!(
            session.frontier().entries().next().is_some(),
            "the token covers the committed batch"
        );
        let response = router
            .clone()
            .oneshot(
                Request::get("/api/v1/databases/prod/collections/users/documents/a")
                    .header(auth, key)
                    .header(SESSION_HEADER.clone(), encode_base64url(&session.encode()))
                    .body(Body::empty())
                    .expect("causal read request"),
            )
            .await
            .expect("causal read");
        assert_eq!(response.status(), StatusCode::OK);
        let value: serde_json::Value = serde_json::from_slice(
            &axum::body::to_bytes(response.into_body(), 4096).await.expect("document"),
        )
        .expect("document JSON");
        assert_eq!(value["n"], 1);
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

    #[tokio::test(start_paused = true)]
    #[allow(
        clippy::too_many_lines,
        reason = "the route journey verifies attach, snapshot, and delta bytes"
    )]
    async fn sql_view_delivers_an_immutable_snapshot_and_committed_delta() {
        use terrabase_sync_core::wire::{Message, ResultOperation, decode_envelope};
        let key = "Bearer tskey_test_abcdefghijklmnop";
        let router = router(state_with_keys_and_control());
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases")
                    .header("authorization", key)
                    .body(Body::from(r#"{"name":"prod","placement":["GRU"],"residency":null}"#))
                    .expect("request"),
            )
            .await
            .expect("answer");
        assert_eq!(response.status(), StatusCode::CREATED);
        let put = |id: &str, total: u32| {
            Request::put(format!("/api/v1/databases/prod/collections/orders/documents/{id}"))
                .header("authorization", key)
                .body(Body::from(format!(r#"{{"status":"paid","total":{total}}}"#)))
                .expect("request")
        };
        assert_eq!(
            router.clone().oneshot(put("o-1", 10)).await.expect("put").status(),
            StatusCode::OK
        );
        let attach_request = || {
            Request::post("/api/v1/reactive/views")
            .header("authorization", key)
            .body(Body::from(r#"{"databaseId":"prod","statement":"SELECT total FROM orders WHERE status = 'paid' LIMIT 100"}"#)).expect("request")
        };
        let response = router.clone().oneshot(attach_request()).await.expect("attach");
        let status = response.status();
        let body = axum::body::to_bytes(response.into_body(), 65_536).await.expect("body");
        assert_eq!(status, StatusCode::CREATED, "{}", String::from_utf8_lossy(&body));
        let attached: serde_json::Value = serde_json::from_slice(&body).expect("attach metadata");
        let snapshot_url = attached["snapshot_chunks"][0].as_str().expect("snapshot URL");
        let log_url = attached["log"].as_str().expect("log URL");
        let capability = attached["capability"].as_str().expect("capability");
        let read = |url: &str| {
            Request::get(url)
                .header("authorization", key)
                .header("x-terrascale-view-capability", capability)
                .body(Body::empty())
                .expect("request")
        };
        let response = router.clone().oneshot(read(snapshot_url)).await.expect("snapshot");
        assert_eq!(response.status(), StatusCode::OK);
        let original = axum::body::to_bytes(response.into_body(), 65_536).await.expect("body");
        let Message::Snapshot { snapshot, .. } =
            decode_envelope(&original).expect("wire envelope").message
        else {
            panic!("snapshot envelope")
        };
        assert_eq!(snapshot.items.len(), 1);
        assert_eq!(snapshot.items[0].identity, "o-1");
        let pending = tokio::spawn(router.clone().oneshot(read(log_url)));
        tokio::task::yield_now().await;
        assert!(!pending.is_finished(), "the log waits before the new write");
        assert_eq!(
            router.clone().oneshot(put("o-2", 20)).await.expect("put").status(),
            StatusCode::OK
        );
        assert_eq!(
            router.clone().oneshot(put("o-coalesced", 30)).await.expect("put").status(),
            StatusCode::OK
        );
        tokio::time::advance(std::time::Duration::from_millis(99)).await;
        tokio::task::yield_now().await;
        assert!(!pending.is_finished(), "writes coalesce within the view's 100 ms window");
        tokio::time::advance(std::time::Duration::from_millis(1)).await;
        let response = tokio::time::timeout(std::time::Duration::from_secs(2), pending)
            .await
            .expect("commit wakes the waiting request")
            .expect("request task")
            .expect("log");
        assert_eq!(response.status(), StatusCode::OK);
        let body = axum::body::to_bytes(response.into_body(), 65_536).await.expect("body");
        let Message::Delta { operations, from_revision, to_revision, .. } =
            decode_envelope(&body).expect("wire envelope").message
        else {
            panic!("delta envelope")
        };
        assert!(operations.iter().any(|operation| matches!(operation, ResultOperation::Insert { identity, .. } if identity == "o-2")));
        assert!(operations.iter().any(|operation| matches!(operation, ResultOperation::Insert { identity, .. } if identity == "o-coalesced")));
        assert_eq!(operations.len(), 2, "unchanged rows are not retransmitted");
        let replay_started = tokio::time::Instant::now();
        let replay = tokio::time::timeout(
            std::time::Duration::from_secs(1),
            router.clone().oneshot(read(log_url)),
        )
        .await
        .expect("another reader gets the existing revision immediately")
        .expect("replay");
        assert_eq!(tokio::time::Instant::now(), replay_started, "replay bypasses cadence");
        assert_eq!(replay.status(), StatusCode::OK);
        assert_eq!(axum::body::to_bytes(replay.into_body(), 65_536).await.expect("body"), body);
        assert_eq!(from_revision, snapshot.revision);
        let next_log = format!(
            "/api/v1/reactive/views/{}/{}/log?offset={to_revision}",
            attached["view"].as_str().expect("view"),
            attached["generation"].as_u64().expect("generation")
        );
        let irrelevant =
            Request::put("/api/v1/databases/prod/collections/orders/documents/ignored")
                .header("authorization", key)
                .body(Body::from(r#"{"status":"open","total":99}"#))
                .expect("request");
        assert_eq!(router.clone().oneshot(irrelevant).await.expect("put").status(), StatusCode::OK);
        let waiting = tokio::spawn(router.clone().oneshot(read(&next_log)));
        tokio::task::yield_now().await;
        assert!(!waiting.is_finished(), "an irrelevant write does not emit a result delta");
        let response = router.clone().oneshot(attach_request()).await.expect("reattach");
        assert_eq!(response.status(), StatusCode::CREATED);
        let reattached: serde_json::Value = serde_json::from_slice(
            &axum::body::to_bytes(response.into_body(), 65_536).await.expect("body"),
        )
        .expect("metadata");
        assert_eq!(reattached["view"], attached["view"]);
        assert_eq!(
            reattached["offset"], to_revision,
            "an irrelevant write does not change the result revision"
        );
        let response = router
            .clone()
            .oneshot(read(reattached["snapshot_chunks"][0].as_str().expect("snapshot")))
            .await
            .expect("snapshot");
        assert_eq!(response.status(), StatusCode::OK, "reattachment names an existing snapshot");
        assert_eq!(
            router.clone().oneshot(put("o-3", 30)).await.expect("put").status(),
            StatusCode::OK
        );
        let response =
            router.clone().oneshot(attach_request()).await.expect("reattach after write");
        assert_eq!(response.status(), StatusCode::CREATED);
        tokio::time::advance(std::time::Duration::from_millis(99)).await;
        tokio::task::yield_now().await;
        assert!(!waiting.is_finished(), "reattachment cannot bypass the next emission window");
        tokio::time::advance(std::time::Duration::from_millis(1)).await;
        let response = tokio::time::timeout(std::time::Duration::from_secs(2), waiting)
            .await
            .expect("existing subscriber wakes")
            .expect("task")
            .expect("log");
        assert_eq!(response.status(), StatusCode::OK);
        let bytes = axum::body::to_bytes(response.into_body(), 65_536).await.expect("body");
        let Message::Delta { from_revision, to_revision: latest_revision, operations, .. } =
            decode_envelope(&bytes).expect("delta").message
        else {
            panic!("delta envelope")
        };
        assert_eq!(
            from_revision, to_revision,
            "reattachment preserves existing subscriber continuity"
        );
        assert!(
            operations.iter().any(
                |op| matches!(op, ResultOperation::Insert { identity, .. } if identity == "o-3")
            )
        );
        assert_eq!(operations.len(), 1, "one new row emits one insert");
        let response = router.clone().oneshot(read(snapshot_url)).await.expect("same snapshot");
        assert_eq!(response.status(), StatusCode::OK);
        assert_eq!(
            axum::body::to_bytes(response.into_body(), 65_536).await.expect("body"),
            original,
            "an existing snapshot URL retains identical bytes after writes"
        );
        assert_eq!(
            router.clone().oneshot(put("o-2", 21)).await.expect("update").status(),
            StatusCode::OK
        );
        let update_log = format!(
            "/api/v1/reactive/views/{}/{}/log?offset={latest_revision}",
            attached["view"].as_str().unwrap(),
            attached["generation"].as_u64().unwrap()
        );
        let response = router.oneshot(read(&update_log)).await.expect("updated view");
        assert_eq!(response.status(), StatusCode::OK);
        let bytes = axum::body::to_bytes(response.into_body(), 65_536).await.expect("body");
        let Message::Delta { operations, .. } = decode_envelope(&bytes).unwrap().message else {
            panic!("delta envelope");
        };
        assert!(matches!(operations.as_slice(), [ResultOperation::Update { identity, fields }]
            if identity == "o-2" && fields.iter().any(|field| field.value == b"21")));
    }

    #[tokio::test]
    async fn concurrent_sql_attachments_share_one_seed_query() {
        let state = state_with_keys_and_control();
        let app = router(Arc::clone(&state));
        let key = "Bearer tskey_test_abcdefghijklmnop";
        let response = app
            .clone()
            .oneshot(
                Request::post("/api/v1/databases")
                    .header("authorization", key)
                    .body(Body::from(r#"{"name":"prod","placement":["GRU"],"residency":null}"#))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::CREATED);
        let host = super::document_host(&state, "prod").unwrap();
        let tenant = terrabase::types::TenantId::try_new(&state.scope.0).unwrap();
        let before = host.engine().queries().recent_index_selections(&tenant).len();
        let barrier = Arc::new(std::sync::Barrier::new(8));
        let mut tasks = Vec::new();
        for _ in 0..8 {
            let app = app.clone();
            let barrier = Arc::clone(&barrier);
            let runtime = tokio::runtime::Handle::current();
            tasks.push(tokio::task::spawn_blocking(move || {
                barrier.wait();
                runtime.block_on(app.oneshot(
                    Request::post("/api/v1/reactive/views").header("authorization", key)
                        .body(Body::from(r#"{"databaseId":"prod","statement":"SELECT total FROM orders LIMIT 10"}"#)).unwrap(),
                )).unwrap()
            }));
        }
        let mut first = None;
        for task in tasks {
            let response = task.await.unwrap();
            let status = response.status();
            let body = axum::body::to_bytes(response.into_body(), 65_536).await.unwrap();
            assert_eq!(status, StatusCode::CREATED, "{}", String::from_utf8_lossy(&body));
            let metadata: serde_json::Value = serde_json::from_slice(&body).unwrap();
            let identity = (metadata["view"].clone(), metadata["snapshot_chunks"].clone());
            if let Some(expected) = &first {
                assert_eq!(&identity, expected);
            } else {
                first = Some(identity);
            }
        }
        assert_eq!(
            host.engine().queries().recent_index_selections(&tenant).len(),
            before + 1,
            "concurrent readers initialize one canonical view with one snapshot query"
        );
    }

    #[tokio::test]
    #[allow(clippy::too_many_lines, reason = "one route contract, one test")]
    async fn sql_executes_with_parameters_and_explains_the_plan() {
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
        // Validated SQL with an out-of-band parameter lowers and executes
        // against the database snapshot.
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases/prod/sql")
                    .header(auth, key)
                    .body(Body::from(
                        r#"{"sql":"SELECT * FROM orders WHERE status = :s ORDER BY total DESC","parameters":{"s":"paid"}}"#,
                    ))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        let body = axum::body::to_bytes(response.into_body(), 8192).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("\"matched\":2"), "SQL result: {body}");
        assert!(body.contains("o-3"), "SQL ordering: {body}");
        // Explain of validated text returns the lowered plan.
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases/prod/sql")
                    .header(auth, key)
                    .body(Body::from(
                        r#"{"sql":"SELECT * FROM orders WHERE status = :s ORDER BY total DESC","parameters":{"s":"paid"},"explain":true}"#,
                    ))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases/prod/sql")
                    .header(auth, key)
                    .body(Body::from(
                        r#"{"sql":"SELECT status, COUNT(*) AS count FROM orders GROUP BY status ORDER BY status"}"#,
                    ))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        let body = axum::body::to_bytes(response.into_body(), 8192).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("\"count\":2"), "aggregate SQL result: {body}");
        // Refusals are typed with a fix: a missing body field and a parse
        // error both name their remedy (D-56).
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases/prod/sql")
                    .header(auth, key)
                    .body(Body::from(r#"{"parameters":{"s":"paid"}}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::BAD_REQUEST);
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("missing_sql"), "{body}");
        assert!(body.contains("\"fix\":"), "{body}");
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases/prod/sql")
                    .header(auth, key)
                    .body(Body::from(r#"{"sql":"SELECT * FROM"}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::BAD_REQUEST);
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("invalid_sql"), "{body}");
        // A write through the read-only endpoint fails closed.
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases/prod/sql")
                    .header(auth, key)
                    .body(Body::from(r#"{"sql":"DELETE FROM orders"}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::BAD_REQUEST);
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("invalid_sql"), "{body}");
    }

    #[tokio::test]
    async fn indexes_register_list_and_unavailable_lifecycle_routes_are_absent() {
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
        // D-31 lifecycle routes are not mounted until a durable coordinator
        // adapter is composed; an absent route must not advertise a 501
        // launch operation or mutate any guard state.
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
            assert_eq!(response.status(), StatusCode::NOT_FOUND, "{method} {path}");
        }
    }

    #[tokio::test]
    async fn exports_fail_closed_naming_e8_and_locked_sources_refuse_derived_databases() {
        use axum::body::Body;
        use axum::http::Request;
        let auth = "authorization";
        let key = "Bearer tskey_test_abcdefghijklmnop";
        let router = router(state_with_keys_and_control());
        // An unlocked source used to prove the unavailable export route is
        // absent rather than a published 501 placeholder.
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
        // E-8 has no durable export adapter in this member, so no route is
        // mounted and no launch operation is published.
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases/prod/exports")
                    .header(auth, key)
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::NOT_FOUND);
        // Unknown databases 404 before the stub answers.
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases/unknown/exports")
                    .header(auth, key)
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::NOT_FOUND);
        // A locked source (eu-only in FRA) refuses derived databases on
        // this GRU member: restore and branch cannot widen the lock.
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases")
                    .header(auth, key)
                    .body(Body::from(r#"{"name":"eu","placement":["FRA"],"residency":"eu-only"}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::CREATED);
        for (path, target) in [
            ("/api/v1/databases/eu/backups/restore", "eu-restored"),
            ("/api/v1/databases/eu/branches", "eu-branch"),
        ] {
            let response = router
                .clone()
                .oneshot(
                    Request::post(path)
                        .header(auth, key)
                        .body(Body::from(format!(r#"{{"name":"{target}"}}"#)))
                        .expect("request"),
                )
                .await
                .expect("answers");
            assert_eq!(response.status(), StatusCode::UNPROCESSABLE_ENTITY, "{path}");
            let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
            let body = String::from_utf8(body.to_vec()).expect("utf-8");
            assert!(body.contains("placement_lock_widened"), "{path}: {body}");
        }
        // An unknown restore source fails closed before any record is made.
        let response = router
            .oneshot(
                Request::post("/api/v1/databases/unknown/backups/restore")
                    .header(auth, key)
                    .body(Body::from(r#"{"name":"unknown-restored"}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::NOT_FOUND);
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
    async fn global_durability_requires_replication_receipts_before_mutation() {
        let auth = "Bearer tskey_test_abcdefghijklmnop";
        let router = router(state_with_keys_and_control());
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases")
                    .header("authorization", auth)
                    .body(Body::from(
                        r#"{"name":"prod","placement":["GRU","SFO","FRA"],"residency":null}"#,
                    ))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::CREATED);
        let path = "/api/v1/databases/prod/collections/users/documents/g-1";
        let response = router
            .clone()
            .oneshot(
                Request::put(path)
                    .header("authorization", auth)
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
        let response = router
            .oneshot(
                Request::get(path)
                    .header("authorization", auth)
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(
            response.status(),
            StatusCode::NOT_FOUND,
            "a refused global write never commits locally"
        );
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
        let etag = response.headers().get("etag").expect("write revision").clone();
        let covered = response.headers().get(&SESSION_HEADER).expect("write session").clone();
        let mut future =
            decode_session_header(response.headers()).expect("valid token").expect("token");
        let (origin, sequence) = future
            .frontier()
            .entries()
            .next()
            .map(|(origin, sequence)| (origin.clone(), sequence))
            .expect("write frontier");
        future.advance(origin, sequence + 1).expect("advance future frontier");
        // The write's opaque token is covered locally: the read serves immediately.
        let response = router
            .clone()
            .oneshot(
                Request::get("/api/v1/databases/prod/collections/users/documents/s-1")
                    .header(auth, key)
                    .header(SESSION_HEADER.clone(), covered)
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        // A future sequence (replication that has not happened yet) times
        // out with the typed remedy, never a stale read.
        for path in [
            "/api/v1/databases/prod/collections/users/documents/s-1",
            "/api/v1/databases/prod/collections/users/documents/missing",
        ] {
            let started = std::time::Instant::now();
            let response = router
                .clone()
                .oneshot(
                    Request::get(path)
                        .header(auth, key)
                        .header(SESSION_HEADER.clone(), encode_base64url(&future.encode()))
                        .header("if-none-match", etag.clone())
                        .body(Body::empty())
                        .expect("request"),
                )
                .await
                .expect("answers");
            assert_eq!(response.status(), StatusCode::CONFLICT);
            let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
            let body = String::from_utf8(body.to_vec()).expect("utf-8");
            assert!(body.contains("stale_replica"), "{body}");
            assert!(
                started.elapsed() >= std::time::Duration::from_secs(2),
                "the wait is bounded but real"
            );
        }
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
