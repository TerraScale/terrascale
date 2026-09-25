//! RFC 8628 device authorization. One conditional-write record owns pairing,
//! approval, poll cadence and consumption, including across replicas.

use super::rate_limit::allow_attempt;
use super::*;
use crate::{
    app::storage_error,
    features::{ui, users},
};
use auth_contracts::models::{Instance, Session, User};

pub(super) const GRANT_TYPE: &str = "urn:ietf:params:oauth:grant-type:device_code";
const COLLECTION: &str = "oauth_device_grants";
const TTL: i64 = 600;
const POLL_INTERVAL: i64 = 5;
const ALPHABET: &[u8; 32] = b"ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
enum GrantStatus {
    Pending,
    Approved,
    Denied,
    Consumed,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Approval {
    user_id: String,
    session_id: String,
    auth_epoch: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct DeviceGrant {
    id: String,
    instance_id: String,
    client_id: String,
    device_code_hash: String,
    scopes: Vec<String>,
    status: GrantStatus,
    approval: Option<Approval>,
    created_at: i64,
    expires_at: i64,
    interval: i64,
    last_polled_at: Option<i64>,
}

#[derive(Deserialize, ToSchema)]
#[allow(dead_code)]
struct DeviceAuthorizationRequest {
    client_id: String,
    client_secret: Option<String>,
    scope: Option<String>,
}

#[derive(Serialize, ToSchema)]
struct DeviceAuthorizationResponse {
    device_code: String,
    user_code: String,
    verification_uri: String,
    verification_uri_complete: String,
    expires_in: i64,
    interval: i64,
}

#[derive(Deserialize, ToSchema)]
struct Decision {
    user_code: String,
    decision: String,
    csrf_token: String,
}

#[derive(OpenApi)]
#[openapi(paths(authorize_device, verification, decide),
    components(schemas(DeviceAuthorizationRequest, DeviceAuthorizationResponse, Decision)),
    tags((name = "OAuth Device", description = "RFC 8628 device authorization and explicit hosted approval")))]
struct DeviceApiDoc;

pub(super) fn openapi() -> utoipa::openapi::OpenApi {
    DeviceApiDoc::openapi()
}

pub(super) fn router() -> Router<AppState> {
    Router::new()
        .route("/{slug}/oauth/device_authorization", post(authorize_device))
        .route("/ui/{slug}/device", get(verification).post(decide))
}

fn protocol_error(error: &str, message: &str) -> Response {
    token_error(StatusCode::BAD_REQUEST, error, message, false)
}

fn normalize_code(raw: &str) -> Option<String> {
    if raw.len() > 32 {
        return None;
    }
    let code: String = raw
        .trim()
        .bytes()
        .filter(|b| *b != b'-')
        .map(|b| (b as char).to_ascii_uppercase())
        .collect();
    (code.len() == 10 && code.bytes().all(|b| ALPHABET.contains(&b))).then_some(code)
}

fn grant_id(instance_id: &str, code: &str) -> String {
    format!(
        "device_{}",
        tokens::sha256_hex(&format!("{instance_id}:{code}"))
    )
}

#[utoipa::path(post, path = "/{slug}/oauth/device_authorization", params(("slug" = String, Path)),
    request_body(content = DeviceAuthorizationRequest, content_type = "application/x-www-form-urlencoded"),
    responses((status = 200, description = "Display user_code and verification_uri to the user; poll token endpoint at interval", body = DeviceAuthorizationResponse), (status = 400, description = "Invalid request or scope", body = OAuthErrorResponse), (status = 401, description = "Invalid OAuth client", body = OAuthErrorResponse), (status = 429, description = "Client issuance limit exceeded")), tag = "OAuth Device")]
async fn authorize_device(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Form(form): Form<HashMap<String, String>>,
) -> AppResult<Response> {
    let instance = state
        .store
        .find_instance_by_slug(&slug)
        .await
        .map_err(|e| storage_error(&e))?
        .ok_or_else(|| AppError::new(StatusCode::NOT_FOUND, "not_found", "No such instance."))?;
    let client = match authenticate_client(&state.store, &instance.id, &headers, &form).await {
        Ok(client) => client,
        Err(error) => return Ok(client_authentication_error_response(error)),
    };
    let scopes: Vec<String> = form
        .get("scope")
        .map_or("openid", String::as_str)
        .split_ascii_whitespace()
        .map(str::to_owned)
        .collect();
    if scopes.is_empty()
        || scopes.len() > SUPPORTED_SCOPES.len()
        || scopes
            .iter()
            .any(|s| !SUPPORTED_SCOPES.contains(&s.as_str()))
    {
        return Ok(protocol_error(
            "invalid_scope",
            "supported scopes: openid profile email",
        ));
    }
    if !allow_attempt(&state, &instance.id, &client.client_id, "issue", 60).await? {
        let mut response = token_error(
            StatusCode::TOO_MANY_REQUESTS,
            "slow_down",
            "too many device authorization requests",
            false,
        );
        response
            .headers_mut()
            .insert(header::RETRY_AFTER, HeaderValue::from_static("60"));
        return Ok(response);
    }
    for _ in 0..5 {
        let code: String = tokens::random_bytes::<10>()
            .iter()
            .map(|b| ALPHABET[usize::from(*b & 31)] as char)
            .collect();
        let device_code = format!("{code}.{}", tokens::random_token(64));
        let record = DeviceGrant {
            id: grant_id(&instance.id, &code),
            instance_id: instance.id.clone(),
            client_id: client.client_id.clone(),
            device_code_hash: tokens::sha256_hex(&device_code),
            scopes: scopes.clone(),
            status: GrantStatus::Pending,
            approval: None,
            created_at: now(),
            expires_at: now() + TTL,
            interval: POLL_INTERVAL,
            last_polled_at: None,
        };
        match state.store.create(COLLECTION, &record.id, &record).await {
            Ok(()) => {}
            Err(e) if e.is_conflict() || e.is_precondition() => continue,
            Err(e) => return Err(storage_error(&e)),
        }
        let user_code = format!("{}-{}", &code[..5], &code[5..]);
        let verification_uri = format!(
            "{}/ui/{}/device",
            state.options.public_origin.trim_end_matches('/'),
            instance.slug
        );
        let verification_uri_complete = format!("{verification_uri}?user_code={user_code}");
        audit(
            &state,
            &instance.id,
            None,
            "oauth.device_requested",
            json!({"client_id":client.client_id}),
        )
        .await?;
        return Ok(no_store(
            Json(DeviceAuthorizationResponse {
                device_code,
                user_code,
                verification_uri,
                verification_uri_complete,
                expires_in: TTL,
                interval: POLL_INTERVAL,
            })
            .into_response(),
        ));
    }
    Err(AppError::unavailable())
}

fn confirmation_token(state: &AppState, record: &DeviceGrant, session: &Session) -> String {
    let value = format!(
        "device-approval:{}:{}:{}:{}",
        record.id, record.created_at, record.client_id, session.id
    );
    base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(tokens::hmac_sha256(
        &state.options.master_key,
        value.as_bytes(),
    ))
}

fn device_page(instance: &Instance, body: &str, status: StatusCode) -> Response {
    no_store(ui::page(
        "Authorize a device",
        &format!("<section id=\"device-authorization\">{body}</section>"),
        Some("<script type=\"module\" src=\"/admin/datastar.js\"></script>"),
        status,
        instance.theme_color.as_deref(),
        instance.logo_url.as_deref(),
    ))
}

fn completed(instance: &Instance, headers: &HeaderMap, body: &str) -> Response {
    if headers
        .get("datastar-request")
        .and_then(|v| v.to_str().ok())
        == Some("true")
    {
        let fragment = format!("<section id=\"device-authorization\">{body}</section>")
            .replace('\r', "&#13;")
            .replace('\n', "&#10;");
        no_store(
            (
                [(header::CONTENT_TYPE, "text/event-stream")],
                format!("event: datastar-patch-elements\ndata: elements {fragment}\n\n"),
            )
                .into_response(),
        )
    } else {
        device_page(instance, body, StatusCode::OK)
    }
}

async fn browser_session(
    state: &AppState,
    instance: &Instance,
    headers: &HeaderMap,
) -> AppResult<Option<(Session, User)>> {
    match cookie_value(headers, SESSION_COOKIE) {
        Some(raw) => users::resolve_session(state, &instance.id, raw).await,
        None => Ok(None),
    }
}

#[utoipa::path(get, path = "/ui/{slug}/device", params(("slug" = String, Path), ("user_code" = Option<String>, Query)),
    responses((status = 200, description = "Enter a code or explicitly confirm client and scopes"), (status = 303, description = "Sign in before approval")), tag = "OAuth Device")]
async fn verification(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    Query(query): Query<HashMap<String, String>>,
    headers: HeaderMap,
) -> AppResult<Response> {
    let instance = state
        .store
        .find_instance_by_slug(&slug)
        .await
        .map_err(|e| storage_error(&e))?
        .ok_or_else(|| AppError::new(StatusCode::NOT_FOUND, "not_found", "No such instance."))?;
    let raw_code = query
        .get("user_code")
        .map(String::as_str)
        .unwrap_or_default();
    let Some((session, user)) = browser_session(&state, &instance, &headers).await? else {
        let next = format!(
            "/ui/{}/device?user_code={}",
            instance.slug,
            urlencoding_fallback(raw_code)
        );
        return Ok(no_store(
            Redirect::to(&format!(
                "/ui/{}/sign-in?next={}",
                instance.slug,
                urlencoding_fallback(&next)
            ))
            .into_response(),
        ));
    };
    if raw_code.is_empty() {
        return Ok(device_page(
            &instance,
            "<h1>Authorize a device</h1><p>Enter the code displayed on the device you are signing in to.</p><form method=\"get\"><label for=\"user-code\">Device code</label><input id=\"user-code\" name=\"user_code\" required maxlength=\"32\" autocomplete=\"off\" autocapitalize=\"characters\"><button>Continue</button></form>",
            StatusCode::OK,
        ));
    }
    if !allow_attempt(&state, &instance.id, &user.id, "verify", 20).await? {
        return Ok(device_page(
            &instance,
            "<h1>Too many attempts</h1><p>Wait one minute before trying again.</p>",
            StatusCode::TOO_MANY_REQUESTS,
        ));
    }
    let record = match normalize_code(raw_code) {
        Some(code) => state
            .store
            .get::<DeviceGrant>(COLLECTION, &grant_id(&instance.id, &code))
            .await
            .map_err(|e| storage_error(&e))?,
        None => None,
    };
    let Some(record) = record.filter(|g| {
        g.instance_id == instance.id && g.status == GrantStatus::Pending && g.expires_at > now()
    }) else {
        return Ok(device_page(
            &instance,
            "<h1>Code unavailable</h1><p>This code is invalid, expired, or already used. Request a new code on your device.</p>",
            StatusCode::BAD_REQUEST,
        ));
    };
    let Some(client) = state
        .store
        .find_oauth_client(&instance.id, &record.client_id)
        .await
        .map_err(|e| storage_error(&e))?
    else {
        return Ok(device_page(
            &instance,
            "<h1>Application unavailable</h1>",
            StatusCode::BAD_REQUEST,
        ));
    };
    let code = normalize_code(raw_code).expect("record lookup validated the code");
    let csrf = confirmation_token(&state, &record, &session);
    let action = format!("/ui/{}/device", instance.slug);
    let form = |decision: &str, label: &str| {
        format!(
            "<form method=\"post\" action=\"{action}\" data-on:submit=\"@post('{action}', {{contentType: 'form'}})\"><input type=\"hidden\" name=\"user_code\" value=\"{code}\"><input type=\"hidden\" name=\"csrf_token\" value=\"{csrf}\"><input type=\"hidden\" name=\"decision\" value=\"{decision}\"><button>{label}</button></form>"
        )
    };
    let body = format!(
        "<h1>Authorize {}</h1><p>Signed in as <strong>{}</strong>.</p><p>Confirm that <strong>{}-{}</strong> matches the code on your device.</p><p>Requested access: <strong>{}</strong>.</p><p>Only approve if you started this sign-in. Someone asking you to approve their code could gain access to your account.</p>{}{}",
        ui::escape_html(&client.name),
        ui::escape_html(user.email.as_deref().unwrap_or(&user.id)),
        &code[..5],
        &code[5..],
        ui::escape_html(&record.scopes.join(", ")),
        form("approve", "Authorize device"),
        form("deny", "Deny")
    );
    Ok(device_page(&instance, &body, StatusCode::OK))
}

#[utoipa::path(post, path = "/ui/{slug}/device", params(("slug" = String, Path)), security(("SessionCookie" = [])),
    request_body(content = Decision, content_type = "application/x-www-form-urlencoded"),
    responses((status = 200, description = "Explicit approval or denial recorded"), (status = 403, description = "Invalid origin or confirmation token"), (status = 409, description = "Code already decided")), tag = "OAuth Device")]
async fn decide(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Form(decision): Form<Decision>,
) -> AppResult<Response> {
    if !users::require_same_origin(&headers, &state.options.public_origin) {
        return Err(AppError::new(
            StatusCode::FORBIDDEN,
            "invalid_origin",
            "Reload this page before approving.",
        ));
    }
    let instance = state
        .store
        .find_instance_by_slug(&slug)
        .await
        .map_err(|e| storage_error(&e))?
        .ok_or_else(|| AppError::new(StatusCode::NOT_FOUND, "not_found", "No such instance."))?;
    let (session, user) = browser_session(&state, &instance, &headers)
        .await?
        .ok_or_else(|| {
            AppError::new(
                StatusCode::UNAUTHORIZED,
                "session_required",
                "Sign in before approving.",
            )
        })?;
    if !matches!(decision.decision.as_str(), "approve" | "deny") {
        return Ok(protocol_error(
            "invalid_request",
            "decision must be approve or deny",
        ));
    }
    if !allow_attempt(&state, &instance.id, &user.id, "verify", 20).await? {
        return Err(AppError::new(
            StatusCode::TOO_MANY_REQUESTS,
            "rate_limited",
            "Wait one minute before trying again.",
        ));
    }
    let Some(code) = normalize_code(&decision.user_code) else {
        return Ok(protocol_error("invalid_request", "invalid user code"));
    };
    for _ in 0..8 {
        let Some((mut record, version)) = state
            .store
            .get_versioned::<DeviceGrant>(COLLECTION, &grant_id(&instance.id, &code))
            .await
            .map_err(|e| storage_error(&e))?
        else {
            return Ok(protocol_error("invalid_request", "invalid user code"));
        };
        if record.instance_id != instance.id
            || record.expires_at <= now()
            || record.status != GrantStatus::Pending
        {
            return Err(AppError::new(
                StatusCode::CONFLICT,
                "device_code_unavailable",
                "This code expired or was already decided.",
            ));
        }
        if !tokens::fixed_time_equals(
            &decision.csrf_token,
            &confirmation_token(&state, &record, &session),
        ) {
            return Err(AppError::new(
                StatusCode::FORBIDDEN,
                "invalid_confirmation",
                "Reload and confirm the code before approving.",
            ));
        }
        if state
            .store
            .find_oauth_client(&instance.id, &record.client_id)
            .await
            .map_err(|e| storage_error(&e))?
            .is_none()
        {
            return Ok(protocol_error(
                "invalid_request",
                "application is no longer active",
            ));
        }
        record.status = if decision.decision == "approve" {
            GrantStatus::Approved
        } else {
            GrantStatus::Denied
        };
        record.approval = Some(Approval {
            user_id: user.id.clone(),
            session_id: session.id.clone(),
            auth_epoch: user.auth_epoch,
        });
        if !state
            .store
            .compare_and_put(COLLECTION, &record.id, &record, &version)
            .await
            .map_err(|e| storage_error(&e))?
        {
            continue;
        }
        audit(
            &state,
            &instance.id,
            Some(&user.id),
            if decision.decision == "approve" {
                "oauth.device_approved"
            } else {
                "oauth.device_denied"
            },
            json!({"client_id":record.client_id}),
        )
        .await?;
        return Ok(completed(
            &instance,
            &headers,
            if decision.decision == "approve" {
                "<h1>Device authorized</h1><p>You can return to your device. You may close this page.</p>"
            } else {
                "<h1>Request denied</h1><p>No access was granted to this device.</p>"
            },
        ));
    }
    Err(AppError::unavailable())
}

pub(super) async fn grant(
    state: &AppState,
    instance: &Instance,
    client: &OAuthClient,
    form: &HashMap<String, String>,
) -> AppResult<Response> {
    let Some(raw) = form.get("device_code").filter(|s| s.len() == 75) else {
        return Ok(protocol_error("invalid_request", "device_code is required"));
    };
    let Some((code, _)) = raw
        .split_once('.')
        .and_then(|(code, secret)| normalize_code(code).map(|code| (code, secret)))
    else {
        return Ok(protocol_error("invalid_grant", "invalid device code"));
    };
    for _ in 0..8 {
        let Some((mut record, version)) = state
            .store
            .get_versioned::<DeviceGrant>(COLLECTION, &grant_id(&instance.id, &code))
            .await
            .map_err(|e| storage_error(&e))?
        else {
            return Ok(protocol_error("invalid_grant", "invalid device code"));
        };
        if record.instance_id != instance.id
            || record.client_id != client.client_id
            || !tokens::fixed_time_equals(&record.device_code_hash, &tokens::sha256_hex(raw))
        {
            return Ok(protocol_error("invalid_grant", "invalid device code"));
        }
        if record.expires_at <= now() {
            return Ok(protocol_error("expired_token", "device code expired"));
        }
        match record.status {
            GrantStatus::Denied => {
                return Ok(protocol_error(
                    "access_denied",
                    "the user denied this request",
                ));
            }
            GrantStatus::Consumed => {
                return Ok(protocol_error("invalid_grant", "device code already used"));
            }
            _ => {}
        }
        let current_time = now();
        let too_fast = record
            .last_polled_at
            .is_some_and(|last| current_time < last.saturating_add(record.interval));
        record.last_polled_at = Some(current_time);
        if too_fast {
            record.interval = record.interval.saturating_add(5);
        }
        if too_fast || record.status == GrantStatus::Pending {
            if !state
                .store
                .compare_and_put(COLLECTION, &record.id, &record, &version)
                .await
                .map_err(|e| storage_error(&e))?
            {
                continue;
            }
            return Ok(if too_fast {
                protocol_error("slow_down", "increase the polling interval by five seconds")
            } else {
                protocol_error("authorization_pending", "the user has not decided yet")
            });
        }
        let Some(approval) = &record.approval else {
            return Err(AppError::internal());
        };
        let Some((user, session)) = approved_identity(state, instance, approval).await? else {
            return Ok(protocol_error(
                "access_denied",
                "the approving identity or session is no longer active",
            ));
        };
        record.status = GrantStatus::Consumed;
        if !state
            .store
            .compare_and_put(COLLECTION, &record.id, &record, &version)
            .await
            .map_err(|e| storage_error(&e))?
        {
            continue;
        }
        // The consume must succeed before issuing credentials. Ambiguous writes
        // never produce tokens, and a retry can never mint a second token set.
        return super::issue_user_tokens(
            state,
            instance,
            client,
            &user,
            Some(&session),
            &record.scopes,
            None,
        )
        .await;
    }
    Err(AppError::unavailable())
}

async fn approved_identity(
    state: &AppState,
    instance: &Instance,
    approval: &Approval,
) -> AppResult<Option<(User, Session)>> {
    let Some(user) = state
        .store
        .get_user(&approval.user_id)
        .await
        .map_err(|e| storage_error(&e))?
        .filter(|u| {
            u.instance_id == instance.id
                && !u.deleted
                && !u.banned
                && u.auth_epoch == approval.auth_epoch
        })
    else {
        return Ok(None);
    };
    if !state
        .store
        .scim_auth_eligible(&user)
        .await
        .map_err(|e| storage_error(&e))?
    {
        return Ok(None);
    }
    let session = state
        .store
        .get_session(&approval.session_id)
        .await
        .map_err(|e| storage_error(&e))?
        .filter(|s| {
            s.instance_id == instance.id
                && s.user_id == user.id
                && s.status == "active"
                && s.expires_at > now()
                && s.auth_epoch == user.auth_epoch
        });
    Ok(session.map(|s| (user, s)))
}
