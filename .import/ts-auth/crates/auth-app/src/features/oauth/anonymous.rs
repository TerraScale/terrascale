//! Consumer guest accounts and mailbox-proven, in-place account conversion.
//! Client opt-in and authority-backed admission limits are mandatory. Upgrade
//! requests never merge with, replace or disclose another mailbox's owner.

use super::rate_limit::allow_attempt;
use super::*;
use crate::{
    app::storage_error,
    features::{mfa, users},
};
use auth_contracts::models::{Instance, User};

const COLLECTION: &str = "anonymous_upgrades";
const CODE_TTL: i64 = 600;

#[derive(Deserialize, ToSchema)]
#[allow(dead_code)]
struct AnonymousRequest {
    client_id: String,
    client_secret: Option<String>,
}

#[derive(Deserialize, ToSchema)]
#[serde(deny_unknown_fields)]
struct UpgradeRequest {
    email: String,
}

#[derive(Serialize, ToSchema)]
struct UpgradeResponse {
    verification_id: String,
    expires_in: i64,
}

#[derive(Deserialize, ToSchema)]
#[serde(deny_unknown_fields)]
struct CompleteUpgrade {
    verification_id: String,
    code: String,
    /// Omit for a passwordless account; verified email sign-in remains available.
    password: Option<String>,
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Upgrade {
    id: String,
    instance_id: String,
    user_id: String,
    client_id: String,
    auth_epoch: u64,
    email: String,
    code_digest: String,
    expires_at: i64,
    attempts: u32,
    consumed: bool,
}

#[derive(OpenApi)]
#[openapi(paths(sign_in, request_upgrade, complete_upgrade),
    components(schemas(AnonymousRequest, UpgradeRequest, UpgradeResponse, CompleteUpgrade)),
    tags((name = "Guest accounts", description = "Opt-in consumer guest sessions and verified conversion preserving user IDs")))]
struct AnonymousApiDoc;

pub(super) fn openapi() -> utoipa::openapi::OpenApi {
    AnonymousApiDoc::openapi()
}

pub(super) fn router() -> Router<AppState> {
    Router::new()
        .route("/{slug}/oauth/anonymous", post(sign_in))
        .route("/{slug}/oauth/anonymous/upgrade", post(request_upgrade))
        .route(
            "/{slug}/oauth/anonymous/upgrade/complete",
            post(complete_upgrade),
        )
}

fn invalid_proof() -> AppError {
    AppError::new(
        StatusCode::BAD_REQUEST,
        "invalid_verification",
        "The verification code is invalid, expired, or already used.",
    )
}

async fn instance(state: &AppState, slug: &str) -> AppResult<Instance> {
    state
        .store
        .find_instance_by_slug(slug)
        .await
        .map_err(|e| storage_error(&e))?
        .ok_or_else(|| AppError::new(StatusCode::NOT_FOUND, "not_found", "No such instance."))
}

fn proof_digest(state: &AppState, id: &str, code: &str) -> String {
    base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(tokens::hmac_sha256(
        &state.options.master_key,
        format!("guest-upgrade:{id}:{code}").as_bytes(),
    ))
}

async fn guest(
    state: &AppState,
    instance: &Instance,
    headers: &HeaderMap,
) -> AppResult<(User, OAuthClient)> {
    let denied = || {
        AppError::new(
            StatusCode::UNAUTHORIZED,
            "invalid_token",
            "An active guest access token is required.",
        )
    };
    let raw = bearer_token(headers).ok_or_else(denied)?;
    let claims = verify_access_token(state, instance, raw)
        .await?
        .ok_or_else(denied)?;
    let user_id = claims
        .get("sub")
        .and_then(Value::as_str)
        .ok_or_else(denied)?;
    let client_id = claims
        .get("aud")
        .and_then(Value::as_str)
        .ok_or_else(denied)?;
    let epoch = claims
        .get("auth_epoch")
        .and_then(Value::as_u64)
        .ok_or_else(denied)?;
    let user = state
        .store
        .get_user(user_id)
        .await
        .map_err(|e| storage_error(&e))?
        .filter(|u| {
            u.instance_id == instance.id
                && u.is_anonymous
                && !u.deleted
                && !u.banned
                && u.auth_epoch == epoch
        })
        .ok_or_else(denied)?;
    if !users::authentication_allowed(state, &instance.id, &user).await?
        || mfa::required_for_user(state, &user.id).await?
    {
        return Err(denied());
    }
    // Refreshed access tokens have no browser-session binding. Original access
    // tokens that do carry one must honor its revocation.
    if let Some(sid) = claims.get("sid").and_then(Value::as_str) {
        let valid = state
            .store
            .get_session(sid)
            .await
            .map_err(|e| storage_error(&e))?
            .is_some_and(|s| {
                s.instance_id == instance.id
                    && s.user_id == user.id
                    && s.auth_epoch == epoch
                    && s.status == "active"
                    && s.expires_at > now()
            });
        if !valid {
            return Err(denied());
        }
    }
    let client = state
        .store
        .find_oauth_client(&instance.id, client_id)
        .await
        .map_err(|e| storage_error(&e))?
        .ok_or_else(denied)?;
    if !client.allow_anonymous {
        return Err(denied());
    }
    Ok((user, client))
}

#[utoipa::path(post, path = "/{slug}/oauth/anonymous", params(("slug" = String, Path)),
    request_body(content = AnonymousRequest, content_type = "application/x-www-form-urlencoded"),
    responses((status = 200, description = "Guest access, refresh and ID tokens; is_anonymous=true", body = OAuthTokenResponse), (status = 403, description = "Client has not enabled anonymous sign-in"), (status = 429, description = "Client admission limit exceeded")), tag = "Guest accounts")]
async fn sign_in(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Form(form): Form<HashMap<String, String>>,
) -> AppResult<Response> {
    let instance = instance(&state, &slug).await?;
    let client = match authenticate_client(&state.store, &instance.id, &headers, &form).await {
        Ok(client) => client,
        Err(error) => return Ok(client_authentication_error_response(error)),
    };
    if !client.allow_anonymous {
        return Ok(token_error(
            StatusCode::FORBIDDEN,
            "unauthorized_client",
            "anonymous sign-in is disabled for this client",
            false,
        ));
    }
    if !allow_attempt(&state, &instance.id, &client.client_id, "guest-sign-in", 30).await? {
        let mut response = token_error(
            StatusCode::TOO_MANY_REQUESTS,
            "rate_limited",
            "too many guest sign-ins; retry in one minute",
            false,
        );
        response
            .headers_mut()
            .insert(header::RETRY_AFTER, HeaderValue::from_static("60"));
        return Ok(response);
    }
    let user = users::create_anonymous_user(&state, &instance.id).await?;
    let (session, _) = users::establish_session(
        &state,
        &instance.id,
        &user,
        "anonymous",
        headers
            .get(header::USER_AGENT)
            .and_then(|v| v.to_str().ok()),
        None,
    )
    .await?;
    let scopes = vec!["openid".into(), "profile".into(), "email".into()];
    Ok(no_store(
        issue_user_tokens(
            &state,
            &instance,
            &client,
            &user,
            Some(&session),
            &scopes,
            None,
        )
        .await?,
    ))
}

#[utoipa::path(post, path = "/{slug}/oauth/anonymous/upgrade", params(("slug" = String, Path)), security(("OAuthAccessBearer" = [])), request_body = UpgradeRequest,
    responses((status = 200, description = "Verification code sent to the proposed email address", body = UpgradeResponse), (status = 401, description = "Active guest bearer required"), (status = 429, description = "Upgrade request limit exceeded")), tag = "Guest accounts")]
async fn request_upgrade(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Json(body): Json<UpgradeRequest>,
) -> AppResult<Response> {
    let instance = instance(&state, &slug).await?;
    let (user, client) = guest(&state, &instance, &headers).await?;
    let email = users::normalize_email(&body.email);
    if !users::is_valid_email(&email) {
        return Err(AppError::new(
            StatusCode::BAD_REQUEST,
            "invalid_email",
            "Enter a valid email address.",
        ));
    }
    if !allow_attempt(&state, &instance.id, &user.id, "guest-upgrade", 5).await?
        || !allow_attempt(
            &state,
            &instance.id,
            &tokens::sha256_hex(&email),
            "guest-mailbox",
            5,
        )
        .await?
    {
        return Err(AppError::new(
            StatusCode::TOO_MANY_REQUESTS,
            "rate_limited",
            "Wait one minute before requesting another code.",
        ));
    }
    let code = tokens::random_digits(8).ok_or_else(AppError::internal)?;
    let id = format!("gup_{}", tokens::random_token(32));
    let record = Upgrade {
        id: id.clone(),
        instance_id: instance.id.clone(),
        user_id: user.id.clone(),
        client_id: client.client_id,
        auth_epoch: user.auth_epoch,
        email: email.clone(),
        code_digest: proof_digest(&state, &id, &code),
        expires_at: now() + CODE_TTL,
        attempts: 0,
        consumed: false,
    };
    state
        .store
        .create(COLLECTION, &id, &record)
        .await
        .map_err(|e| storage_error(&e))?;
    // Do not query mailbox ownership before sending: the response reveals no
    // account existence and cannot be used for mailbox enumeration.
    state.email.send(&email, "Verify your email", &format!("Your {} verification code is {code}. It expires in 10 minutes. Enter it only in the app where you requested it. If you did not request this, ignore this email.", instance.name)).await;
    audit(
        &state,
        &instance.id,
        Some(&user.id),
        "user.guest_upgrade_requested",
        json!({}),
    )
    .await?;
    Ok(no_store(
        Json(UpgradeResponse {
            verification_id: id,
            expires_in: CODE_TTL,
        })
        .into_response(),
    ))
}

#[utoipa::path(post, path = "/{slug}/oauth/anonymous/upgrade/complete", params(("slug" = String, Path)), security(("OAuthAccessBearer" = [])), request_body = CompleteUpgrade,
    responses((status = 200, description = "Account converted with the same subject; old credentials invalidated", body = OAuthTokenResponse), (status = 400, description = "Invalid code or password"), (status = 409, description = "Email is already claimed or guest changed")), tag = "Guest accounts")]
async fn complete_upgrade(
    State(state): State<AppState>,
    Path(slug): Path<String>,
    headers: HeaderMap,
    Json(body): Json<CompleteUpgrade>,
) -> AppResult<Response> {
    let instance = instance(&state, &slug).await?;
    let (user, client) = guest(&state, &instance, &headers).await?;
    if body
        .password
        .as_ref()
        .is_some_and(|password| !(8..=1024).contains(&password.len()))
    {
        return Err(AppError::new(
            StatusCode::BAD_REQUEST,
            "invalid_password",
            "Passwords must be between 8 and 1024 bytes.",
        ));
    }
    if body.verification_id.len() > 128
        || body.code.len() != 8
        || !body.code.bytes().all(|b| b.is_ascii_digit())
    {
        return Err(invalid_proof());
    }
    let mut proved = None;
    for _ in 0..8 {
        let Some((mut record, version)) = state
            .store
            .get_versioned::<Upgrade>(COLLECTION, &body.verification_id)
            .await
            .map_err(|e| storage_error(&e))?
        else {
            return Err(invalid_proof());
        };
        if record.instance_id != instance.id
            || record.user_id != user.id
            || record.client_id != client.client_id
            || record.auth_epoch != user.auth_epoch
            || record.consumed
            || record.attempts >= 5
            || record.expires_at <= now()
        {
            return Err(invalid_proof());
        }
        let valid = tokens::fixed_time_equals(
            &record.code_digest,
            &proof_digest(&state, &record.id, &body.code),
        );
        record.attempts += 1;
        record.consumed = valid;
        if !state
            .store
            .compare_and_put(COLLECTION, &record.id, &record, &version)
            .await
            .map_err(|e| storage_error(&e))?
        {
            continue;
        }
        if !valid {
            return Err(invalid_proof());
        }
        proved = Some(record);
        break;
    }
    let proof = proved.ok_or_else(AppError::unavailable)?;
    let password_hash = match body.password {
        Some(password) => Some(users::hash_password(&password).await?),
        None => None,
    };
    let changed = || {
        AppError::new(
            StatusCode::CONFLICT,
            "upgrade_conflict",
            "The email cannot be attached or this guest already changed. Sign in to an existing account separately.",
        )
    };
    let user = state
        .store
        .upgrade_anonymous_user(
            &user.id,
            &instance.id,
            user.auth_epoch,
            &proof.email,
            password_hash,
            now(),
        )
        .await
        .map_err(|e| {
            if e.is_conflict() || e.is_precondition() {
                changed()
            } else {
                storage_error(&e)
            }
        })?
        .ok_or_else(changed)?;
    // auth_epoch is advanced in the same write as the mailbox claim. Even if
    // cleanup fails, old access/refresh tokens and sessions cannot authenticate.
    users::revoke_sessions(&state, &instance.id, &user.id, None).await?;
    audit(
        &state,
        &instance.id,
        Some(&user.id),
        "user.guest_upgraded",
        json!({}),
    )
    .await?;
    let (session, _) = users::establish_session_for_auth_epoch(
        &state,
        &instance.id,
        &user,
        "guest_email_upgrade",
        None,
        None,
        user.auth_epoch,
    )
    .await?;
    let scopes = vec!["openid".into(), "profile".into(), "email".into()];
    Ok(no_store(
        issue_user_tokens(
            &state,
            &instance,
            &client,
            &user,
            Some(&session),
            &scopes,
            None,
        )
        .await?,
    ))
}
