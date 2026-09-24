//! Shared `sk_` bearer context, route-family scopes, and fixed-window rate policy.

use std::{
    collections::HashMap,
    sync::{Mutex, OnceLock},
    time::{SystemTime, UNIX_EPOCH},
};

use auth_contracts::{
    AppError, AppResult,
    models::{ApiKey, Instance},
};
use auth_security::tokens::sha256_hex;
use axum::{
    Json,
    extract::{Request, State},
    http::{HeaderMap, StatusCode, header},
    middleware::Next,
    response::{IntoResponse, Response},
};
use serde::Serialize;

use crate::AppState;

const WINDOW_SECONDS: u64 = 300;

/// Resolved instance and API-key identity for audit attribution.
#[derive(Debug, Clone)]
pub struct InstanceContext {
    pub instance: Instance,
    pub api_key_id: String,
    pub scopes: Vec<String>,
}

#[derive(Serialize)]
struct ErrorBody<'a> {
    error: ErrorDetail<'a>,
}

#[derive(Serialize)]
struct ErrorDetail<'a> {
    code: &'a str,
    message: &'a str,
}

#[derive(Debug, Clone, PartialEq, Eq, Hash)]
struct BucketKey {
    api_key_id: String,
    window: u64,
}

#[derive(Default)]
struct RateLimiter {
    counts: Mutex<HashMap<BucketKey, u32>>,
}

impl RateLimiter {
    fn allow(&self, api_key_id: &str, limit: u32, now: u64) -> RateDecision {
        let window = now / WINDOW_SECONDS;
        let retry_after = (window + 1) * WINDOW_SECONDS - now;
        // No lock is held over an await; this short critical section keeps bursts atomic.
        let mut counts = self
            .counts
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        counts.retain(|key, _| key.window >= window.saturating_sub(2));
        let used = counts
            .entry(BucketKey {
                api_key_id: api_key_id.to_owned(),
                window,
            })
            .and_modify(|count| *count = count.saturating_add(1))
            .or_insert(1);
        let remaining = limit.saturating_sub(*used);
        RateDecision {
            allowed: *used <= limit,
            remaining,
            retry_after,
        }
    }

    #[cfg(test)]
    fn bucket_count(&self) -> usize {
        self.counts
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .len()
    }
}

#[derive(Debug, Clone, Copy)]
struct RateDecision {
    allowed: bool,
    remaining: u32,
    retry_after: u64,
}

fn limiter() -> &'static RateLimiter {
    static LIMITER: OnceLock<RateLimiter> = OnceLock::new();
    LIMITER.get_or_init(RateLimiter::default)
}

/// Global Axum middleware for Management API bearer keys.
///
/// Install once around the composed app router with `from_fn_with_state`. Invalid
/// credentials pass through so the endpoint returns the established 401 envelope.
/// A valid key is charged once per request after its route-family scope is checked.
pub async fn management_gate(
    State(state): State<AppState>,
    mut request: Request,
    next: Next,
) -> Response {
    let Some(secret) = bearer_secret(request.headers()) else {
        return next.run(request).await;
    };
    if !secret.trim_start().starts_with("sk_") {
        return next.run(request).await;
    }

    let secret = secret.trim();
    let api_key = match find_api_key(&state, secret).await {
        Ok(Some(key)) if !key.revoked => key,
        Ok(_) => return next.run(request).await,
        Err(error) => return error.into_response(),
    };

    let instance = match state
        .store
        .get::<Instance>("instances", &api_key.instance_id)
        .await
    {
        Ok(Some(instance)) => instance,
        Ok(None) => return next.run(request).await,
        Err(_) => return AppError::internal().into_response(),
    };

    if !scope_allows(&api_key.scopes, request.uri().path()) {
        return api_error(
            StatusCode::FORBIDDEN,
            "insufficient_scope",
            "The API key's scopes do not allow this route.",
        );
    }

    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    let decision = limiter().allow(&api_key.id, state.options.rate_limit_per_window, now);
    if !decision.allowed {
        let mut response = api_error(
            StatusCode::TOO_MANY_REQUESTS,
            "rate_limited",
            &format!(
                "Management API rate limit exceeded; retry after {} seconds.",
                decision.retry_after
            ),
        );
        response.headers_mut().insert(
            header::RETRY_AFTER,
            decision
                .retry_after
                .to_string()
                .parse()
                .expect("numeric Retry-After is a valid header value"),
        );
        response.headers_mut().insert(
            "x-ratelimit-remaining",
            decision
                .remaining
                .to_string()
                .parse()
                .expect("numeric remaining count is a valid header value"),
        );
        return response;
    }

    // Preserve the resolved context for middleware/extractor consumers while route
    // handlers can use `resolve_instance` when they only have headers and a slug.
    request.extensions_mut().insert(InstanceContext {
        instance,
        api_key_id: api_key.id,
        scopes: api_key.scopes,
    });
    next.run(request).await
}

/// Resolves the authenticated instance for a route and rejects keys for other slugs.
pub async fn resolve_instance(
    state: &AppState,
    headers: &HeaderMap,
    slug: &str,
) -> AppResult<InstanceContext> {
    let secret = bearer_secret(headers).ok_or_else(unauthorized)?;
    let secret = secret.trim();
    if !secret.starts_with("sk_") {
        return Err(unauthorized());
    }

    let key = find_api_key(state, secret)
        .await?
        .filter(|key| !key.revoked)
        .ok_or_else(unauthorized)?;
    let instance = state
        .store
        .get::<Instance>("instances", &key.instance_id)
        .await
        .map_err(|_| AppError::internal())?
        .filter(|instance| instance.slug == slug)
        .ok_or_else(unauthorized)?;
    Ok(InstanceContext {
        instance,
        api_key_id: key.id,
        scopes: key.scopes,
    })
}

/// Requires a valid management API key that belongs to `slug`.
///
/// This is the stable helper for feature handlers that need an authenticated,
/// instance-bound management context. Route-family scope and rate-limit checks
/// are applied by [`management_gate`] at the composed application boundary.
pub async fn require(
    state: &AppState,
    headers: &HeaderMap,
    slug: &str,
) -> AppResult<InstanceContext> {
    resolve_instance(state, headers, slug).await
}

async fn find_api_key(state: &AppState, secret: &str) -> AppResult<Option<ApiKey>> {
    state
        .store
        .query_one::<ApiKey>("api_keys", "secretKey", &sha256_hex(secret))
        .await
        .map_err(|_| AppError::internal())
}

fn bearer_secret(headers: &HeaderMap) -> Option<&str> {
    let authorization = headers.get(header::AUTHORIZATION)?.to_str().ok()?;
    let (scheme, secret) = authorization.split_at(authorization.get(..7)?.len());
    if !scheme.eq_ignore_ascii_case("Bearer ") {
        return None;
    }
    Some(secret)
}

/// Mirrors the C# gate: a `/v1/{slug}/{family}` route needs that family or `*`.
/// Paths outside the versioned management surface are unrestricted by scopes.
pub fn scope_allows(scopes: &[String], path: &str) -> bool {
    let mut segments = path.split('/').filter(|segment| !segment.is_empty());
    if segments.next() != Some("v1") {
        return true;
    }
    let Some(_slug) = segments.next() else {
        return true;
    };
    let Some(family) = segments.next() else {
        return true;
    };
    scopes.iter().any(|scope| scope == "*" || scope == family)
}

fn unauthorized() -> AppError {
    AppError::new(
        StatusCode::UNAUTHORIZED,
        "unauthorized",
        "Missing or invalid credentials.",
    )
}

fn api_error(status: StatusCode, code: &'static str, message: &str) -> Response {
    (
        status,
        Json(ErrorBody {
            error: ErrorDetail { code, message },
        }),
    )
        .into_response()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn scope_checks_only_the_route_family() {
        let users = vec!["users".to_owned()];
        assert!(scope_allows(&users, "/v1/acme/users"));
        assert!(scope_allows(&users, "/v1/acme/users/user-1/sessions"));
        assert!(!scope_allows(&users, "/v1/acme/webhooks"));
        assert!(scope_allows(&users, "/ui/acme/sign-in"));
        assert!(scope_allows(&users, "/v1/instances"));
        assert!(scope_allows(&["*".to_owned()], "/v1/acme/webhooks"));
    }

    #[test]
    fn fixed_window_limit_resets_and_retry_after_is_positive_at_boundaries() {
        let limiter = RateLimiter::default();
        assert!(limiter.allow("key", 2, 298).allowed);
        assert!(limiter.allow("key", 2, 299).allowed);
        let rejected = limiter.allow("key", 2, 299);
        assert!(!rejected.allowed);
        assert_eq!(rejected.remaining, 0);
        assert_eq!(rejected.retry_after, 1);
        assert!(limiter.allow("key", 2, 300).allowed);
        assert_eq!(limiter.allow("key", 2, 300).remaining, 0);
        assert_eq!(limiter.allow("key", 2, 300).retry_after, 300);
    }

    #[test]
    fn fixed_window_limit_is_atomic_for_concurrent_requests() {
        let limiter = std::sync::Arc::new(RateLimiter::default());
        let allowed = (0..100)
            .map(|_| {
                let limiter = limiter.clone();
                std::thread::spawn(move || limiter.allow("key", 7, 10).allowed)
            })
            .map(|thread| thread.join().expect("rate limiter worker completed"))
            .filter(|allowed| *allowed)
            .count();
        assert_eq!(allowed, 7);
    }

    #[test]
    fn stale_windows_are_pruned() {
        let limiter = RateLimiter::default();
        let _ = limiter.allow("old", 3, 0);
        let _ = limiter.allow("new", 3, WINDOW_SECONDS * 4);
        assert_eq!(limiter.bucket_count(), 1);
    }

    #[test]
    fn bearer_scheme_is_case_insensitive_and_key_prefix_is_exact() {
        let headers = HeaderMap::from_iter([(
            header::AUTHORIZATION,
            "bEaReR sk_id_secret"
                .parse()
                .expect("static header is valid"),
        )]);
        assert_eq!(bearer_secret(&headers), Some("sk_id_secret"));
    }
}
