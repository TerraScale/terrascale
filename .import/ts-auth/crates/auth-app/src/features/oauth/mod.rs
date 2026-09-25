//! OAuth 2.0 and OpenID Connect provider endpoints.
//!
//! The authorization server deliberately keeps redirect URI matching exact,
//! consumes codes and refresh tokens through store-side conditional writes, and
//! signs all access and ID tokens with the instance's current ES256 key.

mod anonymous;
pub(crate) mod cors;
mod device;
mod rate_limit;

use std::{
    collections::{HashMap, HashSet},
    time::{SystemTime, UNIX_EPOCH},
};

use auth_contracts::models::{AuthorizationCode, OAuthClient, RefreshToken};
use auth_security::{
    jwt::{self, JwtKey},
    tokens,
};
use auth_store::{AuthStore, Predicate};
use axum::{
    Form, Json, Router,
    extract::{Path, Query, RawQuery, State},
    http::{HeaderMap, HeaderValue, StatusCode, header},
    response::{Html, IntoResponse, Redirect, Response},
    routing::{delete, get, post},
};
use base64::{Engine, engine::general_purpose::STANDARD};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use url::{Host, Url};
use utoipa::openapi::security::{HttpAuthScheme, HttpBuilder, SecurityScheme};
use utoipa::{OpenApi, ToSchema};

use crate::{AppState, features::management};
use auth_contracts::{AppError, AppResult};

const ACCESS_TOKEN_TTL: i64 = 60 * 60;
const REFRESH_TOKEN_TTL: i64 = 30 * 24 * 60 * 60;
const AUTHORIZATION_CODE_TTL: i64 = 60;
const SUPPORTED_SCOPES: &[&str] = &["openid", "profile", "email"];
const SESSION_COOKIE: &str = "tsauth_session";

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum AuthorizationPrompt {
    Default,
    None,
    Login,
    Consent,
    SelectAccount,
    InteractionRequired,
}

#[derive(OpenApi)]
#[openapi(
    paths(
        discovery, jwks, authorize, token, userinfo, introspect, revoke, logout,
        create_client, list_clients, update_client, delete_client
    ),
    components(schemas(
        CreateClientRequest, UpdateClientRequest, DiscoveryDocument, JwksDocument, OAuthTokenRequest, OAuthTokenResponse,
        OAuthErrorResponse, IntrospectionRequest, IntrospectionResponse, RevocationRequest,
        UserInfoResponse, ApiErrorResponse
    )),
    tags((name = "OAuth", description = "OAuth 2.0 and OpenID Connect endpoints"))
)]
pub struct ApiDoc;

#[derive(Debug, Serialize, ToSchema)]
struct DiscoveryDocument {
    issuer: String,
    authorization_endpoint: String,
    token_endpoint: String,
    device_authorization_endpoint: String,
    userinfo_endpoint: String,
    jwks_uri: String,
    revocation_endpoint: String,
    introspection_endpoint: String,
    end_session_endpoint: String,
    response_types_supported: Vec<String>,
    grant_types_supported: Vec<String>,
    code_challenge_methods_supported: Vec<String>,
    subject_types_supported: Vec<String>,
    id_token_signing_alg_values_supported: Vec<String>,
    token_endpoint_auth_methods_supported: Vec<String>,
    scopes_supported: Vec<String>,
}

#[derive(Debug, Serialize, ToSchema)]
struct JwksDocument {
    keys: Vec<Value>,
}

#[derive(Debug, Deserialize, ToSchema)]
// Utoipa uses this form model to emit OpenAPI; handlers parse a map so both
// Basic and form client authentication share one validation path.
#[allow(dead_code)]
struct OAuthTokenRequest {
    grant_type: Option<String>,
    code: Option<String>,
    redirect_uri: Option<String>,
    code_verifier: Option<String>,
    refresh_token: Option<String>,
    device_code: Option<String>,
    scope: Option<String>,
    client_id: Option<String>,
    client_secret: Option<String>,
}

#[derive(Debug, Serialize, ToSchema)]
struct OAuthTokenResponse {
    access_token: String,
    token_type: String,
    expires_in: i64,
    scope: String,
    refresh_token: Option<String>,
    id_token: Option<String>,
}

#[derive(Debug, Serialize, ToSchema)]
struct OAuthErrorResponse {
    error: String,
    error_description: String,
}

#[derive(Debug, Deserialize, ToSchema)]
// Utoipa uses this form model to emit OpenAPI; handlers parse a map so both
// Basic and form client authentication share one validation path.
#[allow(dead_code)]
struct IntrospectionRequest {
    token: String,
    token_type_hint: Option<String>,
    client_id: Option<String>,
    client_secret: Option<String>,
}

#[derive(Debug, Serialize, ToSchema)]
struct IntrospectionResponse {
    active: bool,
    token_type: Option<String>,
    iss: Option<String>,
    sub: Option<String>,
    aud: Option<String>,
    client_id: Option<String>,
    scope: Option<String>,
    exp: Option<i64>,
    iat: Option<i64>,
}

#[derive(Debug, Deserialize, ToSchema)]
// Utoipa uses this form model to emit OpenAPI; handlers parse a map so both
// Basic and form client authentication share one validation path.
#[allow(dead_code)]
struct RevocationRequest {
    token: String,
    token_type_hint: Option<String>,
    client_id: Option<String>,
    client_secret: Option<String>,
}

#[derive(Debug, Serialize, ToSchema)]
struct UserInfoResponse {
    sub: String,
    is_anonymous: bool,
    email: Option<String>,
    email_verified: Option<bool>,
    name: Option<String>,
}

#[derive(Debug, Serialize, ToSchema)]
struct ApiErrorResponse {
    error: ApiErrorBody,
}

#[derive(Debug, Serialize, ToSchema)]
struct ApiErrorBody {
    code: String,
    message: String,
}

pub fn router() -> Router<AppState> {
    Router::new()
        .merge(device::router())
        .merge(anonymous::router())
        .route("/{slug}/.well-known/openid-configuration", get(discovery))
        .route("/{slug}/.well-known/jwks.json", get(jwks))
        .route("/{slug}/oauth/authorize", get(authorize))
        .route("/{slug}/oauth/token", post(token))
        .route("/{slug}/oauth/userinfo", get(userinfo))
        .route("/{slug}/oauth/introspect", post(introspect))
        .route("/{slug}/oauth/revoke", post(revoke))
        .route("/{slug}/oauth/logout", get(logout))
        .route(
            "/v1/{slug}/oauth-clients",
            post(create_client).get(list_clients),
        )
        .route(
            "/v1/{slug}/oauth-clients/{id}",
            delete(delete_client).patch(update_client),
        )
}

pub fn openapi() -> utoipa::openapi::OpenApi {
    let mut document = ApiDoc::openapi();
    document.merge(device::openapi());
    document.merge(anonymous::openapi());
    document.components.get_or_insert_with(Default::default).add_security_scheme(
        "clientBasic",
        SecurityScheme::Http(
            HttpBuilder::new()
                .scheme(HttpAuthScheme::Basic)
                .description(Some(
                    "OAuth client authentication may use HTTP Basic or client_id/client_secret form fields.",
                ))
                .build(),
        ),
    );
    document
}

#[utoipa::path(
    get,
    path = "/{slug}/.well-known/openid-configuration",
    params(("slug" = String, Path, description = "Instance slug")),
    responses((status = 200, description = "OpenID Provider metadata", body = DiscoveryDocument), (status = 404, description = "Instance not found", body = ApiErrorResponse)),
    tag = "OAuth"
)]
async fn discovery(State(state): State<AppState>, Path(slug): Path<String>) -> AppResult<Response> {
    let Some(instance) = state
        .store
        .find_instance_by_slug(&slug)
        .await
        .map_err(internal)?
    else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "not_found",
            "no such instance",
        ));
    };
    let issuer = issuer(&state, &instance.slug);
    let metadata = json!({
        "issuer": issuer,
        "authorization_endpoint": format!("{issuer}/oauth/authorize"),
        "token_endpoint": format!("{issuer}/oauth/token"),
        "device_authorization_endpoint": format!("{issuer}/oauth/device_authorization"),
        "userinfo_endpoint": format!("{issuer}/oauth/userinfo"),
        "jwks_uri": format!("{issuer}/.well-known/jwks.json"),
        "revocation_endpoint": format!("{issuer}/oauth/revoke"),
        "introspection_endpoint": format!("{issuer}/oauth/introspect"),
        "end_session_endpoint": format!("{issuer}/oauth/logout"),
        "response_types_supported": ["code"],
        "grant_types_supported": ["authorization_code", "refresh_token", "client_credentials", device::GRANT_TYPE],
        "code_challenge_methods_supported": ["S256"],
        "subject_types_supported": ["public"],
        "id_token_signing_alg_values_supported": ["ES256"],
        "token_endpoint_auth_methods_supported": ["client_secret_basic", "client_secret_post", "none"],
        "scopes_supported": SUPPORTED_SCOPES,
    });
    Ok((
        [(header::CACHE_CONTROL, "public, max-age=300")],
        Json(metadata),
    )
        .into_response())
}

#[utoipa::path(
    get,
    path = "/{slug}/.well-known/jwks.json",
    params(("slug" = String, Path, description = "Instance slug")),
    responses((status = 200, description = "Public signing keys", body = JwksDocument), (status = 404, description = "Instance not found", body = ApiErrorResponse)),
    tag = "OAuth"
)]
async fn jwks(State(state): State<AppState>, Path(slug): Path<String>) -> AppResult<Response> {
    let Some(instance) = state
        .store
        .find_instance_by_slug(&slug)
        .await
        .map_err(internal)?
    else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "not_found",
            "no such instance",
        ));
    };
    let keys = state
        .key_ring
        .get_active(&instance.id)
        .await
        .map_err(internal)?;
    let values = keys.iter().map(JwtKey::public_jwk).collect::<Vec<_>>();
    Ok((
        [(header::CACHE_CONTROL, "public, max-age=300")],
        Json(json!({ "keys": values })),
    )
        .into_response())
}

#[utoipa::path(
    get,
    path = "/{slug}/oauth/authorize",
    params(("slug" = String, Path), ("client_id" = String, Query), ("redirect_uri" = String, Query), ("response_type" = String, Query), ("scope" = Option<String>, Query), ("state" = Option<String>, Query), ("code_challenge" = Option<String>, Query), ("code_challenge_method" = Option<String>, Query)),
    responses((status = 303, description = "Redirect to the client or hosted sign-in"), (status = 400, description = "Invalid authorization request", body = ApiErrorResponse)),
    tag = "OAuth"
)]
async fn authorize(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    Query(query): Query<HashMap<String, String>>,
    RawQuery(raw_query): RawQuery,
    headers: HeaderMap,
) -> AppResult<Response> {
    let Some(instance) = state
        .store
        .find_instance_by_slug(&slug)
        .await
        .map_err(internal)?
    else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "not_found",
            "no such instance",
        ));
    };
    let client_id = query
        .get("client_id")
        .map(String::as_str)
        .unwrap_or_default();
    let redirect_uri = query
        .get("redirect_uri")
        .map(String::as_str)
        .unwrap_or_default();
    let state_value = query.get("state").map(String::as_str).unwrap_or_default();
    if client_id.is_empty() {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "client_id is required",
        ));
    }
    let Some(client) = state
        .store
        .find_oauth_client(&instance.id, client_id)
        .await
        .map_err(internal)?
    else {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "unknown client_id",
        ));
    };
    if redirect_uri.is_empty()
        || !client
            .redirect_uris
            .iter()
            .any(|registered| registered == redirect_uri)
    {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "redirect_uri does not exactly match a registered redirect uri",
        ));
    }
    if query.get("response_type").map(String::as_str) != Some("code") {
        return Ok(redirect_error(
            redirect_uri,
            state_value,
            "unsupported_response_type",
            "only response_type=code is supported",
        ));
    }

    let scopes = match query
        .get("scope")
        .map(String::as_str)
        .filter(|value| !value.is_empty())
    {
        Some(value) => value.split_ascii_whitespace().collect::<Vec<_>>(),
        None => vec!["openid"],
    };
    if scopes.iter().any(|scope| !SUPPORTED_SCOPES.contains(scope)) {
        return Ok(redirect_error(
            redirect_uri,
            state_value,
            "invalid_scope",
            "supported scopes: openid profile email",
        ));
    }

    let challenge = query
        .get("code_challenge")
        .map(String::as_str)
        .unwrap_or_default();
    let method = query
        .get("code_challenge_method")
        .map(String::as_str)
        .unwrap_or_default();
    if challenge.is_empty() && !method.is_empty() {
        return Ok(redirect_error(
            redirect_uri,
            state_value,
            "invalid_request",
            "code_challenge is required when code_challenge_method is present",
        ));
    }
    if !challenge.is_empty()
        && (method != "S256" || challenge.len() != 43 || !is_base64url(challenge))
    {
        return Ok(redirect_error(
            redirect_uri,
            state_value,
            "invalid_request",
            "only a valid S256 code challenge is supported",
        ));
    }
    if client.r#public && challenge.is_empty() {
        return Ok(redirect_error(
            redirect_uri,
            state_value,
            "invalid_request",
            "PKCE with S256 is required for public clients",
        ));
    }

    let parsed_prompt = if raw_query
        .as_deref()
        .is_some_and(|value| has_duplicate_query_parameter(value, "prompt"))
    {
        Err(())
    } else {
        parse_authorization_prompt(query.get("prompt").map(String::as_str))
    };
    let prompt = match parsed_prompt {
        Ok(prompt) => prompt,
        Err(()) => {
            return Ok(redirect_error(
                redirect_uri,
                state_value,
                "invalid_request",
                "prompt is empty, duplicated, unknown, or combines none with another value",
            ));
        }
    };
    let unsupported_prompt_error = match prompt {
        AuthorizationPrompt::Login => Some(("login_required", "interactive login is required")),
        AuthorizationPrompt::Consent => Some(("consent_required", "user consent is required")),
        AuthorizationPrompt::SelectAccount => Some((
            "account_selection_required",
            "account selection is required",
        )),
        AuthorizationPrompt::InteractionRequired => {
            Some(("interaction_required", "user interaction is required"))
        }
        AuthorizationPrompt::Default | AuthorizationPrompt::None => None,
    };
    if let Some((error, description)) = unsupported_prompt_error {
        return Ok(redirect_error(
            redirect_uri,
            state_value,
            error,
            description,
        ));
    }

    let cookie = cookie_value(&headers, SESSION_COOKIE);
    let resolved = match cookie {
        Some(value) => crate::features::users::resolve_session(&state, &instance.id, value).await?,
        None => None,
    };
    let Some((session, user)) = resolved else {
        if prompt == AuthorizationPrompt::None {
            return Ok(redirect_error(
                redirect_uri,
                state_value,
                "login_required",
                "no active session",
            ));
        }
        let authorize_url = format!(
            "/{}/oauth/authorize?{}",
            instance.slug,
            query_string(&query)
        );
        return Ok(Redirect::to(&format!(
            "/ui/{}/sign-in?next={}",
            instance.slug,
            urlencoding_fallback(&authorize_url)
        ))
        .into_response());
    };

    // A random nonce bound to this one-time state is sent upstream to the relying
    // party and checked against the returned ID token in the token grant.
    let nonce = query
        .get("nonce")
        .cloned()
        .filter(|value| !value.is_empty());
    let code = tokens::random_token(48);
    let code_record = AuthorizationCode {
        id: format!("oac:{}:{}", session.id, tokens::random_token(12)),
        instance_id: instance.id.clone(),
        client_id: client.client_id.clone(),
        user_id: user.id.clone(),
        user_auth_epoch: user.auth_epoch,
        code_key: tokens::sha256_hex(&code),
        redirect_uri: redirect_uri.to_owned(),
        scopes: scopes.into_iter().map(str::to_owned).collect(),
        code_challenge: (!challenge.is_empty()).then(|| challenge.to_owned()),
        code_challenge_method: (!challenge.is_empty()).then(|| "S256".to_owned()),
        nonce,
        expires_at: now().saturating_add(AUTHORIZATION_CODE_TTL),
        consumed: false,
    };
    state
        .store
        .create_authorization_code(&code_record)
        .await
        .map_err(internal)?;
    audit(
        &state,
        &instance.id,
        Some(&user.id),
        "oauth.code_issued",
        json!({
            "client_id": client.client_id,
            "scope": code_record.scopes.join(" "),
        }),
    )
    .await?;

    let mut callback = Url::parse(redirect_uri).map_err(|_| AppError::internal())?;
    callback.query_pairs_mut().append_pair("code", &code);
    if !state_value.is_empty() {
        callback.query_pairs_mut().append_pair("state", state_value);
    }
    Ok(Redirect::to(callback.as_str()).into_response())
}

fn parse_authorization_prompt(value: Option<&str>) -> Result<AuthorizationPrompt, ()> {
    let Some(value) = value else {
        return Ok(AuthorizationPrompt::Default);
    };
    let values = value.split_ascii_whitespace().collect::<Vec<_>>();
    if values.is_empty() {
        return Err(());
    }
    let mut seen = HashSet::new();
    for value in &values {
        if !seen.insert(*value)
            || !matches!(*value, "none" | "login" | "consent" | "select_account")
        {
            return Err(());
        }
    }
    if seen.contains("none") {
        return (seen.len() == 1)
            .then_some(AuthorizationPrompt::None)
            .ok_or(());
    }
    if seen.contains("login") && seen.contains("consent") {
        return Ok(AuthorizationPrompt::InteractionRequired);
    }
    if seen.contains("login") {
        return Ok(AuthorizationPrompt::Login);
    }
    if seen.contains("consent") {
        return Ok(AuthorizationPrompt::Consent);
    }
    if seen.contains("select_account") {
        return Ok(AuthorizationPrompt::SelectAccount);
    }
    Err(())
}

fn has_duplicate_query_parameter(query: &str, name: &str) -> bool {
    let mut values = url::form_urlencoded::parse(query.as_bytes()).filter(|(key, _)| key == name);
    values.next().is_some() && values.next().is_some()
}

#[utoipa::path(
    post,
    path = "/{slug}/oauth/token",
    params(("slug" = String, Path)),
    request_body(content = OAuthTokenRequest, content_type = "application/x-www-form-urlencoded"),
    responses((status = 200, description = "OAuth token response", body = OAuthTokenResponse), (status = 400, description = "OAuth token error", body = OAuthErrorResponse), (status = 401, description = "Client authentication failed", body = OAuthErrorResponse)),
    security(("clientBasic" = []), ()),
    tag = "OAuth"
)]
async fn token(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Form(form): Form<HashMap<String, String>>,
) -> AppResult<Response> {
    let Some(instance) = state
        .store
        .find_instance_by_slug(&slug)
        .await
        .map_err(internal)?
    else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "not_found",
            "no such instance",
        ));
    };
    let client = match authenticate_client(&state.store, &instance.id, &headers, &form).await {
        Ok(client) => client,
        Err(error) => return Ok(client_authentication_error_response(error)),
    };
    let grant = form
        .get("grant_type")
        .map(String::as_str)
        .unwrap_or_default();
    let response = match grant {
        "authorization_code" => grant_authorization_code(&state, &instance, &client, &form).await?,
        "refresh_token" => grant_refresh_token(&state, &instance, &client, &form).await?,
        "client_credentials" => grant_client_credentials(&state, &instance, &client, &form).await?,
        device::GRANT_TYPE => device::grant(&state, &instance, &client, &form).await?,
        "" => token_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "grant_type is required",
            false,
        ),
        other => token_error(
            StatusCode::BAD_REQUEST,
            "unsupported_grant_type",
            &format!("grant_type '{other}' is not supported"),
            false,
        ),
    };
    Ok(no_store(response))
}

async fn grant_authorization_code(
    state: &AppState,
    instance: &auth_contracts::models::Instance,
    client: &OAuthClient,
    form: &HashMap<String, String>,
) -> AppResult<Response> {
    let raw = form.get("code").map(String::as_str).unwrap_or_default();
    if raw.is_empty() {
        return Ok(token_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "code is required",
            false,
        ));
    }
    let key = tokens::sha256_hex(raw);
    let Some(record) = state
        .store
        .find_authorization_code(&key)
        .await
        .map_err(internal)?
    else {
        return Ok(token_error(
            StatusCode::BAD_REQUEST,
            "invalid_grant",
            "invalid authorization code",
            false,
        ));
    };
    if record.instance_id != instance.id || record.client_id != client.client_id {
        return Ok(token_error(
            StatusCode::BAD_REQUEST,
            "invalid_grant",
            "invalid authorization code",
            false,
        ));
    }
    if record.consumed {
        revoke_user_refresh_families(
            &state.store,
            &record.instance_id,
            &record.client_id,
            &record.user_id,
        )
        .await?;
        return Ok(token_error(
            StatusCode::BAD_REQUEST,
            "invalid_grant",
            "authorization code already used",
            false,
        ));
    }
    if record.expires_at <= now() {
        return Ok(token_error(
            StatusCode::BAD_REQUEST,
            "invalid_grant",
            "authorization code expired",
            false,
        ));
    }
    if form.get("redirect_uri").map(String::as_str) != Some(record.redirect_uri.as_str()) {
        return Ok(token_error(
            StatusCode::BAD_REQUEST,
            "invalid_grant",
            "redirect_uri does not match the authorization request",
            false,
        ));
    }
    match record.code_challenge.as_deref() {
        None if client.r#public => {
            return Ok(token_error(
                StatusCode::BAD_REQUEST,
                "invalid_grant",
                "PKCE is required for public clients",
                false,
            ));
        }
        Some(challenge) => {
            let verifier = form
                .get("code_verifier")
                .map(String::as_str)
                .unwrap_or_default();
            if !(43..=128).contains(&verifier.len()) || !is_pkce_verifier(verifier) {
                return Ok(token_error(
                    StatusCode::BAD_REQUEST,
                    "invalid_grant",
                    "code_verifier is invalid",
                    false,
                ));
            }
            if record.code_challenge_method.as_deref() != Some("S256") {
                return Ok(token_error(
                    StatusCode::BAD_REQUEST,
                    "invalid_grant",
                    "unsupported code challenge method",
                    false,
                ));
            }
            let derived = base64::engine::general_purpose::URL_SAFE_NO_PAD
                .encode(Sha256::digest(verifier.as_bytes()));
            if !tokens::fixed_time_equals(&derived, challenge) {
                return Ok(token_error(
                    StatusCode::BAD_REQUEST,
                    "invalid_grant",
                    "code_verifier does not match the code challenge",
                    false,
                ));
            }
        }
        None => {}
    }
    let consumed = state
        .store
        .consume_authorization_code(&key, &instance.id, &client.client_id, now())
        .await
        .map_err(internal)?;
    let record = match consumed {
        auth_store::CodeConsumeOutcome::Consumed(value) => value,
        auth_store::CodeConsumeOutcome::Used(value) => {
            revoke_user_refresh_families(
                &state.store,
                &value.instance_id,
                &value.client_id,
                &value.user_id,
            )
            .await?;
            return Ok(token_error(
                StatusCode::BAD_REQUEST,
                "invalid_grant",
                "authorization code already used",
                false,
            ));
        }
        auth_store::CodeConsumeOutcome::Expired => {
            return Ok(token_error(
                StatusCode::BAD_REQUEST,
                "invalid_grant",
                "authorization code expired",
                false,
            ));
        }
        auth_store::CodeConsumeOutcome::Missing => {
            return Ok(token_error(
                StatusCode::BAD_REQUEST,
                "invalid_grant",
                "invalid authorization code",
                false,
            ));
        }
    };
    let Some(user) = state
        .store
        .get_user(&record.user_id)
        .await
        .map_err(internal)?
    else {
        return Ok(token_error(
            StatusCode::BAD_REQUEST,
            "invalid_grant",
            "user not found",
            false,
        ));
    };
    if user.instance_id != instance.id || user.deleted || user.banned {
        return Ok(token_error(
            StatusCode::BAD_REQUEST,
            "invalid_grant",
            "user is no longer active",
            false,
        ));
    }
    if user.auth_epoch != record.user_auth_epoch {
        return Ok(token_error(
            StatusCode::BAD_REQUEST,
            "invalid_grant",
            "authorization code is no longer active",
            false,
        ));
    }
    if !state
        .store
        .scim_auth_eligible(&user)
        .await
        .map_err(internal)?
    {
        return Ok(token_error(
            StatusCode::BAD_REQUEST,
            "invalid_grant",
            "user is no longer active",
            false,
        ));
    }
    let session_id = record.id.split(':').nth(1).map(str::to_owned);
    let session = match session_id.as_deref() {
        Some(id) => state
            .store
            .get_session(id)
            .await
            .map_err(internal)?
            .filter(|session| {
                session.instance_id == instance.id
                    && session.user_id == user.id
                    && session.status == "active"
                    && session.expires_at > now()
                    && session.auth_epoch == user.auth_epoch
            }),
        None => None,
    };
    if session_id.is_some() && session.is_none() {
        return Ok(token_error(
            StatusCode::BAD_REQUEST,
            "invalid_grant",
            "the authorizing session is no longer active",
            false,
        ));
    }
    issue_user_tokens(
        state,
        instance,
        client,
        &user,
        session.as_ref(),
        &record.scopes,
        record.nonce.as_deref(),
    )
    .await
}

async fn issue_user_tokens(
    state: &AppState,
    instance: &auth_contracts::models::Instance,
    client: &OAuthClient,
    user: &auth_contracts::models::User,
    session: Option<&auth_contracts::models::Session>,
    scopes: &[String],
    nonce: Option<&str>,
) -> AppResult<Response> {
    let scope = scopes.join(" ");
    let access = sign_access_token(
        state,
        instance,
        &user.id,
        &client.client_id,
        &scope,
        session.map(|s| s.id.as_str()),
        Some((user.auth_epoch, user.is_anonymous)),
    )
    .await?;
    let (refresh, _) = create_refresh_token(
        &state.store,
        &instance.id,
        &client.client_id,
        &user.id,
        scopes,
        user.auth_epoch,
    )
    .await?;
    let mut response = json!({"access_token": access, "token_type":"Bearer", "expires_in":ACCESS_TOKEN_TTL, "scope":scope, "refresh_token":refresh});
    if scopes.iter().any(|item| item == "openid") {
        let mut claims = json!({
            "iss": issuer(state, &instance.slug),
            "sub": user.id,
            "aud": client.client_id,
            "token_use": "id",
            "is_anonymous": user.is_anonymous,
            "at_hash": base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(&Sha256::digest(access.as_bytes())[..16]),
        });
        if let Some(session) = &session {
            claims["auth_time"] = json!(session.created_at);
        }
        if let Some(nonce) = nonce {
            claims["nonce"] = json!(nonce);
        }
        if scopes.iter().any(|item| item == "email")
            && let Some(email) = &user.email
        {
            claims["email"] = json!(email);
            claims["email_verified"] = json!(user.email_verified);
        }
        let key = state
            .key_ring
            .get_current(&instance.id)
            .await
            .map_err(internal)?;
        let id_token = jwt::sign(
            claims,
            Some(now().saturating_add(ACCESS_TOKEN_TTL)),
            &key,
            now(),
        )
        .map_err(|_| AppError::internal())?;
        response["id_token"] = json!(id_token);
    }
    Ok((StatusCode::OK, Json(response)).into_response())
}

async fn grant_refresh_token(
    state: &AppState,
    instance: &auth_contracts::models::Instance,
    client: &OAuthClient,
    form: &HashMap<String, String>,
) -> AppResult<Response> {
    let raw = form
        .get("refresh_token")
        .map(String::as_str)
        .unwrap_or_default();
    if raw.is_empty() {
        return Ok(token_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "refresh_token is required",
            false,
        ));
    }
    let key = tokens::sha256_hex(raw);
    let Some(token) = state
        .store
        .find_refresh_token(&key)
        .await
        .map_err(internal)?
    else {
        return Ok(token_error(
            StatusCode::BAD_REQUEST,
            "invalid_grant",
            "unknown refresh token",
            false,
        ));
    };
    if token.instance_id != instance.id {
        return Ok(token_error(
            StatusCode::BAD_REQUEST,
            "invalid_grant",
            "unknown refresh token",
            false,
        ));
    }
    if token.client_id != client.client_id {
        return Ok(token_error(
            StatusCode::BAD_REQUEST,
            "invalid_grant",
            "refresh token was issued to another client",
            false,
        ));
    }
    if token.status != "active" || token.expires_at <= now() {
        revoke_refresh_family(
            &state.store,
            &instance.id,
            &client.client_id,
            &token.user_id,
            &token.family_id,
        )
        .await?;
        return Ok(token_error(
            StatusCode::BAD_REQUEST,
            "invalid_grant",
            "refresh token is no longer active",
            false,
        ));
    }
    let Some(user) = state
        .store
        .get_user(&token.user_id)
        .await
        .map_err(internal)?
    else {
        revoke_refresh_family(
            &state.store,
            &instance.id,
            &client.client_id,
            &token.user_id,
            &token.family_id,
        )
        .await?;
        return Ok(token_error(
            StatusCode::BAD_REQUEST,
            "invalid_grant",
            "user is no longer active",
            false,
        ));
    };
    if user.auth_epoch != token.user_auth_epoch {
        revoke_refresh_family(
            &state.store,
            &instance.id,
            &client.client_id,
            &token.user_id,
            &token.family_id,
        )
        .await?;
        return Ok(token_error(
            StatusCode::BAD_REQUEST,
            "invalid_grant",
            "refresh token is no longer active",
            false,
        ));
    }
    if user.instance_id != instance.id
        || user.banned
        || user.deleted
        || !state
            .store
            .scim_auth_eligible(&user)
            .await
            .map_err(internal)?
    {
        revoke_refresh_family(
            &state.store,
            &instance.id,
            &client.client_id,
            &token.user_id,
            &token.family_id,
        )
        .await?;
        return Ok(token_error(
            StatusCode::BAD_REQUEST,
            "invalid_grant",
            "user is no longer active",
            false,
        ));
    }
    let new_id = format!("rtk_{}", tokens::random_token(20));
    match state
        .store
        .rotate_refresh_token(&key, &instance.id, &client.client_id, now(), &new_id)
        .await
        .map_err(internal)?
    {
        auth_store::RefreshRotateOutcome::Rotated(rotated) => {
            let created = create_refresh_token_with_id(
                &state.store,
                &new_id,
                &instance.id,
                &client.client_id,
                &user.id,
                &rotated.family_id,
                &rotated.scopes,
                rotated.user_auth_epoch,
            )
            .await;
            let (new_raw, _) = match created {
                Ok(value) => value,
                Err(_error)
                    if state
                        .store
                        .is_refresh_family_revoked(
                            &instance.id,
                            &client.client_id,
                            &rotated.family_id,
                        )
                        .await
                        .map_err(internal)? =>
                {
                    revoke_refresh_family(
                        &state.store,
                        &instance.id,
                        &client.client_id,
                        &user.id,
                        &rotated.family_id,
                    )
                    .await?;
                    return Ok(token_error(
                        StatusCode::BAD_REQUEST,
                        "invalid_grant",
                        "refresh token family is no longer active",
                        false,
                    ));
                }
                Err(error) => return Err(error),
            };
            if state
                .store
                .is_refresh_family_revoked(&instance.id, &client.client_id, &rotated.family_id)
                .await
                .map_err(internal)?
            {
                revoke_refresh_family(
                    &state.store,
                    &instance.id,
                    &client.client_id,
                    &user.id,
                    &rotated.family_id,
                )
                .await?;
                return Ok(token_error(
                    StatusCode::BAD_REQUEST,
                    "invalid_grant",
                    "refresh token family is no longer active",
                    false,
                ));
            }
            audit(
                state,
                &instance.id,
                Some(&user.id),
                "oauth.token_refreshed",
                json!({"client_id":client.client_id}),
            )
            .await?;
            let scope = rotated.scopes.join(" ");
            let access = sign_access_token(
                state,
                instance,
                &user.id,
                &client.client_id,
                &scope,
                None,
                Some((rotated.user_auth_epoch, user.is_anonymous)),
            )
            .await?;
            if state
                .store
                .is_refresh_family_revoked(&instance.id, &client.client_id, &rotated.family_id)
                .await
                .map_err(internal)?
            {
                revoke_refresh_family(
                    &state.store,
                    &instance.id,
                    &client.client_id,
                    &user.id,
                    &rotated.family_id,
                )
                .await?;
                return Ok(token_error(
                    StatusCode::BAD_REQUEST,
                    "invalid_grant",
                    "refresh token family is no longer active",
                    false,
                ));
            }
            let epoch_is_current = state
                .store
                .get_user(&user.id)
                .await
                .map_err(internal)?
                .is_some_and(|current| {
                    current.instance_id == instance.id
                        && !current.deleted
                        && !current.banned
                        && current.auth_epoch == rotated.user_auth_epoch
                });
            if !epoch_is_current {
                revoke_refresh_family(
                    &state.store,
                    &instance.id,
                    &client.client_id,
                    &user.id,
                    &rotated.family_id,
                )
                .await?;
                return Ok(token_error(
                    StatusCode::BAD_REQUEST,
                    "invalid_grant",
                    "refresh token is no longer active",
                    false,
                ));
            }
            Ok((StatusCode::OK, Json(json!({"access_token":access,"token_type":"Bearer","expires_in":ACCESS_TOKEN_TTL,"scope":scope,"refresh_token":new_raw}))).into_response())
        }
        auth_store::RefreshRotateOutcome::Inactive(inactive) => {
            revoke_refresh_family(
                &state.store,
                &instance.id,
                &client.client_id,
                &inactive.user_id,
                &inactive.family_id,
            )
            .await?;
            Ok(token_error(
                StatusCode::BAD_REQUEST,
                "invalid_grant",
                "refresh token is no longer active",
                false,
            ))
        }
        auth_store::RefreshRotateOutcome::Expired => {
            revoke_refresh_family(
                &state.store,
                &instance.id,
                &client.client_id,
                &token.user_id,
                &token.family_id,
            )
            .await?;
            Ok(token_error(
                StatusCode::BAD_REQUEST,
                "invalid_grant",
                "refresh token is no longer active",
                false,
            ))
        }
        auth_store::RefreshRotateOutcome::Missing => Ok(token_error(
            StatusCode::BAD_REQUEST,
            "invalid_grant",
            "unknown refresh token",
            false,
        )),
    }
}

async fn grant_client_credentials(
    state: &AppState,
    instance: &auth_contracts::models::Instance,
    client: &OAuthClient,
    form: &HashMap<String, String>,
) -> AppResult<Response> {
    if client.r#public || client.client_secret_hash.is_none() {
        return Ok(token_error(
            StatusCode::BAD_REQUEST,
            "unauthorized_client",
            "client_credentials requires an authenticated confidential client",
            false,
        ));
    }
    let scope = form.get("scope").map(String::as_str).unwrap_or_default();
    let access = sign_access_token(
        state,
        instance,
        &client.client_id,
        &client.client_id,
        scope,
        None,
        None,
    )
    .await?;
    Ok((StatusCode::OK, Json(json!({"access_token":access,"token_type":"Bearer","expires_in":ACCESS_TOKEN_TTL,"scope":scope}))).into_response())
}

#[utoipa::path(
    get,
    path = "/{slug}/oauth/userinfo",
    params(("slug" = String, Path)),
    responses((status = 200, description = "Claims authorized by the access token", body = UserInfoResponse), (status = 401, description = "Bearer token is missing or invalid", body = OAuthErrorResponse)),
    tag = "OAuth"
)]
async fn userinfo(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
) -> AppResult<Response> {
    let Some(instance) = state
        .store
        .find_instance_by_slug(&slug)
        .await
        .map_err(internal)?
    else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "not_found",
            "no such instance",
        ));
    };
    let Some(token) = bearer_token(&headers) else {
        return Ok(bearer_error("missing bearer token", true));
    };
    let Some(claims) = verify_access_token(&state, &instance, token).await? else {
        return Ok(bearer_error("invalid access token", false));
    };
    let Some(subject) = claims.get("sub").and_then(Value::as_str) else {
        return Ok(bearer_error("invalid access token", false));
    };
    let Some(audience) = claims.get("aud").and_then(aud_string) else {
        return Ok(bearer_error("invalid access token", false));
    };
    if subject == audience {
        return Ok(bearer_error(
            "access token does not represent a user",
            false,
        ));
    }
    let Some(user) = state.store.get_user(subject).await.map_err(internal)? else {
        return Ok(bearer_error("invalid access token", false));
    };
    if user.instance_id != instance.id
        || user.deleted
        || user.banned
        || claims
            .get("auth_epoch")
            .and_then(Value::as_u64)
            .unwrap_or_default()
            != user.auth_epoch
        || !state
            .store
            .scim_auth_eligible(&user)
            .await
            .map_err(internal)?
    {
        return Ok(bearer_error("invalid access token", false));
    }
    let scopes = claims
        .get("scope")
        .and_then(Value::as_str)
        .unwrap_or_default()
        .split_ascii_whitespace()
        .collect::<Vec<_>>();
    let mut body = json!({"sub":user.id, "is_anonymous":user.is_anonymous});
    if scopes.contains(&"email")
        && let Some(email) = user.email
    {
        body["email"] = json!(email);
        body["email_verified"] = json!(user.email_verified);
    }
    if scopes.contains(&"profile")
        && let Some(name) = user.name
    {
        body["name"] = json!(name);
    }
    Ok((StatusCode::OK, Json(body)).into_response())
}

#[utoipa::path(
    post,
    path = "/{slug}/oauth/introspect",
    params(("slug" = String, Path)),
    request_body(content = IntrospectionRequest, content_type = "application/x-www-form-urlencoded"),
    responses((status = 200, description = "Token activity result", body = IntrospectionResponse), (status = 401, description = "Client authentication failed", body = OAuthErrorResponse)),
    security(("clientBasic" = []), ()),
    tag = "OAuth"
)]
async fn introspect(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Form(form): Form<HashMap<String, String>>,
) -> AppResult<Response> {
    let Some(instance) = state
        .store
        .find_instance_by_slug(&slug)
        .await
        .map_err(internal)?
    else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "not_found",
            "no such instance",
        ));
    };
    let client = match authenticate_client(&state.store, &instance.id, &headers, &form).await {
        Ok(value) => value,
        Err(error) => return Ok(no_store(client_authentication_error_response(error))),
    };
    let raw = form.get("token").map(String::as_str).unwrap_or_default();
    if raw.is_empty() {
        return Ok(token_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "token is required",
            false,
        ));
    }
    if raw.contains('.') {
        if let Some(claims) = verify_access_token(&state, &instance, raw).await? {
            let Some(audience) = claims.get("aud").and_then(aud_string) else {
                return Ok(no_store(
                    (StatusCode::OK, Json(json!({"active":false}))).into_response(),
                ));
            };
            if audience != client.client_id {
                return Ok(no_store(
                    (StatusCode::OK, Json(json!({"active":false}))).into_response(),
                ));
            };
            let Some(subject) = claims.get("sub").and_then(Value::as_str) else {
                return Ok(no_store(
                    (StatusCode::OK, Json(json!({"active":false}))).into_response(),
                ));
            };
            let active = if subject == audience {
                true
            } else if let Some(user) = state.store.get_user(subject).await.map_err(internal)? {
                user.instance_id == instance.id
                    && !user.banned
                    && !user.deleted
                    && claims
                        .get("auth_epoch")
                        .and_then(Value::as_u64)
                        .unwrap_or_default()
                        == user.auth_epoch
                    && state
                        .store
                        .scim_auth_eligible(&user)
                        .await
                        .map_err(internal)?
            } else {
                false
            };
            if !active {
                return Ok(no_store(
                    (StatusCode::OK, Json(json!({"active":false}))).into_response(),
                ));
            }
            let response = json!({
                "active":true,"token_type":"Bearer",
                "iss":claims.get("iss").and_then(Value::as_str).unwrap_or_default(),
                "sub":claims.get("sub").and_then(Value::as_str).unwrap_or_default(),
                "aud":claims.get("aud").and_then(aud_string).unwrap_or_default(),
                "client_id":claims.get("aud").and_then(aud_string).unwrap_or_default(),
                "scope":claims.get("scope").and_then(Value::as_str).unwrap_or_default(),
                "exp":claims.get("exp").and_then(Value::as_i64).unwrap_or_default(),
                "iat":claims.get("iat").and_then(Value::as_i64).unwrap_or_default(),
            });
            return Ok(no_store((StatusCode::OK, Json(response)).into_response()));
        }
    } else {
        let key = tokens::sha256_hex(raw);
        if let Some(token) = state
            .store
            .find_refresh_token(&key)
            .await
            .map_err(internal)?
        {
            let active = token.instance_id == instance.id
                && token.client_id == client.client_id
                && !token.family_id.is_empty()
                && token.status == "active"
                && token.expires_at > now()
                && !state
                    .store
                    .is_refresh_family_revoked(&instance.id, &client.client_id, &token.family_id)
                    .await
                    .map_err(internal)?;
            let active = if active {
                if let Some(user) = state
                    .store
                    .get_user(&token.user_id)
                    .await
                    .map_err(internal)?
                {
                    user.instance_id == instance.id
                        && !user.banned
                        && !user.deleted
                        && user.auth_epoch == token.user_auth_epoch
                        && state
                            .store
                            .scim_auth_eligible(&user)
                            .await
                            .map_err(internal)?
                } else {
                    false
                }
            } else {
                false
            };
            if active {
                return Ok(no_store((StatusCode::OK, Json(json!({"active":true,"token_type":"refresh_token","sub":token.user_id,"client_id":token.client_id,"scope":token.scopes.join(" "),"exp":token.expires_at,"iat":token.created_at}))).into_response()));
            }
        }
    }
    Ok(no_store(
        (StatusCode::OK, Json(json!({"active":false}))).into_response(),
    ))
}

#[utoipa::path(
    post,
    path = "/{slug}/oauth/revoke",
    params(("slug" = String, Path)),
    request_body(content = RevocationRequest, content_type = "application/x-www-form-urlencoded"),
    responses((status = 200, description = "Token revoked or already inactive"), (status = 401, description = "Client authentication failed", body = OAuthErrorResponse)),
    security(("clientBasic" = []), ()),
    tag = "OAuth"
)]
async fn revoke(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Form(form): Form<HashMap<String, String>>,
) -> AppResult<Response> {
    let Some(instance) = state
        .store
        .find_instance_by_slug(&slug)
        .await
        .map_err(internal)?
    else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "not_found",
            "no such instance",
        ));
    };
    let client = match authenticate_client(&state.store, &instance.id, &headers, &form).await {
        Ok(value) => value,
        Err(error) => return Ok(client_authentication_error_response(error)),
    };
    let raw = form.get("token").map(String::as_str).unwrap_or_default();
    if raw.is_empty() {
        return Ok(token_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "token is required",
            false,
        ));
    }
    if let Some(token) = state
        .store
        .find_refresh_token(&tokens::sha256_hex(raw))
        .await
        .map_err(internal)?
        && token.instance_id == instance.id
        && token.client_id == client.client_id
        && token.status == "active"
    {
        revoke_refresh_family(
            &state.store,
            &instance.id,
            &client.client_id,
            &token.user_id,
            &token.family_id,
        )
        .await?;
        state
            .store
            .upsert_refresh_token(&RefreshToken {
                status: "revoked".into(),
                ..token
            })
            .await
            .map_err(internal)?;
    }
    Ok(no_store(StatusCode::OK.into_response()))
}

#[utoipa::path(
    get,
    path = "/{slug}/oauth/logout",
    params(("slug" = String, Path), ("client_id" = Option<String>, Query), ("post_logout_redirect_uri" = Option<String>, Query), ("state" = Option<String>, Query)),
    responses((status = 200, description = "Signed-out page"), (status = 303, description = "Redirect to a registered client origin")),
    tag = "OAuth"
)]
async fn logout(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    Query(query): Query<HashMap<String, String>>,
    headers: HeaderMap,
) -> AppResult<Response> {
    let Some(instance) = state
        .store
        .find_instance_by_slug(&slug)
        .await
        .map_err(internal)?
    else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "not_found",
            "no such instance",
        ));
    };
    let mut response = if let Some(cookie) = cookie_value(&headers, SESSION_COOKIE) {
        if let Some((session, _)) =
            crate::features::users::resolve_session(&state, &instance.id, cookie).await?
        {
            state
                .store
                .upsert_session(&auth_contracts::models::Session {
                    status: "revoked".into(),
                    ..session
                })
                .await
                .map_err(internal)?;
        }
        (
            StatusCode::OK,
            Html("<h1>Signed out</h1><p>You have been signed out.</p>".to_owned()),
        )
            .into_response()
    } else {
        (
            StatusCode::OK,
            Html("<h1>Signed out</h1><p>You have been signed out.</p>".to_owned()),
        )
            .into_response()
    };
    crate::features::ui::delete_session_cookie(&mut response);
    let client_id = query
        .get("client_id")
        .map(String::as_str)
        .unwrap_or_default();
    let candidate = query
        .get("post_logout_redirect_uri")
        .map(String::as_str)
        .unwrap_or_default();
    let state_value = query.get("state").map(String::as_str).unwrap_or_default();
    if !candidate.is_empty()
        && !client_id.is_empty()
        && let Some(client) = state
            .store
            .find_oauth_client(&instance.id, client_id)
            .await
            .map_err(internal)?
        && client
            .redirect_uris
            .iter()
            .any(|registered| same_origin(registered, candidate))
    {
        let mut destination = Url::parse(candidate).map_err(|_| AppError::internal())?;
        if !state_value.is_empty() {
            destination
                .query_pairs_mut()
                .append_pair("state", state_value);
        }
        response = Redirect::to(destination.as_str()).into_response();
        crate::features::ui::delete_session_cookie(&mut response);
    }
    Ok(response)
}

#[derive(Debug, Deserialize, ToSchema)]
pub struct CreateClientRequest {
    pub name: Option<String>,
    pub redirect_uris: Option<Vec<String>>,
    #[serde(default, rename = "public")]
    pub is_public: bool,
    pub token_endpoint_auth_method: Option<String>,
    #[serde(default)]
    pub allow_anonymous: bool,
    #[serde(default)]
    pub allowed_origins: Vec<String>,
}

#[utoipa::path(
    post,
    path = "/v1/{slug}/oauth-clients",
    params(("slug" = String, Path)),
    request_body = CreateClientRequest,
    responses((status = 200, description = "Created OAuth client; the secret is returned once"), (status = 400, description = "Invalid client configuration"), (status = 401, description = "Management API key required")),
    tag = "OAuth"
)]
async fn create_client(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Json(body): Json<CreateClientRequest>,
) -> AppResult<Response> {
    let context = match management::require(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(error) => return Ok(error.into_response()),
    };
    let Some(name) = body.name.filter(|value| !value.trim().is_empty()) else {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "name and redirect_uris are required",
        ));
    };
    let Some(redirect_uris) = body.redirect_uris.filter(|uris| !uris.is_empty()) else {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "name and redirect_uris are required",
        ));
    };
    if redirect_uris.iter().any(|value| !valid_redirect_uri(value)) {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "redirect_uris must be absolute http(s) URLs without fragments or credentials",
        ));
    }
    let auth_method = body
        .token_endpoint_auth_method
        .filter(|value| !value.is_empty())
        .unwrap_or_else(|| {
            if body.is_public {
                "none".into()
            } else {
                "client_secret_basic".into()
            }
        });
    if !matches!(
        auth_method.as_str(),
        "client_secret_basic" | "client_secret_post" | "none"
    ) {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "token_endpoint_auth_method must be client_secret_basic, client_secret_post, or none",
        ));
    }
    if body.is_public && auth_method != "none" {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "public clients must use token_endpoint_auth_method=none",
        ));
    }
    let is_public = body.is_public || auth_method == "none";
    if body.allowed_origins.len() > 20
        || body
            .allowed_origins
            .iter()
            .any(|origin| !cors::valid_origin(origin))
    {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "allowed_origins must contain at most 20 exact HTTPS origins (HTTP is allowed for loopback)",
        ));
    }
    let secret = (!is_public).then(|| tokens::random_token(40));
    let client_id = tokens::random_token(24);
    let client = OAuthClient {
        allow_anonymous: body.allow_anonymous,
        id: format!("clt_{}", tokens::random_token(20)),
        instance_id: context.instance.id.clone(),
        client_id: client_id.clone(),
        client_key: format!("{}:{client_id}", context.instance.id),
        name: name.trim().to_owned(),
        client_secret_hash: secret.as_deref().map(tokens::sha256_hex),
        redirect_uris,
        allowed_origins: body.allowed_origins,
        token_endpoint_auth_method: auth_method,
        r#public: is_public,
        created_at: now(),
    };
    state
        .store
        .create_oauth_client(&client)
        .await
        .map_err(internal)?;
    Ok((
        StatusCode::OK,
        Json(json!({"client":client_view(&client),"client_secret":secret})),
    )
        .into_response())
}

#[utoipa::path(
    get,
    path = "/v1/{slug}/oauth-clients",
    params(("slug" = String, Path)),
    responses((status = 200, description = "Instance OAuth clients"), (status = 401, description = "Management API key required")),
    tag = "OAuth"
)]
async fn list_clients(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
) -> AppResult<Response> {
    let context = match management::require(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(error) => return Ok(error.into_response()),
    };
    let predicate = Predicate::equal("instanceId", &context.instance.id).map_err(internal)?;
    let clients: Vec<OAuthClient> = state
        .store
        .query_all("oauth_clients", &[predicate], 200)
        .await
        .map_err(internal)?;
    Ok((
        StatusCode::OK,
        Json(json!({"clients":clients.iter().map(client_view).collect::<Vec<_>>()})),
    )
        .into_response())
}

#[derive(Deserialize, ToSchema)]
#[serde(deny_unknown_fields)]
struct UpdateClientRequest {
    name: Option<String>,
    redirect_uris: Option<Vec<String>>,
    allowed_origins: Option<Vec<String>>,
    allow_anonymous: Option<bool>,
}

#[utoipa::path(patch, path = "/v1/{slug}/oauth-clients/{id}", params(("slug" = String, Path), ("id" = String, Path)), request_body = UpdateClientRequest,
    responses((status = 200, description = "Updated OAuth client without changing credentials"), (status = 400, description = "Invalid origins or redirects"), (status = 409, description = "Concurrent client change; reload and retry")), tag = "OAuth")]
async fn update_client(
    State(state): State<AppState>,
    Path((slug, id)): Path<(String, String)>,
    headers: HeaderMap,
    Json(body): Json<UpdateClientRequest>,
) -> AppResult<Response> {
    let context = management::require(&state, &headers, &slug).await?;
    let Some((mut client, version)) = state
        .store
        .get_versioned::<OAuthClient>("oauth_clients", &id)
        .await
        .map_err(|e| crate::app::storage_error(&e))?
        .filter(|(c, _)| c.instance_id == context.instance.id)
    else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "not_found",
            "OAuth client not found",
        ));
    };
    if let Some(name) = body.name {
        if name.trim().is_empty() || name.len() > 128 {
            return Ok(api_error(
                StatusCode::BAD_REQUEST,
                "invalid_request",
                "name must be 1-128 characters",
            ));
        }
        client.name = name.trim().to_owned();
    }
    if let Some(uris) = body.redirect_uris {
        if uris.is_empty() || uris.len() > 100 || uris.iter().any(|uri| !valid_redirect_uri(uri)) {
            return Ok(api_error(
                StatusCode::BAD_REQUEST,
                "invalid_request",
                "redirect_uris must contain 1-100 valid redirect URIs",
            ));
        }
        client.redirect_uris = uris;
    }
    if let Some(origins) = body.allowed_origins {
        if origins.len() > 20 || origins.iter().any(|origin| !cors::valid_origin(origin)) {
            return Ok(api_error(
                StatusCode::BAD_REQUEST,
                "invalid_request",
                "allowed_origins must contain at most 20 exact HTTPS or loopback HTTP origins",
            ));
        }
        client.allowed_origins = origins;
    }
    if let Some(enabled) = body.allow_anonymous {
        client.allow_anonymous = enabled;
    }
    if !state
        .store
        .compare_and_put("oauth_clients", &id, &client, &version)
        .await
        .map_err(|e| crate::app::storage_error(&e))?
    {
        return Ok(api_error(
            StatusCode::CONFLICT,
            "client_changed",
            "The client changed; reload and retry.",
        ));
    }
    audit(
        &state,
        &context.instance.id,
        Some(&context.api_key_id),
        "oauth.client_updated",
        json!({"client_id":client.client_id}),
    )
    .await?;
    Ok(Json(client_view(&client)).into_response())
}

#[utoipa::path(
    delete,
    path = "/v1/{slug}/oauth-clients/{id}",
    params(("slug" = String, Path), ("id" = String, Path)),
    responses((status = 204, description = "OAuth client deleted"), (status = 401, description = "Management API key required"), (status = 404, description = "OAuth client not found")),
    tag = "OAuth"
)]
async fn delete_client(
    State(state): State<AppState>,
    Path((slug, id)): Path<(String, String)>,
    headers: HeaderMap,
) -> AppResult<Response> {
    let context = match management::require(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(error) => return Ok(error.into_response()),
    };
    let Some(client) = state.store.get_oauth_client(&id).await.map_err(internal)? else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "not_found",
            "no such oauth client",
        ));
    };
    if client.instance_id != context.instance.id {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "not_found",
            "no such oauth client",
        ));
    }
    state
        .store
        .delete_oauth_client(&id)
        .await
        .map_err(internal)?;
    Ok(StatusCode::NO_CONTENT.into_response())
}

#[derive(Debug, PartialEq, Eq)]
enum ClientAuthenticationError {
    InvalidCredentials {
        description: &'static str,
        used_basic: bool,
    },
    StorageUnavailable,
}

fn client_authentication_error_response(error: ClientAuthenticationError) -> Response {
    match error {
        ClientAuthenticationError::InvalidCredentials {
            description,
            used_basic,
        } => token_error(
            StatusCode::UNAUTHORIZED,
            "invalid_client",
            description,
            used_basic,
        ),
        ClientAuthenticationError::StorageUnavailable => token_error(
            StatusCode::INTERNAL_SERVER_ERROR,
            "server_error",
            "client authentication is temporarily unavailable",
            false,
        ),
    }
}

async fn authenticate_client(
    store: &AuthStore,
    instance_id: &str,
    headers: &HeaderMap,
    form: &HashMap<String, String>,
) -> Result<OAuthClient, ClientAuthenticationError> {
    let authorization = headers
        .get(header::AUTHORIZATION)
        .and_then(|value| value.to_str().ok())
        .unwrap_or_default();
    let used_basic = authorization
        .get(..6)
        .is_some_and(|value| value.eq_ignore_ascii_case("Basic "));
    let (client_id, secret) = if used_basic {
        let decoded = STANDARD.decode(authorization[6..].trim()).map_err(|_| {
            ClientAuthenticationError::InvalidCredentials {
                description: "malformed Basic authorization header",
                used_basic: true,
            }
        })?;
        let decoded = String::from_utf8(decoded).map_err(|_| {
            ClientAuthenticationError::InvalidCredentials {
                description: "malformed Basic authorization header",
                used_basic: true,
            }
        })?;
        let (id, secret) =
            decoded
                .split_once(':')
                .ok_or(ClientAuthenticationError::InvalidCredentials {
                    description: "malformed Basic authorization header",
                    used_basic: true,
                })?;
        let decode_form = |part: &str| {
            url::form_urlencoded::parse(format!("v={part}").as_bytes())
                .next()
                .map(|pair| pair.1.into_owned())
                .unwrap_or_default()
        };
        (decode_form(id), Some(decode_form(secret)))
    } else {
        (
            form.get("client_id").cloned().unwrap_or_default(),
            form.get("client_secret")
                .filter(|value| !value.is_empty())
                .cloned(),
        )
    };
    if client_id.is_empty() {
        return Err(ClientAuthenticationError::InvalidCredentials {
            description: "client_id is required",
            used_basic,
        });
    }
    let client = store
        .find_oauth_client(instance_id, &client_id)
        .await
        .map_err(|error| {
            tracing::error!(?error, "OAuth client lookup failed during authentication");
            ClientAuthenticationError::StorageUnavailable
        })?
        .ok_or(ClientAuthenticationError::InvalidCredentials {
            description: "unknown client",
            used_basic,
        })?;

    match client.token_endpoint_auth_method.as_str() {
        "none"
            if !used_basic
                && secret.is_none()
                && !form.contains_key("client_secret")
                && client.client_secret_hash.is_none() =>
        {
            Ok(client)
        }
        "none" => Err(ClientAuthenticationError::InvalidCredentials {
            description: "client authentication method mismatch",
            used_basic,
        }),
        "client_secret_basic" if used_basic && form.get("client_secret").is_none() => {
            verify_client_secret(client, secret, used_basic)
        }
        "client_secret_post" if !used_basic => verify_client_secret(client, secret, used_basic),
        "client_secret_basic" | "client_secret_post" => {
            Err(ClientAuthenticationError::InvalidCredentials {
                description: "client authentication method mismatch",
                used_basic,
            })
        }
        _ => Err(ClientAuthenticationError::InvalidCredentials {
            description: "unsupported client authentication method",
            used_basic,
        }),
    }
}

fn verify_client_secret(
    client: OAuthClient,
    secret: Option<String>,
    used_basic: bool,
) -> Result<OAuthClient, ClientAuthenticationError> {
    let Some(expected) = client.client_secret_hash.as_deref() else {
        return Err(ClientAuthenticationError::InvalidCredentials {
            description: "client authentication method is not configured",
            used_basic,
        });
    };
    let Some(secret) = secret else {
        return Err(ClientAuthenticationError::InvalidCredentials {
            description: "client secret required",
            used_basic,
        });
    };
    if tokens::fixed_time_equals(&tokens::sha256_hex(&secret), expected) {
        Ok(client)
    } else {
        Err(ClientAuthenticationError::InvalidCredentials {
            description: "invalid client secret",
            used_basic,
        })
    }
}

async fn sign_access_token(
    state: &AppState,
    instance: &auth_contracts::models::Instance,
    subject: &str,
    audience: &str,
    scope: &str,
    session_id: Option<&str>,
    user_state: Option<(u64, bool)>,
) -> AppResult<String> {
    let mut claims = json!({
        "iss":issuer(state, &instance.slug),
        "sub":subject,
        "aud":audience,
        "scope":scope,
        "token_use":"access"
    });
    if let Some(session_id) = session_id {
        claims["sid"] = json!(session_id);
    }
    if let Some((auth_epoch, is_anonymous)) = user_state {
        claims["auth_epoch"] = json!(auth_epoch);
        claims["is_anonymous"] = json!(is_anonymous);
    }
    let key = state
        .key_ring
        .get_current(&instance.id)
        .await
        .map_err(internal)?;
    jwt::sign(
        claims,
        Some(now().saturating_add(ACCESS_TOKEN_TTL)),
        &key,
        now(),
    )
    .map_err(|_| AppError::internal())
}

async fn verify_access_token(
    state: &AppState,
    instance: &auth_contracts::models::Instance,
    raw: &str,
) -> AppResult<Option<Value>> {
    let keys = state
        .key_ring
        .get_active(&instance.id)
        .await
        .map_err(internal)?;
    let claims = jwt::verify(
        raw,
        |kid| {
            keys.into_iter()
                .find(|key| key.kid() == kid)
                .map(|key| Box::new(key) as Box<dyn JwtKey>)
        },
        now(),
    )
    .filter(|claims| {
        claims
            .get("exp")
            .and_then(Value::as_i64)
            .is_some_and(|exp| exp > now())
    });
    let Some(claims) = claims else {
        return Ok(None);
    };
    if !is_access_token_claims(&claims) {
        return Ok(None);
    }
    if claims.get("iss").and_then(Value::as_str) != Some(issuer(state, &instance.slug).as_str()) {
        return Ok(None);
    }
    let Some(audience) = claims.get("aud").and_then(aud_string) else {
        return Ok(None);
    };
    if state
        .store
        .find_oauth_client(&instance.id, &audience)
        .await
        .map_err(internal)?
        .is_none()
    {
        return Ok(None);
    }
    if let Some(session_id) = claims.get("sid").and_then(Value::as_str) {
        let Some(session) = state
            .store
            .get_session(session_id)
            .await
            .map_err(internal)?
        else {
            return Ok(None);
        };
        if session.instance_id != instance.id
            || Some(session.user_id.as_str()) != claims.get("sub").and_then(Value::as_str)
            || session.status != "active"
            || session.expires_at <= now()
            || Some(session.auth_epoch) != claims.get("auth_epoch").and_then(Value::as_u64)
        {
            return Ok(None);
        }
    }
    Ok(Some(claims))
}

fn is_access_token_claims(claims: &Value) -> bool {
    claims.get("token_use").and_then(Value::as_str) == Some("access")
}

async fn create_refresh_token(
    store: &AuthStore,
    instance_id: &str,
    client_id: &str,
    user_id: &str,
    scopes: &[String],
    user_auth_epoch: u64,
) -> AppResult<(String, String)> {
    let id = format!("rtk_{}", tokens::random_token(20));
    let family_id = format!("rtf_{}", tokens::random_token(20));
    create_refresh_token_with_id(
        store,
        &id,
        instance_id,
        client_id,
        user_id,
        &family_id,
        scopes,
        user_auth_epoch,
    )
    .await
}

#[expect(
    clippy::too_many_arguments,
    reason = "Refresh-token owner, family, scope, and auth-epoch bindings stay explicit at credential creation."
)]
async fn create_refresh_token_with_id(
    store: &AuthStore,
    id: &str,
    instance_id: &str,
    client_id: &str,
    user_id: &str,
    family_id: &str,
    scopes: &[String],
    user_auth_epoch: u64,
) -> AppResult<(String, String)> {
    let raw = tokens::random_token(48);
    let timestamp = now();
    store
        .create_refresh_token(&RefreshToken {
            id: id.to_owned(),
            instance_id: instance_id.to_owned(),
            client_id: client_id.to_owned(),
            user_id: user_id.to_owned(),
            user_auth_epoch,
            token_key: tokens::sha256_hex(&raw),
            status: "active".into(),
            scopes: scopes.to_vec(),
            created_at: timestamp,
            expires_at: timestamp.saturating_add(REFRESH_TOKEN_TTL),
            rotated_to: None,
            family_id: family_id.to_owned(),
        })
        .await
        .map_err(internal)?;
    Ok((raw, id.to_owned()))
}

async fn revoke_refresh_family(
    store: &AuthStore,
    instance_id: &str,
    client_id: &str,
    user_id: &str,
    family_id: &str,
) -> AppResult<()> {
    if family_id.is_empty() {
        for token in store
            .list_refresh_tokens_by_user(user_id)
            .await
            .map_err(internal)?
        {
            if token.instance_id == instance_id
                && token.client_id == client_id
                && token.family_id.is_empty()
                && token.status == "active"
            {
                store
                    .upsert_refresh_token(&RefreshToken {
                        status: "revoked".into(),
                        ..token
                    })
                    .await
                    .map_err(internal)?;
            }
        }
        return Ok(());
    }
    store
        .revoke_refresh_family(instance_id, client_id, family_id)
        .await
        .map_err(internal)?;
    for token in store
        .list_refresh_tokens_by_user(user_id)
        .await
        .map_err(internal)?
    {
        if token.instance_id == instance_id
            && token.client_id == client_id
            && token.family_id == family_id
            && token.status == "active"
        {
            store
                .upsert_refresh_token(&RefreshToken {
                    status: "revoked".into(),
                    ..token
                })
                .await
                .map_err(internal)?;
        }
    }
    Ok(())
}

async fn revoke_user_refresh_families(
    store: &AuthStore,
    instance_id: &str,
    client_id: &str,
    user_id: &str,
) -> AppResult<()> {
    let mut seen = HashSet::new();
    for token in store
        .list_refresh_tokens_by_user(user_id)
        .await
        .map_err(internal)?
    {
        if token.instance_id == instance_id
            && token.client_id == client_id
            && seen.insert(token.family_id.clone())
        {
            revoke_refresh_family(store, instance_id, client_id, user_id, &token.family_id).await?;
        }
    }
    Ok(())
}

async fn audit(
    state: &AppState,
    instance_id: &str,
    actor_id: Option<&str>,
    event_type: &str,
    data: Value,
) -> AppResult<()> {
    let data = data.as_object().cloned().unwrap_or_default();
    state
        .store
        .create_audit_event(&auth_contracts::models::AuditEvent {
            id: format!("evt_{}", tokens::random_token(20)),
            instance_id: instance_id.to_owned(),
            r#type: event_type.to_owned(),
            actor_id: actor_id.map(str::to_owned),
            data,
            created_at: now(),
            integrity_version: None,
            integrity_hash: None,
            integrity_signature: None,
            integrity_key_fingerprint: None,
        })
        .await
        .map_err(internal)?;
    Ok(())
}

fn client_view(client: &OAuthClient) -> Value {
    json!({"id":client.id,"client_id":client.client_id,"name":client.name,"redirect_uris":client.redirect_uris,
        "token_endpoint_auth_method":client.token_endpoint_auth_method,"public":client.r#public,"allowed_origins":client.allowed_origins,"allow_anonymous":client.allow_anonymous,"created_at":client.created_at})
}

fn api_error(status: StatusCode, code: &str, message: &str) -> Response {
    (
        status,
        Json(json!({"error":{"code":code,"message":message}})),
    )
        .into_response()
}

fn token_error(status: StatusCode, error: &str, description: &str, basic: bool) -> Response {
    let mut response = (
        status,
        Json(json!({"error":error,"error_description":description})),
    )
        .into_response();
    if basic {
        response
            .headers_mut()
            .insert(header::WWW_AUTHENTICATE, HeaderValue::from_static("Basic"));
    }
    response
}

fn no_store(mut response: Response) -> Response {
    response
        .headers_mut()
        .insert(header::CACHE_CONTROL, HeaderValue::from_static("no-store"));
    response
        .headers_mut()
        .insert(header::PRAGMA, HeaderValue::from_static("no-cache"));
    response
}

fn bearer_error(description: &str, missing: bool) -> Response {
    let challenge = if missing {
        "Bearer".to_owned()
    } else {
        format!(
            "Bearer error=\"invalid_token\", error_description=\"{}\"",
            description.replace('"', "")
        )
    };
    let mut response = (StatusCode::UNAUTHORIZED, Json(json!({"error":if missing {"invalid_request"} else {"invalid_token"},"error_description":description}))).into_response();
    if let Ok(value) = HeaderValue::from_str(&challenge) {
        response
            .headers_mut()
            .insert(header::WWW_AUTHENTICATE, value);
    }
    response
}

fn redirect_error(uri: &str, state: &str, error: &str, description: &str) -> Response {
    let Ok(mut destination) = Url::parse(uri) else {
        return api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "redirect_uri is invalid",
        );
    };
    destination
        .query_pairs_mut()
        .append_pair("error", error)
        .append_pair("error_description", description);
    if !state.is_empty() {
        destination.query_pairs_mut().append_pair("state", state);
    }
    Redirect::to(destination.as_str()).into_response()
}

fn issuer(state: &AppState, slug: &str) -> String {
    format!(
        "{}/{}",
        state.options.public_origin.trim_end_matches('/'),
        slug
    )
}
fn now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |duration| duration.as_secs() as i64)
}
fn internal(error: impl std::fmt::Debug) -> AppError {
    tracing::error!(?error, "OAuth operation failed");
    AppError::internal()
}

fn bearer_token(headers: &HeaderMap) -> Option<&str> {
    let value = headers.get(header::AUTHORIZATION)?.to_str().ok()?;
    let (scheme, token) = value.split_once(' ')?;
    (scheme.eq_ignore_ascii_case("Bearer") && !token.trim().is_empty()).then(|| token.trim())
}

fn cookie_value<'a>(headers: &'a HeaderMap, name: &str) -> Option<&'a str> {
    headers
        .get(header::COOKIE)?
        .to_str()
        .ok()?
        .split(';')
        .find_map(|item| {
            let (key, value) = item.trim().split_once('=')?;
            (key == name).then_some(value)
        })
}

fn aud_string(value: &Value) -> Option<String> {
    match value {
        Value::String(item) => Some(item.clone()),
        Value::Array(items) => items.iter().find_map(Value::as_str).map(str::to_owned),
        _ => None,
    }
}

fn valid_redirect_uri(value: &str) -> bool {
    Url::parse(value).is_ok_and(|url| {
        let valid_transport =
            url.scheme() == "https" || (url.scheme() == "http" && is_loopback_host(&url));
        valid_transport
            && url.host_str().is_some()
            && url.username().is_empty()
            && url.password().is_none()
            && url.fragment().is_none()
    })
}

fn is_loopback_host(url: &Url) -> bool {
    match url.host() {
        Some(Host::Domain(host)) => host.eq_ignore_ascii_case("localhost"),
        Some(Host::Ipv4(address)) => address.is_loopback(),
        Some(Host::Ipv6(address)) => address.is_loopback(),
        None => false,
    }
}

fn same_origin(left: &str, right: &str) -> bool {
    let (Ok(left), Ok(right)) = (Url::parse(left), Url::parse(right)) else {
        return false;
    };
    left.scheme() == right.scheme()
        && left.host_str() == right.host_str()
        && left.port_or_known_default() == right.port_or_known_default()
}

fn is_pkce_verifier(value: &str) -> bool {
    value
        .bytes()
        .all(|byte| byte.is_ascii_alphanumeric() || b"-._~".contains(&byte))
}
fn is_base64url(value: &str) -> bool {
    value
        .bytes()
        .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_')
}

fn query_string(query: &HashMap<String, String>) -> String {
    let mut serializer = url::form_urlencoded::Serializer::new(String::new());
    for (key, value) in query {
        serializer.append_pair(key, value);
    }
    serializer.finish()
}

fn urlencoding_fallback(value: &str) -> String {
    url::form_urlencoded::byte_serialize(value.as_bytes()).collect()
}

#[cfg(test)]
mod tests {
    use super::{
        AuthorizationPrompt, has_duplicate_query_parameter, is_access_token_claims, openapi,
        parse_authorization_prompt, valid_redirect_uri,
    };

    #[test]
    fn redirect_uri_requires_https_except_localhost_and_parsed_loopback_addresses() {
        for uri in [
            "https://client.example/callback",
            "http://localhost/callback",
            "http://127.0.0.1:3000/callback",
            "http://[::1]:3000/callback",
        ] {
            assert!(valid_redirect_uri(uri), "expected {uri} to be allowed");
        }
        for uri in [
            "http://client.example/callback",
            "http://localhost.example/callback",
            "http://127.0.0.1.example/callback",
            "http://192.0.2.1/callback",
        ] {
            assert!(!valid_redirect_uri(uri), "expected {uri} to be rejected");
        }
    }

    #[test]
    fn authorization_prompt_parsing_rejects_unsupported_and_conflicting_values() {
        assert_eq!(
            parse_authorization_prompt(None),
            Ok(AuthorizationPrompt::Default)
        );
        assert_eq!(
            parse_authorization_prompt(Some("none")),
            Ok(AuthorizationPrompt::None)
        );
        assert_eq!(
            parse_authorization_prompt(Some("login")),
            Ok(AuthorizationPrompt::Login)
        );
        assert_eq!(
            parse_authorization_prompt(Some("consent")),
            Ok(AuthorizationPrompt::Consent)
        );
        assert_eq!(
            parse_authorization_prompt(Some("login consent")),
            Ok(AuthorizationPrompt::InteractionRequired)
        );
        assert!(parse_authorization_prompt(Some("none login")).is_err());
        assert!(parse_authorization_prompt(Some("unknown")).is_err());
        assert!(parse_authorization_prompt(Some("login login")).is_err());
        assert!(parse_authorization_prompt(Some("  ")).is_err());
        assert!(has_duplicate_query_parameter(
            "client_id=a&prompt=login&prompt=consent",
            "prompt"
        ));
        assert!(has_duplicate_query_parameter(
            "%70rompt=login&prompt=consent",
            "prompt"
        ));
        assert!(!has_duplicate_query_parameter(
            "prompt=login&client_id=a",
            "prompt"
        ));
    }

    #[test]
    fn only_explicit_access_purpose_claims_are_accepted_as_access_tokens() {
        assert!(is_access_token_claims(
            &serde_json::json!({"token_use":"access"})
        ));
        assert!(!is_access_token_claims(
            &serde_json::json!({"token_use":"id"})
        ));
        assert!(!is_access_token_claims(
            &serde_json::json!({"sub":"user","aud":"client"})
        ));
    }

    #[test]
    fn oauth_openapi_exposes_form_schemas_responses_and_basic_client_auth() {
        let document = serde_json::to_value(openapi()).expect("OpenAPI serializes");
        let token = &document["paths"]["/{slug}/oauth/token"]["post"];
        assert_eq!(
            token["requestBody"]["content"]["application/x-www-form-urlencoded"]["schema"]["$ref"],
            "#/components/schemas/OAuthTokenRequest"
        );
        assert_eq!(
            token["responses"]["200"]["content"]["application/json"]["schema"]["$ref"],
            "#/components/schemas/OAuthTokenResponse"
        );
        assert_eq!(
            document["components"]["securitySchemes"]["clientBasic"]["scheme"],
            "basic"
        );
        assert!(
            token["security"].as_array().is_some_and(|items| {
                items.iter().any(|item| item.get("clientBasic").is_some())
            })
        );
        assert!(document["components"]["schemas"]["OAuthErrorResponse"].is_object());
    }
}
