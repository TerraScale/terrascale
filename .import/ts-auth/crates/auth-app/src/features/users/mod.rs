//! Hosted user registration, sign-in, password recovery, and browser sessions.

use std::{
    collections::HashMap,
    sync::{Arc, OnceLock},
    time::{SystemTime, UNIX_EPOCH},
};

use auth_contracts::{
    AppError, AppResult,
    models::{
        AuditEvent, Instance, ScimProvisioning, ScimProvisioningPhase, Session, User,
        VerificationToken,
    },
};
use auth_security::tokens;
use auth_store::{Predicate, StoreError};
use axum::{
    Form, Router,
    extract::{Path, Query, State},
    http::{HeaderMap, StatusCode, header},
    response::{IntoResponse, Redirect, Response},
    routing::{get, post},
};
use serde::Deserialize;
use serde_json::{Map, Value, json};
use utoipa::openapi::{
    Info, OpenApi as OpenApiDoc,
    path::{HttpMethod, Operation, Paths},
};

use crate::{
    AppState,
    features::{billing, ui},
};

/// Hosted sessions live for two weeks; the browser stores only an opaque token.
pub const SESSION_LIFETIME_SECONDS: i64 = 14 * 24 * 60 * 60;
const MAGIC_LINK_LIFETIME_SECONDS: i64 = 10 * 60;
const RESET_PASSWORD_LIFETIME_SECONDS: i64 = 30 * 60;
const VERIFY_EMAIL_LIFETIME_SECONDS: i64 = 24 * 60 * 60;
const PASSWORD_KDF_CONCURRENCY: usize = 4;
const EMAIL_BOUND_TOKEN_PURPOSES: &[&str] =
    &["verify_email", "magic_link", "reset_password", "passcode"];

#[derive(Debug)]
pub enum FirstFactorOutcome {
    Session { token: String },
    MfaPending { token: String },
}

#[derive(Debug, Clone, Default)]
pub struct UserChanges {
    pub email: Option<Option<String>>,
    pub username: Option<Option<String>>,
    pub name: Option<Option<String>>,
    pub password: Option<Option<String>>,
    pub banned: Option<bool>,
    pub ban_reason: Option<Option<String>>,
    pub public_metadata: Option<Map<String, Value>>,
    pub private_metadata: Option<Map<String, Value>>,
}

pub fn router() -> Router<AppState> {
    Router::new()
        .route("/ui/{slug}/sign-in", get(sign_in_page).post(sign_in))
        .route("/ui/{slug}/sign-up", get(sign_up_page).post(sign_up))
        .route("/ui/{slug}/sign-out", post(sign_out))
        .route(
            "/ui/{slug}/verify-email",
            get(verify_email).post(complete_email_verification),
        )
        .route("/ui/{slug}/magic-link", post(request_magic_link))
        .route("/ui/{slug}/magic-link/consume", get(consume_magic_link))
        .route(
            "/ui/{slug}/forgot-password",
            get(forgot_password_page).post(forgot_password),
        )
        .route(
            "/ui/{slug}/reset-password",
            get(reset_password_page).post(reset_password),
        )
        .route("/ui/{slug}/account", get(account_page))
        .route("/ui/{slug}/account/password", post(change_password))
        .route("/ui/{slug}/account/sessions", get(sessions_page))
        .route(
            "/ui/{slug}/account/sessions/{id}/revoke",
            post(revoke_session),
        )
        .route(
            "/ui/{slug}/account/sessions/revoke-all",
            post(revoke_all_sessions),
        )
}

/// Documents every method served by the hosted user router for Scalar/OpenAPI.
pub fn openapi() -> OpenApiDoc {
    let mut paths = Paths::new();
    for (path, method, summary) in [
        (
            "/ui/{slug}/sign-in",
            HttpMethod::Get,
            "Render the sign-in page",
        ),
        (
            "/ui/{slug}/sign-in",
            HttpMethod::Post,
            "Sign in with an email and password",
        ),
        (
            "/ui/{slug}/sign-up",
            HttpMethod::Get,
            "Render the account registration page",
        ),
        (
            "/ui/{slug}/sign-up",
            HttpMethod::Post,
            "Register an account",
        ),
        (
            "/ui/{slug}/sign-out",
            HttpMethod::Post,
            "Sign out the current browser session",
        ),
        (
            "/ui/{slug}/verify-email",
            HttpMethod::Get,
            "Render trusted mailbox completion",
        ),
        (
            "/ui/{slug}/verify-email",
            HttpMethod::Post,
            "Complete trusted mailbox verification",
        ),
        (
            "/ui/{slug}/magic-link",
            HttpMethod::Post,
            "Request a passwordless sign-in link",
        ),
        (
            "/ui/{slug}/magic-link/consume",
            HttpMethod::Get,
            "Consume a passwordless sign-in link",
        ),
        (
            "/ui/{slug}/forgot-password",
            HttpMethod::Get,
            "Render the password recovery page",
        ),
        (
            "/ui/{slug}/forgot-password",
            HttpMethod::Post,
            "Request a password reset link",
        ),
        (
            "/ui/{slug}/reset-password",
            HttpMethod::Get,
            "Validate a password reset link",
        ),
        (
            "/ui/{slug}/reset-password",
            HttpMethod::Post,
            "Reset a password",
        ),
        (
            "/ui/{slug}/account",
            HttpMethod::Get,
            "View the current account",
        ),
        (
            "/ui/{slug}/account/password",
            HttpMethod::Post,
            "Change the current password",
        ),
        (
            "/ui/{slug}/account/sessions",
            HttpMethod::Get,
            "List active sessions",
        ),
        (
            "/ui/{slug}/account/sessions/{id}/revoke",
            HttpMethod::Post,
            "Revoke one session",
        ),
        (
            "/ui/{slug}/account/sessions/revoke-all",
            HttpMethod::Post,
            "Revoke all sessions",
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
        Info::new("TerraScale Auth Hosted Identity API", "1.0.0"),
        paths,
    )
}

fn now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |duration| duration.as_secs() as i64)
}

fn store_error(error: impl std::fmt::Debug) -> AppError {
    tracing::error!(?error, "hosted identity store operation failed");
    AppError::unavailable()
}

fn invalid_request(message: &'static str) -> AppError {
    AppError::new(StatusCode::BAD_REQUEST, "invalid_request", message)
}

fn conflict(message: &'static str) -> AppError {
    AppError::new(StatusCode::CONFLICT, "already_exists", message)
}

fn not_found() -> AppError {
    AppError::new(
        StatusCode::NOT_FOUND,
        "not_found",
        "The requested account was not found.",
    )
}

fn same_origin_error() -> Response {
    ui::page(
        "Request rejected",
        "<h1>Request rejected</h1><div class=\"error\">This request did not come from this site. Reload the page and try again.</div>",
        None,
        StatusCode::FORBIDDEN,
        None,
        None,
    )
}

/// Require a browser request's Origin or Referer to match the configured public origin.
/// Hosted forms rely on this check together with the session cookie's SameSite=Lax flag.
pub fn require_same_origin(headers: &HeaderMap, public_origin: &str) -> bool {
    let Ok(expected) = reqwest::Url::parse(public_origin) else {
        return false;
    };
    let expected = expected.origin().ascii_serialization();
    let supplied = headers
        .get(header::ORIGIN)
        .and_then(|value| value.to_str().ok())
        .or_else(|| {
            headers
                .get(header::REFERER)
                .and_then(|value| value.to_str().ok())
        });
    supplied
        .and_then(|value| reqwest::Url::parse(value).ok())
        .is_some_and(|url| url.origin().ascii_serialization() == expected)
}

pub fn normalize_email(email: &str) -> String {
    email.trim().to_lowercase()
}

pub fn is_valid_email(email: &str) -> bool {
    let email = email.trim();
    let mut parts = email.split('@');
    let Some(local) = parts.next() else {
        return false;
    };
    let Some(domain) = parts.next() else {
        return false;
    };
    email.len() <= 254
        && !local.is_empty()
        && domain.contains('.')
        && parts.next().is_none()
        && !email.chars().any(char::is_whitespace)
}

fn normalize_optional(value: Option<&str>) -> Option<String> {
    value
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(ToOwned::to_owned)
}

fn user_email_key(instance_id: &str, email: &str) -> String {
    format!("{instance_id}:{}", normalize_email(email))
}

fn user_name_key(instance_id: &str, username: &str) -> String {
    format!("{instance_id}:{}", username.trim().to_lowercase())
}

fn map_store_error(error: StoreError) -> AppError {
    if error.is_conflict() {
        conflict("The email address or username is already in use.")
    } else if matches!(error, StoreError::QuotaExceeded) {
        AppError::new(
            StatusCode::PAYMENT_REQUIRED,
            "quota_exceeded",
            "This application has reached its user limit.",
        )
    } else {
        store_error(error)
    }
}

/// Hash a password with Argon2id without blocking a Tokio worker thread.
pub async fn hash_password(password: &str) -> AppResult<String> {
    if !(8..=1024).contains(&password.len()) {
        return Err(invalid_request(
            "Passwords must be between 8 and 1024 bytes.",
        ));
    }
    let password = password.to_owned();
    let permit = password_kdf_semaphore()
        .try_acquire_owned()
        .map_err(|_| password_kdf_busy())?;
    tokio::task::spawn_blocking(move || {
        let _permit = permit;
        auth_security::password::hash(&password)
    })
    .await
    .map_err(|_| AppError::internal())?
    .map_err(|_| AppError::internal())
}

/// Verify a password away from the async executor. Unknown accounts burn the same Argon2 cost.
pub async fn verify_password(user: Option<&User>, password: &str) -> AppResult<bool> {
    let hash = user
        .and_then(|user| {
            (!user.deleted)
                .then_some(user.password_hash.as_deref())
                .flatten()
        })
        .map(ToOwned::to_owned);
    let password = password.to_owned();
    let permit = password_kdf_semaphore()
        .try_acquire_owned()
        .map_err(|_| password_kdf_busy())?;
    tokio::task::spawn_blocking(move || {
        let _permit = permit;
        match hash {
            Some(hash) => auth_security::password::verify(&password, &hash),
            None => {
                static DUMMY_HASH: OnceLock<String> = OnceLock::new();
                let dummy = DUMMY_HASH.get_or_init(|| {
                    auth_security::password::hash("ts-auth sign-in timing equalizer")
                        .expect("fixed valid dummy password and Argon2 parameters")
                });
                let _ = auth_security::password::verify(&password, dummy);
                false
            }
        }
    })
    .await
    .map_err(|_| AppError::internal())
}

fn password_kdf_semaphore() -> Arc<tokio::sync::Semaphore> {
    static SEMAPHORE: OnceLock<Arc<tokio::sync::Semaphore>> = OnceLock::new();
    Arc::clone(
        SEMAPHORE.get_or_init(|| Arc::new(tokio::sync::Semaphore::new(PASSWORD_KDF_CONCURRENCY))),
    )
}

fn password_kdf_busy() -> AppError {
    AppError::new(
        StatusCode::SERVICE_UNAVAILABLE,
        "authentication_busy",
        "Authentication is busy. Please retry shortly.",
    )
}

/// Create an identity through the shared quota and unique-key rules used by every caller.
pub async fn create_user(
    state: &AppState,
    instance_id: &str,
    email: Option<&str>,
    username: Option<&str>,
    password: Option<&str>,
    name: Option<&str>,
) -> AppResult<User> {
    create_user_with_provisioning(
        state,
        instance_id,
        email,
        username,
        password,
        name,
        None,
        false,
        true,
        None,
    )
    .await
}

/// Create a public signup identity that remains untrusted until its mailbox
/// proof completes. Only this path is eligible for replacement on proof.
pub async fn create_unverified_signup_user(
    state: &AppState,
    instance_id: &str,
    email: Option<&str>,
    password: Option<&str>,
    name: Option<&str>,
) -> AppResult<User> {
    create_user_with_provisioning(
        state,
        instance_id,
        email,
        None,
        password,
        name,
        None,
        false,
        false,
        None,
    )
    .await
}

/// Create a hidden identity owned by one SCIM directory while its link and
/// required organization membership are being committed.
pub async fn create_provisional_scim_user(
    state: &AppState,
    instance_id: &str,
    directory_id: &str,
    email: &str,
    name: Option<&str>,
) -> AppResult<User> {
    if directory_id.is_empty() {
        return Err(invalid_request("A SCIM directory is required."));
    }
    create_user_with_provisioning(
        state,
        instance_id,
        Some(email),
        None,
        None,
        name,
        Some(directory_id),
        false,
        true,
        None,
    )
    .await
}

#[expect(
    clippy::too_many_arguments,
    reason = "These values are independent identity-creation policy inputs at the shared creation boundary."
)]
async fn create_user_with_provisioning(
    state: &AppState,
    instance_id: &str,
    email: Option<&str>,
    username: Option<&str>,
    password: Option<&str>,
    name: Option<&str>,
    provisioning_directory_id: Option<&str>,
    email_verified: bool,
    identity_established: bool,
    password_hash_override: Option<String>,
) -> AppResult<User> {
    let email = normalize_optional(email);
    if email.as_deref().is_some_and(|value| !is_valid_email(value)) {
        return Err(invalid_request("Please enter a valid email address."));
    }
    let username = normalize_optional(username);
    if username
        .as_deref()
        .is_some_and(|value| value.len() > 128 || value.chars().any(char::is_whitespace))
    {
        return Err(invalid_request(
            "Usernames must be at most 128 characters and contain no whitespace.",
        ));
    }
    let password_hash = match password_hash_override {
        Some(value) => Some(value),
        None => match password.filter(|value| !value.is_empty()) {
            Some(value) => Some(hash_password(value).await?),
            None => None,
        },
    };

    let billing = billing::current(&state.store, instance_id)
        .await
        .map_err(store_error)?;
    if billing::count_users(&state.store, instance_id, billing.quota)
        .await
        .map_err(store_error)?
        >= billing.quota
    {
        return Err(AppError::new(
            StatusCode::PAYMENT_REQUIRED,
            "quota_exceeded",
            "This application has reached its user limit.",
        ));
    }

    let user_id = format!("usr_{}", tokens::random_token(20));
    let seat = state
        .store
        .claim_quota_seat(instance_id, billing.quota, &user_id)
        .await
        .map_err(map_store_error)?;
    let timestamp = now();
    let user = User {
        id: user_id,
        instance_id: instance_id.to_owned(),
        email: email.as_deref().map(normalize_email),
        email_verified,
        phone: None,
        username,
        password_hash,
        auth_epoch: 0,
        identity_established,
        name: normalize_optional(name),
        image_url: None,
        banned: false,
        ban_reason: None,
        deleted: provisioning_directory_id.is_some(),
        scim_provisioning: provisioning_directory_id.map(|directory_id| ScimProvisioning {
            directory_id: directory_id.to_owned(),
            phase: ScimProvisioningPhase::Pending,
        }),
        scim_instance_wide_directory_ids: Vec::new(),
        public_metadata: Map::new(),
        private_metadata: Map::new(),
        quota_seat: Some(seat),
        created_at: timestamp,
        updated_at: timestamp,
        last_sign_in_at: None,
        email_key: email
            .as_deref()
            .map(|value| user_email_key(instance_id, value)),
        username_key: None,
    };
    let mut user = user;
    user.username_key = user
        .username
        .as_deref()
        .map(|value| user_name_key(instance_id, value));

    match state.store.create_user(&user).await {
        Ok(created) => Ok(created),
        // The store releases the claim only after a confirmed create-only conflict;
        // retain it when a transport error leaves the write outcome ambiguous.
        Err(error) => Err(map_store_error(error)),
    }
}

/// Update a user while preserving the email and username uniqueness claims.
pub async fn update_user(
    state: &AppState,
    instance_id: &str,
    user_id: &str,
    changes: UserChanges,
) -> AppResult<User> {
    let Some(current) = state
        .store
        .get_user(user_id)
        .await
        .map_err(store_error)?
        .filter(|user| user.instance_id == instance_id && !user.deleted)
    else {
        return Err(not_found());
    };

    let next_email = match changes.email.as_ref() {
        Some(Some(value)) => {
            let normalized = normalize_email(value);
            if !is_valid_email(&normalized) {
                return Err(invalid_request("Please enter a valid email address."));
            }
            Some(normalized)
        }
        Some(None) => None,
        None => current.email.clone(),
    };
    let next_username = match changes.username.as_ref() {
        Some(Some(value)) => {
            let normalized = value.trim().to_owned();
            if normalized.is_empty()
                || normalized.len() > 128
                || normalized.chars().any(char::is_whitespace)
            {
                return Err(invalid_request(
                    "Usernames must be 1 to 128 characters and contain no whitespace.",
                ));
            }
            Some(normalized)
        }
        Some(None) => None,
        None => current.username.clone(),
    };
    let password_hash = match changes.password.as_ref() {
        Some(Some(password)) if !password.is_empty() => Some(Some(hash_password(password).await?)),
        Some(Some(_)) => {
            return Err(invalid_request(
                "Passwords must be between 8 and 1024 bytes.",
            ));
        }
        Some(None) => Some(None),
        None => None,
    };
    let new_email_key = next_email
        .as_deref()
        .map(|email| user_email_key(instance_id, email));
    let new_username_key = next_username
        .as_deref()
        .map(|username| user_name_key(instance_id, username));
    let update = changes.clone();
    let email_for_record = next_email.clone();
    let username_for_record = next_username.clone();
    let password_for_record = password_hash.clone();
    let changed_email = next_email != current.email;
    if changed_email {
        // Invalidate links sent to the old mailbox. Consumers also compare the
        // snapshot to the current address to cover concurrent issuance/re-key.
        for purpose in EMAIL_BOUND_TOKEN_PURPOSES {
            state
                .store
                .invalidate_verification_tokens(user_id, purpose)
                .await
                .map_err(store_error)?;
        }
    }
    let updated = state
        .store
        .rekey_user(
            user_id,
            instance_id,
            new_email_key,
            new_username_key,
            move |user| {
                if changed_email {
                    user.email = email_for_record.clone();
                    user.email_verified = false;
                }
                if update.username.is_some() {
                    user.username = username_for_record.clone();
                }
                if let Some(Some(name)) = update.name.as_ref() {
                    user.name = normalize_optional(Some(name));
                }
                if matches!(update.name.as_ref(), Some(None)) {
                    user.name = None;
                }
                if let Some(hash) = &password_for_record {
                    user.password_hash = hash.clone();
                    user.auth_epoch = user.auth_epoch.checked_add(1).unwrap_or(0);
                }
                if let Some(banned) = update.banned {
                    user.banned = banned;
                }
                if let Some(reason) = &update.ban_reason {
                    user.ban_reason = reason.clone();
                }
                if let Some(metadata) = &update.public_metadata {
                    user.public_metadata = metadata.clone();
                }
                if let Some(metadata) = &update.private_metadata {
                    user.private_metadata = metadata.clone();
                }
                user.updated_at = now();
            },
        )
        .await
        .map_err(map_store_error)?;
    if !updated {
        return Err(not_found());
    }
    if changed_email {
        for purpose in EMAIL_BOUND_TOKEN_PURPOSES {
            state
                .store
                .invalidate_verification_tokens(user_id, purpose)
                .await
                .map_err(store_error)?;
        }
    }
    state
        .store
        .get_user(user_id)
        .await
        .map_err(store_error)?
        .filter(|user| user.instance_id == instance_id)
        .ok_or_else(not_found)
}

/// Mark an identity deleted, invalidate credentials, remove memberships, and release uniqueness/quota claims.
pub async fn deprovision_user(
    state: &AppState,
    instance_id: &str,
    user_id: &str,
) -> AppResult<bool> {
    let Some(user) = state
        .store
        .get_user(user_id)
        .await
        .map_err(store_error)?
        .filter(|user| user.instance_id == instance_id)
    else {
        return Ok(false);
    };
    if !user.deleted {
        let changed = state
            .store
            .rekey_user(user_id, instance_id, None, None, |record| {
                if record.deleted {
                    return;
                }
                record.deleted = true;
                record.updated_at = now();
            })
            .await;
        match changed {
            Ok(true) => {}
            Ok(false) => {
                if !state
                    .store
                    .get_user(user_id)
                    .await
                    .map_err(store_error)?
                    .is_some_and(|record| record.instance_id == instance_id && record.deleted)
                {
                    return Ok(false);
                }
            }
            Err(error) => {
                if !state
                    .store
                    .get_user(user_id)
                    .await
                    .map_err(store_error)?
                    .is_some_and(|record| record.instance_id == instance_id && record.deleted)
                {
                    return Err(map_store_error(error));
                }
            }
        }
    }

    state
        .store
        .delete_user_authentication_artifacts(instance_id, user_id)
        .await
        .map_err(store_error)?;
    for passkey in state
        .store
        .list_passkeys(user_id)
        .await
        .map_err(store_error)?
    {
        if passkey.instance_id == instance_id {
            state
                .store
                .delete_passkey(&passkey.id)
                .await
                .map_err(store_error)?;
        }
    }
    for membership in state
        .store
        .list_memberships_by_user(user_id)
        .await
        .map_err(store_error)?
    {
        if membership.instance_id == instance_id {
            state
                .store
                .delete_membership(&membership.id)
                .await
                .map_err(store_error)?;
        }
    }
    state
        .store
        .release_quota_seat_for_user(&user)
        .await
        .map_err(store_error)?;
    Ok(true)
}

/// Recheck current user and instance-wide SCIM authority before issuing or
/// resolving any user authentication session.
pub async fn authentication_allowed(
    state: &AppState,
    instance_id: &str,
    user: &User,
) -> AppResult<bool> {
    if user.instance_id != instance_id || user.deleted || user.banned {
        return Ok(false);
    }
    state
        .store
        .scim_auth_eligible(user)
        .await
        .map_err(store_error)
}

/// Resolve the supplied raw browser token to a live user and session.
pub async fn resolve_session(
    state: &AppState,
    instance_id: &str,
    token: &str,
) -> AppResult<Option<(Session, User)>> {
    if token.is_empty() {
        return Ok(None);
    }
    let Some(session) = state
        .store
        .find_session_by_token(&tokens::sha256_hex(token))
        .await
        .map_err(store_error)?
    else {
        return Ok(None);
    };
    if session.instance_id != instance_id
        || session.status != "active"
        || session.expires_at <= now()
    {
        return Ok(None);
    }
    let Some(user) = state
        .store
        .get_user(&session.user_id)
        .await
        .map_err(store_error)?
        .filter(|user| user.instance_id == instance_id && !user.deleted && !user.banned)
    else {
        return Ok(None);
    };
    if session.auth_epoch != user.auth_epoch {
        return Ok(None);
    }
    if !authentication_allowed(state, instance_id, &user).await? {
        return Ok(None);
    }
    if session
        .last_active_at
        .is_none_or(|last| now().saturating_sub(last) > 300)
    {
        state
            .store
            .try_touch_session(&session.id, instance_id, now())
            .await
            .map_err(store_error)?;
    }
    Ok(Some((session, user)))
}

/// Create a 14-day session for an active same-instance user without signing them in.
pub async fn create_session(
    state: &AppState,
    instance_id: &str,
    user_id: &str,
    user_agent: Option<&str>,
    ip_address: Option<&str>,
) -> AppResult<(Session, String)> {
    let user = state
        .store
        .get_user(user_id)
        .await
        .map_err(store_error)?
        .filter(|user| user.instance_id == instance_id && !user.deleted && !user.banned)
        .ok_or_else(not_found)?;
    if !authentication_allowed(state, instance_id, &user).await? {
        return Err(not_found());
    }
    let token = tokens::random_token(48);
    let timestamp = now();
    let session = Session {
        id: format!("ses_{}", tokens::random_token(20)),
        instance_id: instance_id.to_owned(),
        user_id: user.id.clone(),
        auth_epoch: user.auth_epoch,
        token_key: tokens::sha256_hex(&token),
        status: "active".into(),
        created_at: timestamp,
        expires_at: timestamp + SESSION_LIFETIME_SECONDS,
        last_active_at: Some(timestamp),
        user_agent: user_agent.map(|value| value.chars().take(256).collect()),
        ip_address: ip_address.map(str::to_owned),
    };
    if let Err(error) = state.store.create_session(&session).await {
        let _ = state
            .store
            .try_revoke_session(&session.id, instance_id, Some(&user.id))
            .await;
        return Err(store_error(error));
    }
    Ok((session, token))
}

/// Issue a session and record the successful sign-in event.
pub async fn establish_session(
    state: &AppState,
    instance_id: &str,
    user: &User,
    method: &str,
    user_agent: Option<&str>,
    ip_address: Option<&str>,
) -> AppResult<(Session, String)> {
    establish_session_with_expected_email(
        state,
        instance_id,
        user,
        method,
        user_agent,
        ip_address,
        None,
        None,
    )
    .await
}

/// Establish a session only while the user's current email still matches a
/// mailbox-bound sign-in link.
pub async fn establish_session_for_email(
    state: &AppState,
    instance_id: &str,
    user: &User,
    method: &str,
    user_agent: Option<&str>,
    ip_address: Option<&str>,
    expected_email: &str,
) -> AppResult<(Session, String)> {
    establish_session_with_expected_email(
        state,
        instance_id,
        user,
        method,
        user_agent,
        ip_address,
        Some(expected_email),
        None,
    )
    .await
}

/// Establish a session only while the user's current authentication epoch
/// still matches a previously validated proof.
pub async fn establish_session_for_auth_epoch(
    state: &AppState,
    instance_id: &str,
    user: &User,
    method: &str,
    user_agent: Option<&str>,
    ip_address: Option<&str>,
    expected_auth_epoch: u64,
) -> AppResult<(Session, String)> {
    establish_session_with_expected_email(
        state,
        instance_id,
        user,
        method,
        user_agent,
        ip_address,
        None,
        Some(expected_auth_epoch),
    )
    .await
}

/// Complete a validated first factor, requiring a local confirmed TOTP factor
/// before issuing a browser session when one is enrolled.
pub async fn complete_first_factor(
    state: &AppState,
    instance: &Instance,
    user: &User,
    method: &str,
    user_agent: Option<&str>,
    email_snapshot: Option<&str>,
    continuation: Option<&str>,
) -> AppResult<FirstFactorOutcome> {
    let proof_auth_epoch = user.auth_epoch;
    let user = state
        .store
        .get_user(&user.id)
        .await
        .map_err(store_error)?
        .filter(|current| {
            current.instance_id == instance.id
                && !current.deleted
                && !current.banned
                && current.auth_epoch == proof_auth_epoch
        })
        .ok_or_else(not_found)?;
    if !authentication_allowed(state, &instance.id, &user).await? {
        return Err(not_found());
    }
    let email_snapshot = email_snapshot.map(normalize_email);
    if email_snapshot.as_deref().is_some_and(|snapshot| {
        user.email.as_deref().map(normalize_email).as_deref() != Some(snapshot)
    }) {
        return Err(not_found());
    }
    let fallback = format!("/ui/{}/account", instance.slug);
    let continuation =
        validated_authentication_next(&state.options.public_origin, &instance.slug, continuation)
            .unwrap_or(fallback);
    if crate::features::mfa::required_for_user(state, &user.id).await? {
        let token = crate::features::mfa::begin_pending(
            state,
            &instance.id,
            &user.id,
            &instance.slug,
            method,
            email_snapshot.as_deref(),
            &continuation,
            proof_auth_epoch,
        )
        .await?;
        return Ok(FirstFactorOutcome::MfaPending { token });
    }
    let (_, token) = establish_session_with_expected_email(
        state,
        &instance.id,
        &user,
        method,
        user_agent,
        None,
        email_snapshot.as_deref(),
        Some(proof_auth_epoch),
    )
    .await?;
    Ok(FirstFactorOutcome::Session { token })
}

/// Establish a session after consuming a pending MFA challenge and retain the
/// challenge's primary method and mailbox snapshot for audit and race checks.
pub async fn establish_pending_mfa_session(
    state: &AppState,
    instance: &Instance,
    pending: &crate::features::mfa::PendingAuthentication,
    factor_method: &str,
    user_agent: Option<&str>,
) -> AppResult<(Session, String)> {
    let expected_auth_epoch = pending.user_auth_epoch.ok_or_else(not_found)?;
    let user = state
        .store
        .get_user(&pending.user_id)
        .await
        .map_err(store_error)?
        .filter(|current| {
            current.instance_id == instance.id
                && !current.deleted
                && !current.banned
                && current.auth_epoch == expected_auth_epoch
        })
        .ok_or_else(not_found)?;
    let method = pending.authentication_method.as_deref().unwrap_or("mfa");
    let method = format!("{method}+{factor_method}");
    establish_session_with_expected_email(
        state,
        &instance.id,
        &user,
        &method,
        user_agent,
        None,
        pending.email_snapshot.as_deref(),
        Some(expected_auth_epoch),
    )
    .await
}

#[expect(
    clippy::too_many_arguments,
    reason = "Mailbox and auth-epoch expectations are distinct guards against stale-session issuance."
)]
async fn establish_session_with_expected_email(
    state: &AppState,
    instance_id: &str,
    user: &User,
    method: &str,
    user_agent: Option<&str>,
    ip_address: Option<&str>,
    expected_email: Option<&str>,
    expected_auth_epoch: Option<u64>,
) -> AppResult<(Session, String)> {
    if user.instance_id != instance_id || user.deleted || user.banned {
        return Err(not_found());
    }
    if let Some(expected_epoch) = expected_auth_epoch {
        let current = state
            .store
            .get_user(&user.id)
            .await
            .map_err(store_error)?
            .filter(|current| {
                current.instance_id == instance_id
                    && !current.deleted
                    && !current.banned
                    && current.auth_epoch == expected_epoch
            })
            .ok_or_else(not_found)?;
        if !authentication_allowed(state, instance_id, &current).await? {
            return Err(not_found());
        }
    }
    let (session, token) =
        create_session(state, instance_id, &user.id, user_agent, ip_address).await?;
    if expected_auth_epoch.is_some_and(|expected| session.auth_epoch != expected) {
        let _ = state
            .store
            .try_revoke_session(&session.id, instance_id, Some(&user.id))
            .await;
        return Err(not_found());
    }
    let timestamp = now();
    let expected_email = expected_email.map(str::to_owned);
    let updated = state
        .store
        .try_update_user(&user.id, instance_id, |record| {
            if record.deleted
                || record.banned
                || expected_auth_epoch.is_some_and(|expected| record.auth_epoch != expected)
                || expected_email.as_deref().is_some_and(|expected| {
                    record.email.as_deref().map(normalize_email).as_deref() != Some(expected)
                })
            {
                return false;
            }
            record.last_sign_in_at = Some(timestamp);
            record.updated_at = timestamp;
            true
        })
        .await;
    match updated {
        Ok(true) => {}
        Ok(false) => {
            let _ = state
                .store
                .try_revoke_session(&session.id, instance_id, Some(&user.id))
                .await;
            return Err(not_found());
        }
        Err(error) => {
            let _ = state
                .store
                .try_revoke_session(&session.id, instance_id, Some(&user.id))
                .await;
            return Err(store_error(error));
        }
    }
    if let Err(error) = audit(
        state,
        instance_id,
        Some(&user.id),
        "user.signed_in",
        json!({"method": method}),
    )
    .await
    {
        let _ = state
            .store
            .try_revoke_session(&session.id, instance_id, Some(&user.id))
            .await;
        return Err(error);
    }
    Ok((session, token))
}

/// Revoke all active sessions for one user, optionally preserving one current session.
pub async fn revoke_sessions(
    state: &AppState,
    instance_id: &str,
    user_id: &str,
    except_session_id: Option<&str>,
) -> AppResult<()> {
    let predicate = Predicate::equal("userId", user_id).map_err(store_error)?;
    let sessions: Vec<Session> = state
        .store
        .query_all("sessions", &[predicate], 100)
        .await
        .map_err(store_error)?;
    for session in sessions {
        if session.instance_id == instance_id
            && session.status == "active"
            && Some(session.id.as_str()) != except_session_id
        {
            state
                .store
                .try_revoke_session(&session.id, instance_id, Some(user_id))
                .await
                .map_err(store_error)?;
        }
    }
    Ok(())
}

/// Record an auditable identity lifecycle event.
pub async fn audit(
    state: &AppState,
    instance_id: &str,
    actor_id: Option<&str>,
    event_type: &str,
    data: Value,
) -> AppResult<()> {
    let data = data.as_object().cloned().unwrap_or_default();
    let event = AuditEvent {
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
    };
    state
        .store
        .create_audit_event(&event)
        .await
        .map_err(store_error)?;
    Ok(())
}

/// Claim a mailbox after a trusted proof. An unverified owner is tombstoned
/// with an owner-checked CAS before a fresh verified identity is created.
pub async fn claim_verified_mailbox(
    state: &AppState,
    instance_id: &str,
    email: &str,
    name: Option<&str>,
    method: &str,
) -> AppResult<User> {
    claim_verified_mailbox_with_proof(state, instance_id, email, method, None, None, None, name)
        .await
}

async fn read_verified_mailbox_user(
    state: &AppState,
    instance_id: &str,
    user_id: &str,
    email: &str,
    expected_auth_epoch: u64,
) -> AppResult<User> {
    state
        .store
        .get_user(user_id)
        .await
        .map_err(store_error)?
        .filter(|user| {
            user.id == user_id
                && user.instance_id == instance_id
                && !user.deleted
                && !user.banned
                && user.email_verified
                && user.identity_established
                && user.auth_epoch == expected_auth_epoch
                && user.email.as_deref().map(normalize_email).as_deref() == Some(email)
        })
        .ok_or_else(not_found)
}

/// Claim a mailbox using proof tied to its original account and, when
/// available, authentication epoch. New verified identities receive only
/// credentials and profile fields explicitly supplied after mailbox proof.
#[expect(
    clippy::too_many_arguments,
    reason = "The shared cross-feature contract keeps proof owner, epoch, and post-proof credentials explicit at the mailbox-claim boundary."
)]
pub async fn claim_verified_mailbox_with_proof(
    state: &AppState,
    instance_id: &str,
    email: &str,
    method: &str,
    proof_user_id: Option<&str>,
    proof_auth_epoch: Option<u64>,
    fresh_password_hash: Option<String>,
    fresh_name: Option<&str>,
) -> AppResult<User> {
    if !is_valid_email(email) {
        return Err(invalid_request(
            "The identity provider did not return a valid email.",
        ));
    }
    let normalized = normalize_email(email);
    let mut tombstoned_proof_owner = false;
    for _ in 0..8 {
        if let Some(user) = state
            .store
            .find_user_by_email(instance_id, &normalized)
            .await
            .map_err(store_error)?
        {
            if proof_user_id.is_some_and(|expected| user.id != expected) {
                return Err(not_found());
            }
            if user.deleted || user.banned || user.scim_provisioning.is_some() {
                return Err(AppError::new(
                    StatusCode::FORBIDDEN,
                    "account_unavailable",
                    "This account cannot sign in.",
                ));
            }
            if proof_auth_epoch.is_some_and(|expected| user.auth_epoch != expected) {
                return Err(not_found());
            }
            if user.identity_established || user.email_verified {
                let user_id = user.id.clone();
                let expected_email = normalized.clone();
                let expected_auth_epoch = user.auth_epoch;
                let updated = state
                    .store
                    .try_update_user(&user_id, instance_id, |record| {
                        if record.deleted
                            || record.banned
                            || record.id != user_id
                            || record.email.as_deref().map(normalize_email).as_deref()
                                != Some(expected_email.as_str())
                            || record.auth_epoch != expected_auth_epoch
                            || proof_auth_epoch
                                .is_some_and(|expected| record.auth_epoch != expected)
                            || proof_user_id.is_some_and(|expected| record.id != expected)
                        {
                            return false;
                        }
                        record.email_verified = true;
                        record.identity_established = true;
                        record.updated_at = now();
                        true
                    })
                    .await
                    .map_err(store_error)?;
                if !updated {
                    return Err(not_found());
                }
                let current = read_verified_mailbox_user(
                    state,
                    instance_id,
                    &user_id,
                    &normalized,
                    expected_auth_epoch,
                )
                .await?;
                enroll_by_domain(state, instance_id, &current).await?;
                let current = read_verified_mailbox_user(
                    state,
                    instance_id,
                    &user_id,
                    &normalized,
                    expected_auth_epoch,
                )
                .await?;
                if proof_user_id.is_some_and(|id| current.id != id) {
                    return Err(not_found());
                }
                return Ok(current);
            }
            let deprovisioned = state
                .store
                .deprovision_unverified_user_for_verified_claim_if_epoch(
                    instance_id,
                    &user.id,
                    &normalized,
                    proof_auth_epoch,
                )
                .await
                .map_err(store_error)?;
            if deprovisioned {
                tombstoned_proof_owner = true;
                match create_user_with_provisioning(
                    state,
                    instance_id,
                    Some(&normalized),
                    None,
                    None,
                    fresh_name,
                    None,
                    true,
                    true,
                    fresh_password_hash.clone(),
                )
                .await
                {
                    Ok(created) => {
                        audit(
                            state,
                            instance_id,
                            Some(&created.id),
                            "user.signed_up",
                            json!({"method": method}),
                        )
                        .await?;
                        enroll_by_domain(state, instance_id, &created).await?;
                        let current = read_verified_mailbox_user(
                            state,
                            instance_id,
                            &created.id,
                            &normalized,
                            created.auth_epoch,
                        )
                        .await?;
                        return Ok(current);
                    }
                    Err(error) if error.status == StatusCode::CONFLICT => continue,
                    Err(error) => return Err(error),
                }
            }
            continue;
        }
        if proof_user_id.is_some() && !tombstoned_proof_owner {
            return Err(not_found());
        }
        match create_user_with_provisioning(
            state,
            instance_id,
            Some(&normalized),
            None,
            None,
            fresh_name,
            None,
            true,
            true,
            fresh_password_hash.clone(),
        )
        .await
        {
            Ok(created) => {
                audit(
                    state,
                    instance_id,
                    Some(&created.id),
                    "user.signed_up",
                    json!({"method": method}),
                )
                .await?;
                enroll_by_domain(state, instance_id, &created).await?;
                let current = read_verified_mailbox_user(
                    state,
                    instance_id,
                    &created.id,
                    &normalized,
                    created.auth_epoch,
                )
                .await?;
                return Ok(current);
            }
            Err(error) if error.status == StatusCode::CONFLICT => continue,
            Err(error) => return Err(error),
        }
    }
    Err(store_error(StoreError::Contention))
}

/// Create or resolve a verified-email identity for a trusted federation callback.
pub async fn find_or_create_verified_user(
    state: &AppState,
    instance_id: &str,
    email: &str,
    name: Option<&str>,
) -> AppResult<User> {
    claim_verified_mailbox(state, instance_id, email, name, "federation").await
}

/// Create a short-lived one-use verification token and return the raw link token.
pub async fn issue_token(
    state: &AppState,
    instance_id: &str,
    user_id: Option<&str>,
    purpose: &str,
    ttl_seconds: i64,
) -> AppResult<String> {
    issue_token_internal(state, instance_id, user_id, purpose, ttl_seconds, None).await
}

pub async fn issue_authentication_token(
    state: &AppState,
    instance: &Instance,
    user_id: &str,
    purpose: &str,
    ttl_seconds: i64,
    next: Option<&str>,
) -> AppResult<String> {
    let next = validated_authentication_next(&state.options.public_origin, &instance.slug, next)
        .unwrap_or_else(|| format!("/ui/{}/account", instance.slug));
    issue_token_internal(
        state,
        &instance.id,
        Some(user_id),
        purpose,
        ttl_seconds,
        Some(next),
    )
    .await
}

async fn issue_token_internal(
    state: &AppState,
    instance_id: &str,
    user_id: Option<&str>,
    purpose: &str,
    ttl_seconds: i64,
    authentication_next: Option<String>,
) -> AppResult<String> {
    let token = tokens::random_token(40);
    let (email_snapshot, user_auth_epoch) = if EMAIL_BOUND_TOKEN_PURPOSES.contains(&purpose) {
        let Some(id) = user_id else {
            return Err(invalid_request("This token purpose requires a user."));
        };
        let user = state
            .store
            .get_user(id)
            .await
            .map_err(store_error)?
            .filter(|user| user.instance_id == instance_id && !user.deleted && !user.banned)
            .ok_or_else(not_found)?;
        let Some(email) = user.email else {
            return Err(invalid_request(
                "This token purpose requires a user email address.",
            ));
        };
        (
            Some(normalize_email(&email)),
            matches!(purpose, "magic_link" | "reset_password").then_some(user.auth_epoch),
        )
    } else {
        (None, None)
    };
    let record = VerificationToken {
        id: format!("vrf_{}", tokens::random_token(20)),
        instance_id: instance_id.to_owned(),
        user_id: user_id.map(str::to_owned),
        purpose: purpose.to_owned(),
        authentication_method: None,
        authentication_next,
        user_auth_epoch,
        token_key: tokens::sha256_hex(&token),
        email_snapshot,
        expires_at: now() + ttl_seconds,
        consumed: false,
        failed_attempts: 0,
        created_at: now(),
    };
    state
        .store
        .create_verification_token(&record)
        .await
        .map_err(store_error)?;
    Ok(token)
}

/// Consume a token only if it matches this instance, purpose, and TTL.
pub async fn consume_token(
    state: &AppState,
    instance_id: &str,
    token: &str,
    purpose: &str,
) -> AppResult<Option<VerificationToken>> {
    state
        .store
        .consume_verification_token(&tokens::sha256_hex(token), |record| {
            record.instance_id == instance_id
                && record.purpose == purpose
                && !record.consumed
                && record.expires_at > now()
        })
        .await
        .map_err(store_error)
}

async fn find_valid_token(
    state: &AppState,
    instance_id: &str,
    token: &str,
    purpose: &str,
) -> AppResult<Option<VerificationToken>> {
    state
        .store
        .find_verification_token_matching(&tokens::sha256_hex(token), |record| {
            record.instance_id == instance_id
                && record.purpose == purpose
                && !record.consumed
                && record.expires_at > now()
        })
        .await
        .map_err(store_error)
}

async fn token_is_valid(
    state: &AppState,
    instance_id: &str,
    token: &str,
    purpose: &str,
) -> AppResult<bool> {
    let record = find_valid_token(state, instance_id, token, purpose).await?;
    let Some(record) = record else {
        return Ok(false);
    };
    email_snapshot_matches_user(state, instance_id, &record).await
}

async fn email_snapshot_matches_user(
    state: &AppState,
    instance_id: &str,
    record: &VerificationToken,
) -> AppResult<bool> {
    let (Some(user_id), Some(snapshot)) =
        (record.user_id.as_deref(), record.email_snapshot.as_deref())
    else {
        return Ok(false);
    };
    let user = state.store.get_user(user_id).await.map_err(store_error)?;
    Ok(user.is_some_and(|user| {
        user.instance_id == instance_id
            && !user.deleted
            && !user.banned
            && user.email.as_deref().map(normalize_email).as_deref() == Some(snapshot)
            && (!matches!(record.purpose.as_str(), "magic_link" | "reset_password")
                || record.user_auth_epoch == Some(user.auth_epoch))
    }))
}

/// Validate a same-origin, tenant-local authentication continuation and
/// normalize absolute URLs to a relative path, preserving its query string.
pub(crate) fn validated_authentication_next(
    public_origin: &str,
    slug: &str,
    next: Option<&str>,
) -> Option<String> {
    let next = next?;
    if next.is_empty()
        || next.starts_with("//")
        || next.contains('\\')
        || next.chars().any(char::is_control)
    {
        return None;
    }
    let expected = reqwest::Url::parse(public_origin).ok()?;
    let next_url = reqwest::Url::parse(next)
        .or_else(|_| expected.join(next))
        .ok()?;
    if next_url.origin() != expected.origin()
        || !next_url.username().is_empty()
        || next_url.password().is_some()
        || next_url.fragment().is_some()
    {
        return None;
    }
    let hosted_ui = format!("/ui/{slug}");
    let hosted_ui_prefix = format!("{hosted_ui}/");
    let oauth_authorize = format!("/{slug}/oauth/authorize");
    let path = next_url.path();
    if path != hosted_ui && !path.starts_with(&hosted_ui_prefix) && path != oauth_authorize {
        return None;
    }
    let mut relative = path.to_owned();
    if let Some(query) = next_url.query() {
        relative.push('?');
        relative.push_str(query);
    }
    Some(relative)
}

fn not_found_page() -> Response {
    ui::page(
        "Not found",
        "<h1>Page not found</h1><p class=\"muted\">No ts-auth instance lives at this address.</p><p class=\"hint\"><a href=\"/\">Start over</a></p>",
        None,
        StatusCode::NOT_FOUND,
        None,
        None,
    )
}

async fn page_instance(state: &AppState, slug: &str) -> AppResult<Option<Instance>> {
    state
        .store
        .find_instance_by_slug(slug)
        .await
        .map_err(store_error)
}

fn html_error(message: &str, status: StatusCode, brand: Option<&Instance>) -> Response {
    let body = format!(
        "<h1>Request rejected</h1><div class=\"error\">{}</div>",
        ui::escape_html(message)
    );
    ui::page(
        "Request rejected",
        &body,
        None,
        status,
        brand.and_then(|instance| instance.theme_color.as_deref()),
        brand.and_then(|instance| instance.logo_url.as_deref()),
    )
}

fn redirect(path: &str) -> Response {
    Redirect::to(path).into_response()
}

fn check_email_page(
    slug: &str,
    brand: &Instance,
    email: &str,
    what: &str,
    minutes: u32,
) -> Response {
    let body = format!(
        "<h1>Check your email</h1><div class=\"success\">If an account exists for {}, a {} is on its way.</div><p class=\"muted\">The link expires in {} minutes and can only be used once.</p><p class=\"hint\"><a href=\"/ui/{}/sign-in\">Back to sign in</a></p>",
        ui::escape_html(email),
        ui::escape_html(what),
        minutes,
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

fn done_page(slug: &str, brand: &Instance, title: &str, message: &str) -> Response {
    let body = format!(
        "<h1>{}</h1><div class=\"success\">{}</div><p class=\"hint\"><a href=\"/ui/{}/sign-in\">Back to sign in</a></p>",
        ui::escape_html(title),
        ui::escape_html(message),
        ui::escape_html(slug)
    );
    ui::page(
        title,
        &body,
        None,
        StatusCode::OK,
        brand.theme_color.as_deref(),
        brand.logo_url.as_deref(),
    )
}

fn invalid_link_page(slug: &str, brand: &Instance) -> Response {
    let body = format!(
        "<h1>This link is no longer valid</h1><div class=\"error\">This link is invalid, expired, or already used.</div><p class=\"hint\"><a href=\"/ui/{}/sign-in\">Back to sign in</a> · <a href=\"/ui/{}/forgot-password\">Request a new one</a></p>",
        ui::escape_html(slug),
        ui::escape_html(slug)
    );
    ui::page(
        "Invalid link",
        &body,
        None,
        StatusCode::BAD_REQUEST,
        brand.theme_color.as_deref(),
        brand.logo_url.as_deref(),
    )
}

fn auth_cookie(headers: &HeaderMap) -> Option<&str> {
    let cookies = headers.get(header::COOKIE)?.to_str().ok()?;
    cookies.split(';').find_map(|pair| {
        let (name, value) = pair.trim().split_once('=')?;
        (name == ui::SESSION_COOKIE_NAME).then_some(value)
    })
}

fn html_page(title: &str, body: &str, brand: &Instance, status: StatusCode) -> Response {
    ui::page(
        title,
        body,
        None,
        status,
        brand.theme_color.as_deref(),
        brand.logo_url.as_deref(),
    )
}

#[derive(Deserialize, Default)]
struct NextQuery {
    next: Option<String>,
}

#[utoipa::path(get, path = "/ui/{slug}/sign-in", params(("slug" = String, Path)), responses((status = 200, description = "Sign-in form"), (status = 404, description = "Unknown instance")))]
async fn sign_in_page(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    Query(query): Query<NextQuery>,
) -> Response {
    let brand = match page_instance(&state, &slug).await {
        Ok(Some(value)) => value,
        Ok(None) => return not_found_page(),
        Err(error) => return error.into_response(),
    };
    let next_path =
        validated_authentication_next(&state.options.public_origin, &slug, query.next.as_deref())
            .unwrap_or_else(|| format!("/ui/{slug}/account"));
    let next = ui::escape_html(&next_path);
    let next_query: String = url::form_urlencoded::byte_serialize(next_path.as_bytes()).collect();
    let body = format!(
        r#"<h1>Sign in</h1><form method="post"><input type="hidden" name="next" value="{next}"><label for="si-email">Email</label><input id="si-email" name="email" type="email" required autofocus><label for="si-password">Password</label><input id="si-password" name="password" type="password" required><button type="submit">Sign in</button></form><form method="post" action="/ui/{slug}/magic-link"><input type="hidden" name="next" value="{next}"><label for="si-magic">Or email a magic link</label><input id="si-magic" name="email" type="email" required><button type="submit" class="secondary">Send magic link</button></form><p class="hint"><a href="/ui/{slug}/passkeys/assert?next={next_query}">Sign in with a passkey</a> · <a href="/ui/{slug}/passcode?next={next_query}">Sign in with an email code</a></p><p class="hint"><a href="/ui/{slug}/connections/list?next={next_query}">Sign in with SSO</a></p><p class="hint"><a href="/ui/{slug}/forgot-password">Forgot your password?</a> · <a href="/ui/{slug}/sign-up">Create an account</a></p>"#
    );
    html_page("Sign in", &body, &brand, StatusCode::OK)
}

#[utoipa::path(post, path = "/ui/{slug}/sign-in", params(("slug" = String, Path)), request_body = inline(HashMap<String, String>), responses((status = 303, description = "Signed in or MFA challenge started"), (status = 403, description = "Cross-origin request")))]
async fn sign_in(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Form(form): Form<HashMap<String, String>>,
) -> Response {
    if !require_same_origin(&headers, &state.options.public_origin) {
        return same_origin_error();
    }
    let brand = match page_instance(&state, &slug).await {
        Ok(Some(value)) => value,
        Ok(None) => return not_found_page(),
        Err(error) => return error.into_response(),
    };
    let email = form.get("email").map_or("", |value| value.trim());
    let password = form.get("password").map_or("", String::as_str);
    let requested = form.get("next").map(String::as_str);
    let user = match state
        .store
        .find_user_by_email(&brand.id, &normalize_email(email))
        .await
    {
        Ok(user) => user,
        Err(error) => return store_error(error).into_response(),
    };
    let password_matches = match verify_password(user.as_ref(), password).await {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    };
    if !password_matches {
        let body = format!(
            "<h1>Sign in</h1><div class=\"error\">Invalid email or password.</div><form method=\"post\"><input type=\"hidden\" name=\"next\" value=\"{}\"><label>Email</label><input name=\"email\" type=\"email\" value=\"{}\" required><label>Password</label><input name=\"password\" type=\"password\" required><button>Sign in</button></form>",
            ui::escape_html(requested.unwrap_or("")),
            ui::escape_html(email)
        );
        return html_page("Sign in", &body, &brand, StatusCode::OK);
    }
    let Some(user) = user else {
        return html_error(
            "Invalid email or password.",
            StatusCode::UNAUTHORIZED,
            Some(&brand),
        );
    };
    if user.banned {
        return html_error(
            "This account is suspended.",
            StatusCode::FORBIDDEN,
            Some(&brand),
        );
    }
    let next = validated_authentication_next(&state.options.public_origin, &slug, requested)
        .unwrap_or_else(|| format!("/ui/{slug}/account"));
    match complete_first_factor(
        &state,
        &brand,
        &user,
        "password",
        headers
            .get(header::USER_AGENT)
            .and_then(|value| value.to_str().ok()),
        None,
        Some(&next),
    )
    .await
    {
        Ok(FirstFactorOutcome::MfaPending { token }) => {
            redirect(&format!("/ui/{slug}/mfa/verify?token={token}"))
        }
        Ok(FirstFactorOutcome::Session { token }) => {
            let mut response = redirect(&next);
            ui::set_session_cookie(&mut response, &token);
            response
        }
        Err(error) => error.into_response(),
    }
}

#[utoipa::path(get, path = "/ui/{slug}/sign-up", params(("slug" = String, Path)), responses((status = 200, description = "Registration form"), (status = 404, description = "Unknown instance")))]
async fn sign_up_page(State(state): State<AppState>, Path(slug): Path<String>) -> Response {
    let brand = match page_instance(&state, &slug).await {
        Ok(Some(value)) => value,
        Ok(None) => return not_found_page(),
        Err(error) => return error.into_response(),
    };
    let body = format!(
        "<h1>Create your account</h1><form method=\"post\"><label>Email</label><input name=\"email\" type=\"email\" required autofocus><label>Password</label><input name=\"password\" type=\"password\" minlength=\"8\" required><label>Name <span class=\"muted\">(optional)</span></label><input name=\"name\"><button type=\"submit\">Create account</button></form><p class=\"hint\">Already registered? <a href=\"/ui/{slug}/sign-in\">Sign in</a></p>"
    );
    html_page("Create your account", &body, &brand, StatusCode::OK)
}

#[utoipa::path(post, path = "/ui/{slug}/sign-up", params(("slug" = String, Path)), request_body = inline(HashMap<String, String>), responses((status = 303, description = "Account created"), (status = 409, description = "Email already registered")))]
async fn sign_up(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Form(form): Form<HashMap<String, String>>,
) -> Response {
    if !require_same_origin(&headers, &state.options.public_origin) {
        return same_origin_error();
    }
    let brand = match page_instance(&state, &slug).await {
        Ok(Some(value)) => value,
        Ok(None) => return not_found_page(),
        Err(error) => return error.into_response(),
    };
    let email = form.get("email").map_or("", |value| value.trim());
    let password = form.get("password").map_or("", String::as_str);
    let name = form.get("name").map(String::as_str);
    if !is_valid_email(email) {
        return html_error(
            "Please enter a valid email address.",
            StatusCode::BAD_REQUEST,
            Some(&brand),
        );
    }
    if password.len() < 8 {
        return html_error(
            "Password must be at least 8 characters.",
            StatusCode::BAD_REQUEST,
            Some(&brand),
        );
    }
    let user =
        match create_unverified_signup_user(&state, &brand.id, Some(email), Some(password), name)
            .await
        {
            Ok(user) => user,
            Err(error) => return error.into_response(),
        };
    let (session, token) = match establish_session(
        &state,
        &brand.id,
        &user,
        "password",
        headers
            .get(header::USER_AGENT)
            .and_then(|value| value.to_str().ok()),
        None,
    )
    .await
    {
        Ok(value) => value,
        Err(error) => return rollback_registration(&state, &brand.id, &user.id, error).await,
    };
    let verification = match issue_token(
        &state,
        &brand.id,
        Some(&user.id),
        "verify_email",
        VERIFY_EMAIL_LIFETIME_SECONDS,
    )
    .await
    {
        Ok(value) => value,
        Err(error) => return rollback_registration(&state, &brand.id, &user.id, error).await,
    };
    let link = format!(
        "{}/ui/{slug}/verify-email?token={verification}",
        state.options.public_origin.trim_end_matches('/')
    );
    if let Some(address) = user.email.as_deref() {
        state.email.send(address, "Verify your email", &format!("Welcome to {}!\n\nConfirm your email address by opening:\n\n{link}\n\nThis link expires in 24 hours.", brand.name)).await;
    }
    let mut response = redirect(&format!("/ui/{slug}/account"));
    ui::set_session_cookie(&mut response, &token);
    let _ = session;
    response
}

async fn rollback_registration(
    state: &AppState,
    instance_id: &str,
    user_id: &str,
    cause: AppError,
) -> Response {
    if let Err(cleanup_error) = deprovision_user(state, instance_id, user_id).await {
        tracing::error!(%instance_id, %user_id, ?cleanup_error, "failed to compensate incomplete registration");
    }
    cause.into_response()
}

async fn enroll_by_domain(state: &AppState, instance_id: &str, user: &User) -> AppResult<()> {
    if user.instance_id != instance_id || user.deleted || user.banned || !user.email_verified {
        return Ok(());
    }
    let Some(current) = state
        .store
        .get_user(&user.id)
        .await
        .map_err(store_error)?
        .filter(|current| {
            current.instance_id == instance_id
                && !current.deleted
                && !current.banned
                && current.email_verified
                && current.email == user.email
        })
    else {
        return Ok(());
    };
    let user = &current;
    let Some(email) = user.email.as_deref() else {
        return Ok(());
    };
    let Some(domain) = email.rsplit_once('@').map(|(_, domain)| domain) else {
        return Ok(());
    };
    for org_domain in state
        .store
        .list_org_domains(instance_id)
        .await
        .map_err(store_error)?
    {
        if org_domain.domain.eq_ignore_ascii_case(domain) {
            let membership = auth_contracts::models::OrgMembership {
                id: format!("mem_{}", tokens::random_token(20)),
                instance_id: instance_id.to_owned(),
                org_id: org_domain.org_id.clone(),
                user_id: user.id.clone(),
                member_key: format!("{}:{}", org_domain.org_id, user.id),
                role: "member".into(),
                created_at: now(),
            };
            match state.store.create_membership(&membership).await {
                Ok(_) => {
                    audit(
                        state,
                        instance_id,
                        Some(&user.id),
                        "organization.enrolled",
                        json!({"organization_id":org_domain.org_id,"domain":org_domain.domain}),
                    )
                    .await?
                }
                Err(error) if error.is_conflict() => {}
                Err(error) => return Err(map_store_error(error)),
            }
        }
    }
    Ok(())
}

#[utoipa::path(post, path = "/ui/{slug}/magic-link", params(("slug" = String, Path)), request_body = inline(HashMap<String, String>), responses((status = 200, description = "Generic email sent confirmation")))]
async fn request_magic_link(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Form(form): Form<HashMap<String, String>>,
) -> Response {
    if !require_same_origin(&headers, &state.options.public_origin) {
        return same_origin_error();
    }
    let brand = match page_instance(&state, &slug).await {
        Ok(Some(value)) => value,
        Ok(None) => return not_found_page(),
        Err(error) => return error.into_response(),
    };
    let email = form.get("email").map_or("", |value| value.trim());
    if is_valid_email(email) {
        match state
            .store
            .find_user_by_email(&brand.id, &normalize_email(email))
            .await
        {
            Ok(Some(user)) if !user.deleted && !user.banned => {
                match authentication_allowed(&state, &brand.id, &user).await {
                    Ok(true) => {
                        let token = match issue_authentication_token(
                            &state,
                            &brand,
                            &user.id,
                            "magic_link",
                            MAGIC_LINK_LIFETIME_SECONDS,
                            form.get("next").map(String::as_str),
                        )
                        .await
                        {
                            Ok(value) => value,
                            Err(error) => return error.into_response(),
                        };
                        let link = format!(
                            "{}/ui/{slug}/magic-link/consume?token={token}",
                            state.options.public_origin.trim_end_matches('/')
                        );
                        if let Some(address) = user.email.as_deref() {
                            state.email.send(address, &format!("Your {} sign-in link", brand.name), &format!("Sign in to {} by opening:\n\n{link}\n\nThis link expires in 10 minutes and can only be used once.", brand.name)).await;
                        }
                    }
                    Ok(false) => {}
                    Err(error) => return error.into_response(),
                }
            }
            Ok(_) => {}
            Err(error) => return store_error(error).into_response(),
        }
    }
    check_email_page(&slug, &brand, email, "sign-in link", 10)
}

#[derive(Deserialize, Default)]
struct TokenQuery {
    token: Option<String>,
}

#[utoipa::path(get, path = "/ui/{slug}/magic-link/consume", params(("slug" = String, Path), ("token" = Option<String>, Query)), responses((status = 303, description = "Signed in"), (status = 400, description = "Invalid or expired link")))]
async fn consume_magic_link(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    Query(query): Query<TokenQuery>,
    headers: HeaderMap,
) -> Response {
    let brand = match page_instance(&state, &slug).await {
        Ok(Some(value)) => value,
        Ok(None) => return not_found_page(),
        Err(error) => return error.into_response(),
    };
    let Some(token) = query.token.as_deref() else {
        return invalid_link_page(&slug, &brand);
    };
    let record = match consume_token(&state, &brand.id, token, "magic_link").await {
        Ok(Some(value)) => value,
        Ok(None) => return invalid_link_page(&slug, &brand),
        Err(error) => return error.into_response(),
    };
    let (Some(user_id), Some(email_snapshot)) =
        (record.user_id.as_deref(), record.email_snapshot.as_deref())
    else {
        return invalid_link_page(&slug, &brand);
    };
    if !match email_snapshot_matches_user(&state, &brand.id, &record).await {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    } {
        return invalid_link_page(&slug, &brand);
    }
    let Some(proof_auth_epoch) = record.user_auth_epoch else {
        return invalid_link_page(&slug, &brand);
    };
    let user = match claim_verified_mailbox_with_proof(
        &state,
        &brand.id,
        email_snapshot,
        "magic_link",
        Some(user_id),
        Some(proof_auth_epoch),
        None,
        None,
    )
    .await
    {
        Ok(user) => user,
        Err(error) => return error.into_response(),
    };
    match complete_first_factor(
        &state,
        &brand,
        &user,
        "magic_link",
        headers
            .get(header::USER_AGENT)
            .and_then(|value| value.to_str().ok()),
        Some(email_snapshot),
        record.authentication_next.as_deref(),
    )
    .await
    {
        Ok(FirstFactorOutcome::MfaPending { token }) => {
            redirect(&format!("/ui/{slug}/mfa/verify?token={token}"))
        }
        Ok(FirstFactorOutcome::Session { token }) => {
            let next = record
                .authentication_next
                .as_deref()
                .and_then(|next| {
                    validated_authentication_next(&state.options.public_origin, &slug, Some(next))
                })
                .unwrap_or_else(|| format!("/ui/{slug}/account"));
            let mut response = redirect(&next);
            ui::set_session_cookie(&mut response, &token);
            response
        }
        Err(error) => error.into_response(),
    }
}

#[utoipa::path(get, path = "/ui/{slug}/forgot-password", params(("slug" = String, Path)), responses((status = 200, description = "Password recovery form")))]
async fn forgot_password_page(State(state): State<AppState>, Path(slug): Path<String>) -> Response {
    let brand = match page_instance(&state, &slug).await {
        Ok(Some(value)) => value,
        Ok(None) => return not_found_page(),
        Err(error) => return error.into_response(),
    };
    let body = format!(
        "<h1>Reset your password</h1><p class=\"muted\">Enter your email and we'll send a reset link.</p><form method=\"post\"><label>Email</label><input name=\"email\" type=\"email\" required autofocus><button>Send reset link</button></form><p class=\"hint\"><a href=\"/ui/{slug}/sign-in\">Back to sign in</a></p>"
    );
    html_page("Reset your password", &body, &brand, StatusCode::OK)
}

#[utoipa::path(post, path = "/ui/{slug}/forgot-password", params(("slug" = String, Path)), request_body = inline(HashMap<String, String>), responses((status = 200, description = "Generic email sent confirmation")))]
async fn forgot_password(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Form(form): Form<HashMap<String, String>>,
) -> Response {
    if !require_same_origin(&headers, &state.options.public_origin) {
        return same_origin_error();
    }
    let brand = match page_instance(&state, &slug).await {
        Ok(Some(value)) => value,
        Ok(None) => return not_found_page(),
        Err(error) => return error.into_response(),
    };
    let email = form.get("email").map_or("", |value| value.trim());
    if is_valid_email(email) {
        match state
            .store
            .find_user_by_email(&brand.id, &normalize_email(email))
            .await
        {
            Ok(Some(user)) if !user.deleted && !user.banned => {
                let token = match issue_token(
                    &state,
                    &brand.id,
                    Some(&user.id),
                    "reset_password",
                    RESET_PASSWORD_LIFETIME_SECONDS,
                )
                .await
                {
                    Ok(value) => value,
                    Err(error) => return error.into_response(),
                };
                let link = format!(
                    "{}/ui/{slug}/reset-password?token={token}",
                    state.options.public_origin.trim_end_matches('/')
                );
                if let Some(address) = user.email.as_deref() {
                    state.email.send(address, "Reset your password", &format!("Reset your {} password by opening:\n\n{link}\n\nThis link expires in 30 minutes and can only be used once. If you didn't request this, ignore this email.", brand.name)).await;
                }
            }
            Ok(_) => {}
            Err(error) => return store_error(error).into_response(),
        }
    }
    check_email_page(&slug, &brand, email, "password reset link", 30)
}

#[utoipa::path(get, path = "/ui/{slug}/reset-password", params(("slug" = String, Path), ("token" = Option<String>, Query)), responses((status = 200, description = "Password reset form"), (status = 400, description = "Invalid link")))]
async fn reset_password_page(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    Query(query): Query<TokenQuery>,
) -> Response {
    let brand = match page_instance(&state, &slug).await {
        Ok(Some(value)) => value,
        Ok(None) => return not_found_page(),
        Err(error) => return error.into_response(),
    };
    let Some(token) = query.token.as_deref() else {
        return invalid_link_page(&slug, &brand);
    };
    match token_is_valid(&state, &brand.id, token, "reset_password").await {
        Ok(true) => {
            let body = format!(
                "<h1>Choose a new password</h1><form method=\"post\"><input type=\"hidden\" name=\"token\" value=\"{}\"><label>New password</label><input name=\"password\" type=\"password\" minlength=\"8\" required autofocus><button>Reset password</button></form>",
                ui::escape_html(token)
            );
            html_page("Reset your password", &body, &brand, StatusCode::OK)
        }
        Ok(false) => invalid_link_page(&slug, &brand),
        Err(error) => error.into_response(),
    }
}

#[utoipa::path(post, path = "/ui/{slug}/reset-password", params(("slug" = String, Path)), request_body = inline(HashMap<String, String>), responses((status = 200, description = "Password reset"), (status = 400, description = "Invalid link or weak password")))]
async fn reset_password(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Form(form): Form<HashMap<String, String>>,
) -> Response {
    if !require_same_origin(&headers, &state.options.public_origin) {
        return same_origin_error();
    }
    let brand = match page_instance(&state, &slug).await {
        Ok(Some(value)) => value,
        Ok(None) => return not_found_page(),
        Err(error) => return error.into_response(),
    };
    let token = form.get("token").map_or("", String::as_str);
    let password = form.get("password").map_or("", String::as_str);
    if password.len() < 8 || password.len() > 1024 {
        return html_error(
            "Password must be between 8 and 1024 bytes.",
            StatusCode::BAD_REQUEST,
            Some(&brand),
        );
    }
    let Some(preflight) = (match find_valid_token(&state, &brand.id, token, "reset_password").await
    {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    }) else {
        return invalid_link_page(&slug, &brand);
    };
    let (Some(user_id), Some(email_snapshot), Some(expected_auth_epoch)) = (
        preflight.user_id.as_deref(),
        preflight.email_snapshot.as_deref(),
        preflight.user_auth_epoch,
    ) else {
        return invalid_link_page(&slug, &brand);
    };
    if !match email_snapshot_matches_user(&state, &brand.id, &preflight).await {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    } {
        return invalid_link_page(&slug, &brand);
    }
    let password_hash = match hash_password(password).await {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    };
    let Some(record) = (match consume_token(&state, &brand.id, token, "reset_password").await {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    }) else {
        return invalid_link_page(&slug, &brand);
    };
    if record.id != preflight.id
        || record.user_id.as_deref() != Some(user_id)
        || record.email_snapshot.as_deref() != Some(email_snapshot)
        || record.user_auth_epoch != Some(expected_auth_epoch)
        || !match email_snapshot_matches_user(&state, &brand.id, &record).await {
            Ok(value) => value,
            Err(error) => return error.into_response(),
        }
    {
        return invalid_link_page(&slug, &brand);
    }
    let source_user = match state.store.get_user(user_id).await {
        Ok(Some(user))
            if user.instance_id == brand.id
                && !user.deleted
                && !user.banned
                && user.auth_epoch == expected_auth_epoch
                && user.email.as_deref().map(normalize_email).as_deref()
                    == Some(email_snapshot) =>
        {
            user
        }
        Ok(_) => return invalid_link_page(&slug, &brand),
        Err(error) => return store_error(error).into_response(),
    };
    if source_user.identity_established || source_user.email_verified {
        let Some(new_epoch) = expected_auth_epoch.checked_add(1) else {
            return AppError::unavailable().into_response();
        };
        let updated = match state
            .store
            .try_update_user(user_id, &brand.id, |user| {
                if user.deleted
                    || user.banned
                    || user.auth_epoch != expected_auth_epoch
                    || user.email.as_deref().map(normalize_email).as_deref() != Some(email_snapshot)
                {
                    return false;
                }
                user.password_hash = Some(password_hash.clone());
                user.email_verified = true;
                user.identity_established = true;
                user.auth_epoch = new_epoch;
                user.updated_at = now();
                true
            })
            .await
        {
            Ok(value) => value,
            Err(error) => return store_error(error).into_response(),
        };
        if !updated {
            return invalid_link_page(&slug, &brand);
        }
        if let Err(error) = revoke_sessions(&state, &brand.id, user_id, None).await {
            return error.into_response();
        }
        if let Err(error) = audit(
            &state,
            &brand.id,
            Some(user_id),
            "user.password_reset",
            json!({}),
        )
        .await
        {
            return error.into_response();
        }
    } else {
        let replacement = match claim_verified_mailbox_with_proof(
            &state,
            &brand.id,
            email_snapshot,
            "password_reset",
            Some(user_id),
            Some(expected_auth_epoch),
            Some(password_hash.clone()),
            None,
        )
        .await
        {
            Ok(user) if user.id != user_id => user,
            Ok(_) => return invalid_link_page(&slug, &brand),
            Err(error) => return error.into_response(),
        };
        if let Err(error) = revoke_sessions(&state, &brand.id, user_id, None).await {
            return error.into_response();
        }
        if let Err(error) = audit(
            &state,
            &brand.id,
            Some(&replacement.id),
            "user.password_reset",
            json!({"replaced_unverified_identity": true}),
        )
        .await
        {
            return error.into_response();
        }
    }
    done_page(
        &slug,
        &brand,
        "Password updated",
        "Your password has been updated. You can now sign in.",
    )
}

#[utoipa::path(get, path = "/ui/{slug}/verify-email", params(("slug" = String, Path), ("token" = Option<String>, Query)), responses((status = 200, description = "Trusted mailbox completion form"), (status = 400, description = "Invalid link")))]
async fn verify_email(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    Query(query): Query<TokenQuery>,
) -> Response {
    let brand = match page_instance(&state, &slug).await {
        Ok(Some(value)) => value,
        Ok(None) => return not_found_page(),
        Err(error) => return error.into_response(),
    };
    let Some(token) = query.token.as_deref() else {
        return invalid_link_page(&slug, &brand);
    };
    match token_is_valid(&state, &brand.id, token, "verify_email").await {
        Ok(true) => {
            let body = format!(
                "<h1>Finish creating your account</h1><p class=\"muted\">Confirm your mailbox, then choose a password to secure this account.</p><form method=\"post\"><input type=\"hidden\" name=\"token\" value=\"{}\"><label>Password</label><input name=\"password\" type=\"password\" minlength=\"8\" maxlength=\"1024\" required autofocus><label>Name <span class=\"muted\">(optional)</span></label><input name=\"name\"><button>Verify email and continue</button></form>",
                ui::escape_html(token)
            );
            html_page("Verify your email", &body, &brand, StatusCode::OK)
        }
        Ok(false) => invalid_link_page(&slug, &brand),
        Err(error) => error.into_response(),
    }
}

#[utoipa::path(post, path = "/ui/{slug}/verify-email", params(("slug" = String, Path)), request_body = inline(HashMap<String, String>), responses((status = 200, description = "Mailbox verified and account secured"), (status = 400, description = "Invalid link or weak password")))]
async fn complete_email_verification(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Form(form): Form<HashMap<String, String>>,
) -> Response {
    if !require_same_origin(&headers, &state.options.public_origin) {
        return same_origin_error();
    }
    let brand = match page_instance(&state, &slug).await {
        Ok(Some(value)) => value,
        Ok(None) => return not_found_page(),
        Err(error) => return error.into_response(),
    };
    let token = form.get("token").map_or("", String::as_str);
    let password = form.get("password").map_or("", String::as_str);
    let name = form
        .get("name")
        .map(|value| value.trim())
        .filter(|value| !value.is_empty());
    let Some(preflight) = (match find_valid_token(&state, &brand.id, token, "verify_email").await {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    }) else {
        return invalid_link_page(&slug, &brand);
    };
    if !match email_snapshot_matches_user(&state, &brand.id, &preflight).await {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    } {
        return invalid_link_page(&slug, &brand);
    }
    let Some(proof_user_id) = preflight.user_id.as_deref() else {
        return invalid_link_page(&slug, &brand);
    };
    let source_user = match state.store.get_user(proof_user_id).await {
        Ok(Some(user)) if user.instance_id == brand.id && !user.deleted && !user.banned => user,
        Ok(_) => return invalid_link_page(&slug, &brand),
        Err(error) => return store_error(error).into_response(),
    };
    let fresh_password_hash = if source_user.identity_established || source_user.email_verified {
        None
    } else {
        if password.len() < 8 || password.len() > 1024 {
            return html_error(
                "Password must be between 8 and 1024 bytes.",
                StatusCode::BAD_REQUEST,
                Some(&brand),
            );
        }
        match hash_password(password).await {
            Ok(value) => Some(value),
            Err(error) => return error.into_response(),
        }
    };
    let Some(record) = (match consume_token(&state, &brand.id, token, "verify_email").await {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    }) else {
        return invalid_link_page(&slug, &brand);
    };
    if record.id != preflight.id || record.user_id != preflight.user_id {
        return invalid_link_page(&slug, &brand);
    }
    let (Some(user_id), Some(email_snapshot)) =
        (record.user_id.as_deref(), record.email_snapshot.as_deref())
    else {
        return invalid_link_page(&slug, &brand);
    };
    if !match email_snapshot_matches_user(&state, &brand.id, &record).await {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    } {
        return invalid_link_page(&slug, &brand);
    }
    let user = match claim_verified_mailbox_with_proof(
        &state,
        &brand.id,
        email_snapshot,
        "email_verification",
        Some(user_id),
        None,
        fresh_password_hash,
        name,
    )
    .await
    {
        Ok(user) => user,
        Err(error) => return error.into_response(),
    };
    if let Err(error) = audit(
        &state,
        &brand.id,
        Some(&user.id),
        "user.email_verified",
        json!({}),
    )
    .await
    {
        return error.into_response();
    }
    if user.id == user_id {
        return done_page(
            &slug,
            &brand,
            "Email confirmed",
            "This mailbox belongs to an established account. Sign in with your existing credentials or reset your password.",
        );
    }
    done_page(
        &slug,
        &brand,
        "Account secured",
        "Your email is verified. Sign in with the password you just chose.",
    )
}

#[utoipa::path(get, path = "/ui/{slug}/account", params(("slug" = String, Path)), responses((status = 200, description = "Account page"), (status = 303, description = "Sign-in redirect")))]
async fn account_page(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
) -> Response {
    let brand = match page_instance(&state, &slug).await {
        Ok(Some(value)) => value,
        Ok(None) => return not_found_page(),
        Err(error) => return error.into_response(),
    };
    let Some(token) = auth_cookie(&headers) else {
        return redirect(&format!("/ui/{slug}/sign-in?next=%2Fui%2F{slug}%2Faccount"));
    };
    let (session, user) = match resolve_session(&state, &brand.id, token).await {
        Ok(Some(value)) => value,
        Ok(None) => return redirect(&format!("/ui/{slug}/sign-in?next=%2Fui%2F{slug}%2Faccount")),
        Err(error) => return error.into_response(),
    };
    let email_status = if user.email_verified {
        "✓ email verified"
    } else {
        "email not verified"
    };
    let body = format!(
        "<h1>Your account</h1><div class=\"list\"><div class=\"row\"><span>{}</span><span class=\"muted\">{email_status}</span></div><div class=\"row\"><span>{}</span><span class=\"muted\">email</span></div></div><form method=\"post\" action=\"/ui/{slug}/account/password\"><label>Current password</label><input name=\"current\" type=\"password\" required><label>New password</label><input name=\"new\" type=\"password\" minlength=\"8\" required><button>Change password</button></form><div class=\"list\"><div class=\"row\"><a href=\"/ui/{slug}/account/sessions\">Active sessions</a><span class=\"muted\">devices</span></div><div class=\"row\"><a href=\"/ui/{slug}/mfa/enroll\">Multi-factor authentication</a></div><div class=\"row\"><a href=\"/ui/{slug}/passkeys/manage\">Passkeys</a></div></div><form method=\"post\" action=\"/ui/{slug}/account/sessions/revoke-all\"><button type=\"submit\" class=\"secondary\">Sign out everywhere</button></form><form method=\"post\" action=\"/ui/{slug}/sign-out\"><button type=\"submit\" class=\"secondary\">Sign out</button></form>",
        ui::escape_html(
            user.name
                .as_deref()
                .or(user.email.as_deref())
                .unwrap_or("user")
        ),
        ui::escape_html(user.email.as_deref().unwrap_or(""))
    );
    let _ = session;
    html_page("Your account", &body, &brand, StatusCode::OK)
}

#[utoipa::path(post, path = "/ui/{slug}/account/password", params(("slug" = String, Path)), request_body = inline(HashMap<String, String>), responses((status = 303, description = "Password changed"), (status = 401, description = "Current password is invalid")))]
async fn change_password(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Form(form): Form<HashMap<String, String>>,
) -> Response {
    if !require_same_origin(&headers, &state.options.public_origin) {
        return same_origin_error();
    }
    let brand = match page_instance(&state, &slug).await {
        Ok(Some(value)) => value,
        Ok(None) => return not_found_page(),
        Err(error) => return error.into_response(),
    };
    let Some(token) = auth_cookie(&headers) else {
        return redirect(&format!("/ui/{slug}/sign-in"));
    };
    let (session, user) = match resolve_session(&state, &brand.id, token).await {
        Ok(Some(value)) => value,
        Ok(None) => return redirect(&format!("/ui/{slug}/sign-in")),
        Err(error) => return error.into_response(),
    };
    let current = form.get("current").map_or("", String::as_str);
    let current_matches = match verify_password(Some(&user), current).await {
        Ok(value) => value,
        Err(error) => return error.into_response(),
    };
    if !current_matches {
        return html_error(
            "Current password is incorrect.",
            StatusCode::UNAUTHORIZED,
            Some(&brand),
        );
    }
    let password = form.get("new").map_or("", String::as_str);
    let hash = match hash_password(password).await {
        Ok(value) => value,
        Err(error) if error.code == "authentication_busy" => return error.into_response(),
        Err(_) => {
            return html_error(
                "New password must be at least 8 characters.",
                StatusCode::BAD_REQUEST,
                Some(&brand),
            );
        }
    };
    let expected_epoch = user.auth_epoch;
    let Some(new_epoch) = expected_epoch.checked_add(1) else {
        return AppError::unavailable().into_response();
    };
    let updated = match state
        .store
        .try_update_user(&user.id, &brand.id, |record| {
            if record.deleted || record.banned || record.auth_epoch != expected_epoch {
                return false;
            }
            record.password_hash = Some(hash.clone());
            record.auth_epoch = new_epoch;
            record.updated_at = now();
            true
        })
        .await
    {
        Ok(value) => value,
        Err(error) => return store_error(error).into_response(),
    };
    if !updated {
        return redirect(&format!("/ui/{slug}/sign-in"));
    }
    match state
        .store
        .try_update_session_auth_epoch(&session.id, &brand.id, &user.id, expected_epoch, new_epoch)
        .await
    {
        Ok(true) => {}
        Ok(false) => return redirect(&format!("/ui/{slug}/sign-in")),
        Err(error) => return store_error(error).into_response(),
    }
    if let Err(error) = revoke_sessions(&state, &brand.id, &user.id, Some(&session.id)).await {
        return error.into_response();
    }
    if let Err(error) = audit(
        &state,
        &brand.id,
        Some(&user.id),
        "user.password_changed",
        json!({}),
    )
    .await
    {
        return error.into_response();
    }
    redirect(&format!("/ui/{slug}/account"))
}

#[utoipa::path(post, path = "/ui/{slug}/sign-out", params(("slug" = String, Path)), responses((status = 303, description = "Session revoked and cookie cleared")))]
async fn sign_out(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
) -> Response {
    if !require_same_origin(&headers, &state.options.public_origin) {
        return same_origin_error();
    }
    if let Ok(Some(instance)) = page_instance(&state, &slug).await
        && let Some(token) = auth_cookie(&headers)
        && let Ok(Some((session, user))) = resolve_session(&state, &instance.id, token).await
    {
        if let Err(error) = state
            .store
            .try_revoke_session(&session.id, &instance.id, Some(&user.id))
            .await
        {
            return store_error(error).into_response();
        }
        let _ = audit(
            &state,
            &instance.id,
            Some(&user.id),
            "user.signed_out",
            json!({}),
        )
        .await;
    }
    let mut response = redirect(&format!("/ui/{slug}/sign-in"));
    ui::delete_session_cookie(&mut response);
    response
}

#[utoipa::path(get, path = "/ui/{slug}/account/sessions", params(("slug" = String, Path)), responses((status = 200, description = "Active sessions page"), (status = 303, description = "Sign-in redirect")))]
async fn sessions_page(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
) -> Response {
    let brand = match page_instance(&state, &slug).await {
        Ok(Some(value)) => value,
        Ok(None) => return not_found_page(),
        Err(error) => return error.into_response(),
    };
    let Some(token) = auth_cookie(&headers) else {
        return redirect(&format!("/ui/{slug}/sign-in"));
    };
    let (current, user) = match resolve_session(&state, &brand.id, token).await {
        Ok(Some(value)) => value,
        Ok(None) => return redirect(&format!("/ui/{slug}/sign-in")),
        Err(error) => return error.into_response(),
    };
    let predicate = match Predicate::equal("userId", &user.id) {
        Ok(value) => value,
        Err(error) => return store_error(error).into_response(),
    };
    let sessions: Vec<Session> = match state.store.query_all("sessions", &[predicate], 100).await {
        Ok(value) => value,
        Err(error) => return store_error(error).into_response(),
    };
    let mut rows = String::new();
    for session in sessions.into_iter().filter(|session| {
        session.instance_id == brand.id && session.status == "active" && session.expires_at > now()
    }) {
        let device = ui::escape_html(
            session
                .user_agent
                .as_deref()
                .filter(|agent| !agent.trim().is_empty())
                .unwrap_or("Unknown device"),
        );
        let marker = if session.id == current.id {
            " · <strong>this device</strong>"
        } else {
            ""
        };
        rows.push_str(&format!("<div class=\"row\"><div><div>{device}</div><div class=\"muted\">created {} · last active {}{marker}</div></div><form class=\"inline\" method=\"post\" action=\"/ui/{slug}/account/sessions/{}/revoke\"><button type=\"submit\" class=\"secondary\">Revoke</button></form></div>", stamp(session.created_at), stamp(session.last_active_at.unwrap_or(session.created_at)), ui::escape_html(&session.id)));
    }
    let body = format!(
        "<h1>Active sessions</h1><div class=\"list\">{rows}</div><p class=\"hint\"><a href=\"/ui/{slug}/account\">Back to account</a></p>"
    );
    html_page("Active sessions", &body, &brand, StatusCode::OK)
}

fn stamp(timestamp: i64) -> String {
    format!("{timestamp} UTC")
}

#[utoipa::path(post, path = "/ui/{slug}/account/sessions/{id}/revoke", params(("slug" = String, Path), ("id" = String, Path)), responses((status = 303, description = "Session revoked")))]
async fn revoke_session(
    State(state): State<AppState>,
    Path((slug, id)): Path<(String, String)>,
    headers: HeaderMap,
) -> Response {
    if !require_same_origin(&headers, &state.options.public_origin) {
        return same_origin_error();
    }
    let brand = match page_instance(&state, &slug).await {
        Ok(Some(value)) => value,
        Ok(None) => return not_found_page(),
        Err(error) => return error.into_response(),
    };
    let Some(token) = auth_cookie(&headers) else {
        return redirect(&format!("/ui/{slug}/sign-in"));
    };
    let (current, user) = match resolve_session(&state, &brand.id, token).await {
        Ok(Some(value)) => value,
        Ok(None) => return redirect(&format!("/ui/{slug}/sign-in")),
        Err(error) => return error.into_response(),
    };
    let target = match state.store.get_session(&id).await {
        Ok(value) => value,
        Err(error) => return store_error(error).into_response(),
    };
    let mut clear_cookie = false;
    if let Some(target) = target.filter(|session| {
        session.instance_id == brand.id && session.user_id == user.id && session.status == "active"
    }) {
        match state
            .store
            .try_revoke_session(&target.id, &brand.id, Some(&user.id))
            .await
        {
            Ok(_) => {}
            Err(error) => return store_error(error).into_response(),
        }
        clear_cookie = target.id == current.id;
    }
    let mut response = redirect(&format!("/ui/{slug}/account/sessions"));
    if clear_cookie {
        ui::delete_session_cookie(&mut response);
    }
    response
}

#[utoipa::path(post, path = "/ui/{slug}/account/sessions/revoke-all", params(("slug" = String, Path)), responses((status = 303, description = "All sessions revoked and cookie cleared")))]
async fn revoke_all_sessions(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
) -> Response {
    if !require_same_origin(&headers, &state.options.public_origin) {
        return same_origin_error();
    }
    let brand = match page_instance(&state, &slug).await {
        Ok(Some(value)) => value,
        Ok(None) => return not_found_page(),
        Err(error) => return error.into_response(),
    };
    let Some(token) = auth_cookie(&headers) else {
        return redirect(&format!("/ui/{slug}/sign-in"));
    };
    let (_, user) = match resolve_session(&state, &brand.id, token).await {
        Ok(Some(value)) => value,
        Ok(None) => return redirect(&format!("/ui/{slug}/sign-in")),
        Err(error) => return error.into_response(),
    };
    if let Err(error) = revoke_sessions(&state, &brand.id, &user.id, None).await {
        return error.into_response();
    }
    if let Err(error) = audit(
        &state,
        &brand.id,
        Some(&user.id),
        "user.signed_out",
        json!({}),
    )
    .await
    {
        return error.into_response();
    }
    let mut response = redirect(&format!("/ui/{slug}/sign-in"));
    ui::delete_session_cookie(&mut response);
    response
}

#[cfg(test)]
#[allow(dead_code)]
#[path = "../../../tests/support/terrabase_http.rs"]
mod identity_password_gate_fixture;

#[cfg(test)]
mod password_gate_tests {
    use super::*;
    use auth_contracts::models::Instance;
    use axum::{
        Form,
        extract::{Path, State},
        http::{HeaderValue, header},
    };

    #[tokio::test]
    async fn mailbox_password_kdf_backpressure_keeps_proof_and_seed_retryable() {
        let storage = identity_password_gate_fixture::TerraBaseHttpFixture::start().await;
        let public_origin = "http://localhost:8080".to_owned();
        let options = crate::AuthOptions {
            listen: "http://127.0.0.1:0".into(),
            public_origin: public_origin.clone(),
            terrascale_api_url: storage.origin.clone(),
            terrascale_api_key: identity_password_gate_fixture::TEST_TERRASCALE_API_KEY.into(),
            terrascale_project_id: None,
            terrascale_tenant_id: None,
            database: "ts-auth-password-gate-test".into(),
            master_key: vec![7; 32],
            audit_signing_key: Some(vec![8; 32]),
            audit_verification_keys: Vec::new(),
            operator_key: "test-only-operator-key".into(),
            metrics_key: Some("test-only-metrics-key".into()),
            rate_limit_per_window: 1_000,
            stripe_secret_key: None,
            stripe_webhook_secret: None,
            stripe_api_url: "http://127.0.0.1:1".into(),
            price_pro: None,
            price_enterprise: None,
            smtp_host: None,
            smtp_port: 587,
            smtp_username: None,
            smtp_password: None,
            smtp_from_address: None,
        };
        let state =
            crate::app::build_state_with_mode(options, true).expect("test app state should build");
        let instance = Instance {
            id: "ins_password-gate".into(),
            slug: "password-gate".into(),
            name: "Password gate test".into(),
            logo_url: None,
            theme_color: None,
            stripe_customer_id: None,
            created_at: now(),
        };
        state
            .store
            .create_instance(&instance)
            .await
            .expect("instance should persist");
        let seed = create_unverified_signup_user(
            &state,
            &instance.id,
            Some("kdf-gate@example.test"),
            Some("attacker-password"),
            Some("Untrusted Seed"),
        )
        .await
        .expect("unverified seed should be created");
        let token = issue_token(&state, &instance.id, Some(&seed.id), "verify_email", 600)
            .await
            .expect("verification proof should be issued");

        let permits = password_kdf_semaphore()
            .acquire_many_owned(PASSWORD_KDF_CONCURRENCY as u32)
            .await
            .expect("test should hold all password KDF permits");
        let mut headers = HeaderMap::new();
        headers.insert(
            header::ORIGIN,
            HeaderValue::from_str(&public_origin).expect("public origin should be a header"),
        );
        let form = || {
            Form(HashMap::from([
                ("token".to_owned(), token.clone()),
                ("password".to_owned(), "new-mailbox-password".to_owned()),
                ("name".to_owned(), "Mailbox Owner".to_owned()),
            ]))
        };
        let busy = complete_email_verification(
            State(state.clone()),
            Path(instance.slug.clone()),
            headers.clone(),
            form(),
        )
        .await;
        assert_eq!(busy.status(), StatusCode::SERVICE_UNAVAILABLE);
        assert!(
            state
                .store
                .get_user(&seed.id)
                .await
                .expect("seed should remain readable")
                .is_some_and(|user| !user.deleted),
            "KDF backpressure must not retire the unverified seed"
        );
        assert!(
            find_valid_token(&state, &instance.id, &token, "verify_email")
                .await
                .expect("proof lookup should succeed")
                .is_some(),
            "KDF backpressure must leave the proof unconsumed"
        );

        drop(permits);
        let completed = complete_email_verification(
            State(state.clone()),
            Path(instance.slug.clone()),
            headers,
            form(),
        )
        .await;
        assert_eq!(completed.status(), StatusCode::OK);
        let old = state
            .store
            .get_user(&seed.id)
            .await
            .expect("seed lookup should succeed")
            .expect("tombstone should remain");
        assert!(old.deleted);
        let fresh = state
            .store
            .find_user_by_email(&instance.id, "kdf-gate@example.test")
            .await
            .expect("mailbox lookup should succeed")
            .expect("proof should create a verified identity");
        assert_ne!(fresh.id, seed.id);
        assert!(fresh.email_verified && fresh.identity_established);
        assert!(
            verify_password(Some(&fresh), "new-mailbox-password")
                .await
                .expect("fresh password should verify")
        );
    }
}
