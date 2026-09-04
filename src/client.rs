//! Client root and the shared request pipeline.
//!
//! [`TerraScaleClient`] mirrors the `@terrascale/sdk` root: document CRUD,
//! typed SQL, reactive subscribe/execute, tenants, and management, with
//! region pinning, session tokens (read-your-writes + monotonic reads), and
//! `421` placement repair shared by every sub-client.

use std::sync::{Arc, RwLock};

use serde::de::DeserializeOwned;

use crate::config::ClientConfig;
use crate::documents::{DatabaseScope, DocumentsClient};
use crate::error::{Error, ErrorKind, Result};
use crate::management::ManagementClient;
use crate::reactive::ReactiveClient;
use crate::regions::{normalize_base_url, pin_base_url, RegionAffinity, RegionCode};
use crate::sql::SqlClient;
use crate::tenants::TenantsClient;
use crate::transport::{HttpTransport, Request, ReqwestTransport, Response};

/// REST root for the public API (GOAL D-34).
pub const API_ROOT: &str = "/api/v1";

/// Placement header returned with a 421 (GOAL D-58).
pub const PLACEMENT_HEADER: &str = "x-terrascale-placement";

/// Shareable pipeline state behind every sub-client.
#[derive(Debug)]
pub(crate) struct Shared {
    transport: Arc<dyn HttpTransport>,
    config: ClientConfig,
    placement: RwLock<Option<RegionCode>>,
    session_token: RwLock<Option<String>>,
}

/// Shareable handle to the pipeline.
pub(crate) type SharedClient = Arc<Shared>;

impl Shared {
    fn new(config: ClientConfig, transport: Arc<dyn HttpTransport>) -> Self {
        Self {
            transport,
            config,
            placement: RwLock::new(None),
            session_token: RwLock::new(None),
        }
    }

    /// Global (unpinned) base URL for first contact and global routes.
    pub fn global_base_url(&self) -> String {
        format!("{}{API_ROOT}", self.config.api_base_url())
    }

    /// Base URL for pinned REST traffic, honoring a per-request hint, the
    /// repaired 421 placement, the preferred region, and affinity.
    ///
    /// An explicit hint or a learned 421 placement pins directly. Otherwise a
    /// preferred region pins under strict affinity and uses first contact
    /// under failover (the proxy routes, then a 421 teaches the pin).
    pub fn pinned_base_url(&self, region_hint: Option<RegionCode>) -> Result<String> {
        let repaired = self.placement.read().ok().and_then(|guard| *guard);
        if let Some(region) = region_hint.or(repaired) {
            let global = normalize_base_url(self.config.api_base_url())?;
            let pinned = pin_base_url(&global, Some(region), Some(region), RegionAffinity::Strict)?;
            return Ok(format!("{pinned}{API_ROOT}"));
        }
        let base = pin_base_url(
            self.config.api_base_url(),
            self.config.preferred_region(),
            None,
            self.config.region_affinity(),
        )?;
        Ok(format!("{base}{API_ROOT}"))
    }

    /// Project-scoped root, e.g. `{base}/api/v1/projects/{id}`.
    pub fn project_root(&self, project_id: Option<&str>) -> Result<String> {
        self.project_root_in(project_id, None)
    }

    /// Project-scoped root pinned to one region for this request.
    pub fn project_root_in(
        &self,
        project_id: Option<&str>,
        region_hint: Option<RegionCode>,
    ) -> Result<String> {
        let selected = project_id
            .map(str::to_owned)
            .or_else(|| self.config.project_id().map(str::to_owned));
        selected
            .map(|id| {
                self.pinned_base_url(region_hint)
                    .map(|base| format!("{base}/projects/{id}"))
            })
            .transpose()?
            .ok_or_else(|| {
                Error::new(
                    ErrorKind::Validation,
                    "project_required",
                    "A projectId must be supplied either per request or in the client config.",
                )
            })
    }

    /// Database-scoped root for document, SQL, and reactive routes.
    pub fn database_root(&self, scope: &DatabaseScope) -> Result<String> {
        self.database_root_in(scope, None)
    }

    /// Database-scoped root pinned to one region for this request.
    pub fn database_root_in(
        &self,
        scope: &DatabaseScope,
        region_hint: Option<RegionCode>,
    ) -> Result<String> {
        Ok(format!(
            "{}/databases/{}",
            self.project_root_in(scope.project_id.as_deref(), region_hint)?,
            scope.database_id
        ))
    }

    /// Collection names follow the schema builder contract.
    pub fn require_collection(&self, collection: &str) -> Result<String> {
        if collection.trim().is_empty() {
            return Err(Error::new(
                ErrorKind::Validation,
                "invalid_collection",
                "Collection names cannot be empty.",
            ));
        }
        Ok(collection.to_owned())
    }

    /// Currently pinned region, if the SDK learned or configured one.
    #[must_use]
    pub fn pinned_region(&self) -> Option<RegionCode> {
        self.placement
            .read()
            .ok()
            .and_then(|guard| *guard)
            .or(self.config.preferred_region())
    }

    /// Current session token (causal frontier for read-your-writes).
    #[must_use]
    pub fn session_token(&self) -> Option<String> {
        self.session_token
            .read()
            .ok()
            .and_then(|guard| guard.clone())
    }

    /// Install a session token; it is carried on subsequent requests and
    /// refreshed from server responses.
    pub fn set_session_token(&self, token: Option<String>) {
        if let Ok(mut guard) = self.session_token.write() {
            *guard = token;
        }
    }

    fn decorate(&self, request: &mut Request) {
        if let Some(api_key) = self.config.api_key() {
            request
                .headers
                .push(("authorization".to_owned(), format!("Bearer {api_key}")));
        }
        if !request
            .headers
            .iter()
            .any(|(k, _)| k.eq_ignore_ascii_case("accept"))
        {
            request
                .headers
                .push(("accept".to_owned(), "application/json".to_owned()));
        }
        if let Some(token) = self.session_token() {
            request
                .headers
                .push(("x-terrascale-session".to_owned(), token));
        }
        if self.config.preferred_region().is_some() || self.pinned_region().is_some() {
            if let Some(region) = self.pinned_region() {
                request.headers.push((
                    "x-terrascale-region-hint".to_owned(),
                    region.as_str().to_owned(),
                ));
            }
        }
        let idempotency = format!("ts_{}", simple_id());
        if matches!(
            request.method,
            crate::transport::Method::Post | crate::transport::Method::Put
        ) && !request
            .headers
            .iter()
            .any(|(k, _)| k.eq_ignore_ascii_case("idempotency-key"))
        {
            request
                .headers
                .push(("idempotency-key".to_owned(), idempotency));
        }
    }

    fn observe(&self, response: &Response) {
        if let Some(token) = response.header("x-terrascale-session") {
            self.set_session_token(Some(token.to_owned()));
        }
        if let Some(placement) = response.header(PLACEMENT_HEADER) {
            if let Ok(region) = placement.parse() {
                if let Ok(mut guard) = self.placement.write() {
                    *guard = Some(region);
                }
            }
        }
    }

    fn check(&self, route_name: &str, response: &Response) -> Result<()> {
        if (200..300).contains(&response.status) {
            return Ok(());
        }
        if response.status == 421 {
            let placement = response.header(PLACEMENT_HEADER).map(str::to_owned);
            if let Some(ref placement) = placement {
                if let Ok(region) = placement.parse() {
                    if let Ok(mut guard) = self.placement.write() {
                        *guard = Some(region);
                    }
                }
            }
            let mut error = Error::for_status(
                421,
                "misdirected_region".to_owned(),
                "The region does not host this database; the SDK repaired its pin from X-TerraScale-Placement. Retry the request.".to_owned(),
            );
            error.placement = placement;
            return Err(error);
        }
        Err(decode_error(route_name, response))
    }

    /// Send a request and decode a JSON body.
    pub async fn send_typed<T: DeserializeOwned>(&self, mut request: Request) -> Result<T> {
        self.decorate(&mut request);
        let route = request.route_name.clone();
        let response = self.transport.send(request).await?;
        self.observe(&response);
        self.check(&route, &response)?;
        response.json_body().map_err(|mut e: Error| {
            e.code = format!("{}.invalid_response", route_to_code(&route));
            e
        })
    }

    /// Send a request and expect an empty success body.
    pub async fn send_empty(&self, mut request: Request) -> Result<()> {
        self.decorate(&mut request);
        let response = self.transport.send(request).await?;
        self.observe(&response);
        self.check("request", &response)
    }
}

fn route_to_code(route: &str) -> String {
    route.replace('.', "_")
}

#[derive(Debug, serde::Deserialize)]
struct ErrorBody {
    #[serde(default)]
    code: Option<String>,
    #[serde(default)]
    message: Option<String>,
}

fn decode_error(route_name: &str, response: &Response) -> Error {
    let body: Option<ErrorBody> = serde_json::from_slice(&response.body).unwrap_or(None);
    let code = body
        .as_ref()
        .and_then(|b| b.code.clone())
        .unwrap_or_else(|| route_to_code(route_name));
    let message = body
        .as_ref()
        .and_then(|b| b.message.clone())
        .unwrap_or_else(|| {
            format!(
                "Request {route_name} failed with status {}.",
                response.status
            )
        });
    let mut error = Error::for_status(response.status, code, message);
    error.request_id = response.header("x-request-id").map(str::to_owned);
    error
}

fn simple_id() -> u64 {
    use std::time::{SystemTime, UNIX_EPOCH};
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.subsec_nanos() as u64 ^ (d.as_secs() << 32))
        .unwrap_or(0)
}

/// Root client. Build with [`ClientConfig::builder`] then branch into
/// `documents()`, `sql()`, `reactive()`, `tenants()`, or `management()`.
///
/// ```rust,no_run
/// use terrascale::{ClientConfig, RegionCode, TerraScaleClient};
///
/// # async fn example() -> terrascale::Result<()> {
/// let client = TerraScaleClient::new(
///     ClientConfig::builder()
///         .api_key("ts_test_key")
///         .preferred_region(RegionCode::Gru)
///         .build()?,
/// );
/// let _docs = client.documents();
/// # Ok(())
/// # }
/// ```
#[derive(Debug, Clone)]
pub struct TerraScaleClient {
    shared: SharedClient,
}

impl TerraScaleClient {
    /// Create a client with the production HTTPS transport.
    #[must_use]
    pub fn new(config: ClientConfig) -> Self {
        Self::with_transport(config, Arc::new(ReqwestTransport::new()))
    }

    /// Create a client with an injected transport (mocks, custom stacks).
    pub fn with_transport(config: ClientConfig, transport: Arc<dyn HttpTransport>) -> Self {
        Self {
            shared: Arc::new(Shared::new(config, transport)),
        }
    }

    /// Effective configuration.
    #[must_use]
    pub fn config(&self) -> &ClientConfig {
        &self.shared.config
    }

    /// Currently pinned region, if any.
    #[must_use]
    pub fn pinned_region(&self) -> Option<RegionCode> {
        self.shared.pinned_region()
    }

    /// Override the region pin directly (e.g. from out-of-band placement).
    pub fn pin_region(&self, region: RegionCode) {
        if let Ok(mut guard) = self.shared.placement.write() {
            *guard = Some(region);
        }
    }

    /// Current session token (causal frontier).
    #[must_use]
    pub fn session_token(&self) -> Option<String> {
        self.shared.session_token()
    }

    /// Install a session token.
    pub fn set_session_token(&self, token: Option<String>) {
        self.shared.set_session_token(token);
    }
    /// Document CRUD.
    #[must_use]
    pub fn documents(&self) -> DocumentsClient {
        DocumentsClient::new(self.shared.clone())
    }

    /// Typed + raw read-only SQL.
    #[must_use]
    pub fn sql(&self) -> SqlClient {
        SqlClient::new(self.shared.clone())
    }

    /// Reactive subscribe / execute, checkpoints, typed resets.
    #[must_use]
    pub fn reactive(&self) -> ReactiveClient {
        ReactiveClient::new(self.shared.clone())
    }

    /// Database-per-tenant provisioning and tenant keys.
    #[must_use]
    pub fn tenants(&self) -> TenantsClient {
        TenantsClient::new(self.shared.clone())
    }

    /// Databases, branches, backups, usage, regions.
    #[must_use]
    pub fn management(&self) -> ManagementClient {
        ManagementClient::new(self.shared.clone())
    }
}
