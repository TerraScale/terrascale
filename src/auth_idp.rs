//! ts-auth (hosted IdP) management and OIDC client.
//!
//! ts-auth is a separate service from TerraBase: it lives on its own origin
//! (`auth_base_url`), authenticates with its own instance key
//! (`auth_api_key`), and its routes are not part of the TerraBase producer
//! contract. Obtain a client with [`crate::TerraScaleClient::auth_idp`].
//!
//! Wire shapes follow the ts-auth contract: management routes under
//! `/v1/{slug}` with `Authorization: Bearer <sk_ key>`, public OIDC routes
//! under `/{slug}` with client credentials in the form body, per-family list
//! envelopes and casings, tri-state user patches, and one-time secrets
//! returned only by the creating or rotating call.

use std::future::Future;
use std::sync::Arc;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};
use sha2::{Digest, Sha256};

use crate::client::{SharedClient, SKIP_AUTH_MARKER, SKIP_SESSION_MARKER};
use crate::error::{Error, ErrorKind, Result};
use crate::transport::{Method, Request};

const MAX_USER_PAGE_SIZE: u32 = 100;
const MAX_AUDIT_PAGE_SIZE: u32 = 100;
const MAX_JWT_TEMPLATE_NAME_BYTES: usize = 48;
/// Per-request deadline for every ts-auth send; the shared pipeline has no
/// per-request timeout of its own.
const REQUEST_DEADLINE: Duration = Duration::from_secs(30);

// ---------------------------------------------------------------------------
// Client core
// ---------------------------------------------------------------------------

/// ts-auth management client for one instance. Obtain with
/// [`crate::TerraScaleClient::auth_idp`]; it shares the caller's transport,
/// retry policy, and configuration pipeline but sends ts-auth credentials
/// only.
#[derive(Clone)]
pub struct AuthIdpClient {
    shared: SharedClient,
    api_key: Arc<str>,
    /// `Bearer {api_key}`, precomputed once per client.
    bearer: Arc<str>,
    /// `{auth_base_url}/v1/{encoded-slug}`.
    management_prefix: Arc<str>,
    /// `{auth_base_url}/{encoded-slug}`.
    oidc_prefix: Arc<str>,
    slug: String,
}

impl std::fmt::Debug for AuthIdpClient {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("AuthIdpClient")
            .field("management_prefix", &self.management_prefix)
            .field("slug", &self.slug)
            .field("api_key", &"[REDACTED]")
            .finish()
    }
}

impl AuthIdpClient {
    pub(crate) fn new(shared: SharedClient, slug: &str) -> Result<Self> {
        validate_slug(slug)?;
        let Some(base_url) = shared.config().auth_base_url().map(str::to_owned) else {
            return Err(validation(
                "auth_base_url_not_configured",
                "auth_base_url is not configured.",
            ));
        };
        let api_key = shared
            .config()
            .auth_api_key()
            .unwrap_or_default()
            .to_owned();
        let encoded_slug = percent_encode(slug);
        let bearer = format!("Bearer {api_key}");
        Ok(Self {
            shared,
            api_key: api_key.into(),
            bearer: bearer.into(),
            management_prefix: format!("{base_url}/v1/{encoded_slug}").into(),
            oidc_prefix: format!("{base_url}/{encoded_slug}").into(),
            slug: slug.to_owned(),
        })
    }

    /// User lifecycle, MFA factors, passkeys, sessions, and impersonation.
    #[must_use]
    pub fn users(&self) -> AuthIdpUsers {
        AuthIdpUsers {
            client: self.clone(),
        }
    }

    /// Session revocation.
    #[must_use]
    pub fn sessions(&self) -> AuthIdpSessions {
        AuthIdpSessions {
            client: self.clone(),
        }
    }

    /// Instance API key minting and revocation.
    #[must_use]
    pub fn api_keys(&self) -> AuthIdpApiKeys {
        AuthIdpApiKeys {
            client: self.clone(),
        }
    }

    /// Organizations, memberships, invitations, and verified domains.
    #[must_use]
    pub fn organizations(&self) -> AuthIdpOrganizations {
        AuthIdpOrganizations {
            client: self.clone(),
        }
    }

    /// SSO connections (OIDC and SAML).
    #[must_use]
    pub fn connections(&self) -> AuthIdpConnections {
        AuthIdpConnections {
            client: self.clone(),
        }
    }

    /// SCIM directories and directory token rotation.
    #[must_use]
    pub fn directories(&self) -> AuthIdpDirectories {
        AuthIdpDirectories {
            client: self.clone(),
        }
    }

    /// JWT templates and per-template token minting.
    #[must_use]
    pub fn jwt_templates(&self) -> AuthIdpJwtTemplates {
        AuthIdpJwtTemplates {
            client: self.clone(),
        }
    }

    /// Webhook endpoints, delivery tests, and dead-letter replay.
    #[must_use]
    pub fn webhooks(&self) -> AuthIdpWebhooks {
        AuthIdpWebhooks {
            client: self.clone(),
        }
    }

    /// Hash-chained audit event log.
    #[must_use]
    pub fn audit_events(&self) -> AuthIdpAuditEvents {
        AuthIdpAuditEvents {
            client: self.clone(),
        }
    }

    /// Plan, checkout, portal, and quota reconciliation.
    #[must_use]
    pub fn billing(&self) -> AuthIdpBillingClient {
        AuthIdpBillingClient {
            client: self.clone(),
        }
    }

    /// Instance branding settings.
    #[must_use]
    pub fn instance(&self) -> AuthIdpInstanceClient {
        AuthIdpInstanceClient {
            client: self.clone(),
        }
    }

    /// OAuth client management.
    #[must_use]
    pub fn oauth_clients(&self) -> AuthIdpOauthClients {
        AuthIdpOauthClients {
            client: self.clone(),
        }
    }

    /// Public OIDC protocol routes (discovery, JWKS, token, revoke,
    /// introspect, userinfo). No instance key is sent.
    #[must_use]
    pub fn oidc(&self) -> AuthIdpOidc {
        AuthIdpOidc {
            client: self.clone(),
        }
    }

    /// Management route root: `{auth_base_url}/v1/{slug}`.
    fn management_url(&self, path: &str) -> String {
        format!("{}{path}", self.management_prefix)
    }

    /// Public OIDC route root: `{auth_base_url}/{slug}`.
    fn oidc_url(&self, path: &str) -> String {
        format!("{}{path}", self.oidc_prefix)
    }

    /// Attach the ts-auth instance bearer key and suppress the pipeline's
    /// TerraBase credential and causal session header: `decorate` pushes its
    /// own Authorization and session headers unconditionally, so the skip
    /// markers are what prevent them from leaking to ts-auth.
    fn authorize(&self, mut request: Request) -> Result<Request> {
        if self.api_key.is_empty() {
            return Err(validation(
                "auth_api_key_not_configured",
                "auth_api_key is not configured.",
            ));
        }
        request
            .headers
            .push(("authorization".to_owned(), self.bearer.to_string()));
        request
            .headers
            .push((SKIP_AUTH_MARKER.to_owned(), String::new()));
        request
            .headers
            .push((SKIP_SESSION_MARKER.to_owned(), String::new()));
        Ok(request)
    }

    /// Mark a public request so the pipeline injects no bearer credential and
    /// no TerraBase session header.
    fn public(mut request: Request) -> Request {
        request
            .headers
            .push((SKIP_AUTH_MARKER.to_owned(), String::new()));
        request
            .headers
            .push((SKIP_SESSION_MARKER.to_owned(), String::new()));
        request
    }

    /// The single send choke point for every ts-auth family: bounds the whole
    /// pipeline (retries included) to [`REQUEST_DEADLINE`].
    async fn send_typed<T: DeserializeOwned>(&self, request: Request) -> Result<T> {
        bounded(self.shared.send_typed(request)).await
    }

    /// Deadline-bounded send for empty-body routes. DELETE routes answer 204
    /// exactly; a wider 2xx hides a broken route.
    async fn send_empty(&self, request: Request) -> Result<()> {
        let method = request.method;
        let response = bounded(self.shared.send_response(request)).await?;
        if method == Method::Delete && response.status != 204 {
            return Err(Error::new(
                ErrorKind::InvalidResponse,
                "expected_http_204",
                format!("expected HTTP 204, got {}", response.status),
            ));
        }
        Ok(())
    }

    async fn management_json<T: DeserializeOwned>(
        &self,
        route: &str,
        method: Method,
        path: &str,
        body: &impl Serialize,
    ) -> Result<T> {
        let request = Request::json(route, method, self.management_url(path), body)?;
        self.send_typed(self.authorize(request)?).await
    }

    async fn management_no_body<T: DeserializeOwned>(
        &self,
        route: &str,
        method: Method,
        path: &str,
    ) -> Result<T> {
        let request = self.authorize(Request {
            route_name: route.to_owned(),
            method,
            url: self.management_url(path),
            headers: Vec::new(),
            body: None,
        })?;
        self.send_typed(request).await
    }

    async fn management_empty(&self, route: &str, method: Method, path: &str) -> Result<()> {
        let request = self.authorize(Request {
            route_name: route.to_owned(),
            method,
            url: self.management_url(path),
            headers: Vec::new(),
            body: None,
        })?;
        self.send_empty(request).await
    }

    async fn oidc_get<T: DeserializeOwned>(&self, route: &str, path: &str) -> Result<T> {
        let request = Self::public(Request {
            route_name: route.to_owned(),
            method: Method::Get,
            url: self.oidc_url(path),
            headers: Vec::new(),
            body: None,
        });
        self.send_typed(request).await
    }

    fn oidc_form(&self, route: &str, path: &str, pairs: &[(&str, &str)]) -> Request {
        Self::public(Request {
            route_name: route.to_owned(),
            method: Method::Post,
            url: self.oidc_url(path),
            headers: vec![(
                "content-type".to_owned(),
                "application/x-www-form-urlencoded".to_owned(),
            )],
            body: Some(form_urlencoded(pairs)),
        })
    }
}

/// Bound one ts-auth request to [`REQUEST_DEADLINE`].
async fn bounded<T>(future: impl Future<Output = Result<T>>) -> Result<T> {
    match tokio::time::timeout(REQUEST_DEADLINE, future).await {
        Ok(result) => result,
        Err(_elapsed) => Err(Error::new(
            ErrorKind::Transport,
            "ts_auth_deadline_exceeded",
            "ts-auth request exceeded the 30 second deadline.",
        )),
    }
}

// ---------------------------------------------------------------------------
// Users
// ---------------------------------------------------------------------------

/// A ts-auth user (snake_case wire).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpUser {
    /// User id.
    pub id: String,
    /// Email address, when the user has one.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub email: Option<String>,
    /// Whether the email address was verified.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub email_verified: Option<bool>,
    /// Username, when the user has one.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub username: Option<String>,
    /// Display name, when set.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    /// Whether the user is banned.
    pub banned: bool,
    /// Ban reason; omitted by list responses, null when unbanned.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ban_reason: Option<String>,
    /// Creation time (unix seconds).
    pub created_at: i64,
    /// Last sign-in time (unix seconds), when the user has signed in.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub last_sign_in_at: Option<i64>,
    /// Caller-visible metadata.
    #[serde(default)]
    pub public_metadata: Value,
    /// Private metadata; omitted by list responses.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub private_metadata: Option<Value>,
}

/// One cursor page of users (`{"data":[...]}` envelope).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpUserPage {
    /// Users in this page, ordered by id ascending.
    pub data: Vec<AuthIdpUser>,
}

/// Request body for user creation; every field is optional.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct CreateAuthIdpUser {
    /// Email address.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub email: Option<String>,
    /// Username.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub username: Option<String>,
    /// Password (server enforces a minimum length of 8).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub password: Option<String>,
    /// Display name.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
}

/// Tri-state user patch body: a field never touched is omitted (unchanged),
/// `unset_*` sends JSON null (cleared), and `with_*` sends the value.
///
/// Only configured fields are serialized, so an untouched patch builder
/// produces an empty body and is rejected client-side.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct AuthIdpUserPatch {
    fields: Map<String, Value>,
}

impl AuthIdpUserPatch {
    /// Start with an empty patch.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Set the display name.
    #[must_use]
    pub fn with_name(mut self, value: impl Into<String>) -> Self {
        self.fields
            .insert("name".to_owned(), Value::String(value.into()));
        self
    }

    /// Clear the display name.
    #[must_use]
    pub fn unset_name(mut self) -> Self {
        self.fields.insert("name".to_owned(), Value::Null);
        self
    }

    /// Set the username.
    #[must_use]
    pub fn with_username(mut self, value: impl Into<String>) -> Self {
        self.fields
            .insert("username".to_owned(), Value::String(value.into()));
        self
    }

    /// Clear the username.
    #[must_use]
    pub fn unset_username(mut self) -> Self {
        self.fields.insert("username".to_owned(), Value::Null);
        self
    }

    /// Set the password (minimum 8 characters).
    #[must_use]
    pub fn with_password(mut self, value: impl Into<String>) -> Self {
        self.fields
            .insert("password".to_owned(), Value::String(value.into()));
        self
    }

    /// Clear the password.
    #[must_use]
    pub fn unset_password(mut self) -> Self {
        self.fields.insert("password".to_owned(), Value::Null);
        self
    }

    /// Set the email address (string only; JSON null is rejected by the API).
    #[must_use]
    pub fn with_email(mut self, value: impl Into<String>) -> Self {
        self.fields
            .insert("email".to_owned(), Value::String(value.into()));
        self
    }

    /// Ban or unban the user.
    #[must_use]
    pub fn with_banned(mut self, value: bool) -> Self {
        self.fields.insert("banned".to_owned(), Value::Bool(value));
        self
    }

    /// Set the ban reason.
    #[must_use]
    pub fn with_ban_reason(mut self, value: impl Into<String>) -> Self {
        self.fields
            .insert("ban_reason".to_owned(), Value::String(value.into()));
        self
    }

    /// Clear the ban reason.
    #[must_use]
    pub fn unset_ban_reason(mut self) -> Self {
        self.fields.insert("ban_reason".to_owned(), Value::Null);
        self
    }

    /// Replace public metadata wholesale.
    #[must_use]
    pub fn with_public_metadata(mut self, value: Map<String, Value>) -> Self {
        self.fields
            .insert("public_metadata".to_owned(), Value::Object(value));
        self
    }

    /// Clear public metadata.
    #[must_use]
    pub fn unset_public_metadata(mut self) -> Self {
        self.fields
            .insert("public_metadata".to_owned(), Value::Null);
        self
    }

    /// Replace private metadata wholesale.
    #[must_use]
    pub fn with_private_metadata(mut self, value: Map<String, Value>) -> Self {
        self.fields
            .insert("private_metadata".to_owned(), Value::Object(value));
        self
    }

    /// Clear private metadata.
    #[must_use]
    pub fn unset_private_metadata(mut self) -> Self {
        self.fields
            .insert("private_metadata".to_owned(), Value::Null);
        self
    }

    /// Whether no field is configured.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.fields.is_empty()
    }

    fn into_body(self) -> Result<Value> {
        if self.fields.is_empty() {
            return Err(validation(
                "invalid_user_patch",
                "The user patch must set at least one field.",
            ));
        }
        if let Some(Value::String(password)) = self.fields.get("password") {
            if password.chars().count() < 8 {
                return Err(validation(
                    "invalid_password",
                    "Password must be at least 8 characters.",
                ));
            }
        }
        Ok(Value::Object(self.fields))
    }
}

/// A ts-auth session (snake_case wire).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpSession {
    /// Session id.
    pub id: String,
    /// Session status.
    pub status: String,
    /// Creation time (unix seconds).
    pub created_at: i64,
    /// Expiry time (unix seconds).
    pub expires_at: i64,
    /// Last activity time (unix seconds), when recorded.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub last_active_at: Option<i64>,
    /// Client user agent, when recorded.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub user_agent: Option<String>,
    /// Client IP address, when recorded.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ip_address: Option<String>,
}

/// One-time impersonation token (Debug redacted).
#[derive(Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpImpersonationToken {
    /// Bearer token for the impersonated session.
    pub token: String,
    /// Expiry time (unix seconds).
    pub expires_at: i64,
}

impl std::fmt::Debug for AuthIdpImpersonationToken {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("AuthIdpImpersonationToken")
            .field("expires_at", &self.expires_at)
            .field("token", &"[REDACTED]")
            .finish()
    }
}

/// A user MFA factor (camelCase wire).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AuthIdpMfaFactor {
    /// Factor id.
    pub id: String,
    /// Factor type.
    #[serde(rename = "type")]
    pub factor_type: String,
    /// Whether the factor was confirmed.
    pub confirmed: bool,
    /// Creation time (unix seconds).
    pub created_at: i64,
}

/// A user passkey (camelCase wire).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AuthIdpPasskey {
    /// Passkey id.
    pub id: String,
    /// Caller-assigned nickname.
    pub nickname: String,
    /// WebAuthn algorithm name.
    pub algorithm: String,
    /// Registration time (unix seconds).
    pub created_at: i64,
    /// Last use time (unix seconds), when used.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub last_used_at: Option<i64>,
}

/// A user's organization membership from the per-user organizations list
/// (snake_case wire; `organization` is null for deleted organizations).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpUserMembership {
    /// Membership id.
    pub id: String,
    /// Membership role.
    pub role: String,
    /// Creation time (unix seconds).
    pub created_at: i64,
    /// The full organization record, or null.
    #[serde(default)]
    pub organization: Option<AuthIdpOrganization>,
}

/// Users, MFA factors, passkeys, sessions, and impersonation.
#[derive(Debug, Clone)]
pub struct AuthIdpUsers {
    client: AuthIdpClient,
}

impl AuthIdpUsers {
    /// List one cursor page of users (`limit` 1-100, default 20; the cursor
    /// is the last item id).
    pub async fn list(
        &self,
        limit: Option<u32>,
        starting_after: Option<&str>,
    ) -> Result<AuthIdpUserPage> {
        let mut query = Vec::new();
        if let Some(limit) = limit {
            validate_page_limit(limit, MAX_USER_PAGE_SIZE, "user")?;
            query.push(format!("limit={limit}"));
        }
        if let Some(cursor) = starting_after {
            validate_non_empty(cursor, "starting_after")?;
            query.push(format!("starting_after={}", percent_encode(cursor.trim())));
        }
        let suffix = if query.is_empty() {
            String::new()
        } else {
            format!("?{}", query.join("&"))
        };
        self.client
            .management_no_body(
                "auth_idp.users.list",
                Method::Get,
                &format!("/users{suffix}"),
            )
            .await
    }

    /// List every user, walking the id cursor until a short page or a cursor
    /// that does not advance (repeated or non-monotonic; the server contract
    /// is id-ascending), so a server that never returns an empty page cannot
    /// loop the walk forever.
    pub async fn list_all(&self, page_size: u32) -> Result<Vec<AuthIdpUser>> {
        validate_page_limit(page_size, MAX_USER_PAGE_SIZE, "user")?;
        let mut users = Vec::new();
        let mut cursor: Option<String> = None;
        loop {
            let page = self.list(Some(page_size), cursor.as_deref()).await?;
            // A short page is by definition the last page.
            let full = page.data.len() == page_size as usize;
            let next = page.data.last().map(|user| user.id.clone());
            users.extend(page.data);
            if !full {
                return Ok(users);
            }
            let next = next.expect("full page");
            if cursor
                .as_deref()
                .is_some_and(|previous| previous >= next.as_str())
            {
                return Ok(users);
            }
            cursor = Some(next);
        }
    }

    /// Fetch one user with private metadata included.
    pub async fn get(&self, user_id: &str) -> Result<AuthIdpUser> {
        self.client
            .management_no_body(
                "auth_idp.users.get",
                Method::Get,
                &format!("/users/{}", path_id(user_id, "user_id")?),
            )
            .await
    }

    /// Create a user.
    pub async fn create(&self, request: CreateAuthIdpUser) -> Result<AuthIdpUser> {
        self.client
            .management_json("auth_idp.users.create", Method::Post, "/users", &request)
            .await
    }

    /// Patch a user with tri-state semantics per field.
    pub async fn patch(&self, user_id: &str, patch: AuthIdpUserPatch) -> Result<AuthIdpUser> {
        let body = patch.into_body()?;
        self.client
            .management_json(
                "auth_idp.users.patch",
                Method::Patch,
                &format!("/users/{}", path_id(user_id, "user_id")?),
                &body,
            )
            .await
    }

    /// Soft-delete a user, releasing all claims.
    pub async fn delete(&self, user_id: &str) -> Result<()> {
        self.client
            .management_empty(
                "auth_idp.users.delete",
                Method::Delete,
                &format!("/users/{}", path_id(user_id, "user_id")?),
            )
            .await
    }

    /// Mint a one-time impersonation token for a user.
    pub async fn impersonate(&self, user_id: &str) -> Result<AuthIdpImpersonationToken> {
        self.client
            .management_no_body(
                "auth_idp.users.impersonate",
                Method::Post,
                &format!("/users/{}/impersonate", path_id(user_id, "user_id")?),
            )
            .await
    }

    /// List a user's sessions.
    pub async fn list_sessions(&self, user_id: &str) -> Result<Vec<AuthIdpSession>> {
        let page: DataEnvelope<AuthIdpSession> = self
            .client
            .management_no_body(
                "auth_idp.users.sessions.list",
                Method::Get,
                &format!("/users/{}/sessions", path_id(user_id, "user_id")?),
            )
            .await?;
        Ok(page.data)
    }

    /// Revoke every session of a user.
    pub async fn revoke_all_sessions(&self, user_id: &str) -> Result<()> {
        self.client
            .management_empty(
                "auth_idp.users.sessions.revoke_all",
                Method::Post,
                &format!(
                    "/users/{}/sessions/revoke-all",
                    path_id(user_id, "user_id")?
                ),
            )
            .await
    }

    /// List a user's MFA factors.
    pub async fn list_mfa_factors(&self, user_id: &str) -> Result<Vec<AuthIdpMfaFactor>> {
        let page: DataEnvelope<AuthIdpMfaFactor> = self
            .client
            .management_no_body(
                "auth_idp.users.mfa.list",
                Method::Get,
                &format!("/users/{}/mfa-factors", path_id(user_id, "user_id")?),
            )
            .await?;
        Ok(page.data)
    }

    /// Delete one of a user's MFA factors.
    pub async fn delete_mfa_factor(&self, user_id: &str, factor_id: &str) -> Result<()> {
        self.client
            .management_empty(
                "auth_idp.users.mfa.delete",
                Method::Delete,
                &format!(
                    "/users/{}/mfa-factors/{}",
                    path_id(user_id, "user_id")?,
                    path_id(factor_id, "factor_id")?
                ),
            )
            .await
    }

    /// Regenerate a factor's recovery codes; returns the ten plaintext codes.
    pub async fn regenerate_recovery_codes(
        &self,
        user_id: &str,
        factor_id: &str,
    ) -> Result<Vec<String>> {
        let page: DataEnvelope<String> = self
            .client
            .management_no_body(
                "auth_idp.users.mfa.regenerate_recovery",
                Method::Post,
                &format!(
                    "/users/{}/mfa-factors/{}/regenerate-recovery",
                    path_id(user_id, "user_id")?,
                    path_id(factor_id, "factor_id")?
                ),
            )
            .await?;
        Ok(page.data)
    }

    /// List a user's passkeys (`{"passkeys":[...]}` envelope).
    pub async fn list_passkeys(&self, user_id: &str) -> Result<Vec<AuthIdpPasskey>> {
        let page: PasskeysEnvelope = self
            .client
            .management_no_body(
                "auth_idp.users.passkeys.list",
                Method::Get,
                &format!("/users/{}/passkeys", path_id(user_id, "user_id")?),
            )
            .await?;
        Ok(page.passkeys)
    }

    /// Delete one of a user's passkeys.
    pub async fn delete_passkey(&self, user_id: &str, passkey_id: &str) -> Result<()> {
        self.client
            .management_empty(
                "auth_idp.users.passkeys.delete",
                Method::Delete,
                &format!(
                    "/users/{}/passkeys/{}",
                    path_id(user_id, "user_id")?,
                    path_id(passkey_id, "passkey_id")?
                ),
            )
            .await
    }

    /// List a user's organization memberships with the full organization
    /// records (bare array envelope).
    pub async fn list_organizations(&self, user_id: &str) -> Result<Vec<AuthIdpUserMembership>> {
        self.client
            .management_no_body(
                "auth_idp.users.organizations.list",
                Method::Get,
                &format!("/users/{}/organizations", path_id(user_id, "user_id")?),
            )
            .await
    }
}

// ---------------------------------------------------------------------------
// Sessions
// ---------------------------------------------------------------------------

/// Session revocation.
#[derive(Debug, Clone)]
pub struct AuthIdpSessions {
    client: AuthIdpClient,
}

impl AuthIdpSessions {
    /// Revoke one session by id.
    pub async fn revoke(&self, session_id: &str) -> Result<()> {
        self.client
            .management_empty(
                "auth_idp.sessions.revoke",
                Method::Post,
                &format!("/sessions/{}/revoke", path_id(session_id, "session_id")?),
            )
            .await
    }
}

// ---------------------------------------------------------------------------
// API keys
// ---------------------------------------------------------------------------

/// An instance API key record (never carries the secret).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpApiKey {
    /// Key id.
    pub id: String,
    /// Caller-assigned name.
    pub name: String,
    /// Granted scopes.
    pub scopes: Vec<String>,
    /// Creation time (unix seconds).
    pub created_at: i64,
    /// Last use time (unix seconds), when used.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub last_used_at: Option<i64>,
    /// Whether the key was revoked.
    pub revoked: bool,
}

/// A freshly minted API key with its one-time secret (Debug redacted).
#[derive(Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpApiKeyCreated {
    /// Key id.
    pub id: String,
    /// Caller-assigned name.
    pub name: String,
    /// The full `sk_` bearer key; shown only by this response.
    pub api_key: String,
    /// Granted scopes.
    pub scopes: Vec<String>,
}

impl std::fmt::Debug for AuthIdpApiKeyCreated {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("AuthIdpApiKeyCreated")
            .field("id", &self.id)
            .field("name", &self.name)
            .field("scopes", &self.scopes)
            .field("api_key", &"[REDACTED]")
            .finish()
    }
}

/// Instance API key minting and revocation.
#[derive(Debug, Clone)]
pub struct AuthIdpApiKeys {
    client: AuthIdpClient,
}

impl AuthIdpApiKeys {
    /// Mint an API key; the secret is returned once.
    pub async fn create(
        &self,
        name: &str,
        scopes: Option<&[String]>,
    ) -> Result<AuthIdpApiKeyCreated> {
        validate_non_empty(name, "name")?;
        if let Some(scopes) = scopes {
            for scope in scopes {
                validate_non_empty(scope, "scope")?;
            }
        }
        #[derive(Serialize)]
        struct Body<'a> {
            name: &'a str,
            #[serde(skip_serializing_if = "Option::is_none")]
            scopes: Option<&'a [String]>,
        }
        self.client
            .management_json(
                "auth_idp.api_keys.create",
                Method::Post,
                "/api-keys",
                &Body {
                    name: name.trim(),
                    scopes,
                },
            )
            .await
    }

    /// List API key records; secrets are never included.
    pub async fn list(&self) -> Result<Vec<AuthIdpApiKey>> {
        let page: DataEnvelope<AuthIdpApiKey> = self
            .client
            .management_no_body("auth_idp.api_keys.list", Method::Get, "/api-keys")
            .await?;
        Ok(page.data)
    }

    /// Revoke an API key by id.
    pub async fn delete(&self, key_id: &str) -> Result<()> {
        self.client
            .management_empty(
                "auth_idp.api_keys.delete",
                Method::Delete,
                &format!("/api-keys/{}", path_id(key_id, "key_id")?),
            )
            .await
    }
}

// ---------------------------------------------------------------------------
// Organizations
// ---------------------------------------------------------------------------

/// An organization (snake_case wire).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpOrganization {
    /// Organization id.
    pub id: String,
    /// Display name.
    pub name: String,
    /// URL-safe slug (derived from the name when not provided).
    pub slug: String,
    /// Creation time (unix seconds).
    pub created_at: i64,
}

/// Summary of the member user attached to a membership list entry.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpMembershipUser {
    /// User id.
    pub id: String,
    /// Email address, when the user has one.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub email: Option<String>,
    /// Display name, when set.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
}

/// An organization membership (snake_case wire).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpMembership {
    /// Membership id.
    pub id: String,
    /// Owning organization.
    pub organization_id: String,
    /// Member user.
    pub user_id: String,
    /// Membership role.
    pub role: String,
    /// Creation time (unix seconds).
    pub created_at: i64,
    /// Member summary; present on membership lists, null for deleted users.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub user: Option<AuthIdpMembershipUser>,
}

/// An organization invitation (snake_case wire).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpInvitation {
    /// Invitation id.
    pub id: String,
    /// Owning organization.
    pub organization_id: String,
    /// Invited email address.
    pub email: String,
    /// Granted role.
    pub role: String,
    /// Invitation status.
    pub status: String,
    /// Expiry time (unix seconds).
    pub expires_at: i64,
    /// Creation time (unix seconds).
    pub created_at: i64,
}

/// A verified organization domain (snake_case wire).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpOrganizationDomain {
    /// Domain id.
    pub id: String,
    /// Owning organization.
    pub organization_id: String,
    /// Verified domain name.
    pub domain: String,
    /// Creation time (unix seconds).
    pub created_at: i64,
}

/// Result of a domain enrollment: the organization the user was attached to.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpEnrollment {
    /// Organization the user was enrolled into.
    pub org_id: String,
}

/// Organizations, memberships, invitations, and domains.
#[derive(Debug, Clone)]
pub struct AuthIdpOrganizations {
    client: AuthIdpClient,
}

impl AuthIdpOrganizations {
    /// Create an organization; the slug is derived from the name when absent.
    pub async fn create(&self, name: &str, slug: Option<&str>) -> Result<AuthIdpOrganization> {
        validate_non_empty(name, "name")?;
        if let Some(slug) = slug {
            validate_non_empty(slug, "slug")?;
        }
        #[derive(Serialize)]
        struct Body<'a> {
            name: &'a str,
            #[serde(skip_serializing_if = "Option::is_none")]
            slug: Option<&'a str>,
        }
        self.client
            .management_json(
                "auth_idp.organizations.create",
                Method::Post,
                "/organizations",
                &Body {
                    name: name.trim(),
                    slug: slug.map(str::trim),
                },
            )
            .await
    }

    /// List organizations (bare array envelope).
    pub async fn list(&self) -> Result<Vec<AuthIdpOrganization>> {
        self.client
            .management_no_body("auth_idp.organizations.list", Method::Get, "/organizations")
            .await
    }

    /// Fetch one organization.
    pub async fn get(&self, org_id: &str) -> Result<AuthIdpOrganization> {
        self.client
            .management_no_body(
                "auth_idp.organizations.get",
                Method::Get,
                &format!("/organizations/{}", path_id(org_id, "org_id")?),
            )
            .await
    }

    /// Rename an organization (name only).
    pub async fn patch(&self, org_id: &str, name: &str) -> Result<AuthIdpOrganization> {
        validate_non_empty(name, "name")?;
        #[derive(Serialize)]
        struct Body<'a> {
            name: &'a str,
        }
        self.client
            .management_json(
                "auth_idp.organizations.patch",
                Method::Patch,
                &format!("/organizations/{}", path_id(org_id, "org_id")?),
                &Body { name: name.trim() },
            )
            .await
    }

    /// Delete an organization.
    pub async fn delete(&self, org_id: &str) -> Result<()> {
        self.client
            .management_empty(
                "auth_idp.organizations.delete",
                Method::Delete,
                &format!("/organizations/{}", path_id(org_id, "org_id")?),
            )
            .await
    }

    /// Add a user to an organization.
    pub async fn create_membership(
        &self,
        org_id: &str,
        user_id: &str,
        role: &str,
    ) -> Result<AuthIdpMembership> {
        validate_non_empty(user_id, "user_id")?;
        validate_non_empty(role, "role")?;
        #[derive(Serialize)]
        struct Body<'a> {
            user_id: &'a str,
            role: &'a str,
        }
        self.client
            .management_json(
                "auth_idp.organizations.memberships.create",
                Method::Post,
                &format!("/organizations/{}/memberships", path_id(org_id, "org_id")?),
                &Body {
                    user_id: user_id.trim(),
                    role: role.trim(),
                },
            )
            .await
    }

    /// List an organization's memberships with member summaries.
    pub async fn list_memberships(&self, org_id: &str) -> Result<Vec<AuthIdpMembership>> {
        self.client
            .management_no_body(
                "auth_idp.organizations.memberships.list",
                Method::Get,
                &format!("/organizations/{}/memberships", path_id(org_id, "org_id")?),
            )
            .await
    }

    /// Change a member's role.
    pub async fn patch_membership(
        &self,
        org_id: &str,
        membership_id: &str,
        role: &str,
    ) -> Result<AuthIdpMembership> {
        validate_non_empty(role, "role")?;
        #[derive(Serialize)]
        struct Body<'a> {
            role: &'a str,
        }
        self.client
            .management_json(
                "auth_idp.organizations.memberships.patch",
                Method::Patch,
                &format!(
                    "/organizations/{}/memberships/{}",
                    path_id(org_id, "org_id")?,
                    path_id(membership_id, "membership_id")?
                ),
                &Body { role: role.trim() },
            )
            .await
    }

    /// Remove a member.
    pub async fn delete_membership(&self, org_id: &str, membership_id: &str) -> Result<()> {
        self.client
            .management_empty(
                "auth_idp.organizations.memberships.delete",
                Method::Delete,
                &format!(
                    "/organizations/{}/memberships/{}",
                    path_id(org_id, "org_id")?,
                    path_id(membership_id, "membership_id")?
                ),
            )
            .await
    }

    /// Invite a user to an organization by email.
    pub async fn create_invitation(
        &self,
        org_id: &str,
        email: &str,
        role: &str,
    ) -> Result<AuthIdpInvitation> {
        validate_non_empty(email, "email")?;
        validate_non_empty(role, "role")?;
        #[derive(Serialize)]
        struct Body<'a> {
            email: &'a str,
            role: &'a str,
        }
        self.client
            .management_json(
                "auth_idp.organizations.invitations.create",
                Method::Post,
                &format!("/organizations/{}/invitations", path_id(org_id, "org_id")?),
                &Body {
                    email: email.trim(),
                    role: role.trim(),
                },
            )
            .await
    }

    /// List an organization's invitations.
    pub async fn list_invitations(&self, org_id: &str) -> Result<Vec<AuthIdpInvitation>> {
        self.client
            .management_no_body(
                "auth_idp.organizations.invitations.list",
                Method::Get,
                &format!("/organizations/{}/invitations", path_id(org_id, "org_id")?),
            )
            .await
    }

    /// Revoke a pending invitation.
    pub async fn revoke_invitation(
        &self,
        org_id: &str,
        invitation_id: &str,
    ) -> Result<AuthIdpInvitation> {
        self.client
            .management_no_body(
                "auth_idp.organizations.invitations.revoke",
                Method::Post,
                &format!(
                    "/organizations/{}/invitations/{}/revoke",
                    path_id(org_id, "org_id")?,
                    path_id(invitation_id, "invitation_id")?
                ),
            )
            .await
    }

    /// Add a verified domain to an organization (unique per instance).
    pub async fn create_domain(
        &self,
        org_id: &str,
        domain: &str,
    ) -> Result<AuthIdpOrganizationDomain> {
        validate_non_empty(domain, "domain")?;
        #[derive(Serialize)]
        struct Body<'a> {
            domain: &'a str,
        }
        self.client
            .management_json(
                "auth_idp.organizations.domains.create",
                Method::Post,
                &format!("/organizations/{}/domains", path_id(org_id, "org_id")?),
                &Body {
                    domain: domain.trim(),
                },
            )
            .await
    }

    /// List an organization's verified domains.
    pub async fn list_domains(&self, org_id: &str) -> Result<Vec<AuthIdpOrganizationDomain>> {
        self.client
            .management_no_body(
                "auth_idp.organizations.domains.list",
                Method::Get,
                &format!("/organizations/{}/domains", path_id(org_id, "org_id")?),
            )
            .await
    }

    /// Remove a verified domain.
    pub async fn delete_domain(&self, org_id: &str, domain_id: &str) -> Result<()> {
        self.client
            .management_empty(
                "auth_idp.organizations.domains.delete",
                Method::Delete,
                &format!(
                    "/organizations/{}/domains/{}",
                    path_id(org_id, "org_id")?,
                    path_id(domain_id, "domain_id")?
                ),
            )
            .await
    }

    /// Enroll a user into the organization owning `domain` (case-insensitive
    /// exact match; the user's email must end `@{domain}`; idempotent).
    pub async fn enroll(&self, domain: &str, user_id: &str) -> Result<AuthIdpEnrollment> {
        validate_non_empty(domain, "domain")?;
        validate_non_empty(user_id, "user_id")?;
        #[derive(Serialize)]
        struct Body<'a> {
            user_id: &'a str,
        }
        self.client
            .management_json(
                "auth_idp.domains.enroll",
                Method::Post,
                &format!("/domains/{}/enroll", path_id(domain, "domain")?),
                &Body {
                    user_id: user_id.trim(),
                },
            )
            .await
    }
}

// ---------------------------------------------------------------------------
// OAuth clients
// ---------------------------------------------------------------------------

/// Request body for OAuth client creation (snake_case wire).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CreateAuthIdpOauthClient {
    /// Caller-assigned name.
    pub name: String,
    /// One or more redirect URIs.
    pub redirect_uris: Vec<String>,
    /// Whether the client is public (no client secret).
    #[serde(default)]
    pub public: bool,
    /// Token endpoint auth method (defaults server-side to
    /// `client_secret_basic` for confidential clients and `none` for public
    /// ones).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub token_endpoint_auth_method: Option<String>,
}

impl CreateAuthIdpOauthClient {
    /// Build a confidential-client request with at least one redirect URI.
    pub fn new(name: impl Into<String>, redirect_uris: Vec<String>) -> Self {
        Self {
            name: name.into(),
            redirect_uris,
            public: false,
            token_endpoint_auth_method: None,
        }
    }

    fn validate(&self) -> Result<()> {
        validate_non_empty(&self.name, "name")?;
        if self.redirect_uris.is_empty() {
            return Err(validation(
                "invalid_redirect_uris",
                "redirect_uris must contain at least one URI.",
            ));
        }
        for uri in &self.redirect_uris {
            validate_non_empty(uri, "redirect_uri")?;
        }
        Ok(())
    }
}

/// An OAuth client record (camelCase wire; secrets are never included).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AuthIdpOauthClient {
    /// Client record id (`clt_...`).
    pub id: String,
    /// OAuth client id used on the wire.
    pub client_id: String,
    /// Caller-assigned name.
    pub name: String,
    /// Registered redirect URIs.
    pub redirect_uris: Vec<String>,
    /// Token endpoint auth method.
    pub token_endpoint_auth_method: String,
    /// Whether the client is public.
    pub public: bool,
    /// Creation time (unix seconds).
    pub created_at: i64,
}

/// A freshly created OAuth client with its one-time secret (Debug redacted;
/// the secret is null for public clients and `none` auth).
#[derive(Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpOauthClientCreated {
    /// The created client.
    pub client: AuthIdpOauthClient,
    /// The one-time client secret.
    pub client_secret: Option<String>,
}

impl std::fmt::Debug for AuthIdpOauthClientCreated {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("AuthIdpOauthClientCreated")
            .field("client", &self.client)
            .field("client_secret", &"[REDACTED]")
            .finish()
    }
}

/// OAuth client management.
#[derive(Debug, Clone)]
pub struct AuthIdpOauthClients {
    client: AuthIdpClient,
}

impl AuthIdpOauthClients {
    /// Create an OAuth client; the secret is returned once.
    pub async fn create(
        &self,
        request: CreateAuthIdpOauthClient,
    ) -> Result<AuthIdpOauthClientCreated> {
        request.validate()?;
        self.client
            .management_json(
                "auth_idp.oauth_clients.create",
                Method::Post,
                "/oauth-clients",
                &request,
            )
            .await
    }

    /// List OAuth clients (`{"clients":[...]}` envelope, no secrets).
    pub async fn list(&self) -> Result<Vec<AuthIdpOauthClient>> {
        let page: ClientsEnvelope = self
            .client
            .management_no_body("auth_idp.oauth_clients.list", Method::Get, "/oauth-clients")
            .await?;
        Ok(page.clients)
    }

    /// Delete an OAuth client by record id (`clt_...`).
    pub async fn delete(&self, client_id: &str) -> Result<()> {
        self.client
            .management_empty(
                "auth_idp.oauth_clients.delete",
                Method::Delete,
                &format!("/oauth-clients/{}", path_id(client_id, "client_id")?),
            )
            .await
    }
}

// ---------------------------------------------------------------------------
// Connections
// ---------------------------------------------------------------------------

/// Connection protocol: OIDC or SAML.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum AuthIdpConnectionType {
    /// OpenID Connect.
    #[serde(rename = "oidc")]
    Oidc,
    /// SAML 2.0.
    #[serde(rename = "saml")]
    Saml,
}

/// Request body for connection creation (snake_case wire). Use
/// [`CreateAuthIdpConnection::oidc`] or [`CreateAuthIdpConnection::saml`];
/// secrets are accepted at creation and never returned.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CreateAuthIdpConnection {
    /// Connection protocol.
    #[serde(rename = "type")]
    pub connection_type: AuthIdpConnectionType,
    /// Caller-assigned name.
    pub name: String,
    /// Owning organization, when the connection is org-scoped.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub org_id: Option<String>,
    /// OIDC: issuer URL (absolute HTTP(S)).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub issuer_url: Option<String>,
    /// OIDC: client id issued by the upstream IdP.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub client_id: Option<String>,
    /// OIDC: client secret issued by the upstream IdP.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub client_secret: Option<String>,
    /// OIDC: scopes to request (accepted, not persisted).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub scopes: Option<Vec<String>>,
    /// OIDC: explicit authorization endpoint (avoids discovery).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub authorization_endpoint: Option<String>,
    /// OIDC: explicit token endpoint (avoids discovery).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub token_endpoint: Option<String>,
    /// OIDC: explicit userinfo endpoint (avoids discovery).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub userinfo_endpoint: Option<String>,
    /// SAML: IdP entity id.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub idp_entity_id: Option<String>,
    /// SAML: SSO redirect URL (absolute HTTP(S)).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sso_redirect_url: Option<String>,
    /// SAML: X.509 certificate in PEM form.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub x509_certificate_pem: Option<String>,
}

impl CreateAuthIdpConnection {
    /// Start an OIDC connection; explicit endpoints can be added to skip
    /// discovery.
    #[must_use]
    pub fn oidc(
        name: impl Into<String>,
        issuer_url: impl Into<String>,
        client_id: impl Into<String>,
        client_secret: impl Into<String>,
    ) -> Self {
        Self {
            connection_type: AuthIdpConnectionType::Oidc,
            name: name.into(),
            org_id: None,
            issuer_url: Some(issuer_url.into()),
            client_id: Some(client_id.into()),
            client_secret: Some(client_secret.into()),
            scopes: None,
            authorization_endpoint: None,
            token_endpoint: None,
            userinfo_endpoint: None,
            idp_entity_id: None,
            sso_redirect_url: None,
            x509_certificate_pem: None,
        }
    }

    /// Start a SAML connection.
    #[must_use]
    pub fn saml(
        name: impl Into<String>,
        idp_entity_id: impl Into<String>,
        sso_redirect_url: impl Into<String>,
        x509_certificate_pem: impl Into<String>,
    ) -> Self {
        Self {
            connection_type: AuthIdpConnectionType::Saml,
            name: name.into(),
            org_id: None,
            issuer_url: None,
            client_id: None,
            client_secret: None,
            scopes: None,
            authorization_endpoint: None,
            token_endpoint: None,
            userinfo_endpoint: None,
            idp_entity_id: Some(idp_entity_id.into()),
            sso_redirect_url: Some(sso_redirect_url.into()),
            x509_certificate_pem: Some(x509_certificate_pem.into()),
        }
    }

    /// Scope the connection to one organization.
    #[must_use]
    pub fn org_id(mut self, value: impl Into<String>) -> Self {
        self.org_id = Some(value.into());
        self
    }

    /// Request extra OIDC scopes (accepted, not persisted).
    #[must_use]
    pub fn scopes(mut self, value: Vec<String>) -> Self {
        self.scopes = Some(value);
        self
    }

    /// Set an explicit authorization endpoint.
    #[must_use]
    pub fn authorization_endpoint(mut self, value: impl Into<String>) -> Self {
        self.authorization_endpoint = Some(value.into());
        self
    }

    /// Set an explicit token endpoint.
    #[must_use]
    pub fn token_endpoint(mut self, value: impl Into<String>) -> Self {
        self.token_endpoint = Some(value.into());
        self
    }

    /// Set an explicit userinfo endpoint.
    #[must_use]
    pub fn userinfo_endpoint(mut self, value: impl Into<String>) -> Self {
        self.userinfo_endpoint = Some(value.into());
        self
    }

    fn validate(&self) -> Result<()> {
        validate_non_empty(&self.name, "name")?;
        if let Some(org_id) = &self.org_id {
            validate_non_empty(org_id, "org_id")?;
        }
        match self.connection_type {
            AuthIdpConnectionType::Oidc => {
                validate_http_url(self.issuer_url.as_deref().unwrap_or_default(), "issuer_url")?;
                validate_non_empty(self.client_id.as_deref().unwrap_or_default(), "client_id")?;
                validate_non_empty(
                    self.client_secret.as_deref().unwrap_or_default(),
                    "client_secret",
                )?;
                for (field, value) in [
                    ("authorization_endpoint", &self.authorization_endpoint),
                    ("token_endpoint", &self.token_endpoint),
                    ("userinfo_endpoint", &self.userinfo_endpoint),
                ] {
                    if let Some(value) = value {
                        validate_http_url(value, field)?;
                    }
                }
            }
            AuthIdpConnectionType::Saml => {
                validate_non_empty(
                    self.idp_entity_id.as_deref().unwrap_or_default(),
                    "idp_entity_id",
                )?;
                validate_http_url(
                    self.sso_redirect_url.as_deref().unwrap_or_default(),
                    "sso_redirect_url",
                )?;
                validate_non_empty(
                    self.x509_certificate_pem.as_deref().unwrap_or_default(),
                    "x509_certificate_pem",
                )?;
            }
        }
        Ok(())
    }
}

/// Connection patch body; absent fields are unchanged.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct PatchAuthIdpConnection {
    /// Enable or disable the connection.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub enabled: Option<bool>,
    /// Rename the connection.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    /// Replace the upstream client secret (OIDC only).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub client_secret: Option<String>,
}

/// An SSO connection view (snake_case wire; secrets are never returned).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpConnection {
    /// Connection id.
    pub id: String,
    /// Connection protocol.
    #[serde(rename = "type")]
    pub connection_type: String,
    /// Caller-assigned name.
    pub name: String,
    /// Whether the connection is enabled.
    pub enabled: bool,
    /// Owning organization, when org-scoped.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub org_id: Option<String>,
    /// OIDC issuer URL.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub issuer_url: Option<String>,
    /// OIDC client id.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub client_id: Option<String>,
    /// OIDC authorization endpoint.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub authorization_endpoint: Option<String>,
    /// OIDC token endpoint.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub token_endpoint: Option<String>,
    /// OIDC userinfo endpoint.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub userinfo_endpoint: Option<String>,
    /// SAML IdP entity id.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub idp_entity_id: Option<String>,
    /// SAML SSO redirect URL.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sso_redirect_url: Option<String>,
    /// SAML SP entity id.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sp_entity_id: Option<String>,
    /// Creation time (unix seconds).
    pub created_at: i64,
}

/// SSO connection management.
#[derive(Debug, Clone)]
pub struct AuthIdpConnections {
    client: AuthIdpClient,
}

impl AuthIdpConnections {
    /// Create a connection; secrets are accepted and never re-served.
    pub async fn create(&self, request: CreateAuthIdpConnection) -> Result<AuthIdpConnection> {
        request.validate()?;
        self.client
            .management_json(
                "auth_idp.connections.create",
                Method::Post,
                "/connections",
                &request,
            )
            .await
    }

    /// List connections ordered by creation time (`{"data":[...]}` envelope).
    pub async fn list(&self) -> Result<Vec<AuthIdpConnection>> {
        let page: DataEnvelope<AuthIdpConnection> = self
            .client
            .management_no_body("auth_idp.connections.list", Method::Get, "/connections")
            .await?;
        Ok(page.data)
    }

    /// Patch a connection; absent fields are unchanged.
    pub async fn patch(
        &self,
        connection_id: &str,
        patch: PatchAuthIdpConnection,
    ) -> Result<AuthIdpConnection> {
        if let Some(name) = &patch.name {
            validate_non_empty(name, "name")?;
        }
        if let Some(secret) = &patch.client_secret {
            validate_non_empty(secret, "client_secret")?;
        }
        self.client
            .management_json(
                "auth_idp.connections.patch",
                Method::Patch,
                &format!("/connections/{}", path_id(connection_id, "connection_id")?),
                &patch,
            )
            .await
    }

    /// Delete a connection.
    pub async fn delete(&self, connection_id: &str) -> Result<()> {
        self.client
            .management_empty(
                "auth_idp.connections.delete",
                Method::Delete,
                &format!("/connections/{}", path_id(connection_id, "connection_id")?),
            )
            .await
    }
}

// ---------------------------------------------------------------------------
// Directories
// ---------------------------------------------------------------------------

/// A created or rotated SCIM directory with its one-time token
/// (Debug redacted).
#[derive(Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpDirectoryToken {
    /// Directory id.
    pub id: String,
    /// Owning organization, when set.
    pub org_id: Option<String>,
    /// The `scim_` bearer token; shown only by creation and rotation, and a
    /// rotation kills the previous token immediately.
    pub token: String,
}

impl std::fmt::Debug for AuthIdpDirectoryToken {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("AuthIdpDirectoryToken")
            .field("id", &self.id)
            .field("org_id", &self.org_id)
            .field("token", &"[REDACTED]")
            .finish()
    }
}

/// A SCIM directory record (snake_case wire; the token is never included).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpDirectory {
    /// Directory id.
    pub id: String,
    /// Owning organization, when set.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub org_id: Option<String>,
    /// Creation time (unix seconds).
    pub created_at: i64,
}

/// SCIM directories and token rotation.
#[derive(Debug, Clone)]
pub struct AuthIdpDirectories {
    client: AuthIdpClient,
}

impl AuthIdpDirectories {
    /// Create a SCIM directory; the token is returned once.
    pub async fn create(&self, org_id: Option<&str>) -> Result<AuthIdpDirectoryToken> {
        if let Some(org_id) = org_id {
            validate_non_empty(org_id, "org_id")?;
        }
        #[derive(Serialize)]
        struct Body<'a> {
            #[serde(skip_serializing_if = "Option::is_none")]
            org_id: Option<&'a str>,
        }
        self.client
            .management_json(
                "auth_idp.directories.create",
                Method::Post,
                "/directories",
                &Body {
                    org_id: org_id.map(str::trim),
                },
            )
            .await
    }

    /// List directories (`{"data":[...]}` envelope; no tokens).
    pub async fn list(&self) -> Result<Vec<AuthIdpDirectory>> {
        let page: DataEnvelope<AuthIdpDirectory> = self
            .client
            .management_no_body("auth_idp.directories.list", Method::Get, "/directories")
            .await?;
        Ok(page.data)
    }

    /// Rotate the directory token; the old token dies immediately.
    pub async fn rotate(&self, directory_id: &str) -> Result<AuthIdpDirectoryToken> {
        self.client
            .management_no_body(
                "auth_idp.directories.rotate",
                Method::Post,
                &format!(
                    "/directories/{}/rotate",
                    path_id(directory_id, "directory_id")?
                ),
            )
            .await
    }

    /// Delete a directory.
    pub async fn delete(&self, directory_id: &str) -> Result<()> {
        self.client
            .management_empty(
                "auth_idp.directories.delete",
                Method::Delete,
                &format!("/directories/{}", path_id(directory_id, "directory_id")?),
            )
            .await
    }
}

// ---------------------------------------------------------------------------
// JWT templates
// ---------------------------------------------------------------------------

/// Request body for JWT template creation (snake_case wire).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CreateAuthIdpJwtTemplate {
    /// Template name (1-48 of `[a-z0-9-_]`; the server lowercases).
    pub name: String,
    /// Non-empty set of static or `{{user.*}}`/`{{org.*}}` claim templates.
    pub claims: Map<String, Value>,
    /// Token lifetime (seconds; server default 300).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub lifetime_seconds: Option<u32>,
}

impl CreateAuthIdpJwtTemplate {
    /// Build a template with a non-empty claims map.
    pub fn new(name: impl Into<String>, claims: Map<String, Value>) -> Self {
        Self {
            name: name.into(),
            claims,
            lifetime_seconds: None,
        }
    }
}

/// JWT template patch body; absent fields are unchanged.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct PatchAuthIdpJwtTemplate {
    /// New template name.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    /// Replacement claims map.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub claims: Option<Map<String, Value>>,
    /// New lifetime (seconds).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub lifetime_seconds: Option<u32>,
}

/// A JWT template record (snake_case wire).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpJwtTemplate {
    /// Template id.
    pub id: String,
    /// Template name.
    pub name: String,
    /// Claim templates.
    pub claims: Map<String, Value>,
    /// Token lifetime (seconds).
    pub lifetime_seconds: u32,
    /// Creation time (unix seconds).
    pub created_at: i64,
}

/// A minted ES256 compact JWT (Debug redacted).
#[derive(Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpJwtToken {
    /// The signed compact JWT with substituted claims.
    pub jwt: String,
}

impl std::fmt::Debug for AuthIdpJwtToken {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("AuthIdpJwtToken")
            .field("jwt", &"[REDACTED]")
            .finish()
    }
}

/// JWT templates and per-template token minting. Templates are addressed by
/// name, not id.
#[derive(Debug, Clone)]
pub struct AuthIdpJwtTemplates {
    client: AuthIdpClient,
}

impl AuthIdpJwtTemplates {
    /// Create a JWT template.
    pub async fn create(&self, request: CreateAuthIdpJwtTemplate) -> Result<AuthIdpJwtTemplate> {
        validate_template_name(&request.name)?;
        if request.claims.is_empty() {
            return Err(validation(
                "invalid_claims",
                "claims must contain at least one entry.",
            ));
        }
        self.client
            .management_json(
                "auth_idp.jwt_templates.create",
                Method::Post,
                "/jwt-templates",
                &request,
            )
            .await
    }

    /// List JWT templates (bare array envelope).
    pub async fn list(&self) -> Result<Vec<AuthIdpJwtTemplate>> {
        self.client
            .management_no_body("auth_idp.jwt_templates.list", Method::Get, "/jwt-templates")
            .await
    }

    /// Patch a JWT template by name; absent fields are unchanged.
    pub async fn patch(
        &self,
        name: &str,
        patch: PatchAuthIdpJwtTemplate,
    ) -> Result<AuthIdpJwtTemplate> {
        validate_template_name(name)?;
        if let Some(name) = &patch.name {
            validate_template_name(name)?;
        }
        if patch.claims.as_ref().is_some_and(Map::is_empty) {
            return Err(validation(
                "invalid_claims",
                "claims must contain at least one entry.",
            ));
        }
        self.client
            .management_json(
                "auth_idp.jwt_templates.patch",
                Method::Patch,
                &format!("/jwt-templates/{}", percent_encode(name.trim())),
                &patch,
            )
            .await
    }

    /// Delete a JWT template by name.
    pub async fn delete(&self, name: &str) -> Result<()> {
        validate_template_name(name)?;
        self.client
            .management_empty(
                "auth_idp.jwt_templates.delete",
                Method::Delete,
                &format!("/jwt-templates/{}", percent_encode(name.trim())),
            )
            .await
    }

    /// Mint a JWT for a user from the named template's claims.
    pub async fn create_token(&self, name: &str, user_id: &str) -> Result<AuthIdpJwtToken> {
        validate_template_name(name)?;
        validate_non_empty(user_id, "user_id")?;
        #[derive(Serialize)]
        struct Body<'a> {
            user_id: &'a str,
        }
        self.client
            .management_json(
                "auth_idp.jwt_templates.tokens.create",
                Method::Post,
                &format!("/jwt-templates/{}/tokens", percent_encode(name.trim())),
                &Body {
                    user_id: user_id.trim(),
                },
            )
            .await
    }
}

// ---------------------------------------------------------------------------
// Webhooks
// ---------------------------------------------------------------------------

/// A created webhook with its one-time signing secret (Debug redacted). The
/// secret is the HMAC key for [`verify_auth_idp_webhook`].
#[derive(Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpWebhookCreated {
    /// Webhook id.
    pub id: String,
    /// Delivery URL.
    pub url: String,
    /// Subscribed events.
    pub events: Vec<String>,
    /// The one-time signing secret.
    pub signing_secret: String,
}

impl std::fmt::Debug for AuthIdpWebhookCreated {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("AuthIdpWebhookCreated")
            .field("id", &self.id)
            .field("url", &self.url)
            .field("events", &self.events)
            .field("signing_secret", &"[REDACTED]")
            .finish()
    }
}

/// A webhook endpoint record (snake_case wire).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpWebhook {
    /// Webhook id.
    pub id: String,
    /// Delivery URL.
    pub url: String,
    /// Subscribed events.
    pub events: Vec<String>,
    /// Whether deliveries are enabled.
    pub enabled: bool,
    /// Creation time (unix seconds).
    pub created_at: i64,
}

/// Result of a webhook test delivery.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpWebhookTest {
    /// Whether the test event was queued.
    pub queued: bool,
}

/// A dead-lettered delivery (snake_case wire; newest first).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpDeadLetterEntry {
    /// Composite id `endpointId:eventId`.
    pub id: String,
    /// Webhook endpoint id.
    pub endpoint_id: String,
    /// Event id.
    pub event_id: String,
    /// Delivery attempts so far.
    pub attempts: u32,
    /// Last delivery error, when recorded.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub last_error: Option<String>,
    /// Last update time (unix seconds).
    pub updated_at: i64,
}

/// Result of a dead-letter replay.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpDeadLetterReplay {
    /// Whether the event was re-queued.
    pub replayed: bool,
}

/// Webhook endpoints, delivery tests, and dead-letter replay.
#[derive(Debug, Clone)]
pub struct AuthIdpWebhooks {
    client: AuthIdpClient,
}

impl AuthIdpWebhooks {
    /// Create a webhook; the signing secret is returned once.
    pub async fn create(&self, url: &str, events: &[&str]) -> Result<AuthIdpWebhookCreated> {
        validate_webhook_url(url)?;
        if events.is_empty() {
            return Err(validation(
                "invalid_events",
                "events must contain at least one event.",
            ));
        }
        for event in events {
            validate_non_empty(event, "event")?;
        }
        #[derive(Serialize)]
        struct Body<'a> {
            url: &'a str,
            events: Vec<&'a str>,
        }
        self.client
            .management_json(
                "auth_idp.webhooks.create",
                Method::Post,
                "/webhooks",
                &Body {
                    url: url.trim(),
                    events: events.iter().map(|event| event.trim()).collect(),
                },
            )
            .await
    }

    /// List webhooks (bare array envelope).
    pub async fn list(&self) -> Result<Vec<AuthIdpWebhook>> {
        self.client
            .management_no_body("auth_idp.webhooks.list", Method::Get, "/webhooks")
            .await
    }

    /// Delete a webhook.
    pub async fn delete(&self, webhook_id: &str) -> Result<()> {
        self.client
            .management_empty(
                "auth_idp.webhooks.delete",
                Method::Delete,
                &format!("/webhooks/{}", path_id(webhook_id, "webhook_id")?),
            )
            .await
    }

    /// Queue a test event for a webhook.
    pub async fn test(&self, webhook_id: &str) -> Result<AuthIdpWebhookTest> {
        self.client
            .management_json(
                "auth_idp.webhooks.test",
                Method::Post,
                &format!("/webhooks/{}/test", path_id(webhook_id, "webhook_id")?),
                &EmptyObject {},
            )
            .await
    }

    /// List dead-lettered deliveries (bare array envelope, newest first).
    pub async fn list_dead_letter(&self) -> Result<Vec<AuthIdpDeadLetterEntry>> {
        self.client
            .management_no_body(
                "auth_idp.webhooks.dead_letter.list",
                Method::Get,
                "/webhooks/dead-letter",
            )
            .await
    }

    /// Replay one dead-lettered delivery by its composite id.
    pub async fn replay_dead_letter(&self, id: &str) -> Result<AuthIdpDeadLetterReplay> {
        self.client
            .management_no_body(
                "auth_idp.webhooks.dead_letter.replay",
                Method::Post,
                &format!("/webhooks/dead-letter/{}/replay", path_id(id, "id")?),
            )
            .await
    }
}

// ---------------------------------------------------------------------------
// Audit events
// ---------------------------------------------------------------------------

/// A hash-chained audit event (snake_case wire; `actor_id` is omitted when
/// null).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpAuditEvent {
    /// Event id.
    pub id: String,
    /// Emitting instance.
    pub instance_id: String,
    /// Event type.
    #[serde(rename = "type")]
    pub event_type: String,
    /// Acting key id, when the event has one.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub actor_id: Option<String>,
    /// Event payload.
    pub data: Value,
    /// Creation time (unix seconds).
    pub created_at: i64,
    /// Integrity chain position.
    pub integrity_version: u64,
    /// Hash bound to the chain; null when the audit signing key is unset.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub integrity_hash: Option<String>,
    /// Signature over the hash; null when the audit signing key is unset.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub integrity_signature: Option<String>,
    /// Fingerprint of the signing key; null when the audit signing key is unset.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub integrity_key_fingerprint: Option<String>,
}

/// The hash-chained audit event log.
#[derive(Debug, Clone)]
pub struct AuthIdpAuditEvents {
    client: AuthIdpClient,
}

impl AuthIdpAuditEvents {
    /// List audit events newest first (bare array envelope; `limit` 1-100,
    /// default 50).
    pub async fn list(&self, limit: Option<u32>) -> Result<Vec<AuthIdpAuditEvent>> {
        let suffix = match limit {
            None => String::new(),
            Some(limit) => {
                validate_page_limit(limit, MAX_AUDIT_PAGE_SIZE, "audit event")?;
                format!("?limit={limit}")
            }
        };
        self.client
            .management_no_body(
                "auth_idp.audit_events.list",
                Method::Get,
                &format!("/audit-events{suffix}"),
            )
            .await
    }
}

// ---------------------------------------------------------------------------
// Billing
// ---------------------------------------------------------------------------

/// Instance billing state (snake_case wire).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpBilling {
    /// Current plan.
    pub plan: String,
    /// Subscription status.
    pub status: String,
    /// Stripe customer id, when provisioned.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub stripe_customer_id: Option<String>,
    /// Stripe subscription id, when provisioned.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub stripe_subscription_id: Option<String>,
    /// Current period end (unix seconds), when subscribed.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub current_period_end: Option<i64>,
    /// Billable user count.
    pub user_count: u64,
    /// Plan user quota.
    pub quota: u64,
}

/// A Stripe checkout session URL.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpCheckout {
    /// Hosted checkout URL.
    pub checkout_url: String,
}

/// A Stripe billing portal URL.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpPortal {
    /// Hosted portal URL.
    pub portal_url: String,
}

/// Quota enforcement decision.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpEnforceCheck {
    /// Whether the operation is allowed under the quota.
    pub allowed: bool,
    /// Current plan.
    pub plan: String,
    /// Plan user quota.
    pub quota: u64,
}

/// Quota reconciliation counters.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpQuotaReconcile {
    /// Claims examined.
    pub claims_examined: u64,
    /// Claims removed as stale.
    pub claims_removed: u64,
    /// Claims retained.
    pub claims_retained: u64,
}

/// Plan, checkout, portal, and quota reconciliation.
#[derive(Debug, Clone)]
pub struct AuthIdpBillingClient {
    client: AuthIdpClient,
}

impl AuthIdpBillingClient {
    /// Read the instance billing state.
    pub async fn get(&self) -> Result<AuthIdpBilling> {
        self.client
            .management_no_body("auth_idp.billing.get", Method::Get, "/billing")
            .await
    }

    /// Start a Stripe checkout session for `plan` (`pro` or `enterprise`).
    pub async fn checkout(&self, plan: &str) -> Result<AuthIdpCheckout> {
        let plan = plan.trim();
        if !matches!(plan, "pro" | "enterprise") {
            return Err(validation(
                "invalid_plan",
                "plan must be pro or enterprise.",
            ));
        }
        #[derive(Serialize)]
        struct Body<'a> {
            plan: &'a str,
        }
        self.client
            .management_json(
                "auth_idp.billing.checkout",
                Method::Post,
                "/billing/checkout",
                &Body { plan },
            )
            .await
    }

    /// Open a Stripe billing portal session.
    pub async fn portal(&self) -> Result<AuthIdpPortal> {
        self.client
            .management_json(
                "auth_idp.billing.portal",
                Method::Post,
                "/billing/portal",
                &EmptyObject {},
            )
            .await
    }

    /// Ask whether `current_users` still fits the quota.
    pub async fn enforce_check(&self, current_users: u64) -> Result<AuthIdpEnforceCheck> {
        #[derive(Serialize)]
        struct Body {
            current_users: u64,
        }
        self.client
            .management_json(
                "auth_idp.billing.enforce_check",
                Method::Post,
                "/billing/enforce-check",
                &Body { current_users },
            )
            .await
    }

    /// Reconcile stale user claims against the quota.
    pub async fn reconcile(&self) -> Result<AuthIdpQuotaReconcile> {
        self.client
            .management_no_body(
                "auth_idp.billing.quota_reconcile",
                Method::Post,
                "/billing/quota/reconcile",
            )
            .await
    }
}

// ---------------------------------------------------------------------------
// Instance
// ---------------------------------------------------------------------------

/// Instance patch body; absent fields are unchanged.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct PatchAuthIdpInstance {
    /// New instance name.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    /// New logo URL (absolute HTTP(S) only).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub logo_url: Option<String>,
    /// New theme color (`#hhhhhh`).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub theme_color: Option<String>,
}

impl PatchAuthIdpInstance {
    fn validate(&self) -> Result<()> {
        if let Some(name) = &self.name {
            validate_non_empty(name, "name")?;
        }
        if let Some(logo_url) = &self.logo_url {
            validate_http_url(logo_url, "logo_url")?;
        }
        if let Some(color) = &self.theme_color {
            let valid = color.len() == 7
                && color.starts_with('#')
                && color.as_bytes()[1..]
                    .iter()
                    .all(|byte| byte.is_ascii_hexdigit());
            if !valid {
                return Err(validation(
                    "invalid_theme_color",
                    "theme_color must be # followed by six hex digits.",
                ));
            }
        }
        Ok(())
    }
}

/// Instance branding record (snake_case wire; absent from PATCH responses,
/// hence optional).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AuthIdpInstance {
    /// Instance id.
    pub id: String,
    /// Instance slug.
    pub slug: String,
    /// Instance name.
    pub name: String,
    /// Logo URL, when set.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub logo_url: Option<String>,
    /// Theme color, when set.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub theme_color: Option<String>,
    /// Creation time (unix seconds); not present in PATCH responses.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub created_at: Option<i64>,
}

/// Instance branding settings.
#[derive(Debug, Clone)]
pub struct AuthIdpInstanceClient {
    client: AuthIdpClient,
}

impl AuthIdpInstanceClient {
    /// Read the instance record.
    pub async fn get(&self) -> Result<AuthIdpInstance> {
        self.client
            .management_no_body("auth_idp.instance.get", Method::Get, "/instance")
            .await
    }

    /// Patch instance branding; absent fields are unchanged.
    pub async fn patch(&self, patch: PatchAuthIdpInstance) -> Result<AuthIdpInstance> {
        patch.validate()?;
        self.client
            .management_json(
                "auth_idp.instance.patch",
                Method::Patch,
                "/instance",
                &patch,
            )
            .await
    }
}

// ---------------------------------------------------------------------------
// OIDC (public routes)
// ---------------------------------------------------------------------------

/// The provider discovery document (snake_case wire).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct OidcDiscovery {
    /// Issuer identifier.
    pub issuer: String,
    /// Authorization endpoint URL.
    pub authorization_endpoint: String,
    /// Token endpoint URL.
    pub token_endpoint: String,
    /// Userinfo endpoint URL.
    pub userinfo_endpoint: String,
    /// JWKS URL.
    pub jwks_uri: String,
    /// Revocation endpoint URL.
    pub revocation_endpoint: String,
    /// Introspection endpoint URL.
    pub introspection_endpoint: String,
    /// End-session endpoint URL, when advertised.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub end_session_endpoint: Option<String>,
    /// Supported response types.
    pub response_types_supported: Vec<String>,
    /// Supported grant types.
    pub grant_types_supported: Vec<String>,
    /// Supported PKCE code challenge methods.
    pub code_challenge_methods_supported: Vec<String>,
    /// Supported subject types.
    pub subject_types_supported: Vec<String>,
    /// Supported ID token signing algorithms.
    pub id_token_signing_alg_values_supported: Vec<String>,
    /// Supported token endpoint auth methods.
    pub token_endpoint_auth_methods_supported: Vec<String>,
    /// Supported scopes.
    pub scopes_supported: Vec<String>,
}

/// One EC public key from the JWKS (P-256, ES256).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct OidcJwk {
    /// Key type (`EC`).
    pub kty: String,
    /// Curve (`P-256`).
    pub crv: String,
    /// Algorithm (`ES256`).
    pub alg: String,
    /// Key use (`sig`).
    #[serde(rename = "use")]
    pub key_use: String,
    /// Key id.
    pub kid: String,
    /// X coordinate (base64url).
    pub x: String,
    /// Y coordinate (base64url).
    pub y: String,
}

/// The EC key set published at `.well-known/jwks.json`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct OidcJwks {
    /// Published keys.
    pub keys: Vec<OidcJwk>,
}

/// A minted OAuth token (Debug redacted).
#[derive(Clone, PartialEq, Serialize, Deserialize)]
pub struct OidcTokenResponse {
    /// The access token.
    pub access_token: String,
    /// Token type (`Bearer`).
    pub token_type: String,
    /// Lifetime (seconds).
    pub expires_in: i64,
    /// Granted scope, when different from the request.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub scope: Option<String>,
    /// Refresh token (never for the client credentials grant).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub refresh_token: Option<String>,
    /// ID token (authorization code grant with the `openid` scope).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub id_token: Option<String>,
}

impl std::fmt::Debug for OidcTokenResponse {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("OidcTokenResponse")
            .field("token_type", &self.token_type)
            .field("expires_in", &self.expires_in)
            .field("scope", &self.scope)
            .field("access_token", &"[REDACTED]")
            .field("refresh_token", &"[REDACTED]")
            .field("id_token", &"[REDACTED]")
            .finish()
    }
}

/// RFC 7662 token introspection result.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct OidcIntrospection {
    /// Whether the token is active.
    pub active: bool,
    /// Token type, for active tokens.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub token_type: Option<String>,
    /// Issuer, for active tokens.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub iss: Option<String>,
    /// Subject, for active tokens.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sub: Option<String>,
    /// Audience (string or array), for active tokens.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub aud: Option<Value>,
    /// Client id, for active tokens.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub client_id: Option<String>,
    /// Scope, for active tokens.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub scope: Option<String>,
    /// Expiry (unix seconds), for active tokens.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub exp: Option<i64>,
    /// Issued-at (unix seconds), for active tokens.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub iat: Option<i64>,
}

/// OpenID Connect userinfo claims.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct OidcUserInfo {
    /// Subject.
    pub sub: String,
    /// Email, when granted.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub email: Option<String>,
    /// Email verification, when granted.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub email_verified: Option<bool>,
    /// Name, when granted.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
}

/// Public OIDC protocol routes under `/{slug}`. No instance key is sent;
/// the token endpoint authenticates with `client_secret_post` form fields.
#[derive(Debug, Clone)]
pub struct AuthIdpOidc {
    client: AuthIdpClient,
}

impl AuthIdpOidc {
    /// Fetch the discovery document.
    pub async fn discovery(&self) -> Result<OidcDiscovery> {
        self.client
            .oidc_get(
                "auth_idp.oidc.discovery",
                "/.well-known/openid-configuration",
            )
            .await
    }

    /// Fetch the ES256 signing keys.
    pub async fn jwks(&self) -> Result<OidcJwks> {
        self.client
            .oidc_get("auth_idp.oidc.jwks", "/.well-known/jwks.json")
            .await
    }

    /// Exchange an authorization code (PKCE `code_verifier` optional).
    pub async fn token_authorization_code(
        &self,
        client_id: &str,
        client_secret: &str,
        code: &str,
        redirect_uri: &str,
        code_verifier: Option<&str>,
    ) -> Result<OidcTokenResponse> {
        validate_non_empty(client_id, "client_id")?;
        validate_non_empty(client_secret, "client_secret")?;
        validate_non_empty(code, "code")?;
        validate_non_empty(redirect_uri, "redirect_uri")?;
        if let Some(verifier) = code_verifier {
            validate_non_empty(verifier, "code_verifier")?;
        }
        let mut pairs = vec![
            ("grant_type", "authorization_code"),
            ("client_id", client_id.trim()),
            ("client_secret", client_secret.trim()),
            ("code", code.trim()),
            ("redirect_uri", redirect_uri.trim()),
        ];
        if let Some(verifier) = code_verifier {
            pairs.push(("code_verifier", verifier.trim()));
        }
        self.send_token(pairs).await
    }

    /// Refresh a token.
    pub async fn token_refresh(
        &self,
        client_id: &str,
        client_secret: &str,
        refresh_token: &str,
    ) -> Result<OidcTokenResponse> {
        validate_non_empty(client_id, "client_id")?;
        validate_non_empty(client_secret, "client_secret")?;
        validate_non_empty(refresh_token, "refresh_token")?;
        self.send_token(vec![
            ("grant_type", "refresh_token"),
            ("client_id", client_id.trim()),
            ("client_secret", client_secret.trim()),
            ("refresh_token", refresh_token.trim()),
        ])
        .await
    }

    /// Mint a client credentials token.
    pub async fn token_client_credentials(
        &self,
        client_id: &str,
        client_secret: &str,
        scope: Option<&str>,
    ) -> Result<OidcTokenResponse> {
        validate_non_empty(client_id, "client_id")?;
        validate_non_empty(client_secret, "client_secret")?;
        let mut pairs = vec![
            ("grant_type", "client_credentials"),
            ("client_id", client_id.trim()),
            ("client_secret", client_secret.trim()),
        ];
        if let Some(scope) = scope {
            validate_non_empty(scope, "scope")?;
            pairs.push(("scope", scope.trim()));
        }
        self.send_token(pairs).await
    }

    /// Revoke a token. The endpoint authenticates the OAuth client with
    /// `client_secret_post` form fields (RFC 7009 §2.1); unknown tokens also
    /// succeed with 200 and an empty body.
    pub async fn revoke(&self, client_id: &str, client_secret: &str, token: &str) -> Result<()> {
        validate_non_empty(client_id, "client_id")?;
        validate_non_empty(client_secret, "client_secret")?;
        validate_non_empty(token, "token")?;
        let request = self.client.oidc_form(
            "auth_idp.oidc.revoke",
            "/oauth/revoke",
            &[
                ("client_id", client_id.trim()),
                ("client_secret", client_secret.trim()),
                ("token", token.trim()),
            ],
        );
        self.client.send_empty(request).await
    }

    /// Introspect a token (RFC 7662). The endpoint authenticates the OAuth
    /// client with `client_secret_post` form fields.
    pub async fn introspect(
        &self,
        client_id: &str,
        client_secret: &str,
        token: &str,
    ) -> Result<OidcIntrospection> {
        validate_non_empty(client_id, "client_id")?;
        validate_non_empty(client_secret, "client_secret")?;
        validate_non_empty(token, "token")?;
        let request = self.client.oidc_form(
            "auth_idp.oidc.introspect",
            "/oauth/introspect",
            &[
                ("client_id", client_id.trim()),
                ("client_secret", client_secret.trim()),
                ("token", token.trim()),
            ],
        );
        self.client.send_typed(request).await
    }

    /// Read userinfo with an OAuth access token (not the instance key).
    pub async fn userinfo(&self, access_token: &str) -> Result<OidcUserInfo> {
        validate_non_empty(access_token, "access_token")?;
        let mut request = AuthIdpClient::public(Request {
            route_name: "auth_idp.oidc.userinfo".to_owned(),
            method: Method::Get,
            url: self.client.oidc_url("/oauth/userinfo"),
            headers: Vec::new(),
            body: None,
        });
        request.headers.push((
            "authorization".to_owned(),
            format!("Bearer {}", access_token.trim()),
        ));
        self.client.send_typed(request).await
    }

    async fn send_token(&self, pairs: Vec<(&str, &str)>) -> Result<OidcTokenResponse> {
        let request = self
            .client
            .oidc_form("auth_idp.oidc.token", "/oauth/token", &pairs);
        self.client.send_typed(request).await
    }
}

// ---------------------------------------------------------------------------
// Webhook signature verification
// ---------------------------------------------------------------------------

/// Verify a ts-auth webhook delivery signature: recompute
/// `sha256=` + lowercaseHex(HMAC-SHA256(secret, `timestamp + "." + body`))
/// over the raw body bytes and compare in constant time. Malformed inputs
/// return false; no replay window is enforced (see
/// [`verify_auth_idp_webhook_with_tolerance`]).
#[must_use]
pub fn verify_auth_idp_webhook(
    signing_secret: &str,
    timestamp: &str,
    signature: &str,
    payload: &[u8],
) -> bool {
    verify_signature(signing_secret, timestamp, signature, payload)
}

/// [`verify_auth_idp_webhook`] with a replay window: the `X-TsAuth-Timestamp`
/// header must be unix seconds within ±`tolerance` of `now`.
#[must_use]
pub fn verify_auth_idp_webhook_with_tolerance(
    signing_secret: &str,
    timestamp: &str,
    signature: &str,
    payload: &[u8],
    tolerance: Duration,
    now: SystemTime,
) -> bool {
    let Ok(parsed) = timestamp.parse::<u64>() else {
        return false;
    };
    // Millisecond resolution: a second-granularity diff would accept a
    // timestamp up to ~one second outside the tolerance window.
    let now = now
        .duration_since(UNIX_EPOCH)
        .map(|elapsed| elapsed.as_millis())
        .unwrap_or_default();
    if now.abs_diff(u128::from(parsed) * 1000) > tolerance.as_millis() {
        return false;
    }
    verify_signature(signing_secret, timestamp, signature, payload)
}

fn verify_signature(
    signing_secret: &str,
    timestamp: &str,
    signature: &str,
    payload: &[u8],
) -> bool {
    // An empty secret is never a configured signing secret; the other SDKs
    // treat it the same way (false), and no honest delivery can verify.
    if signing_secret.is_empty() {
        return false;
    }

    // The server emits unix seconds; a non-numeric timestamp header never
    // verifies in any variant. Sign and magnitude stay unchecked here so the
    // no-tolerance variant keeps accepting negative and far-future values.
    if !is_numeric_timestamp(timestamp) {
        return false;
    }
    let Some(hex) = signature.strip_prefix("sha256=") else {
        return false;
    };
    let mut expected = [0u8; 32];
    if !decode_hex_lower(hex, &mut expected) {
        return false;
    }
    let mut message = Vec::with_capacity(timestamp.len() + 1 + payload.len());
    message.extend_from_slice(timestamp.as_bytes());
    message.push(b'.');
    message.extend_from_slice(payload);
    constant_time_eq(&hmac_sha256(signing_secret.as_bytes(), &message), &expected)
}

/// HMAC-SHA256 (RFC 2104), written locally over the `sha2` primitive so the
/// verifier needs no dedicated HMAC dependency.
fn hmac_sha256(key: &[u8], message: &[u8]) -> [u8; 32] {
    const BLOCK_BYTES: usize = 64;
    let mut key_block = [0u8; BLOCK_BYTES];
    if key.len() > BLOCK_BYTES {
        key_block[..32].copy_from_slice(&Sha256::digest(key));
    } else {
        key_block[..key.len()].copy_from_slice(key);
    }
    let mut inner = Sha256::new();
    for byte in key_block {
        inner.update([byte ^ 0x36]);
    }
    inner.update(message);
    let inner = inner.finalize();
    let mut outer = Sha256::new();
    for byte in key_block {
        outer.update([byte ^ 0x5c]);
    }
    outer.update(inner);
    outer.finalize().into()
}

fn constant_time_eq(left: &[u8], right: &[u8]) -> bool {
    if left.len() != right.len() {
        return false;
    }
    let mut difference = 0u8;
    for (left, right) in left.iter().zip(right) {
        difference |= left ^ right;
    }
    difference == 0
}

fn decode_hex_lower(value: &str, out: &mut [u8]) -> bool {
    if value.len() != out.len() * 2 {
        return false;
    }
    for (index, [high, low]) in value.as_bytes().as_chunks::<2>().0.iter().enumerate() {
        match (hex_digit(*high), hex_digit(*low)) {
            (Some(high), Some(low)) => out[index] = high << 4 | low,
            _ => return false,
        }
    }
    true
}

fn hex_digit(byte: u8) -> Option<u8> {
    match byte {
        b'0'..=b'9' => Some(byte - b'0'),
        b'a'..=b'f' => Some(byte - b'a' + 10),
        _ => None,
    }
}

/// Whether the timestamp is numeric: an optional minus followed by one or
/// more ASCII digits.
fn is_numeric_timestamp(value: &str) -> bool {
    let digits = value.strip_prefix('-').unwrap_or(value);
    !digits.is_empty() && digits.bytes().all(|byte| byte.is_ascii_digit())
}

// ---------------------------------------------------------------------------
// Private helpers
// ---------------------------------------------------------------------------

#[derive(Debug, Deserialize)]
struct DataEnvelope<T> {
    data: Vec<T>,
}

#[derive(Debug, Deserialize)]
struct PasskeysEnvelope {
    passkeys: Vec<AuthIdpPasskey>,
}

#[derive(Debug, Deserialize)]
struct ClientsEnvelope {
    clients: Vec<AuthIdpOauthClient>,
}

#[derive(Serialize)]
struct EmptyObject {}

fn validation(code: &str, message: &str) -> Error {
    Error::new(ErrorKind::Validation, code, message)
}

fn validate_non_empty(value: &str, field: &str) -> Result<()> {
    if value.trim().is_empty() {
        return Err(validation(
            &format!("invalid_{field}"),
            &format!("{field} cannot be empty."),
        ));
    }
    Ok(())
}

fn validate_page_limit(limit: u32, max: u32, family: &str) -> Result<()> {
    if !(1..=max).contains(&limit) {
        return Err(validation(
            "invalid_limit",
            &format!("{family} limit must be between 1 and {max}."),
        ));
    }
    Ok(())
}

fn validate_slug(slug: &str) -> Result<()> {
    if slug.is_empty()
        || slug == "."
        || slug == ".."
        || slug
            .bytes()
            .any(|byte| byte == b'/' || byte.is_ascii_whitespace() || byte.is_ascii_control())
    {
        return Err(validation(
            "invalid_instance_slug",
            "The instance slug must be non-empty and cannot contain whitespace, control characters, slashes, or dot segments.",
        ));
    }
    Ok(())
}

fn validate_http_url(value: &str, field: &str) -> Result<()> {
    validate_non_empty(value, field)?;
    let parsed = reqwest::Url::parse(value.trim());
    let valid = parsed
        .as_ref()
        .is_ok_and(|url| matches!(url.scheme(), "http" | "https") && url.host_str().is_some());
    if !valid {
        return Err(validation(
            &format!("invalid_{field}"),
            &format!("{field} must be an absolute HTTP(S) URL."),
        ));
    }
    Ok(())
}

fn validate_webhook_url(url: &str) -> Result<()> {
    validate_non_empty(url, "url")?;
    let parsed = reqwest::Url::parse(url.trim())
        .map_err(|_| validation("invalid_url", "url must be an absolute HTTP(S) URL."))?;
    match parsed.scheme() {
        "https" => Ok(()),
        "http" => {
            let host = parsed
                .host_str()
                .unwrap_or_default()
                .trim_start_matches('[')
                .trim_end_matches(']');
            // The service's rule: exactly these loopback spellings, not every
            // loopback address (127.0.0.2 and *.localhost are rejected).
            let loopback =
                matches!(host, "127.0.0.1" | "::1") || host.eq_ignore_ascii_case("localhost");
            if loopback {
                Ok(())
            } else {
                Err(validation(
                    "invalid_url",
                    "url must use HTTPS, except loopback HTTP endpoints.",
                ))
            }
        }
        _ => Err(validation("invalid_url", "url must use HTTP(S).")),
    }
}

fn validate_template_name(name: &str) -> Result<()> {
    let trimmed = name.trim();
    if trimmed.is_empty()
        || trimmed.len() > MAX_JWT_TEMPLATE_NAME_BYTES
        || !trimmed
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_'))
    {
        return Err(validation(
            "invalid_jwt_template_name",
            "jwt template names must contain 1-48 ASCII letters, digits, hyphens, or underscores; the server lowercases them.",
        ));
    }
    Ok(())
}

/// Validate a path id and percent-encode it for the URL. Dot segments are
/// never valid ids, names, or domains; keeping one would let a caller mutate
/// the route after normalization.
fn path_id(value: &str, field: &str) -> Result<String> {
    validate_non_empty(value, field)?;
    let trimmed = value.trim();
    if trimmed == "." || trimmed == ".." {
        return Err(Error::new(
            ErrorKind::Validation,
            format!("invalid_{field}"),
            "Path segments cannot be \".\" or \"..\".",
        ));
    }
    Ok(percent_encode(trimmed))
}

fn percent_encode(value: &str) -> String {
    const HEX: &[u8; 16] = b"0123456789ABCDEF";
    let mut encoded = String::with_capacity(value.len());
    for byte in value.bytes() {
        if byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'.' | b'_' | b'~') {
            encoded.push(char::from(byte));
        } else {
            encoded.push('%');
            encoded.push(char::from(HEX[(byte >> 4) as usize]));
            encoded.push(char::from(HEX[(byte & 0x0f) as usize]));
        }
    }
    encoded
}

/// Encode `application/x-www-form-urlencoded` pairs (space as `%20`).
fn form_urlencoded(pairs: &[(&str, &str)]) -> Vec<u8> {
    pairs
        .iter()
        .map(|(name, value)| format!("{}={}", percent_encode(name), percent_encode(value)))
        .collect::<Vec<_>>()
        .join("&")
        .into_bytes()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn hex(bytes: &[u8]) -> String {
        bytes.iter().map(|byte| format!("{byte:02x}")).collect()
    }

    #[test]
    fn hmac_sha256_matches_rfc_4231_vectors() {
        let key = [0x0b; 20];
        assert_eq!(
            hex(&hmac_sha256(&key, b"Hi There")),
            "b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7"
        );
        assert_eq!(
            hex(&hmac_sha256(b"Jefe", b"what do ya want for nothing?")),
            "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843"
        );
        // Key longer than one block exercises the hashed-key branch.
        let long_key = [0xaa; 131];
        assert_eq!(
            hex(&hmac_sha256(
                &long_key,
                b"Test Using Larger Than Block-Size Key - Hash Key First"
            )),
            "60e431591ee0b67f0d8a26aacbf5b77f8e0bc6213728c5140546040f0ee37f54"
        );
    }

    #[test]
    fn webhook_verifier_accepts_only_intact_signatures() {
        let secret = "whsec_0123456789abcdef0123456789abcdef";
        let timestamp = "1758000000";
        let body = br#"{"id":"evt_1","type":"user.created","created_at":1758000000,"data":{}}"#;
        let message = [timestamp.as_bytes(), b".", body.as_slice()].concat();
        let signature = format!("sha256={}", hex(&hmac_sha256(secret.as_bytes(), &message)));

        assert!(verify_auth_idp_webhook(secret, timestamp, &signature, body));
        assert!(!verify_auth_idp_webhook(
            secret,
            timestamp,
            &signature,
            br#"{"id":"evt_1","type":"user.created","created_at":1758000001,"data":{}}"#
        ));
        assert!(!verify_auth_idp_webhook(
            "whsec_other",
            timestamp,
            &signature,
            body
        ));
        assert!(!verify_auth_idp_webhook(
            secret, timestamp, "deadbeef", body
        ));
        assert!(!verify_auth_idp_webhook(
            secret,
            timestamp,
            &format!("sha256={}", hex(&hmac_sha256(secret.as_bytes(), b"no"))),
            body
        ));
        // Uppercase hex is not the promised lowercase form.
        let upper = format!(
            "sha256={}",
            hex(&hmac_sha256(secret.as_bytes(), &message)).to_uppercase()
        );
        assert!(!verify_auth_idp_webhook(secret, timestamp, &upper, body));
    }

    #[test]
    fn webhook_tolerance_window_bounds_the_timestamp() {
        let secret = "whsec_0123456789abcdef0123456789abcdef";
        let timestamp = "1000";
        let body = b"payload";
        let message = b"1000.payload";
        let signature = format!("sha256={}", hex(&hmac_sha256(secret.as_bytes(), message)));
        let at = |seconds: u64| UNIX_EPOCH + Duration::from_secs(seconds);

        assert!(verify_auth_idp_webhook_with_tolerance(
            secret,
            timestamp,
            &signature,
            body,
            Duration::from_secs(300),
            at(1200)
        ));
        assert!(!verify_auth_idp_webhook_with_tolerance(
            secret,
            timestamp,
            &signature,
            body,
            Duration::from_secs(300),
            at(2000)
        ));
        assert!(!verify_auth_idp_webhook_with_tolerance(
            secret,
            "not-a-number",
            &signature,
            body,
            Duration::from_secs(300),
            at(1000)
        ));
        // Without tolerance the stale timestamp still verifies.
        assert!(verify_auth_idp_webhook(secret, timestamp, &signature, body));
    }
}
