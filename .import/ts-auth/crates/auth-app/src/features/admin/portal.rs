//! Session-gated administrator overview.

use std::collections::HashSet;

use auth_contracts::{
    AppError,
    models::{
        Connection, Instance, OrgMembership, Organization, OrganizationStatus, ScimDirectory, User,
    },
};
use auth_security::tokens::random_token;
use axum::{
    extract::{Path, State},
    http::{HeaderMap, HeaderName, HeaderValue, StatusCode, header},
    response::{IntoResponse, Redirect, Response},
};

use crate::{
    AppState,
    features::{ui, users},
};

use super::connections::callback_url_for_portal;

pub(super) struct AdminContext {
    pub instance: Instance,
    pub user: User,
    pub organizations: Vec<Organization>,
}

pub(super) async fn get_portal(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
) -> Response {
    let context = match resolve_admin(&state, &headers, &slug).await {
        Ok(Some(context)) => context,
        Ok(None) => return sign_in_redirect(&slug),
        Err(error) => return render_error(error.status, "Temporarily unavailable", &error.message),
    };
    if context.organizations.is_empty() {
        return render_error(
            StatusCode::FORBIDDEN,
            "Not authorized",
            "Only organization admins can use this portal.",
        );
    }

    let connections = match state.store.list_connections(&context.instance.id).await {
        Ok(items) => items,
        Err(error) => return store_error(error),
    };
    let directories = match state
        .store
        .list_scim_directories(&context.instance.id)
        .await
    {
        Ok(items) => items,
        Err(error) => return store_error(error),
    };
    let owned_orgs: HashSet<&str> = context
        .organizations
        .iter()
        .map(|org| org.id.as_str())
        .collect();
    let mut connections: Vec<_> = connections
        .into_iter()
        .filter(|connection| {
            connection
                .org_id
                .as_deref()
                .is_some_and(|id| owned_orgs.contains(id))
        })
        .collect();
    connections.sort_by_key(|connection| connection.created_at);
    let directories: Vec<_> = directories
        .into_iter()
        .filter(|directory| {
            directory
                .org_id
                .as_deref()
                .is_some_and(|id| owned_orgs.contains(id))
        })
        .collect();

    render_portal(&state, &context, &connections, &directories).into_response()
}

pub(super) async fn resolve_admin(
    state: &AppState,
    headers: &HeaderMap,
    slug: &str,
) -> Result<Option<AdminContext>, AppError> {
    let instance = state
        .store
        .find_instance_by_slug(slug)
        .await
        .map_err(internal)?;
    let Some(instance) = instance else {
        return Ok(None);
    };
    let Some(token) = session_token(headers) else {
        return Ok(None);
    };
    let Some((_, user)) = users::resolve_session(state, &instance.id, token).await? else {
        return Ok(None);
    };

    let organizations = state
        .store
        .list_organizations(&instance.id)
        .await
        .map_err(internal)?
        .into_iter()
        .filter(|org| is_active_organization_for_instance(org, &instance.id))
        .collect::<Vec<_>>();
    let memberships = if organizations.is_empty() {
        Vec::new()
    } else {
        state
            .store
            .list_memberships_by_user(&user.id)
            .await
            .map_err(internal)?
    };
    let mut admin_organizations = Vec::new();
    for organization in organizations {
        let mut has_eligible_admin_membership = false;
        for membership in memberships.iter().filter(|membership| {
            is_admin_membership_for_organization(
                membership,
                &instance.id,
                &user.id,
                &organization.id,
            )
        }) {
            if state
                .store
                .org_membership_auth_eligible(membership)
                .await
                .map_err(internal)?
            {
                has_eligible_admin_membership = true;
                break;
            }
        }
        if has_eligible_admin_membership {
            admin_organizations.push(organization);
        }
    }
    admin_organizations.sort_by_cached_key(|organization| organization.name.to_lowercase());
    Ok(Some(AdminContext {
        instance,
        user,
        organizations: admin_organizations,
    }))
}

fn is_active_organization_for_instance(organization: &Organization, instance_id: &str) -> bool {
    organization.instance_id == instance_id && organization.status == OrganizationStatus::Active
}

fn is_admin_membership_for_organization(
    membership: &OrgMembership,
    instance_id: &str,
    user_id: &str,
    organization_id: &str,
) -> bool {
    membership.instance_id == instance_id
        && membership.user_id == user_id
        && membership.org_id == organization_id
        && membership.role == "admin"
}

fn session_token(headers: &HeaderMap) -> Option<&str> {
    let cookie = headers.get(header::COOKIE)?.to_str().ok()?;
    let mut found = None;
    for part in cookie.split(';').map(str::trim) {
        let Some((name, value)) = part.split_once('=') else {
            continue;
        };
        if name == ui::SESSION_COOKIE_NAME {
            if found.is_some() || value.is_empty() {
                return None;
            }
            found = Some(value);
        }
    }
    found
}

fn sign_in_redirect(slug: &str) -> Response {
    let path = format!("/ui/{}/admin", encode_segment(slug));
    let next = url::form_urlencoded::byte_serialize(path.as_bytes()).collect::<String>();
    Redirect::to(&format!("/ui/{}/sign-in?next={next}", encode_segment(slug))).into_response()
}

fn render_portal(
    state: &AppState,
    context: &AdminContext,
    connections: &[Connection],
    directories: &[ScimDirectory],
) -> Response {
    let slug_path = encode_segment(&context.instance.slug);
    let origin = state.options.public_origin.trim_end_matches('/');
    let connections_html = if connections.is_empty() {
        "<div class=\"empty\">No connections yet. Add a provider to let people sign in.</div>"
            .to_owned()
    } else {
        connections
            .iter()
            .map(|connection| render_connection(connection, origin, &context.instance.slug))
            .collect::<Vec<_>>()
            .join("")
    };
    let directories_html = if directories.is_empty() {
        "<div class=\"empty\">No directories yet.</div>".to_owned()
    } else {
        directories.iter().map(|directory| format!(
            "<div class=\"row\"><div><strong>SCIM directory</strong><small>/scim/{}</small></div></div>",
            ui::escape_html(&directory.id),
        )).collect::<Vec<_>>().join("")
    };
    let organization_options = if context.organizations.len() < 2 {
        String::new()
    } else {
        let options = context
            .organizations
            .iter()
            .map(|org| {
                format!(
                    "<option value=\"{}\">{}</option>",
                    ui::escape_html(&org.id),
                    ui::escape_html(&org.name),
                )
            })
            .collect::<String>();
        format!(
            "<div class=\"field\"><label for=\"connection-org\">Organization</label><select class=\"input\" id=\"connection-org\" name=\"org_id\" required>{options}</select></div>"
        )
    };
    let error = "";
    let body = format!(
        r#"
<div class="shell">
  <aside class="sidebar">
    <a class="brand-link" href="/ui/{slug_path}/admin"><img src="/admin/logo.svg" alt="TerraScale"></a>
    <nav aria-label="Admin navigation">
      <div class="nav-caption">Authentication</div>
      <a class="nav-link active" href="/ui/{slug_path}/admin">Overview and providers</a>
      <a class="nav-link" href="/ui/{slug_path}/connections/list">Hosted sign-in</a>
      <a class="nav-link" href="/ui/{slug_path}/account">My account</a>
    </nav>
    <div class="sidebar-footer">{instance_name}<br>{user_label}</div>
  </aside>
  <main class="main">
    <div class="topline"><div><div class="eyebrow">Identity settings</div><h1>Authentication</h1><p class="subtitle">Configure how people sign in to {instance_name}.</p></div><span class="pill"><span class="dot"></span>Organization admin</span></div>
    {error}
    <div class="stats">
      <div class="stat"><span class="label">Connections</span><strong>{connection_count}</strong><small>Across your organizations</small></div>
      <div class="stat"><span class="label">Active</span><strong>{active_count}</strong><small>Available to sign in</small></div>
      <div class="stat"><span class="label">Directories</span><strong>{directory_count}</strong><small>SCIM provisioning</small></div>
    </div>
    <div class="grid">
      <div class="stack">
        <section class="panel" id="providers">
          <div class="panel-header"><h2>Add a sign-in provider</h2><p>Choose a provider, enter its app credentials, then add the callback URL in its console.</p></div>
          <div class="panel-body">
            <div class="provider-grid">
              <button class="provider" type="button" data-on:click="@get('/ui/{slug_path}/admin/providers/github')"><span class="provider-mark">G</span><span><strong>GitHub</strong><small>OAuth app</small></span><span class="provider-arrow" aria-hidden="true">→</span></button>
              <button class="provider" type="button" data-on:click="@get('/ui/{slug_path}/admin/providers/microsoft')"><span class="provider-mark">M</span><span><strong>Microsoft</strong><small>Entra ID</small></span><span class="provider-arrow" aria-hidden="true">→</span></button>
              <button class="provider" type="button" data-on:click="@get('/ui/{slug_path}/admin/providers/google')"><span class="provider-mark">G</span><span><strong>Google</strong><small>OpenID Connect</small></span><span class="provider-arrow" aria-hidden="true">→</span></button>
              <button class="provider" type="button" data-on:click="@get('/ui/{slug_path}/admin/providers/oidc')"><span class="provider-mark">↗</span><span><strong>Other OIDC</strong><small>Custom issuer</small></span><span class="provider-arrow" aria-hidden="true">→</span></button>
            </div>
            <div id="provider-setup" class="setup" aria-live="polite"></div>
          </div>
        </section>
        <section class="panel" id="saml">
          <div class="panel-header"><h2>Enterprise SAML</h2><p>Connect an identity provider for your organization.</p></div>
          <div class="panel-body">
            <form method="post" action="/ui/{slug_path}/admin/connections">
              {organization_options}
              <div class="field"><label for="saml-name">Name</label><input class="input" id="saml-name" name="name" maxlength="120" required></div>
              <div class="field"><label for="saml-entity">IdP entity ID</label><input class="input" id="saml-entity" name="idp_entity_id" maxlength="2048" required></div>
              <div class="field"><label for="saml-sso">SSO redirect URL</label><input class="input" id="saml-sso" name="sso_redirect_url" type="url" placeholder="https://idp.example/sso" required></div>
              <div class="field"><label for="saml-cert">X.509 certificate (PEM)</label><textarea class="input" id="saml-cert" name="x509_certificate_pem" autocomplete="off" placeholder="-----BEGIN CERTIFICATE-----" required></textarea></div>
              <div class="form-actions"><button class="btn btn-primary" type="submit">Add SAML connection</button><small>Signing certificates are public keys and remain visible to authorized admins.</small></div>
            </form>
          </div>
        </section>
      </div>
      <div class="stack">
        <section class="panel" id="connections"><div class="panel-header"><h2>Connections</h2><p>Manage providers available to your organizations.</p></div><div class="panel-body">{connections_html}</div></section>
        <section class="panel"><div class="panel-header"><h2>Directories</h2><p>Provisioned by SCIM.</p></div><div class="panel-body">{directories_html}</div></section>
        <section class="panel"><div class="panel-header"><h2>How this works</h2></div><div class="panel-body"><div class="hint-card"><strong>1. Create an app</strong><span>Register an OAuth or OIDC app with your provider.</span><strong>2. Add credentials here</strong><span>The client secret is encrypted before it is stored.</span><strong>3. Set the callback URL</strong><span>Copy the exact URL shown with each connection and add it in the provider console.</span></div></div></section>
      </div>
    </div>
  </main>
</div>"#,
        slug_path = slug_path,
        instance_name = ui::escape_html(&context.instance.name),
        user_label = ui::escape_html(
            context
                .user
                .email
                .as_deref()
                .or(context.user.name.as_deref())
                .unwrap_or(&context.user.id)
        ),
        connection_count = connections.len(),
        active_count = connections
            .iter()
            .filter(|connection| connection.enabled)
            .count(),
        directory_count = directories.len(),
        organization_options = organization_options,
        connections_html = connections_html,
        directories_html = directories_html,
        error = error,
    );
    html_response("Authentication", &body, StatusCode::OK)
}

fn render_connection(connection: &Connection, origin: &str, slug: &str) -> String {
    let callback = callback_url_for_portal(origin, slug, connection);
    let slug_path = encode_segment(slug);
    let provider = match connection.r#type.as_str() {
        "github" => "GitHub",
        "oidc" => "OpenID Connect",
        "saml" => "SAML",
        other => other,
    };
    let state = if connection.enabled {
        "Enabled"
    } else {
        "Disabled"
    };
    let callback_text = callback
        .as_deref()
        .map(|value| format!(" · Callback: {}", ui::escape_html(value)))
        .unwrap_or_default();
    let connection_id = encode_segment(&connection.id);
    let action = if connection.enabled {
        "disable"
    } else {
        "enable"
    };
    let button = if connection.enabled {
        "Disable"
    } else {
        "Enable"
    };
    format!(
        "<div class=\"row\"><div><strong>{}</strong><small>{} · {}{}</small></div><form method=\"post\" action=\"/ui/{slug_path}/admin/connections/{connection_id}/{action}\"><button class=\"btn btn-secondary\" type=\"submit\">{button}</button></form></div>",
        ui::escape_html(&connection.name),
        ui::escape_html(provider),
        state,
        callback_text,
    )
}

pub(super) fn html_response(title: &str, body: &str, status: StatusCode) -> Response {
    let nonce = random_token(32);
    let html = format!(
        "<!doctype html><html lang=\"en\" data-theme=\"dark\" data-nonce=\"{nonce}\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><meta name=\"color-scheme\" content=\"dark\"><title>{} · TerraScale</title><link rel=\"icon\" href=\"/admin/logo.svg\" type=\"image/svg+xml\"><link rel=\"stylesheet\" href=\"/admin/admin.css\"><script type=\"module\" src=\"/admin/datastar.js\"></script></head><body>{}</body></html>",
        ui::escape_html(title),
        body,
    );
    let mut response = (
        status,
        [(header::CONTENT_TYPE, "text/html; charset=utf-8")],
        html,
    )
        .into_response();
    security_headers(&mut response, Some(&nonce));
    response
}

pub(super) fn error_response(status: StatusCode, title: &str, message: &str) -> Response {
    render_error(status, title, message)
}

fn render_error(status: StatusCode, title: &str, message: &str) -> Response {
    let body = format!(
        "<main class=\"main\"><section class=\"panel\"><div class=\"panel-header\"><h1>{}</h1><p>{}</p></div><div class=\"panel-body\"><a class=\"btn btn-secondary\" href=\"/\">Return home</a></div></section></main>",
        ui::escape_html(title),
        ui::escape_html(message)
    );
    html_response(title, &body, status)
}

pub(super) fn security_headers(response: &mut Response, script_nonce: Option<&str>) {
    let headers = response.headers_mut();
    headers.insert(header::CACHE_CONTROL, HeaderValue::from_static("no-store"));
    let script_sources = script_nonce
        .map(|nonce| format!("'self' 'nonce-{nonce}'"))
        .unwrap_or_else(|| "'self'".to_owned());
    let policy = format!(
        "default-src 'self'; script-src {script_sources}; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'; object-src 'none'"
    );
    headers.insert(
        HeaderName::from_static("content-security-policy"),
        HeaderValue::from_str(&policy).expect("the generated CSP nonce is an HTTP-safe token"),
    );
    headers.insert(
        HeaderName::from_static("x-content-type-options"),
        HeaderValue::from_static("nosniff"),
    );
    headers.insert(
        HeaderName::from_static("x-frame-options"),
        HeaderValue::from_static("DENY"),
    );
    headers.insert(
        HeaderName::from_static("referrer-policy"),
        HeaderValue::from_static("strict-origin-when-cross-origin"),
    );
}

fn store_error(error: impl std::fmt::Debug) -> Response {
    tracing::error!(?error, "admin portal storage request failed");
    render_error(
        StatusCode::INTERNAL_SERVER_ERROR,
        "Temporarily unavailable",
        "The administrator portal could not load your settings. Please try again.",
    )
}

fn internal(error: impl std::fmt::Debug) -> AppError {
    tracing::error!(?error, "admin portal authorization lookup failed");
    AppError::internal()
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

#[cfg(test)]
mod tests {
    use super::*;
    use auth_contracts::models::{Connection, OrganizationStatus};

    #[test]
    fn admin_access_excludes_deleting_and_deleted_organizations() {
        let active = Organization {
            id: "org_a".into(),
            instance_id: "ins_1".into(),
            name: "Active".into(),
            slug_key: "ins_1:active".into(),
            status: OrganizationStatus::Active,
            public_metadata: Default::default(),
            created_at: 1,
        };
        let deleting = Organization {
            status: OrganizationStatus::Deleting,
            ..active.clone()
        };
        let deleted = Organization {
            status: OrganizationStatus::Deleted,
            ..active.clone()
        };

        assert!(is_active_organization_for_instance(&active, "ins_1"));
        assert!(!is_active_organization_for_instance(&active, "ins_other"));
        assert!(!is_active_organization_for_instance(&deleting, "ins_1"));
        assert!(!is_active_organization_for_instance(&deleted, "ins_1"));
    }

    #[test]
    fn admin_memberships_are_scoped_to_the_user_instance_and_organization() {
        let admin = OrgMembership {
            id: "mem_1".into(),
            instance_id: "ins_1".into(),
            org_id: "org_1".into(),
            user_id: "usr_1".into(),
            member_key: "ins_1:org_1:usr_1".into(),
            role: "admin".into(),
            created_at: 1,
        };

        assert!(is_admin_membership_for_organization(
            &admin, "ins_1", "usr_1", "org_1"
        ));
        assert!(!is_admin_membership_for_organization(
            &admin,
            "ins_other",
            "usr_1",
            "org_1"
        ));
        assert!(!is_admin_membership_for_organization(
            &admin,
            "ins_1",
            "usr_other",
            "org_1"
        ));
        assert!(!is_admin_membership_for_organization(
            &admin,
            "ins_1",
            "usr_1",
            "org_other"
        ));
        assert!(!is_admin_membership_for_organization(
            &OrgMembership {
                role: "member".into(),
                ..admin
            },
            "ins_1",
            "usr_1",
            "org_1"
        ));
    }

    #[test]
    fn session_token_skips_malformed_cookie_segments() {
        let mut headers = HeaderMap::new();
        headers.insert(
            header::COOKIE,
            HeaderValue::from_str(&format!(
                "flag; {}=session-token; malformed",
                ui::SESSION_COOKIE_NAME
            ))
            .expect("cookie header is valid"),
        );

        assert_eq!(session_token(&headers), Some("session-token"));
    }

    #[tokio::test]
    async fn admin_html_nonce_matches_its_csp_without_enabling_unsafe_eval() {
        let response = html_response("Authentication", "<main>Admin</main>", StatusCode::OK);
        let csp = response
            .headers()
            .get(HeaderName::from_static("content-security-policy"))
            .unwrap()
            .to_str()
            .unwrap()
            .to_owned();
        let body = axum::body::to_bytes(response.into_body(), 4096)
            .await
            .unwrap();
        let html = std::str::from_utf8(&body).unwrap();
        let nonce = html
            .split_once("data-nonce=\"")
            .unwrap()
            .1
            .split_once('"')
            .unwrap()
            .0;

        assert_eq!(nonce.len(), 32);
        assert!(nonce.bytes().all(|byte| byte.is_ascii_alphanumeric()));
        assert!(csp.contains(&format!("'nonce-{nonce}'")));
        assert!(!csp.contains("unsafe-eval"));
    }

    #[test]
    fn connection_rendering_escapes_user_data_and_encodes_path_segments() {
        let connection = Connection {
            id: "con_abc".into(),
            instance_id: "ins_1".into(),
            r#type: "oidc".into(),
            name: "<img src=x onerror=alert(1)>".into(),
            scopes: vec!["openid".into(), "email".into(), "profile".into()],
            enabled: true,
            org_id: Some("org_1".into()),
            issuer_url: None,
            client_id: None,
            client_secret_box: Some("never-render-me".into()),
            authorization_endpoint: None,
            token_endpoint: None,
            userinfo_endpoint: None,
            idp_entity_id: None,
            sso_redirect_url: None,
            x509_certificate_pem: None,
            sp_entity_id: None,
            created_at: 1,
        };
        let html = render_connection(&connection, "https://auth.example", "tenant%2Fone");
        assert!(html.contains("&lt;img src=x onerror=alert(1)&gt;"));
        assert!(!html.contains("never-render-me"));
        assert!(
            html.contains(
                "https://auth.example/ui/tenant%252Fone/connections/con_abc/oidc/callback"
            )
        );
        assert!(html.contains("/admin/connections/con_abc/disable"));
        assert!(html.contains("action=\"/ui/tenant%252Fone/admin/connections/con_abc/disable\""));
        assert_eq!(encode_segment("tenant/one"), "tenant%2Fone");
    }
}
