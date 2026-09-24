//! Provider setup fragments and organization-scoped connection actions.

use std::{
    net::{IpAddr, SocketAddr},
    time::Duration,
};

use auth_contracts::models::{AuditEvent, Connection, Organization};
use auth_security::{secretbox, tokens::random_token};
use axum::{
    Form,
    extract::{Path, State},
    http::{HeaderMap, StatusCode, header},
    response::{IntoResponse, Redirect, Response},
};
use serde::Deserialize;
use url::{Host, Url};

use crate::{
    AppState,
    features::{ui, users},
};

use super::portal::{self, AdminContext};

const MAX_FORM_FIELD: usize = 16 * 1024;
const MAX_DISCOVERY_BYTES: usize = 1024 * 1024;

#[derive(Debug, Deserialize)]
pub(super) struct ConnectionForm {
    provider: Option<String>,
    org_id: Option<String>,
    name: Option<String>,
    client_id: Option<String>,
    client_secret: Option<String>,
    tenant_id: Option<String>,
    issuer_url: Option<String>,
    idp_entity_id: Option<String>,
    sso_redirect_url: Option<String>,
    x509_certificate_pem: Option<String>,
}

#[derive(Debug, Deserialize)]
struct Discovery {
    issuer: Option<String>,
    authorization_endpoint: Option<String>,
    token_endpoint: Option<String>,
    userinfo_endpoint: Option<String>,
}

pub(super) async fn get_provider_setup(
    State(state): State<AppState>,
    Path((slug, provider)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    let context = match portal::resolve_admin(&state, &headers, &slug).await {
        Ok(Some(context)) => context,
        Ok(None) => return provider_sign_in_redirect(&slug),
        Err(error) => {
            return portal::error_response(error.status, "Temporarily unavailable", &error.message);
        }
    };
    if context.organizations.is_empty() {
        return portal::error_response(
            StatusCode::FORBIDDEN,
            "Not authorized",
            "Only organization admins can configure sign-in providers.",
        );
    }
    let Some(fragment) = provider_setup_html(&provider, &slug, &context.organizations) else {
        return portal::error_response(
            StatusCode::NOT_FOUND,
            "Provider unavailable",
            "Choose one of the listed providers.",
        );
    };
    let event = datastar_patch_event(&fragment);
    let mut response = (
        [(header::CONTENT_TYPE, "text/event-stream; charset=utf-8")],
        event,
    )
        .into_response();
    portal::security_headers(&mut response, None);
    response
}

pub(super) async fn create_connection(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Form(form): Form<ConnectionForm>,
) -> Response {
    if !users::require_same_origin(&headers, &state.options.public_origin) {
        return portal::error_response(
            StatusCode::FORBIDDEN,
            "Request rejected",
            "Submit this form from the ts-auth administrator portal.",
        );
    }
    let context = match portal::resolve_admin(&state, &headers, &slug).await {
        Ok(Some(context)) => context,
        Ok(None) => return provider_sign_in_redirect(&slug),
        Err(error) => {
            return portal::error_response(error.status, "Temporarily unavailable", &error.message);
        }
    };
    if context.organizations.is_empty() {
        return portal::error_response(
            StatusCode::FORBIDDEN,
            "Not authorized",
            "Only organization admins can create sign-in connections.",
        );
    }
    if !form_within_limits(&form) {
        return portal::error_response(
            StatusCode::PAYLOAD_TOO_LARGE,
            "Form too large",
            "One or more fields exceed the supported size.",
        );
    }
    let Some(org) = select_organization(&context.organizations, form.org_id.as_deref()) else {
        return portal::error_response(
            StatusCode::FORBIDDEN,
            "Organization unavailable",
            "Choose an organization you administer.",
        );
    };

    let result = match form.provider.as_deref().unwrap_or_default() {
        "" => create_saml(&state, &context, org, &form).await,
        provider => create_oauth_provider(&state, &context, org, &form, provider).await,
    };
    match result {
        Ok(()) => Redirect::to(&format!(
            "/ui/{}/admin#connections",
            encode_segment(&context.instance.slug)
        ))
        .into_response(),
        Err(error) => {
            if let Some(status) = error.status {
                portal::error_response(status, "Connection not saved", &error.message)
            } else {
                tracing::error!(?error, "admin connection creation failed");
                portal::error_response(
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "Connection not saved",
                    "The connection could not be saved. Please try again.",
                )
            }
        }
    }
}

pub(super) async fn update_connection(
    State(state): State<AppState>,
    Path((slug, connection_id, action)): Path<(String, String, String)>,
    headers: HeaderMap,
) -> Response {
    if !users::require_same_origin(&headers, &state.options.public_origin) {
        return portal::error_response(
            StatusCode::FORBIDDEN,
            "Request rejected",
            "Submit this action from the ts-auth administrator portal.",
        );
    }
    let context = match portal::resolve_admin(&state, &headers, &slug).await {
        Ok(Some(context)) => context,
        Ok(None) => return provider_sign_in_redirect(&slug),
        Err(error) => {
            return portal::error_response(error.status, "Temporarily unavailable", &error.message);
        }
    };
    if context.organizations.is_empty() {
        return portal::error_response(
            StatusCode::FORBIDDEN,
            "Not authorized",
            "Only organization admins can manage sign-in connections.",
        );
    }
    if !matches!(action.as_str(), "enable" | "disable") {
        return portal::error_response(
            StatusCode::NOT_FOUND,
            "Action unavailable",
            "Choose enable or disable.",
        );
    }
    let connection = match state.store.get_connection(&connection_id).await {
        Ok(Some(connection)) if connection.instance_id == context.instance.id => connection,
        Ok(_) => {
            return portal::error_response(
                StatusCode::NOT_FOUND,
                "Connection unavailable",
                "This connection does not exist.",
            );
        }
        Err(error) => {
            tracing::error!(?error, "admin connection lookup failed");
            return portal::error_response(
                StatusCode::INTERNAL_SERVER_ERROR,
                "Temporarily unavailable",
                "The connection could not be loaded.",
            );
        }
    };
    let authorized = connection
        .org_id
        .as_deref()
        .is_some_and(|org_id| context.organizations.iter().any(|org| org.id == org_id));
    if !authorized {
        return portal::error_response(
            StatusCode::FORBIDDEN,
            "Not authorized",
            "You can only manage connections for organizations you administer.",
        );
    }

    let enabled = action == "enable";
    let instance_id = context.instance.id.clone();
    let admin_organization_ids = context
        .organizations
        .iter()
        .map(|organization| organization.id.clone())
        .collect::<Vec<_>>();
    let connection = match state
        .store
        .update_connection(&connection_id, |current| {
            let still_authorized = current.instance_id == instance_id
                && current.org_id.as_deref().is_some_and(|org_id| {
                    admin_organization_ids
                        .iter()
                        .any(|authorized_org_id| authorized_org_id == org_id)
                });
            if still_authorized {
                current.enabled = enabled;
            }
        })
        .await
    {
        Ok(Some(connection))
            if connection.instance_id == context.instance.id
                && connection.org_id.as_deref().is_some_and(|org_id| {
                    context
                        .organizations
                        .iter()
                        .any(|organization| organization.id == org_id)
                }) =>
        {
            connection
        }
        Ok(Some(_)) => {
            return portal::error_response(
                StatusCode::NOT_FOUND,
                "Connection unavailable",
                "This connection does not exist.",
            );
        }
        Ok(None) => {
            return portal::error_response(
                StatusCode::NOT_FOUND,
                "Connection unavailable",
                "This connection does not exist.",
            );
        }
        Err(error) => {
            tracing::error!(?error, "admin connection update failed");
            return portal::error_response(
                StatusCode::INTERNAL_SERVER_ERROR,
                "Connection not updated",
                "The connection could not be updated. Please try again.",
            );
        }
    };
    let event = AuditEvent {
        id: format!("evt_{}", random_token(20)),
        instance_id: context.instance.id.clone(),
        r#type: if connection.enabled {
            "connection.enabled"
        } else {
            "connection.disabled"
        }
        .to_owned(),
        actor_id: Some(context.user.id),
        data: serde_json::Map::from_iter([
            (
                "connection_id".to_owned(),
                serde_json::Value::String(connection.id),
            ),
            (
                "org_id".to_owned(),
                serde_json::Value::String(connection.org_id.unwrap_or_default()),
            ),
        ]),
        created_at: now(),
        integrity_version: None,
        integrity_hash: None,
        integrity_signature: None,
        integrity_key_fingerprint: None,
    };
    if let Err(error) = state.store.create_audit_event(&event).await {
        tracing::error!(?error, "admin connection audit write failed");
        return portal::error_response(
            StatusCode::INTERNAL_SERVER_ERROR,
            "Connection updated",
            "The connection changed, but its audit record could not be written. Contact your administrator.",
        );
    }
    Redirect::to(&format!(
        "/ui/{}/admin#connections",
        encode_segment(&context.instance.slug)
    ))
    .into_response()
}

fn provider_setup_html(
    provider: &str,
    slug: &str,
    organizations: &[Organization],
) -> Option<String> {
    let (name, detail, extra) = match provider {
        "github" => (
            "GitHub",
            "Create an OAuth app in GitHub Developer Settings and request access to the user's verified email address.",
            "",
        ),
        "microsoft" => (
            "Microsoft Entra ID",
            "Create an app registration and add a client secret. Use a single tenant application.",
            "<div class=\"field\"><label for=\"provider-tenant\">Directory (tenant) ID</label><input class=\"input\" id=\"provider-tenant\" name=\"tenant_id\" required placeholder=\"00000000-0000-0000-0000-000000000000\"><small>Use the GUID from your Entra overview.</small></div>",
        ),
        "google" => (
            "Google",
            "Create a Web application OAuth client in Google Cloud Console.",
            "",
        ),
        "oidc" => (
            "Other OpenID Connect",
            "Enter an issuer with a public HTTPS discovery document.",
            "<div class=\"field\"><label for=\"provider-issuer\">Issuer URL</label><input class=\"input\" id=\"provider-issuer\" name=\"issuer_url\" type=\"url\" required placeholder=\"https://idp.example.com\"></div>",
        ),
        _ => return None,
    };
    let org_field = organization_select(organizations);
    let slug = encode_segment(slug);
    Some(format!(
        r#"<div id="provider-setup" class="setup"><div class="setup-head"><div><h3>{name}</h3><p>{detail}</p></div><span class="pill">New connection</span></div><form method="post" action="/ui/{slug}/admin/connections"><input type="hidden" name="provider" value="{provider}">{org_field}<div class="field"><label for="provider-name">Display name</label><input class="input" id="provider-name" name="name" value="{name}" maxlength="120" required></div>{extra}<div class="field"><label for="provider-client-id">Client ID</label><input class="input" id="provider-client-id" name="client_id" autocomplete="off" maxlength="512" required></div><div class="field"><label for="provider-client-secret">Client secret</label><input class="input" id="provider-client-secret" name="client_secret" type="password" autocomplete="new-password" maxlength="2048" required></div><div class="form-actions"><button class="btn btn-primary" type="submit">Add {name}</button><small>The secret is encrypted before it is stored.</small></div></form></div>"#,
        provider = provider,
        name = ui::escape_html(name),
        detail = ui::escape_html(detail),
        slug = slug,
        org_field = org_field,
        extra = extra,
    ))
}

fn organization_select(organizations: &[Organization]) -> String {
    if organizations.len() < 2 {
        return String::new();
    }
    let options = organizations
        .iter()
        .map(|org| {
            format!(
                "<option value=\"{}\">{}</option>",
                ui::escape_html(&org.id),
                ui::escape_html(&org.name)
            )
        })
        .collect::<String>();
    format!(
        "<div class=\"field\"><label for=\"connection-org\">Organization</label><select class=\"input\" id=\"connection-org\" name=\"org_id\" required>{options}</select></div>"
    )
}

async fn create_saml(
    state: &AppState,
    context: &AdminContext,
    org: &Organization,
    form: &ConnectionForm,
) -> Result<(), CreateError> {
    let name = required_trimmed(form.name.as_deref(), "Name is required.", 120)?;
    let entity = required_trimmed(
        form.idp_entity_id.as_deref(),
        "IdP entity ID is required.",
        2048,
    )?;
    let sso_text = required_trimmed(
        form.sso_redirect_url.as_deref(),
        "SSO redirect URL is required.",
        2048,
    )?;
    if !valid_saml_sso_url(&sso_text) {
        return Err(CreateError::bad_request(
            "SSO redirect URL must be an absolute HTTPS URL without user credentials or a fragment.",
        ));
    }
    let pem = form
        .x509_certificate_pem
        .as_deref()
        .unwrap_or_default()
        .trim();
    if pem.len() > MAX_FORM_FIELD || !valid_certificate_pem(pem) {
        return Err(CreateError::bad_request(
            "Enter a valid PEM encoded X.509 certificate.",
        ));
    }
    let connection = Connection {
        id: format!("con_{}", random_token(20)),
        instance_id: context.instance.id.clone(),
        r#type: "saml".into(),
        name,
        scopes: Vec::new(),
        enabled: true,
        org_id: Some(org.id.clone()),
        issuer_url: None,
        client_id: None,
        client_secret_box: None,
        authorization_endpoint: None,
        token_endpoint: None,
        userinfo_endpoint: None,
        idp_entity_id: Some(entity),
        sso_redirect_url: Some(sso_text),
        x509_certificate_pem: Some(pem.to_owned()),
        sp_entity_id: None,
        created_at: now(),
    };
    persist_connection(state, context, &connection, "connection.created", None).await
}

async fn create_oauth_provider(
    state: &AppState,
    context: &AdminContext,
    org: &Organization,
    form: &ConnectionForm,
    provider: &str,
) -> Result<(), CreateError> {
    if !matches!(provider, "github" | "microsoft" | "google" | "oidc") {
        return Err(CreateError::bad_request(
            "Choose a supported sign-in provider.",
        ));
    }
    let name = required_trimmed(form.name.as_deref(), "Display name is required.", 120)?;
    let client_id = required_trimmed(form.client_id.as_deref(), "Client ID is required.", 512)?;
    let client_secret = form.client_secret.as_deref().unwrap_or_default();
    if client_secret.trim().is_empty() || client_secret.len() > 2048 {
        return Err(CreateError::bad_request(
            "Client secret is required and must be 2048 bytes or fewer.",
        ));
    }

    let (issuer_url, authorization_endpoint, token_endpoint, userinfo_endpoint, connection_type) =
        if provider == "github" {
            (
                None,
                "https://github.com/login/oauth/authorize".to_owned(),
                "https://github.com/login/oauth/access_token".to_owned(),
                Some("https://api.github.com/user/emails".to_owned()),
                "github",
            )
        } else {
            let issuer = match provider {
                "microsoft" => {
                    let tenant = form.tenant_id.as_deref().unwrap_or_default().trim();
                    if !valid_uuid(tenant) {
                        return Err(CreateError::bad_request(
                            "Enter a valid Microsoft directory (tenant) ID.",
                        ));
                    }
                    format!(
                        "https://login.microsoftonline.com/{}/v2.0",
                        tenant.to_ascii_lowercase()
                    )
                }
                "google" => "https://accounts.google.com".to_owned(),
                _ => form
                    .issuer_url
                    .as_deref()
                    .unwrap_or_default()
                    .trim()
                    .to_owned(),
            };
            let issuer = validate_issuer(&issuer).await?;
            let discovery = fetch_discovery(&issuer).await?;
            if discovery
                .issuer
                .as_deref()
                .map(|value| value.trim_end_matches('/'))
                != Some(issuer.as_str().trim_end_matches('/'))
            {
                return Err(CreateError::bad_request(
                    "The provider discovery issuer does not match the configured issuer.",
                ));
            }
            let authorization = discovery.authorization_endpoint.ok_or_else(|| {
                CreateError::bad_request(
                    "The provider discovery document has no authorization endpoint.",
                )
            })?;
            let token = discovery.token_endpoint.ok_or_else(|| {
                CreateError::bad_request("The provider discovery document has no token endpoint.")
            })?;
            validate_public_https_endpoint(&authorization).await?;
            validate_public_https_endpoint(&token).await?;
            let userinfo = if let Some(endpoint) = discovery.userinfo_endpoint {
                validate_public_https_endpoint(&endpoint).await?;
                Some(endpoint)
            } else {
                None
            };
            (
                Some(issuer.to_string()),
                authorization,
                token,
                userinfo,
                "oidc",
            )
        };
    let secret_box = secretbox::encrypt(&state.options.master_key, client_secret.as_bytes())
        .map_err(|error| {
            tracing::error!(?error, "could not encrypt admin provider secret");
            CreateError::internal()
        })?;
    let scopes = if connection_type == "oidc" {
        vec![
            "openid".to_owned(),
            "email".to_owned(),
            "profile".to_owned(),
        ]
    } else {
        Vec::new()
    };
    let connection = Connection {
        id: format!("con_{}", random_token(20)),
        instance_id: context.instance.id.clone(),
        r#type: connection_type.into(),
        name,
        scopes,
        enabled: true,
        org_id: Some(org.id.clone()),
        issuer_url,
        client_id: Some(client_id),
        client_secret_box: Some(secret_box),
        authorization_endpoint: Some(authorization_endpoint),
        token_endpoint: Some(token_endpoint),
        userinfo_endpoint,
        idp_entity_id: None,
        sso_redirect_url: None,
        x509_certificate_pem: None,
        sp_entity_id: None,
        created_at: now(),
    };
    persist_connection(
        state,
        context,
        &connection,
        "connection.created",
        Some(provider),
    )
    .await
}

async fn persist_connection(
    state: &AppState,
    context: &AdminContext,
    connection: &Connection,
    event_type: &str,
    provider: Option<&str>,
) -> Result<(), CreateError> {
    state
        .store
        .create_connection(connection)
        .await
        .map_err(|error| {
            tracing::error!(?error, "admin connection persistence failed");
            CreateError::internal()
        })?;
    let mut data = serde_json::Map::from_iter([
        (
            "org_id".to_owned(),
            serde_json::Value::String(connection.org_id.clone().unwrap_or_default()),
        ),
        (
            "connection_id".to_owned(),
            serde_json::Value::String(connection.id.clone()),
        ),
    ]);
    if let Some(provider) = provider {
        data.insert(
            "provider".to_owned(),
            serde_json::Value::String(provider.to_owned()),
        );
    }
    let event = AuditEvent {
        id: format!("evt_{}", random_token(20)),
        instance_id: context.instance.id.clone(),
        r#type: event_type.to_owned(),
        actor_id: Some(context.user.id.clone()),
        data,
        created_at: now(),
        integrity_version: None,
        integrity_hash: None,
        integrity_signature: None,
        integrity_key_fingerprint: None,
    };
    state
        .store
        .create_audit_event(&event)
        .await
        .map_err(|error| {
            tracing::error!(?error, "admin connection audit write failed");
            CreateError::internal()
        })?;
    Ok(())
}

async fn validate_issuer(value: &str) -> Result<Url, CreateError> {
    if value.len() > 2048 {
        return Err(CreateError::bad_request(
            "Issuer URL must be 2048 bytes or fewer.",
        ));
    }
    let issuer = Url::parse(value)
        .map_err(|_| CreateError::bad_request("Issuer must be an absolute HTTPS URL."))?;
    if issuer.scheme() != "https"
        || issuer.host().is_none()
        || !issuer.username().is_empty()
        || issuer.password().is_some()
        || issuer.query().is_some()
        || issuer.fragment().is_some()
    {
        return Err(CreateError::bad_request(
            "Issuer must be an absolute HTTPS URL without credentials, query, or fragment.",
        ));
    }
    resolve_public_host(&issuer)
        .await
        .map_err(|_| CreateError::bad_request("Issuer must resolve to a public HTTPS host."))?;
    Ok(issuer)
}

async fn validate_public_https_endpoint(value: &str) -> Result<(), CreateError> {
    if value.len() > 4096 {
        return Err(CreateError::bad_request(
            "The provider discovery document contains an oversized endpoint.",
        ));
    }
    let endpoint = Url::parse(value).map_err(|_| {
        CreateError::bad_request("The provider discovery document contains an invalid endpoint.")
    })?;
    if endpoint.scheme() != "https"
        || endpoint.host().is_none()
        || !endpoint.username().is_empty()
        || endpoint.password().is_some()
        || endpoint.fragment().is_some()
    {
        return Err(CreateError::bad_request(
            "Provider authorization, token, and userinfo endpoints must use public HTTPS URLs.",
        ));
    }
    resolve_public_host(&endpoint).await.map_err(|_| {
        CreateError::bad_request("Provider endpoints must resolve to public IP addresses.")
    })?;
    Ok(())
}

async fn fetch_discovery(issuer: &Url) -> Result<Discovery, CreateError> {
    let client = pinned_public_client(issuer)
        .await
        .map_err(|_| CreateError::bad_request("The provider discovery host is unavailable."))?;
    let discovery_url = format!(
        "{}/.well-known/openid-configuration",
        issuer.as_str().trim_end_matches('/')
    );
    let response = client
        .get(&discovery_url)
        .header(header::ACCEPT, "application/json")
        .header(
            header::USER_AGENT,
            "TerraScale-Auth/1.0 (OpenID Connect provider discovery)",
        )
        .send()
        .await
        .map_err(|_| {
            CreateError::bad_request("The provider discovery document could not be reached.")
        })?;
    if !response.status().is_success() {
        return Err(CreateError::bad_request(
            "The provider discovery document could not be reached.",
        ));
    }
    if response
        .content_length()
        .is_some_and(|length| length > MAX_DISCOVERY_BYTES as u64)
    {
        return Err(CreateError::bad_request(
            "The provider discovery document is too large.",
        ));
    }
    let mut response = response;
    let mut bytes = Vec::new();
    while let Some(chunk) = response.chunk().await.map_err(|_| {
        CreateError::bad_request("The provider discovery document could not be read.")
    })? {
        if bytes.len().saturating_add(chunk.len()) > MAX_DISCOVERY_BYTES {
            return Err(CreateError::bad_request(
                "The provider discovery document is too large.",
            ));
        }
        bytes.extend_from_slice(&chunk);
    }
    serde_json::from_slice(&bytes)
        .map_err(|_| CreateError::bad_request("The provider discovery response is not valid JSON."))
}

/// Create an outbound HTTPS client pinned to a public address for an upstream provider URL.
///
/// This keeps hostname resolution and connection establishment in one request boundary, so
/// redirects and DNS rebinding cannot move provider traffic to a private network destination.
pub async fn pinned_public_client(url: &Url) -> Result<reqwest::Client, &'static str> {
    if url.scheme() != "https"
        || url.host().is_none()
        || !url.username().is_empty()
        || url.password().is_some()
        || url.fragment().is_some()
    {
        return Err("provider URL must be public HTTPS without user credentials or a fragment");
    }
    let destination = resolve_public_host(url).await?;
    let host = url
        .host_str()
        .ok_or("provider URL must include a hostname")?;
    let port = url
        .port_or_known_default()
        .ok_or("provider URL has no valid port")?;
    reqwest::Client::builder()
        .no_proxy()
        .redirect(reqwest::redirect::Policy::none())
        .connect_timeout(Duration::from_secs(5))
        .timeout(Duration::from_secs(15))
        .resolve(host, SocketAddr::new(destination, port))
        .build()
        .map_err(|_| "provider HTTP client could not be created")
}

async fn resolve_public_host(url: &Url) -> Result<IpAddr, &'static str> {
    let port = url
        .port_or_known_default()
        .ok_or("provider URL has no valid port")?;
    let addresses: Vec<SocketAddr> = match url.host().ok_or("provider URL must include a host")? {
        Host::Ipv4(address) => vec![SocketAddr::new(IpAddr::V4(address), port)],
        Host::Ipv6(address) => vec![SocketAddr::new(IpAddr::V6(address), port)],
        Host::Domain(host) => tokio::net::lookup_host((host, port))
            .await
            .map_err(|_| "provider URL hostname did not resolve")?
            .collect(),
    };
    if addresses.is_empty() || addresses.iter().any(|address| !is_public_ip(address.ip())) {
        return Err("provider URL must resolve only to public IP addresses");
    }
    Ok(addresses[0].ip())
}

fn is_public_ip(address: IpAddr) -> bool {
    match address {
        IpAddr::V4(ip) => {
            let [a, b, c, _] = ip.octets();
            !(ip.is_private()
                || ip.is_loopback()
                || ip.is_link_local()
                || ip.is_broadcast()
                || ip.is_unspecified()
                || a == 0
                || a >= 224
                || (a == 100 && (64..=127).contains(&b))
                || (a == 192 && b == 0 && (c == 0 || c == 2))
                || (a == 192 && b == 88 && c == 99)
                || (a == 198 && (b == 18 || b == 19 || (b == 51 && c == 100)))
                || (a == 203 && b == 0 && c == 113))
        }
        IpAddr::V6(ip) => {
            let segments = ip.segments();
            ip.to_ipv4_mapped().is_none()
                && (segments[0] & 0xe000) == 0x2000
                && segments[0] != 0x2002
                && !(segments[0] == 0x2001 && (segments[1] <= 0x01ff || segments[1] == 0x0db8))
        }
    }
}

fn valid_certificate_pem(value: &str) -> bool {
    crate::features::connections::saml::validate_idp_certificate_pem(value).is_ok()
}

fn valid_saml_sso_url(value: &str) -> bool {
    Url::parse(value).is_ok_and(|url| {
        url.scheme() == "https"
            && url.host_str().is_some()
            && url.username().is_empty()
            && url.password().is_none()
            && url.fragment().is_none()
    })
}

fn required_trimmed(
    value: Option<&str>,
    missing: &'static str,
    limit: usize,
) -> Result<String, CreateError> {
    let value = value.unwrap_or_default().trim();
    if value.is_empty() {
        return Err(CreateError::bad_request(missing));
    }
    if value.len() > limit {
        return Err(CreateError::bad_request(
            "A connection field exceeds its supported length.",
        ));
    }
    Ok(value.to_owned())
}

fn valid_uuid(value: &str) -> bool {
    value.len() == 36
        && value.bytes().enumerate().all(|(index, byte)| {
            if matches!(index, 8 | 13 | 18 | 23) {
                byte == b'-'
            } else {
                byte.is_ascii_hexdigit()
            }
        })
}

fn select_organization<'a>(
    organizations: &'a [Organization],
    requested: Option<&str>,
) -> Option<&'a Organization> {
    match requested.filter(|value| !value.trim().is_empty()) {
        Some(id) => organizations.iter().find(|org| org.id == id),
        None => organizations.first(),
    }
}

fn form_within_limits(form: &ConnectionForm) -> bool {
    [
        form.provider.as_deref(),
        form.org_id.as_deref(),
        form.name.as_deref(),
        form.client_id.as_deref(),
        form.client_secret.as_deref(),
        form.tenant_id.as_deref(),
        form.issuer_url.as_deref(),
        form.idp_entity_id.as_deref(),
        form.sso_redirect_url.as_deref(),
        form.x509_certificate_pem.as_deref(),
    ]
    .into_iter()
    .flatten()
    .all(|field| field.len() <= MAX_FORM_FIELD)
}

fn callback_url(origin: &str, slug: &str, connection: &Connection) -> Option<String> {
    let slug = encode_segment(slug);
    let connection_id = encode_segment(&connection.id);
    match connection.r#type.as_str() {
        "oidc" => Some(format!(
            "{origin}/ui/{slug}/connections/{connection_id}/oidc/callback"
        )),
        "github" => Some(format!(
            "{origin}/ui/{slug}/connections/{connection_id}/github/callback"
        )),
        "saml" => Some(format!(
            "{origin}/{slug}/connections/{connection_id}/saml/acs"
        )),
        _ => None,
    }
}

pub(super) fn callback_url_for_portal(
    origin: &str,
    slug: &str,
    connection: &Connection,
) -> Option<String> {
    callback_url(origin, slug, connection)
}

fn provider_sign_in_redirect(slug: &str) -> Response {
    let slug = encode_segment(slug);
    Redirect::to(&format!("/ui/{slug}/sign-in?next=%2Fui%2F{slug}%2Fadmin")).into_response()
}

fn datastar_patch_event(fragment: &str) -> String {
    let safe_fragment = fragment.replace('\r', "&#13;").replace('\n', "&#10;");
    format!("event: datastar-patch-elements\ndata: elements {safe_fragment}\n\n")
}

fn encode_segment(value: &str) -> String {
    let mut result = String::with_capacity(value.len());
    for byte in value.bytes() {
        if byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_' | b'.' | b'~') {
            result.push(byte as char);
        } else {
            result.push_str(&format!("%{byte:02X}"));
        }
    }
    result
}

fn now() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |time| time.as_secs() as i64)
}

#[derive(Debug)]
struct CreateError {
    status: Option<StatusCode>,
    message: String,
}

impl CreateError {
    fn bad_request(message: impl Into<String>) -> Self {
        Self {
            status: Some(StatusCode::BAD_REQUEST),
            message: message.into(),
        }
    }
    fn internal() -> Self {
        Self {
            status: None,
            message: "An unexpected error occurred.".to_owned(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use auth_contracts::models::OrganizationStatus;

    #[test]
    fn provider_fragments_are_fixed_markup_and_scope_org_choices() {
        let organizations = vec![
            Organization {
                id: "org_a".into(),
                instance_id: "ins_1".into(),
                name: "<One>".into(),
                slug_key: "ins_1:one".into(),
                status: OrganizationStatus::Active,
                public_metadata: Default::default(),
                created_at: 1,
            },
            Organization {
                id: "org_b".into(),
                instance_id: "ins_1".into(),
                name: "Two".into(),
                slug_key: "ins_1:two".into(),
                status: OrganizationStatus::Active,
                public_metadata: Default::default(),
                created_at: 2,
            },
        ];
        let fragment = provider_setup_html("oidc", "demo", &organizations).unwrap();
        assert!(fragment.contains("&lt;One&gt;"));
        assert!(fragment.contains("value=\"org_a\""));
        assert!(fragment.contains("name=\"client_secret\" type=\"password\""));
        assert!(!fragment.contains("data-on:"));
        assert!(provider_setup_html("unknown", "demo", &organizations).is_none());
    }

    #[test]
    fn datastar_fragments_cannot_inject_additional_sse_fields_or_events() {
        let event = datastar_patch_event("<div>alpha\n\nevent: forged\rdata: ignored</div>");
        let mut lines = event.lines();
        assert_eq!(lines.next(), Some("event: datastar-patch-elements"));
        let data = lines.next().unwrap();
        assert!(data.starts_with(
            "data: elements <div>alpha&#10;&#10;event: forged&#13;data: ignored</div>"
        ));
        assert_eq!(lines.next(), Some(""));
        assert!(lines.next().is_none());
    }

    #[test]
    fn certificate_validation_uses_the_runtime_x509_parser() {
        const VALID_CERTIFICATE: &str = "-----BEGIN CERTIFICATE-----\nMIIDGzCCAgOgAwIBAgIUQoszMz9XkUsVsIcwGexKBCJeGgEwDQYJKoZIhvcNAQEL\nBQAwHTEbMBkGA1UEAwwSdHMtYXV0aC1hZG1pbi10ZXN0MB4XDTI2MDkyNDA5MzE1\nN1oXDTM2MDkyMTA5MzE1N1owHTEbMBkGA1UEAwwSdHMtYXV0aC1hZG1pbi10ZXN0\nMIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAuhdkV8ZnAoTsP/s+mJgZ\nWqu7jKF0GO4UPXgqdPVhDer/3JXfdS69s4XM23zqNHlHoXKQ07KrQYifyv0iAbBY\nzE/JwE1KinLkeGO+0S+XNq+RTZfawMChfReqv5XZJXJq4KRHGzndSbZOQcJ00TBN\nDhhRXhCMD0cP02PzOvQNB6faseNRlQ9DF5FTtjscKAbYjnqXWsdsQmgFWd1iVgpM\nQdwxsOzG1k9lzpTo1i/OpNIuaeWsggN1mI0z611gm+bFtVNYOJqldL5HmukjQb6Z\nKAoHVDmVHRa+S5ltyh33K5sK3Mjin84bA/kvpps4lzjd2KamB+z1gHB1i149iwNE\nCQIDAQABo1MwUTAdBgNVHQ4EFgQU1zuZtMhIZhIKsYlBarB+udzK3GIwHwYDVR0j\nBBgwFoAU1zuZtMhIZhIKsYlBarB+udzK3GIwDwYDVR0TAQH/BAUwAwEB/zANBgkq\nhkiG9w0BAQsFAAOCAQEAbD0KTOPfDCqH29lzf2fb9fAgnzaYUs4LnSHCmJ8+VWFF\n96TjTsdqEydAbg5doFgybt9JeYOqNKoLXgbTyTUT6LginjqEb+e6KF+jnJh2pJuE\n44aVOOxAEjJhshxQf78HoXvPRRgfN2yGemS8z4OtYWkB7M/msrUUEcLW1Qf/mrS5\nWBrcz51ZDNpEhN7v1Tnn4X+oHBXD9yfodr67jpJ2aSeMXn426LF12JbIHlYfDdtb\nPjrkBM3k20khDkZ+xVdXsfUZoEkRjpdCEL5V8AIyeBby4PG5dw+OW1eMkLwYqA38\nnPGzGtUO+wBLVvdDZT2sQorrhx7wpPW1OPxPNVcrLQ==\n-----END CERTIFICATE-----\n";
        assert!(valid_certificate_pem(VALID_CERTIFICATE));
        assert!(!valid_certificate_pem(
            "-----BEGIN CERTIFICATE-----\nYWJj\n-----END CERTIFICATE-----"
        ));
    }

    #[test]
    fn saml_idp_sso_endpoint_requires_https_and_rejects_url_credentials() {
        assert!(valid_saml_sso_url("https://idp.example/sso"));
        assert!(!valid_saml_sso_url("http://idp.example/sso"));
        assert!(!valid_saml_sso_url("https://user:secret@idp.example/sso"));
        assert!(!valid_saml_sso_url("https://idp.example/sso#fragment"));
    }

    #[test]
    fn endpoint_ip_filter_rejects_private_and_reserved_ranges() {
        assert!(!is_public_ip("127.0.0.1".parse().unwrap()));
        assert!(!is_public_ip("10.2.3.4".parse().unwrap()));
        assert!(!is_public_ip("100.64.0.1".parse().unwrap()));
        assert!(!is_public_ip("2001:db8::1".parse().unwrap()));
        assert!(is_public_ip("8.8.8.8".parse().unwrap()));
        assert!(is_public_ip("2606:4700:4700::1111".parse().unwrap()));
    }

    #[test]
    fn organization_selection_cannot_escape_the_live_admin_membership_list() {
        let organizations = vec![Organization {
            id: "org_allowed".into(),
            instance_id: "ins_1".into(),
            name: "Allowed".into(),
            slug_key: "ins_1:allowed".into(),
            status: OrganizationStatus::Active,
            public_metadata: Default::default(),
            created_at: 1,
        }];
        assert_eq!(
            select_organization(&organizations, None).map(|org| org.id.as_str()),
            Some("org_allowed")
        );
        assert_eq!(
            select_organization(&organizations, Some("org_allowed")).map(|org| org.id.as_str()),
            Some("org_allowed")
        );
        assert!(select_organization(&organizations, Some("org_other")).is_none());
    }

    #[test]
    fn saml_connection_shows_its_registered_acs_url() {
        let connection = Connection {
            id: "con_123".into(),
            instance_id: "ins_1".into(),
            r#type: "saml".into(),
            name: "Enterprise SSO".into(),
            scopes: Vec::new(),
            enabled: true,
            org_id: Some("org_1".into()),
            issuer_url: None,
            client_id: None,
            client_secret_box: None,
            authorization_endpoint: None,
            token_endpoint: None,
            userinfo_endpoint: None,
            idp_entity_id: Some("idp.example".into()),
            sso_redirect_url: Some("https://idp.example/sso".into()),
            x509_certificate_pem: None,
            sp_entity_id: None,
            created_at: 1,
        };
        assert_eq!(
            callback_url("https://auth.example", "tenant", &connection).as_deref(),
            Some("https://auth.example/tenant/connections/con_123/saml/acs")
        );
    }

    #[test]
    fn microsoft_tenant_id_requires_canonical_uuid_shape() {
        assert!(valid_uuid("01234567-89AB-CDEF-0123-456789ABCDEF"));
        assert!(!valid_uuid("tenant"));
        assert!(!valid_uuid("0123456789ABCDEF0123456789ABCDEF"));
    }
}
