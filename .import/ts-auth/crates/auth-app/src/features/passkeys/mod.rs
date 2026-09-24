//! Browser passkey registration, discoverable sign-in, and credential management.

use std::time::{SystemTime, UNIX_EPOCH};

use auth_contracts::{
    AppError, AppResult,
    models::{Instance, Passkey, User, VerificationToken},
};
use auth_security::{
    encoding, tokens,
    webauthn::{self, CoseKey, WebAuthnError},
};
use axum::{
    Json, Router,
    body::to_bytes,
    extract::{Path, Query, Request, State},
    http::{HeaderMap, StatusCode, header},
    response::{IntoResponse, Redirect, Response},
    routing::{get, post},
};
use base64::{Engine, engine::general_purpose::STANDARD};
use serde::Deserialize;
use serde_json::json;
use utoipa::openapi::{
    Info, OpenApi as OpenApiDoc,
    path::{HttpMethod, Operation, Paths},
};

use crate::{
    AppState,
    features::{management, ui, users},
};

const CHALLENGE_LIFETIME_SECONDS: i64 = 10 * 60;
const CEREMONY_TIMEOUT_MILLIS: u32 = 300_000;
const BODY_LIMIT: usize = 1024 * 1024;

pub fn router() -> Router<AppState> {
    Router::new()
        .route("/ui/{slug}/passkeys/assert", get(assertion_page))
        .route("/ui/{slug}/passkeys/manage", get(manage_page))
        .route(
            "/ui/{slug}/passkeys/manage/{id}/delete",
            post(delete_hosted_passkey),
        )
        .route(
            "/ui/{slug}/passkeys/register/options",
            post(registration_options),
        )
        .route("/ui/{slug}/passkeys/register", post(register_passkey))
        .route(
            "/ui/{slug}/passkeys/assert/options",
            post(assertion_options),
        )
        .route("/ui/{slug}/passkeys/assert/verify", post(verify_assertion))
        .route("/v1/{slug}/users/{user_id}/passkeys", get(list_passkeys))
        .route(
            "/v1/{slug}/users/{user_id}/passkeys/{passkey_id}",
            axum::routing::delete(delete_passkey),
        )
}

pub fn openapi() -> OpenApiDoc {
    let mut paths = Paths::new();
    for (path, method, summary) in [
        (
            "/ui/{slug}/passkeys/assert",
            HttpMethod::Get,
            "Render passkey sign-in",
        ),
        (
            "/ui/{slug}/passkeys/manage",
            HttpMethod::Get,
            "Render passkey management",
        ),
        (
            "/ui/{slug}/passkeys/manage/{id}/delete",
            HttpMethod::Post,
            "Remove a hosted passkey",
        ),
        (
            "/ui/{slug}/passkeys/register/options",
            HttpMethod::Post,
            "Start passkey registration",
        ),
        (
            "/ui/{slug}/passkeys/register",
            HttpMethod::Post,
            "Register a passkey",
        ),
        (
            "/ui/{slug}/passkeys/assert/options",
            HttpMethod::Post,
            "Start passkey sign-in",
        ),
        (
            "/ui/{slug}/passkeys/assert/verify",
            HttpMethod::Post,
            "Verify a passkey assertion",
        ),
        (
            "/v1/{slug}/users/{user_id}/passkeys",
            HttpMethod::Get,
            "List user passkeys",
        ),
        (
            "/v1/{slug}/users/{user_id}/passkeys/{passkey_id}",
            HttpMethod::Delete,
            "Delete a user passkey",
        ),
    ] {
        paths.add_path_operation(
            path,
            vec![method],
            Operation::builder()
                .tag("Hosted identity")
                .summary(Some(summary))
                .build(),
        );
    }
    OpenApiDoc::new(Info::new("TerraScale Auth Passkeys API", "1.0.0"), paths)
}

fn now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |duration| duration.as_secs() as i64)
}

fn store_error(error: impl std::fmt::Debug) -> AppError {
    tracing::error!(?error, "passkey store operation failed");
    AppError::unavailable()
}

fn error(status: StatusCode, code: &'static str, message: impl Into<String>) -> Response {
    (
        status,
        axum::Json(json!({"error":{"code":code,"message":message.into()}})),
    )
        .into_response()
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
async fn page_instance(state: &AppState, slug: &str) -> Result<Instance, Response> {
    match state.store.find_instance_by_slug(slug).await {
        Ok(Some(instance)) => Ok(instance),
        Ok(None) => Err(ui::page(
            "Not found",
            "<h1>Not found</h1><p class=\"muted\">No ts-auth instance lives at this address.</p>",
            None,
            StatusCode::NOT_FOUND,
            None,
            None,
        )),
        Err(error) => Err(store_error(error).into_response()),
    }
}

fn cookie(headers: &HeaderMap) -> Option<&str> {
    let value = headers.get(header::COOKIE)?.to_str().ok()?;
    value.split(';').find_map(|pair| {
        let (name, value) = pair.trim().split_once('=')?;
        (name == ui::SESSION_COOKIE_NAME).then_some(value)
    })
}

async fn session_user(
    state: &AppState,
    instance_id: &str,
    headers: &HeaderMap,
) -> AppResult<Option<(auth_contracts::models::Session, User)>> {
    let Some(token) = cookie(headers) else {
        return Ok(None);
    };
    users::resolve_session(state, instance_id, token).await
}

fn json_error(status: StatusCode, code: &'static str, message: impl Into<String>) -> Response {
    error(status, code, message)
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
async fn read_json<T: for<'de> Deserialize<'de>>(request: Request) -> Result<T, Response> {
    let bytes = to_bytes(request.into_body(), BODY_LIMIT)
        .await
        .map_err(|_| {
            json_error(
                StatusCode::BAD_REQUEST,
                "invalid_request",
                "Request body is missing or too large.",
            )
        })?;
    serde_json::from_slice(&bytes).map_err(|_| {
        json_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "Request body must be valid JSON.",
        )
    })
}

fn js_string(value: &str) -> String {
    serde_json::to_string(value)
        .unwrap_or_else(|_| "\"\"".to_owned())
        .replace("</", "<\\/")
        .replace('\u{2028}', "\\u2028")
        .replace('\u{2029}', "\\u2029")
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
fn base64url_decode(field: &str) -> Result<Vec<u8>, Response> {
    encoding::base64url_decode(field).map_err(|_| {
        json_error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "Malformed base64url WebAuthn field.",
        )
    })
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
fn origin_and_rp_id(state: &AppState) -> Result<(String, String), Response> {
    let url = reqwest::Url::parse(&state.options.public_origin)
        .map_err(|_| AppError::internal().into_response())?;
    let host = url
        .host_str()
        .ok_or_else(|| AppError::internal().into_response())?;
    Ok((url.origin().ascii_serialization(), host.to_owned()))
}

/// Issue a challenge for one instance-bound WebAuthn ceremony.
pub async fn issue_challenge(
    state: &AppState,
    instance_id: &str,
    purpose: &str,
    user_id: Option<&str>,
    authentication_next: Option<&str>,
) -> AppResult<String> {
    let challenge = encoding::base64url_encode(&tokens::random_bytes::<32>());
    let created_at = now();
    let record = VerificationToken {
        id: format!("vrf_{}", tokens::random_token(20)),
        instance_id: instance_id.to_owned(),
        user_id: user_id.map(str::to_owned),
        purpose: purpose.to_owned(),
        authentication_method: None,
        authentication_next: authentication_next.map(str::to_owned),
        user_auth_epoch: None,
        token_key: tokens::sha256_hex(&challenge),
        email_snapshot: None,
        expires_at: created_at + CHALLENGE_LIFETIME_SECONDS,
        consumed: false,
        failed_attempts: 0,
        created_at,
    };
    state
        .store
        .create_verification_token(&record)
        .await
        .map_err(store_error)?;
    Ok(challenge)
}

/// Consume a challenge only for the matching ceremony and optional bound user.
pub async fn consume_challenge(
    state: &AppState,
    instance_id: &str,
    purpose: &str,
    challenge: &str,
    user_id: Option<&str>,
) -> AppResult<Option<VerificationToken>> {
    let record = state
        .store
        .consume_verification_token(&tokens::sha256_hex(challenge), |record| {
            record.instance_id == instance_id
                && record.purpose == purpose
                && !record.consumed
                && record.expires_at > now()
                && (user_id.is_none() || record.user_id.as_deref() == user_id)
        })
        .await
        .map_err(store_error)?;
    Ok(record)
}

#[derive(Deserialize, Default)]
struct AssertionQuery {
    next: Option<String>,
}

async fn assertion_page(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    Query(query): Query<AssertionQuery>,
) -> Response {
    let brand = match page_instance(&state, &slug).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let base = js_string(&format!("/ui/{slug}"));
    let next = users::validated_authentication_next(
        &state.options.public_origin,
        &slug,
        query.next.as_deref(),
    )
    .unwrap_or_else(|| format!("/ui/{slug}/account"));
    let next = js_string(&next);
    let script = format!(
        r#"
const base={base};
const next={next};
const button=document.getElementById('passkey-button');
const error=document.getElementById('passkey-error');
function b64uDecode(value){{let text=value.replace(/-/g,'+').replace(/_/g,'/');while(text.length%4)text+='=';const raw=atob(text);return Uint8Array.from(raw,c=>c.charCodeAt(0));}}
function b64uEncode(bytes){{let raw='';for(const byte of new Uint8Array(bytes))raw+=String.fromCharCode(byte);return btoa(raw).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');}}
button.addEventListener('click',async()=>{{error.hidden=true;try{{
 const optionsResponse=await fetch(base+'/passkeys/assert/options?next='+encodeURIComponent(next),{{method:'POST',credentials:'same-origin'}});
 const optionsBody=await optionsResponse.json();if(!optionsResponse.ok)throw new Error(optionsBody.error?.message||'Could not start passkey sign-in.');
 const pk=optionsBody.publicKey;const credential=await navigator.credentials.get({{publicKey:{{challenge:b64uDecode(pk.challenge),rpId:pk.rpId,allowCredentials:[],userVerification:pk.userVerification,timeout:pk.timeout}}}});
 const assertion=credential.response;const verifyResponse=await fetch(base+'/passkeys/assert/verify',{{method:'POST',credentials:'same-origin',headers:{{'content-type':'application/json'}},body:JSON.stringify({{id:credential.id,response:{{clientDataJSON:b64uEncode(assertion.clientDataJSON),authenticatorData:b64uEncode(assertion.authenticatorData),signature:b64uEncode(assertion.signature),userHandle:assertion.userHandle?b64uEncode(assertion.userHandle):null}}}})}});
 const body=await verifyResponse.json();if(!verifyResponse.ok)throw new Error(body.error?.message||'Passkey sign-in failed.');window.location.href=body.redirect;
}}catch(e){{error.hidden=false;error.textContent=e?.message||'Passkey sign-in failed.';}}}});
"#
    );
    let body = format!(
        "<h1>Use a passkey</h1><p class=\"muted\">Sign in with a fingerprint, face, screen lock, or security key.</p><div id=\"passkey-error\" class=\"error\" hidden></div><button id=\"passkey-button\" type=\"button\">Continue</button><p class=\"hint\"><a href=\"/ui/{slug}/sign-in\">Back to sign in</a></p><script>{script}</script>"
    );
    ui::page(
        "Sign in with a passkey",
        &body,
        None,
        StatusCode::OK,
        brand.theme_color.as_deref(),
        brand.logo_url.as_deref(),
    )
}

async fn manage_page(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
) -> Response {
    let brand = match page_instance(&state, &slug).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let (_session, user) = match session_user(&state, &brand.id, &headers).await {
        Ok(Some(value)) => value,
        Ok(None) => return Redirect::to(&format!("/ui/{slug}/sign-in")).into_response(),
        Err(error) => return error.into_response(),
    };
    let passkeys = match state.store.list_passkeys(&user.id).await {
        Ok(passkeys) => passkeys
            .into_iter()
            .filter(|passkey| passkey.instance_id == brand.id)
            .collect::<Vec<_>>(),
        Err(error) => return store_error(error).into_response(),
    };
    let rows = if passkeys.is_empty() {
        "<p class=\"muted\">No passkeys yet.</p>".to_owned()
    } else {
        passkeys.iter().map(|passkey| format!("<form class=\"inline\" method=\"post\" action=\"/ui/{slug}/passkeys/manage/{}/delete\"><div class=\"row\"><span>{} <span class=\"muted\">{} · added {}</span></span><button class=\"secondary\" type=\"submit\">Remove</button></div></form>", ui::escape_html(&passkey.id), ui::escape_html(&passkey.nickname), ui::escape_html(&passkey.algorithm), passkey.created_at)).collect::<String>()
    };
    let base = js_string(&format!("/ui/{slug}"));
    let script = format!(
        r#"
const base={base};const button=document.getElementById('register-passkey');const error=document.getElementById('passkey-error');
function b64uDecode(value){{let text=value.replace(/-/g,'+').replace(/_/g,'/');while(text.length%4)text+='=';const raw=atob(text);return Uint8Array.from(raw,c=>c.charCodeAt(0));}}
function b64uEncode(bytes){{let raw='';for(const byte of new Uint8Array(bytes))raw+=String.fromCharCode(byte);return btoa(raw).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');}}
button.addEventListener('click',async()=>{{error.hidden=true;try{{
 const optionsResponse=await fetch(base+'/passkeys/register/options',{{method:'POST',credentials:'same-origin'}});const optionsBody=await optionsResponse.json();if(!optionsResponse.ok)throw new Error(optionsBody.error?.message||'Could not start registration.');
 const pk=optionsBody.publicKey;const credential=await navigator.credentials.create({{publicKey:{{challenge:b64uDecode(pk.challenge),rp:pk.rp,user:{{id:b64uDecode(pk.user.id),name:pk.user.name,displayName:pk.user.displayName}},pubKeyCredParams:pk.pubKeyCredParams,excludeCredentials:(pk.excludeCredentials||[]).map(c=>({{type:c.type,id:b64uDecode(c.id)}})),authenticatorSelection:pk.authenticatorSelection,timeout:pk.timeout,attestation:'none'}}}});
 const response=credential.response;const result=await fetch(base+'/passkeys/register',{{method:'POST',credentials:'same-origin',headers:{{'content-type':'application/json'}},body:JSON.stringify({{id:credential.id,nickname:credential.authenticatorAttachment==='platform'?'This device':'Security key',response:{{clientDataJSON:b64uEncode(response.clientDataJSON),attestationObject:b64uEncode(response.attestationObject),transports:[]}}}})}});const body=await result.json();if(!result.ok)throw new Error(body.error?.message||'Registration failed.');window.location.reload();
}}catch(e){{error.hidden=false;error.textContent=e?.message||'Registration failed.';}}}});
"#
    );
    let identifier = user
        .name
        .as_deref()
        .or(user.email.as_deref())
        .unwrap_or(&user.id);
    let body = format!(
        "<h1>Passkeys</h1><p class=\"muted\">Passkeys for {}.</p><div id=\"passkey-error\" class=\"error\" hidden></div><div class=\"list\">{rows}</div><button id=\"register-passkey\" type=\"button\">Register a new passkey</button><script>{script}</script>",
        ui::escape_html(identifier)
    );
    ui::page(
        "Passkeys",
        &body,
        None,
        StatusCode::OK,
        brand.theme_color.as_deref(),
        brand.logo_url.as_deref(),
    )
}

async fn delete_hosted_passkey(
    State(state): State<AppState>,
    Path((slug, id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    if !users::require_same_origin(&headers, &state.options.public_origin) {
        return error(
            StatusCode::FORBIDDEN,
            "origin_rejected",
            "Cross-origin request rejected.",
        );
    }
    let brand = match page_instance(&state, &slug).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let (_session, user) = match session_user(&state, &brand.id, &headers).await {
        Ok(Some(value)) => value,
        Ok(None) => return Redirect::to(&format!("/ui/{slug}/sign-in")).into_response(),
        Err(error) => return error.into_response(),
    };
    let passkey = match state.store.get_passkey(&id).await {
        Ok(Some(passkey)) if passkey.instance_id == brand.id && passkey.user_id == user.id => {
            passkey
        }
        Ok(_) => {
            return error(
                StatusCode::NOT_FOUND,
                "passkey_not_found",
                "Unknown passkey.",
            );
        }
        Err(error) => return store_error(error).into_response(),
    };
    if let Err(error) = state.store.delete_passkey(&passkey.id).await {
        return store_error(error).into_response();
    }
    if let Err(error) = users::audit(
        &state,
        &brand.id,
        Some(&user.id),
        "passkey.deleted",
        json!({"passkeyId":passkey.id,"userId":user.id}),
    )
    .await
    {
        return error.into_response();
    }
    Redirect::to(&format!("/ui/{slug}/passkeys/manage")).into_response()
}

async fn registration_options(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
) -> Response {
    if !users::require_same_origin(&headers, &state.options.public_origin) {
        return error(
            StatusCode::FORBIDDEN,
            "origin_rejected",
            "Cross-origin request rejected.",
        );
    }
    let brand = match page_instance(&state, &slug).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let (_session, user) = match session_user(&state, &brand.id, &headers).await {
        Ok(Some(value)) => value,
        Ok(None) => {
            return error(
                StatusCode::UNAUTHORIZED,
                "session_required",
                "A valid session is required.",
            );
        }
        Err(error) => return error.into_response(),
    };
    let challenge =
        match issue_challenge(&state, &brand.id, "webauthn.register", Some(&user.id), None).await {
            Ok(value) => value,
            Err(error) => return error.into_response(),
        };
    let existing = match state.store.list_passkeys(&user.id).await {
        Ok(values) => values,
        Err(error) => return store_error(error).into_response(),
    };
    let user_bytes = encoding::base64url_encode(user.id.as_bytes());
    let email = user.email.as_deref().unwrap_or(&user.id);
    Json(json!({"publicKey":{
        "challenge":challenge,
        "rp":{"id":reqwest::Url::parse(&state.options.public_origin).ok().and_then(|url| url.host_str().map(ToOwned::to_owned)).unwrap_or_default(),"name":brand.name},
        "user":{"id":user_bytes,"name":email,"displayName":user.name.as_deref().unwrap_or(email)},
        "pubKeyCredParams":[{"type":"public-key","alg":-7},{"type":"public-key","alg":-257}],
        "timeout":CEREMONY_TIMEOUT_MILLIS,
        "excludeCredentials":existing.into_iter().filter(|passkey| passkey.instance_id == brand.id).map(|passkey| json!({"type":"public-key","id":passkey.credential_key})).collect::<Vec<_>>(),
        "authenticatorSelection":{"residentKey":"required","userVerification":"preferred"},"attestation":"none"
    }})).into_response()
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RegistrationBody {
    id: String,
    response: RegistrationResponse,
    nickname: Option<String>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RegistrationResponse {
    client_data_json: String,
    attestation_object: String,
    transports: Option<Vec<String>>,
}

async fn register_passkey(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    request: Request,
) -> Response {
    if !users::require_same_origin(&headers, &state.options.public_origin) {
        return error(
            StatusCode::FORBIDDEN,
            "origin_rejected",
            "Cross-origin request rejected.",
        );
    }
    let brand = match page_instance(&state, &slug).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let (_session, user) = match session_user(&state, &brand.id, &headers).await {
        Ok(Some(value)) => value,
        Ok(None) => {
            return error(
                StatusCode::UNAUTHORIZED,
                "session_required",
                "A valid session is required.",
            );
        }
        Err(error) => return error.into_response(),
    };
    let body: RegistrationBody = match read_json(request).await {
        Ok(body) => body,
        Err(response) => return response,
    };
    if body.id.is_empty()
        || body.response.client_data_json.is_empty()
        || body.response.attestation_object.is_empty()
    {
        return error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "id, clientDataJSON, and attestationObject are required.",
        );
    }
    let credential_id = match base64url_decode(&body.id) {
        Ok(value) => value,
        Err(response) => return response,
    };
    let client_bytes = match base64url_decode(&body.response.client_data_json) {
        Ok(value) => value,
        Err(response) => return response,
    };
    let client_data_json = match String::from_utf8(client_bytes) {
        Ok(value) => value,
        Err(_) => {
            return error(
                StatusCode::BAD_REQUEST,
                "invalid_request",
                "clientDataJSON must be UTF-8 JSON.",
            );
        }
    };
    let attestation = match base64url_decode(&body.response.attestation_object) {
        Ok(value) => value,
        Err(response) => return response,
    };
    let Some(challenge) = webauthn::client_data_challenge(&client_data_json) else {
        return error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "clientDataJSON has no WebAuthn challenge.",
        );
    };
    let (origin, rp_id) = match origin_and_rp_id(&state) {
        Ok(value) => value,
        Err(response) => return response,
    };
    let registration = match webauthn::verify_registration(
        &client_data_json,
        &attestation,
        &challenge,
        &rp_id,
        &origin,
        false,
    ) {
        Ok(value) => value,
        Err(error) => return webauthn_error(error),
    };
    if registration.credential_id != credential_id {
        return error(
            StatusCode::BAD_REQUEST,
            "registration_failed",
            "Credential ID does not match attested credential data.",
        );
    }
    if !match consume_challenge(
        &state,
        &brand.id,
        "webauthn.register",
        &challenge,
        Some(&user.id),
    )
    .await
    {
        Ok(Some(_)) => true,
        Ok(None) => false,
        Err(error) => return error.into_response(),
    } {
        return error(
            StatusCode::BAD_REQUEST,
            "invalid_challenge",
            "Challenge is unknown, expired, or already used.",
        );
    }
    let credential_key = encoding::base64url_encode(&credential_id);
    if match state
        .store
        .find_passkey_by_credential_id(&credential_key)
        .await
    {
        Ok(Some(_)) => true,
        Ok(None) => false,
        Err(error) => return store_error(error).into_response(),
    } {
        return error(
            StatusCode::CONFLICT,
            "credential_exists",
            "This credential is already registered.",
        );
    }
    let nickname = body
        .nickname
        .filter(|value| !value.trim().is_empty())
        .map(|value| value.trim().chars().take(80).collect::<String>())
        .unwrap_or_else(|| "Passkey".into());
    let passkey = Passkey {
        id: format!("pky_{}", tokens::random_token(20)),
        instance_id: brand.id.clone(),
        user_id: user.id.clone(),
        credential_key,
        public_key_cbor_base64: STANDARD.encode(registration.public_key.raw_cbor),
        algorithm: registration.public_key.algorithm.to_owned(),
        sign_count: registration.sign_count,
        transports: body
            .response
            .transports
            .filter(|values| !values.is_empty())
            .map(|values| values.join(",")),
        nickname,
        created_at: now(),
        last_used_at: None,
    };
    if let Err(store_failure) = state.store.create_passkey(&passkey).await {
        return if store_failure.is_conflict() {
            error(
                StatusCode::CONFLICT,
                "credential_exists",
                "This credential is already registered.",
            )
        } else {
            store_error(store_failure).into_response()
        };
    }
    if let Err(error) = users::audit(
        &state,
        &brand.id,
        Some(&user.id),
        "passkey.created",
        json!({"passkeyId":passkey.id,"nickname":passkey.nickname}),
    )
    .await
    {
        return error.into_response();
    }
    Json(json!({"ok":true})).into_response()
}

async fn assertion_options(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Query(query): Query<AssertionQuery>,
) -> Response {
    if !users::require_same_origin(&headers, &state.options.public_origin) {
        return error(
            StatusCode::FORBIDDEN,
            "origin_rejected",
            "Cross-origin request rejected.",
        );
    }
    let brand = match page_instance(&state, &slug).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let next = users::validated_authentication_next(
        &state.options.public_origin,
        &slug,
        query.next.as_deref(),
    )
    .unwrap_or_else(|| format!("/ui/{slug}/account"));
    let challenge =
        match issue_challenge(&state, &brand.id, "webauthn.assert", None, Some(&next)).await {
            Ok(value) => value,
            Err(error) => return error.into_response(),
        };
    let rp_id = reqwest::Url::parse(&state.options.public_origin)
        .ok()
        .and_then(|url| url.host_str().map(ToOwned::to_owned))
        .unwrap_or_default();
    Json(json!({"publicKey":{"challenge":challenge,"rpId":rp_id,"timeout":CEREMONY_TIMEOUT_MILLIS,"userVerification":"preferred"}})).into_response()
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct AssertionBody {
    id: String,
    response: AssertionResponse,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct AssertionResponse {
    client_data_json: String,
    authenticator_data: String,
    signature: String,
    user_handle: Option<String>,
}

async fn verify_assertion(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    request: Request,
) -> Response {
    if !users::require_same_origin(&headers, &state.options.public_origin) {
        return error(
            StatusCode::FORBIDDEN,
            "origin_rejected",
            "Cross-origin request rejected.",
        );
    }
    let user_agent = headers
        .get(header::USER_AGENT)
        .and_then(|value| value.to_str().ok())
        .map(str::to_owned);
    let brand = match page_instance(&state, &slug).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let body: AssertionBody = match read_json(request).await {
        Ok(body) => body,
        Err(response) => return response,
    };
    if body.id.is_empty()
        || body.response.client_data_json.is_empty()
        || body.response.authenticator_data.is_empty()
        || body.response.signature.is_empty()
    {
        return error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "id, clientDataJSON, authenticatorData, and signature are required.",
        );
    }
    let credential_id = match base64url_decode(&body.id) {
        Ok(value) => value,
        Err(response) => return response,
    };
    let client_bytes = match base64url_decode(&body.response.client_data_json) {
        Ok(value) => value,
        Err(response) => return response,
    };
    let client_data_json = match String::from_utf8(client_bytes) {
        Ok(value) => value,
        Err(_) => {
            return error(
                StatusCode::BAD_REQUEST,
                "invalid_request",
                "clientDataJSON must be UTF-8 JSON.",
            );
        }
    };
    let authenticator_data = match base64url_decode(&body.response.authenticator_data) {
        Ok(value) => value,
        Err(response) => return response,
    };
    let signature = match base64url_decode(&body.response.signature) {
        Ok(value) => value,
        Err(response) => return response,
    };
    let Some(challenge) = webauthn::client_data_challenge(&client_data_json) else {
        return error(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "clientDataJSON has no WebAuthn challenge.",
        );
    };
    let credential_key = encoding::base64url_encode(&credential_id);
    let passkey = match state
        .store
        .find_passkey_by_credential_id(&credential_key)
        .await
    {
        Ok(Some(passkey)) if passkey.instance_id == brand.id => passkey,
        Ok(_) => {
            return error(
                StatusCode::UNAUTHORIZED,
                "unknown_credential",
                "Unknown credential.",
            );
        }
        Err(error) => return store_error(error).into_response(),
    };
    let user = match state.store.get_user(&passkey.user_id).await {
        Ok(Some(user)) if user.instance_id == brand.id && !user.deleted && !user.banned => user,
        Ok(_) => {
            return error(
                StatusCode::UNAUTHORIZED,
                "unknown_credential",
                "Unknown credential.",
            );
        }
        Err(error) => return store_error(error).into_response(),
    };
    match users::authentication_allowed(&state, &brand.id, &user).await {
        Ok(true) => {}
        Ok(false) => {
            return error(
                StatusCode::UNAUTHORIZED,
                "unknown_credential",
                "Unknown credential.",
            );
        }
        Err(error) => return error.into_response(),
    }
    if let Some(user_handle) = body.response.user_handle.as_deref() {
        let user_handle = match base64url_decode(user_handle) {
            Ok(value) => value,
            Err(response) => return response,
        };
        if user_handle != user.id.as_bytes() {
            return error(
                StatusCode::UNAUTHORIZED,
                "unknown_credential",
                "Unknown credential.",
            );
        }
    }
    let raw_key = match STANDARD.decode(&passkey.public_key_cbor_base64) {
        Ok(value) => value,
        Err(_) => return AppError::internal().into_response(),
    };
    let public_key = match CoseKey::from_stored(&raw_key) {
        Ok(value) => value,
        Err(error) => return webauthn_error(error),
    };
    let (origin, rp_id) = match origin_and_rp_id(&state) {
        Ok(value) => value,
        Err(response) => return response,
    };
    let assertion = match webauthn::verify_assertion(
        &client_data_json,
        &authenticator_data,
        &signature,
        &challenge,
        &rp_id,
        &origin,
        &credential_id,
        &public_key,
        passkey.sign_count,
        false,
    ) {
        Ok(value) => value,
        Err(error) => return webauthn_error(error),
    };
    let challenge =
        match consume_challenge(&state, &brand.id, "webauthn.assert", &challenge, None).await {
            Ok(Some(value)) => value,
            Ok(None) => {
                return error(
                    StatusCode::UNAUTHORIZED,
                    "invalid_challenge",
                    "Challenge is unknown, expired, or already used.",
                );
            }
            Err(error) => return error.into_response(),
        };
    match users::authentication_allowed(&state, &brand.id, &user).await {
        Ok(true) => {}
        Ok(false) => {
            return error(
                StatusCode::UNAUTHORIZED,
                "unknown_credential",
                "Unknown credential.",
            );
        }
        Err(error) => return error.into_response(),
    }
    match state
        .store
        .advance_passkey_sign_count(&passkey.id, passkey.sign_count, assertion.sign_count, now())
        .await
    {
        Ok(true) => {}
        Ok(false) => {
            return error(
                StatusCode::UNAUTHORIZED,
                "assertion_replayed",
                "This assertion has already been used.",
            );
        }
        Err(error) => return store_error(error).into_response(),
    }
    let next = challenge
        .authentication_next
        .as_deref()
        .and_then(|next| {
            users::validated_authentication_next(&state.options.public_origin, &slug, Some(next))
        })
        .unwrap_or_else(|| format!("/ui/{slug}/account"));
    match users::complete_first_factor(
        &state,
        &brand,
        &user,
        "passkey",
        user_agent.as_deref(),
        None,
        Some(&next),
    )
    .await
    {
        Ok(users::FirstFactorOutcome::MfaPending { token }) => Json(json!({
            "mfa_required": true,
            "mfa_token": token,
            "redirect": format!("/ui/{slug}/mfa/verify?token={token}"),
        }))
        .into_response(),
        Ok(users::FirstFactorOutcome::Session { token }) => {
            let mut response = Json(json!({"redirect":next})).into_response();
            ui::set_session_cookie(&mut response, &token);
            response
        }
        Err(error) => error.into_response(),
    }
}

fn webauthn_error(error: WebAuthnError) -> Response {
    let code = match error {
        WebAuthnError::Format => "malformed_webauthn_data",
        WebAuthnError::Unsupported => "unsupported_credential",
        WebAuthnError::Validation => "webauthn_validation_failed",
        WebAuthnError::Signature => "invalid_signature",
        WebAuthnError::SignCount => "assertion_replayed",
    };
    error_response(
        StatusCode::UNAUTHORIZED,
        code,
        "WebAuthn verification failed.",
    )
}

fn error_response(status: StatusCode, code: &'static str, message: &'static str) -> Response {
    (
        status,
        axum::Json(json!({"error":{"code":code,"message":message}})),
    )
        .into_response()
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
async fn api_context(
    state: &AppState,
    headers: &HeaderMap,
    slug: &str,
    route_path: &str,
) -> Result<management::InstanceContext, Response> {
    let context = management::resolve_instance(state, headers, slug)
        .await
        .map_err(IntoResponse::into_response)?;
    if !management::scope_allows(&context.scopes, route_path) {
        return Err(error(
            StatusCode::FORBIDDEN,
            "insufficient_scope",
            "The API key cannot access passkeys.",
        ));
    }
    Ok(context)
}

async fn list_passkeys(
    State(state): State<AppState>,
    Path((slug, user_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    let route = format!("/v1/{slug}/users/{user_id}/passkeys");
    let context = match api_context(&state, &headers, &slug, &route).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let user = match state.store.get_user(&user_id).await {
        Ok(Some(user)) if user.instance_id == context.instance.id => user,
        Ok(_) => return error(StatusCode::NOT_FOUND, "user_not_found", "Unknown user."),
        Err(error) => return store_error(error).into_response(),
    };
    let passkeys = match state.store.list_passkeys(&user.id).await {
        Ok(value) => value,
        Err(error) => return store_error(error).into_response(),
    };
    let values: Vec<_> = passkeys.into_iter().filter(|passkey| passkey.instance_id == context.instance.id).map(|passkey| json!({"id":passkey.id,"nickname":passkey.nickname,"algorithm":passkey.algorithm,"created_at":passkey.created_at,"last_used_at":passkey.last_used_at})).collect();
    Json(json!({"passkeys":values})).into_response()
}

async fn delete_passkey(
    State(state): State<AppState>,
    Path((slug, user_id, passkey_id)): Path<(String, String, String)>,
    headers: HeaderMap,
) -> Response {
    let route = format!("/v1/{slug}/users/{user_id}/passkeys/{passkey_id}");
    let context = match api_context(&state, &headers, &slug, &route).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let passkey = match state.store.get_passkey(&passkey_id).await {
        Ok(Some(value)) if value.instance_id == context.instance.id && value.user_id == user_id => {
            value
        }
        Ok(_) => {
            return error(
                StatusCode::NOT_FOUND,
                "passkey_not_found",
                "Unknown passkey.",
            );
        }
        Err(error) => return store_error(error).into_response(),
    };
    if let Err(error) = state.store.delete_passkey(&passkey.id).await {
        return store_error(error).into_response();
    }
    if let Err(error) = users::audit(
        &state,
        &context.instance.id,
        Some(&context.api_key_id),
        "passkey.deleted",
        json!({"passkeyId":passkey.id,"userId":user_id}),
    )
    .await
    {
        return error.into_response();
    }
    StatusCode::NO_CONTENT.into_response()
}
