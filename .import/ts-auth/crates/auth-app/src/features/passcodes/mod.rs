//! Passwordless email passcodes and the second-factor bridge.

use std::{
    collections::HashMap,
    time::{SystemTime, UNIX_EPOCH},
};

use auth_contracts::{
    AppError,
    models::{Instance, VerificationToken},
};
use auth_security::tokens;
use axum::{
    Router,
    body::to_bytes,
    extract::{Path, Query, Request, State},
    http::{HeaderMap, StatusCode, header},
    response::{IntoResponse, Response},
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
    features::{mfa, ui, users},
};

const CODE_LIFETIME_SECONDS: i64 = 10 * 60;
const RESEND_THROTTLE_SECONDS: i64 = 30;
const MAX_VERIFY_ATTEMPTS: i32 = 5;
const INVALID_DELAY_MILLIS: u64 = 250;
const BODY_LIMIT: usize = 64 * 1024;

pub fn router() -> Router<AppState> {
    Router::new()
        .route("/ui/{slug}/passcode", get(passcode_page))
        .route("/ui/{slug}/passcode/request", post(request_passcode))
        .route("/ui/{slug}/passcode/verify", post(verify_passcode))
        .route("/ui/{slug}/passcode/mfa", get(mfa_page).post(verify_mfa))
}

pub fn openapi() -> OpenApiDoc {
    let mut paths = Paths::new();
    for (path, method, summary) in [
        (
            "/ui/{slug}/passcode",
            HttpMethod::Get,
            "Render email passcode sign-in",
        ),
        (
            "/ui/{slug}/passcode/request",
            HttpMethod::Post,
            "Request an email passcode",
        ),
        (
            "/ui/{slug}/passcode/verify",
            HttpMethod::Post,
            "Verify an email passcode",
        ),
        (
            "/ui/{slug}/passcode/mfa",
            HttpMethod::Get,
            "Render second-factor verification",
        ),
        (
            "/ui/{slug}/passcode/mfa",
            HttpMethod::Post,
            "Verify second factor after passcode sign-in",
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
    OpenApiDoc::new(
        Info::new("TerraScale Auth Email Passcodes API", "1.0.0"),
        paths,
    )
}

fn now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |duration| duration.as_secs() as i64)
}

fn internal(error: impl std::fmt::Debug) -> AppError {
    tracing::error!(?error, "passcode store operation failed");
    AppError::unavailable()
}

fn error_json(status: StatusCode, code: &'static str, message: &'static str) -> Response {
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
        Err(error) => Err(internal(error).into_response()),
    }
}

#[expect(
    clippy::result_large_err,
    reason = "HTTP response is the intentional endpoint error payload"
)]
async fn parse_fields(request: Request) -> Result<(HashMap<String, String>, bool), Response> {
    let is_json = request
        .headers()
        .get(header::CONTENT_TYPE)
        .and_then(|value| value.to_str().ok())
        .is_some_and(|value| {
            value
                .split(';')
                .next()
                .is_some_and(|mime| mime.trim().eq_ignore_ascii_case("application/json"))
        });
    let bytes = to_bytes(request.into_body(), BODY_LIMIT)
        .await
        .map_err(|_| {
            error_json(
                StatusCode::BAD_REQUEST,
                "invalid_request",
                "Request body is too large or unreadable.",
            )
        })?;
    let fields = if is_json {
        serde_json::from_slice::<HashMap<String, String>>(&bytes).map_err(|_| {
            error_json(
                StatusCode::BAD_REQUEST,
                "invalid_request",
                "Request body must be valid JSON.",
            )
        })?
    } else {
        url::form_urlencoded::parse(&bytes).into_owned().collect()
    };
    Ok((fields, is_json))
}

fn require_origin(headers: &HeaderMap, state: &AppState) -> bool {
    users::require_same_origin(headers, &state.options.public_origin)
}

fn generic_confirmation(slug: &str, brand: &Instance, email: &str) -> Response {
    let body = format!(
        "<h1>Check your email</h1><div class=\"success\">If an account exists for {}, a sign-in code is on its way.</div><p class=\"muted\">The code expires in 10 minutes and can only be used once.</p><p class=\"hint\"><a href=\"/ui/{}/sign-in\">Back to sign in</a></p>",
        ui::escape_html(email),
        ui::escape_html(slug)
    );
    ui::page(
        "Check your email",
        &body,
        None,
        StatusCode::OK,
        brand.theme_color.as_deref(),
        brand.logo_url.as_deref(),
    )
}

#[derive(Deserialize, Default)]
struct PasscodePageQuery {
    next: Option<String>,
}

async fn passcode_page(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    Query(query): Query<PasscodePageQuery>,
) -> Response {
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
    let next = ui::escape_html(&next);
    let body = format!(
        "<h1>Sign in with a code</h1><p class=\"muted\">We'll email you a one-time code.</p><form method=\"post\" action=\"/ui/{slug}/passcode/request\"><input type=\"hidden\" name=\"next\" value=\"{next}\"><label>Email</label><input name=\"email\" type=\"email\" autocomplete=\"username\" required><button>Send code</button></form><form method=\"post\" action=\"/ui/{slug}/passcode/verify\"><input type=\"hidden\" name=\"next\" value=\"{next}\"><label>Email</label><input name=\"email\" type=\"email\" required><label>Code</label><input name=\"code\" inputmode=\"numeric\" autocomplete=\"one-time-code\" required><button>Verify</button></form><p class=\"hint\"><a href=\"/ui/{slug}/sign-in\">Back to sign in</a></p>"
    );
    ui::page(
        "Sign in with a code",
        &body,
        None,
        StatusCode::OK,
        brand.theme_color.as_deref(),
        brand.logo_url.as_deref(),
    )
}

#[utoipa::path(post, path = "/ui/{slug}/passcode/request", params(("slug" = String, Path)), request_body = inline(HashMap<String, String>), responses((status = 200, description = "Generic delivery confirmation"), (status = 403, description = "Cross-origin request")))]
async fn request_passcode(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    request: Request,
) -> Response {
    let headers = request.headers().clone();
    if !require_origin(&headers, &state) {
        return error_json(
            StatusCode::FORBIDDEN,
            "origin_rejected",
            "Cross-origin request rejected.",
        );
    }
    let (fields, is_json) = match parse_fields(request).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let email = fields.get("email").map_or("", |value| value.trim());
    if email.is_empty() {
        return error_json(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "email is required.",
        );
    }
    let brand = match page_instance(&state, &slug).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let authentication_next = users::validated_authentication_next(
        &state.options.public_origin,
        &slug,
        fields.get("next").map(String::as_str),
    )
    .unwrap_or_else(|| format!("/ui/{slug}/account"));
    match state
        .store
        .find_user_by_email(&brand.id, &users::normalize_email(email))
        .await
    {
        Ok(Some(user)) if !user.deleted && !user.banned && user.email.is_some() => {
            match users::authentication_allowed(&state, &brand.id, &user).await {
                Ok(false) => {}
                Err(error) => return error.into_response(),
                Ok(true) => {
                    let recent = match state
                        .store
                        .list_user_verification_tokens(&user.id, "passcode")
                        .await
                    {
                        Ok(records) => records
                            .into_iter()
                            .any(|record| record.created_at > now() - RESEND_THROTTLE_SECONDS),
                        Err(error) => return internal(error).into_response(),
                    };
                    if recent {
                        return if is_json {
                            axum::Json(json!({"ok":true})).into_response()
                        } else {
                            generic_confirmation(&slug, &brand, email)
                        };
                    }
                    let Some(code) = tokens::random_digits(6) else {
                        return AppError::internal().into_response();
                    };
                    let record = VerificationToken {
                        id: format!("vtok_{}", tokens::random_token(20)),
                        instance_id: brand.id.clone(),
                        user_id: Some(user.id.clone()),
                        purpose: "passcode".into(),
                        authentication_method: None,
                        authentication_next: Some(authentication_next.clone()),
                        user_auth_epoch: Some(user.auth_epoch),
                        token_key: tokens::sha256_hex(&code),
                        email_snapshot: user.email.as_deref().map(users::normalize_email),
                        expires_at: now() + CODE_LIFETIME_SECONDS,
                        consumed: false,
                        failed_attempts: 0,
                        created_at: now(),
                    };
                    if let Err(error) = state.store.create_verification_token(&record).await {
                        return internal(error).into_response();
                    }
                    if let Some(to) = user.email.as_deref() {
                        state
                            .email
                            .send(
                                to,
                                "Your ts-auth sign-in code",
                                &format!("Your ts-auth sign-in code is {code}"),
                            )
                            .await;
                    }
                }
            }
        }
        Ok(_) => {}
        Err(error) => return internal(error).into_response(),
    }
    if is_json {
        axum::Json(json!({"ok":true})).into_response()
    } else {
        generic_confirmation(&slug, &brand, email)
    }
}

fn invalid_code() -> Response {
    (
        StatusCode::UNAUTHORIZED,
        axum::Json(json!({"ok":false,"error":"invalid_code"})),
    )
        .into_response()
}

#[utoipa::path(post, path = "/ui/{slug}/passcode/verify", params(("slug" = String, Path)), request_body = inline(HashMap<String, String>), responses((status = 200, description = "Sign-in or MFA required"), (status = 401, description = "Invalid code")))]
async fn verify_passcode(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    request: Request,
) -> Response {
    let headers = request.headers().clone();
    let user_agent = headers
        .get(header::USER_AGENT)
        .and_then(|value| value.to_str().ok())
        .map(str::to_owned);
    if !require_origin(&headers, &state) {
        return error_json(
            StatusCode::FORBIDDEN,
            "origin_rejected",
            "Cross-origin request rejected.",
        );
    }
    let (fields, is_json) = match parse_fields(request).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let email = fields.get("email").map_or("", |value| value.trim());
    let code = fields.get("code").map_or("", |value| value.trim());
    if email.is_empty() || code.is_empty() {
        return error_json(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "email and code are required.",
        );
    }
    let brand = match page_instance(&state, &slug).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let user = match state
        .store
        .find_user_by_email(&brand.id, &users::normalize_email(email))
        .await
    {
        Ok(value) => value.filter(|user| !user.deleted && !user.banned),
        Err(error) => return internal(error).into_response(),
    };
    let user = if let Some(user) = user {
        match users::authentication_allowed(&state, &brand.id, &user).await {
            Ok(true) => Some(user),
            Ok(false) => None,
            Err(error) => return error.into_response(),
        }
    } else {
        None
    };
    let normalized_email = users::normalize_email(email);
    let record = if let Some(user) = &user {
        state
            .store
            .consume_verification_token(&tokens::sha256_hex(code), |record| {
                record.instance_id == brand.id
                    && record.purpose == "passcode"
                    && record.user_id.as_deref() == Some(user.id.as_str())
                    && record.email_snapshot.as_deref() == Some(normalized_email.as_str())
                    && !record.consumed
                    && record.expires_at > now()
                    && record.failed_attempts < MAX_VERIFY_ATTEMPTS
            })
            .await
    } else {
        Ok(None)
    };
    let record = match record {
        Ok(value) => value,
        Err(error) => return internal(error).into_response(),
    };
    let (Some(record), Some(user)) = (record, user) else {
        tokio::time::sleep(std::time::Duration::from_millis(INVALID_DELAY_MILLIS)).await;
        let failed_user = state
            .store
            .find_user_by_email(&brand.id, &users::normalize_email(email))
            .await
            .ok()
            .flatten()
            .filter(|user| !user.deleted && !user.banned);
        if let Some(user) = failed_user {
            match users::authentication_allowed(&state, &brand.id, &user).await {
                Ok(false) => {}
                Err(error) => return error.into_response(),
                Ok(true) => {
                    match state
                        .store
                        .record_verification_failure(&brand.id, &user.id, "passcode")
                        .await
                    {
                        Ok(attempts) if attempts >= MAX_VERIFY_ATTEMPTS => {
                            if let Err(error) = state
                                .store
                                .invalidate_verification_tokens(&user.id, "passcode")
                                .await
                            {
                                return internal(error).into_response();
                            }
                        }
                        Ok(_) => {}
                        Err(error) => return internal(error).into_response(),
                    }
                }
            }
        }
        return invalid_code();
    };
    let Some(email_snapshot) = record.email_snapshot.as_deref() else {
        return invalid_code();
    };
    let Some(proof_user_id) = record.user_id.as_deref() else {
        return invalid_code();
    };
    if record.user_auth_epoch != Some(user.auth_epoch) {
        return invalid_code();
    }
    let verified_user = match users::claim_verified_mailbox_with_proof(
        &state,
        &brand.id,
        email_snapshot,
        "passcode",
        Some(proof_user_id),
        record.user_auth_epoch,
        None,
        None,
    )
    .await
    {
        Ok(user) => user,
        Err(error) => return error.into_response(),
    };
    let next = record
        .authentication_next
        .as_deref()
        .and_then(|next| {
            users::validated_authentication_next(&state.options.public_origin, &slug, Some(next))
        })
        .unwrap_or_else(|| format!("/ui/{slug}/account"));
    match users::complete_first_factor(
        &state,
        &brand,
        &verified_user,
        "passcode",
        user_agent.as_deref(),
        Some(email_snapshot),
        Some(&next),
    )
    .await
    {
        Ok(users::FirstFactorOutcome::MfaPending { token }) => {
            if is_json {
                axum::Json(json!({"mfa_required":true,"mfa_token":token})).into_response()
            } else {
                axum::response::Redirect::to(&format!("/ui/{slug}/mfa/verify?token={token}"))
                    .into_response()
            }
        }
        Ok(users::FirstFactorOutcome::Session { token }) => {
            let mut response = if is_json {
                axum::Json(json!({"ok":true,"redirect":next})).into_response()
            } else {
                axum::response::Redirect::to(&next).into_response()
            };
            ui::set_session_cookie(&mut response, &token);
            response
        }
        Err(error) => error.into_response(),
    }
}

#[derive(Deserialize, Default)]
struct MfaQuery {
    token: Option<String>,
}

async fn pending_user(
    state: &AppState,
    instance_id: &str,
    token: &str,
) -> Result<Option<String>, AppError> {
    mfa::peek_pending(state, instance_id, token)
        .await
        .map_err(internal)
}

async fn mfa_page(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    Query(query): Query<MfaQuery>,
) -> Response {
    let brand = match page_instance(&state, &slug).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let Some(token) = query.token.as_deref() else {
        return error_json(
            StatusCode::UNAUTHORIZED,
            "invalid_mfa_token",
            "Sign-in step expired.",
        );
    };
    match pending_user(&state, &brand.id, token).await {
        Ok(Some(_)) => {
            let encoded_token: String =
                url::form_urlencoded::byte_serialize(token.as_bytes()).collect();
            let recovery = format!("/ui/{slug}/mfa/recovery?token={encoded_token}");
            let body = format!(
                "<h1>Two-factor verification</h1><p class=\"muted\">Enter the code from your authenticator app to finish signing in.</p><form method=\"post\"><input type=\"hidden\" name=\"mfa_token\" value=\"{}\"><label>Authenticator code</label><input name=\"code\" inputmode=\"numeric\" autocomplete=\"one-time-code\" required autofocus><button>Verify</button></form><p class=\"hint\"><a href=\"{recovery}\">Use a recovery code</a> · <a href=\"/ui/{slug}/sign-in\">Back to sign in</a></p>",
                ui::escape_html(token)
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
        Ok(None) => error_json(
            StatusCode::UNAUTHORIZED,
            "invalid_mfa_token",
            "Sign-in step expired.",
        ),
        Err(error) => error.into_response(),
    }
}

#[utoipa::path(post, path = "/ui/{slug}/passcode/mfa", params(("slug" = String, Path)), request_body = inline(HashMap<String, String>), responses((status = 200, description = "Sign-in complete"), (status = 401, description = "Invalid MFA code")))]
async fn verify_mfa(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    request: Request,
) -> Response {
    let headers = request.headers().clone();
    let user_agent = headers
        .get(header::USER_AGENT)
        .and_then(|value| value.to_str().ok())
        .map(str::to_owned);
    if !require_origin(&headers, &state) {
        return error_json(
            StatusCode::FORBIDDEN,
            "origin_rejected",
            "Cross-origin request rejected.",
        );
    }
    let (fields, is_json) = match parse_fields(request).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let token = fields
        .get("mfa_token")
        .map(String::as_str)
        .or_else(|| fields.get("token").map(String::as_str))
        .unwrap_or("");
    let code = fields.get("code").map_or("", |value| value.trim());
    if token.is_empty() || code.is_empty() {
        return error_json(
            StatusCode::BAD_REQUEST,
            "invalid_request",
            "mfa_token and code are required.",
        );
    }
    let brand = match page_instance(&state, &slug).await {
        Ok(value) => value,
        Err(response) => return response,
    };
    let Some(pending) = (match mfa::peek_pending_authentication(&state, &brand.id, token).await {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    }) else {
        return error_json(
            StatusCode::UNAUTHORIZED,
            "invalid_mfa_token",
            "Sign-in step expired.",
        );
    };
    let valid = match mfa::verify_code(&state, &pending.user_id, code).await {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    };
    if !valid {
        tokio::time::sleep(std::time::Duration::from_millis(INVALID_DELAY_MILLIS)).await;
        let accepted = match state
            .store
            .record_verification_token_failure(&tokens::sha256_hex(token), 10)
            .await
        {
            Ok(value) => value,
            Err(error) => return internal(error).into_response(),
        };
        return if accepted {
            invalid_code()
        } else {
            error_json(
                StatusCode::UNAUTHORIZED,
                "invalid_mfa_token",
                "Sign-in step expired.",
            )
        };
    }
    let Some(consumed_pending) = (match mfa::consume_pending(&state, &brand.id, token).await {
        Ok(pending) => pending,
        Err(error) => return error.into_response(),
    }) else {
        return error_json(
            StatusCode::UNAUTHORIZED,
            "invalid_mfa_token",
            "Sign-in step expired.",
        );
    };
    if consumed_pending.user_id != pending.user_id {
        return error_json(
            StatusCode::UNAUTHORIZED,
            "invalid_mfa_token",
            "Sign-in step expired.",
        );
    }
    let next = pending
        .authentication_next
        .as_deref()
        .and_then(|next| {
            users::validated_authentication_next(&state.options.public_origin, &slug, Some(next))
        })
        .unwrap_or_else(|| format!("/ui/{slug}/account"));
    match users::establish_pending_mfa_session(
        &state,
        &brand,
        &consumed_pending,
        "totp",
        user_agent.as_deref(),
    )
    .await
    {
        Ok((_, token)) => {
            let mut response = if is_json {
                axum::Json(json!({"ok":true,"redirect":next})).into_response()
            } else {
                axum::response::Redirect::to(&next).into_response()
            };
            ui::set_session_cookie(&mut response, &token);
            response
        }
        Err(error) => error.into_response(),
    }
}
