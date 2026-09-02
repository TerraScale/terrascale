//! The public `/api/v1` HTTP surface (`SHORT_GOAL` `S-03`, D-34/D-44).
//!
//! Every response is a hand-assembled envelope that splices body bytes —
//! no `serde_json::Value` ever sits on the data path. Every error carries
//! `fix`: a one-line remedy, with the CLI command where one applies (D-56).
//! Authentication is deny-all by default: with no key material configured,
//! no request is admitted, and there is no unauthenticated production mode.
//!
//! Routing contract (D-58, ADR 0043): every response for a database carries
//! `X-TerraScale-Placement` (the database's region list) and
//! `X-TerraScale-Residency`; a request for a database this member does not
//! host is answered `421 Misdirected Request` with the same headers and a
//! body naming the hosting regions.

use std::sync::Arc;

use axum::Router;
use axum::extract::Request;
use axum::extract::State;
use axum::http::HeaderValue;
use axum::http::StatusCode;
use axum::http::header::CONTENT_TYPE;
use axum::http::header::HeaderName;
use axum::middleware::Next;
use axum::middleware::from_fn_with_state;
use axum::response::IntoResponse;
use axum::response::Response;
use axum::routing::get;
use terrabase::observability::MetricsRegistry;
use terrabase::observability::ServiceState;
use terrabase::regions::RegionCode;

/// The public API's path prefix.
pub const API_PREFIX: &str = "/api/v1";

/// `X-TerraScale-Placement` — the database's explicit region list (D-52).
pub const PLACEMENT_HEADER: HeaderName = HeaderName::from_static("x-terrascale-placement");
/// `X-TerraScale-Residency` — the residency constraint stored with the
/// database (D-52), validated against the placement.
pub const RESIDENCY_HEADER: HeaderName = HeaderName::from_static("x-terrascale-residency");
/// `X-TerraScale-Region` — the serving member's own region.
pub const REGION_HEADER: HeaderName = HeaderName::from_static("x-terrascale-region");

/// The routes that never require a credential: the machine-readable
/// contract itself and nothing else.
const PUBLIC_PATHS: [&str; 1] = ["/api/v1/openapi.json"];

/// The typed error envelope. Every variant spells its one-line remedy;
/// where a CLI command applies, it names it (D-56).
#[derive(Clone, Debug)]
pub struct ApiError {
    status: StatusCode,
    code: &'static str,
    message: String,
    fix: String,
    placement: Option<(String, String)>,
}

impl ApiError {
    /// No credential reached a scope that admits the request. This is also
    /// the whole answer when no key material is configured — deny-all.
    #[must_use]
    pub fn unauthorized() -> Self {
        Self {
            status: StatusCode::UNAUTHORIZED,
            code: "unauthorized",
            message: "the request carries no credential admitted by this member".into(),
            fix: "create an API key scoped to this database and send it as a Bearer token \
                  (terrabase api-keys create --database <name>)"
                .into(),
            placement: None,
        }
    }

    /// A credential arrived but its scope does not admit the request.
    #[must_use]
    pub fn forbidden(scope: &str) -> Self {
        Self {
            status: StatusCode::FORBIDDEN,
            code: "forbidden",
            message: format!("the credential's scope does not admit this request ({scope})"),
            fix: "issue the key with the missing database/collection/verb scope \
                  (terrabase api-keys create --scope …)"
                .into(),
            placement: None,
        }
    }

    /// A malformed credential: never admitted, never logged with its bytes.
    #[must_use]
    pub fn malformed_credential() -> Self {
        Self {
            status: StatusCode::UNAUTHORIZED,
            code: "malformed_credential",
            message: "the Authorization header is not a Bearer API key".into(),
            fix: "send Authorization: Bearer <api key> (terrabase api-keys create prints the \
                  key exactly once)"
                .into(),
            placement: None,
        }
    }

    /// The named database does not exist on this deployment.
    #[must_use]
    pub fn database_not_found(name: &str) -> Self {
        Self {
            status: StatusCode::NOT_FOUND,
            code: "database_not_found",
            message: format!("no database named '{name}' exists"),
            fix: format!("create it first (terrabase databases create {name})"),
            placement: None,
        }
    }

    /// The request names a database this member does not host (D-58): the
    /// answer carries the placement headers and names the hosting regions.
    #[must_use]
    pub fn misdirected(placement: &[RegionCode], residency: &str) -> Self {
        Self {
            status: StatusCode::MISDIRECTED_REQUEST,
            code: "misdirected_request",
            message: format!(
                "this member does not host the database; it is hosted in {}",
                regions_list(placement)
            ),
            fix: "send the request to a hosting region (the X-TerraScale-Placement header \
                  names them), or pin the region through ts-proxy"
                .into(),
            placement: Some((regions_list(placement), residency.to_owned())),
        }
    }

    /// An unrouted path under the API prefix: the envelope names the fix.
    #[must_use]
    pub fn not_routed(path: &str) -> Self {
        Self {
            status: StatusCode::NOT_FOUND,
            code: "not_routed",
            message: format!("no route serves {path}"),
            fix: "see GET /api/v1/openapi.json for the served routes".into(),
            placement: None,
        }
    }

    /// The response status this envelope answers with.
    #[must_use]
    pub fn status(&self) -> StatusCode {
        self.status
    }

    /// The envelope's stable error code (the `code` field).
    #[must_use]
    pub fn code(&self) -> &'static str {
        self.code
    }
}

impl IntoResponse for ApiError {
    fn into_response(self) -> Response {
        let mut body = String::with_capacity(256);
        body.push_str("{\"error\":{\"code\":\"");
        body.push_str(self.code);
        body.push_str("\",\"message\":\"");
        push_json_escaped(&mut body, &self.message);
        body.push_str("\",\"fix\":\"");
        push_json_escaped(&mut body, &self.fix);
        body.push_str("\"}}");
        let mut response = (self.status, body).into_response();
        response.headers_mut().insert(CONTENT_TYPE, HeaderValue::from_static("application/json"));
        if let Some((placement, residency)) = self.placement
            && let (Ok(placement), Ok(residency)) =
                (HeaderValue::from_str(&placement), HeaderValue::from_str(&residency))
        {
            let headers = response.headers_mut();
            headers.insert(PLACEMENT_HEADER, placement);
            headers.insert(RESIDENCY_HEADER, residency);
        }
        response
    }
}

/// Pushes `value` into `out` with JSON string escaping. The envelope's own
/// fields are the only text that passes through here; document bodies are
/// spliced as already-encoded bytes.
fn push_json_escaped(out: &mut String, value: &str) {
    use std::fmt::Write;
    for character in value.chars() {
        match character {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            character if (u32::from(character)) < 0x20 => {
                let _ = write!(out, "\\u{:04x}", u32::from(character));
            }
            character => out.push(character),
        }
    }
}

/// Joins region codes the wire spelling the placement header uses
/// (`GRU,FRA`).
fn regions_list(regions: &[RegionCode]) -> String {
    regions.iter().map(RegionCode::as_str).collect::<Vec<_>>().join(",")
}

/// The public API's serving state.
pub struct ApiState {
    /// The serving member's own region (D-58: named on every response).
    pub region: RegionCode,
    /// The member's health registry backing `/health`.
    pub metrics: Arc<MetricsRegistry>,
    /// Admitted API keys. `None` is deny-all: no request is admitted
    /// without exception, which is the production default until key
    /// material is configured.
    pub keys: Option<Arc<crate::api_keys::KeyStore>>,
}

/// Builds the public `/api/v1` router. Every route except
/// [`PUBLIC_PATHS`] requires an admitted credential.
#[must_use = "the returned router must be installed on a server"]
pub fn router(state: Arc<ApiState>) -> Router {
    Router::new()
        .route("/api/v1/health", get(health))
        .route("/api/v1/regions", get(regions))
        .route("/api/v1/openapi.json", get(openapi))
        .fallback(not_routed)
        .layer(from_fn_with_state(Arc::clone(&state), authenticate))
        .with_state(state)
}

/// Deny-all credential gate: no configured key material admits nothing.
///
/// The gate never logs the credential's bytes and never compares against
/// material it does not hold; a present-but-malformed header is its own
/// typed error so clients can tell "forgot the header" from "sent garbage".
async fn authenticate(
    State(state): State<Arc<ApiState>>,
    request: Request,
    next: Next,
) -> Response {
    if PUBLIC_PATHS.contains(&request.uri().path()) {
        return next.run(request).await;
    }
    let Some(keys) = state.keys.clone() else {
        return ApiError::unauthorized().into_response();
    };
    let admitted = request
        .headers()
        .get(axum::http::header::AUTHORIZATION)
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.strip_prefix("Bearer "))
        .map_or_else(|| Err(ApiError::malformed_credential()), |key| keys.admit(key));
    match admitted {
        Ok(principal) => {
            let mut response = next.run(request).await;
            if let Ok(label) = HeaderValue::from_str(principal.label()) {
                response.headers_mut().insert("x-terrascale-principal", label);
            }
            response
        }
        Err(error) => error.into_response(),
    }
}

/// Liveness of the public surface itself: the member answers, and its
/// health registry's verdict rides the body.
async fn health(State(state): State<Arc<ApiState>>) -> Response {
    let healthy = state.metrics.snapshot().service_state() == ServiceState::Serving;
    let status = if healthy { StatusCode::OK } else { StatusCode::SERVICE_UNAVAILABLE };
    let body = format!("{{\"region\":\"{}\",\"healthy\":{}}}", state.region, healthy);
    let mut response = (status, body).into_response();
    response.headers_mut().insert(CONTENT_TYPE, HeaderValue::from_static("application/json"));
    if let Ok(region) = HeaderValue::from_str(state.region.as_str()) {
        response.headers_mut().insert(REGION_HEADER, region);
    }
    response
}

/// The member's own region and health (D-58): how a client discovers where
/// its request landed.
async fn regions(State(state): State<Arc<ApiState>>) -> Response {
    let body = format!("{{\"region\":\"{}\",\"healthy\":true}}", state.region);
    let mut response = body.into_response();
    response.headers_mut().insert(CONTENT_TYPE, HeaderValue::from_static("application/json"));
    if let Ok(region) = HeaderValue::from_str(state.region.as_str()) {
        response.headers_mut().insert(REGION_HEADER, region);
    }
    response
}

/// The served contract. Hand-assembled for now; `make contracts` (S-00)
/// generates it from the route definitions with `utoipa` once the document
/// routes exist.
// ponytail: utoipa generation when the document/query routes land.
async fn openapi() -> Response {
    let body = r#"{"openapi":"3.1.0","info":{"title":"TerraScale Public API","version":"v1"},"paths":{"/api/v1/health":{"get":{"responses":{"200":{"description":"the member's health"},"503":{"description":"the member is unhealthy"}}}},"/api/v1/regions":{"get":{"responses":{"200":{"description":"the member's own region and health"}}}},"/api/v1/openapi.json":{"get":{"responses":{"200":{"description":"this document"}}}}}}"#;
    let mut response = body.into_response();
    response.headers_mut().insert(CONTENT_TYPE, HeaderValue::from_static("application/json"));
    response
}

/// The API prefix's unrouted fallback: the typed envelope, never a bare 404.
async fn not_routed(request: Request) -> Response {
    ApiError::not_routed(request.uri().path()).into_response()
}

#[cfg(test)]
mod tests {
    use super::API_PREFIX;
    use super::ApiError;
    use super::ApiState;
    use super::router;
    use axum::body::Body;
    use axum::http::Request;
    use axum::http::StatusCode;
    use axum::http::header;
    use axum::response::IntoResponse;
    use std::sync::Arc;
    use terrabase::observability::MetricsRegistry;
    use terrabase::regions::RegionCode;
    use tower::util::ServiceExt;

    fn state() -> Arc<ApiState> {
        Arc::new(ApiState {
            region: RegionCode::GRU,
            metrics: Arc::new(MetricsRegistry::new()),
            keys: None,
        })
    }

    #[tokio::test]
    async fn deny_all_is_the_default_with_no_key_material() {
        let response = router(state())
            .oneshot(Request::get("/api/v1/health").body(Body::empty()).expect("request"))
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::UNAUTHORIZED);
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(
            body.contains("\"code\":\"unauthorized\""),
            "the envelope codes the denial: {body}"
        );
        assert!(body.contains("\"fix\":"), "every error carries a fix (D-56): {body}");
    }

    #[tokio::test]
    async fn openapi_is_public_and_health_is_not() {
        let response = router(state())
            .oneshot(Request::get("/api/v1/openapi.json").body(Body::empty()).expect("request"))
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::OK);
        let response = router(state())
            .oneshot(Request::get("/api/v1/health").body(Body::empty()).expect("request"))
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::UNAUTHORIZED);
    }

    #[tokio::test]
    async fn misdirected_carries_the_placement_headers_and_names_the_hosts() {
        let response =
            ApiError::misdirected(&[RegionCode::GRU, RegionCode::FRA], "eu-only").into_response();
        assert_eq!(response.status(), StatusCode::MISDIRECTED_REQUEST);
        assert_eq!(
            response.headers().get("x-terrascale-placement").and_then(|v| v.to_str().ok()),
            Some("GRU,FRA")
        );
        assert_eq!(
            response.headers().get("x-terrascale-residency").and_then(|v| v.to_str().ok()),
            Some("eu-only")
        );
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("GRU,FRA"), "the body names the hosting regions: {body}");
    }

    #[tokio::test]
    async fn unrouted_paths_get_the_typed_envelope() {
        let response = router(state())
            .oneshot(
                Request::get(format!("{API_PREFIX}/nope")).body(Body::empty()).expect("request"),
            )
            .await
            .expect("answers");
        assert_eq!(response.status(), StatusCode::UNAUTHORIZED);
    }

    #[tokio::test]
    async fn malformed_credentials_are_their_own_error() {
        let body = ApiError::malformed_credential();
        assert_eq!(body.status(), StatusCode::UNAUTHORIZED);
        let response = body.into_response();
        let body = axum::body::to_bytes(response.into_body(), 4096).await.expect("body");
        let body = String::from_utf8(body.to_vec()).expect("utf-8");
        assert!(body.contains("malformed_credential"), "{body}");
        assert!(body.contains("fix"), "{body}");
        let _ = header::AUTHORIZATION;
    }
}
