//! TOTP enrollment, second-factor challenges, recovery codes, and factor management.

use std::time::{SystemTime, UNIX_EPOCH};

use auth_contracts::{
    AppError, AppResult,
    models::{Instance, MfaFactor, User, VerificationToken},
};
use auth_security::{
    secretbox, tokens,
    totp::{self, Algorithm},
};
use axum::{
    Form, Json, Router,
    extract::{Path, Query, State},
    http::{HeaderMap, StatusCode, header},
    response::{IntoResponse, Redirect, Response},
    routing::{get, post},
};
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

const PENDING_LIFETIME_SECONDS: i64 = 10 * 60;
const MAX_PENDING_ATTEMPTS: i32 = 10;
const INVALID_DELAY_MILLIS: u64 = 250;

pub fn router() -> Router<AppState> {
    Router::new()
        .route("/ui/{slug}/mfa/enroll", get(enrollment_page))
        .route("/ui/{slug}/mfa/enroll/start", post(start_enrollment))
        .route("/ui/{slug}/mfa/enroll/confirm", post(confirm_enrollment))
        .route(
            "/ui/{slug}/mfa/verify",
            get(verification_page).post(verify_mfa),
        )
        .route("/ui/{slug}/mfa/recovery", get(recovery_page))
        .route(
            "/ui/{slug}/mfa/recovery/consume",
            post(consume_recovery_code),
        )
        .route("/v1/{slug}/users/{user_id}/mfa-factors", get(list_factors))
        .route(
            "/v1/{slug}/users/{user_id}/mfa-factors/{id}",
            axum::routing::delete(delete_factor),
        )
        .route(
            "/v1/{slug}/users/{user_id}/mfa-factors/{id}/regenerate-recovery",
            post(regenerate_recovery_codes),
        )
}

pub fn openapi() -> OpenApiDoc {
    let mut paths = Paths::new();
    for (path, method, summary) in [
        (
            "/ui/{slug}/mfa/enroll",
            HttpMethod::Get,
            "Render TOTP enrollment",
        ),
        (
            "/ui/{slug}/mfa/enroll/start",
            HttpMethod::Post,
            "Start TOTP enrollment",
        ),
        (
            "/ui/{slug}/mfa/enroll/confirm",
            HttpMethod::Post,
            "Confirm an authenticator code",
        ),
        (
            "/ui/{slug}/mfa/verify",
            HttpMethod::Get,
            "Render sign-in MFA challenge",
        ),
        (
            "/ui/{slug}/mfa/verify",
            HttpMethod::Post,
            "Complete sign-in MFA challenge",
        ),
        (
            "/ui/{slug}/mfa/recovery",
            HttpMethod::Get,
            "Render recovery code form",
        ),
        (
            "/ui/{slug}/mfa/recovery/consume",
            HttpMethod::Post,
            "Consume a recovery code",
        ),
        (
            "/v1/{slug}/users/{user_id}/mfa-factors",
            HttpMethod::Get,
            "List MFA factors",
        ),
        (
            "/v1/{slug}/users/{user_id}/mfa-factors/{id}",
            HttpMethod::Delete,
            "Delete an MFA factor",
        ),
        (
            "/v1/{slug}/users/{user_id}/mfa-factors/{id}/regenerate-recovery",
            HttpMethod::Post,
            "Regenerate recovery codes",
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
    OpenApiDoc::new(Info::new("TerraScale Auth MFA API", "1.0.0"), paths)
}

fn now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |duration| duration.as_secs() as i64)
}

fn store_error(error: impl std::fmt::Debug) -> AppError {
    tracing::error!(?error, "MFA store operation failed");
    AppError::unavailable()
}

fn account_unavailable() -> AppError {
    AppError::new(
        StatusCode::UNAUTHORIZED,
        "account_unavailable",
        "This account cannot sign in.",
    )
}

fn error(status: StatusCode, code: &'static str, message: &'static str) -> Response {
    (
        status,
        Json(json!({"error":{"code":code,"message":message}})),
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
            "<h1>Not found</h1><p class=\"muted\">This page does not exist.</p>",
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

fn signin(slug: &str) -> Response {
    Redirect::to(&format!("/ui/{slug}/sign-in")).into_response()
}

#[derive(Debug, Clone)]
pub struct PendingAuthentication {
    pub user_id: String,
    pub user_auth_epoch: Option<u64>,
    pub authentication_method: Option<String>,
    pub email_snapshot: Option<String>,
    pub authentication_next: Option<String>,
}

fn pending_authentication(record: VerificationToken) -> Option<PendingAuthentication> {
    Some(PendingAuthentication {
        user_id: record.user_id?,
        user_auth_epoch: record.user_auth_epoch,
        authentication_method: record.authentication_method,
        email_snapshot: record.email_snapshot,
        authentication_next: record.authentication_next,
    })
}

/// True when the user has an active confirmed TOTP factor.
pub async fn required_for_user(state: &AppState, user_id: &str) -> AppResult<bool> {
    let factors = state
        .store
        .list_mfa_factors(user_id)
        .await
        .map_err(store_error)?;
    Ok(factors
        .iter()
        .any(|factor| factor.r#type == "totp" && factor.confirmed))
}

/// Issue a single-use pending-MFA token bound to an in-progress sign-in.
#[expect(
    clippy::too_many_arguments,
    reason = "This cross-feature entry point receives separately validated identity, continuation, and auth-epoch bindings."
)]
pub async fn begin_pending(
    state: &AppState,
    instance_id: &str,
    user_id: &str,
    instance_slug: &str,
    authentication_method: &str,
    email_snapshot: Option<&str>,
    authentication_next: &str,
    user_auth_epoch: u64,
) -> AppResult<String> {
    let user = state
        .store
        .get_user(user_id)
        .await
        .map_err(store_error)?
        .filter(|user| user.instance_id == instance_id && !user.deleted && !user.banned)
        .ok_or_else(account_unavailable)?;
    if !users::authentication_allowed(state, instance_id, &user).await? {
        return Err(account_unavailable());
    }
    if user.auth_epoch != user_auth_epoch {
        return Err(account_unavailable());
    }
    let email_snapshot = email_snapshot.map(users::normalize_email);
    if email_snapshot.as_deref().is_some_and(|snapshot| {
        user.email.as_deref().map(users::normalize_email).as_deref() != Some(snapshot)
    }) {
        return Err(account_unavailable());
    }
    let authentication_next = users::validated_authentication_next(
        &state.options.public_origin,
        instance_slug,
        Some(authentication_next),
    )
    .ok_or_else(account_unavailable)?;
    let token = tokens::random_token(40);
    let created_at = now();
    let record = VerificationToken {
        id: format!("vrf_{}", tokens::random_token(20)),
        instance_id: instance_id.to_owned(),
        user_id: Some(user_id.to_owned()),
        purpose: "mfa.pending".into(),
        authentication_method: Some(authentication_method.to_owned()),
        authentication_next: Some(authentication_next),
        user_auth_epoch: Some(user_auth_epoch),
        token_key: tokens::sha256_hex(&token),
        expires_at: created_at + PENDING_LIFETIME_SECONDS,
        consumed: false,
        failed_attempts: 0,
        created_at,
        email_snapshot,
    };
    state
        .store
        .create_verification_token(&record)
        .await
        .map_err(store_error)?;
    Ok(token)
}

async fn pending_record(
    state: &AppState,
    instance_id: &str,
    token: &str,
) -> AppResult<Option<VerificationToken>> {
    let record = state
        .store
        .find_verification_token_matching(&tokens::sha256_hex(token), |record| {
            record.instance_id == instance_id
                && record.purpose == "mfa.pending"
                && !record.consumed
                && record.expires_at > now()
                && record.failed_attempts < MAX_PENDING_ATTEMPTS
        })
        .await
        .map_err(store_error)?;
    Ok(record)
}

/// Validate a pending MFA token without consuming it; returns its bound user id.
pub async fn peek_pending(
    state: &AppState,
    instance_id: &str,
    token: &str,
) -> AppResult<Option<String>> {
    Ok(peek_pending_authentication(state, instance_id, token)
        .await?
        .map(|pending| pending.user_id))
}

/// Validate pending MFA state without consuming it.
pub async fn peek_pending_authentication(
    state: &AppState,
    instance_id: &str,
    token: &str,
) -> AppResult<Option<PendingAuthentication>> {
    let Some(pending) = pending_record(state, instance_id, token)
        .await?
        .and_then(pending_authentication)
    else {
        return Ok(None);
    };
    let Some(user) = state
        .store
        .get_user(&pending.user_id)
        .await
        .map_err(store_error)?
        .filter(|user| user.instance_id == instance_id && !user.deleted && !user.banned)
    else {
        return Ok(None);
    };
    if pending.user_auth_epoch == Some(user.auth_epoch)
        && users::authentication_allowed(state, instance_id, &user).await?
    {
        Ok(Some(pending))
    } else {
        Ok(None)
    }
}

/// Atomically consume a pending MFA token after a correct second factor.
pub async fn consume_pending(
    state: &AppState,
    instance_id: &str,
    token: &str,
) -> AppResult<Option<PendingAuthentication>> {
    let record = state
        .store
        .consume_verification_token(&tokens::sha256_hex(token), |record| {
            record.instance_id == instance_id
                && record.purpose == "mfa.pending"
                && !record.consumed
                && record.expires_at > now()
                && record.failed_attempts < MAX_PENDING_ATTEMPTS
        })
        .await
        .map_err(store_error)?;
    let Some(pending) = record.and_then(pending_authentication) else {
        return Ok(None);
    };
    let Some(user) = state
        .store
        .get_user(&pending.user_id)
        .await
        .map_err(store_error)?
        .filter(|user| user.instance_id == instance_id && !user.deleted && !user.banned)
    else {
        return Ok(None);
    };
    if pending.user_auth_epoch == Some(user.auth_epoch)
        && users::authentication_allowed(state, instance_id, &user).await?
    {
        Ok(Some(pending))
    } else {
        Ok(None)
    }
}

fn matching_totp_step(seed: &[u8], code: &str, timestamp: i64) -> Option<i64> {
    if code.len() != 6 || !code.as_bytes().iter().all(u8::is_ascii_digit) {
        return None;
    }
    let current = timestamp.div_euclid(30);
    let mut matched = None;
    for offset in -1..=1 {
        let Some(step) = current.checked_add(offset).filter(|value| *value >= 0) else {
            continue;
        };
        let Ok(counter) = u64::try_from(step) else {
            continue;
        };
        let Some(candidate) = totp::hotp(seed, counter, 6, Algorithm::Sha1) else {
            continue;
        };
        if tokens::fixed_time_equals(&candidate, code) {
            matched = Some(matched.map_or(step, |old: i64| old.max(step)));
        }
    }
    matched
}

/// Verify a TOTP with an atomic monotonic step check, or consume a one-use recovery code.
pub async fn verify_code(state: &AppState, user_id: &str, code: &str) -> AppResult<bool> {
    let factors = state
        .store
        .list_mfa_factors(user_id)
        .await
        .map_err(store_error)?;
    let candidate_hash = tokens::sha256_hex(code);
    for factor in factors {
        if factor.r#type == "totp"
            && factor.confirmed
            && let Some(boxed) = factor.secret_box.as_deref()
        {
            let seed = secretbox::decrypt(&state.options.master_key, boxed)
                .map_err(|_| AppError::internal())?;
            if let Some(step) = matching_totp_step(&seed, code, now()) {
                return state
                    .store
                    .try_accept_totp_step(&factor.id, &factor.instance_id, user_id, step)
                    .await
                    .map_err(store_error);
            }
        }
        if factor.r#type == "recovery"
            && factor.confirmed
            && factor.recovery_code_hashes.as_ref().is_some_and(|codes| {
                codes
                    .iter()
                    .any(|stored| tokens::fixed_time_equals(stored, &candidate_hash))
            })
        {
            return state
                .store
                .consume_recovery_code(&factor.id, &candidate_hash)
                .await
                .map_err(store_error);
        }
    }
    Ok(false)
}

pub fn new_recovery_codes() -> Vec<String> {
    (0..10).map(|_| tokens::random_token(10)).collect()
}

async fn enrollment_page(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
) -> Response {
    let brand = match page_instance(&state, &slug).await {
        Ok(instance) => instance,
        Err(response) => return response,
    };
    match session_user(&state, &brand.id, &headers).await {
        Ok(Some(_)) => {
            let body = format!(
                "<h1>Set up multi-factor authentication</h1><p class=\"muted\">Use an authenticator app to scan a setup secret and then confirm a code.</p><form method=\"post\" action=\"/ui/{slug}/mfa/enroll/start\"><button>Start enrollment</button></form><p class=\"hint\"><a href=\"/ui/{slug}/account\">Back to account</a></p>"
            );
            ui::page(
                "Set up MFA",
                &body,
                None,
                StatusCode::OK,
                brand.theme_color.as_deref(),
                brand.logo_url.as_deref(),
            )
        }
        Ok(None) => signin(&slug),
        Err(error) => error.into_response(),
    }
}

async fn start_enrollment(
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
        Ok(instance) => instance,
        Err(response) => return response,
    };
    let (session, user) = match session_user(&state, &brand.id, &headers).await {
        Ok(Some(value)) => value,
        Ok(None) => return signin(&slug),
        Err(error) => return error.into_response(),
    };
    let factors = match state.store.list_mfa_factors(&user.id).await {
        Ok(factors) => factors,
        Err(error) => return store_error(error).into_response(),
    };
    if factors
        .iter()
        .any(|factor| factor.r#type == "totp" && factor.confirmed)
    {
        return ui::page(
            "Already enrolled",
            &format!(
                "<h1>Already enrolled</h1><div class=\"error\">You already have a confirmed authenticator. Delete it before enrolling a new one.</div><a href=\"/ui/{slug}/account\"><button class=\"secondary\">Back to account</button></a>"
            ),
            None,
            StatusCode::CONFLICT,
            brand.theme_color.as_deref(),
            brand.logo_url.as_deref(),
        );
    }
    for pending in factors
        .iter()
        .filter(|factor| factor.r#type == "totp" && !factor.confirmed)
    {
        if let Err(error) = state
            .store
            .try_delete_mfa_factor(&pending.id, &brand.id, &user.id)
            .await
        {
            return store_error(error).into_response();
        }
    }
    let seed = tokens::random_bytes::<20>();
    let secret_box = match secretbox::encrypt(&state.options.master_key, &seed) {
        Ok(value) => value,
        Err(_) => return AppError::internal().into_response(),
    };
    let factor = MfaFactor {
        id: format!("mfa_{}", tokens::random_token(20)),
        instance_id: brand.id.clone(),
        user_id: user.id.clone(),
        r#type: "totp".into(),
        secret_box: Some(secret_box),
        recovery_code_hashes: None,
        confirmed: false,
        created_at: now(),
        last_used_totp_step: None,
    };
    if let Err(error) = state.store.create_mfa_factor(&factor).await {
        return store_error(error).into_response();
    }
    let secret = auth_security::encoding::base32_encode(&seed);
    let account = user.email.as_deref().unwrap_or(&user.id);
    let label: String = url::form_urlencoded::byte_serialize(account.as_bytes()).collect();
    let otpauth = format!(
        "otpauth://totp/ts-auth:{label}?secret={secret}&issuer=ts-auth&algorithm=SHA1&digits=6&period=30"
    );
    let body = format!(
        "<h1>Set up your authenticator</h1><p class=\"muted\">Add this entry to your authenticator app, then confirm the code it generates.</p><label>Secret (base32)</label><div class=\"row\"><code>{}</code></div><label>Manual entry URI</label><div class=\"row\"><code>{}</code></div><form method=\"post\" action=\"/ui/{slug}/mfa/enroll/confirm?factor={}\"><label>Code from your app</label><input name=\"code\" inputmode=\"numeric\" autocomplete=\"one-time-code\" required autofocus><button>Confirm</button></form>",
        ui::escape_html(&secret),
        ui::escape_html(&otpauth),
        ui::escape_html(&factor.id)
    );
    let _ = session;
    ui::page(
        "Set up your authenticator",
        &body,
        None,
        StatusCode::OK,
        brand.theme_color.as_deref(),
        brand.logo_url.as_deref(),
    )
}

#[derive(Deserialize, Default)]
struct FactorQuery {
    factor: Option<String>,
}

async fn confirm_enrollment(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    Query(query): Query<FactorQuery>,
    headers: HeaderMap,
    Form(form): Form<std::collections::HashMap<String, String>>,
) -> Response {
    if !users::require_same_origin(&headers, &state.options.public_origin) {
        return error(
            StatusCode::FORBIDDEN,
            "origin_rejected",
            "Cross-origin request rejected.",
        );
    }
    let brand = match page_instance(&state, &slug).await {
        Ok(instance) => instance,
        Err(response) => return response,
    };
    let (session, user) = match session_user(&state, &brand.id, &headers).await {
        Ok(Some(value)) => value,
        Ok(None) => return signin(&slug),
        Err(error) => return error.into_response(),
    };
    let Some(factor_id) = query.factor else {
        return error(
            StatusCode::NOT_FOUND,
            "factor_not_found",
            "Unknown MFA factor.",
        );
    };
    let factor = match state.store.get_mfa_factor(&factor_id).await {
        Ok(Some(factor))
            if factor.instance_id == brand.id
                && factor.user_id == user.id
                && factor.r#type == "totp"
                && !factor.confirmed =>
        {
            factor
        }
        Ok(_) => {
            return error(
                StatusCode::NOT_FOUND,
                "factor_not_found",
                "Unknown MFA factor.",
            );
        }
        Err(error) => return store_error(error).into_response(),
    };
    let code = form.get("code").map_or("", |value| value.trim());
    let seed = match factor
        .secret_box
        .as_deref()
        .and_then(|boxed| secretbox::decrypt(&state.options.master_key, boxed).ok())
    {
        Some(seed) => seed,
        None => return AppError::internal().into_response(),
    };
    let Some(step) = matching_totp_step(&seed, code, now()) else {
        let body = format!(
            "<h1>Set up your authenticator</h1><div class=\"error\">That code didn't match. Try again.</div><form method=\"post\" action=\"/ui/{slug}/mfa/enroll/confirm?factor={}\"><label>Code from your app</label><input name=\"code\" inputmode=\"numeric\" required autofocus><button>Confirm</button></form>",
            ui::escape_html(&factor.id)
        );
        let _ = session;
        return ui::page(
            "Set up your authenticator",
            &body,
            None,
            StatusCode::BAD_REQUEST,
            brand.theme_color.as_deref(),
            brand.logo_url.as_deref(),
        );
    };
    let confirmed = match state
        .store
        .try_update_mfa_factor(&factor.id, &brand.id, &user.id, |current| {
            if current.confirmed || current.r#type != "totp" {
                return false;
            }
            current.confirmed = true;
            current.last_used_totp_step = Some(step);
            true
        })
        .await
    {
        Ok(value) => value,
        Err(error) => return store_error(error).into_response(),
    };
    if !confirmed {
        return error(
            StatusCode::CONFLICT,
            "factor_changed",
            "This enrollment was already completed.",
        );
    }
    let codes = new_recovery_codes();
    let hashes: Vec<_> = codes.iter().map(|code| tokens::sha256_hex(code)).collect();
    let existing = match state.store.list_mfa_factors(&user.id).await {
        Ok(factors) => factors
            .into_iter()
            .find(|factor| factor.r#type == "recovery"),
        Err(error) => return store_error(error).into_response(),
    };
    if let Some(recovery) = existing {
        match state
            .store
            .try_update_mfa_factor(&recovery.id, &brand.id, &user.id, |factor| {
                factor.recovery_code_hashes = Some(hashes.clone());
                true
            })
            .await
        {
            Ok(true) => {}
            Ok(false) => {
                return error(
                    StatusCode::CONFLICT,
                    "factor_changed",
                    "Recovery codes could not be updated.",
                );
            }
            Err(error) => return store_error(error).into_response(),
        }
    } else {
        let recovery = MfaFactor {
            id: format!("mfa_{}", tokens::random_token(20)),
            instance_id: brand.id.clone(),
            user_id: user.id.clone(),
            r#type: "recovery".into(),
            secret_box: None,
            recovery_code_hashes: Some(hashes),
            confirmed: true,
            created_at: now(),
            last_used_totp_step: None,
        };
        if let Err(error) = state.store.create_mfa_factor(&recovery).await {
            return store_error(error).into_response();
        }
    }
    if let Err(error) = users::audit(
        &state,
        &brand.id,
        Some(&user.id),
        "mfa.enrolled",
        json!({"factorId":factor.id,"type":"totp"}),
    )
    .await
    {
        return error.into_response();
    }
    let rows = codes
        .iter()
        .map(|code| {
            format!(
                "<div class=\"row\"><code>{}</code></div>",
                ui::escape_html(code)
            )
        })
        .collect::<String>();
    let body = format!(
        "<h1>Recovery codes</h1><p class=\"muted\">Two-factor authentication is on. Each code works once and will not be shown again.</p><div class=\"list\">{rows}</div><a href=\"/ui/{slug}/account\"><button class=\"secondary\">I saved my codes</button></a>"
    );
    ui::page(
        "Recovery codes",
        &body,
        None,
        StatusCode::OK,
        brand.theme_color.as_deref(),
        brand.logo_url.as_deref(),
    )
}

#[derive(Deserialize, Default)]
struct ChallengeQuery {
    token: Option<String>,
}

async fn verification_page(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    Query(query): Query<ChallengeQuery>,
) -> Response {
    let brand = match page_instance(&state, &slug).await {
        Ok(instance) => instance,
        Err(response) => return response,
    };
    let Some(token) = query.token.as_deref() else {
        return error(
            StatusCode::UNAUTHORIZED,
            "invalid_mfa_token",
            "Sign-in step expired.",
        );
    };
    match peek_pending_authentication(&state, &brand.id, token).await {
        Ok(Some(pending)) => {
            let next = pending
                .authentication_next
                .as_deref()
                .and_then(|next| {
                    users::validated_authentication_next(
                        &state.options.public_origin,
                        &slug,
                        Some(next),
                    )
                })
                .unwrap_or_else(|| format!("/ui/{slug}/account"));
            let encoded_token: String =
                url::form_urlencoded::byte_serialize(token.as_bytes()).collect();
            let encoded_next: String =
                url::form_urlencoded::byte_serialize(next.as_bytes()).collect();
            let recovery =
                format!("/ui/{slug}/mfa/recovery?token={encoded_token}&next={encoded_next}");
            let body = format!(
                "<h1>Two-factor verification</h1><form method=\"post\" action=\"/ui/{slug}/mfa/verify\"><input type=\"hidden\" name=\"token\" value=\"{}\"><input type=\"hidden\" name=\"next\" value=\"{}\"><label>Authenticator code</label><input name=\"code\" inputmode=\"numeric\" autocomplete=\"one-time-code\" required autofocus><button>Verify</button></form><p class=\"hint\">Lost your authenticator? <a href=\"{recovery}\">Use a recovery code</a>.</p>",
                ui::escape_html(token),
                ui::escape_html(&next)
            );
            ui::page(
                "Two-factor verification",
                &body,
                None,
                StatusCode::OK,
                brand.theme_color.as_deref(),
                brand.logo_url.as_deref(),
            )
        }
        Ok(None) => ui::page(
            "Sign-in expired",
            &format!(
                "<h1>Your sign-in expired</h1><div class=\"error\">This step timed out. Please sign in again.</div><a href=\"/ui/{slug}/sign-in\"><button class=\"secondary\">Back to sign in</button></a>"
            ),
            None,
            StatusCode::UNAUTHORIZED,
            brand.theme_color.as_deref(),
            brand.logo_url.as_deref(),
        ),
        Err(error) => error.into_response(),
    }
}

async fn verify_mfa(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Form(form): Form<std::collections::HashMap<String, String>>,
) -> Response {
    if !users::require_same_origin(&headers, &state.options.public_origin) {
        return error(
            StatusCode::FORBIDDEN,
            "origin_rejected",
            "Cross-origin request rejected.",
        );
    }
    let brand = match page_instance(&state, &slug).await {
        Ok(instance) => instance,
        Err(response) => return response,
    };
    let token = form.get("token").map_or("", String::as_str);
    let code = form.get("code").map_or("", |value| value.trim());
    let Some(user_id) = (match peek_pending(&state, &brand.id, token).await {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    }) else {
        return error(
            StatusCode::UNAUTHORIZED,
            "invalid_mfa_token",
            "Sign-in step expired.",
        );
    };
    let valid = match verify_code(&state, &user_id, code).await {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    };
    if !valid {
        tokio::time::sleep(std::time::Duration::from_millis(INVALID_DELAY_MILLIS)).await;
        let allowed = match state
            .store
            .record_verification_token_failure(&tokens::sha256_hex(token), MAX_PENDING_ATTEMPTS)
            .await
        {
            Ok(value) => value,
            Err(error) => return store_error(error).into_response(),
        };
        return if allowed {
            error(
                StatusCode::UNAUTHORIZED,
                "invalid_code",
                "Invalid code. Try again.",
            )
        } else {
            error(
                StatusCode::UNAUTHORIZED,
                "invalid_mfa_token",
                "Sign-in step expired.",
            )
        };
    }
    let Some(pending) = (match consume_pending(&state, &brand.id, token).await {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    }) else {
        return error(
            StatusCode::UNAUTHORIZED,
            "invalid_mfa_token",
            "Sign-in step expired.",
        );
    };
    if pending.user_id != user_id {
        return error(
            StatusCode::UNAUTHORIZED,
            "invalid_mfa_token",
            "Sign-in step expired.",
        );
    }
    match users::establish_pending_mfa_session(
        &state,
        &brand,
        &pending,
        "totp",
        headers
            .get(header::USER_AGENT)
            .and_then(|value| value.to_str().ok()),
    )
    .await
    {
        Ok((_, token)) => {
            let next = pending
                .authentication_next
                .as_deref()
                .and_then(|next| {
                    users::validated_authentication_next(
                        &state.options.public_origin,
                        &slug,
                        Some(next),
                    )
                })
                .unwrap_or_else(|| format!("/ui/{slug}/account"));
            let mut response = Redirect::to(&next).into_response();
            ui::set_session_cookie(&mut response, &token);
            response
        }
        Err(error) => error.into_response(),
    }
}

async fn recovery_page(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    Query(query): Query<ChallengeQuery>,
    headers: HeaderMap,
) -> Response {
    let brand = match page_instance(&state, &slug).await {
        Ok(instance) => instance,
        Err(response) => return response,
    };
    let (user_id, pending_token, next) = if let Some(token) = query.token.as_deref() {
        let pending = match peek_pending_authentication(&state, &brand.id, token).await {
            Ok(Some(value)) => value,
            Ok(None) => {
                return error(
                    StatusCode::UNAUTHORIZED,
                    "invalid_mfa_token",
                    "Sign-in step expired.",
                );
            }
            Err(error) => return error.into_response(),
        };
        (
            pending.user_id,
            Some(token.to_owned()),
            pending
                .authentication_next
                .as_deref()
                .and_then(|next| {
                    users::validated_authentication_next(
                        &state.options.public_origin,
                        &slug,
                        Some(next),
                    )
                })
                .unwrap_or_else(|| format!("/ui/{slug}/account")),
        )
    } else {
        let (_session, user) = match session_user(&state, &brand.id, &headers).await {
            Ok(Some(value)) => value,
            Ok(None) => return signin(&slug),
            Err(error) => return error.into_response(),
        };
        (user.id, None, format!("/ui/{slug}/account"))
    };
    let factors = match state.store.list_mfa_factors(&user_id).await {
        Ok(value) => value,
        Err(error) => return store_error(error).into_response(),
    };
    if !factors.iter().any(|factor| {
        factor.r#type == "recovery"
            && factor
                .recovery_code_hashes
                .as_ref()
                .is_some_and(|codes| !codes.is_empty())
    }) {
        return ui::page(
            "No recovery codes",
            "<h1>No recovery codes left</h1><div class=\"error\">You have no unused recovery codes.</div>",
            None,
            StatusCode::NOT_FOUND,
            brand.theme_color.as_deref(),
            brand.logo_url.as_deref(),
        );
    }
    let pending_fields = pending_token.as_deref().map(|token| format!("<input type=\"hidden\" name=\"token\" value=\"{}\"><input type=\"hidden\" name=\"next\" value=\"{}\">", ui::escape_html(token), ui::escape_html(&next))).unwrap_or_default();
    let body = format!(
        "<h1>Use a recovery code</h1><p class=\"muted\">Enter one of your one-time recovery codes.</p><form method=\"post\" action=\"/ui/{slug}/mfa/recovery/consume\">{pending_fields}<label>Recovery code</label><input name=\"code\" autocomplete=\"off\" autofocus required><button>Unlock account</button></form>"
    );
    ui::page(
        "Use a recovery code",
        &body,
        None,
        StatusCode::OK,
        brand.theme_color.as_deref(),
        brand.logo_url.as_deref(),
    )
}

async fn consume_recovery_code(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Form(form): Form<std::collections::HashMap<String, String>>,
) -> Response {
    if !users::require_same_origin(&headers, &state.options.public_origin) {
        return error(
            StatusCode::FORBIDDEN,
            "origin_rejected",
            "Cross-origin request rejected.",
        );
    }
    let brand = match page_instance(&state, &slug).await {
        Ok(instance) => instance,
        Err(response) => return response,
    };
    let pending_token = form
        .get("token")
        .filter(|token| !token.is_empty())
        .map(String::as_str);
    let pending = if let Some(token) = pending_token {
        match peek_pending_authentication(&state, &brand.id, token).await {
            Ok(Some(pending)) => Some(pending),
            Ok(None) => {
                return error(
                    StatusCode::UNAUTHORIZED,
                    "invalid_mfa_token",
                    "Sign-in step expired.",
                );
            }
            Err(error) => return error.into_response(),
        }
    } else {
        match session_user(&state, &brand.id, &headers).await {
            Ok(Some((session, user))) => Some(PendingAuthentication {
                user_id: user.id,
                user_auth_epoch: Some(session.auth_epoch),
                authentication_method: None,
                email_snapshot: None,
                authentication_next: None,
            }),
            Ok(None) => return signin(&slug),
            Err(error) => return error.into_response(),
        }
    };
    let Some(mut pending_context) = pending else {
        return signin(&slug);
    };
    let user_id = pending_context.user_id.clone();
    let next = pending_context
        .authentication_next
        .as_deref()
        .and_then(|next| {
            users::validated_authentication_next(&state.options.public_origin, &slug, Some(next))
        })
        .unwrap_or_else(|| format!("/ui/{slug}/account"));
    let code = form.get("code").map_or("", |value| value.trim());
    let valid = if code.is_empty() {
        false
    } else {
        match verify_code(&state, &user_id, code).await {
            Ok(value) => value,
            Err(error) => return error.into_response(),
        }
    };
    if !valid {
        if let Some(token) = pending_token {
            tokio::time::sleep(std::time::Duration::from_millis(INVALID_DELAY_MILLIS)).await;
            let accepted = match state
                .store
                .record_verification_token_failure(&tokens::sha256_hex(token), MAX_PENDING_ATTEMPTS)
                .await
            {
                Ok(value) => value,
                Err(error) => return store_error(error).into_response(),
            };
            if !accepted {
                return error(
                    StatusCode::UNAUTHORIZED,
                    "invalid_mfa_token",
                    "Sign-in step expired.",
                );
            }
        }
        let retry = pending_token
            .map(|token| {
                let encoded_token: String =
                    url::form_urlencoded::byte_serialize(token.as_bytes()).collect();
                let encoded_next: String =
                    url::form_urlencoded::byte_serialize(next.as_bytes()).collect();
                format!("<a href=\"/ui/{slug}/mfa/recovery?token={encoded_token}&amp;next={encoded_next}\"><button class=\"secondary\">Try another code</button></a>")
            })
            .unwrap_or_else(|| {
                format!("<a href=\"/ui/{slug}/sign-in\"><button class=\"secondary\">Back to sign in</button></a>")
            });
        let body = format!(
            "<h1>Use a recovery code</h1><div class=\"error\">That code is invalid or already used.</div>{retry}"
        );
        return ui::page(
            "Use a recovery code",
            &body,
            None,
            StatusCode::UNAUTHORIZED,
            brand.theme_color.as_deref(),
            brand.logo_url.as_deref(),
        );
    }
    if let Some(token) = pending_token {
        let Some(consumed_pending) = (match consume_pending(&state, &brand.id, token).await {
            Ok(value) => value,
            Err(error) => return error.into_response(),
        }) else {
            return error(
                StatusCode::UNAUTHORIZED,
                "invalid_mfa_token",
                "Sign-in step expired.",
            );
        };
        if consumed_pending.user_id != user_id {
            return error(
                StatusCode::UNAUTHORIZED,
                "invalid_mfa_token",
                "Sign-in step expired.",
            );
        }
    }
    let user = match state.store.get_user(&user_id).await {
        Ok(Some(user)) if user.instance_id == brand.id && !user.deleted && !user.banned => user,
        Ok(_) => {
            return error(
                StatusCode::UNAUTHORIZED,
                "account_unavailable",
                "This account cannot sign in.",
            );
        }
        Err(error) => return store_error(error).into_response(),
    };
    let Some(expected_epoch) = pending_context.user_auth_epoch else {
        return error(
            StatusCode::UNAUTHORIZED,
            "invalid_mfa_token",
            "Sign-in step expired.",
        );
    };
    let Some(new_epoch) = (match state
        .store
        .advance_user_auth_epoch_if_current(&brand.id, &user.id, expected_epoch)
        .await
    {
        Ok(value) => value,
        Err(error) => return store_error(error).into_response(),
    }) else {
        return error(
            StatusCode::UNAUTHORIZED,
            "account_unavailable",
            "This account cannot sign in.",
        );
    };
    pending_context.user_auth_epoch = Some(new_epoch);
    let user = match state.store.get_user(&user.id).await {
        Ok(Some(user))
            if user.instance_id == brand.id
                && !user.deleted
                && !user.banned
                && user.auth_epoch == new_epoch =>
        {
            user
        }
        Ok(_) => {
            return error(
                StatusCode::UNAUTHORIZED,
                "account_unavailable",
                "This account cannot sign in.",
            );
        }
        Err(error) => return store_error(error).into_response(),
    };
    let established = if pending_token.is_some() {
        users::establish_pending_mfa_session(
            &state,
            &brand,
            &pending_context,
            "recovery_code",
            headers
                .get(header::USER_AGENT)
                .and_then(|value| value.to_str().ok()),
        )
        .await
    } else {
        users::establish_session_for_auth_epoch(
            &state,
            &brand.id,
            &user,
            "recovery_code",
            headers
                .get(header::USER_AGENT)
                .and_then(|value| value.to_str().ok()),
            None,
            new_epoch,
        )
        .await
    };
    match established {
        Ok((_, token)) => {
            let mut response = Redirect::to(&next).into_response();
            ui::set_session_cookie(&mut response, &token);
            response
        }
        Err(error) => error.into_response(),
    }
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
            "The API key cannot access MFA resources.",
        ));
    }
    Ok(context)
}

async fn list_factors(
    State(state): State<AppState>,
    Path((slug, user_id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    let route = format!("/v1/{slug}/users/{user_id}/mfa-factors");
    let context = match api_context(&state, &headers, &slug, &route).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let user = match state.store.get_user(&user_id).await {
        Ok(Some(user)) if user.instance_id == context.instance.id => user,
        Ok(_) => return error(StatusCode::NOT_FOUND, "user_not_found", "Unknown user."),
        Err(error) => return store_error(error).into_response(),
    };
    let factors = match state.store.list_mfa_factors(&user.id).await {
        Ok(value) => value,
        Err(error) => return store_error(error).into_response(),
    };
    let data: Vec<_> = factors.into_iter().filter(|factor| factor.instance_id == context.instance.id).map(|factor| json!({"id":factor.id,"type":factor.r#type,"confirmed":factor.confirmed,"created_at":factor.created_at})).collect();
    Json(json!({"data":data})).into_response()
}

async fn delete_factor(
    State(state): State<AppState>,
    Path((slug, user_id, id)): Path<(String, String, String)>,
    headers: HeaderMap,
) -> Response {
    let route = format!("/v1/{slug}/users/{user_id}/mfa-factors/{id}");
    let context = match api_context(&state, &headers, &slug, &route).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let deleted = match state
        .store
        .try_delete_mfa_factor(&id, &context.instance.id, &user_id)
        .await
    {
        Ok(value) => value,
        Err(error) => return store_error(error).into_response(),
    };
    if !deleted {
        return error(
            StatusCode::NOT_FOUND,
            "factor_not_found",
            "Unknown MFA factor.",
        );
    }
    if let Err(error) = users::audit(
        &state,
        &context.instance.id,
        Some(&context.api_key_id),
        "mfa.factor_deleted",
        json!({"factorId":id,"userId":user_id}),
    )
    .await
    {
        return error.into_response();
    }
    StatusCode::NO_CONTENT.into_response()
}

async fn regenerate_recovery_codes(
    State(state): State<AppState>,
    Path((slug, user_id, id)): Path<(String, String, String)>,
    headers: HeaderMap,
) -> Response {
    let route = format!("/v1/{slug}/users/{user_id}/mfa-factors/{id}/regenerate-recovery");
    let context = match api_context(&state, &headers, &slug, &route).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let factor = match state.store.get_mfa_factor(&id).await {
        Ok(Some(factor))
            if factor.instance_id == context.instance.id && factor.user_id == user_id =>
        {
            factor
        }
        Ok(_) => {
            return error(
                StatusCode::NOT_FOUND,
                "factor_not_found",
                "Unknown MFA factor.",
            );
        }
        Err(error) => return store_error(error).into_response(),
    };
    if factor.r#type != "recovery" {
        return error(
            StatusCode::BAD_REQUEST,
            "invalid_factor",
            "Recovery codes can only be regenerated for a recovery factor.",
        );
    }
    let codes = new_recovery_codes();
    let hashes: Vec<_> = codes.iter().map(|code| tokens::sha256_hex(code)).collect();
    let updated = state
        .store
        .try_update_mfa_factor(&id, &context.instance.id, &user_id, |factor| {
            factor.recovery_code_hashes = Some(hashes.clone());
            true
        })
        .await;
    match updated {
        Ok(true) => Json(json!({"data":codes})).into_response(),
        Ok(false) => error(
            StatusCode::CONFLICT,
            "factor_changed",
            "Recovery codes could not be regenerated.",
        ),
        Err(error) => store_error(error).into_response(),
    }
}
