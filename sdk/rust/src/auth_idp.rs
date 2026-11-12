//! ts-auth (hosted IdP) OIDC client.
//!
//! ts-auth is a separate service from TerraBase: it lives on its own origin
//! (`auth_base_url`) and its routes are not part of the TerraBase producer
//! contract. Obtain a client with [`crate::TerraScaleClient::auth_idp`].
//!
//! Wire shapes follow the ts-auth contract: public OIDC routes under `/{slug}`
//! with explicit public, Basic, or form client authentication.

use std::collections::HashSet;
use std::future::Future;
use std::sync::Arc;
use std::time::Duration;

use base64::Engine;
use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};

use crate::client::{SharedClient, SKIP_AUTH_MARKER, SKIP_SESSION_MARKER};
use crate::error::{Error, ErrorKind, Result};
use crate::transport::{Method, Request};

/// The one global ts-auth issuer of TerraBase platform tokens. Regional auth
/// endpoints never change it; there is no trailing slash.
pub const TERRASCALE_ISSUER: &str = "https://auth.terrascale.tech/terrascale";
/// The exact TerraBase resource audience; a trailing slash is a different audience.
pub const TERRABASE_AUDIENCE: &str = "https://api.terrascale.tech";
/// The only TerraBase scopes. Send a subset joined by single spaces.
pub const TERRABASE_SCOPES: [&str; 4] =
    ["databases:read", "databases:write", "sql:read", "sql:write"];

/// Per-request deadline for every ts-auth send; the shared pipeline has no
/// per-request timeout of its own.
const REQUEST_DEADLINE: Duration = Duration::from_secs(30);

// ---------------------------------------------------------------------------
// Client core
// ---------------------------------------------------------------------------

/// ts-auth OIDC client for one instance. Obtain with
/// [`crate::TerraScaleClient::auth_idp`]; it shares the caller's transport,
/// retry policy, and configuration pipeline and sends no TerraBase credential.
#[derive(Clone)]
pub struct AuthIdpClient {
    shared: SharedClient,
    /// `{auth_base_url}/{encoded-slug}`.
    oidc_prefix: Arc<str>,
    slug: String,
}

impl std::fmt::Debug for AuthIdpClient {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("AuthIdpClient")
            .field("oidc_prefix", &self.oidc_prefix)
            .field("slug", &self.slug)
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
        let encoded_slug = percent_encode(slug);
        Ok(Self {
            shared,
            oidc_prefix: format!("{base_url}/{encoded_slug}").into(),
            slug: slug.to_owned(),
        })
    }

    /// Public OIDC protocol routes (discovery, JWKS, token, revoke,
    /// userinfo).
    #[must_use]
    pub fn oidc(&self) -> AuthIdpOidc {
        AuthIdpOidc {
            client: self.clone(),
        }
    }

    /// Public OIDC route root: `{auth_base_url}/{slug}`.
    fn oidc_url(&self, path: &str) -> String {
        format!("{}{path}", self.oidc_prefix)
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
        bounded(self.shared.send_typed(request))
            .await
            .map_err(|mut error| {
                if error.kind == ErrorKind::InvalidResponse {
                    error.message = "OAuth response does not match the provider contract.".into();
                }
                error
            })
    }

    async fn send_empty(&self, request: Request) -> Result<()> {
        bounded(self.shared.send_response(request)).await?;
        Ok(())
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
    /// Granted scope; an empty string is a valid narrowed grant.
    pub scope: String,
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

/// Originating OAuth client authentication. Resource credentials have a separate type.
#[derive(Clone, Copy)]
pub enum OidcClientAuthentication<'a> {
    None {
        client_id: &'a str,
    },
    Basic {
        client_id: &'a str,
        client_secret: &'a str,
    },
    Form {
        client_id: &'a str,
        client_secret: &'a str,
    },
}

impl std::fmt::Debug for OidcClientAuthentication<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let (method, id) = match self {
            Self::None { client_id } => ("none", client_id),
            Self::Basic { client_id, .. } => ("client_secret_basic", client_id),
            Self::Form { client_id, .. } => ("client_secret_post", client_id),
        };
        f.debug_struct("OidcClientAuthentication")
            .field("method", &method)
            .field("client_id", id)
            .field("client_secret", &"[REDACTED]")
            .finish()
    }
}

impl<'a> OidcClientAuthentication<'a> {
    fn apply(self, pairs: &mut Vec<(&'a str, &'a str)>) -> Result<Option<String>> {
        let (id, secret) = match self {
            Self::None { client_id } => (client_id, None),
            Self::Basic {
                client_id,
                client_secret,
            }
            | Self::Form {
                client_id,
                client_secret,
            } => (client_id, Some(client_secret)),
        };
        validate_non_empty(id, "client_id")?;
        if let Some(secret) = secret {
            validate_non_empty(secret, "client_secret")?;
        }
        match self {
            Self::None { client_id } => {
                pairs.push(("client_id", client_id));
                Ok(None)
            }
            Self::Form {
                client_id,
                client_secret,
            } => {
                pairs.extend([("client_id", client_id), ("client_secret", client_secret)]);
                Ok(None)
            }
            Self::Basic {
                client_id,
                client_secret,
            } => Ok(Some(basic_authorization(client_id, client_secret))),
        }
    }
}

/// OpenID Connect userinfo claims.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct OidcUserInfo {
    /// Given name with the profile scope.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub given_name: Option<String>,
    /// Family name with the profile scope.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub family_name: Option<String>,
    /// HTTPS avatar with the profile scope.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub picture: Option<String>,
    /// Public metadata with the profile scope.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub public_metadata: Option<serde_json::Map<String, serde_json::Value>>,
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

/// OIDC routes with explicit originating-client authentication. No TerraBase
/// credential is sent.
#[derive(Debug, Clone)]
pub struct AuthIdpOidc {
    client: AuthIdpClient,
}

impl AuthIdpOidc {
    pub async fn discovery(&self) -> Result<OidcDiscovery> {
        self.client
            .oidc_get(
                "auth_idp.oidc.discovery",
                "/.well-known/openid-configuration",
            )
            .await
    }

    pub async fn jwks(&self) -> Result<OidcJwks> {
        self.client
            .oidc_get("auth_idp.oidc.jwks", "/.well-known/jwks.json")
            .await
    }

    /// Redeem a code. Omit resource to inherit, or repeat its exact stored value.
    pub async fn token_authorization_code(
        &self,
        authentication: OidcClientAuthentication<'_>,
        code: &str,
        redirect_uri: &str,
        code_verifier: Option<&str>,
        resource: Option<&str>,
    ) -> Result<OidcTokenResponse> {
        validate_non_empty(code, "code")?;
        validate_non_empty(redirect_uri, "redirect_uri")?;
        let mut pairs = vec![
            ("grant_type", "authorization_code"),
            ("code", code),
            ("redirect_uri", redirect_uri),
        ];
        if let Some(verifier) = code_verifier {
            validate_non_empty(verifier, "code_verifier")?;
            pairs.push(("code_verifier", verifier));
        }
        append_resource(&mut pairs, resource)?;
        self.send_token(authentication, pairs).await
    }

    /// Absence of scope inherits; an explicit subset (including empty) narrows.
    pub async fn token_refresh(
        &self,
        authentication: OidcClientAuthentication<'_>,
        refresh_token: &str,
        resource: Option<&str>,
        scope: Option<&str>,
    ) -> Result<OidcTokenResponse> {
        validate_non_empty(refresh_token, "refresh_token")?;
        let mut pairs = vec![
            ("grant_type", "refresh_token"),
            ("refresh_token", refresh_token),
        ];
        append_resource(&mut pairs, resource)?;
        append_scope(&mut pairs, scope)?;
        self.send_token(authentication, pairs).await
    }

    /// Confidential originating clients require explicit resource and scopes.
    pub async fn token_client_credentials(
        &self,
        authentication: OidcClientAuthentication<'_>,
        resource: &str,
        scope: &str,
    ) -> Result<OidcTokenResponse> {
        if matches!(authentication, OidcClientAuthentication::None { .. }) {
            return Err(validation(
                "confidential_client_required",
                "Client credentials requires a confidential originating client.",
            ));
        }
        validate_non_empty(scope, "scope")?;
        let mut pairs = vec![("grant_type", "client_credentials")];
        append_resource(&mut pairs, Some(resource))?;
        append_scope(&mut pairs, Some(scope))?;
        self.send_token(authentication, pairs).await
    }

    /// Redeem a device code once. The provider enforces its polling interval.
    pub async fn token_device_code(
        &self,
        authentication: OidcClientAuthentication<'_>,
        device_code: &str,
        resource: Option<&str>,
    ) -> Result<OidcTokenResponse> {
        validate_non_empty(device_code, "device_code")?;
        let mut pairs = vec![
            ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
            ("device_code", device_code),
        ];
        append_resource(&mut pairs, resource)?;
        self.send_token(authentication, pairs).await
    }

    /// Revoke using originating-client credentials. Resource credentials cannot be supplied.
    pub async fn revoke(
        &self,
        authentication: OidcClientAuthentication<'_>,
        token: &str,
    ) -> Result<()> {
        validate_non_empty(token, "token")?;
        let request = self.originating_form(
            "auth_idp.oidc.revoke",
            "/oauth/revoke",
            authentication,
            vec![("token", token)],
        )?;
        self.client.send_empty(request).await
    }

    /// Read userinfo with an OAuth access token, never an instance key.
    pub async fn userinfo(&self, access_token: &str) -> Result<OidcUserInfo> {
        validate_non_empty(access_token, "access_token")?;
        let mut request = AuthIdpClient::public(Request {
            route_name: "auth_idp.oidc.userinfo".to_owned(),
            method: Method::Get,
            url: self.client.oidc_url("/oauth/userinfo"),
            headers: Vec::new(),
            body: None,
        });
        request
            .headers
            .push(("authorization".to_owned(), format!("Bearer {access_token}")));
        self.client.send_typed(request).await
    }

    fn originating_form<'a>(
        &self,
        route: &str,
        path: &str,
        authentication: OidcClientAuthentication<'a>,
        mut pairs: Vec<(&'a str, &'a str)>,
    ) -> Result<Request> {
        let authorization = authentication.apply(&mut pairs)?;
        let mut request = self.client.oidc_form(route, path, &pairs);
        if let Some(authorization) = authorization {
            request
                .headers
                .push(("authorization".into(), authorization));
        }
        Ok(request)
    }

    async fn send_token<'a>(
        &self,
        authentication: OidcClientAuthentication<'a>,
        pairs: Vec<(&'a str, &'a str)>,
    ) -> Result<OidcTokenResponse> {
        let request =
            self.originating_form("auth_idp.oidc.token", "/oauth/token", authentication, pairs)?;
        self.client.send_typed(request).await
    }
}

// ---------------------------------------------------------------------------
// Private helpers
// ---------------------------------------------------------------------------

pub(crate) fn validation(code: &str, message: &str) -> Error {
    Error::new(ErrorKind::Validation, code, message)
}

fn basic_authorization(client_id: &str, secret: &str) -> String {
    let components = format!("{}:{}", percent_encode(client_id), percent_encode(secret));
    format!(
        "Basic {}",
        base64::engine::general_purpose::STANDARD.encode(components)
    )
}

fn valid_scope_token(scope: &str) -> bool {
    !scope.is_empty()
        && scope.len() <= 256
        && scope
            .bytes()
            .all(|byte| matches!(byte, 0x21 | 0x23..=0x5b | 0x5d..=0x7e))
}

fn valid_scope_string(scope: &str) -> bool {
    if scope.is_empty() {
        return true;
    }
    let scopes: Vec<&str> = scope.split(' ').collect();
    scopes.len() <= 100
        && scopes.iter().all(|scope| valid_scope_token(scope))
        && scopes.iter().collect::<HashSet<_>>().len() == scopes.len()
}

/// Match the provider's exact ASCII HTTPS resource grammar without rewriting it.
fn valid_resource(value: &str) -> bool {
    let Some(remainder) = value.strip_prefix("https://") else {
        return false;
    };
    let authority = remainder.split(['/', '?', '#']).next().unwrap_or_default();
    if value.len() > 2048
        || authority.is_empty()
        || authority.contains('@')
        || remainder[authority.len()..].contains(['[', ']'])
        || value.bytes().any(|byte| {
            !byte.is_ascii_alphanumeric()
                && !matches!(
                    byte,
                    b'-' | b'.'
                        | b'_'
                        | b'~'
                        | b':'
                        | b'/'
                        | b'?'
                        | b'['
                        | b']'
                        | b'@'
                        | b'!'
                        | b'$'
                        | b'&'
                        | b'\''
                        | b'('
                        | b')'
                        | b'*'
                        | b'+'
                        | b','
                        | b';'
                        | b'='
                        | b'%'
                )
        })
        || value.as_bytes().iter().enumerate().any(|(index, byte)| {
            *byte == b'%'
                && !value
                    .as_bytes()
                    .get(index + 1..index + 3)
                    .is_some_and(|digits| digits.iter().all(u8::is_ascii_hexdigit))
        })
    {
        return false;
    }
    reqwest::Url::parse(value).is_ok_and(|url| {
        url.scheme() == "https"
            && url.host_str().is_some()
            && url.username().is_empty()
            && url.password().is_none()
            && url.fragment().is_none()
    })
}

fn validate_resource(resource: &str) -> Result<()> {
    if !valid_resource(resource) {
        return Err(validation(
            "invalid_resource",
            "Resource must be one exact absolute HTTPS URI without credentials or a fragment.",
        ));
    }
    Ok(())
}

fn append_resource<'a>(
    pairs: &mut Vec<(&'a str, &'a str)>,
    resource: Option<&'a str>,
) -> Result<()> {
    if let Some(resource) = resource {
        validate_resource(resource)?;
        pairs.push(("resource", resource));
    }
    Ok(())
}

fn append_scope<'a>(pairs: &mut Vec<(&'a str, &'a str)>, scope: Option<&'a str>) -> Result<()> {
    if let Some(scope) = scope {
        if !valid_scope_string(scope) {
            return Err(validation(
                "invalid_scope",
                "Scope must contain unique bounded scope tokens.",
            ));
        }
        pairs.push(("scope", scope));
    }
    Ok(())
}

pub(crate) fn validate_non_empty(value: &str, field: &str) -> Result<()> {
    if value.trim().is_empty() {
        return Err(validation(
            &format!("invalid_{field}"),
            &format!("{field} cannot be empty."),
        ));
    }
    Ok(())
}

pub(crate) fn validate_page_limit(limit: u32, max: u32, family: &str) -> Result<()> {
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

/// Validate a path id and percent-encode it for the URL. Dot segments are
/// never valid ids, names, or domains; keeping one would let a caller mutate
/// the route after normalization.
pub(crate) fn path_id(value: &str, field: &str) -> Result<String> {
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

pub(crate) fn percent_encode(value: &str) -> String {
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
