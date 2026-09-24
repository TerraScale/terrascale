//! Federated sign-in connections: OIDC, GitHub OAuth, and SAML.

pub(crate) mod saml;

use std::{
    collections::{HashMap, HashSet},
    time::{SystemTime, UNIX_EPOCH},
};

use auth_contracts::models::{Connection, VerificationToken};
use auth_security::{jwt, secretbox, tokens};
use auth_store::AuthStore;
use axum::{
    Json, Router,
    extract::{Path, Query, State},
    http::{HeaderMap, HeaderValue, StatusCode, header},
    response::{Html, IntoResponse, Redirect, Response},
    routing::{get, patch, post},
};
use base64::{Engine, engine::general_purpose::STANDARD};
use reqwest::Url;
use serde::{Deserialize, de::DeserializeOwned};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use utoipa::{OpenApi, ToSchema};

use crate::{
    AppState,
    features::{management, users},
};
use auth_contracts::{AppError, AppResult};

const STATE_TTL_SECONDS: i64 = 10 * 60;
const MAX_PROVIDER_JSON_BYTES: usize = 1024 * 1024;
const OIDC_STATE_COOKIE: &str = "tsauth_oidc_state";
const GITHUB_AUTHORIZATION: &str = "https://github.com/login/oauth/authorize";
const GITHUB_TOKEN: &str = "https://github.com/login/oauth/access_token";
const GITHUB_EMAILS: &str = "https://api.github.com/user/emails";
const DEFAULT_OIDC_SCOPES: &[&str] = &["openid", "email", "profile"];
const MAX_OIDC_SCOPES: usize = 64;
const MAX_OIDC_SCOPE_LENGTH: usize = 128;

#[derive(OpenApi)]
#[openapi(
    paths(
        create_connection, list_connections, update_connection, delete_connection,
        list_public_connections, hosted_connection_choices, authorize_oidc, complete_oidc, authorize_github,
        complete_github, saml::saml_login, saml::saml_acs
    ),
    components(schemas(CreateConnectionRequest, UpdateConnectionRequest)),
    tags((name = "Connections", description = "Upstream OIDC, GitHub OAuth, and SAML sign-in connections"))
)]
pub struct ApiDoc;

pub fn router() -> Router<AppState> {
    Router::new()
        .route(
            "/v1/{slug}/connections",
            post(create_connection).get(list_connections),
        )
        .route(
            "/v1/{slug}/connections/{id}",
            patch(update_connection).delete(delete_connection),
        )
        .route("/ui/{slug}/connections", get(list_public_connections))
        .route(
            "/ui/{slug}/connections/list",
            get(hosted_connection_choices),
        )
        .route(
            "/ui/{slug}/connections/{connection_id}/oidc/authorize",
            get(authorize_oidc),
        )
        .route(
            "/ui/{slug}/connections/{connection_id}/oidc/callback",
            get(complete_oidc),
        )
        .route(
            "/ui/{slug}/connections/{connection_id}/github/authorize",
            get(authorize_github),
        )
        .route(
            "/ui/{slug}/connections/{connection_id}/github/callback",
            get(complete_github),
        )
        .route(
            "/ui/{slug}/connections/{connection_id}/saml/login",
            get(saml::saml_login),
        )
        .route(
            "/{slug}/connections/{connection_id}/saml/acs",
            post(saml::saml_acs),
        )
}

pub fn openapi() -> utoipa::openapi::OpenApi {
    ApiDoc::openapi()
}

#[derive(Debug, Deserialize, ToSchema)]
pub struct CreateConnectionRequest {
    pub r#type: Option<String>,
    pub name: Option<String>,
    #[serde(rename = "org_id")]
    pub org_id: Option<String>,
    #[serde(rename = "issuer_url")]
    pub issuer_url: Option<String>,
    #[serde(rename = "client_id")]
    pub client_id: Option<String>,
    #[serde(rename = "client_secret")]
    pub client_secret: Option<String>,
    pub scopes: Option<Vec<String>>,
    #[serde(rename = "authorization_endpoint")]
    pub authorization_endpoint: Option<String>,
    #[serde(rename = "token_endpoint")]
    pub token_endpoint: Option<String>,
    #[serde(rename = "userinfo_endpoint")]
    pub userinfo_endpoint: Option<String>,
    #[serde(rename = "idp_entity_id")]
    pub idp_entity_id: Option<String>,
    #[serde(rename = "sso_redirect_url")]
    pub sso_redirect_url: Option<String>,
    #[serde(rename = "x509_certificate_pem")]
    pub x509_certificate_pem: Option<String>,
    #[serde(rename = "sp_entity_id")]
    pub sp_entity_id: Option<String>,
}

#[derive(Debug, Deserialize, ToSchema)]
pub struct UpdateConnectionRequest {
    pub enabled: Option<bool>,
    pub name: Option<String>,
    #[serde(rename = "client_secret")]
    pub client_secret: Option<String>,
}

#[utoipa::path(
    post,
    path = "/v1/{slug}/connections",
    params(("slug" = String, Path)),
    request_body = CreateConnectionRequest,
    responses((status = 201, description = "Connection created"), (status = 400, description = "Invalid provider configuration"), (status = 401, description = "Management API key required")),
    tag = "Connections"
)]
async fn create_connection(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Json(body): Json<CreateConnectionRequest>,
) -> AppResult<Response> {
    let context = match management::require(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(error) => return Ok(error.into_response()),
    };
    let Some(name) = body
        .name
        .as_deref()
        .filter(|value| !value.trim().is_empty())
    else {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "name is required",
        ));
    };
    if body.scopes.is_some() && body.r#type.as_deref() != Some("oidc") {
        return Ok(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "scopes may only be configured for OIDC connections",
        ));
    }
    let connection = match body.r#type.as_deref() {
        Some("oidc") => {
            match create_oidc_connection(&state, &context.instance.id, name, &body).await {
                Ok(connection) => connection,
                Err(response) => return Ok(response),
            }
        }
        Some("github") => {
            match create_github_connection(&state, &context.instance.id, name, &body).await {
                Ok(connection) => connection,
                Err(response) => return Ok(response),
            }
        }
        Some("saml") => match create_saml_connection(&state, &context.instance.id, name, &body) {
            Ok(connection) => connection,
            Err(response) => return Ok(response),
        },
        _ => {
            return Ok(api_error(
                StatusCode::BAD_REQUEST,
                "invalid_request",
                "type must be oidc, github, or saml",
            ));
        }
    };
    match state.store.create_connection(&connection).await {
        Ok(_) => Ok((StatusCode::CREATED, Json(connection_view(&connection))).into_response()),
        Err(error) if error.is_conflict() => Ok(api_error(
            StatusCode::CONFLICT,
            "connection_exists",
            "a connection with this id already exists",
        )),
        Err(error) => Err(internal(error)),
    }
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
async fn create_oidc_connection(
    state: &AppState,
    instance_id: &str,
    name: &str,
    body: &CreateConnectionRequest,
) -> Result<Connection, Response> {
    let scopes = normalize_oidc_scopes(body.scopes.as_deref()).map_err(|()| {
        api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "scopes must contain openid and email and use valid OIDC scope tokens",
        )
    })?;
    let Some(raw_issuer) = body.issuer_url.as_deref() else {
        return Err(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "issuer_url must be an absolute http(s) URL",
        ));
    };
    let Some(issuer) = valid_public_https_url(raw_issuer) else {
        return Err(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "issuer_url must be an absolute http(s) URL",
        ));
    };
    let (Some(client_id), Some(client_secret)) =
        (body.client_id.as_deref(), body.client_secret.as_deref())
    else {
        return Err(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "client_id and client_secret are required",
        ));
    };
    if client_id.trim().is_empty() || client_secret.is_empty() {
        return Err(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "client_id and client_secret are required",
        ));
    }
    let discovery = match fetch_discovery(&issuer).await {
        Ok(Some(value)) => value,
        _ => {
            return Err(api_error(
                StatusCode::BAD_REQUEST,
                "discovery_failed",
                "the issuer did not provide valid OpenID discovery metadata",
            ));
        }
    };
    if discovery.issuer.as_deref() != Some(issuer.as_str()) {
        return Err(api_error(
            StatusCode::BAD_REQUEST,
            "discovery_failed",
            "discovery issuer does not match issuer_url",
        ));
    }
    let authorization_url = match body
        .authorization_endpoint
        .as_deref()
        .or(discovery.authorization_endpoint.as_deref())
        .and_then(valid_public_https_url)
    {
        Some(value) => value.to_string(),
        None => {
            return Err(api_error(
                StatusCode::BAD_REQUEST,
                "discovery_failed",
                "could not resolve a valid authorization endpoint",
            ));
        }
    };
    let token_endpoint_url = match body
        .token_endpoint
        .as_deref()
        .or(discovery.token_endpoint.as_deref())
        .and_then(valid_public_https_url)
    {
        Some(value) => value.to_string(),
        None => {
            return Err(api_error(
                StatusCode::BAD_REQUEST,
                "discovery_failed",
                "could not resolve a valid token endpoint",
            ));
        }
    };
    let userinfo_url = body
        .userinfo_endpoint
        .as_deref()
        .or(discovery.userinfo_endpoint.as_deref())
        .and_then(valid_public_https_url)
        .map(|value| value.to_string());
    for endpoint in std::iter::once(&authorization_url)
        .chain(std::iter::once(&token_endpoint_url))
        .chain(userinfo_url.iter())
    {
        let Some(url) = valid_public_https_url(endpoint) else {
            return Err(api_error(
                StatusCode::BAD_REQUEST,
                "discovery_failed",
                "provider endpoints must use public HTTPS URLs",
            ));
        };
        if crate::features::admin::pinned_public_client(&url)
            .await
            .is_err()
        {
            return Err(api_error(
                StatusCode::BAD_REQUEST,
                "discovery_failed",
                "provider endpoints must resolve to public addresses",
            ));
        }
    }
    let secret_box = secretbox::encrypt(&state.options.master_key, client_secret.as_bytes())
        .map_err(|_| {
            api_error(
                StatusCode::INTERNAL_SERVER_ERROR,
                "internal_error",
                "could not secure provider credentials",
            )
        })?;
    Ok(Connection {
        id: format!("con_{}", tokens::random_token(20)),
        instance_id: instance_id.to_owned(),
        r#type: "oidc".into(),
        name: name.trim().to_owned(),
        scopes,
        enabled: true,
        org_id: clean_option(body.org_id.as_deref()),
        issuer_url: Some(issuer.to_string()),
        client_id: Some(client_id.to_owned()),
        client_secret_box: Some(secret_box),
        authorization_endpoint: Some(authorization_url),
        token_endpoint: Some(token_endpoint_url),
        userinfo_endpoint: userinfo_url,
        idp_entity_id: None,
        sso_redirect_url: None,
        x509_certificate_pem: None,
        sp_entity_id: clean_option(body.sp_entity_id.as_deref()),
        created_at: now(),
    })
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
async fn create_github_connection(
    state: &AppState,
    instance_id: &str,
    name: &str,
    body: &CreateConnectionRequest,
) -> Result<Connection, Response> {
    let (Some(client_id), Some(client_secret)) =
        (body.client_id.as_deref(), body.client_secret.as_deref())
    else {
        return Err(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "client_id and client_secret are required",
        ));
    };
    if client_id.trim().is_empty() || client_secret.is_empty() {
        return Err(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "client_id and client_secret are required",
        ));
    }
    let authorization_endpoint =
        configured_github_endpoint(body.authorization_endpoint.as_deref(), GITHUB_AUTHORIZATION)?;
    let token_endpoint = configured_github_endpoint(body.token_endpoint.as_deref(), GITHUB_TOKEN)?;
    let userinfo_endpoint =
        configured_github_endpoint(body.userinfo_endpoint.as_deref(), GITHUB_EMAILS)?;
    for endpoint in [&authorization_endpoint, &token_endpoint, &userinfo_endpoint] {
        let Some(url) = valid_public_https_url(endpoint) else {
            return Err(api_error(
                StatusCode::BAD_REQUEST,
                "invalid_request",
                "GitHub endpoints must use public HTTPS URLs",
            ));
        };
        if crate::features::admin::pinned_public_client(&url)
            .await
            .is_err()
        {
            return Err(api_error(
                StatusCode::BAD_REQUEST,
                "invalid_request",
                "GitHub endpoints must resolve to public addresses",
            ));
        }
    }
    let secret_box = secretbox::encrypt(&state.options.master_key, client_secret.as_bytes())
        .map_err(|_| {
            api_error(
                StatusCode::INTERNAL_SERVER_ERROR,
                "internal_error",
                "could not secure provider credentials",
            )
        })?;
    Ok(Connection {
        id: format!("con_{}", tokens::random_token(20)),
        instance_id: instance_id.to_owned(),
        r#type: "github".into(),
        name: name.trim().to_owned(),
        scopes: Vec::new(),
        enabled: true,
        org_id: clean_option(body.org_id.as_deref()),
        issuer_url: None,
        client_id: Some(client_id.to_owned()),
        client_secret_box: Some(secret_box),
        authorization_endpoint: Some(authorization_endpoint),
        token_endpoint: Some(token_endpoint),
        userinfo_endpoint: Some(userinfo_endpoint),
        idp_entity_id: None,
        sso_redirect_url: None,
        x509_certificate_pem: None,
        sp_entity_id: None,
        created_at: now(),
    })
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
fn configured_github_endpoint(value: Option<&str>, default: &str) -> Result<String, Response> {
    match value {
        Some(value) => valid_public_https_url(value)
            .map(|url| url.to_string())
            .ok_or_else(|| {
                api_error(
                    StatusCode::BAD_REQUEST,
                    "invalid_request",
                    "GitHub endpoints must use valid public HTTPS URLs",
                )
            }),
        None => Ok(default.to_owned()),
    }
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
fn create_saml_connection(
    _state: &AppState,
    instance_id: &str,
    name: &str,
    body: &CreateConnectionRequest,
) -> Result<Connection, Response> {
    let (Some(entity_id), Some(sso_url), Some(certificate)) = (
        body.idp_entity_id.as_deref(),
        body.sso_redirect_url.as_deref(),
        body.x509_certificate_pem.as_deref(),
    ) else {
        return Err(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "idp_entity_id, sso_redirect_url, and x509_certificate_pem are required",
        ));
    };
    if entity_id.trim().is_empty()
        || valid_public_https_url(sso_url).is_none()
        || certificate_body(certificate).is_none()
    {
        return Err(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_certificate",
            "SAML IdP entity id, SSO URL, or certificate is invalid",
        ));
    }
    let connection = Connection {
        id: format!("con_{}", tokens::random_token(20)),
        instance_id: instance_id.to_owned(),
        r#type: "saml".into(),
        name: name.trim().to_owned(),
        scopes: Vec::new(),
        enabled: true,
        org_id: clean_option(body.org_id.as_deref()),
        issuer_url: None,
        client_id: None,
        client_secret_box: None,
        authorization_endpoint: None,
        token_endpoint: None,
        userinfo_endpoint: None,
        idp_entity_id: Some(entity_id.trim().to_owned()),
        sso_redirect_url: Some(sso_url.to_owned()),
        x509_certificate_pem: Some(certificate.to_owned()),
        sp_entity_id: clean_option(body.sp_entity_id.as_deref()),
        created_at: now(),
    };
    if saml::idp_descriptor(&connection).is_err() {
        return Err(api_error(
            StatusCode::BAD_REQUEST,
            "invalid_certificate",
            "x509_certificate_pem is not a parsable PEM certificate",
        ));
    }
    Ok(connection)
}

#[utoipa::path(
    get,
    path = "/v1/{slug}/connections",
    params(("slug" = String, Path)),
    responses((status = 200, description = "Instance connections"), (status = 401, description = "Management API key required")),
    tag = "Connections"
)]
async fn list_connections(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
) -> AppResult<Response> {
    let context = match management::require(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(error) => return Ok(error.into_response()),
    };
    let mut connections = state
        .store
        .list_connections(&context.instance.id)
        .await
        .map_err(internal)?;
    connections.sort_by_key(|item| item.created_at);
    Ok((
        StatusCode::OK,
        Json(json!({"data":connections.iter().map(connection_view).collect::<Vec<_>>()})),
    )
        .into_response())
}

#[utoipa::path(
    patch,
    path = "/v1/{slug}/connections/{id}",
    params(("slug" = String, Path), ("id" = String, Path)),
    request_body = UpdateConnectionRequest,
    responses((status = 200, description = "Connection updated"), (status = 401, description = "Management API key required"), (status = 404, description = "Connection not found")),
    tag = "Connections"
)]
async fn update_connection(
    State(state): State<AppState>,
    Path((slug, id)): Path<(String, String)>,
    headers: HeaderMap,
    Json(body): Json<UpdateConnectionRequest>,
) -> AppResult<Response> {
    let context = match management::require(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(error) => return Ok(error.into_response()),
    };
    let Some(connection) = state.store.get_connection(&id).await.map_err(internal)? else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "connection_not_found",
            "no connection with this id in this instance",
        ));
    };
    if connection.instance_id != context.instance.id {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "connection_not_found",
            "no connection with this id in this instance",
        ));
    }
    let name = if let Some(name) = body.name {
        if name.trim().is_empty() {
            return Ok(api_error(
                StatusCode::BAD_REQUEST,
                "invalid_request",
                "name must not be empty",
            ));
        }
        Some(name.trim().to_owned())
    } else {
        None
    };
    let secret_box = if let Some(secret) = body.client_secret {
        if secret.trim().is_empty() {
            return Ok(api_error(
                StatusCode::BAD_REQUEST,
                "invalid_request",
                "client_secret must not be empty",
            ));
        }
        Some(
            secretbox::encrypt(&state.options.master_key, secret.as_bytes())
                .map_err(|_| AppError::internal())?,
        )
    } else {
        None
    };
    let enabled = body.enabled;
    let instance_id = context.instance.id;
    let updated = state
        .store
        .update_connection(&id, |current| {
            if current.instance_id != instance_id {
                return;
            }
            if let Some(name) = &name {
                current.name.clone_from(name);
            }
            if let Some(enabled) = enabled {
                current.enabled = enabled;
            }
            if let Some(secret_box) = &secret_box {
                current.client_secret_box = Some(secret_box.clone());
            }
        })
        .await
        .map_err(internal)?;
    let Some(connection) = updated.filter(|connection| connection.instance_id == instance_id)
    else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "connection_not_found",
            "no connection with this id in this instance",
        ));
    };
    Ok((StatusCode::OK, Json(connection_view(&connection))).into_response())
}

#[utoipa::path(
    delete,
    path = "/v1/{slug}/connections/{id}",
    params(("slug" = String, Path), ("id" = String, Path)),
    responses((status = 204, description = "Connection deleted"), (status = 401, description = "Management API key required"), (status = 404, description = "Connection not found")),
    tag = "Connections"
)]
async fn delete_connection(
    State(state): State<AppState>,
    Path((slug, id)): Path<(String, String)>,
    headers: HeaderMap,
) -> AppResult<Response> {
    let context = match management::require(&state, &headers, &slug).await {
        Ok(value) => value,
        Err(error) => return Ok(error.into_response()),
    };
    let Some(connection) = state.store.get_connection(&id).await.map_err(internal)? else {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "connection_not_found",
            "no connection with this id in this instance",
        ));
    };
    if connection.instance_id != context.instance.id {
        return Ok(api_error(
            StatusCode::NOT_FOUND,
            "connection_not_found",
            "no connection with this id in this instance",
        ));
    }
    state.store.delete_connection(&id).await.map_err(internal)?;
    Ok(StatusCode::NO_CONTENT.into_response())
}

#[utoipa::path(
    get,
    path = "/ui/{slug}/connections",
    params(("slug" = String, Path)),
    responses((status = 200, description = "Enabled sign-in connections"), (status = 404, description = "Instance not found")),
    tag = "Connections"
)]
async fn list_public_connections(
    State(state): State<AppState>,
    Path(slug): Path<String>,
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
    let mut connections = state
        .store
        .list_connections(&instance.id)
        .await
        .map_err(internal)?;
    connections.retain(|item| item.enabled);
    connections.sort_by_key(|item| item.created_at);
    Ok((
        StatusCode::OK,
        Json(
            connections
                .iter()
                .map(|item| json!({"id":item.id,"type":item.r#type,"name":item.name}))
                .collect::<Vec<_>>(),
        ),
    )
        .into_response())
}

#[utoipa::path(
    get,
    path = "/ui/{slug}/connections/list",
    params(("slug" = String, Path, description = "Instance slug")),
    responses((status = 200, description = "Hosted sign-in choices"), (status = 404, description = "Instance not found")),
    tag = "Connections"
)]
async fn hosted_connection_choices(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    Query(query): Query<ConnectionNextQuery>,
) -> Response {
    let instance = match state.store.find_instance_by_slug(&slug).await {
        Ok(Some(value)) => value,
        Ok(None) => return not_found_page(),
        Err(error) => return internal(error).into_response(),
    };
    let mut connections = match state.store.list_connections(&instance.id).await {
        Ok(value) => value,
        Err(error) => return internal(error).into_response(),
    };
    connections.retain(|connection| connection.enabled);
    connections.sort_by_key(|connection| connection.created_at);
    let next = users::validated_authentication_next(
        &state.options.public_origin,
        &slug,
        query.next.as_deref(),
    );
    let next_input = next.as_deref().map_or_else(String::new, |next| {
        format!(
            "<input type=\"hidden\" name=\"next\" value=\"{}\">",
            crate::features::ui::escape_html(next)
        )
    });
    let buttons = connections
        .iter()
        .filter_map(|connection| {
            let flow = match connection.r#type.as_str() {
                "oidc" => "oidc/authorize",
                "github" => "github/authorize",
                "saml" => "saml/login",
                _ => return None,
            };
            let action = format!(
                "/ui/{}/connections/{}/{flow}",
                ui_path_segment(&slug),
                ui_path_segment(&connection.id),
            );
            Some(format!(
                "<form method=\"get\" action=\"{}\">{next_input}<button type=\"submit\">Continue with {}</button></form>",
                crate::features::ui::escape_html(&action),
                crate::features::ui::escape_html(&connection.name),
            ))
        })
        .collect::<Vec<_>>()
        .join("\n");
    let choices = if buttons.is_empty() {
        "<p class=\"muted\">No sign-in options are configured.</p>".to_owned()
    } else {
        format!("<div class=\"list\">{buttons}</div>")
    };
    let safe_slug = crate::features::ui::escape_html(&ui_path_segment(&slug));
    let body = format!(
        "<h1>Sign in to {}</h1>{choices}<p class=\"hint\"><a href=\"/ui/{safe_slug}/sign-in\">Back to sign in</a></p>",
        crate::features::ui::escape_html(&instance.name),
    );
    crate::features::ui::page(
        "Sign in",
        &body,
        None,
        StatusCode::OK,
        instance.theme_color.as_deref(),
        instance.logo_url.as_deref(),
    )
}

#[derive(Debug, Default, Deserialize)]
struct ConnectionNextQuery {
    next: Option<String>,
}

fn ui_path_segment(value: &str) -> String {
    let mut url = Url::parse("https://ts-auth.invalid/").expect("static base URL is valid");
    url.path_segments_mut()
        .expect("base URL supports path segments")
        .push(value);
    url.path().trim_start_matches('/').to_owned()
}

#[utoipa::path(
    get,
    path = "/ui/{slug}/connections/{connection_id}/oidc/authorize",
    params(("slug" = String, Path), ("connection_id" = String, Path)),
    responses((status = 303, description = "Redirect to upstream OpenID Provider"), (status = 404, description = "Connection not found")),
    tag = "Connections"
)]
async fn authorize_oidc(
    State(state): State<AppState>,
    Path((slug, connection_id)): Path<(String, String)>,
    Query(query): Query<ConnectionNextQuery>,
) -> AppResult<Response> {
    let Some(instance) = state
        .store
        .find_instance_by_slug(&slug)
        .await
        .map_err(internal)?
    else {
        return Ok(not_found_page());
    };
    let Some(connection) =
        find_connection(&state.store, &instance.id, &connection_id, "oidc").await?
    else {
        return Ok(not_found_page());
    };
    let (Some(endpoint), Some(client_id)) = (
        connection
            .authorization_endpoint
            .as_deref()
            .and_then(valid_public_https_url),
        connection.client_id.as_deref(),
    ) else {
        return Ok(connection_error(
            &slug,
            "This OpenID connection is incomplete.",
            StatusCode::BAD_REQUEST,
        ));
    };
    let next = users::validated_authentication_next(
        &state.options.public_origin,
        &slug,
        query.next.as_deref(),
    );
    let (state_value, nonce, verifier) =
        mint_oidc_state(&state, &instance.id, &connection.id, next.as_deref()).await?;
    let challenge = base64::engine::general_purpose::URL_SAFE_NO_PAD
        .encode(Sha256::digest(verifier.as_bytes()));
    let mut target = endpoint;
    target
        .query_pairs_mut()
        .append_pair("client_id", client_id)
        .append_pair(
            "redirect_uri",
            &oidc_callback_url(&state, &slug, &connection.id),
        )
        .append_pair("response_type", "code")
        .append_pair("scope", &connection.scopes.join(" "))
        .append_pair("state", &state_value)
        .append_pair("nonce", &nonce)
        .append_pair("code_challenge", &challenge)
        .append_pair("code_challenge_method", "S256");
    let mut response = Redirect::to(target.as_str()).into_response();
    append_oidc_state_cookie(&mut response, &slug, &connection.id, &state_value);
    Ok(response)
}

#[utoipa::path(
    get,
    path = "/ui/{slug}/connections/{connection_id}/oidc/callback",
    params(("slug" = String, Path), ("connection_id" = String, Path), ("state" = String, Query), ("code" = Option<String>, Query)),
    responses((status = 303, description = "Successful sign-in redirects to the hosted account"), (status = 400, description = "State or upstream identity is invalid"), (status = 502, description = "Upstream provider failed")),
    tag = "Connections"
)]
async fn complete_oidc(
    State(state): State<AppState>,
    Path((slug, connection_id)): Path<(String, String)>,
    Query(query): Query<HashMap<String, String>>,
    headers: HeaderMap,
) -> AppResult<Response> {
    let Some(instance) = state
        .store
        .find_instance_by_slug(&slug)
        .await
        .map_err(internal)?
    else {
        let mut response = not_found_page();
        clear_oidc_state_cookie(&mut response, &slug, &connection_id);
        return Ok(response);
    };
    let state_value = query.get("state").map(String::as_str).unwrap_or_default();
    let expected_cookie = cookie_value(&headers, OIDC_STATE_COOKIE);
    let valid_cookie = expected_cookie
        .is_some_and(|value| tokens::fixed_time_equals(value, &tokens::sha256_hex(state_value)));
    if !valid_cookie {
        let mut response = connection_error(
            &slug,
            "This sign-in attempt is invalid, expired, or already used.",
            StatusCode::BAD_REQUEST,
        );
        clear_oidc_state_cookie(&mut response, &slug, &connection_id);
        return Ok(response);
    }
    let Some(record) = consume_state(
        &state.store,
        &instance.id,
        &connection_id,
        state_value,
        "oidc.state",
    )
    .await?
    else {
        let mut response = connection_error(
            &slug,
            "This sign-in attempt is invalid, expired, or already used.",
            StatusCode::BAD_REQUEST,
        );
        clear_oidc_state_cookie(&mut response, &slug, &connection_id);
        return Ok(response);
    };
    let Some(connection) =
        find_connection(&state.store, &instance.id, &connection_id, "oidc").await?
    else {
        let mut response = not_found_page();
        clear_oidc_state_cookie(&mut response, &slug, &connection_id);
        return Ok(response);
    };
    if query.contains_key("error") {
        let mut response = connection_error(
            &slug,
            "The identity provider rejected this sign-in.",
            StatusCode::BAD_REQUEST,
        );
        clear_oidc_state_cookie(&mut response, &slug, &connection_id);
        return Ok(response);
    }
    let Some(code) = query.get("code").filter(|value| !value.is_empty()) else {
        let mut response = connection_error(
            &slug,
            "The identity provider did not return an authorization code.",
            StatusCode::BAD_REQUEST,
        );
        clear_oidc_state_cookie(&mut response, &slug, &connection_id);
        return Ok(response);
    };
    let state_data = record
        .email_snapshot
        .as_deref()
        .and_then(|boxed| secretbox::decrypt(&state.options.master_key, boxed).ok())
        .and_then(|decoded| serde_json::from_slice::<OidcStateSecrets>(&decoded).ok());
    let Some(state_data) = state_data else {
        let mut response = connection_error(
            &slug,
            "This sign-in attempt is invalid, expired, or already used.",
            StatusCode::BAD_REQUEST,
        );
        clear_oidc_state_cookie(&mut response, &slug, &connection_id);
        return Ok(response);
    };
    let result = complete_oidc_flow(
        &state,
        OidcFlowInput {
            instance: &instance,
            connection: &connection,
            slug: &slug,
            nonce: &state_data.nonce,
            verifier: &state_data.verifier,
            code,
            headers: &headers,
            continuation: record.authentication_next.as_deref(),
        },
    )
    .await;
    match result {
        Ok(mut response) => {
            clear_oidc_state_cookie(&mut response, &slug, &connection_id);
            Ok(response)
        }
        Err(UpstreamFailure::Invalid(message)) => {
            let mut response = connection_error(&slug, message, StatusCode::BAD_REQUEST);
            clear_oidc_state_cookie(&mut response, &slug, &connection_id);
            Ok(response)
        }
        Err(UpstreamFailure::Unavailable(message)) => {
            let mut response = connection_error(&slug, message, StatusCode::BAD_GATEWAY);
            clear_oidc_state_cookie(&mut response, &slug, &connection_id);
            Ok(response)
        }
    }
}

#[derive(Deserialize)]
struct OidcStateSecrets {
    nonce: String,
    verifier: String,
}

struct OidcFlowInput<'a> {
    instance: &'a auth_contracts::models::Instance,
    connection: &'a Connection,
    slug: &'a str,
    nonce: &'a str,
    verifier: &'a str,
    code: &'a str,
    headers: &'a HeaderMap,
    continuation: Option<&'a str>,
}

async fn complete_oidc_flow(
    state: &AppState,
    input: OidcFlowInput<'_>,
) -> Result<Response, UpstreamFailure> {
    let OidcFlowInput {
        instance,
        connection,
        slug,
        nonce,
        verifier,
        code,
        headers,
        continuation,
    } = input;
    let (Some(issuer), Some(token_endpoint), Some(client_id), Some(secret_box)) = (
        connection.issuer_url.as_deref(),
        connection.token_endpoint.as_deref(),
        connection.client_id.as_deref(),
        connection.client_secret_box.as_deref(),
    ) else {
        return Err(UpstreamFailure::Invalid(
            "The OpenID connection is incomplete.",
        ));
    };
    let secret = secretbox::decrypt(&state.options.master_key, secret_box)
        .map_err(|_| UpstreamFailure::Invalid("The OpenID credentials could not be read."))?;
    let secret = String::from_utf8(secret)
        .map_err(|_| UpstreamFailure::Invalid("The OpenID credentials could not be read."))?;
    let token_endpoint = valid_public_https_url(token_endpoint).ok_or(UpstreamFailure::Invalid(
        "The OpenID token endpoint URL is invalid.",
    ))?;
    let token_http = crate::features::admin::pinned_public_client(&token_endpoint)
        .await
        .map_err(|_| {
            UpstreamFailure::Unavailable("The identity-provider token endpoint is unavailable.")
        })?;
    let token_response = token_http
        .post(token_endpoint.clone())
        .form(&[
            ("grant_type", "authorization_code"),
            ("code", code),
            (
                "redirect_uri",
                &oidc_callback_url(state, slug, &connection.id),
            ),
            ("client_id", client_id),
            ("client_secret", secret.as_str()),
            ("code_verifier", verifier),
        ])
        .send()
        .await
        .map_err(|_| UpstreamFailure::Unavailable("Contacting the identity provider failed."))?;
    if !token_response.status().is_success() {
        return Err(UpstreamFailure::Unavailable(
            "Exchanging the authorization code failed.",
        ));
    }
    let token: Value = read_provider_json(token_response).await.map_err(|_| {
        UpstreamFailure::Unavailable("The identity provider returned an invalid token response.")
    })?;
    let Some(id_token) = token
        .get("id_token")
        .and_then(Value::as_str)
        .filter(|value| !value.is_empty())
    else {
        return Err(UpstreamFailure::Unavailable(
            "The identity provider did not return an id_token.",
        ));
    };
    let issuer_url = valid_public_https_url(issuer).ok_or(UpstreamFailure::Invalid(
        "The OpenID issuer URL is invalid.",
    ))?;
    let discovery = fetch_discovery(&issuer_url)
        .await
        .map_err(|_| {
            UpstreamFailure::Unavailable("Fetching identity-provider discovery metadata failed.")
        })?
        .ok_or(UpstreamFailure::Unavailable(
            "Fetching identity-provider discovery metadata failed.",
        ))?;
    if discovery.issuer.as_deref() != Some(issuer) {
        return Err(UpstreamFailure::Invalid(
            "The identity-provider issuer does not match this connection.",
        ));
    }
    let Some(jwks_uri) = discovery
        .jwks_uri
        .as_deref()
        .and_then(valid_public_https_url)
    else {
        return Err(UpstreamFailure::Unavailable(
            "The identity provider has no valid JWKS endpoint.",
        ));
    };
    let jwks_http = crate::features::admin::pinned_public_client(&jwks_uri)
        .await
        .map_err(|_| {
            UpstreamFailure::Unavailable(
                "The identity-provider signing keys endpoint is unavailable.",
            )
        })?;
    let jwks_response = jwks_http.get(jwks_uri).send().await.map_err(|_| {
        UpstreamFailure::Unavailable("Fetching identity-provider signing keys failed.")
    })?;
    if !jwks_response.status().is_success() {
        return Err(UpstreamFailure::Unavailable(
            "Fetching identity-provider signing keys failed.",
        ));
    }
    let jwks: Value = read_provider_json(jwks_response).await.map_err(|_| {
        UpstreamFailure::Unavailable("The identity provider returned invalid signing keys.")
    })?;
    let Some(keys) = jwks.get("keys").and_then(Value::as_array) else {
        return Err(UpstreamFailure::Unavailable(
            "The identity provider returned invalid signing keys.",
        ));
    };
    let claims = verify_oidc_id_token(id_token, keys, issuer, client_id, nonce).ok_or(
        UpstreamFailure::Invalid(
            "The id_token signature or required claims could not be verified.",
        ),
    )?;
    let subject = claims
        .get("sub")
        .and_then(Value::as_str)
        .filter(|value| !value.is_empty())
        .ok_or(UpstreamFailure::Invalid("The id_token has no subject."))?;
    let mut email = claims
        .get("email")
        .and_then(Value::as_str)
        .map(str::to_owned);
    let mut email_verified = claims.get("email_verified").and_then(Value::as_bool);
    let mut name = claims
        .get("name")
        .and_then(Value::as_str)
        .map(str::to_owned);
    if email.is_none() {
        let (Some(userinfo_endpoint), Some(access_token)) = (
            connection
                .userinfo_endpoint
                .as_deref()
                .and_then(valid_public_https_url),
            token.get("access_token").and_then(Value::as_str),
        ) else {
            return Err(UpstreamFailure::Invalid(
                "The identity provider did not provide an email address.",
            ));
        };
        let userinfo_http = crate::features::admin::pinned_public_client(&userinfo_endpoint)
            .await
            .map_err(|_| {
                UpstreamFailure::Unavailable(
                    "The identity-provider user profile endpoint is unavailable.",
                )
            })?;
        let profile = userinfo_http
            .get(userinfo_endpoint)
            .bearer_auth(access_token)
            .send()
            .await
            .map_err(|_| UpstreamFailure::Unavailable("Fetching the user profile failed."))?;
        if !profile.status().is_success() {
            return Err(UpstreamFailure::Unavailable(
                "Fetching the user profile failed.",
            ));
        }
        let profile: Value = read_provider_json(profile).await.map_err(|_| {
            UpstreamFailure::Unavailable("The identity provider returned an invalid user profile.")
        })?;
        if profile.get("sub").and_then(Value::as_str) != Some(subject) {
            return Err(UpstreamFailure::Invalid(
                "The user profile subject does not match the id_token.",
            ));
        }
        email = profile
            .get("email")
            .and_then(Value::as_str)
            .map(str::to_owned);
        email_verified = profile.get("email_verified").and_then(Value::as_bool);
        name = name.or_else(|| {
            profile
                .get("name")
                .and_then(Value::as_str)
                .map(str::to_owned)
        });
    }
    let Some(email) = email else {
        return Err(UpstreamFailure::Invalid(
            "The identity provider did not provide an email address.",
        ));
    };
    if email_verified != Some(true) {
        return Err(UpstreamFailure::Invalid(
            "The identity provider did not confirm this email address.",
        ));
    }
    finish_federated_sign_in(
        state,
        instance,
        slug,
        headers,
        &email,
        name.as_deref(),
        &format!("oidc:{}", connection.id),
        continuation,
    )
    .await
    .map_err(|_| {
        UpstreamFailure::Invalid("Could not create or access an account for this identity.")
    })
}

#[utoipa::path(
    get,
    path = "/ui/{slug}/connections/{connection_id}/github/authorize",
    params(("slug" = String, Path), ("connection_id" = String, Path)),
    responses((status = 303, description = "Redirect to GitHub"), (status = 404, description = "Connection not found")),
    tag = "Connections"
)]
async fn authorize_github(
    State(state): State<AppState>,
    Path((slug, connection_id)): Path<(String, String)>,
    Query(query): Query<ConnectionNextQuery>,
) -> AppResult<Response> {
    let Some(instance) = state
        .store
        .find_instance_by_slug(&slug)
        .await
        .map_err(internal)?
    else {
        return Ok(not_found_page());
    };
    let Some(connection) =
        find_connection(&state.store, &instance.id, &connection_id, "github").await?
    else {
        return Ok(not_found_page());
    };
    let (Some(endpoint), Some(client_id)) = (
        connection
            .authorization_endpoint
            .as_deref()
            .and_then(valid_public_https_url),
        connection.client_id.as_deref(),
    ) else {
        return Ok(connection_error(
            &slug,
            "This GitHub connection is incomplete.",
            StatusCode::BAD_REQUEST,
        ));
    };
    if crate::features::admin::pinned_public_client(&endpoint)
        .await
        .is_err()
    {
        return Ok(connection_error(
            &slug,
            "This GitHub authorization endpoint is unavailable.",
            StatusCode::BAD_GATEWAY,
        ));
    }
    let next = users::validated_authentication_next(
        &state.options.public_origin,
        &slug,
        query.next.as_deref(),
    );
    let state_value = mint_state(
        &state.store,
        &instance.id,
        &connection.id,
        "github.state",
        None,
        next.as_deref(),
    )
    .await?;
    let mut target = endpoint;
    target
        .query_pairs_mut()
        .append_pair("client_id", client_id)
        .append_pair(
            "redirect_uri",
            &github_callback_url(&state, &slug, &connection.id),
        )
        .append_pair("scope", "user:email")
        .append_pair("state", &state_value);
    let mut response = Redirect::to(target.as_str()).into_response();
    append_state_cookie(&mut response, &slug, &connection.id, &state_value);
    Ok(response)
}

#[utoipa::path(
    get,
    path = "/ui/{slug}/connections/{connection_id}/github/callback",
    params(("slug" = String, Path), ("connection_id" = String, Path), ("state" = String, Query), ("code" = Option<String>, Query)),
    responses((status = 303, description = "Successful sign-in redirects to the hosted account"), (status = 400, description = "State or GitHub identity is invalid"), (status = 502, description = "GitHub is unavailable")),
    tag = "Connections"
)]
async fn complete_github(
    State(state): State<AppState>,
    Path((slug, connection_id)): Path<(String, String)>,
    Query(query): Query<HashMap<String, String>>,
    headers: HeaderMap,
) -> AppResult<Response> {
    let Some(instance) = state
        .store
        .find_instance_by_slug(&slug)
        .await
        .map_err(internal)?
    else {
        return Ok(not_found_page());
    };
    let state_value = query.get("state").map(String::as_str).unwrap_or_default();
    let expected = cookie_value(&headers, "tsauth_github_state");
    let valid_cookie = expected
        .is_some_and(|value| tokens::fixed_time_equals(value, &tokens::sha256_hex(state_value)));
    let mut invalid = connection_error(
        &slug,
        "This sign-in attempt is invalid, expired, or already used.",
        StatusCode::BAD_REQUEST,
    );
    clear_state_cookie(&mut invalid, &slug, &connection_id);
    if !valid_cookie {
        return Ok(invalid);
    }
    let record = consume_state(
        &state.store,
        &instance.id,
        &connection_id,
        state_value,
        "github.state",
    )
    .await?;
    let Some(record) = record else {
        return Ok(invalid);
    };
    let Some(connection) =
        find_connection(&state.store, &instance.id, &connection_id, "github").await?
    else {
        return Ok(not_found_page());
    };
    let Some(code) = query.get("code").filter(|value| !value.is_empty()) else {
        return Ok(invalid);
    };
    match complete_github_flow(
        &state,
        &instance,
        &connection,
        &slug,
        code,
        &headers,
        record.authentication_next.as_deref(),
    )
    .await
    {
        Ok(mut response) => {
            clear_state_cookie(&mut response, &slug, &connection_id);
            Ok(response)
        }
        Err(UpstreamFailure::Invalid(message)) => {
            let mut response = connection_error(&slug, message, StatusCode::BAD_REQUEST);
            clear_state_cookie(&mut response, &slug, &connection_id);
            Ok(response)
        }
        Err(UpstreamFailure::Unavailable(message)) => {
            let mut response = connection_error(&slug, message, StatusCode::BAD_GATEWAY);
            clear_state_cookie(&mut response, &slug, &connection_id);
            Ok(response)
        }
    }
}

async fn complete_github_flow(
    state: &AppState,
    instance: &auth_contracts::models::Instance,
    connection: &Connection,
    slug: &str,
    code: &str,
    headers: &HeaderMap,
    continuation: Option<&str>,
) -> Result<Response, UpstreamFailure> {
    let (Some(token_endpoint), Some(email_endpoint), Some(client_id), Some(secret_box)) = (
        connection.token_endpoint.as_deref(),
        connection.userinfo_endpoint.as_deref(),
        connection.client_id.as_deref(),
        connection.client_secret_box.as_deref(),
    ) else {
        return Err(UpstreamFailure::Invalid(
            "This GitHub connection is incomplete.",
        ));
    };
    let secret = secretbox::decrypt(&state.options.master_key, secret_box)
        .map_err(|_| UpstreamFailure::Invalid("The GitHub credentials could not be read."))?;
    let secret = String::from_utf8(secret)
        .map_err(|_| UpstreamFailure::Invalid("The GitHub credentials could not be read."))?;
    let token_endpoint = valid_public_https_url(token_endpoint).ok_or(UpstreamFailure::Invalid(
        "The GitHub token endpoint URL is invalid.",
    ))?;
    let email_endpoint = valid_public_https_url(email_endpoint).ok_or(UpstreamFailure::Invalid(
        "The GitHub user profile endpoint URL is invalid.",
    ))?;
    let token_http = crate::features::admin::pinned_public_client(&token_endpoint)
        .await
        .map_err(|_| UpstreamFailure::Unavailable("The GitHub token endpoint is unavailable."))?;
    let response = token_http
        .post(token_endpoint)
        .header(header::ACCEPT, "application/json")
        .header(header::USER_AGENT, "TerraScale-Auth/1.0")
        .form(&[
            ("client_id", client_id),
            ("client_secret", secret.as_str()),
            ("code", code),
            (
                "redirect_uri",
                &github_callback_url(state, slug, &connection.id),
            ),
        ])
        .send()
        .await
        .map_err(|_| UpstreamFailure::Unavailable("Contacting GitHub failed."))?;
    if !response.status().is_success() {
        return Err(UpstreamFailure::Unavailable(
            "Exchanging the GitHub authorization code failed.",
        ));
    }
    let tokens: Value = read_provider_json(response)
        .await
        .map_err(|_| UpstreamFailure::Unavailable("GitHub returned an invalid token response."))?;
    let Some(bearer) = tokens
        .get("access_token")
        .and_then(Value::as_str)
        .filter(|value| !value.is_empty())
    else {
        return Err(UpstreamFailure::Unavailable(
            "GitHub did not return an access token.",
        ));
    };
    let email_http = crate::features::admin::pinned_public_client(&email_endpoint)
        .await
        .map_err(|_| UpstreamFailure::Unavailable("The GitHub email endpoint is unavailable."))?;
    let response = email_http
        .get(email_endpoint)
        .bearer_auth(bearer)
        .header(header::ACCEPT, "application/vnd.github+json")
        .header(header::USER_AGENT, "TerraScale-Auth/1.0")
        .send()
        .await
        .map_err(|_| UpstreamFailure::Unavailable("Fetching the GitHub email address failed."))?;
    if !response.status().is_success() {
        return Err(UpstreamFailure::Unavailable(
            "Fetching the GitHub email address failed.",
        ));
    }
    let emails: Value = read_provider_json(response)
        .await
        .map_err(|_| UpstreamFailure::Unavailable("GitHub returned invalid email addresses."))?;
    let Some(items) = emails.as_array() else {
        return Err(UpstreamFailure::Unavailable(
            "GitHub did not return email addresses.",
        ));
    };
    let verified = items
        .iter()
        .filter(|item| item.get("verified").and_then(Value::as_bool) == Some(true))
        .filter_map(|item| item.get("email").and_then(Value::as_str))
        .collect::<Vec<_>>();
    let email = items
        .iter()
        .find(|item| {
            item.get("verified").and_then(Value::as_bool) == Some(true)
                && item.get("primary").and_then(Value::as_bool) == Some(true)
        })
        .and_then(|item| item.get("email"))
        .and_then(Value::as_str)
        .or_else(|| verified.first().copied())
        .ok_or(UpstreamFailure::Invalid(
            "GitHub did not provide a verified email address.",
        ))?;
    finish_federated_sign_in(
        state,
        instance,
        slug,
        headers,
        email,
        None,
        &format!("github:{}", connection.id),
        continuation,
    )
    .await
    .map_err(|_| {
        UpstreamFailure::Invalid("Could not create or access an account for this identity.")
    })
}

#[derive(Deserialize)]
struct Discovery {
    issuer: Option<String>,
    authorization_endpoint: Option<String>,
    token_endpoint: Option<String>,
    userinfo_endpoint: Option<String>,
    jwks_uri: Option<String>,
}

async fn fetch_discovery(issuer: &Url) -> Result<Option<Discovery>, ()> {
    let http = crate::features::admin::pinned_public_client(issuer)
        .await
        .map_err(|_| ())?;
    let endpoint = format!(
        "{}/.well-known/openid-configuration",
        issuer.as_str().trim_end_matches('/')
    );
    let response = http.get(endpoint).send().await.map_err(|_| ())?;
    if !response.status().is_success() {
        return Ok(None);
    }
    Ok(read_provider_json::<Discovery>(response).await.ok())
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ProviderJsonError {
    TooLarge,
    Read,
    Invalid,
}

async fn read_provider_json<T: DeserializeOwned>(
    mut response: reqwest::Response,
) -> Result<T, ProviderJsonError> {
    if response
        .content_length()
        .is_some_and(|length| length > MAX_PROVIDER_JSON_BYTES as u64)
    {
        return Err(ProviderJsonError::TooLarge);
    }
    let capacity = response
        .content_length()
        .unwrap_or_default()
        .min(MAX_PROVIDER_JSON_BYTES as u64) as usize;
    let mut bytes = Vec::with_capacity(capacity);
    while let Some(chunk) = response
        .chunk()
        .await
        .map_err(|_| ProviderJsonError::Read)?
    {
        if bytes.len().saturating_add(chunk.len()) > MAX_PROVIDER_JSON_BYTES {
            return Err(ProviderJsonError::TooLarge);
        }
        bytes.extend_from_slice(&chunk);
    }
    serde_json::from_slice(&bytes).map_err(|_| ProviderJsonError::Invalid)
}

fn verify_oidc_id_token(
    raw: &str,
    jwks: &[Value],
    issuer: &str,
    client_id: &str,
    expected_nonce: &str,
) -> Option<Value> {
    let keys = jwks
        .iter()
        .filter_map(|key| jwt::public_key_from_jwk(key).ok())
        .collect::<Vec<_>>();
    let claims = jwt::verify_with_keys(raw, &keys, now())?;
    if claims.get("iss").and_then(Value::as_str) != Some(issuer)
        || claims.get("nonce").and_then(Value::as_str) != Some(expected_nonce)
    {
        return None;
    }
    let aud = claims.get("aud")?;
    let (contains_client, audience_count) = match aud {
        Value::String(value) => (value == client_id, 1),
        Value::Array(values) if values.iter().all(Value::is_string) => (
            values.iter().any(|value| value.as_str() == Some(client_id)),
            values.len(),
        ),
        _ => (false, 0),
    };
    if !contains_client || audience_count == 0 {
        return None;
    }
    if (audience_count > 1 || claims.get("azp").is_some())
        && claims.get("azp").and_then(Value::as_str) != Some(client_id)
    {
        return None;
    }
    if claims
        .get("exp")
        .and_then(Value::as_i64)
        .is_none_or(|exp| exp <= now())
    {
        return None;
    }
    let issued_at = claims.get("iat").and_then(Value::as_i64)?;
    if issued_at > now().saturating_add(300) {
        return None;
    }
    Some(claims)
}

#[expect(
    clippy::too_many_arguments,
    reason = "Federated sign-in keeps provider-proven identity and continuation bindings explicit at the authentication boundary."
)]
async fn finish_federated_sign_in(
    state: &AppState,
    instance: &auth_contracts::models::Instance,
    slug: &str,
    headers: &HeaderMap,
    email: &str,
    name: Option<&str>,
    method: &str,
    continuation: Option<&str>,
) -> AppResult<Response> {
    let user = users::find_or_create_verified_user(state, &instance.id, email, name).await?;
    let user_agent = headers
        .get(header::USER_AGENT)
        .and_then(|value| value.to_str().ok());
    let next =
        users::validated_authentication_next(&state.options.public_origin, slug, continuation)
            .unwrap_or_else(|| format!("/ui/{slug}/account"));
    match users::complete_first_factor(
        state,
        instance,
        &user,
        method,
        user_agent,
        Some(email),
        Some(&next),
    )
    .await?
    {
        users::FirstFactorOutcome::Session { token } => {
            let mut response = Redirect::to(&next).into_response();
            crate::features::ui::set_session_cookie(&mut response, &token);
            Ok(response)
        }
        users::FirstFactorOutcome::MfaPending { token } => {
            Ok(Redirect::to(&format!("/ui/{slug}/mfa/verify?token={token}")).into_response())
        }
    }
}

async fn mint_state(
    store: &AuthStore,
    instance_id: &str,
    connection_id: &str,
    purpose: &str,
    request_id: Option<&str>,
    authentication_next: Option<&str>,
) -> AppResult<String> {
    let raw = tokens::random_token(40);
    let mut id = format!("vrf_{}", tokens::random_token(20));
    if let Some(request_id) = request_id {
        id.push(':');
        id.push_str(request_id);
    }
    store
        .create_verification_token(&VerificationToken {
            id,
            instance_id: instance_id.to_owned(),
            user_id: Some(connection_id.to_owned()),
            purpose: purpose.to_owned(),
            authentication_method: None,
            authentication_next: authentication_next.map(str::to_owned),
            user_auth_epoch: None,
            token_key: tokens::sha256_hex(&raw),
            expires_at: now().saturating_add(STATE_TTL_SECONDS),
            consumed: false,
            failed_attempts: 0,
            created_at: now(),
            email_snapshot: None,
        })
        .await
        .map_err(internal)?;
    Ok(raw)
}

async fn mint_oidc_state(
    state: &AppState,
    instance_id: &str,
    connection_id: &str,
    authentication_next: Option<&str>,
) -> AppResult<(String, String, String)> {
    let state_value = tokens::random_token(40);
    let nonce = tokens::random_token(40);
    let verifier = tokens::random_token(64);
    let secrets =
        serde_json::to_vec(&json!({"nonce":nonce,"verifier":verifier})).map_err(internal)?;
    let boxed = secretbox::encrypt(&state.options.master_key, &secrets)
        .map_err(|_| AppError::internal())?;
    // The existing optional snapshot slot is encrypted and this purpose token
    // never treats it as an email address; keep the PKCE verifier server-side.
    let record = VerificationToken {
        id: format!("vrf_{}", tokens::random_token(20)),
        instance_id: instance_id.to_owned(),
        user_id: Some(connection_id.to_owned()),
        purpose: "oidc.state".to_owned(),
        authentication_method: None,
        authentication_next: authentication_next.map(str::to_owned),
        user_auth_epoch: None,
        token_key: tokens::sha256_hex(&state_value),
        expires_at: now().saturating_add(STATE_TTL_SECONDS),
        consumed: false,
        failed_attempts: 0,
        created_at: now(),
        email_snapshot: Some(boxed),
    };
    state
        .store
        .create_verification_token(&record)
        .await
        .map_err(internal)?;
    Ok((state_value, nonce, verifier))
}

async fn consume_state(
    store: &AuthStore,
    instance_id: &str,
    connection_id: &str,
    raw: &str,
    purpose: &str,
) -> AppResult<Option<VerificationToken>> {
    if raw.is_empty() {
        return Ok(None);
    }
    let key = tokens::sha256_hex(raw);
    store
        .consume_verification_token(&key, |record| {
            record.instance_id == instance_id
                && record.purpose == purpose
                && record.user_id.as_deref() == Some(connection_id)
                && !record.consumed
                && record.expires_at > now()
        })
        .await
        .map_err(internal)
}

async fn find_connection(
    store: &AuthStore,
    instance_id: &str,
    connection_id: &str,
    expected_type: &str,
) -> AppResult<Option<Connection>> {
    Ok(store
        .get_connection(connection_id)
        .await
        .map_err(internal)?
        .filter(|value| {
            value.instance_id == instance_id && value.r#type == expected_type && value.enabled
        }))
}

fn connection_view(connection: &Connection) -> Value {
    json!({
        "id":connection.id,"type":connection.r#type,"name":connection.name,"scopes":connection.scopes,"enabled":connection.enabled,"org_id":connection.org_id,
        "issuer_url":connection.issuer_url,"client_id":connection.client_id,"authorization_endpoint":connection.authorization_endpoint,
        "token_endpoint":connection.token_endpoint,"userinfo_endpoint":connection.userinfo_endpoint,"idp_entity_id":connection.idp_entity_id,
        "sso_redirect_url":connection.sso_redirect_url,"sp_entity_id":connection.sp_entity_id,"created_at":connection.created_at
    })
}

fn normalize_oidc_scopes(scopes: Option<&[String]>) -> Result<Vec<String>, ()> {
    let Some(scopes) = scopes else {
        return Ok(DEFAULT_OIDC_SCOPES
            .iter()
            .map(|scope| (*scope).to_owned())
            .collect());
    };
    if scopes.is_empty() || scopes.len() > MAX_OIDC_SCOPES {
        return Err(());
    }
    let mut normalized = Vec::with_capacity(scopes.len());
    let mut seen = HashSet::with_capacity(scopes.len());
    for scope in scopes {
        let scope = scope.trim();
        if scope.len() > MAX_OIDC_SCOPE_LENGTH
            || scope.is_empty()
            || !scope.bytes().all(|byte| {
                byte == b'!' || (b'#'..=b'[').contains(&byte) || (b']'..=b'~').contains(&byte)
            })
        {
            return Err(());
        }
        if seen.insert(scope) {
            normalized.push(scope.to_owned());
        }
    }
    if !normalized.iter().any(|scope| scope == "openid")
        || !normalized.iter().any(|scope| scope == "email")
    {
        return Err(());
    }
    Ok(normalized)
}

fn api_error(status: StatusCode, code: &str, message: &str) -> Response {
    (
        status,
        Json(json!({"error":{"code":code,"message":message}})),
    )
        .into_response()
}

fn not_found_page() -> Response {
    (StatusCode::NOT_FOUND, Html("<h1>Page not found</h1><p>No instance or enabled connection lives at this address.</p>".to_owned())).into_response()
}

fn connection_error(slug: &str, message: &str, status: StatusCode) -> Response {
    let safe_slug = escape_html(slug);
    let safe_message = escape_html(message);
    (status, Html(format!("<h1>Sign-in failed</h1><p>{safe_message}</p><p><a href=\"/ui/{safe_slug}/sign-in\">Back to sign in</a></p>"))).into_response()
}

fn internal(error: impl std::fmt::Debug) -> AppError {
    tracing::error!(?error, "connection operation failed");
    AppError::internal()
}

fn valid_http_url(value: &str) -> Option<Url> {
    let url = Url::parse(value).ok()?;
    (matches!(url.scheme(), "http" | "https")
        && url.host_str().is_some()
        && url.username().is_empty()
        && url.password().is_none()
        && url.fragment().is_none())
    .then_some(url)
}

fn valid_public_https_url(value: &str) -> Option<Url> {
    valid_http_url(value).filter(|url| url.scheme() == "https")
}

fn clean_option(value: Option<&str>) -> Option<String> {
    value
        .filter(|item| !item.trim().is_empty())
        .map(|item| item.trim().to_owned())
}
fn oidc_callback_url(state: &AppState, slug: &str, connection_id: &str) -> String {
    format!(
        "{}/ui/{slug}/connections/{connection_id}/oidc/callback",
        state.options.public_origin.trim_end_matches('/')
    )
}
fn github_callback_url(state: &AppState, slug: &str, connection_id: &str) -> String {
    format!(
        "{}/ui/{slug}/connections/{connection_id}/github/callback",
        state.options.public_origin.trim_end_matches('/')
    )
}
fn now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |duration| duration.as_secs() as i64)
}

fn append_state_cookie(response: &mut Response, slug: &str, connection_id: &str, state: &str) {
    let value = format!(
        "tsauth_github_state={}; Path=/ui/{slug}/connections/{connection_id}/github/callback; Max-Age={STATE_TTL_SECONDS}; Secure; HttpOnly; SameSite=Lax",
        tokens::sha256_hex(state)
    );
    if let Ok(header) = HeaderValue::from_str(&value) {
        response.headers_mut().append(header::SET_COOKIE, header);
    }
}

fn append_oidc_state_cookie(response: &mut Response, slug: &str, connection_id: &str, state: &str) {
    let value = format!(
        "{OIDC_STATE_COOKIE}={}; Path=/ui/{slug}/connections/{connection_id}/oidc/callback; Max-Age={STATE_TTL_SECONDS}; Secure; HttpOnly; SameSite=Lax",
        tokens::sha256_hex(state)
    );
    if let Ok(header) = HeaderValue::from_str(&value) {
        response.headers_mut().append(header::SET_COOKIE, header);
    }
}

fn clear_oidc_state_cookie(response: &mut Response, slug: &str, connection_id: &str) {
    let value = format!(
        "{OIDC_STATE_COOKIE}=; Path=/ui/{slug}/connections/{connection_id}/oidc/callback; Max-Age=0; Expires=Thu, 01 Jan 1970 00:00:00 GMT; Secure; HttpOnly; SameSite=Lax"
    );
    if let Ok(header) = HeaderValue::from_str(&value) {
        response.headers_mut().append(header::SET_COOKIE, header);
    }
}

fn clear_state_cookie(response: &mut Response, slug: &str, connection_id: &str) {
    let value = format!(
        "tsauth_github_state=; Path=/ui/{slug}/connections/{connection_id}/github/callback; Max-Age=0; Expires=Thu, 01 Jan 1970 00:00:00 GMT; Secure; HttpOnly; SameSite=Lax"
    );
    if let Ok(header) = HeaderValue::from_str(&value) {
        response.headers_mut().append(header::SET_COOKIE, header);
    }
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

fn certificate_body(value: &str) -> Option<String> {
    let encoded = value
        .replace("-----BEGIN CERTIFICATE-----", "")
        .replace("-----END CERTIFICATE-----", "")
        .split_ascii_whitespace()
        .collect::<String>();
    let decoded = STANDARD.decode(&encoded).ok()?;
    (!decoded.is_empty()).then_some(encoded)
}

fn escape_html(value: &str) -> String {
    value
        .replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&#39;")
}

enum UpstreamFailure {
    Invalid(&'static str),
    Unavailable(&'static str),
}

#[cfg(test)]
mod tests {
    use super::{
        MAX_PROVIDER_JSON_BYTES, ProviderJsonError, append_oidc_state_cookie,
        clear_oidc_state_cookie, normalize_oidc_scopes, read_provider_json, valid_http_url,
        verify_oidc_id_token,
    };
    use auth_security::tokens;
    use axum::{http::header, response::IntoResponse};
    use std::io::{Read, Write};

    #[test]
    fn oidc_scopes_default_normalize_deduplicate_and_enforce_required_tokens() {
        assert_eq!(
            normalize_oidc_scopes(None).expect("omitted scopes use the safe OIDC default"),
            ["openid", "email", "profile"]
        );
        assert_eq!(
            normalize_oidc_scopes(Some(&[
                "openid".to_owned(),
                " email ".to_owned(),
                "groups:read".to_owned(),
                "email".to_owned(),
            ]))
            .expect("well-formed custom scope tokens are accepted"),
            ["openid", "email", "groups:read"]
        );
        assert!(normalize_oidc_scopes(Some(&["openid".to_owned()])).is_err());
        for invalid in ["two words", "bad\\token", "bad\"token", ""] {
            assert!(
                normalize_oidc_scopes(Some(&[
                    "openid".to_owned(),
                    "email".to_owned(),
                    invalid.to_owned(),
                ]))
                .is_err(),
                "invalid OIDC scope token should be rejected: {invalid:?}"
            );
        }
        assert!(normalize_oidc_scopes(Some(&vec!["openid".to_owned(); 65])).is_err());
        assert!(
            normalize_oidc_scopes(Some(&[
                "openid".to_owned(),
                "email".to_owned(),
                "x".repeat(129),
            ]))
            .is_err()
        );
    }

    async fn local_provider_response(
        wire_response: Vec<u8>,
    ) -> (reqwest::Response, std::thread::JoinHandle<()>) {
        let listener = std::net::TcpListener::bind("127.0.0.1:0")
            .expect("loopback provider fixture should bind");
        let address = listener
            .local_addr()
            .expect("loopback provider fixture should have an address");
        assert!(address.ip().is_loopback());
        let server = std::thread::spawn(move || {
            let (mut stream, _) = listener
                .accept()
                .expect("local provider client should connect");
            let mut request = [0; 1024];
            let _ = stream
                .read(&mut request)
                .expect("local provider request should be readable");
            stream
                .write_all(&wire_response)
                .expect("local provider response should be writable");
        });
        let client = reqwest::Client::builder()
            .no_proxy()
            .build()
            .expect("local test HTTP client should build");
        let response = client
            .get(format!("http://{address}/provider-json"))
            .send()
            .await
            .expect("local provider response should arrive");
        (response, server)
    }

    #[test]
    fn configured_upstream_urls_cannot_embed_credentials_or_fragments() {
        assert!(valid_http_url("https://idp.example/.well-known/openid-configuration").is_some());
        assert!(valid_http_url("file:///etc/passwd").is_none());
        assert!(valid_http_url("https://user:pass@idp.example/authorize").is_none());
        assert!(valid_http_url("https://idp.example/authorize#fragment").is_none());
    }

    #[tokio::test]
    async fn provider_json_rejects_declared_and_chunked_oversize_bodies() {
        let declared = format!(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
            MAX_PROVIDER_JSON_BYTES + 1
        )
        .into_bytes();
        let (response, server) = local_provider_response(declared).await;
        assert_eq!(
            read_provider_json::<serde_json::Value>(response)
                .await
                .expect_err("oversized declared bodies should be rejected"),
            ProviderJsonError::TooLarge
        );
        server
            .join()
            .expect("declared-size fixture thread should finish");

        let body = vec![b'x'; MAX_PROVIDER_JSON_BYTES + 1];
        let mut chunked = b"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n".to_vec();
        chunked.extend_from_slice(format!("{:X}\r\n", body.len()).as_bytes());
        chunked.extend_from_slice(&body);
        chunked.extend_from_slice(b"\r\n0\r\n\r\n");
        let (response, server) = local_provider_response(chunked).await;
        assert_eq!(
            read_provider_json::<serde_json::Value>(response)
                .await
                .expect_err("oversized chunked bodies should be rejected"),
            ProviderJsonError::TooLarge
        );
        server
            .join()
            .expect("chunked-size fixture thread should finish");
    }

    #[tokio::test]
    async fn provider_json_reads_small_loopback_fixture() {
        let body = br#"{"issuer":"https://issuer.example"}"#;
        let mut wire = format!(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
            body.len()
        )
        .into_bytes();
        wire.extend_from_slice(body);
        let (response, server) = local_provider_response(wire).await;
        let value = read_provider_json::<serde_json::Value>(response)
            .await
            .expect("small provider JSON should parse");
        assert_eq!(value["issuer"], "https://issuer.example");
        server
            .join()
            .expect("small response fixture thread should finish");
    }

    #[test]
    fn id_token_audience_and_nonce_are_checked_after_signature_verification() {
        let claims = serde_json::json!({"iss":"https://issuer.example","aud":"client","sub":"user","nonce":"n","exp":i64::MAX,"iat":0});
        let key = auth_security::Es256Key::generate("kid");
        let raw = auth_security::jwt::sign(claims, None, &key, 0).expect("signing works");
        let jwk = key.public_jwk();
        assert!(
            verify_oidc_id_token(
                &raw,
                std::slice::from_ref(&jwk),
                "https://issuer.example",
                "client",
                "n"
            )
            .is_some()
        );
        assert!(
            verify_oidc_id_token(&raw, &[jwk], "https://issuer.example", "client", "other")
                .is_none()
        );
    }

    #[test]
    fn oidc_state_cookie_is_short_lived_bound_and_lax_for_the_cross_site_get_callback() {
        let mut response = axum::response::Redirect::to("/provider").into_response();
        append_oidc_state_cookie(&mut response, "acme", "con_123", "random-state-value");
        let cookie = response
            .headers()
            .get(header::SET_COOKIE)
            .expect("cookie is set")
            .to_str()
            .expect("valid cookie header");
        assert!(cookie.starts_with("tsauth_oidc_state="));
        assert!(cookie.contains(&tokens::sha256_hex("random-state-value")));
        assert!(!cookie.contains("random-state-value"));
        assert!(cookie.contains("Path=/ui/acme/connections/con_123/oidc/callback"));
        assert!(cookie.contains("Max-Age=600; Secure; HttpOnly; SameSite=Lax"));

        clear_oidc_state_cookie(&mut response, "acme", "con_123");
        let cookies = response
            .headers()
            .get_all(header::SET_COOKIE)
            .iter()
            .filter_map(|value| value.to_str().ok())
            .collect::<Vec<_>>();
        assert!(
            cookies
                .iter()
                .any(|value| value.contains("Max-Age=0") && value.contains("SameSite=Lax"))
        );
    }

    #[test]
    fn kidless_id_token_is_accepted_only_when_jwks_key_selection_is_unambiguous() {
        use base64::{Engine, engine::general_purpose::URL_SAFE_NO_PAD};
        let key = auth_security::Es256Key::generate("kid-one");
        let claims = serde_json::json!({"iss":"https://issuer.example","aud":"client","sub":"user","nonce":"n","exp":i64::MAX,"iat":0});
        let header = URL_SAFE_NO_PAD.encode(br#"{"alg":"ES256","typ":"JWT"}"#);
        let payload =
            URL_SAFE_NO_PAD.encode(serde_json::to_vec(&claims).expect("claims serialize"));
        let signing_input = format!("{header}.{payload}");
        let signature = URL_SAFE_NO_PAD.encode(key.sign(signing_input.as_bytes()));
        let raw = format!("{signing_input}.{signature}");

        assert!(
            verify_oidc_id_token(
                &raw,
                &[key.public_jwk()],
                "https://issuer.example",
                "client",
                "n"
            )
            .is_some()
        );
        assert!(
            verify_oidc_id_token(
                &raw,
                &[
                    key.public_jwk(),
                    auth_security::Es256Key::generate("kid-two").public_jwk()
                ],
                "https://issuer.example",
                "client",
                "n",
            )
            .is_none()
        );
    }
}
