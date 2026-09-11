//! The public `/api/v1` HTTP surface.
//!
//! Document responses splice stored JSON bytes; management handlers expose
//! typed durable service results. Every error carries `fix`: a one-line remedy.
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
use axum::http::HeaderMap;
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
use base64::{Engine as _, engine::general_purpose::URL_SAFE_NO_PAD};
use terrabase::CommitOutcome;
use terrabase::Session;
use terrabase::SessionError;
use terrabase::SessionOptions;
use terrabase::SessionToken;
use terrabase::Transaction;
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

#[path = "api_development.rs"]
mod development;
#[path = "api_exports.rs"]
mod exports;
#[path = "api_imports.rs"]
pub(crate) mod imports;
pub use imports::ImportRuntime;
#[path = "api_lifecycle.rs"]
mod lifecycle;
#[path = "api_operations.rs"]
pub(crate) mod operations;

pub use development::DevelopmentRuntime;
pub use webhooks::DispatchWorker;

pub(crate) use exports::recover_exports;

/// Starts the configured webhook worker once for a serving API instance.
/// The owner calls its shutdown method when that instance stops serving.
#[must_use]
pub fn spawn_webhook_dispatcher(state: &Arc<ApiState>) -> Option<DispatchWorker> {
    webhooks::spawn_dispatcher(state)
}
#[path = "api_schema.rs"]
mod schema;
#[path = "schema_index_runtime.rs"]
mod schema_index_runtime;
#[path = "schema_migration_recovery.rs"]
mod schema_migration_recovery;
#[path = "schema_migration_runtime.rs"]
mod schema_migration_runtime;

/// Starts the member's schema migration driver once. The serving runtime
/// aborts and joins the returned task during shutdown.
#[must_use]
pub fn spawn_schema_migrations(state: &Arc<ApiState>) -> Option<tokio::task::JoinHandle<()>> {
    schema_migration_runtime::spawn(state)
}

#[path = "api_usage_indexes.rs"]
mod usage_indexes;
#[path = "api_webhooks.rs"]
mod webhooks;

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

    /// A backup/restore refusal (S-06): the backup side failed or was
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
        response.headers_mut().insert(
            axum::http::header::CACHE_CONTROL,
            HeaderValue::from_static("private, no-store"),
        );
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
    /// Verified production write-budget consumer, absent in local mode.
    pub budget: Option<Arc<crate::production_budget::ProductionBudgetRuntime>>,
    /// Native production backup owner retained for the serving lifetime.
    pub native_backups: Option<Arc<crate::native_backup_runtime::NativeBackupRuntime>>,
    /// Native recovery worker owner retained until bounded shutdown completes.
    pub native_recovery: Option<Arc<crate::native_recovery::NativeRecoveryRuntime>>,
    /// Production resolver for an already admitted, paused restore target.
    pub native_restore_target: Option<
        Arc<
            dyn Fn(
                    &str,
                ) -> Result<
                    (terrabase::engine::EngineConfig, terrabase::backup::PlacementLock),
                    String,
                > + Send
                + Sync,
        >,
    >,
    /// Production-only authoritative database ownership and retained Member leases.
    pub admitted_databases: Option<Arc<crate::admitted_databases::AdmittedDatabaseRegistry>>,
    /// Actual peer acknowledgements; absent progress cannot certify durable writes.
    pub replication_progress: Option<Arc<crate::replication_runtime::ReplicationProgress>>,
    /// True only on the standalone local composition: this member is the
    /// whole placement, so a synced local commit IS the regional fact and
    /// the progress ledger is advanced locally instead of by followers.
    pub standalone_durability: bool,
    /// Bounded branch-only development admission faults.
    pub development: Arc<DevelopmentRuntime>,
    /// Durable production create authority; absent configuration denies production create.
    pub provisioning: Option<Arc<crate::provisioning::ProvisioningClient>>,
    /// The serving member's own region (D-58: named on every response).
    pub region: RegionCode,
    /// Immutable identity of this serving member, used to bind discovery cursors.
    pub member_id: String,
    /// The member's health registry backing `/health`.
    pub metrics: Arc<MetricsRegistry>,
    /// Admitted member data keys. `None` admits no data keys; other product
    /// credentials require the separately configured live identity authority.
    pub keys: Option<Arc<crate::api_keys::KeyStore>>,
    /// Live product identity authority; absent configuration denies delegated credentials.
    pub delegated_identity: Option<Arc<crate::delegated_identity::IdentityVerifier>>,
    /// Optional tenant-pinned BYO-JWT admission; absent denies JWT-shaped bearers.
    pub byo_jwt: Option<Arc<dyn crate::auth_runtime::byo_config::ByoJwtSignatureAdmission>>,
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
    /// Development-only external backup adapter. Production uses
    /// `native_backups` and leaves this unset.
    pub backups: Option<Arc<crate::backup::BackupStreams>>,
    /// Where the fleet writes replicas: `<data_root>/backup` on the local
    /// stack (`S-06`).
    pub backup_root: std::path::PathBuf,
    /// Development-only external time-travel cache. Production leaves this
    /// unset and routes restore operations through the native owner.
    pub timetravel: Option<Arc<crate::backup::TimeTravelCache>>,
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
    /// Authorized regional encryption-key leases for durable webhook secrets.
    pub webhook_keys: Option<Arc<dyn crate::webhook_api::WebhookKeyLeases>>,
    /// Configured durable export jobs and authorized encrypted artifact destination.
    pub exports: Option<Arc<crate::export_api::ExportRuntime>>,
    /// Durable import owner; absent composition fails closed.
    pub imports: Option<Arc<ImportRuntime>>,
}

impl ApiState {
    /// Builds serving state from the already-composed production owners.
    ///
    /// The constructor intentionally accepts every mutable owner explicitly;
    /// it never opens a second catalog, engine, backup fleet, or credential
    /// store while assembling the public API.
    #[allow(clippy::too_many_arguments, reason = "production ownership is explicit")]
    pub fn production(
        native_backups: Arc<crate::native_backup_runtime::NativeBackupRuntime>,
        native_recovery: Arc<crate::native_recovery::NativeRecoveryRuntime>,
        native_restore_target: Arc<
            dyn Fn(
                    &str,
                ) -> Result<
                    (terrabase::engine::EngineConfig, terrabase::backup::PlacementLock),
                    String,
                > + Send
                + Sync,
        >,
        region: RegionCode,
        member_id: String,
        metrics: Arc<MetricsRegistry>,
        admitted_databases: Arc<crate::admitted_databases::AdmittedDatabaseRegistry>,
        replication_progress: Arc<crate::replication_runtime::ReplicationProgress>,
        scope: (String, String),
        provisioning: Option<Arc<crate::provisioning::ProvisioningClient>>,
        backups: Option<Arc<crate::backup::BackupStreams>>,
        backup_root: std::path::PathBuf,
        timetravel: Option<Arc<crate::backup::TimeTravelCache>>,
        guards: Arc<crate::guards::AbuseGuards>,
        origin_proof_keys: Arc<crate::reactive_api::OriginProofKeyRing>,
        capability_keys: Arc<crate::reactive_api::CapabilityKeyRing>,
        keys: Option<Arc<crate::api_keys::KeyStore>>,
        delegated_identity: Option<Arc<crate::delegated_identity::IdentityVerifier>>,
        byo_jwt: Option<Arc<dyn crate::auth_runtime::byo_config::ByoJwtSignatureAdmission>>,
        webhook_keys: Option<Arc<dyn crate::webhook_api::WebhookKeyLeases>>,
        exports: Option<Arc<crate::export_api::ExportRuntime>>,
        budget: Option<Arc<crate::production_budget::ProductionBudgetRuntime>>,
    ) -> Arc<Self> {
        Arc::new(Self {
            native_backups: Some(native_backups),
            native_recovery: Some(native_recovery),
            native_restore_target: Some(native_restore_target),
            admitted_databases: Some(admitted_databases),
            replication_progress: Some(replication_progress),
            standalone_durability: false,
            development: Arc::default(),
            provisioning,
            region,
            member_id,
            metrics,
            keys,
            delegated_identity,
            byo_jwt,
            control: None,
            catalog: None,
            scope,
            backups,
            backup_root,
            timetravel,
            guards,
            origin_proof_keys,
            capability_keys,
            webhook_keys,
            exports,
            imports: None,
            budget,
        })
    }
}

/// The served route table as an `OpenAPI` router: the single source of both
/// the running axum router and the published contract document, so the two
/// can never drift apart.
fn openapi_router() -> OpenApiRouter<Arc<ApiState>> {
    OpenApiRouter::new()
        .routes(routes!(health))
        .routes(routes!(regions))
        .routes(routes!(list_databases, create_database))
        .routes(routes!(get_provisioning_operation))
        .routes(routes!(operations::list_operations))
        .routes(routes!(get_database, delete_database))
        .routes(routes!(lifecycle::deletion_status))
        .routes(routes!(restore_database))
        .routes(routes!(list_collections))
        .routes(routes!(schema::get_schema, schema::put_schema))
        .routes(routes!(schema_migration_recovery::rollback_schema))
        .routes(routes!(usage_indexes::database_usage))
        .routes(routes!(usage_indexes::delete_index))
        .routes(routes!(usage_indexes::index_recommendations))
        .routes(routes!(webhooks::list_webhooks, webhooks::create_webhook))
        .routes(routes!(webhooks::delete_webhook))
        .routes(routes!(webhooks::list_deliveries))
        .routes(routes!(webhooks::replay_webhook))
        .routes(routes!(webhooks::rotate_secret))
        .routes(routes!(imports::create_import, imports::list_imports))
        .routes(routes!(imports::get_import))
        .routes(routes!(imports::upload_import))
        .routes(routes!(imports::resume_import))
        .routes(routes!(exports::list_exports, exports::create_export))
        .routes(routes!(exports::get_export))
        .routes(routes!(exports::resume_export))
        .routes(routes!(exports::download_export))
        .routes(routes!(exports::cancel_export))
        .routes(routes!(development::development_fault, development::clear_development_fault))
        .routes(routes!(development::development_logs))
        .routes(routes!(list_indexes, create_index))
        .routes(routes!(list_documents))
        .routes(routes!(batch_documents))
        .routes(routes!(get_document, put_document, patch_document, delete_document))
        .routes(routes!(document_history))
        .routes(routes!(execute_sql))
        .routes(routes!(crate::reactive_api::websocket))
        .routes(routes!(list_backups))
        .routes(routes!(list_backup_points))
        .routes(routes!(restore_backup))
        .routes(routes!(list_branches, create_branch))
        .routes(routes!(crate::authorization_leases::authorization_lease))
        .routes(routes!(crate::reactive_api::attach_view))
        .routes(routes!(crate::reactive_api::snapshot))
        .routes(routes!(crate::reactive_api::view_log))
        .routes(routes!(crate::reactive_api::view_websocket))
}

/// Compose the management and admitted auth contracts into the single public
/// document. Auth routes are mounted by the production data runtime with a
/// different state type, so their handlers are merged at the `OpenAPI` level.
fn full_openapi_document() -> utoipa::openapi::OpenApi {
    let (_, mut document) = openapi_router().split_for_parts();
    let (_, auth_document) = crate::auth_runtime::routes().split_for_parts();
    document.merge(auth_document);
    document
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
    let mut schemas = document["components"]["schemas"].as_object().cloned().unwrap_or_default();
    schemas.extend(openapi_schemas().as_object().expect("schema table").clone());
    document["components"] = serde_json::json!({
        "securitySchemes": {
            "bearerAuth": {
                "type": "http",
                "scheme": "bearer",
                "bearerFormat": "TerraScale API key"
            },
            "viewWebSocketCapability": {
                "type": "apiKey", "in": "header", "name": "Sec-WebSocket-Protocol",
                "description": "Offer terrascale.sync.v1 and terrascale.cap.<view capability>; the server selects only terrascale.sync.v1."
            }
        },
        "schemas": schemas
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
            let security = if operation_id == "authorization_lease" {
                serde_json::json!([])
            } else if operation_id == "view_websocket" {
                serde_json::json!([{ "viewWebSocketCapability": [] }])
            } else {
                serde_json::json!([{ "bearerAuth": [] }])
            };
            operation.insert("security".to_owned(), security);
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
                            if let Some(schema) = success_schema
                                && (!response.contains_key("content")
                                    || ((status == "202"
                                        && matches!(
                                            operation_id.as_str(),
                                            "create_database" | "create_branch" | "delete_database"
                                        ))
                                        || (status == "200" && operation_id == "deletion_status")))
                            {
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
                if matches!(operation_id.as_str(), "get_schema" | "put_schema") {
                    set_response_header(operation, "200", "ETag", "string");
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
                    for status in ["200", "201", "202"] {
                        set_response_header(operation, status, "X-TerraScale-Placement", "string");
                        set_response_header(operation, status, "X-TerraScale-Residency", "string");
                    }
                }
                if operation_id == "delete_database" {
                    set_response_header(operation, "202", "Location", "string");
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
        "delete_database" => {
            add_parameter(operation, "idempotency-key", "header", true, "string");
        }
        "create_webhook" => add_parameter(operation, "idempotency-key", "header", false, "string"),
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
            if id == "put_document" {
                add_parameter(operation, "if-none-match", "header", false, "string");
            }
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
            add_parameter(operation, "x-terrascale-capability", "header", true, "string");
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
        "restore_backup" => ("RestoreRequest", true),
        "create_branch" => ("CreateBranchRequest", true),
        _ => return None,
    })
}

fn success_schema(operation_id: &str) -> Option<&'static str> {
    match operation_id {
        "health" | "regions" => Some("HealthResponse"),
        "list_databases" => Some("DatabaseListResponse"),
        "list_branches" => Some("BranchListResponse"),
        "create_database" | "create_branch" | "restore_backup" => Some("ProvisioningOperation"),
        "get_provisioning_operation" => Some("OperationSummary"),
        "delete_database" => Some("DeletionOperation"),
        "deletion_status" => Some("DeletionOperation"),
        "get_database" | "restore_database" => Some("DatabaseRecord"),
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
        "list_backup_points" => Some("BackupPointPage"),
        "attach_view" => Some("AttachViewResponse"),
        "authorization_lease" => Some("Lease"),
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
        "HealthResponse": { "type": "object", "required": ["region", "healthy", "version"], "properties": { "region": { "type": "string" }, "healthy": { "type": "boolean" }, "version": { "type": "string" } } },
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
        "HistoryResponse": { "type": "object", "required": ["history"], "properties": { "history": { "type": "array", "items": { "type": "object", "required": ["revision", "deleted", "commitUlid", "hlc", "origin", "policy", "explanation"], "properties": { "revision": { "type": "string" }, "deleted": { "type": "boolean" }, "commitUlid": { "type": "string" }, "hlc": { "type": "integer", "format": "int64" }, "origin": { "type": "string" }, "policy": { "type": "string" }, "explanation": { "type": "string" } } } } } },
        "BatchResponse": { "type": "object", "required": ["committed", "count"], "properties": { "committed": { "type": "integer", "format": "int64" }, "count": { "type": "integer" } } },
        "SqlRequest": { "type": "object", "required": ["sql"], "properties": { "sql": { "type": "string" }, "parameters": { "type": "object", "additionalProperties": { "$ref": "#/components/schemas/JsonValue" } }, "explain": { "type": "boolean" }, "timeout_ms": { "type": "integer", "minimum": 1, "maximum": 30000 }, "cancelled": { "type": "boolean" } } },
        "SqlResponse": { "type": "object", "required": ["matched", "rows"], "properties": { "matched": { "type": "integer", "format": "int64" }, "rows": { "type": "array", "items": { "type": "object", "required": ["id", "value"], "properties": { "id": { "type": "string" }, "value": { "$ref": "#/components/schemas/JsonValue" } } } } } },
        "BackupStatusResponse": { "type": "object", "required": ["ready", "streaming"], "properties": { "ready": { "type": "boolean" }, "streaming": { "type": "boolean" } } },
        "BackupPoint": { "type": "object", "additionalProperties": false, "required": ["point_id", "source_database_id", "captured_at_unix_millis", "retain_until_unix_millis", "logical_checksum", "logical_documents"], "properties": { "point_id": { "type": "string", "pattern": "^[0-9a-f]{64}$" }, "source_database_id": { "type": "string" }, "captured_at_unix_millis": { "type": "integer", "format": "uint64", "minimum": 0 }, "retain_until_unix_millis": { "type": "integer", "format": "uint64", "minimum": 0 }, "logical_checksum": { "type": "string", "pattern": "^[0-9a-f]+$" }, "logical_documents": { "type": "string", "pattern": "^[0-9]+$" } } },
        "BackupPointPage": { "type": "object", "required": ["points"], "properties": { "points": { "type": "array", "items": { "$ref": "#/components/schemas/BackupPoint" } }, "next_cursor": { "type": ["string", "null"] } } },
        "CreateDatabaseRequest": { "type": "object", "required": ["name", "placement", "residency", "operation_id"], "description": "Production provisioning requires an immutable placement, residency constraint and stable operation identifier.", "properties": { "operation_id": { "type": "string" }, "name": { "type": "string" }, "placement": { "type": "array", "minItems": 1, "items": { "type": "string" } }, "residency": { "type": "string" } } },
        "CreateIndexRequest": { "type": "object", "required": ["field"], "properties": { "field": { "type": "string" } } },
        "BatchRequest": { "type": "object", "required": ["operations"], "properties": { "operations": { "type": "array", "minItems": 1, "maxItems": 1000, "items": { "type": "object", "required": ["op", "id"], "properties": { "op": { "type": "string", "enum": ["put", "patch", "delete"] }, "id": { "type": "string" }, "value": { "$ref": "#/components/schemas/JsonValue" } } } } } },
        "RestoreRequest": { "type": "object", "additionalProperties": false, "required": ["name", "operation_id"], "properties": { "name": { "type": "string" }, "at": { "type": "integer", "format": "int64", "minimum": 0 }, "point_id": { "type": "string", "pattern": "^[0-9a-f]{64}$" }, "operation_id": { "type": "string" } } },
        "CreateBranchRequest": { "type": "object", "required": ["name", "operation_id"], "description": "Production branch allocation is asynchronous and does not accept a point-in-time selector.", "properties": { "name": { "type": "string" }, "operation_id": { "type": "string" } } },
        "AttachViewRequest": { "type": "object", "required": ["databaseId", "statement"], "properties": { "databaseId": { "type": "string" }, "statement": { "type": "string" }, "parameters": { "type": "string", "description": "Base64 encoded SQL parameter bytes" }, "checkpoint": { "type": ["string", "null"], "description": "Base64 encoded opaque resume checkpoint" }, "subscription": { "type": ["string", "null"] }, "entitlement_epoch": { "type": "integer", "format": "int64" } } },
        "AttachViewResponse": { "type": "object", "required": ["view", "generation", "snapshot_chunks", "log", "checkpoint", "capability", "capability_header", "offset"], "properties": { "view": { "type": "string" }, "generation": { "type": "integer", "format": "int64" }, "snapshot_chunks": { "type": "array", "items": { "type": "string" } }, "log": { "type": "string" }, "checkpoint": { "type": "string" }, "capability": { "type": "string" }, "capability_header": { "type": "string" }, "offset": { "type": "integer", "format": "int64" } } },
    });
    let Some(schemas) = schemas.as_object_mut() else {
        return serde_json::json!({});
    };
    schemas.insert("DeletionRequest".to_owned(), serde_json::json!({"type": "object", "required": ["environment", "tenant_id", "project_id", "database_id", "operation_id"], "properties": {"environment": {"type": "string"}, "tenant_id": {"type": "string"}, "project_id": {"type": "string"}, "database_id": {"type": "string"}, "operation_id": {"type": "string"}}}));
    schemas.insert("DeletionStop".to_owned(), serde_json::json!({"type": "object", "required": ["environment", "organization_id", "project_id", "database_id", "operation_id", "region", "cutoff"], "properties": {"environment": {"type": "string"}, "organization_id": {"type": "string"}, "project_id": {"type": "string"}, "database_id": {"type": "string"}, "parent_database_id": {"type": "string"}, "operation_id": {"type": "string"}, "region": {"type": "string"}, "cutoff": {"type": "string", "format": "date-time"}, "retain_until": {"type": "string", "format": "date-time"}}}));
    schemas.insert("DeletionPreparation".to_owned(), serde_json::json!({"type": "object", "required": ["environment", "organization_id", "project_id", "database_id", "operation_id", "region", "prepared", "last_accounted_at"], "properties": {"environment": {"type": "string"}, "organization_id": {"type": "string"}, "project_id": {"type": "string"}, "database_id": {"type": "string"}, "parent_database_id": {"type": "string"}, "operation_id": {"type": "string"}, "region": {"type": "string"}, "prepared": {"type": "boolean"}, "last_accounted_at": {"type": "string", "format": "date-time"}}}));
    schemas.insert("DeletionReceipt".to_owned(), serde_json::json!({"type": "object", "required": ["environment", "organization_id", "project_id", "database_id", "operation_id", "region", "cutoff", "access_revoked", "usage_stopped"], "properties": {"environment": {"type": "string"}, "organization_id": {"type": "string"}, "project_id": {"type": "string"}, "database_id": {"type": "string"}, "parent_database_id": {"type": "string"}, "operation_id": {"type": "string"}, "region": {"type": "string"}, "cutoff": {"type": "string", "format": "date-time"}, "retain_until": {"type": "string", "format": "date-time"}, "access_revoked": {"type": "boolean"}, "usage_stopped": {"type": "boolean"}}}));
    schemas.insert("DeletionEdgeReceipt".to_owned(), serde_json::json!({"type": "object", "required": ["request", "cutoff", "access_revoked"], "properties": {"request": {"$ref": "#/components/schemas/DeletionRequest"}, "cutoff": {"type": "string", "format": "date-time"}, "access_revoked": {"type": "boolean"}}}));
    schemas.insert("DeletionClaimReceipt".to_owned(), serde_json::json!({"type": "object", "required": ["schemaVersion", "environment", "tenant_id", "project_id", "database_id", "operation_id", "policy_revision", "policy_fingerprint", "grace_condition", "deadline", "accepted_at", "authority_epoch"], "properties": {"environment": {"type": "string"}, "tenant_id": {"type": "string"}, "project_id": {"type": "string"}, "database_id": {"type": "string"}, "operation_id": {"type": "string"}, "policy_revision": {"type": "string"}, "policy_fingerprint": {"type": "string"}, "grace_condition": {"type": "string"}, "authority_epoch": {"type": "string"}, "schemaVersion": {"type": "integer"}, "deadline": {"type": "string", "format": "date-time"}, "accepted_at": {"type": "string", "format": "date-time"}}}));
    schemas.insert("DeletionTarget".to_owned(), serde_json::json!({"type": "object", "required": ["stop"], "properties": {"stop": {"$ref": "#/components/schemas/DeletionStop"}, "preparation": {"$ref": "#/components/schemas/DeletionPreparation"}, "receipt": {"$ref": "#/components/schemas/DeletionReceipt"}}}));
    schemas.insert("DeletionOperation".to_owned(), serde_json::json!({"type": "object", "required": ["request", "requested_at", "state", "cutoff", "targets"], "properties": {"request": {"$ref": "#/components/schemas/DeletionRequest"}, "requested_at": {"type": "string", "format": "date-time"}, "state": {"type": "string", "enum": ["preparing", "stopping", "edge_pending", "cleanup_pending"]}, "cutoff": {"type": "string", "format": "date-time"}, "targets": {"type": "array", "minItems": 1, "maxItems": 3000, "items": {"$ref": "#/components/schemas/DeletionTarget"}}, "edge_receipt": {"$ref": "#/components/schemas/DeletionEdgeReceipt"}, "claim": {"$ref": "#/components/schemas/DeletionClaimReceipt"}, "automatic_policy_revision": {"type": "string"}, "automatic_grace_condition": {"type": "string"}, "automatic_deadline": {"type": "string", "format": "date-time"}}, "description": "Durable asynchronous database deletion progress. cleanup_pending preserves the accepted usage cutoff while physical cleanup remains incomplete."}));
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
    let openapi_document = full_openapi_document();
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
    let (router, _) = openapi_router().split_for_parts();
    let openapi_document = full_openapi_document();
    let openapi_json =
        Arc::new(enrich_openapi(openapi_document.to_json().expect("OpenAPI must serialize")));
    let scalar_document: serde_json::Value =
        serde_json::from_str(&openapi_json).expect("OpenAPI must deserialize");
    router
        .route("/api/v1/openapi.json", get(openapi))
        .merge(Scalar::with_url("/scalar", scalar_document))
        .fallback(not_routed)
        .layer(Extension(openapi_json))
        .layer(from_fn_with_state(Arc::clone(&state), placement_headers))
        .layer(from_fn_with_state(Arc::clone(&state), authenticate))
        .layer(axum::middleware::from_fn(private_error_cache_control))
        .layer(browser_cors())
        .with_state(state)
}

async fn private_error_cache_control(request: Request, next: Next) -> Response {
    let mut response = next.run(request).await;
    if response.status().is_client_error() || response.status().is_server_error() {
        response.headers_mut().insert(
            axum::http::header::CACHE_CONTROL,
            HeaderValue::from_static("private, no-store"),
        );
    }
    response
}

// Credentials are explicit bearer/capability headers, never browser cookies.
// A fixed wildcard origin lets immutable responses remain shared across apps.
fn browser_cors() -> tower_http::cors::CorsLayer {
    tower_http::cors::CorsLayer::new()
        .allow_origin(tower_http::cors::Any)
        .allow_methods([
            Method::GET,
            Method::HEAD,
            Method::POST,
            Method::PUT,
            Method::PATCH,
            Method::DELETE,
        ])
        .allow_headers(
            [
                "authorization",
                "content-type",
                "if-match",
                "if-none-match",
                "idempotency-key",
                "x-terrascale-capability",
                "x-terrascale-session",
                "x-terrascale-region",
                "x-terrascale-project-id",
                "x-terrascale-tenant-id",
                "x-terrascale-preferred-region",
                "x-terrascale-durability",
                "x-terrascale-allow-stale",
                "x-terrascale-delete-confirmation",
                "traceparent",
                "tracestate",
            ]
            .map(HeaderName::from_static),
        )
        .expose_headers(
            [
                "etag",
                "retry-after",
                "x-request-id",
                "traceparent",
                "x-terrascale-session",
                "x-terrascale-region",
                "x-terrascale-placement",
                "x-terrascale-residency",
                "x-terrascale-artifact-length",
                "x-terrascale-artifact-checksum",
            ]
            .map(HeaderName::from_static),
        )
        .max_age(std::time::Duration::from_mins(10))
        .vary(Vec::<HeaderName>::new())
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
    // Admitted production records are immutable.  The authentication layer
    // already resolved this database before dispatch, so reuse that record
    // instead of reopening lifecycle state after every handler response.
    let headers = state
        .admitted_databases
        .as_ref()
        .and_then(|registry| registry.resolve_metadata(&database).ok().flatten())
        .map(|database| (database.record.regions.join(","), database.record.residency.clone()));
    let headers = match headers {
        Some(headers) => headers,
        None => match database_record(&state, &database) {
            Ok(record) => (record.placement_header(), record.residency_header().to_owned()),
            Err(_) => return response,
        },
    };
    if let (Ok(placement), Ok(residency)) =
        (HeaderValue::from_str(&headers.0), HeaderValue::from_str(&headers.1))
    {
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
/// Product credentials require a fresh TLS-authenticated identity authority
/// result bound to the member-owned tenant/project and requested database.
/// Credential claims never select an authority endpoint or widen permissions.
async fn authenticate(
    State(state): State<Arc<ApiState>>,
    mut request: Request,
    next: Next,
) -> Response {
    if PUBLIC_PATHS.contains(&request.uri().path())
        || (request.method() == Method::GET
            && crate::authorization_leases::is_public_lease_path(request.uri().path()))
    {
        return next.run(request).await;
    }
    // Only the immutable reactive delivery paths may use the edge proof.
    // Attach, REST, WebSocket, and every unknown path remain bearer-only.
    if (request.method() == Method::GET || request.method() == Method::HEAD)
        && is_reactive_delivery_path(request.uri().path())
        && crate::reactive_api::verify_origin_proof(
            request.headers(),
            request.method(),
            request.uri(),
            &state.origin_proof_keys,
            unix_seconds(),
            state.region,
        )
    {
        request.extensions_mut().insert(crate::reactive_api::OriginProofVerified);
        return next.run(request).await;
    }
    if request.method() == Method::GET
        && let Some((view, generation)) =
            crate::reactive_api::websocket_target(request.uri().path())
    {
        return match crate::reactive_api::websocket_capability(
            request.headers(),
            view,
            generation,
            &state,
        ) {
            Ok(_) => next.run(request).await,
            Err(error) => error.into_response(),
        };
    }
    // The canonical attach has its database in a bounded JSON body. Bind the
    // identity authority to that same database, then restore the exact bytes
    // for the handler; never accept a caller-provided owner header here.
    let attach_scope =
        if request.method() == Method::POST && request.uri().path() == "/api/v1/reactive/views" {
            let body = std::mem::replace(request.body_mut(), axum::body::Body::empty());
            let bytes = match axum::body::to_bytes(body, 65_536).await {
                Ok(bytes) => bytes,
                Err(_) => return ApiError::unauthorized().into_response(),
            };
            let parsed = serde_json::from_slice::<crate::reactive_api::AttachRequest>(&bytes);
            *request.body_mut() = axum::body::Body::from(bytes);
            let Ok(attach) = parsed else {
                return ApiError::unauthorized().into_response();
            };
            if attach.database_id.is_empty()
                || attach.database_id.len() > 128
                || attach.database_id.contains(['/', '?', '#'])
            {
                return ApiError::unauthorized().into_response();
            }
            let collection = match crate::reactive_api::attach_collection(&state, &attach) {
                Ok(collection) => collection,
                Err(error) => return error.into_response(),
            };
            Some((attach.database_id, collection))
        } else {
            None
        };
    let token = request
        .headers()
        .get(axum::http::header::AUTHORIZATION)
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.strip_prefix("Bearer "));
    let Some(token) = token else {
        return ApiError::malformed_credential().into_response();
    };
    let request_method = request.method().clone();
    let request_path = attach_scope.as_ref().map_or_else(
        || request.uri().path().to_owned(),
        |(database, _)| format!("/api/v1/databases/{database}/sql"),
    );
    let request_headers = request.headers().clone();
    let (principal, verified_project) = match admit_bearer(
        &state,
        &request_method,
        &request_path,
        &request_headers,
        token,
        attach_scope.as_ref().map(|(_, collection)| collection.as_str()),
    )
    .await
    {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    };
    if let Some(scope) = verified_project {
        request.extensions_mut().insert(scope);
    }
    let admitted: Result<crate::api_keys::Principal, ApiError> = Ok(principal);
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
                // Exports and webhooks expose data from every collection. A
                // collection-scoped credential cannot widen through either API.
                let database_resource = request.uri().path().split('/').nth(5);
                if matches!(database_resource, Some("exports" | "imports" | "webhooks" | "usage"))
                    && !principal.scope().admits_database(verb, database)
                {
                    return ApiError::forbidden("access to the whole database").into_response();
                }
            } else if (verb == Verbs::ADMIN
                || (request.method() == Method::GET && request.uri().path() == "/api/v1/databases"))
                && !principal.scope().admits_global(Verbs::ADMIN)
            {
                return ApiError::forbidden("deployment administration").into_response();
            }
            let development_path = request.uri().path().to_owned();
            if let Err(error) = development::admit(&state, &principal, &development_path).await {
                return error.into_response();
            }
            if let (Some(registry), Some(database)) =
                (&state.admitted_databases, database_of(request.uri().path()))
                && let Ok(Some(admitted)) = registry.resolve(database)
            {
                let now = std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .ok()
                    .and_then(|v| u64::try_from(v.as_millis()).ok());
                if let Some(now) = now {
                    admitted.activity.observe(now);
                }
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

async fn admit_bearer(
    state: &ApiState,
    method: &Method,
    path: &str,
    headers: &HeaderMap,
    token: &str,
    query_collection: Option<&str>,
) -> Result<
    (crate::api_keys::Principal, Option<crate::delegated_identity::VerifiedProjectScope>),
    ApiError,
> {
    let collection = query_collection.or_else(|| collection_of(path));
    let route_owner = match database_of(path) {
        Some(database) => crate::public_admission::route_owner(state, database)?,
        None if project_operation_headers(method, path) => {
            let tenant = headers
                .get("x-terrascale-tenant-id")
                .and_then(|value| value.to_str().ok())
                .ok_or_else(ApiError::unauthorized)?;
            let project = headers
                .get("x-terrascale-project-id")
                .and_then(|value| value.to_str().ok())
                .ok_or_else(ApiError::unauthorized)?;
            (tenant.to_owned(), project.to_owned())
        }
        None => state.scope.clone(),
    };
    let project_operation = is_project_administration(method, path);
    let mut verified_project = None;
    let admitted = match state.keys.as_ref().map(|keys| {
        if state.admitted_databases.is_some() {
            keys.admit_for_owner(token, &route_owner)
        } else {
            keys.admit(token)
        }
    }) {
        Some(Ok(principal)) => {
            if project_operation && principal.scope().admits_global(Verbs::ADMIN) {
                verified_project = Some(crate::delegated_identity::VerifiedProjectScope {
                    tenant_id: route_owner.0.clone(),
                    project_id: route_owner.1.clone(),
                });
            }
            Ok(principal)
        }
        _ if state.byo_jwt.is_some() && token.matches('.').count() == 2 => {
            let admission = state.byo_jwt.as_ref().expect("checked above");
            let context = admission
                .authenticate(token, unix_seconds())
                .await
                .map_err(|_| ApiError::unauthorized())?;
            if context.tenant() != route_owner.0 {
                return Err(ApiError::unauthorized());
            }
            // Token operations are an upper bound; the live identity
            // authority remains the membership/revocation source of truth.
            let _capability = crate::auth_runtime::byo_config::principal_for(
                &context,
                database_of(path),
                collection,
                required_verb(method, path),
                project_operation,
            )
            .ok_or_else(ApiError::unauthorized)?;
            match (&state.delegated_identity, database_of(path)) {
                (Some(identity), Some(database)) => {
                    let principal = identity
                        .admit_verified_byo(
                            context.issuer(),
                            context.subject(),
                            &route_owner.0,
                            &route_owner.1,
                            database,
                            collection,
                            required_verb(method, path),
                        )
                        .await
                        .map_err(|_| ApiError::unauthorized())?;
                    if !admission.generation_current(&context) {
                        return Err(ApiError::unauthorized());
                    }
                    if unix_millis() >= context.expires_at().saturating_mul(1_000) {
                        return Err(ApiError::unauthorized());
                    }
                    Ok(principal.cap_expiry(context.expires_at().saturating_mul(1_000)))
                }
                (Some(identity), None) if project_operation => {
                    let result = identity
                        .admit_verified_byo_project(
                            context.issuer(),
                            context.subject(),
                            &route_owner.0,
                            &route_owner.1,
                        )
                        .await
                        .map_err(|_| ApiError::unauthorized())?;
                    if !admission.generation_current(&context) {
                        return Err(ApiError::unauthorized());
                    }
                    let (principal, scope) = result;
                    if unix_millis() >= context.expires_at().saturating_mul(1_000) {
                        return Err(ApiError::unauthorized());
                    }
                    Ok({
                        verified_project = Some(scope);
                        principal.cap_expiry(context.expires_at().saturating_mul(1_000))
                    })
                }
                _ => Err(ApiError::unauthorized()),
            }
        }
        _ => match (&state.delegated_identity, database_of(path)) {
            (Some(identity), Some(database)) => identity
                .admit(
                    token,
                    &route_owner.0,
                    &route_owner.1,
                    database,
                    collection,
                    required_verb(method, path),
                )
                .await
                .map_err(|_| ApiError::unauthorized()),
            (Some(identity), None) if project_operation => {
                let tenant = headers.get("x-terrascale-tenant-id").and_then(|v| v.to_str().ok());
                let project = headers.get("x-terrascale-project-id").and_then(|v| v.to_str().ok());
                match (tenant, project) {
                    (Some(tenant), Some(project)) => {
                        match identity.admit_project(token, tenant, project).await {
                            Ok((principal, scope)) => {
                                verified_project = Some(scope);
                                Ok(principal)
                            }
                            Err(_) => Err(ApiError::unauthorized()),
                        }
                    }
                    _ => Err(ApiError::unauthorized()),
                }
            }
            _ => Err(ApiError::unauthorized()),
        },
    }?;
    if state.admitted_databases.is_some()
        && admitted.owner() != Some((route_owner.0.as_str(), route_owner.1.as_str()))
    {
        return Err(ApiError::unauthorized());
    }
    Ok((admitted, verified_project))
}

fn project_operation_headers(method: &Method, path: &str) -> bool {
    is_project_administration(method, path)
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

fn unix_millis() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .ok()
        .and_then(|value| u64::try_from(value.as_millis()).ok())
        .unwrap_or(u64::MAX)
}

/// The verb one request needs: reads ride `READ`, document writes ride
/// `WRITE`, and database lifecycle, indexes, schema, webhooks, and exports
/// ride `ADMIN` (the [`crate::api_keys::Verbs`] contract). Unknown mutating
/// paths fail closed toward `ADMIN`.
fn is_project_administration(method: &Method, path: &str) -> bool {
    (*method == Method::POST && path == "/api/v1/databases")
        || (*method == Method::GET && path == "/api/v1/operations")
        || (*method == Method::GET
            && path
                .strip_prefix("/api/v1/operations/")
                .is_some_and(|id| !id.is_empty() && !id.contains('/')))
}

fn required_verb(method: &Method, path: &str) -> Verbs {
    if path == "/api/v1/operations" {
        return Verbs::ADMIN;
    }
    if path
        .strip_prefix("/api/v1/operations/")
        .is_some_and(|id| !id.is_empty() && !id.contains('/'))
    {
        return Verbs::ADMIN;
    }
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
    // The immutable build identity every consumer (bench lane, dashboard,
    // operator) observes first: a served member states what it is.
    let body = format!(
        "{{\"region\":\"{}\",\"healthy\":{},\"version\":\"terrabase-{}\"}}",
        state.region,
        healthy,
        env!("CARGO_PKG_VERSION")
    );
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
    let local_serving = state.metrics.snapshot().service_state() == ServiceState::Serving;
    let replication_serving =
        state.replication_progress.as_ref().is_none_or(|progress| !progress.is_fenced());
    // This is the same live admission authority used by publication.  Keep
    // it separate from degraded serving: retained databases may continue
    // serving reads while new database admission is refused.
    let admission_ready =
        state.admitted_databases.as_ref().is_none_or(|registry| registry.admission_ready());
    let healthy = local_serving && replication_serving && admission_ready;
    // Keep the member route's body identical in shape to `/api/v1/health`.
    // The edge-owned `/api/v1/regions` catalog is a different producer and
    // is described by its own RegionCatalogResponse contract.
    let body = format!(
        "{{\"region\":\"{}\",\"healthy\":{healthy},\"version\":\"terrabase-{}\"}}",
        state.region,
        env!("CARGO_PKG_VERSION")
    );
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

#[utoipa::path(get, path = "/api/v1/operations/{operation_id}", responses((status = 200, description = "Durable operation", body = crate::api::operations::OperationSummary)))]
async fn get_provisioning_operation(
    State(state): State<Arc<ApiState>>,
    Path(operation_id): Path<String>,
    scope: Option<Extension<crate::delegated_identity::VerifiedProjectScope>>,
) -> Response {
    let Some(Extension(scope)) = scope else {
        return ApiError::control_unavailable().into_response();
    };
    let Some(authority) = &state.provisioning else {
        return match operations::member_operation(&state, &scope, &operation_id) {
            Ok(Some(operation)) => match serde_json::to_string(&operation) {
                Ok(body) => json_response(StatusCode::OK, body),
                Err(_) => ApiError::control_unavailable().into_response(),
            },
            Ok(None) => {
                provisioning_error(crate::provisioning::ProvisioningError::NotFound).into_response()
            }
            Err(()) => ApiError::control_unavailable().into_response(),
        };
    };
    match authority.status(&scope, &operation_id).await {
        Ok(operation) => {
            match serde_json::to_string(&operations::provisioning_summary(operation)) {
                Ok(body) => json_response(StatusCode::OK, body),
                Err(_) => ApiError::control_unavailable().into_response(),
            }
        }
        Err(crate::provisioning::ProvisioningError::NotFound) => {
            match operations::member_operation(&state, &scope, &operation_id) {
                Ok(Some(operation)) => match serde_json::to_string(&operation) {
                    Ok(body) => json_response(StatusCode::OK, body),
                    Err(_) => ApiError::control_unavailable().into_response(),
                },
                Ok(None) => provisioning_error(crate::provisioning::ProvisioningError::NotFound)
                    .into_response(),
                Err(()) => ApiError::control_unavailable().into_response(),
            }
        }
        Err(error) => provisioning_error(error).into_response(),
    }
}

async fn create_provisioned_database(
    authority: &crate::provisioning::ProvisioningClient,
    scope: &crate::delegated_identity::VerifiedProjectScope,
    body: &str,
) -> Response {
    #[derive(serde::Deserialize)]
    #[serde(deny_unknown_fields)]
    struct Create {
        name: String,
        placement: Vec<String>,
        residency: String,
        operation_id: String,
    }
    let request: Create = match serde_json::from_str(body) {
        Ok(request) => request,
        Err(_) => {
            return ApiError {
                status: StatusCode::BAD_REQUEST,
                code: "invalid_create_request",
                message: "create requires name, placement, residency and a stable operation_id"
                    .into(),
                fix: "supply the immutable placement and reuse operation_id when retrying".into(),
                placement: None,
            }
            .into_response();
        }
    };
    match authority
        .create(scope, &request.name, &request.placement, &request.residency, &request.operation_id)
        .await
    {
        Ok(operation) => match serde_json::to_string(&operation) {
            Ok(body) => json_response(StatusCode::ACCEPTED, body),
            Err(_) => ApiError::control_unavailable().into_response(),
        },
        Err(error) => provisioning_error(error).into_response(),
    }
}

fn provisioning_error(error: crate::provisioning::ProvisioningError) -> ApiError {
    use crate::provisioning::ProvisioningError;
    let (status, code, message, fix) = match error {
        ProvisioningError::InvalidRequest => (
            StatusCode::BAD_REQUEST,
            "invalid_provisioning_request",
            "the provisioning request does not satisfy the database policy",
            "verify the database name, placement, residency and operation identifier",
        ),
        ProvisioningError::Conflict => (
            StatusCode::CONFLICT,
            "provisioning_conflict",
            "the request conflicts with an existing immutable database or operation",
            "retry the original request with its unchanged operation identifier",
        ),
        ProvisioningError::NotFound => (
            StatusCode::NOT_FOUND,
            "operation_not_found",
            "the operation does not exist in this project",
            "verify the operation identifier and project credentials",
        ),
        ProvisioningError::Forbidden => (
            StatusCode::FORBIDDEN,
            "provisioning_forbidden",
            "the authority denied provisioning access",
            "use credentials authorized to administer this project",
        ),
        ProvisioningError::Unavailable => return ApiError::control_unavailable(),
    };
    ApiError { status, code, message: message.into(), fix: fix.into(), placement: None }
}

/// Creates one database from an explicit placement and optional residency
/// constraint (D-52).
#[utoipa::path(post, path = "/api/v1/databases", responses((status = 202, description = "Durable provisioning accepted; not ready to serve", body = crate::provisioning::ProvisioningOperation)))]
async fn create_database(
    State(state): State<Arc<ApiState>>,
    scope: Option<Extension<crate::delegated_identity::VerifiedProjectScope>>,
    body: String,
) -> Response {
    if let Some(authority) = &state.provisioning {
        let Some(Extension(scope)) = scope else {
            return ApiError::control_unavailable().into_response();
        };
        return create_provisioned_database(authority, &scope, &body).await;
    }
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
            // Local-composition guard knob: a benchmark subject must not be
            // throttled by the Free plan's read-rate cell. Production never
            // reads this environment; the billing lane owns its plans there.
            if state.standalone_durability
                && let Some(rps) = std::env::var("TERRABASE_LOCAL_GUARD_READ_RPS")
                    .ok()
                    .and_then(|value| value.parse::<u64>().ok())
                    .filter(|value| *value > 0)
            {
                let limits = crate::guards::Plan::Free.limits();
                state.guards.set_limits_override(
                    name,
                    crate::guards::AbuseLimits { read_query_rps: rps, ..limits },
                );
            }
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
                let Some(backups) = state.backups.as_ref() else {
                    let _ = control.soft_delete(name);
                    return ApiError::backup("native backup owner is unavailable").into_response();
                };
                let plan = backups.plan_for(name);
                if let Err(error) =
                    attach_backup(backups, name, &location, &state.backup_root, &plan, state.region)
                {
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
    match database_record(&state, &name) {
        Ok(record) => {
            if !record.placement.contains(&state.region) {
                return ApiError::misdirected(&record.placement, record.residency_header())
                    .into_response();
            }
            record_response(StatusCode::OK, &record)
        }
        Err(error) => error.into_response(),
    }
}

/// Soft-deletes one database: restorable for seven days. The backup
/// stream stops with it; retention owns the artifacts (S-06).
#[utoipa::path(delete, path = "/api/v1/databases/{name}", responses((status = 202, description = "Durable database deletion accepted", body = serde_json::Value)))]
async fn delete_database(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
    Extension(principal): Extension<crate::api_keys::Principal>,
    headers: axum::http::HeaderMap,
) -> Response {
    if let Some(authority) = &state.provisioning {
        if !principal.scope().admits_database(Verbs::ADMIN, &name) {
            return ApiError::forbidden("whole database deletion").into_response();
        }
        let Some(operation) = headers.get("idempotency-key").and_then(|value| value.to_str().ok())
        else {
            return ApiError {
                status: StatusCode::BAD_REQUEST,
                code: "missing_operation_id",
                message: "deletion requires an idempotency key".into(),
                fix: "send Idempotency-Key with a stable deletion operation identifier".into(),
                placement: None,
            }
            .into_response();
        };
        let (tenant_id, project_id) = match database_owner_scope(&state, &name) {
            Ok(value) => value,
            Err(error) => return error.into_response(),
        };
        let scope = crate::delegated_identity::VerifiedProjectScope { tenant_id, project_id };
        return match authority
            .deletion(
                &scope,
                &name,
                operation,
                true,
                headers.get("authorization").and_then(|value| value.to_str().ok()),
            )
            .await
        {
            Ok(value) => {
                let mut response = json_response(StatusCode::ACCEPTED, value.to_string());
                if let Ok(location) = HeaderValue::from_str(&format!(
                    "/api/v1/databases/{name}/deletions/{operation}"
                )) {
                    response.headers_mut().insert(axum::http::header::LOCATION, location);
                }
                response
            }
            Err(error) => provisioning_error(error).into_response(),
        };
    }
    let Some(control) = state.control.clone() else {
        return ApiError::control_unavailable().into_response();
    };
    match control.soft_delete(&name) {
        Ok(record) => {
            if let Some(backups) = &state.backups {
                backups.detach(&name);
            }
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
    headers: axum::http::HeaderMap,
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
    let mut session = if headers.contains_key(&SESSION_HEADER) {
        match open_session(engine, &scope, &headers) {
            Ok(session) => Some(session),
            Err(error) => return error.into_response(),
        }
    } else {
        None
    };
    let snapshot = match &mut session {
        Some(session) => session.read_snapshot().map_err(session_error),
        None => engine.read_snapshot(&scope).map_err(|_| ApiError::storage_failure()),
    };
    let snapshot = match snapshot {
        Ok(snapshot) => snapshot,
        Err(error) => return error.into_response(),
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
            let mut response = json_response(StatusCode::OK, body);
            if let Some(session) = &session {
                with_session(&mut response, session.session_token());
            }
            response
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
    headers: axum::http::HeaderMap,
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
    let mut session = if headers.contains_key(&SESSION_HEADER) {
        match open_session(engine, &scope, &headers) {
            Ok(session) => Some(session),
            Err(error) => return error.into_response(),
        }
    } else {
        None
    };
    if let Some(session) = &mut session
        && let Err(error) = session.read_snapshot() {
            return session_error(error).into_response();
        }
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
            let mut response = json_response(StatusCode::OK, body);
            if let Some(session) = &session {
                with_session(&mut response, session.session_token());
            }
            response
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
    headers: axum::http::HeaderMap,
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
    let mut session = if headers.contains_key(&SESSION_HEADER) {
        match open_session(engine, &scope, &headers) {
            Ok(session) => Some(session),
            Err(error) => return error.into_response(),
        }
    } else {
        None
    };
    let snapshot = match &mut session {
        Some(session) => session.read_snapshot().map_err(session_error),
        None => engine.read_snapshot(&scope).map_err(|_| ApiError::storage_failure()),
    };
    let snapshot = match snapshot {
        Ok(snapshot) => snapshot,
        Err(error) => return error.into_response(),
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
    let mut response = json_response(StatusCode::OK, body);
    if let Some(session) = &session {
        with_session(&mut response, session.session_token());
    }
    response
}

/// The documents list's paging parameters.
#[derive(serde::Deserialize)]
struct DocumentPaging {
    limit: Option<usize>,
    after: Option<String>,
}

/// Durability is established by peer acknowledgements after local commit.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum Durability {
    Regional,
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
}

struct WriteDurability {
    class: Durability,
    uniform_context: Option<std::collections::BTreeMap<String, u64>>,
    progress: Arc<crate::replication_runtime::ReplicationProgress>,
    scope: terrabase_cluster::production::DatabaseRouteScope,
    timeout: std::time::Duration,
    standalone: bool,
}

/// Durability proofs for a logical batch.  Cross-file commits publish one
/// receipt per origin plus a coordinator completion receipt; keep those
/// proofs private while preserving the single public batch response.
struct BatchDurability {
    entries: Vec<(String, WriteDurability)>,
}

fn engine_identity(engine: &terrabase::Engine) -> String {
    // The owner scope is shared by split files; the durable database ULID is
    // the physical-file identity needed to keep origin proofs distinct.
    engine.database_id().to_string()
}

fn durability_unavailable(global: bool) -> ApiError {
    ApiError {
        status: StatusCode::SERVICE_UNAVAILABLE,
        code: if global {
            "global_durability_unavailable"
        } else {
            "regional_durability_unavailable"
        },
        message: "the requested replication acknowledgements are unavailable before commit".into(),
        fix: "retry when the selected regional members are available".into(),
        placement: None,
    }
}

fn durability_unknown() -> ApiError {
    ApiError {
        status: StatusCode::SERVICE_UNAVAILABLE,
        code: "commit_unknown",
        message:
            "the write may have committed; its requested replication durability is unconfirmed"
                .into(),
        fix: "retry the identical request with its original Idempotency-Key; do not mint a new key"
            .into(),
        placement: None,
    }
}

impl WriteDurability {
    async fn prepare(
        state: &ApiState,
        engine: &terrabase::Engine,
        engine_scope: &terrabase::types::TenantScope,
        headers: &axum::http::HeaderMap,
    ) -> Result<Self, ApiError> {
        Self::prepare_inner(state, engine, engine_scope, headers, false).await
    }

    async fn prepare_replay(
        state: &ApiState,
        engine: &terrabase::Engine,
        engine_scope: &terrabase::types::TenantScope,
        headers: &axum::http::HeaderMap,
    ) -> Result<Self, ApiError> {
        Self::prepare_inner(state, engine, engine_scope, headers, true).await
    }

    async fn prepare_inner(
        state: &ApiState,
        engine: &terrabase::Engine,
        engine_scope: &terrabase::types::TenantScope,
        headers: &axum::http::HeaderMap,
        replay: bool,
    ) -> Result<Self, ApiError> {
        let class = Durability::of_header(headers)?;
        let unavailable = || durability_unavailable(class == Durability::Global);
        let progress = state.replication_progress.clone().ok_or_else(unavailable)?;
        #[cfg(test)]
        tests::rf2::replicate(&progress, engine, engine_scope);
        let owner = engine.database_owner();
        let scope = terrabase_cluster::production::DatabaseRouteScope::new(
            owner.tenant(),
            owner.project(),
            owner.database(),
        );
        // Standalone composition: this member is the entire placement, so
        // the scope is admitted for its own region. Identical admission is
        // idempotent; the ledger entry is what sequence-zero checks read.
        let standalone = state.standalone_durability;
        if standalone {
            let selected = std::collections::BTreeSet::from([state.region.as_str().to_owned()]);
            progress
                .admit(scope, selected)
                .map_err(|_| durability_unavailable(class == Durability::Global))?;
        }
        let timeout_ms = match headers.get("x-terrascale-timeout-ms") {
            Some(value) => value
                .to_str()
                .ok()
                .and_then(|value| value.parse::<u64>().ok())
                .filter(|value| *value > 0 && *value <= 30_000)
                .ok_or_else(|| ApiError::invalid_header("X-TerraScale-Timeout-Ms"))?,
            None => 5_000,
        };
        let timeout = std::time::Duration::from_millis(timeout_ms);
        // Sequence zero checks the accepted assignment and fencing state;
        // it does not establish durability for any committed operation.
        progress.wait_regional(scope, 0, timeout).await.map_err(|_| unavailable())?;
        let uniform_context = if class == Durability::Global && !replay {
            let context = engine
                .durable_origin_prefixes(engine_scope)
                .map_err(|_| ApiError::storage_failure())?;
            progress.wait_uniform(scope, &context, timeout).await.map_err(|_| unavailable())?;
            Some(context)
        } else {
            None
        };
        Ok(Self { class, uniform_context, progress, scope, timeout, standalone })
    }

    fn guard(&self, transaction: &mut terrabase::Transaction<'_>) -> Result<(), ApiError> {
        if let Some(context) = &self.uniform_context {
            transaction
                .require_uniform_context(context.clone())
                .map_err(|_| durability_unavailable(true))?;
        }
        Ok(())
    }

    fn guard_routed(
        &self,
        transaction: &mut terrabase::Transaction<'_>,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Result<(), ApiError> {
        if let Some(context) = &self.uniform_context {
            transaction
                .require_routed_uniform_context(collection, id, context.clone())
                .map_err(|_| durability_unavailable(true))?;
        }
        Ok(())
    }

    async fn confirm(
        &self,
        engine: &terrabase::Engine,
        engine_scope: &terrabase::types::TenantScope,
        receipt: &terrabase::CommitReceipt,
    ) -> Result<(), ApiError> {
        #[cfg(test)]
        tests::rf2::replicate(&self.progress, engine, engine_scope);
        let sequence = receipt.sequence().get();
        // Standalone composition: the synced local commit above already
        // established the regional fact (there are no followers to prove
        // it separately), so the ledger is advanced to the receipt before
        // the wait, which then observes exactly what was committed.
        if self.standalone {
            self.progress.advance_regional(self.scope, sequence);
        }
        self.progress
            .wait_regional(self.scope, sequence, self.timeout)
            .await
            .map_err(|_| durability_unknown())?;
        if self.class == Durability::Global {
            let rows = engine
                .journal_rows(engine_scope, sequence.saturating_sub(1), 1)
                .map_err(|_| durability_unknown())?;
            let row = rows
                .first()
                .filter(|row| row.commit_sequence == sequence)
                .ok_or_else(durability_unknown)?;
            self.progress
                .wait_global(self.scope, &row.origin, row.origin_sequence, self.timeout)
                .await
                .map_err(|_| durability_unknown())?;
        }
        Ok(())
    }
}

impl BatchDurability {
    async fn prepare(
        state: &ApiState,
        host: &terrabase_tbep::engine_host::EngineHost,
        collection: &CollectionName,
        ids: &[DocumentId],
        scope: &terrabase::types::TenantScope,
        headers: &axum::http::HeaderMap,
        replay: bool,
    ) -> Result<Self, ApiError> {
        let coordinator =
            host.batch_coordinator(collection, ids).map_err(|_| ApiError::control_unavailable())?;
        let mut engines = Vec::with_capacity(ids.len() + 1);
        engines.push(coordinator);
        // Single-child routed writes coordinate on the admitted root even
        // though their document participant is the child origin.
        engines.push(host.engine());
        for id in ids {
            engines.push(
                host.document_participant(collection, std::slice::from_ref(id))
                    .map_err(|_| ApiError::control_unavailable())?,
            );
        }
        let mut entries = Vec::new();
        for engine in engines {
            let identity = engine_identity(engine);
            if entries.iter().any(|(known, _)| known == &identity) {
                continue;
            }
            let engine_scope = engine.scope_for(scope.tenant());
            let durability = if replay {
                WriteDurability::prepare_replay(state, engine, &engine_scope, headers).await?
            } else {
                WriteDurability::prepare(state, engine, &engine_scope, headers).await?
            };
            entries.push((identity, durability));
        }
        Ok(Self { entries })
    }

    fn guard(
        &self,
        transaction: &mut terrabase::Transaction<'_>,
        host: &terrabase_tbep::engine_host::EngineHost,
        collection: &CollectionName,
        ids: &[DocumentId],
    ) -> Result<(), ApiError> {
        let mut guarded = std::collections::BTreeSet::new();
        for id in ids {
            let engine = host
                .document_participant(collection, std::slice::from_ref(id))
                .map_err(|_| ApiError::control_unavailable())?;
            let identity = engine_identity(engine);
            if !guarded.insert(identity.clone()) {
                continue;
            }
            let entry = self
                .entries
                .iter()
                .find(|(known, _)| known == &identity)
                .ok_or_else(durability_unknown)?;
            entry.1.guard_routed(transaction, collection, id)?;
        }
        Ok(())
    }

    async fn confirm(
        &self,
        host: &terrabase_tbep::engine_host::EngineHost,
        scope: &terrabase::types::TenantScope,
        operation: &OperationId,
        fallback_engine: &terrabase::Engine,
        fallback_scope: &terrabase::types::TenantScope,
        fallback: &terrabase::CommitReceipt,
    ) -> Result<(), ApiError> {
        let Some(receipts) =
            host.batch_commit_receipts(scope, operation).map_err(|_| durability_unknown())?
        else {
            let entry = self
                .entries
                .iter()
                .find(|(identity, _)| identity == &engine_identity(fallback_engine))
                .ok_or_else(durability_unknown)?;
            return entry.1.confirm(fallback_engine, fallback_scope, fallback).await;
        };
        for (engine, receipt) in receipts {
            let identity = engine_identity(&engine);
            let entry = self
                .entries
                .iter()
                .find(|(known, _)| known == &identity)
                .ok_or_else(durability_unknown)?;
            let engine_scope = engine.scope_for(scope.tenant());
            entry.1.confirm(&engine, &engine_scope, &receipt).await?;
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
    let identity = match headers.get("idempotency-key") {
        Some(value) => {
            let key = value.to_str().map_err(|_| ApiError::invalid_header("Idempotency-Key"))?;
            let namespace = match kind {
                "api-put" | "api-patch" | "api-delete" => "api-document",
                _ => kind,
            };
            format!("{namespace}-idem-{key}")
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

fn attach_write_budget(
    state: &ApiState,
    transaction: &mut terrabase::Transaction<'_>,
    scope: &terrabase::types::TenantScope,
    project_id: &str,
    database_id: &str,
    operation: &OperationId,
    units: u64,
) -> Result<(), ApiError> {
    let Some(budget) = &state.budget else {
        return Ok(());
    };
    budget
        .attach(
            transaction,
            scope.tenant().as_str(),
            project_id,
            database_id,
            operation.as_str(),
            state.region,
            units,
        )
        .map_err(|reason| ApiError {
            status: StatusCode::TOO_MANY_REQUESTS,
            code: "write_budget_unavailable",
            message: reason,
            fix: "obtain a current write allocation and retry the request".into(),
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
    // Capture all routed keys before reserving the logical batch operation.
    let ids = match batch_route_ids(&operations) {
        Ok(ids) => ids,
        Err(error) => return error,
    };
    let operation = match write_operation(&headers, "api-batch") {
        Ok(operation) => operation,
        Err(error) => return error.into_response(),
    };
    let fingerprint = if has_idempotency_key(&headers) {
        batch_request_fingerprint(&collection, &operations)
    } else {
        [0; 32]
    };
    match replay_batch_request(
        &state,
        host.engine_host(),
        &collection,
        &ids,
        &scope,
        &headers,
        &operation,
        fingerprint,
    )
    .await
    {
        Ok(Some(response)) => return response,
        Ok(None) => {}
        Err(error) => return error.into_response(),
    }
    let engine = match host.engine_host().batch_coordinator(&collection, &ids) {
        Ok(engine) => engine,
        Err(_) => return ApiError::control_unavailable().into_response(),
    };
    let scope = engine.scope_for(scope.tenant());
    let durability = match BatchDurability::prepare(
        &state,
        host.engine_host(),
        &collection,
        &ids,
        &scope,
        &headers,
        false,
    )
    .await
    {
        Ok(durability) => durability,
        Err(error) => return error.into_response(),
    };
    let mut session = match open_session(engine, &scope, &headers) {
        Ok(session) => session,
        Err(error) => return error.into_response(),
    };
    let mut transaction =
        match host.engine_host().begin_routed_batch(&scope, &collection, &ids, operation.clone()) {
            Ok(transaction) => transaction,
            Err(terrabase::EngineError::OperationIdReuse { .. }) => {
                return durability_unknown().into_response();
            }
            Err(_) => return ApiError::storage_failure().into_response(),
        };
    // The operation reservation excludes a simultaneous publisher. Resolve
    // once more before inspecting mutable patch bases or delete targets.
    match replay_batch_request(
        &state,
        host.engine_host(),
        &collection,
        &ids,
        &scope,
        &headers,
        &operation,
        fingerprint,
    )
    .await
    {
        Ok(Some(response)) => return response,
        Ok(None) => {}
        Err(error) => return error.into_response(),
    }
    let database_id = engine.database_owner().database().to_text();
    let mut units = 0_u64;
    let operation_id = transaction.operation_id().clone();
    for (index, item) in operations.iter().enumerate() {
        match stage_batch_operation(&mut transaction, &collection, item) {
            Ok(operation_units) => units = units.saturating_add(operation_units),
            Err((code, message)) => return batch_refusal(index, code, message),
        }
    }
    if has_idempotency_key(&headers)
        && let Err(error) = transaction.bind_batch_request(fingerprint, operations.len()) {
            return document_request_error(error).into_response();
        }
    if let Err(error) = attach_write_budget(
        &state,
        &mut transaction,
        &scope,
        engine.database_owner().project().to_string().as_str(),
        &database_id,
        &operation_id,
        units,
    ) {
        return error.into_response();
    }
    if let Err(error) = durability.guard(&mut transaction, host.engine_host(), &collection, &ids) {
        return error.into_response();
    }
    let outcome = commit_on_runtime(transaction, LocalDurability::Synced);
    if !matches!(outcome, CommitOutcome::Committed(_)) {
        match replay_batch_request(
            &state,
            host.engine_host(),
            &collection,
            &ids,
            &scope,
            &headers,
            &operation,
            fingerprint,
        )
        .await
        {
            Ok(Some(response)) => return response,
            Ok(None) => {}
            Err(error) => return error.into_response(),
        }
    }
    match outcome {
        CommitOutcome::Committed(receipt) => {
            if let Err(error) = durability
                .confirm(
                    host.engine_host(),
                    &scope,
                    &operation_id,
                    engine,
                    &scope,
                    &receipt,
                )
                .await
            {
                return error.into_response();
            }
            match batch_write_response(&mut session, receipt.sequence().get(), operations.len()) {
                Ok(response) => response,
                Err(error) => error.into_response(),
            }
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

fn batch_route_ids(operations: &[serde_json::Value]) -> Result<Vec<DocumentId>, Response> {
    let mut ids = Vec::with_capacity(operations.len());
    for (index, item) in operations.iter().enumerate() {
        let Some(id) = item.get("id").and_then(serde_json::Value::as_str) else {
            return Err(batch_refusal(index, "missing_id", "the op names no document"));
        };
        let id = DocumentId::try_new(id)
            .map_err(|_| batch_refusal(index, "invalid_id", "the op names an invalid document"))?;
        ids.push(id);
    }
    Ok(ids)
}

fn batch_request_fingerprint(
    collection: &CollectionName,
    operations: &[serde_json::Value],
) -> [u8; 32] {
    let mut hash = blake3::Hasher::new();
    hash.update(b"batch-request-v1");
    hash.update(&(collection.as_str().len() as u64).to_le_bytes());
    hash.update(collection.as_str().as_bytes());
    // Serialization of parsed JSON makes whitespace and object-key order irrelevant.
    for item in operations {
        let encoded = item.to_string();
        hash.update(&(encoded.len() as u64).to_le_bytes());
        hash.update(encoded.as_bytes());
    }
    *hash.finalize().as_bytes()
}

async fn replay_batch_request(
    state: &ApiState,
    host: &terrabase_tbep::engine_host::EngineHost,
    collection: &CollectionName,
    ids: &[DocumentId],
    scope: &terrabase::types::TenantScope,
    headers: &axum::http::HeaderMap,
    operation: &OperationId,
    fingerprint: [u8; 32],
) -> Result<Option<Response>, ApiError> {
    if !has_idempotency_key(headers) {
        return Ok(None);
    }
    let Some((engine, receipt, count)) = host
        .resolve_routed_batch_request(scope, collection, ids, operation.clone(), fingerprint)
        .map_err(document_request_error)?
    else {
        return Ok(None);
    };
    let resolved_scope = engine.scope_for(scope.tenant());
    let scope = &resolved_scope;
    let durability =
        BatchDurability::prepare(state, host, collection, ids, scope, headers, true).await?;
    durability.confirm(host, scope, operation, &engine, scope, &receipt).await?;
    let mut session = open_session(&engine, scope, headers)?;
    batch_write_response(&mut session, receipt.sequence().get(), count).map(Some)
}

fn batch_write_response(
    session: &mut Session<'_>,
    sequence: u64,
    count: usize,
) -> Result<Response, ApiError> {
    session.read_snapshot().map_err(session_error)?;
    let mut response =
        json_response(StatusCode::OK, format!("{{\"committed\":{sequence},\"count\":{count}}}"));
    with_session(&mut response, session.session_token());
    Ok(response)
}

/// Decodes the bounded operation list before opening a write transaction.
/// A larger bound lets one durable group commit amortize a FULL WAL flush while
/// still keeping request memory and transaction time bounded.
const MAX_DOCUMENT_BATCH_OPERATIONS: usize = 8192;

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
    if operations.is_empty() || operations.len() > MAX_DOCUMENT_BATCH_OPERATIONS {
        return Err(ApiError {
            status: StatusCode::UNPROCESSABLE_ENTITY,
            code: "batch_size_out_of_bounds",
            message: format!("a batch is 1..={MAX_DOCUMENT_BATCH_OPERATIONS} operations"),
            fix: format!(
                "split the batch into groups of at most {MAX_DOCUMENT_BATCH_OPERATIONS} operations"
            ),
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
) -> Result<u64, (&'static str, &'static str)> {
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
            let units = terrabase::usage::write_units_for_bytes(
                terrabase::storage::estimated_document_body_bytes(
                    collection.as_str(),
                    id.as_str(),
                    serde_json::to_vec(value).map_or(0, |bytes| bytes.len() as u64),
                ),
            );
            let document = Document::new(id, collection.clone(), json_value(value));
            if transaction.put(document, ExpectedVersion::Any).is_err() {
                return Err(("staging_refused", "the put could not stage"));
            }
            Ok(units)
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
            let units = terrabase::usage::write_units_for_bytes(
                terrabase::storage::estimated_document_body_bytes(
                    collection.as_str(),
                    id.as_str(),
                    document_value_bytes(&merged),
                ),
            );
            let patched = Document::new(id, collection.clone(), merged);
            if transaction.put(patched, ExpectedVersion::Any).is_err() {
                return Err(("staging_refused", "the patch could not stage"));
            }
            Ok(units)
        }
        Some("delete") => {
            if transaction.read_in_collection(collection, &id).ok().flatten().is_none() {
                return Err(("document_not_found", "the delete target is absent"));
            }
            transaction.delete_in_collection(collection, id, ExpectedVersion::Any);
            Ok(1)
        }
        _ => Err(("unknown_op", "op must be put, patch, or delete")),
    }
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

fn decode_session_header(
    headers: &axum::http::HeaderMap,
) -> Result<Option<SessionToken>, ApiError> {
    let Some(value) = headers.get(&SESSION_HEADER) else { return Ok(None) };
    let value = value.to_str().map_err(|_| invalid_session_token())?;
    if value.is_empty() || value.len() > MAX_SESSION_HEADER_BYTES {
        return Err(invalid_session_token());
    }
    let bytes = URL_SAFE_NO_PAD.decode(value).map_err(|_| invalid_session_token())?;
    let token = SessionToken::decode(&bytes).map_err(|_| invalid_session_token())?;
    if URL_SAFE_NO_PAD.encode(token.encode()) != value {
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
    let encoded = URL_SAFE_NO_PAD.encode(token.encode());
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
) -> Result<Arc<terrabase_tbep::member_host::MemberHost>, ApiError> {
    if let Some(admitted) = &state.admitted_databases {
        let database = admitted
            .resolve_degraded_serving(name)
            .map_err(|_| ApiError::storage_failure())?
            .ok_or_else(|| ApiError::database_not_found(name))?;
        let placement = database
            .record
            .regions
            .iter()
            .filter_map(|region| region.parse().ok())
            .collect::<Vec<_>>();
        if !placement.contains(&state.region) {
            return Err(ApiError::misdirected(&placement, &database.record.residency));
        }
        let scope =
            document_scope(database.host.engine()).map_err(|()| ApiError::storage_failure())?;
        if database
            .host
            .engine()
            .lifecycle_state(&scope)
            .map_err(|_| ApiError::storage_failure())?
            .access_revoked()
        {
            return Err(ApiError::database_not_found(name));
        }
        return Ok(database.host.clone());
    }
    let record = database_record(state, name)?;
    if record.deleted {
        return Err(ApiError::database_not_found(name));
    }
    if !record.placement.contains(&state.region) {
        return Err(ApiError::misdirected(&record.placement, record.residency_header()));
    }
    let catalog = state.catalog.as_ref().ok_or_else(ApiError::control_unavailable)?;
    let host = catalog
        .host_for(&state.scope.0, &state.scope.1, name)
        .map_err(|_| ApiError::storage_failure())?;
    let tenant = terrabase::types::TenantId::try_new(host.tenant())
        .map_err(|_| ApiError::storage_failure())?;
    let project = host.engine().database_owner().project().to_string();
    let placement = terrabase::usage::UsagePlacement::try_new(
        tenant,
        &project,
        name,
        name,
        state.region,
        record.placement,
        &record.created_at.to_string(),
    )
    .map_err(|_| ApiError::storage_failure())?;
    host.engine().configure_usage_placement(placement).map_err(|_| ApiError::storage_failure())?;
    Ok(host)
}

pub(crate) fn database_record(
    state: &ApiState,
    name: &str,
) -> Result<crate::control::DatabaseRecord, ApiError> {
    if let Some(admitted) = &state.admitted_databases {
        return admitted
            .resolve_metadata(name)
            .map_err(|_| ApiError::storage_failure())?
            .map(|database| database.public_record().map_err(|_| ApiError::storage_failure()))
            .transpose()?
            .ok_or_else(|| ApiError::database_not_found(name));
    }
    state
        .control
        .as_ref()
        .ok_or_else(ApiError::control_unavailable)?
        .get(name)
        .map_err(ApiError::control)?
        .ok_or_else(|| ApiError::database_not_found(name))
}

pub(crate) fn database_records(
    state: &ApiState,
) -> Result<Vec<crate::control::DatabaseRecord>, ApiError> {
    if let Some(admitted) = &state.admitted_databases {
        return admitted.public_records().map_err(|_| ApiError::storage_failure());
    }
    state
        .control
        .as_ref()
        .ok_or_else(ApiError::control_unavailable)?
        .list()
        .map_err(ApiError::control)
}

pub(crate) fn database_owner_scope(
    state: &ApiState,
    name: &str,
) -> Result<(String, String), ApiError> {
    crate::public_admission::route_owner(state, name)
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

/// Parses create-only PUT without reading the document. The storage commit's
/// `ExpectedVersion::New` check owns the atomic condition and idempotent replay.
fn create_only_precondition(headers: &axum::http::HeaderMap) -> Result<bool, ApiError> {
    let mut values = headers.get_all(axum::http::header::IF_NONE_MATCH).iter();
    let Some(value) = values.next() else {
        return Ok(false);
    };
    if values.next().is_some()
        || value.as_bytes() != b"*"
        || headers.contains_key(axum::http::header::IF_MATCH)
    {
        return Err(ApiError {
            status: StatusCode::BAD_REQUEST,
            code: "invalid_header",
            message: "document PUT accepts only If-None-Match: * without If-Match".into(),
            fix: "send If-None-Match: * to create only, or use If-Match to update a known revision"
                .into(),
            placement: None,
        });
    }
    Ok(true)
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
        // The entity-tag form is the wire standard; the bare form is the
        // revision as the write response body reports it, so a client that
        // echoes the body value back satisfies the same precondition.
        Some(revision) if format!("\"{revision}\"") == expected || revision == expected => Ok(()),
        _ => Err(ApiError::precondition_failed("the ETag no longer matches the live revision")),
    }
}

/// The original request, independent of the live merge base and assigned revision.
fn document_request_fingerprint(
    method: &str,
    collection: &CollectionName,
    id: &DocumentId,
    value: Option<&serde_json::Value>,
    headers: &axum::http::HeaderMap,
) -> [u8; 32] {
    let mut hash = blake3::Hasher::new();
    hash.update(b"document-request-v1");
    let mut field = |bytes: &[u8]| {
        hash.update(&(bytes.len() as u64).to_le_bytes());
        hash.update(bytes);
    };
    field(method.as_bytes());
    field(collection.as_str().as_bytes());
    field(id.as_str().as_bytes());
    field(&value.map_or_else(Vec::new, |value| value.to_string().into_bytes()));
    for name in ["if-match", "if-none-match"] {
        field(name.as_bytes());
        let values = headers.get_all(name);
        field(&(values.iter().count() as u64).to_le_bytes());
        for value in values {
            field(value.as_bytes());
        }
    }
    *hash.finalize().as_bytes()
}

fn document_request_error(error: terrabase::EngineError) -> ApiError {
    match error {
        terrabase::EngineError::OperationIdReuse { .. } => ApiError {
            status: StatusCode::CONFLICT,
            code: "commit_rejected",
            message: "the idempotency key already names a different request".into(),
            fix: "retry the original method, path, body, and preconditions, or use a new key"
                .into(),
            placement: None,
        },
        _ => ApiError::storage_failure(),
    }
}

#[allow(
    clippy::too_many_arguments,
    reason = "replay needs the authorized write request and original identity"
)]
async fn replay_document_request(
    state: &ApiState,
    host: &terrabase_tbep::engine_host::EngineHost,
    _engine: &terrabase::Engine,
    scope: &terrabase::types::TenantScope,
    headers: &axum::http::HeaderMap,
    operation: &OperationId,
    fingerprint: [u8; 32],
    collection: &CollectionName,
    id: &DocumentId,
) -> Result<Option<Response>, ApiError> {
    if !has_idempotency_key(headers) {
        return Ok(None);
    }
    let Some((resolved_engine, receipt, revision)) = host
        .resolve_routed_document_request(scope, collection, id, operation.clone(), fingerprint)
        .map_err(document_request_error)?
    else {
        return Ok(None);
    };
    let resolved_scope = resolved_engine.scope_for(scope.tenant());
    let scope = &resolved_scope;
    let durability = BatchDurability::prepare(
        state,
        host,
        collection,
        std::slice::from_ref(id),
        scope,
        headers,
        true,
    )
    .await?;
    durability.confirm(host, scope, operation, &resolved_engine, scope, &receipt).await?;
    let mut session = open_session(&resolved_engine, scope, headers)?;
    let response = document_write_response(&mut session, collection, id, revision)?;
    Ok(Some(response))
}

fn document_write_response(
    session: &mut Session<'_>,
    collection: &CollectionName,
    id: &DocumentId,
    revision: Option<String>,
) -> Result<Response, ApiError> {
    let token = refresh_session(session, collection, id)?;
    Ok(document_write_response_with_token(&token, revision))
}

fn document_write_response_with_token(token: &SessionToken, revision: Option<String>) -> Response {
    let mut response = match revision {
        Some(revision) => {
            let mut response =
                json_response(StatusCode::OK, format!("{{\"revision\":\"{revision}\"}}"));
            if let Ok(etag) = HeaderValue::from_str(&format!("\"{revision}\"")) {
                response.headers_mut().insert(axum::http::header::ETAG, etag);
            }
            response
        }
        None => json_response(StatusCode::NO_CONTENT, String::new()),
    };
    with_session(&mut response, token);
    response
}

/// One document's live revision for a response `ETag`: the same probe the
/// GET path answers `If-None-Match` from, so a write's `ETag` always names
/// what the next `GET` serves.
fn routed_live_revision(
    host: &terrabase_tbep::member_host::MemberHost,
    scope: &terrabase::types::TenantScope,
    collection: &CollectionName,
    id: &DocumentId,
) -> Result<String, ApiError> {
    let operation = crate::read_operation::next().map_err(|()| ApiError::storage_failure())?;
    let transaction = host
        .engine_host()
        .begin_routed_document(scope, collection, id, operation)
        .map_err(|_| ApiError::storage_failure())?;
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
    let engine =
        match host.engine_host().document_participant(&collection, std::slice::from_ref(&id)) {
            Ok(engine) => engine,
            Err(_) => return ApiError::control_unavailable().into_response(),
        };
    let scope = engine.scope_for(scope.tenant());

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
    let Ok(operation) = crate::read_operation::next() else {
        return ApiError::storage_failure().into_response();
    };
    // Engine reads are synchronous (and may perform SQLite I/O). Keep them
    // off Tokio worker threads so concurrent HTTP requests can continue to
    // make progress while a storage read is in flight.
    let if_none_match = headers
        .get(axum::http::header::IF_NONE_MATCH)
        .and_then(|value| value.to_str().ok())
        .map(str::to_owned);
    let document =
        match blocking_on_runtime(|| -> Result<Option<(String, Option<Document>)>, ApiError> {
            let transaction = host
                .engine_host()
                .begin_routed_document(&scope, &collection, &id, operation)
                .map_err(|_| ApiError::storage_failure())?;
            let revision = transaction
                .revision_in_collection(&collection, &id)
                .map_err(|_| ApiError::storage_failure())?;
            let Some(revision) = revision else {
                return Ok(None);
            };
            let etag = format!("\"{revision}\"");
            if if_none_match.as_deref() == Some(etag.as_str()) {
                return Ok(Some((revision, None)));
            }
            let document = transaction
                .read_in_collection(&collection, &id)
                .map_err(|_| ApiError::storage_failure())?;
            Ok(document.map(|document| (revision, Some(document))))
        }) {
            Ok(Some((revision, None))) => {
                let etag = format!("\"{revision}\"");
                let mut response = StatusCode::NOT_MODIFIED.into_response();
                if let Ok(etag) = HeaderValue::from_str(&etag) {
                    response.headers_mut().insert(axum::http::header::ETAG, etag);
                }
                if let Some(session) = &session {
                    with_session(&mut response, session.session_token());
                }
                return response;
            }
            Ok(Some((revision, Some(document)))) => (revision, document),
            Ok(None) => {
                return document_not_found(collection.as_str(), id.as_str()).into_response();
            }
            Err(error) => return error.into_response(),
        };
    let (revision, document) = document;
    let etag = format!("\"{revision}\"");
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
/// bytes the configured backup retention no longer keeps.
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
    let Some(backups) = state.backups.as_ref() else {
        return ApiError::backup("native backup owner is unavailable").into_response();
    };
    let plan = backups.plan_for(name);
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
    let owner = match database_owner_scope(state, name) {
        Ok(owner) => owner,
        Err(error) => return error.into_response(),
    };
    let Ok(tenant) = terrabase::types::TenantId::try_new(&owner.0) else {
        return ApiError::control_unavailable().into_response();
    };
    let Some(timetravel) = state.timetravel.as_ref() else {
        return ApiError::backup("native backup owner is unavailable").into_response();
    };
    match timetravel.read_at(name, &tenant, at_seconds, collection, id) {
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
    let trace = std::env::var_os("TERRABASE_TRACE_PUT").is_some();
    let t0 = std::time::Instant::now();
    let mut t = t0;
    let mark = |trace: bool, t: &mut std::time::Instant, stage: &str| {
        if trace {
            let now = std::time::Instant::now();
            eprintln!("PUTTRACE {stage}: {:?}", now - *t);
            *t = now;
        }
    };
    let host = match document_host(&state, &name) {
        Ok(host) => host,
        Err(error) => return error.into_response(),
    };
    mark(trace, &mut t, "document_host");
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
    let engine =
        match host.engine_host().document_participant(&collection, std::slice::from_ref(&id)) {
            Ok(engine) => engine,
            Err(_) => return ApiError::control_unavailable().into_response(),
        };
    let scope = engine.scope_for(scope.tenant());

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
    let document = Document::new(id.clone(), collection.clone(), json_value(&value));
    if engine.validate_document_shard_route(&scope, &collection, &id).is_err() {
        return ApiError::control_unavailable().into_response();
    }
    let operation = match write_operation(&headers, "api-put") {
        Ok(operation) => operation,
        Err(error) => return error.into_response(),
    };
    let fingerprint = if has_idempotency_key(&headers) {
        document_request_fingerprint("PUT", &collection, &id, Some(&value), &headers)
    } else {
        [0; 32]
    };
    match replay_document_request(
        &state,
        host.engine_host(),
        engine,
        &scope,
        &headers,
        &operation,
        fingerprint,
        &collection,
        &id,
    )
    .await
    {
        Ok(Some(response)) => return response,
        Ok(None) => {}
        Err(error) => return error.into_response(),
    }
    let mut session = match open_session(engine, &scope, &headers) {
        Ok(session) => session,
        Err(error) => return error.into_response(),
    };
    let durability = match BatchDurability::prepare(
        &state,
        host.engine_host(),
        &collection,
        std::slice::from_ref(&id),
        &scope,
        &headers,
        false,
    )
    .await
    {
        Ok(durability) => durability,
        Err(error) => return error.into_response(),
    };
    mark(trace, &mut t, "durability_prepare");
    let mut transaction =
        match host.engine_host().begin_routed_document(&scope, &collection, &id, operation.clone())
        {
            Ok(transaction) => transaction,
            Err(terrabase::EngineError::OperationIdReuse { .. }) => {
                return durability_unknown().into_response();
            }
            Err(_) => return ApiError::storage_failure().into_response(),
        };
    // The operation reservation now excludes a simultaneous publisher of
    // this identity. Recheck before any live-state precondition or merge.
    match replay_document_request(
        &state,
        host.engine_host(),
        engine,
        &scope,
        &headers,
        &operation,
        fingerprint,
        &collection,
        &id,
    )
    .await
    {
        Ok(Some(response)) => return response,
        Ok(None) => {}
        Err(error) => return error.into_response(),
    }
    let database_id = engine.database_owner().database().to_text();
    let operation_id = transaction.operation_id().clone();
    let units =
        terrabase::usage::write_units_for_bytes(terrabase::storage::estimated_document_body_bytes(
            collection.as_str(),
            id.as_str(),
            serde_json::to_vec(&value).map_or(0, |bytes| bytes.len() as u64),
        ));
    if let Err(error) = attach_write_budget(
        &state,
        &mut transaction,
        &scope,
        engine.database_owner().project().to_string().as_str(),
        &database_id,
        &operation_id,
        units,
    ) {
        return error.into_response();
    }
    let create_only = match create_only_precondition(&headers) {
        Ok(create_only) => create_only,
        Err(error) => return error.into_response(),
    };
    if let Err(error) = check_if_match(&headers, &transaction, document.collection(), document.id())
    {
        return error.into_response();
    }
    // Keep the create-only condition in the durable write-set digest so a
    // repeated idempotency key replays its original result. Existence is
    // tested atomically at commit, including concurrent creates.
    let expected = if create_only {
        ExpectedVersion::New
    } else if has_idempotency_key(&headers) {
        ExpectedVersion::Any
    } else {
        match transaction.read_in_collection(document.collection(), document.id()) {
            Ok(Some(existing)) => ExpectedVersion::Exact(existing.metadata().version()),
            Ok(None) => ExpectedVersion::New,
            Err(_) => return ApiError::storage_failure().into_response(),
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
    if has_idempotency_key(&headers)
        && let Err(error) = transaction.bind_document_request(fingerprint) {
            return document_request_error(error).into_response();
        }
    if let Err(error) = durability.guard(
        &mut transaction,
        host.engine_host(),
        &collection,
        std::slice::from_ref(&id),
    ) {
        return error.into_response();
    }
    mark(trace, &mut t, "staging");
    let outcome = commit_on_runtime(transaction, LocalDurability::Synced);
    if !matches!(outcome, CommitOutcome::Committed(_)) {
        match replay_document_request(
            &state,
            host.engine_host(),
            engine,
            &scope,
            &headers,
            &operation,
            fingerprint,
            &collection,
            &id,
        )
        .await
        {
            Ok(Some(response)) => return response,
            Ok(None) => {}
            Err(error) => return error.into_response(),
        }
    }
    match outcome {
        CommitOutcome::Committed(receipt) => {
            mark(trace, &mut t, "commit");
            if let Err(error) = durability
                .confirm(host.engine_host(), &scope, &operation_id, engine, &scope, &receipt)
                .await
            {
                return error.into_response();
            }
            let revision = if has_idempotency_key(&headers) {
                match engine.resolve_document_request(&scope, operation.clone(), fingerprint) {
                    Ok(Some((_, revision))) => revision,
                    Ok(None) => return ApiError::storage_failure().into_response(),
                    Err(error) => return document_request_error(error).into_response(),
                }
            } else {
                match routed_live_revision(&host, &scope, &collection, &id) {
                    Ok(revision) => Some(revision),
                    Err(error) => return error.into_response(),
                }
            };
            match document_write_response(&mut session, &collection, &id, revision) {
                Ok(response) => response,
                Err(error) => error.into_response(),
            }
        }
        CommitOutcome::Rejected(terrabase::CommitError::Conflict) if create_only => {
            ApiError::precondition_failed("If-None-Match: * needs the document to be absent")
                .into_response()
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
    let (collection, id, value) = match patch_request_parts(&collection, &id, &body) {
        Ok(parts) => parts,
        Err(error) => return error.into_response(),
    };

    let engine =
        match host.engine_host().document_participant(&collection, std::slice::from_ref(&id)) {
            Ok(engine) => engine,
            Err(_) => return ApiError::control_unavailable().into_response(),
        };
    let scope = engine.scope_for(scope.tenant());
    if engine.validate_document_shard_route(&scope, &collection, &id).is_err() {
        return ApiError::control_unavailable().into_response();
    }
    let operation = match write_operation(&headers, "api-patch") {
        Ok(operation) => operation,
        Err(error) => return error.into_response(),
    };
    let fingerprint = if has_idempotency_key(&headers) {
        document_request_fingerprint("PATCH", &collection, &id, Some(&value), &headers)
    } else {
        [0; 32]
    };
    match replay_document_request(
        &state,
        host.engine_host(),
        engine,
        &scope,
        &headers,
        &operation,
        fingerprint,
        &collection,
        &id,
    )
    .await
    {
        Ok(Some(response)) => return response,
        Ok(None) => {}
        Err(error) => return error.into_response(),
    }
    let mut session = match open_session(engine, &scope, &headers) {
        Ok(session) => session,
        Err(error) => return error.into_response(),
    };
    let durability = match BatchDurability::prepare(
        &state,
        host.engine_host(),
        &collection,
        std::slice::from_ref(&id),
        &scope,
        &headers,
        false,
    )
    .await
    {
        Ok(durability) => durability,
        Err(error) => return error.into_response(),
    };
    let mut transaction =
        match host.engine_host().begin_routed_document(&scope, &collection, &id, operation.clone())
        {
            Ok(transaction) => transaction,
            Err(terrabase::EngineError::OperationIdReuse { .. }) => {
                return durability_unknown().into_response();
            }
            Err(_) => return ApiError::storage_failure().into_response(),
        };
    // The operation reservation now excludes a simultaneous publisher of
    // this identity. Recheck before any live-state precondition or merge.
    match replay_document_request(
        &state,
        host.engine_host(),
        engine,
        &scope,
        &headers,
        &operation,
        fingerprint,
        &collection,
        &id,
    )
    .await
    {
        Ok(Some(response)) => return response,
        Ok(None) => {}
        Err(error) => return error.into_response(),
    }
    let database_id = engine.database_owner().database().to_text();
    let operation_id = transaction.operation_id().clone();
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
    let units =
        terrabase::usage::write_units_for_bytes(terrabase::storage::estimated_document_body_bytes(
            collection.as_str(),
            id.as_str(),
            document_value_bytes(&merged),
        ));
    let patched = Document::new(id.clone(), collection.clone(), merged);
    if let Err(error) = attach_write_budget(
        &state,
        &mut transaction,
        &scope,
        engine.database_owner().project().to_string().as_str(),
        &database_id,
        &operation_id,
        units,
    ) {
        return error.into_response();
    }
    // The original patch is replayed from its durable fingerprint; a new
    // publication must still hold the exact merge-base version.
    let expected = ExpectedVersion::Exact(existing.metadata().version());
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
    if has_idempotency_key(&headers)
        && let Err(error) = transaction.bind_document_request(fingerprint) {
            return document_request_error(error).into_response();
        }
    if let Err(error) = durability.guard(
        &mut transaction,
        host.engine_host(),
        &collection,
        std::slice::from_ref(&id),
    ) {
        return error.into_response();
    }
    let outcome = commit_on_runtime(transaction, LocalDurability::Synced);
    if !matches!(outcome, CommitOutcome::Committed(_)) {
        match replay_document_request(
            &state,
            host.engine_host(),
            engine,
            &scope,
            &headers,
            &operation,
            fingerprint,
            &collection,
            &id,
        )
        .await
        {
            Ok(Some(response)) => return response,
            Ok(None) => {}
            Err(error) => return error.into_response(),
        }
    }
    match outcome {
        CommitOutcome::Committed(receipt) => {
            if let Err(error) = durability
                .confirm(host.engine_host(), &scope, &operation_id, engine, &scope, &receipt)
                .await
            {
                return error.into_response();
            }
            let revision = if has_idempotency_key(&headers) {
                match engine.resolve_document_request(&scope, operation.clone(), fingerprint) {
                    Ok(Some((_, revision))) => revision,
                    Ok(None) => return ApiError::storage_failure().into_response(),
                    Err(error) => return document_request_error(error).into_response(),
                }
            } else {
                match routed_live_revision(&host, &scope, &collection, &id) {
                    Ok(revision) => Some(revision),
                    Err(error) => return error.into_response(),
                }
            };
            match document_write_response(&mut session, &collection, &id, revision) {
                Ok(response) => response,
                Err(error) => error.into_response(),
            }
        }
        CommitOutcome::Unknown(_) => durability_unknown().into_response(),
        CommitOutcome::Rejected(reason) => ApiError {
            status: StatusCode::CONFLICT,
            code: "commit_rejected",
            message: format!("the engine refused the patch: {reason:?}"),
            fix: "check whether it landed; retry the identical request with its original Idempotency-Key".into(),
            placement: None,
        }
        .into_response(),
    }
}

fn patch_request_parts(
    collection: &str,
    id: &str,
    body: &str,
) -> Result<(CollectionName, DocumentId, serde_json::Value), ApiError> {
    let collection = CollectionName::try_new(collection)
        .map_err(|_| invalid_identifier("collection name", collection))?;
    let id = DocumentId::try_new(id).map_err(|_| invalid_identifier("document identifier", id))?;
    let value = parse_merge_patch(body)?;
    Ok((collection, id, value))
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
                body.push_str(",\"commitUlid\":\"");
                push_json_escaped(&mut body, &entry.commit_ulid);
                body.push_str("\",\"hlc\":");
                body.push_str(&entry.hlc.to_string());
                body.push_str(",\"origin\":\"");
                push_json_escaped(&mut body, &entry.origin);
                body.push_str("\",\"policy\":\"");
                push_json_escaped(&mut body, &entry.policy);
                body.push_str("\",\"explanation\":\"");
                push_json_escaped(&mut body, &entry.explanation);
                body.push('"');
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
    let engine =
        match host.engine_host().document_participant(&collection, std::slice::from_ref(&id)) {
            Ok(engine) => engine,
            Err(_) => return ApiError::control_unavailable().into_response(),
        };
    let scope = engine.scope_for(scope.tenant());

    if engine.validate_document_shard_route(&scope, &collection, &id).is_err() {
        return ApiError::control_unavailable().into_response();
    }
    let operation = match write_operation(&headers, "api-delete") {
        Ok(operation) => operation,
        Err(error) => return error.into_response(),
    };
    let fingerprint = if has_idempotency_key(&headers) {
        document_request_fingerprint("DELETE", &collection, &id, None, &headers)
    } else {
        [0; 32]
    };
    match replay_document_request(
        &state,
        host.engine_host(),
        engine,
        &scope,
        &headers,
        &operation,
        fingerprint,
        &collection,
        &id,
    )
    .await
    {
        Ok(Some(response)) => return response,
        Ok(None) => {}
        Err(error) => return error.into_response(),
    }
    let mut session = match open_session(engine, &scope, &headers) {
        Ok(session) => session,
        Err(error) => return error.into_response(),
    };
    let durability = match BatchDurability::prepare(
        &state,
        host.engine_host(),
        &collection,
        std::slice::from_ref(&id),
        &scope,
        &headers,
        false,
    )
    .await
    {
        Ok(durability) => durability,
        Err(error) => return error.into_response(),
    };
    let mut transaction =
        match host.engine_host().begin_routed_document(&scope, &collection, &id, operation.clone())
        {
            Ok(transaction) => transaction,
            Err(terrabase::EngineError::OperationIdReuse { .. }) => {
                return durability_unknown().into_response();
            }
            Err(_) => return ApiError::storage_failure().into_response(),
        };
    // The operation reservation now excludes a simultaneous publisher of
    // this identity. Recheck before any live-state precondition or merge.
    match replay_document_request(
        &state,
        host.engine_host(),
        engine,
        &scope,
        &headers,
        &operation,
        fingerprint,
        &collection,
        &id,
    )
    .await
    {
        Ok(Some(response)) => return response,
        Ok(None) => {}
        Err(error) => return error.into_response(),
    }
    if let Err(error) = check_if_match(&headers, &transaction, &collection, &id) {
        return error.into_response();
    }
    let present = match transaction.read_in_collection(&collection, &id) {
        Ok(document) => document.is_some(),
        Err(_) => return ApiError::storage_failure().into_response(),
    };
    if !present {
        return document_not_found(collection.as_str(), id.as_str()).into_response();
    }
    let database_id = engine.database_owner().database().to_text();
    let operation_id = transaction.operation_id().clone();
    if let Err(error) = attach_write_budget(
        &state,
        &mut transaction,
        &scope,
        engine.database_owner().project().to_string().as_str(),
        &database_id,
        &operation_id,
        1,
    ) {
        return error.into_response();
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
    if has_idempotency_key(&headers)
        && let Err(error) = transaction.bind_document_request(fingerprint) {
            return document_request_error(error).into_response();
        }
    if let Err(error) = durability.guard(
        &mut transaction,
        host.engine_host(),
        &collection,
        std::slice::from_ref(&id),
    ) {
        return error.into_response();
    }
    let outcome = commit_on_runtime(transaction, LocalDurability::Synced);
    if !matches!(outcome, CommitOutcome::Committed(_)) {
        match replay_document_request(
            &state,
            host.engine_host(),
            engine,
            &scope,
            &headers,
            &operation,
            fingerprint,
            &collection,
            &id,
        )
        .await
        {
            Ok(Some(response)) => return response,
            Ok(None) => {}
            Err(error) => return error.into_response(),
        }
    }
    match outcome {
        CommitOutcome::Committed(receipt) => {
            if let Err(error) = durability
                .confirm(host.engine_host(), &scope, &operation_id, engine, &scope, &receipt)
                .await
            {
                return error.into_response();
            }
            let revision = if has_idempotency_key(&headers) {
                match engine.resolve_document_request(&scope, operation.clone(), fingerprint) {
                    Ok(Some((_, revision))) => revision,
                    Ok(None) => return ApiError::storage_failure().into_response(),
                    Err(error) => return document_request_error(error).into_response(),
                }
            } else {
                None
            };
            match document_write_response(&mut session, &collection, &id, revision) {
                Ok(response) => response,
                Err(error) => error.into_response(),
            }
        }
        CommitOutcome::Unknown(_) => durability_unknown().into_response(),
        CommitOutcome::Rejected(reason) => ApiError {
            status: StatusCode::CONFLICT,
            code: "commit_rejected",
            message: format!("the engine refused the deletion: {reason:?}"),
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
    let mut session = if headers.contains_key(&SESSION_HEADER) {
        match open_session(engine, &scope, &headers) {
            Ok(session) => Some(session),
            Err(error) => return error.into_response(),
        }
    } else {
        None
    };
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
        let mut response = json_response(StatusCode::OK, explained);
        if let Some(session) = &session {
            with_session(&mut response, session.session_token());
        }
        return response;
    }
    // Query planning above is in-memory, but snapshot acquisition and query
    // execution synchronously scan SQLite-backed storage. Keep that work off
    // Tokio workers under the normal multithread runtime.
    let execution = match blocking_on_runtime(|| {
        let snapshot = match &mut session {
            Some(session) => session.read_snapshot().map_err(session_error),
            None => engine.read_snapshot(&scope).map_err(|_| ApiError::storage_failure()),
        }?;
        let admitted_cut = host
            .engine_host()
            .admitted_shard_read_cut()
            .map_err(|_| ApiError::storage_failure())?;
        Ok::<_, ApiError>(match admitted_cut.as_ref() {
            Some(cut) if cut.route_generation.is_some() => store.execute_admitted(&plan, cut),
            _ => store.execute(&plan, &snapshot),
        })
    }) {
        Ok(execution) => execution,
        Err(error) => return error.into_response(),
    };
    match execution {
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
            let mut response = json_response(StatusCode::OK, rendered);
            if let Some(session) = &session {
                with_session(&mut response, session.session_token());
            }
            response
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

/// Backups run per file (S-06): readiness is an artifact
/// fact, never "the child process is alive".
#[utoipa::path(get, path = "/api/v1/databases/{name}/backups", responses((status = 200, description = "Backup readiness")))]
async fn list_backups(State(state): State<Arc<ApiState>>, Path(name): Path<String>) -> Response {
    if let Err(error) = document_host(&state, &name) {
        return error.into_response();
    }
    if let Err(error) = check_read_guard(&state, &name, terrabase::observability::Operation::Read) {
        return error.into_response();
    }
    let (ready, streaming) = if let Some(native) = &state.native_backups {
        let ready = match native.is_ready_async(&name).await {
            Ok(value) => value,
            Err(error) => return ApiError::backup(error).into_response(),
        };
        let streaming = match native.attached_async(&name).await {
            Ok(value) => value,
            Err(error) => return ApiError::backup(error).into_response(),
        };
        (ready, streaming)
    } else if let Some(backups) = &state.backups {
        (backups.is_ready(&name), backups.attached(&name))
    } else {
        return ApiError::backup("backup owner unavailable").into_response();
    };
    let body = format!("{{\"ready\":{ready},\"streaming\":{streaming}}}");
    if let Err(error) = account_direct_read(&state, &name, response_bytes(body.len())) {
        return error.into_response();
    }
    json_response(StatusCode::OK, body)
}

#[derive(serde::Deserialize)]
#[serde(deny_unknown_fields)]
struct BackupPointQuery {
    limit: Option<usize>,
    cursor: Option<String>,
}

/// Lists authenticated native logical recovery points in bounded keyset pages.
#[utoipa::path(get, path = "/api/v1/databases/{name}/backups/points", params(("limit" = Option<usize>, Query, description = "Page size, default 50, maximum 100"), ("cursor" = Option<String>, Query, description = "Opaque source-bound continuation cursor")), responses((status = 200, description = "Retained backup points", body = crate::backup_point_catalog::BackupPointPage)))]
async fn list_backup_points(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
    Extension(principal): Extension<crate::api_keys::Principal>,
    extract::Query(query): extract::Query<BackupPointQuery>,
) -> Response {
    if !principal.scope().admits_database(Verbs::ADMIN, &name) {
        return ApiError::forbidden("backup point listing").into_response();
    }
    let Some(registry) = &state.admitted_databases else {
        return ApiError::control_unavailable().into_response();
    };
    let source = match registry.resolve_serving(&name) {
        Ok(Some(value)) => value,
        Ok(None) => return ApiError::database_not_found(&name).into_response(),
        Err(_) => return ApiError::storage_failure().into_response(),
    };
    let Some((tenant, project)) = principal.owner() else {
        return ApiError::forbidden("backup point listing requires a project credential")
            .into_response();
    };
    if source.record.tenant_id != tenant || source.record.project_id != project {
        return ApiError::forbidden("backup source is outside credential scope").into_response();
    }
    let Some(runtime) = &state.native_backups else {
        return ApiError::backup("native backup point catalog unavailable").into_response();
    };
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |duration| duration.as_millis().min(u128::from(u64::MAX)) as u64);
    match crate::backup_point_catalog::list_backup_points(
        runtime,
        &source.record.database_id,
        query.limit,
        query.cursor.as_deref(),
        now,
    )
    .await
    {
        Ok(page) => json_response(StatusCode::OK, serde_json::to_string(&page).unwrap_or_default()),
        Err(
            crate::backup_point_catalog::BackupPointCatalogError::InvalidLimit
            | crate::backup_point_catalog::BackupPointCatalogError::InvalidCursor,
        ) => provisioning_error(crate::provisioning::ProvisioningError::InvalidRequest)
            .into_response(),
        Err(error) => ApiError::backup(error).into_response(),
    }
}

/// `POST /databases/{name}/backups/restore` — restores to a NEW database
/// (D-29: a restore never lands on the live file), optionally at a point
/// in time (`"at"`: unix milliseconds).
#[utoipa::path(post, path = "/api/v1/databases/{name}/backups/restore", responses((status = 202, description = "Backup restore accepted", body = crate::provisioning::ProvisioningOperation)))]
async fn restore_backup(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
    Extension(principal): Extension<crate::api_keys::Principal>,
    body: String,
) -> Response {
    if !principal.scope().admits_database(Verbs::ADMIN, &name) {
        return ApiError::forbidden("database restore").into_response();
    }
    if let Some(authority) = &state.provisioning {
        let registry = match &state.admitted_databases {
            Some(registry) => registry,
            None => return ApiError::control_unavailable().into_response(),
        };
        let source = match registry.resolve_serving(&name) {
            Ok(Some(value)) => value,
            Ok(None) => return ApiError::database_not_found(&name).into_response(),
            Err(_) => return ApiError::storage_failure().into_response(),
        };
        let Some((principal_tenant, principal_project)) = principal.owner() else {
            return ApiError::forbidden("restore requires a project credential").into_response();
        };
        if source.record.tenant_id != principal_tenant
            || source.record.project_id != principal_project
        {
            return ApiError::forbidden("restore source is outside credential scope")
                .into_response();
        }
        let value: serde_json::Value = match serde_json::from_str(&body) {
            Ok(value) => value,
            Err(_) => {
                return provisioning_error(crate::provisioning::ProvisioningError::InvalidRequest)
                    .into_response();
            }
        };
        let (target, operation, selection) = match parse_restore_request(&value) {
            Ok(request) => request,
            Err(()) => {
                return provisioning_error(crate::provisioning::ProvisioningError::InvalidRequest)
                    .into_response();
            }
        };
        let scope = crate::delegated_identity::VerifiedProjectScope {
            tenant_id: source.record.tenant_id.clone(),
            project_id: source.record.project_id.clone(),
        };
        return match authority
            .restore(&scope, &source.record.database_id, &target, selection, &operation)
            .await
        {
            Ok(value) => json_response(
                StatusCode::ACCEPTED,
                serde_json::to_string(&value).unwrap_or_default(),
            ),
            Err(error) => provisioning_error(error).into_response(),
        };
    }
    // Local/development members use the Litestream adapter. A production
    // composition has native backup state but no provisioning authority and
    // must fail closed rather than silently crossing into the local path.
    if state.native_backups.is_some() || state.native_recovery.is_some() {
        return ApiError::backup("native restore provisioning authority is unavailable")
            .into_response();
    }
    restore_or_branch(state, name, body, RestoreFlavor::Restore).await
}

/// `POST /databases/{name}/branches` — restore plus register with parent
/// metadata (D-38/D-39).
#[utoipa::path(post, path = "/api/v1/databases/{name}/branches", responses((status = 202, description = "Durable branch provisioning accepted; not ready to serve", body = crate::provisioning::ProvisioningOperation)))]
async fn create_branch(
    State(state): State<Arc<ApiState>>,
    Path(name): Path<String>,
    Extension(principal): Extension<crate::api_keys::Principal>,
    body: String,
) -> Response {
    if !principal.scope().admits_database(Verbs::ADMIN, &name) {
        return ApiError::forbidden("whole database branching").into_response();
    }
    if let Some(authority) = &state.provisioning {
        if let Err(error) = document_host(&state, &name) {
            return error.into_response();
        }
        let Some(registry) = &state.admitted_databases else {
            return ApiError::control_unavailable().into_response();
        };
        let parent = match registry.resolve_serving(&name) {
            Ok(Some(value)) => value,
            Ok(None) => return ApiError::database_not_found(&name).into_response(),
            Err(_) => return ApiError::storage_failure().into_response(),
        };
        let value: serde_json::Value = match serde_json::from_str(&body) {
            Ok(value) => value,
            Err(_) => {
                return provisioning_error(crate::provisioning::ProvisioningError::InvalidRequest)
                    .into_response();
            }
        };
        let (Some(target), Some(operation)) = (
            value.get("name").and_then(serde_json::Value::as_str),
            value.get("operation_id").and_then(serde_json::Value::as_str),
        ) else {
            return provisioning_error(crate::provisioning::ProvisioningError::InvalidRequest)
                .into_response();
        };
        if value.get("at").is_some() {
            return provisioning_error(crate::provisioning::ProvisioningError::InvalidRequest)
                .into_response();
        }
        let scope = crate::delegated_identity::VerifiedProjectScope {
            tenant_id: parent.record.tenant_id.clone(),
            project_id: parent.record.project_id.clone(),
        };
        return match authority.branch(&scope, &parent.record, target, operation).await {
            Ok(value) => json_response(
                StatusCode::ACCEPTED,
                serde_json::to_string(&value).unwrap_or_default(),
            ),
            Err(error) => provisioning_error(error).into_response(),
        };
    }
    // A composed native owner must never silently cross back into the local
    // control/catalog adapter. Native branch admission is performed by the
    // authenticated provisioning ingress and its orchestrator pipeline; an
    // incomplete production composition therefore fails closed.
    if state.native_backups.is_some() || state.native_recovery.is_some() {
        return ApiError::backup("native branch provisioning authority is unavailable")
            .into_response();
    }
    restore_or_branch(state, name, body, RestoreFlavor::Branch).await
}

/// Whether the new database registers plain or as a branch.
enum RestoreFlavor {
    Restore,
    Branch,
}

/// Parses a native restore request without allowing malformed selectors or
/// unknown fields to silently select the latest backup.
fn parse_restore_request(
    value: &serde_json::Value,
) -> Result<(String, String, serde_json::Value), ()> {
    let object = value.as_object().ok_or(())?;
    if object.keys().any(|key| !matches!(key.as_str(), "name" | "operation_id" | "at" | "point_id"))
    {
        return Err(());
    }
    let target = object.get("name").and_then(serde_json::Value::as_str).ok_or(())?;
    let operation = object.get("operation_id").and_then(serde_json::Value::as_str).ok_or(())?;
    if target.is_empty() || !valid_restore_operation_id(operation) {
        return Err(());
    }
    let at = restore_at_millis(object.get("at"))?;
    let point_id = object
        .get("point_id")
        .map(|value| value.as_str().filter(|point_id| valid_restore_point_id(point_id)).ok_or(()));
    let point_id = match point_id {
        Some(point_id) => Some(point_id?),
        None => None,
    };
    if at.is_some() && point_id.is_some() {
        return Err(());
    }
    let selection = match (at, point_id) {
        (Some(unix_millis), None) => {
            serde_json::json!({"kind":"unix_millis","unix_millis":unix_millis})
        }
        (None, Some(point_id)) => serde_json::json!({"kind":"point_id","point_id":point_id}),
        (None, None) => serde_json::json!({"kind":"latest"}),
        (Some(_), Some(_)) => unreachable!("mutually exclusive restore selectors"),
    };
    Ok((target.to_owned(), operation.to_owned(), selection))
}

fn valid_restore_point_id(value: &str) -> bool {
    value.len() == 64
        && value.bytes().all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

/// Parses the optional restore point without allowing malformed values to
/// silently select the latest backup.
fn restore_at_millis(value: Option<&serde_json::Value>) -> Result<Option<i64>, ()> {
    match value {
        None => Ok(None),
        Some(value) => value.as_i64().filter(|millis| *millis >= 0).map(Some).ok_or(()),
    }
}

fn valid_restore_operation_id(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 128
        && value.bytes().all(|byte| (0x21..=0x7e).contains(&byte))
}

/// The shared restore-to-new-database path: register the target record,
/// restore the file into the target's catalog location, and answer the
/// record. A failed restore soft-deletes the record it just made so no
/// half-born database lingers as live.
#[allow(
    clippy::needless_pass_by_value,
    reason = "the axum handlers move owned state, name, body, and flavor into this shared path"
)]
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
    if let Some(operation) = request.get("operation_id")
        && operation.as_str().is_none_or(|operation| !valid_restore_operation_id(operation)) {
            return ApiError {
                status: StatusCode::BAD_REQUEST,
                code: "invalid_operation_id",
                message: "the operation_id must be printable ASCII text of at most 128 bytes"
                    .into(),
                fix: "send a stable non-empty operation_id, or omit it on a local member".into(),
                placement: None,
            }
            .into_response();
        }
    let at = match restore_at_millis(request.get("at")) {
        Ok(at) => at,
        Err(()) => return invalid_at("it must be a non-negative integer or null"),
    };
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
    let at_seconds = at.map(|millis| u64::try_from(millis / 1000).unwrap_or(0));
    let Some(backups) = state.backups.as_ref() else {
        return ApiError::backup("native backup restore authority is unavailable").into_response();
    };
    match backups.restore_to_location(&name, at_seconds, &location) {
        Ok(()) => {
            // The new database is a main.db from its first moment: it gets
            // its own stream under the source's plan (Free until the
            // billing lane supplies plans; D-25 retention, fail-closed).
            let plan = backups.plan_for(&name);
            if let Err(error) =
                attach_backup(backups, target, &location, &state.backup_root, &plan, state.region)
            {
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
    let tenant = if engine.owner_scope_enforced() {
        terrabase::types::TenantId::try_new(&engine.database_owner().tenant().to_string())
            .map_err(|_| ())?
    } else {
        local_tenant()?
    };
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
fn document_value_bytes(value: &DocumentValue) -> u64 {
    let mut encoded = String::new();
    push_value(&mut encoded, value);
    encoded.len() as u64
}

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

/// Commits a staged transaction without occupying a Tokio worker while
/// SQLite performs synchronous durable I/O. The direct path is required for
/// current-thread runtimes and callers outside Tokio.
pub(crate) fn commit_on_runtime(
    transaction: Transaction<'_>,
    durability: LocalDurability,
) -> CommitOutcome {
    let multi_thread = tokio::runtime::Handle::try_current()
        .is_ok_and(|handle| handle.runtime_flavor() == tokio::runtime::RuntimeFlavor::MultiThread);
    if multi_thread {
        tokio::task::block_in_place(|| transaction.commit(durability))
    } else {
        transaction.commit(durability)
    }
}

/// Runs synchronous engine work without occupying an async worker on the
/// multithread runtime. A direct call is retained for current-thread runtimes
/// (where `block_in_place` panics) and callers outside Tokio.
pub(crate) fn blocking_on_runtime<F, T>(work: F) -> T
where
    F: FnOnce() -> T,
{
    let multi_thread = tokio::runtime::Handle::try_current()
        .is_ok_and(|handle| handle.runtime_flavor() == tokio::runtime::RuntimeFlavor::MultiThread);
    if multi_thread { tokio::task::block_in_place(work) } else { work() }
}

#[cfg(test)]
mod tests {
    #[path = "../../api_restore_selection_tests.rs"]
    mod api_restore_selection_tests;
    #[path = "api_session_query_tests.rs"]
    mod api_session_query_tests;
    #[path = "batch_replay.rs"]
    mod batch_replay;
    #[path = "../../budget_http_tests.rs"]
    mod budget_http_tests;
    #[path = "../../byo_router_tests.rs"]
    mod byo_router_tests;
    #[path = "conditional_create.rs"]
    mod conditional_create;
    #[path = "conditional_replay.rs"]
    mod conditional_replay;
    mod cors;
    #[path = "put_causal_snapshot.rs"]
    mod put_causal_snapshot;
    pub(super) mod rf2;
    mod shared_journal;
    mod websocket;
    use super::API_PREFIX;
    use super::ApiError;
    use super::ApiState;
    use super::SESSION_HEADER;
    use super::commit_on_runtime;
    use super::decode_session_header;
    use super::openapi_json;
    use super::router;
    use axum::body::Body;
    use axum::http::HeaderValue;
    use axum::http::Request;
    use axum::http::StatusCode;
    use axum::http::header;
    use axum::response::IntoResponse;
    use base64::{Engine as _, engine::general_purpose::URL_SAFE_NO_PAD};
    use std::sync::Arc;
    use terrabase::OriginLaneId;
    use terrabase::SessionEpoch;
    use terrabase::SessionToken;
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
    use terrabase::types::RegionId;
    use tower::util::ServiceExt;

    fn staged_runtime_transaction() -> terrabase::Transaction<'static> {
        let root = std::env::temp_dir()
            .join(format!("terrabase-commit-runtime-{}", terrabase::identity::generate()));
        let engine = terrabase::Engine::open(
            terrabase::EngineConfig::durable(root).expect("durable config"),
        )
        .expect("engine");
        let engine: &'static terrabase::Engine = Box::leak(Box::new(engine));
        let owner = engine.database_owner();
        let tenant =
            terrabase::types::TenantId::try_new(&owner.tenant().to_text()).expect("tenant");
        let scope = engine.scope_for(&tenant);
        let snapshot = engine.read_snapshot(&scope).expect("snapshot");
        let mut transaction = engine
            .begin(&scope, snapshot, OperationId::try_new("runtime-commit").expect("operation"))
            .expect("transaction");
        transaction
            .put(
                Document::new(
                    DocumentId::try_new("runtime-doc").expect("document id"),
                    CollectionName::try_new("runtime").expect("collection"),
                    DocumentValue::Null,
                ),
                ExpectedVersion::Any,
            )
            .expect("stage");
        transaction
    }

    #[tokio::test(flavor = "current_thread")]
    async fn runtime_commit_current_thread_uses_direct_commit() {
        let transaction = staged_runtime_transaction();
        assert!(matches!(
            commit_on_runtime(transaction, LocalDurability::Synced),
            terrabase::CommitOutcome::Committed(_)
        ));
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn runtime_commit_multi_thread_publishes_successfully() {
        let transaction = staged_runtime_transaction();
        assert!(matches!(
            commit_on_runtime(transaction, LocalDurability::Synced),
            terrabase::CommitOutcome::Committed(_)
        ));
    }

    #[tokio::test]
    async fn delegated_identity_admitted_databases_bind_owners_and_ignore_selectors() {
        let (registry, member, records, root) = crate::admitted_databases::tests::fixture();
        let first = &records[0];
        let second = &records[1];
        let (verifier, _, authority_task) = crate::delegated_identity::tests::authority_for(
            &first.tenant_id,
            &first.project_id,
            &first.database_id,
            "read",
        )
        .await;
        let mut state = state();
        let editable = Arc::get_mut(&mut state).unwrap();
        editable.admitted_databases = Some(Arc::new(registry));
        editable.delegated_identity = Some(Arc::new(verifier));
        editable.scope = (second.tenant_id.clone(), second.project_id.clone());
        let keys = crate::api_keys::KeyStore::empty();
        keys.register_for_owner(
            "tskey_tenant_bound_abcdefghijkl",
            "tenant-key",
            crate::api_keys::Scope::every_database(crate::api_keys::Verbs::ALL),
            (second.tenant_id.clone(), second.project_id.clone()),
            None,
        );
        editable.keys = Some(Arc::new(keys));
        let app = router(state);
        let request = |token: &str, database: &str| {
            Request::builder()
                .uri(format!("/api/v1/databases/{database}/collections"))
                .header("authorization", format!("Bearer {token}"))
                .header("x-terrascale-tenant-id", &second.tenant_id)
                .header("x-terrascale-project-id", &second.project_id)
                .body(Body::empty())
                .unwrap()
        };
        let admitted =
            app.clone().oneshot(request("delegated-token", &first.database_id)).await.unwrap();
        assert_eq!(admitted.status(), StatusCode::OK);
        let denied = app
            .clone()
            .oneshot(request("tskey_tenant_bound_abcdefghijkl", &first.database_id))
            .await
            .unwrap();
        assert_eq!(denied.status(), StatusCode::UNAUTHORIZED);
        let allowed_key = app
            .clone()
            .oneshot(request("tskey_tenant_bound_abcdefghijkl", &second.database_id))
            .await
            .unwrap();
        assert_eq!(allowed_key.status(), StatusCode::OK);
        assert_eq!(
            app.clone().oneshot(request("delegated-token", "same-name")).await.unwrap().status(),
            StatusCode::NOT_FOUND
        );
        authority_task.abort();
        drop(app);
        member.close(std::time::Duration::from_secs(1)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }

    #[tokio::test]
    async fn delegated_identity_project_selectors_require_live_administration() {
        let tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
        let project = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
        let (verifier, reply, task) =
            crate::delegated_identity::tests::authority_for(tenant, project, "*", "admin").await;
        let mut state = state();
        Arc::get_mut(&mut state).unwrap().delegated_identity = Some(Arc::new(verifier));
        let app = router(state);
        let request = || {
            Request::builder()
                .method("POST")
                .uri("/api/v1/databases")
                .header("authorization", "Bearer delegated-token")
                .header("x-terrascale-tenant-id", tenant)
                .header("x-terrascale-project-id", project)
                .body(Body::empty())
                .unwrap()
        };
        let admitted = app.clone().oneshot(request()).await.unwrap();
        assert_ne!(admitted.status(), StatusCode::UNAUTHORIZED);
        assert_eq!(admitted.headers()["x-terrascale-principal"], "user-1");
        reply.lock().unwrap()["scopes"] = serde_json::json!(["database:read"]);
        assert_eq!(app.oneshot(request()).await.unwrap().status(), StatusCode::UNAUTHORIZED);
        task.abort();
    }

    #[tokio::test]
    async fn delegated_identity_is_live_on_actual_member_router() {
        let (verifier, reply, task) = crate::delegated_identity::tests::authority().await;
        let mut state = state();
        Arc::get_mut(&mut state).unwrap().scope.0 =
            crate::delegated_identity::tests::TEST_TENANT.into();
        Arc::get_mut(&mut state).unwrap().delegated_identity = Some(Arc::new(verifier));
        let app = router(state);
        let request = || {
            Request::builder()
                .uri("/api/v1/databases/db-a")
                .header("authorization", "Bearer delegated-token")
                .body(Body::empty())
                .unwrap()
        };
        let admitted = app.clone().oneshot(request()).await.unwrap();
        assert_ne!(admitted.status(), StatusCode::UNAUTHORIZED);
        assert_eq!(admitted.headers()["x-terrascale-principal"], "user-1");
        reply.lock().unwrap()["active"] = serde_json::json!(false);
        let revoked = app.oneshot(request()).await.unwrap();
        assert_eq!(revoked.status(), StatusCode::UNAUTHORIZED);
        task.abort();
    }

    fn state() -> Arc<ApiState> {
        let backups = Arc::new(crate::backup::BackupStreams::open_member());
        let backup_root = std::env::temp_dir().join("terrabase-api-test-backup-root");
        let timetravel =
            Arc::new(crate::backup::TimeTravelCache::new(Arc::clone(&backups), &backup_root));
        Arc::new(ApiState {
            budget: None,
            native_backups: None,
            native_recovery: None,
            native_restore_target: None,
            development: Arc::default(),
            provisioning: None,
            admitted_databases: None,
            replication_progress: None,
            standalone_durability: false,
            region: RegionCode::GRU,
            member_id: "test-member".into(),
            metrics: Arc::new(MetricsRegistry::new()),
            delegated_identity: None,
            byo_jwt: None,
            keys: None,
            control: None,
            catalog: None,
            scope: ("terrabase-local".into(), "local".into()),
            backups: Some(backups),
            backup_root,
            timetravel: Some(timetravel),
            guards: Arc::new(crate::guards::AbuseGuards::default()),
            origin_proof_keys: Arc::new(crate::reactive_api::OriginProofKeyRing::default()),
            webhook_keys: None,
            exports: None,
            imports: None,
            capability_keys: Arc::new(crate::reactive_api::CapabilityKeyRing::conformance()),
        })
    }

    pub(crate) fn state_with_keys_and_control() -> Arc<ApiState> {
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
            budget: None,
            native_backups: None,
            native_recovery: None,
            native_restore_target: None,
            development: Arc::default(),
            provisioning: None,
            admitted_databases: None,
            replication_progress: Some(rf2::progress()),
            standalone_durability: false,
            region: RegionCode::GRU,
            member_id: "test-member".into(),
            metrics: Arc::new(MetricsRegistry::new()),
            delegated_identity: None,
            byo_jwt: None,
            keys: Some(Arc::new(KeyStore::with_keys(&[(
                "tskey_test_abcdefghijklmnop",
                "bootstrap",
                Scope::every_database(Verbs::ALL),
            )]))),
            control: Some(Arc::new(control)),
            catalog: Some(Arc::new(catalog)),
            scope: ("terrabase-local".into(), "local".into()),
            backups: Some(backups),
            backup_root: dir.join("backup"),
            timetravel: Some(timetravel),
            guards: Arc::new(crate::guards::AbuseGuards::default()),
            origin_proof_keys: Arc::new(crate::reactive_api::OriginProofKeyRing::default()),
            webhook_keys: None,
            exports: None,
            imports: None,
            capability_keys: Arc::new(crate::reactive_api::CapabilityKeyRing::conformance()),
        })
    }

    #[test]
    fn session_header_is_opaque_canonical_and_multi_origin() {
        let mut token = SessionToken::for_epoch(7).expect("epoch");
        token
            .advance(
                OriginLaneId::try_new(RegionId::try_new("GRU").expect("origin"), 1)
                    .expect("origin lane"),
                11,
            )
            .expect("frontier");
        token
            .advance(
                OriginLaneId::try_new(RegionId::try_new("FRA").expect("origin"), 1)
                    .expect("origin lane"),
                19,
            )
            .expect("frontier");
        let encoded = URL_SAFE_NO_PAD.encode(token.encode());
        let mut headers = axum::http::HeaderMap::new();
        headers.insert(SESSION_HEADER.clone(), HeaderValue::from_str(&encoded).expect("header"));
        assert_eq!(decode_session_header(&headers).expect("decode"), Some(token));

        // Decimal legacy values, padding, malformed alphabet, and tampering
        // are all rejected before a request reaches an engine.
        for value in [
            String::new(),
            "1".to_owned(),
            format!("{encoded}="),
            "!!!!".to_owned(),
            "A".repeat(super::MAX_SESSION_HEADER_BYTES + 1),
        ] {
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
            body.contains("\"code\":\"malformed_credential\""),
            "the envelope codes the denial: {body}"
        );
        assert!(body.contains("\"fix\":"), "every error carries a fix (D-56): {body}");
    }

    #[tokio::test]
    async fn regions_reports_local_and_replication_health() {
        let state = state();
        state.metrics.transition_service(ServiceState::Serving).expect("serving");
        let response = super::regions(axum::extract::State(Arc::clone(&state))).await;
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        assert_member_regions_body(&body, true);

        state.metrics.transition_service(ServiceState::Draining).expect("draining");
        let response = super::regions(axum::extract::State(state)).await;
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        assert_member_regions_body(&body, false);
    }

    #[tokio::test]
    async fn regions_reports_fenced_replication_as_degraded() {
        let state = state_with_keys_and_control();
        state.metrics.transition_service(ServiceState::Serving).expect("serving");
        state.replication_progress.as_ref().expect("rf2 progress").fence();
        let response = super::regions(axum::extract::State(state)).await;
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        assert_member_regions_body(&body, false);
    }

    #[tokio::test]
    async fn regions_reports_live_admission_degradation_without_requiring_reads_to_stop() {
        let ready = Arc::new(std::sync::atomic::AtomicBool::new(true));
        let gate = Arc::clone(&ready);
        let registry = Arc::new(crate::admitted_databases::AdmittedDatabaseRegistry::new(
            Arc::new(move || gate.load(std::sync::atomic::Ordering::Acquire)),
        ));
        let mut state = state();
        state.metrics.transition_service(ServiceState::Serving).expect("serving");
        Arc::get_mut(&mut state).expect("state is uniquely owned").admitted_databases =
            Some(registry.clone());

        let response = super::regions(axum::extract::State(Arc::clone(&state))).await;
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        assert_member_regions_body(&body, true);

        // The same gate that rejects new publication may degrade discovery;
        // degraded serving remains available through resolve_degraded_serving.
        ready.store(false, std::sync::atomic::Ordering::Release);
        let response = super::regions(axum::extract::State(state)).await;
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        assert_member_regions_body(&body, false);
        assert!(!registry.admission_ready());
    }

    fn assert_member_regions_body(body: &[u8], healthy: bool) {
        let value: serde_json::Value = serde_json::from_slice(body).expect("member regions JSON");
        assert_eq!(value["region"], "GRU");
        assert_eq!(value["healthy"], healthy);
        assert_eq!(value["version"], format!("terrabase-{}", env!("CARGO_PKG_VERSION")));
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
            ("/api/v1/databases/{name}/collections/{collection}/schema", "get"),
            ("/api/v1/databases/{name}/collections/{collection}/schema", "put"),
            ("/api/v1/reactive/views", "post"),
            ("/api/v1/reactive/views/{view}/{generation}/snapshot/{chunk}", "get"),
            ("/api/v1/reactive/views/{view}/{generation}/log", "get"),
            ("/api/v1/databases/{database}/auth/records/{model}/{id}", "get"),
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
        let body = axum::body::to_bytes(response.into_body(), 1_048_576).await.expect("body");
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
        assert_eq!(
            document["paths"]["/api/v1/regions"]["get"]["responses"]["200"]["content"]["application/json"]
                ["schema"]["$ref"],
            "#/components/schemas/HealthResponse",
            "member regions remains the health-shaped producer"
        );
        assert!(
            schemas["HealthResponse"]["required"]
                .as_array()
                .is_some_and(|required| required.iter().any(|field| field == "version"))
        );
        assert!(
            document["paths"]
                .get("/api/v1/databases/{database}/auth/records/{model}/{id}")
                .and_then(|path| path.get("get"))
                .is_some()
        );
        for path in [
            "/api/v1/databases/{database}/auth/insertUnique",
            "/api/v1/databases/{database}/auth/compareAndSwap",
            "/api/v1/databases/{database}/auth/consumeOne",
            "/api/v1/databases/{database}/auth/index",
            "/api/v1/databases/{database}/auth/findPage",
            "/api/v1/databases/{database}/auth/transactions/begin",
            "/api/v1/databases/{database}/auth/transactions/commit",
            "/api/v1/databases/{database}/auth/transactions/rollback",
            "/api/v1/databases/{database}/auth/updateOne",
            "/api/v1/databases/{database}/auth/updateMany",
            "/api/v1/databases/{database}/auth/deleteOne",
            "/api/v1/databases/{database}/auth/deleteMany",
            "/api/v1/databases/{database}/auth/incrementOne",
            "/api/v1/databases/{database}/auth/consumeMatching",
            "/api/v1/databases/{database}/auth/count",
        ] {
            assert!(document["paths"].get(path).is_some(), "auth route is published: {path}");
        }
        for schema in [
            "AuthResult",
            "UniqueInsert",
            "ConditionalUpdate",
            "Consume",
            "UniqueIndex",
            "PageQuery",
            "TransactionIdentity",
            "PredicateInput",
            "PredicateUpdate",
            "PredicateOnly",
            "Increment",
            "PredicateOperatorSchema",
            "PredicateConnectorSchema",
            "PredicateModeSchema",
        ] {
            assert!(schemas.contains_key(schema), "auth schema is published: {schema}");
        }
        let insert_parameters = document["paths"]["/api/v1/databases/{database}/auth/insertUnique"]
            ["post"]["parameters"]
            .as_array()
            .expect("insert parameters");
        assert!(insert_parameters.iter().any(|parameter| {
            parameter["name"] == "X-TerraScale-Auth-Transaction" && parameter["required"] == false
        }));
        assert!(
            schemas["TransactionIdentity"]["properties"]["transactionId"]["description"]
                .as_str()
                .is_some_and(|description| description.contains("100 UTF-8 bytes"))
        );
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
            "put_schema",
        ];
        let mut operation_count = 0;
        let mut operation_ids = std::collections::BTreeSet::new();
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
                assert!(operation_ids.insert(id), "duplicate operation id: {id}");
                assert_eq!(
                    operation.get("security"),
                    Some(&if id == "view_websocket" {
                        serde_json::json!([{ "viewWebSocketCapability": [] }])
                    } else {
                        serde_json::json!([{ "bearerAuth": [] }])
                    }),
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
        assert_eq!(operation_count, 65, "registered route count changed");
    }

    #[test]
    fn openapi_preserves_referenced_typed_component_schemas() {
        let document: serde_json::Value = serde_json::from_str(&openapi_json()).expect("JSON");
        let mut pending = vec![&document];
        while let Some(value) = pending.pop() {
            match value {
                serde_json::Value::Object(object) => {
                    if let Some(reference) = object.get("$ref").and_then(serde_json::Value::as_str)
                    {
                        let pointer = reference.strip_prefix('#').expect("local schema reference");
                        assert!(document.pointer(pointer).is_some(), "unresolved {reference}");
                    }
                    pending.extend(object.values());
                }
                serde_json::Value::Array(array) => pending.extend(array),
                _ => {}
            }
        }
        assert_eq!(
            document["components"]["schemas"]["SchemaIndexDescriptor"]["properties"]["fields"]["type"],
            "array"
        );
        assert_eq!(
            document["components"]["schemas"]["IndexDescriptor"]["properties"]["field"]["type"],
            "string"
        );
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
    /// Skips when the external backup executable is not packaged on this machine.
    #[tokio::test]
    async fn backup_restore_and_branch_through_the_router() {
        use crate::backup::BackupStreams;
        if BackupStreams::discover().is_none() {
            eprintln!("skipping: litestream is not on PATH");
            return;
        }
        let state = state_with_keys_and_control();
        let backup_root = state.backup_root.clone();
        let fleet = Arc::clone(state.backups.as_ref().expect("development backup fleet"));
        let router = router(state);
        let auth = "Bearer tskey_test_abcdefghijklmnop";

        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases")
                    .header("authorization", auth)
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
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
    /// passed), and an unknown plan fails closed to Free. No backup process
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

        state.backups.as_ref().expect("development backup fleet").set_plan("planned", "pro");
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

        state.backups.as_ref().expect("development backup fleet").set_plan("planned", "team");
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
    /// the live read serves the new one. Skips when the external backup
    /// executable is not packaged on this machine.
    #[tokio::test]
    async fn document_time_travel_at_returns_the_old_value() {
        use crate::backup::BackupStreams;
        if BackupStreams::discover().is_none() {
            eprintln!("skipping: litestream is not on PATH");
            return;
        }
        let state = state_with_keys_and_control();
        let backup_root = state.backup_root.clone();
        let fleet = Arc::clone(state.backups.as_ref().expect("development backup fleet"));
        let router = router(state);
        let auth = "Bearer tskey_test_abcdefghijklmnop";
        let unix_now = super::unix_now_seconds;

        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases")
                    .header("authorization", auth)
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
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
            assert_eq!(status, StatusCode::OK, "the past serves: {body}");
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
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
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
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
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
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
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
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
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
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
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
        // The bare revision — what the write response body reports — satisfies
        // the same precondition as the quoted entity-tag form.
        let response = router
            .clone()
            .oneshot(
                Request::patch(path)
                    .header(auth, key)
                    .header("if-match", patched_etag.trim_matches('"'))
                    .body(Body::from(r#"{"n":9}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        let live_etag = response
            .headers()
            .get("etag")
            .and_then(|value| value.to_str().ok())
            .expect("the bare-form patch names the revision")
            .to_owned();
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
                    .header("if-match", live_etag.as_str())
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
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
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
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
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
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
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
                    .header(SESSION_HEADER.clone(), URL_SAFE_NO_PAD.encode(session.encode()))
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
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
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
                .header("x-terrascale-capability", capability)
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
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
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
                        .body(Body::from(r#"{"databaseId":"prod","statement":"SELECT total FROM orders WHERE total > 0 LIMIT 10"}"#)).unwrap(),
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
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
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
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
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
        // This collection has indexes but no applied schema yet.
        for (method, path) in [("GET", "/api/v1/databases/prod/collections/orders/schema")] {
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

    #[test]
    fn openapi_describes_backup_points_and_strict_restore_selectors() {
        let document: serde_json::Value = serde_json::from_str(&openapi_json()).expect("JSON");
        assert_eq!(
            document["paths"]["/api/v1/databases/{name}/backups/points"]["get"]["responses"]["200"]
                ["content"]["application/json"]["schema"]["$ref"],
            "#/components/schemas/BackupPointPage"
        );
        assert_eq!(
            document["components"]["schemas"]["BackupPoint"]["properties"]["captured_at_unix_millis"]
                ["format"],
            "uint64"
        );
        assert_eq!(
            document["components"]["schemas"]["BackupPoint"]["properties"]["logical_documents"]["type"],
            "string"
        );
        assert_eq!(
            document["components"]["schemas"]["RestoreRequest"]["additionalProperties"],
            false
        );
        assert_eq!(
            document["components"]["schemas"]["RestoreRequest"]["properties"]["point_id"]["pattern"],
            "^[0-9a-f]{64}$"
        );
    }

    #[tokio::test]
    async fn deletion_status_location_is_mounted_and_reaches_authority_guard() {
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
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
                    .body(Body::from(r#"{"name":"prod","placement":["GRU"],"residency":null}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::CREATED);

        // The route emitted by DELETE is mounted even when this test fixture
        // has no provisioning authority; a mounted handler returns the typed
        // dependency error instead of the fallback 404.
        let response = router
            .oneshot(
                Request::get("/api/v1/databases/prod/deletions/delete-1")
                    .header(auth, key)
                    .body(Body::empty())
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::SERVICE_UNAVAILABLE);
    }

    #[tokio::test]
    async fn exports_fail_closed_naming_e8_and_locked_sources_refuse_derived_databases() {
        use axum::body::Body;
        use axum::http::Request;
        let auth = "authorization";
        let key = "Bearer tskey_test_abcdefghijklmnop";
        let router = router(state_with_keys_and_control());
        // An admitted database still refuses exports without configured dependencies.
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases")
                    .header(auth, key)
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
                    .body(Body::from(r#"{"name":"prod","placement":["GRU"],"residency":null}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::CREATED);
        // This fixture deliberately has no export runtime.
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases/prod/exports")
                    .header(auth, key)
                    .body(Body::from(r#"{"idempotency_key":"export-test"}"#))
                    .expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::SERVICE_UNAVAILABLE);
        // Unknown databases 404 before the stub answers.
        let response = router
            .clone()
            .oneshot(
                Request::post("/api/v1/databases/unknown/exports")
                    .header(auth, key)
                    .body(Body::from(r#"{"idempotency_key":"export-test"}"#))
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
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
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
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
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
        assert!(body.contains("\"policy\":\"origin\""), "policy is exposed: {body}");
        assert!(body.contains("\"explanation\":\"origin write"), "explanation is exposed: {body}");
        let parsed: serde_json::Value = serde_json::from_str(&body).expect("history JSON");
        let first = &parsed["history"][0];
        for field in ["commitUlid", "hlc", "origin", "policy", "explanation"] {
            assert!(!first[field].is_null(), "history exposes {field}: {parsed}");
        }
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
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
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
                    .header("x-terrascale-tenant-id", "terrabase-local")
                    .header("x-terrascale-project-id", "local")
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
                        .header(SESSION_HEADER.clone(), URL_SAFE_NO_PAD.encode(future.encode()))
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

    #[test]
    fn restore_at_rejects_malformed_values_and_preserves_latest() {
        for value in [
            serde_json::json!(-1),
            serde_json::json!("1000"),
            serde_json::json!(1.5),
            serde_json::Value::Null,
        ] {
            assert_eq!(super::restore_at_millis(Some(&value)), Err(()));
        }
        assert_eq!(super::restore_at_millis(None), Ok(None));
        assert_eq!(super::restore_at_millis(Some(&serde_json::json!(1_000))), Ok(Some(1_000)));
    }

    #[test]
    fn restore_request_selectors_are_strict_and_forwarded() {
        let point_id = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        let (_, _, selection) = super::parse_restore_request(&serde_json::json!({
            "name": "restored",
            "operation_id": "restore-1",
            "point_id": point_id,
        }))
        .expect("point selector");
        assert_eq!(selection, serde_json::json!({"kind":"point_id", "point_id":point_id}));

        let (_, _, selection) = super::parse_restore_request(
            &serde_json::json!({"name":"restored", "operation_id":"restore-2"}),
        )
        .expect("latest selector");
        assert_eq!(selection, serde_json::json!({"kind":"latest"}));

        for request in [
            serde_json::json!({"name":"restored", "operation_id":"restore-3", "at":100, "point_id":point_id}),
            serde_json::json!({"name":"restored", "operation_id":"restore-4", "point_id":point_id.to_ascii_uppercase()}),
            serde_json::json!({"name":"restored", "operation_id":"restore-5", "unknown":true}),
            serde_json::json!({"name":"restored", "operation_id":"restore-6", "at":null}),
        ] {
            assert!(super::parse_restore_request(&request).is_err(), "request: {request}");
        }
    }
}

#[cfg(test)]
#[path = "api_write_durability_tests.rs"]
mod write_durability_tests;

#[cfg(test)]
pub(crate) async fn confirm_regional_for_replication_test(
    engine: &terrabase::Engine,
    tenant: &terrabase::types::TenantScope,
    receipt: &terrabase::CommitReceipt,
    progress: Arc<crate::replication_runtime::ReplicationProgress>,
    scope: terrabase_cluster::production::DatabaseRouteScope,
    timeout: std::time::Duration,
) -> Result<(), String> {
    WriteDurability {
        class: Durability::Regional,
        uniform_context: None,
        progress,
        scope,
        timeout,
        standalone: false,
    }
    .confirm(engine, tenant, receipt)
    .await
    .map_err(|error| error.code.to_owned())
}
