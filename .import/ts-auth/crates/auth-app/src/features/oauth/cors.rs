//! Explicit-origin CORS for bearer/form OAuth APIs. Hosted cookie routes and
//! management APIs never participate, and credentialed CORS is never enabled.
use super::*;
use axum::{extract::Request, http::Method, middleware::Next};

pub(super) fn valid_origin(value: &str) -> bool {
    Url::parse(value).is_ok_and(|url| {
        url.origin().ascii_serialization() == value
            && (url.scheme() == "https" || (url.scheme() == "http" && is_loopback_host(&url)))
    })
}

fn endpoint(path: &str) -> Option<&str> {
    let (slug, route) = path.strip_prefix('/')?.split_once('/')?;
    matches!(
        route,
        "oauth/token"
            | "oauth/userinfo"
            | "oauth/revoke"
            | "oauth/device_authorization"
            | "oauth/anonymous"
            | "oauth/anonymous/upgrade"
            | "oauth/anonymous/upgrade/complete"
    )
    .then_some(slug)
}

pub(crate) async fn cors(State(state): State<AppState>, request: Request, next: Next) -> Response {
    let Some(slug) = endpoint(request.uri().path()).map(str::to_owned) else {
        return next.run(request).await;
    };
    let origin = request
        .headers()
        .get(header::ORIGIN)
        .and_then(|h| h.to_str().ok())
        .map(str::to_owned);
    let Some(origin) = origin else {
        let mut response = next.run(request).await;
        response
            .headers_mut()
            .append(header::VARY, HeaderValue::from_static("Origin"));
        return no_store(response);
    };
    let preflight = request.method() == Method::OPTIONS;
    let allowed = allowed(&state, &slug, &origin).await;
    match allowed {
        Err(error) => return error.into_response(),
        Ok(false) => {
            // Reject before running a mutation; allowed origins are explicit
            // instance policy, not just response decoration.
            return (
                StatusCode::FORBIDDEN,
                [(header::VARY, "Origin")],
                Json(json!({"error":"origin_not_allowed"})),
            )
                .into_response();
        }
        Ok(true) => {}
    }
    if preflight {
        let method = request
            .headers()
            .get(header::ACCESS_CONTROL_REQUEST_METHOD)
            .and_then(|h| h.to_str().ok())
            .unwrap_or_default();
        let method_allowed = if request.uri().path().ends_with("/userinfo") {
            method == "GET"
        } else {
            method == "POST"
        };
        let headers_allowed = request
            .headers()
            .get(header::ACCESS_CONTROL_REQUEST_HEADERS)
            .and_then(|h| h.to_str().ok())
            .is_none_or(|h| {
                h.split(',').all(|name| {
                    matches!(
                        name.trim().to_ascii_lowercase().as_str(),
                        "authorization" | "content-type"
                    )
                })
            });
        if !method_allowed || !headers_allowed {
            return StatusCode::FORBIDDEN.into_response();
        }
    }
    let mut response = if preflight {
        StatusCode::NO_CONTENT.into_response()
    } else {
        next.run(request).await
    };
    if let Ok(value) = HeaderValue::from_str(&origin) {
        response
            .headers_mut()
            .insert(header::ACCESS_CONTROL_ALLOW_ORIGIN, value);
    }
    response
        .headers_mut()
        .append(header::VARY, HeaderValue::from_static("Origin"));
    if preflight {
        response.headers_mut().append(
            header::VARY,
            HeaderValue::from_static(
                "Access-Control-Request-Method, Access-Control-Request-Headers",
            ),
        );
        response.headers_mut().insert(
            header::ACCESS_CONTROL_ALLOW_METHODS,
            HeaderValue::from_static("GET, POST"),
        );
        response.headers_mut().insert(
            header::ACCESS_CONTROL_ALLOW_HEADERS,
            HeaderValue::from_static("Authorization, Content-Type"),
        );
    }
    no_store(response)
}

async fn allowed(state: &AppState, slug: &str, origin: &str) -> AppResult<bool> {
    if !valid_origin(origin) {
        return Ok(false);
    }
    if origin == state.options.public_origin.trim_end_matches('/') {
        return Ok(true);
    }
    let Some(instance) = state
        .store
        .find_instance_by_slug(slug)
        .await
        .map_err(|e| crate::app::storage_error(&e))?
    else {
        return Ok(false);
    };
    let predicates =
        [Predicate::equal("instanceId", &instance.id)
            .map_err(|e| crate::app::storage_error(&e))?];
    let clients: Vec<OAuthClient> = state
        .store
        .query_all("oauth_clients", &predicates, 100)
        .await
        .map_err(|e| crate::app::storage_error(&e))?;
    Ok(clients
        .into_iter()
        .any(|c| c.instance_id == instance.id && c.allowed_origins.iter().any(|o| o == origin)))
}
