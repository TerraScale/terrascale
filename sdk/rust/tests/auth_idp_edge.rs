//! Edge-case tests for the ts-auth client, one section per matrix block:
//! response decoding (A), status mapping (B), request construction (C),
//! retry/cancellation (D), pagination (E), webhook verification (F), and
//! configuration (G). Matrix ids appear in the test names.

use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use async_trait::async_trait;
use serde_json::{json, Map, Value};
use terrascale::auth_idp::{
    verify_auth_idp_webhook, verify_auth_idp_webhook_with_tolerance, AuthIdpUser, AuthIdpUserPatch,
    CreateAuthIdpUser, OidcClientAuthentication, OidcResourceCredentials,
    OidcResourceIntrospection, PatchAuthIdpOauthClient, PatchAuthIdpOauthResource,
};
use terrascale::{
    ClientConfig, ClientConfigBuilder, Error, ErrorKind, HttpTransport, Method, Request, Response,
    TerraScaleClient,
};

const SLUG: &str = "acme";

// ---------------------------------------------------------------------------
// Transport seam
// ---------------------------------------------------------------------------

#[derive(Debug, Default)]
struct Mock {
    requests: Mutex<Vec<Request>>,
    responses: Mutex<VecDeque<terrascale::Result<Response>>>,
}

impl Mock {
    fn new() -> Arc<Self> {
        Arc::new(Self::default())
    }

    fn push(&self, response: Response) {
        self.responses
            .lock()
            .expect("response mutex")
            .push_back(Ok(response));
    }

    fn push_err(&self, error: Error) {
        self.responses
            .lock()
            .expect("response mutex")
            .push_back(Err(error));
    }

    fn count(&self) -> usize {
        self.requests.lock().expect("request mutex").len()
    }

    fn request(&self, index: usize) -> Request {
        self.requests
            .lock()
            .expect("request mutex")
            .get(index)
            .cloned()
            .expect("captured request")
    }
}

#[async_trait]
impl HttpTransport for Mock {
    async fn send(&self, request: Request) -> terrascale::Result<Response> {
        self.requests.lock().expect("request mutex").push(request);
        self.responses
            .lock()
            .expect("response mutex")
            .pop_front()
            .unwrap_or_else(|| Err(Error::transport("mock ran out of responses")))
    }
}

/// A transport that records the request and then never resolves, for
/// mid-flight cancellation tests.
#[derive(Debug, Default)]
struct Hanging {
    requests: Mutex<Vec<Request>>,
}

impl Hanging {
    fn count(&self) -> usize {
        self.requests.lock().expect("request mutex").len()
    }
}

#[async_trait]
impl HttpTransport for Hanging {
    async fn send(&self, request: Request) -> terrascale::Result<Response> {
        self.requests.lock().expect("request mutex").push(request);
        std::future::pending::<()>().await;
        unreachable!("pending never resolves")
    }
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

fn client(mock: Arc<Mock>) -> TerraScaleClient {
    let transport: Arc<dyn HttpTransport> = mock;
    TerraScaleClient::with_transport(config().build().expect("config"), transport)
}

fn retrying(mock: Arc<Mock>, attempts: u8) -> TerraScaleClient {
    let transport: Arc<dyn HttpTransport> = mock;
    TerraScaleClient::with_transport(
        config()
            .max_retry_attempts(attempts)
            .retry_delay_ms(0)
            .build()
            .expect("config"),
        transport,
    )
}

/// The common builder; callers add per-test knobs before `build()`.
fn config() -> ClientConfigBuilder {
    ClientConfig::builder()
        .auth_base_url("https://auth.test")
        .auth_api_key("sk_test_key")
}

fn empty(status: u16) -> Response {
    Response {
        status,
        headers: Vec::new(),
        body: Vec::new().into(),
    }
}

/// A raw response with explicit headers and body bytes.
fn raw(status: u16, headers: &[(&str, &str)], body: &[u8]) -> Response {
    Response {
        status,
        headers: headers
            .iter()
            .map(|(name, value)| ((*name).to_owned(), (*value).to_owned()))
            .collect(),
        body: body.to_vec().into(),
    }
}

fn error_envelope(status: u16, code: &str, message: &str) -> Response {
    Response::json(
        status,
        &json!({ "error": { "code": code, "message": message } }),
    )
}

fn user_json(id: &str) -> Value {
    json!({
        "id": id,
        "email": "ada@lovelace.test",
        "email_verified": true,
        "username": "ada",
        "name": "Ada",
        "banned": false,
        "ban_reason": null,
        "created_at": 1_758_000_000_i64,
        "last_sign_in_at": 1_758_000_100_i64,
        "public_metadata": { "tier": "gold" },
        "private_metadata": { "flag": true },
    })
}

fn err_of<T: std::fmt::Debug>(result: terrascale::Result<T>) -> Error {
    result.expect_err("expected an error")
}

/// HMAC-SHA256 from RFC 2104 over sha2, the same construction the verifier
/// uses (cross-checked against RFC 4231 vectors in the crate's unit tests).
fn hmac_sha256(key: &[u8], message: &[u8]) -> [u8; 32] {
    use sha2::{Digest, Sha256};
    let mut key_block = [0u8; 64];
    if key.len() > 64 {
        key_block[..32].copy_from_slice(&Sha256::digest(key));
    } else {
        key_block[..key.len()].copy_from_slice(key);
    }
    let mut inner = Sha256::new();
    for byte in key_block {
        inner.update([byte ^ 0x36]);
    }
    inner.update(message);
    let inner = inner.finalize();
    let mut outer = Sha256::new();
    for byte in key_block {
        outer.update([byte ^ 0x5c]);
    }
    outer.update(inner);
    outer.finalize().into()
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}

fn sign(secret: &str, timestamp: &str, payload: &[u8]) -> String {
    let message = [timestamp.as_bytes(), b".", payload].concat();
    format!("sha256={}", hex(&hmac_sha256(secret.as_bytes(), &message)))
}

fn body_json(request: &Request) -> Value {
    serde_json::from_slice(request.body.as_deref().unwrap_or_default()).expect("json body")
}

// ---------------------------------------------------------------------------
// A. Response-decode edges
// ---------------------------------------------------------------------------

#[tokio::test]
async fn a1_missing_or_null_id_is_invalid_response() {
    for body in [
        json!({ "banned": false, "created_at": 1_758_000_000_i64 }),
        json!({ "id": null, "banned": false, "created_at": 1_758_000_000_i64 }),
    ] {
        let mock = Mock::new();
        mock.push(Response::json(200, &body));
        let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

        let error = err_of(auth.users().get("user_1").await);
        assert_eq!(error.kind, ErrorKind::InvalidResponse);
        assert_eq!(error.code, "auth_idp_users_get.invalid_response");
        assert_eq!(error.status, Some(200));
        assert!(
            error
                .message
                .starts_with("response body does not match the contract: data error at line "),
            "unexpected message: {}",
            error.message
        );
    }
}

#[tokio::test]
async fn a2_ban_reason_present_null_and_absent_both_decode_to_none() {
    let mock = Mock::new();
    mock.push(Response::json(
        200,
        &json!({ "id": "user_1", "banned": false, "ban_reason": null, "created_at": 1_i64 }),
    ));
    mock.push(Response::json(
        200,
        &json!({ "id": "user_2", "banned": true, "created_at": 1_i64 }),
    ));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    let present_null = auth.users().get("user_1").await.expect("user");
    assert!(present_null.ban_reason.is_none());
    let absent = auth.users().get("user_2").await.expect("user");
    assert!(absent.ban_reason.is_none());
    assert!(absent.banned);
}

#[tokio::test]
async fn a3_unknown_extra_fields_are_ignored() {
    let mock = Mock::new();
    mock.push(Response::json(
        200,
        &json!({
            "id": "user_1",
            "banned": false,
            "created_at": 1_i64,
            "future_field": { "x": 1 },
            "another_future": [1, 2, 3],
        }),
    ));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    let user = auth.users().get("user_1").await.expect("forward compat");
    assert_eq!(user.id, "user_1");
}

#[tokio::test]
async fn a4_wrong_typed_created_at_is_invalid_response() {
    let mock = Mock::new();
    mock.push(Response::json(
        200,
        &json!({ "id": "user_1", "banned": false, "created_at": "not-a-number" }),
    ));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    let error = err_of(auth.users().get("user_1").await);
    assert_eq!(error.kind, ErrorKind::InvalidResponse);
    assert_eq!(error.code, "auth_idp_users_get.invalid_response");
    assert!(
        error
            .message
            .starts_with("response body does not match the contract: data error at line "),
        "unexpected message: {}",
        error.message
    );
    assert!(!error.message.contains("not-a-number"));
}

#[tokio::test]
async fn a5_malformed_json_body_is_invalid_response() {
    let mock = Mock::new();
    mock.push(raw(
        200,
        &[("content-type", "application/json")],
        br#"{"data": ["#,
    ));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    let error = err_of(auth.users().list(None, None).await);
    assert_eq!(error.kind, ErrorKind::InvalidResponse);
    assert_eq!(error.code, "auth_idp_users_list.invalid_response");
    assert!(
        error
            .message
            .starts_with("response body does not match the contract:"),
        "unexpected message: {}",
        error.message
    );
}

#[tokio::test]
async fn a6_empty_body_on_a_json_route_is_invalid_response() {
    let mock = Mock::new();
    mock.push(empty(200));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    let error = err_of(auth.users().get("user_1").await);
    assert_eq!(error.kind, ErrorKind::InvalidResponse);
    assert_eq!(error.code, "auth_idp_users_get.invalid_response");
    assert_eq!(error.message, "empty response does not match the contract.");
}

#[tokio::test]
async fn a7_non_json_html_body_is_invalid_response() {
    let mock = Mock::new();
    mock.push(raw(
        200,
        &[("content-type", "text/html")],
        b"<html><body>502 Bad Gateway</body></html>",
    ));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    let error = err_of(auth.users().get("user_1").await);
    assert_eq!(error.kind, ErrorKind::InvalidResponse);
    assert_eq!(error.code, "auth_idp_users_get.invalid_response");
    assert!(
        error
            .message
            .starts_with("response body does not match the contract:"),
        "unexpected message: {}",
        error.message
    );
}

#[tokio::test]
async fn a8_envelope_mixups_are_invalid_response_both_directions() {
    let mock = Mock::new();
    // Bare array where the {"data": [...]} users envelope is expected.
    mock.push(Response::json(200, &json!([user_json("user_1")])));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");
    let error = err_of(auth.users().list(None, None).await);
    assert_eq!(error.kind, ErrorKind::InvalidResponse);
    assert_eq!(error.code, "auth_idp_users_list.invalid_response");
    // serde accepts a struct from a sequence positionally, so the bare array
    // fails inside the `data` field: element 0 is a map where Vec is expected.
    assert!(
        error
            .message
            .starts_with("response body does not match the contract: data error at line "),
        "unexpected message: {}",
        error.message
    );

    // {"data": [...]} where the bare-array organizations envelope is expected.
    mock.push(Response::json(
        200,
        &json!({ "data": [{ "id": "org_1", "name": "Acme", "slug": "acme", "created_at": 1_i64 }] }),
    ));
    let error = err_of(auth.organizations().list().await);
    assert_eq!(error.kind, ErrorKind::InvalidResponse);
    assert_eq!(error.code, "auth_idp_organizations_list.invalid_response");
    assert!(
        error
            .message
            .starts_with("response body does not match the contract: data error at line "),
        "unexpected message: {}",
        error.message
    );
}

#[tokio::test]
async fn a9_null_data_array_is_invalid_response() {
    let mock = Mock::new();
    mock.push(Response::json(200, &json!({ "data": null })));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    // pins current behavior: a null array is a decode error, never a silent empty page.
    let error = err_of(auth.users().list(None, None).await);
    assert_eq!(error.kind, ErrorKind::InvalidResponse);
    assert_eq!(error.code, "auth_idp_users_list.invalid_response");
    assert!(
        error
            .message
            .starts_with("response body does not match the contract: data error at line "),
        "unexpected message: {}",
        error.message
    );
}

#[tokio::test]
async fn http_error_secret_reply_diagnostics_are_sanitized() {
    const CANARY: &str = "one-time-secret-canary";
    for (route, body) in [
        (
            "auth_idp.users.mfa.regenerate_recovery",
            json!({"data": CANARY}),
        ),
        (
            "auth_idp.users.impersonate",
            json!({"token": CANARY, "expires_at": CANARY}),
        ),
        (
            "auth_idp.api_keys.create",
            json!({"id": "key_1", "name": "key", "api_key": CANARY, "scopes": CANARY}),
        ),
        ("auth_idp.directories.create", json!(CANARY)),
        ("auth_idp.directories.rotate", json!(CANARY)),
        ("auth_idp.jwt_templates.tokens.create", json!(CANARY)),
        (
            "auth_idp.webhooks.create",
            json!({
                "id": "hook_1",
                "url": "https://example.test/hook",
                "events": CANARY,
                "signing_secret": CANARY,
            }),
        ),
    ] {
        let mock = Mock::new();
        let mut response = Response::json(200, &body);
        response
            .headers
            .push(("X-Request-ID".into(), "secret-reply-request".into()));
        mock.push(response);
        let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");
        let result = match route {
            "auth_idp.users.mfa.regenerate_recovery" => auth
                .users()
                .regenerate_recovery_codes("user_1", "factor_1")
                .await
                .map(|_| ()),
            "auth_idp.users.impersonate" => auth.users().impersonate("user_1").await.map(|_| ()),
            "auth_idp.api_keys.create" => auth.api_keys().create("key", None).await.map(|_| ()),
            "auth_idp.directories.create" => auth.directories().create(None).await.map(|_| ()),
            "auth_idp.directories.rotate" => {
                auth.directories().rotate("directory_1").await.map(|_| ())
            }
            "auth_idp.jwt_templates.tokens.create" => auth
                .jwt_templates()
                .create_token("template", "user_1")
                .await
                .map(|_| ()),
            "auth_idp.webhooks.create" => auth
                .webhooks()
                .create("https://example.test/hook", &["user.created"])
                .await
                .map(|_| ()),
            _ => unreachable!("test route"),
        };
        let error = err_of(result);
        assert_eq!(error.kind, ErrorKind::InvalidResponse, "{route}");
        assert_eq!(
            error.code,
            format!("{}.invalid_response", route.replace('.', "_"))
        );
        assert_eq!(error.status, Some(200), "{route}");
        assert_eq!(error.request_id.as_deref(), Some("secret-reply-request"));
        assert!(!error.message.contains(CANARY), "{route}");
        assert!(!format!("{error}").contains(CANARY), "{route}");
        assert!(!format!("{error:?}").contains(CANARY), "{route}");
        assert_eq!(mock.count(), 1, "{route}");
    }
}

#[tokio::test]
async fn http_error_malformed_replies_retain_response_context() {
    for body in [
        b"{\"data\": [".as_slice(),
        b"<html>bad upstream response</html>".as_slice(),
        b"".as_slice(),
    ] {
        let mock = Mock::new();
        mock.push(raw(200, &[("X-Request-ID", "malformed-request")], body));
        let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");
        let error = err_of(auth.users().get("user_1").await);
        assert_eq!(error.kind, ErrorKind::InvalidResponse);
        assert_eq!(error.code, "auth_idp_users_get.invalid_response");
        assert_eq!(error.status, Some(200));
        assert_eq!(error.request_id.as_deref(), Some("malformed-request"));
        assert_eq!(mock.count(), 1);
    }

    let mock = Mock::new();
    mock.push(raw(200, &[("X-Request-ID", "delete-request")], b""));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");
    let error = err_of(auth.users().delete("user_1").await);
    assert_eq!(error.kind, ErrorKind::InvalidResponse);
    assert_eq!(error.code, "expected_http_204");
    assert_eq!(error.message, "expected HTTP 204, got 200");
    assert_eq!(error.status, Some(200));
    assert_eq!(error.request_id.as_deref(), Some("delete-request"));
}

#[tokio::test]
async fn http_error_oversized_reply_retains_response_context() {
    let mock = Mock::new();
    mock.push(Response {
        status: 200,
        headers: vec![("X-Request-ID".into(), "capacity-request".into())],
        body: vec![b'x'; terrascale::transport::MAX_JSON_BYTES + 1].into(),
    });
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");
    let error = err_of(auth.users().get("user_1").await);
    assert_eq!(error.kind, ErrorKind::InvalidResponse);
    assert_eq!(error.code, "invalid_response");
    assert_eq!(error.message, "JSON response exceeds 4 MiB.");
    assert_eq!(error.status, Some(200));
    assert_eq!(error.request_id.as_deref(), Some("capacity-request"));
    assert_eq!(mock.count(), 1);
}

#[tokio::test]
async fn http_error_oversized_status_replies_are_not_retried() {
    for status in [429, 503] {
        let mock = Mock::new();
        let request_id = format!("oversized-{status}-request");
        mock.push(Response {
            status,
            headers: vec![
                ("X-Request-ID".into(), request_id.clone()),
                ("Retry-After".into(), "0".into()),
            ],
            body: vec![b'x'; terrascale::transport::MAX_JSON_BYTES + 1].into(),
        });
        mock.push(Response::json(200, &user_json("user_1")));
        let auth = retrying(Arc::clone(&mock), 1)
            .auth_idp(SLUG)
            .expect("client");
        let error = err_of(auth.users().get("user_1").await);
        assert_eq!(error.kind, ErrorKind::InvalidResponse, "HTTP {status}");
        assert_eq!(error.code, "invalid_response");
        assert_eq!(error.message, "JSON response exceeds 4 MiB.");
        assert_eq!(error.status, Some(status));
        assert_eq!(error.request_id.as_deref(), Some(request_id.as_str()));
        assert_eq!(mock.count(), 1, "HTTP {status} must remain terminal");
    }
}

#[tokio::test]
async fn http_error_ordinary_unavailable_reply_still_retries() {
    let mock = Mock::new();
    mock.push(raw(
        503,
        &[
            ("Retry-After", "0"),
            ("X-Request-ID", "unavailable-request"),
        ],
        br#"{"error":{"code":"temporarily_unavailable","message":"Try again."}}"#,
    ));
    mock.push(Response::json(200, &user_json("user_1")));
    let auth = retrying(Arc::clone(&mock), 1)
        .auth_idp(SLUG)
        .expect("client");
    let user = auth.users().get("user_1").await.expect("retried user");
    assert_eq!(user.id, "user_1");
    assert_eq!(mock.count(), 2, "ordinary HTTP 503 remains retryable");
}

#[tokio::test]
async fn http_error_oauth_diagnostic_keeps_response_context() {
    let mock = Mock::new();
    let mut response = Response::json(
        200,
        &json!({
            "access_token": "minted-secret",
            "token_type": "Bearer",
            "expires_in": "minted-secret",
            "scope": "documents:read",
        }),
    );
    response
        .headers
        .push(("X-Request-ID".into(), "oauth-request".into()));
    mock.push(response);
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");
    let error = err_of(
        auth.oidc()
            .token_client_credentials(
                OidcClientAuthentication::Form {
                    client_id: "client_1",
                    client_secret: "request-secret",
                },
                "https://api.test/documents",
                "documents:read",
            )
            .await,
    );
    assert_eq!(error.kind, ErrorKind::InvalidResponse);
    assert_eq!(
        error.message,
        "OAuth response does not match the provider contract."
    );
    assert_eq!(error.status, Some(200));
    assert_eq!(error.request_id.as_deref(), Some("oauth-request"));
    assert!(!format!("{error:?}").contains("minted-secret"));
    assert!(!format!("{error:?}").contains("request-secret"));
}

#[tokio::test]
async fn a10_large_page_of_100_users_decodes() {
    let mock = Mock::new();
    let page: Vec<Value> = (1..=100)
        .map(|index| user_json(&format!("user_{index}")))
        .collect();
    mock.push(Response::json(200, &json!({ "data": page })));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    let users = auth.users().list(Some(100), None).await.expect("page");
    assert_eq!(users.data.len(), 100);
    for (index, user) in users.data.iter().enumerate() {
        assert_eq!(user.id, format!("user_{}", index + 1));
    }
}

#[tokio::test]
async fn a11_unicode_round_trips_through_decode_and_patch_bytes() {
    let name = "Ada 💻 王小明 e\u{301}";
    let mock = Mock::new();
    mock.push(Response::json(
        200,
        &json!({ "id": "user_1", "name": name, "banned": false, "created_at": 1_i64 }),
    ));
    mock.push(Response::json(200, &user_json("user_1")));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    let user = auth.users().get("user_1").await.expect("user");
    assert_eq!(user.name.as_deref(), Some(name));
    assert_eq!(user.name.as_deref().unwrap().as_bytes(), name.as_bytes());

    auth.users()
        .patch("user_1", AuthIdpUserPatch::new().with_name(name))
        .await
        .expect("patched");
    // The wire body carries the exact UTF-8 bytes, no \u escaping.
    let expected = format!("{{\"name\":\"{name}\"}}");
    assert_eq!(mock.request(1).body.as_deref(), Some(expected.as_bytes()));
}

// ---------------------------------------------------------------------------
// B. Status-code and error-mapping edges
// ---------------------------------------------------------------------------

#[tokio::test]
async fn b12_b13_b15_status_kinds_and_codes_are_mapped() {
    let mock = Mock::new();
    mock.push(error_envelope(401, "unauthorized", "Invalid instance key."));
    mock.push(error_envelope(
        403,
        "insufficient_scope",
        "Missing scope users:read.",
    ));
    mock.push(error_envelope(409, "email_taken", "Email already taken."));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    let unauthorized = err_of(auth.users().get("user_1").await);
    assert_eq!(unauthorized.kind, ErrorKind::Authentication);
    assert_eq!(unauthorized.code, "unauthorized");
    assert_eq!(unauthorized.message, "Invalid instance key.");
    assert_eq!(unauthorized.status, Some(401));

    let forbidden = err_of(auth.users().get("user_1").await);
    assert_eq!(forbidden.kind, ErrorKind::Authorization);
    assert_eq!(forbidden.code, "insufficient_scope");
    assert_eq!(forbidden.message, "Missing scope users:read.");

    let conflict = err_of(auth.users().create(CreateAuthIdpUser::default()).await);
    assert_eq!(conflict.kind, ErrorKind::Conflict);
    assert_eq!(conflict.code, "email_taken");
    assert_eq!(conflict.message, "Email already taken.");
}

#[tokio::test]
async fn b14_404_codes_are_preserved_and_route_derived_when_absent() {
    let mock = Mock::new();
    mock.push(error_envelope(404, "user_not_found", "No such user."));
    mock.push(error_envelope(404, "not_found", "Not found."));
    mock.push(empty(404));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    let specific = err_of(auth.users().get("user_ghost").await);
    assert_eq!(specific.kind, ErrorKind::NotFound);
    assert_eq!(specific.code, "user_not_found");

    let generic = err_of(auth.users().get("user_ghost").await);
    assert_eq!(generic.kind, ErrorKind::NotFound);
    assert_eq!(generic.code, "not_found");

    let bare = err_of(auth.users().get("user_ghost").await);
    assert_eq!(bare.kind, ErrorKind::NotFound);
    assert_eq!(bare.code, "auth_idp_users_get");
    assert_eq!(
        bare.message,
        "Request auth_idp.users.get failed with status 404."
    );
}

#[tokio::test]
async fn b16_429_rate_limited_surfaces_without_retries_and_retries_when_configured() {
    // MaxRetryAttempts=0: the error surfaces immediately, carrying the
    // server's Retry-After (seconds form) on the error.
    let mock = Mock::new();
    mock.push(raw(
        429,
        &[("retry-after", "7")],
        br#"{"error":{"code":"rate_limited","message":"Too many requests."}}"#,
    ));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");
    let error = err_of(auth.users().list(None, None).await);
    assert_eq!(error.kind, ErrorKind::RateLimit);
    assert_eq!(error.code, "rate_limited");
    assert_eq!(error.message, "Too many requests.");
    assert_eq!(error.status, Some(429));
    assert_eq!(error.retry_after, Some(Duration::from_secs(7)));
    assert_eq!(mock.count(), 1);

    // A missing or non-numeric Retry-After yields no delay on the error.
    let mock = Mock::new();
    mock.push(raw(429, &[("retry-after", "tomorrow")], b""));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");
    let error = err_of(auth.users().list(None, None).await);
    assert_eq!(error.retry_after, None);

    // With retries configured the GET is retried and then succeeds.
    let mock = Mock::new();
    mock.push(raw(429, &[("retry-after", "2")], b""));
    mock.push(Response::json(200, &json!({ "data": [] })));
    let auth = retrying(Arc::clone(&mock), 1)
        .auth_idp(SLUG)
        .expect("client");
    auth.users().list(None, None).await.expect("retried");
    assert_eq!(mock.count(), 2);

    // A Retry-After longer than the 30 second cap is surfaced uncapped
    // instead of slept through and retried.
    let mock = Mock::new();
    mock.push(raw(429, &[("retry-after", "31")], b""));
    let auth = retrying(Arc::clone(&mock), 2)
        .auth_idp(SLUG)
        .expect("client");
    let error = err_of(auth.users().list(None, None).await);
    assert_eq!(error.kind, ErrorKind::RateLimit);
    assert_eq!(error.retry_after, Some(Duration::from_secs(31)));
    assert_eq!(mock.count(), 1, "delays beyond the cap never retry");
}

#[tokio::test]
async fn b17_503_checkout_errors_map_to_unavailable_kind() {
    let mock = Mock::new();
    mock.push(error_envelope(
        503,
        "temporarily_unavailable",
        "Try again shortly.",
    ));
    mock.push(error_envelope(
        503,
        "billing_not_configured",
        "Stripe is not configured.",
    ));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    let unavailable = err_of(auth.billing().checkout("pro").await);
    assert_eq!(unavailable.kind, ErrorKind::Unavailable);
    assert_eq!(unavailable.code, "temporarily_unavailable");
    assert_eq!(unavailable.status, Some(503));

    let unconfigured = err_of(auth.billing().checkout("enterprise").await);
    assert_eq!(unconfigured.kind, ErrorKind::Unavailable);
    assert_eq!(unconfigured.code, "billing_not_configured");
}

#[tokio::test]
async fn b18_flat_oauth_error_maps_despite_extra_headers() {
    let mock = Mock::new();
    mock.push(raw(
        401,
        &[("www-authenticate", "Basic realm=\"ts-auth\"")],
        br#"{"error":"invalid_client","error_description":"Client authentication failed."}"#,
    ));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    let error = err_of(
        auth.oidc()
            .token_client_credentials(
                OidcClientAuthentication::Form {
                    client_id: "oc_1",
                    client_secret: "wrong-secret",
                },
                "https://api.test/documents",
                "documents:read",
            )
            .await,
    );
    assert_eq!(error.kind, ErrorKind::Authentication);
    assert_eq!(error.code, "invalid_client");
    assert_eq!(error.message, "Client authentication failed.");
    assert_eq!(error.status, Some(401));
}

#[tokio::test]
async fn b19_flat_oauth_error_without_description_falls_back_to_the_code() {
    let mock = Mock::new();
    mock.push(raw(401, &[], br#"{"error":"invalid_grant"}"#));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    let error = err_of(
        auth.oidc()
            .token_refresh(
                OidcClientAuthentication::Form {
                    client_id: "oc_1",
                    client_secret: "sec_1",
                },
                "rt_stale",
                None,
                None,
            )
            .await,
    );
    assert_eq!(error.kind, ErrorKind::Authentication);
    assert_eq!(error.code, "invalid_grant");
    // Error.message is a plain String; an absent description falls back to
    // the error code itself.
    assert_eq!(error.message, "invalid_grant");
}

#[tokio::test]
async fn b20_null_error_fields_fall_back_to_status_derived_error() {
    let mock = Mock::new();
    mock.push(raw(
        401,
        &[("content-type", "application/json")],
        br#"{"error":{"code":null,"message":null}}"#,
    ));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    let error = err_of(auth.users().get("user_1").await);
    assert_eq!(error.kind, ErrorKind::Authentication);
    assert_eq!(error.code, "auth_idp_users_get");
    assert_eq!(
        error.message,
        "Request auth_idp.users.get failed with status 401."
    );
}

#[tokio::test]
async fn b21_non_json_error_bodies_derive_from_status() {
    let mock = Mock::new();
    mock.push(empty(500));
    mock.push(raw(
        500,
        &[("content-type", "text/plain")],
        b"upstream exploded",
    ));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    for _ in 0..2 {
        let error = err_of(auth.billing().get().await);
        assert_eq!(error.kind, ErrorKind::Server);
        assert_eq!(error.code, "auth_idp_billing_get");
        assert_eq!(
            error.message,
            "Request auth_idp.billing.get failed with status 500."
        );
    }
}

#[tokio::test]
async fn b22_delete_requires_exactly_204() {
    let mock = Mock::new();
    mock.push(Response::json(200, &user_json("user_1")));
    mock.push(empty(204));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    let error = err_of(auth.users().delete("user_1").await);
    assert_eq!(error.kind, ErrorKind::InvalidResponse);
    assert_eq!(error.message, "expected HTTP 204, got 200");
    auth.users().delete("user_2").await.expect("204 accepted");
    assert_eq!(mock.count(), 2);
    assert_eq!(
        mock.request(0).url,
        "https://auth.test/v1/acme/users/user_1"
    );
}

// ---------------------------------------------------------------------------
// C. Request-construction edges
// ---------------------------------------------------------------------------

#[tokio::test]
async fn c23_path_segment_encoding_pins_exact_bytes() {
    let mock = Mock::new();
    for _ in 0..6 {
        mock.push(Response::json(200, &user_json("user_1")));
    }
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    let expected = [
        ("a/b?c=d#e&f+g %h", "a%2Fb%3Fc%3Dd%23e%26f%2Bg%20%25h"),
        ("50%", "50%25"),
        ("café", "caf%C3%A9"),
        // UTF-8 bytes percent-encoded in uppercase hex.
        ("ユーザー", "%E3%83%A6%E3%83%BC%E3%82%B6%E3%83%BC"),
        // Unreserved characters pass through; embedded dots stay inert.
        ("user_1.~", "user_1.~"),
    ];
    for (index, (raw_id, encoded)) in expected.iter().enumerate() {
        auth.users().get(raw_id).await.expect("get");
        assert_eq!(
            mock.request(index).url,
            format!("https://auth.test/v1/acme/users/{encoded}"),
            "raw id {raw_id:?}"
        );
    }

    // Bare dot segments would mutate the route after normalization; they are
    // rejected before any transport call.
    for dot in [".", ".."] {
        let error = auth
            .users()
            .get(dot)
            .await
            .expect_err("dot segment rejected");
        assert_eq!(error.kind, ErrorKind::Validation);
        assert_eq!(error.code, "invalid_user_id");
    }
    assert_eq!(mock.count(), expected.len());
}

#[tokio::test]
async fn c24_blank_inputs_fail_validation_without_http() {
    let mock = Mock::new();
    let sdk = client(Arc::clone(&mock));
    let auth = sdk.auth_idp(SLUG).expect("client");

    let error = err_of(auth.users().get("   ").await);
    assert_eq!(
        (error.kind, error.code.as_str(), error.message.as_str()),
        (
            ErrorKind::Validation,
            "invalid_user_id",
            "user_id cannot be empty.",
        )
    );

    let error = err_of(auth.api_keys().create("  ", None).await);
    assert_eq!(error.code, "invalid_name");
    assert_eq!(error.message, "name cannot be empty.");

    let error = err_of(
        auth.organizations()
            .create_invitation("org_1", "  ", "admin")
            .await,
    );
    assert_eq!(error.code, "invalid_email");

    let error = sdk.auth_idp("  ").expect_err("blank slug");
    assert_eq!(error.code, "invalid_instance_slug");

    assert_eq!(mock.count(), 0, "no HTTP issued for blank inputs");
}

#[tokio::test]
async fn c25_starting_after_is_encoded_once_exactly() {
    let mock = Mock::new();
    mock.push(Response::json(200, &json!({ "data": [] })));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    auth.users()
        .list(Some(5), Some("cur&s=x+y%z#f"))
        .await
        .expect("list");
    assert_eq!(
        mock.request(0).url,
        "https://auth.test/v1/acme/users?limit=5&starting_after=cur%26s%3Dx%2By%25z%23f"
    );
}

#[tokio::test]
async fn c26_limit_boundaries_are_sent_or_rejected_preflight() {
    let mock = Mock::new();
    mock.push(Response::json(200, &json!({ "data": [] })));
    mock.push(Response::json(200, &json!({ "data": [] })));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    auth.users().list(Some(1), None).await.expect("limit 1");
    auth.users().list(Some(100), None).await.expect("limit 100");
    assert!(mock.request(0).url.ends_with("users?limit=1"));
    assert!(mock.request(1).url.ends_with("users?limit=100"));

    for limit in [0, 101] {
        let error = err_of(auth.users().list(Some(limit), None).await);
        assert_eq!(error.kind, ErrorKind::Validation);
        assert_eq!(error.code, "invalid_limit");
        assert_eq!(error.message, "user limit must be between 1 and 100.");
    }
    assert_eq!(mock.count(), 2, "out-of-range limits never hit the wire");
}

#[tokio::test]
async fn c27_slug_rules_reject_bad_slugs_and_accept_long_ones() {
    let sdk = client(Mock::new());
    for slug in ["", ".", "..", "ac me", "ac/me", "ac\tme", "ac\nme"] {
        let error = sdk.auth_idp(slug).expect_err("bad slug");
        assert_eq!(error.kind, ErrorKind::Validation);
        assert_eq!(error.code, "invalid_instance_slug");
        assert_eq!(
            error.message,
            "The instance slug must be non-empty and cannot contain whitespace, control characters, slashes, or dot segments."
        );
    }

    // pins current behavior: no length cap and non-ASCII allowed.
    sdk.auth_idp(&"a".repeat(300)).expect("long slug accepted");
    sdk.auth_idp("acmé").expect("unicode slug accepted");
}

#[tokio::test]
async fn c28_api_keys_with_control_or_whitespace_bytes_are_rejected_at_build() {
    let error = config()
        .auth_api_key("sk_bad\nkey")
        .build()
        .expect_err("control characters rejected");
    assert_eq!(error.kind, ErrorKind::Validation);
    assert_eq!(error.code, "invalid_api_key");
    assert_eq!(error.message, "API keys cannot contain control characters.");

    let error = config()
        .auth_api_key("sk key with spaces")
        .build()
        .expect_err("whitespace rejected");
    assert_eq!(error.code, "invalid_api_key");
    assert_eq!(
        error.message,
        "API keys cannot contain whitespace characters."
    );
}

#[tokio::test]
async fn c29_c30_c31_tri_state_patch_carries_exactly_the_configured_fields() {
    let mock = Mock::new();
    mock.push(Response::json(200, &user_json("user_1")));
    mock.push(Response::json(200, &user_json("user_1")));
    let users = client(Arc::clone(&mock))
        .auth_idp(SLUG)
        .expect("client")
        .users();

    let metadata: Map<String, Value> =
        serde_json::from_value(json!({ "tier": "gold" })).expect("map");
    users
        .patch(
            "user_1",
            AuthIdpUserPatch::new()
                .with_name("Ada Lovelace")
                .unset_username()
                .with_password("correct horse battery")
                .with_email("ada@new.test")
                .with_banned(true)
                .with_ban_reason("policy violation")
                .with_public_metadata(metadata)
                .unset_private_metadata(),
        )
        .await
        .expect("full patch");
    let full = mock.request(0);
    assert_eq!(full.method, Method::Patch);
    assert_eq!(
        body_json(&full),
        json!({
            "name": "Ada Lovelace",
            "username": null,
            "password": "correct horse battery",
            "email": "ada@new.test",
            "banned": true,
            "ban_reason": "policy violation",
            "public_metadata": { "tier": "gold" },
            "private_metadata": null,
        })
    );

    users
        .patch("user_1", AuthIdpUserPatch::new().with_name("Renamed"))
        .await
        .expect("single-field patch");
    // Exactly one configured field, valid JSON, no extra keys.
    assert_eq!(
        mock.request(1).body.as_deref(),
        Some(br#"{"name":"Renamed"}"#.as_slice())
    );

    let error = err_of(users.patch("user_1", AuthIdpUserPatch::new()).await);
    assert_eq!(error.kind, ErrorKind::Validation);
    assert_eq!(error.code, "invalid_user_patch");
    assert_eq!(error.message, "The user patch must set at least one field.");
    assert_eq!(mock.count(), 2, "empty patch never hit the wire");
}

#[tokio::test]
async fn c32_create_with_every_optional_omitted_sends_an_empty_object() {
    let mock = Mock::new();
    mock.push(Response::json(201, &user_json("user_1")));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    auth.users()
        .create(CreateAuthIdpUser::default())
        .await
        .expect("created");
    let request = mock.request(0);
    assert_eq!(request.url, "https://auth.test/v1/acme/users");
    assert_eq!(request.body.as_deref(), Some(b"{}".as_slice()));
}

#[tokio::test]
async fn c33_form_encoding_escapes_reserved_characters_exactly_once() {
    let mock = Mock::new();
    mock.push(Response::json(
        200,
        &json!({ "access_token": "at_1", "token_type": "Bearer", "expires_in": 3600, "scope": "documents:read" }),
    ));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    auth.oidc()
        .token_client_credentials(
            OidcClientAuthentication::Form {
                client_id: "oc_1",
                client_secret: "s&=+% #",
            },
            "https://api.test/documents",
            "documents:read",
        )
        .await
        .expect("token");
    let request = mock.request(0);
    assert_eq!(request.url, "https://auth.test/acme/oauth/token");
    assert_eq!(
        request.body.as_deref(),
        Some(
            b"grant_type=client_credentials&resource=https%3A%2F%2Fapi.test%2Fdocuments&scope=documents%3Aread&client_id=oc_1&client_secret=s%26%3D%2B%25%20%23"
                .as_slice()
        )
    );
}

#[tokio::test]
async fn c34_metadata_patch_serializes_nested_values_faithfully() {
    let mock = Mock::new();
    mock.push(Response::json(200, &user_json("user_1")));
    let users = client(Arc::clone(&mock))
        .auth_idp(SLUG)
        .expect("client")
        .users();

    let metadata: Map<String, Value> = serde_json::from_value(json!({
        "plan": "pro",
        "enabled": false,
        "limits": { "rows": [1, 2.5, true, null], "deep": { "k": "v" } },
    }))
    .expect("map");
    users
        .patch(
            "user_1",
            AuthIdpUserPatch::new().with_public_metadata(metadata),
        )
        .await
        .expect("patched");
    assert_eq!(
        body_json(&mock.request(0)),
        json!({
            "public_metadata": {
                "plan": "pro",
                "enabled": false,
                "limits": { "rows": [1, 2.5, true, null], "deep": { "k": "v" } },
            }
        })
    );
}

// ---------------------------------------------------------------------------
// D. Retry / cancellation edges
// ---------------------------------------------------------------------------

#[tokio::test]
async fn d35_get_503_surfaces_after_exactly_three_attempts() {
    let mock = Mock::new();
    for _ in 0..3 {
        mock.push(empty(503));
    }
    let auth = retrying(Arc::clone(&mock), 2)
        .auth_idp(SLUG)
        .expect("client");

    let error = err_of(auth.users().list(None, None).await);
    assert_eq!(error.kind, ErrorKind::Unavailable);
    assert_eq!(error.status, Some(503));
    assert_eq!(mock.count(), 3, "initial attempt + two retries");
}

#[tokio::test]
async fn d36_retry_delay_honors_retry_after_else_exponential_backoff() {
    // A server-sent Retry-After wins over the configured delay: Retry-After: 0
    // with a 250 ms config delay retries immediately.
    let mock = Mock::new();
    mock.push(raw(503, &[("retry-after", "0")], b""));
    mock.push(Response::json(200, &json!({ "data": [] })));
    let transport: Arc<dyn HttpTransport> = mock.clone();
    let sdk = TerraScaleClient::with_transport(
        config()
            .max_retry_attempts(1)
            .retry_delay_ms(250)
            .build()
            .expect("config"),
        transport,
    );
    let started = Instant::now();
    sdk.auth_idp(SLUG)
        .expect("client")
        .users()
        .list(None, None)
        .await
        .expect("retried");
    assert!(
        started.elapsed() < Duration::from_millis(250),
        "Retry-After: 0 must not wait the configured delay"
    );
    assert_eq!(mock.count(), 2);

    // Without Retry-After the delay is retry_delay_ms * 2^(attempt - 1): the
    // first retry waits exactly the base delay.
    let mock = Mock::new();
    mock.push(empty(503));
    mock.push(Response::json(200, &json!({ "data": [] })));
    let transport: Arc<dyn HttpTransport> = mock.clone();
    let sdk = TerraScaleClient::with_transport(
        config()
            .max_retry_attempts(1)
            .retry_delay_ms(250)
            .build()
            .expect("config"),
        transport,
    );
    let started = Instant::now();
    sdk.auth_idp(SLUG)
        .expect("client")
        .users()
        .list(None, None)
        .await
        .expect("retried");
    assert!(started.elapsed() >= Duration::from_millis(250));
    assert_eq!(mock.count(), 2);

    // With retry_delay_ms(0) the backoff-free retry is immediate.
    let mock = Mock::new();
    mock.push(empty(503));
    mock.push(Response::json(200, &json!({ "data": [] })));
    let transport: Arc<dyn HttpTransport> = mock.clone();
    let sdk = TerraScaleClient::with_transport(
        config()
            .max_retry_attempts(1)
            .retry_delay_ms(0)
            .build()
            .expect("config"),
        transport,
    );
    let started = Instant::now();
    sdk.auth_idp(SLUG)
        .expect("client")
        .users()
        .list(None, None)
        .await
        .expect("retried");
    assert!(started.elapsed() < Duration::from_secs(1));
    assert_eq!(mock.count(), 2);
}

#[tokio::test]
async fn d37_post_503_is_never_retried() {
    let mock = Mock::new();
    mock.push(empty(503));
    let auth = retrying(Arc::clone(&mock), 2)
        .auth_idp(SLUG)
        .expect("client");

    let error = err_of(auth.users().create(CreateAuthIdpUser::default()).await);
    assert_eq!(error.kind, ErrorKind::Unavailable);
    assert_eq!(error.status, Some(503));
    assert_eq!(
        mock.count(),
        1,
        "mutations never retry without an idempotency key"
    );
}

#[tokio::test]
async fn d38_client_errors_never_retry_even_for_gets() {
    for (status, kind) in [
        (400u16, ErrorKind::Validation),
        (409, ErrorKind::Conflict),
        (401, ErrorKind::Authentication),
        (403, ErrorKind::Authorization),
    ] {
        let mock = Mock::new();
        mock.push(empty(status));
        let auth = retrying(Arc::clone(&mock), 2)
            .auth_idp(SLUG)
            .expect("client");

        let error = err_of(auth.users().list(None, None).await);
        assert_eq!(error.kind, kind, "status {status}");
        assert_eq!(error.status, Some(status));
        assert_eq!(mock.count(), 1, "status {status} must not be retried");
    }
}

#[tokio::test]
async fn d39_transport_errors_retry_reads_but_never_mutations() {
    let mock = Mock::new();
    mock.push_err(Error::transport("connection refused"));
    mock.push(Response::json(200, &json!({ "data": [] })));
    let auth = retrying(Arc::clone(&mock), 1)
        .auth_idp(SLUG)
        .expect("client");
    auth.users().list(None, None).await.expect("read retried");
    assert_eq!(mock.count(), 2);

    let mock = Mock::new();
    mock.push_err(Error::transport("connection reset"));
    let auth = retrying(Arc::clone(&mock), 1)
        .auth_idp(SLUG)
        .expect("client");
    let error = err_of(auth.users().create(CreateAuthIdpUser::default()).await);
    assert_eq!(error.kind, ErrorKind::Transport);
    assert_eq!(error.code, "transport");
    assert_eq!(mock.count(), 1, "mutations never retry a transport error");
}

#[tokio::test]
async fn d40_unpolled_future_issues_no_http() {
    // Rust's cancellation seam is future drop: the ts-auth routes take no
    // CancellationToken/OperationOptions. A future that is never awaited is
    // lazy, so no request is issued.
    let mock = Mock::new();
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");
    let users = auth.users();
    let pending = users.get("user_1");
    drop(pending);
    assert_eq!(mock.count(), 0);
}

#[tokio::test]
async fn d41_mid_flight_cancellation_by_future_drop_is_honored() {
    let hanging = Arc::new(Hanging::default());
    let transport: Arc<dyn HttpTransport> = hanging.clone();
    let sdk = TerraScaleClient::with_transport(config().build().expect("config"), transport);

    // The request reaches the transport, then the caller cancels by dropping
    // the future (timeout drops it). There is no error type to pin: dropping
    // is the cancellation.
    let result = tokio::time::timeout(
        Duration::from_millis(50),
        sdk.auth_idp(SLUG).expect("client").users().get("user_1"),
    )
    .await;
    assert!(result.is_err(), "the in-flight call was dropped at 50 ms");
    assert_eq!(hanging.count(), 1, "exactly one HTTP attempt was issued");
}

#[tokio::test(start_paused = true)]
async fn d42_hanging_request_hits_the_30_second_deadline() {
    let hanging = Arc::new(Hanging::default());
    let transport: Arc<dyn HttpTransport> = hanging.clone();
    let sdk = TerraScaleClient::with_transport(config().build().expect("config"), transport);

    // The transport never resolves; the paused clock is advanced past the
    // 30 second ts-auth deadline, which must surface as the typed error.
    let request = tokio::spawn(async move {
        sdk.auth_idp(SLUG)
            .expect("client")
            .users()
            .get("user_1")
            .await
    });
    tokio::time::advance(Duration::from_secs(31)).await;
    let error = err_of(request.await.expect("task joined"));
    assert_eq!(error.kind, ErrorKind::Transport);
    assert_eq!(error.code, "ts_auth_deadline_exceeded");
    assert_eq!(hanging.count(), 1, "exactly one HTTP attempt was issued");
}

// ---------------------------------------------------------------------------
// E. Pagination edges
// ---------------------------------------------------------------------------

#[tokio::test]
async fn e43_e44_list_all_walks_full_and_short_final_pages() {
    // 2, 2, empty -> 4 users over 3 requests.
    let mock = Mock::new();
    mock.push(Response::json(
        200,
        &json!({ "data": [user_json("user_1"), user_json("user_2")] }),
    ));
    mock.push(Response::json(
        200,
        &json!({ "data": [user_json("user_3"), user_json("user_4")] }),
    ));
    mock.push(Response::json(200, &json!({ "data": [] })));
    let users = client(Arc::clone(&mock))
        .auth_idp(SLUG)
        .expect("client")
        .users();
    let all = users.list_all(2).await.expect("walked");
    let ids = all.iter().map(|user| user.id.as_str()).collect::<Vec<_>>();
    assert_eq!(ids, ["user_1", "user_2", "user_3", "user_4"]);
    assert_eq!(mock.count(), 3);

    // Short final page (1 row) -> the walk stops there; the empty-page
    // request is never issued.
    let mock = Mock::new();
    mock.push(Response::json(
        200,
        &json!({ "data": [user_json("user_1"), user_json("user_2")] }),
    ));
    mock.push(Response::json(
        200,
        &json!({ "data": [user_json("user_3")] }),
    ));
    let users = client(Arc::clone(&mock))
        .auth_idp(SLUG)
        .expect("client")
        .users();
    let all: Vec<AuthIdpUser> = users.list_all(2).await.expect("walked");
    let ids = all.iter().map(|user| user.id.as_str()).collect::<Vec<_>>();
    assert_eq!(ids, ["user_1", "user_2", "user_3"]);
    assert_eq!(mock.count(), 2);
    assert_eq!(
        mock.request(1).url,
        "https://auth.test/v1/acme/users?limit=2&starting_after=user_2"
    );

    // A server repeating the same full page forever: the repeated cursor ends
    // the walk instead of looping.
    let mock = Mock::new();
    let repeated = json!({ "data": [user_json("user_1"), user_json("user_2")] });
    mock.push(Response::json(200, &repeated));
    mock.push(Response::json(200, &repeated));
    let users = client(Arc::clone(&mock))
        .auth_idp(SLUG)
        .expect("client")
        .users();
    let all: Vec<AuthIdpUser> = users.list_all(2).await.expect("walk stops");
    let ids = all.iter().map(|user| user.id.as_str()).collect::<Vec<_>>();
    assert_eq!(ids, ["user_1", "user_2", "user_1", "user_2"]);
    assert_eq!(mock.count(), 2, "the repeated cursor terminated the walk");

    // An alternating cursor never repeats but does not ascend either: the
    // id-ascending contract is violated, so the walk stops after two pages.
    let mock = Mock::new();
    mock.push(Response::json(
        200,
        &json!({ "data": [user_json("user_1"), user_json("user_b")] }),
    ));
    mock.push(Response::json(
        200,
        &json!({ "data": [user_json("user_3"), user_json("user_a")] }),
    ));
    let users = client(Arc::clone(&mock))
        .auth_idp(SLUG)
        .expect("client")
        .users();
    let all: Vec<AuthIdpUser> = users.list_all(2).await.expect("walk stops");
    let ids = all.iter().map(|user| user.id.as_str()).collect::<Vec<_>>();
    assert_eq!(ids, ["user_1", "user_b", "user_3", "user_a"]);
    assert_eq!(
        mock.count(),
        2,
        "the non-advancing (user_a < user_b) cursor terminated the walk"
    );
}

#[tokio::test]
async fn e45_unknown_cursor_decodes_the_servers_resume_point() {
    let mock = Mock::new();
    mock.push(Response::json(
        200,
        &json!({ "data": [user_json("user_1"), user_json("user_2")] }),
    ));
    let auth = client(Arc::clone(&mock)).auth_idp(SLUG).expect("client");

    let page = auth
        .users()
        .list(Some(2), Some("user_ghost"))
        .await
        .expect("server resumed from the top");
    assert_eq!(page.data.len(), 2);
    assert_eq!(
        mock.request(0).url,
        "https://auth.test/v1/acme/users?limit=2&starting_after=user_ghost"
    );
}

// ---------------------------------------------------------------------------
// F. Webhook verifier edges
// ---------------------------------------------------------------------------

const SECRET: &str = "whsec_0123456789abcdef0123456789abcdef";

#[test]
fn f47_f48_f49_signature_shape_edges_are_rejected() {
    let timestamp = "1758000000";
    let body = br#"{"id":"evt_1"}"#;
    let signature = sign(SECRET, timestamp, body);

    // Uppercase hex is not the promised lowercase form.
    let upper = format!("sha256={}", signature["sha256=".len()..].to_uppercase());
    assert!(!verify_auth_idp_webhook(SECRET, timestamp, &upper, body));

    // Missing sha256= prefix.
    assert!(!verify_auth_idp_webhook(
        SECRET,
        timestamp,
        &signature["sha256=".len()..],
        body
    ));

    // 63 and 65 hex characters after the prefix.
    let hex = &signature["sha256=".len()..];
    assert_eq!(hex.len(), 64);
    let short = format!("sha256={}", &hex[..63]);
    assert!(!verify_auth_idp_webhook(SECRET, timestamp, &short, body));
    let long = format!("sha256={hex}a");
    assert!(!verify_auth_idp_webhook(SECRET, timestamp, &long, body));
}

#[test]
fn f50_non_numeric_timestamps_fail_in_both_variants() {
    let body = br#"{"id":"evt_1"}"#;
    for timestamp in ["not-a-number", "12.5", "", "+100", " 1758000000"] {
        let signature = sign(SECRET, timestamp, body);
        assert!(
            !verify_auth_idp_webhook(SECRET, timestamp, &signature, body),
            "non-numeric {timestamp:?} must not verify without tolerance"
        );
        assert!(
            !verify_auth_idp_webhook_with_tolerance(
                SECRET,
                timestamp,
                &signature,
                body,
                Duration::from_secs(300),
                SystemTime::now(),
            ),
            "non-numeric {timestamp:?} must not verify with tolerance"
        );
    }

    // Negative and far-future timestamps pass without tolerance (no replay
    // window) and fail with one.
    for timestamp in ["-5", "9999999999"] {
        let signature = sign(SECRET, timestamp, body);
        assert!(verify_auth_idp_webhook(SECRET, timestamp, &signature, body));
        assert!(!verify_auth_idp_webhook_with_tolerance(
            SECRET,
            timestamp,
            &signature,
            body,
            Duration::from_secs(300),
            UNIX_EPOCH + Duration::from_secs(1_758_000_000),
        ));
    }
}

#[test]
fn f51_empty_payload_is_a_signable_input() {
    let timestamp = "1758000000";
    let signature = sign(SECRET, timestamp, b"");
    assert!(verify_auth_idp_webhook(SECRET, timestamp, &signature, b""));
}

#[test]
fn f52_empty_secret_never_verifies() {
    // An empty secret is never a configured signing secret; it returns false
    // in every variant, matching the Go and TypeScript verifiers.
    let timestamp = "1758000000";
    let body = b"payload";
    let signature = sign("", timestamp, body);
    assert!(!verify_auth_idp_webhook("", timestamp, &signature, body));
    assert!(!verify_auth_idp_webhook(
        SECRET, timestamp, &signature, body
    ));
}

#[test]
fn f53_tolerance_boundary_is_inclusive_one_second_past_fails() {
    let timestamp = "1000";
    let body = b"payload";
    let signature = sign(SECRET, timestamp, body);
    let at = |seconds: u64| UNIX_EPOCH + Duration::from_secs(seconds);

    let tolerance = Duration::from_secs(300);
    assert!(verify_auth_idp_webhook_with_tolerance(
        SECRET,
        timestamp,
        &signature,
        body,
        tolerance,
        at(1300)
    ));
    assert!(verify_auth_idp_webhook_with_tolerance(
        SECRET,
        timestamp,
        &signature,
        body,
        tolerance,
        at(700)
    ));
    assert!(!verify_auth_idp_webhook_with_tolerance(
        SECRET,
        timestamp,
        &signature,
        body,
        tolerance,
        at(1301)
    ));
    assert!(!verify_auth_idp_webhook_with_tolerance(
        SECRET,
        timestamp,
        &signature,
        body,
        tolerance,
        at(699)
    ));
}

#[test]
fn f54_unicode_payload_bytes_verify() {
    let timestamp = "1758000000";
    let body = "ユーザー 💻 创建".as_bytes();
    let signature = sign(SECRET, timestamp, body);
    assert!(verify_auth_idp_webhook(SECRET, timestamp, &signature, body));
    // Any other multi-byte body fails the exact-byte comparison.
    assert!(!verify_auth_idp_webhook(
        SECRET,
        timestamp,
        &signature,
        "ユーザー 💻 别の".as_bytes(),
    ));
}

#[test]
fn f55_different_secrets_produce_different_valid_signatures() {
    let timestamp = "1758000000";
    let body = br#"{"id":"evt_1"}"#;
    let first = sign(SECRET, timestamp, body);
    let second = sign("whsec_ffffffffffffffffffffffffffffffff", timestamp, body);
    assert_ne!(first, second);
    assert!(verify_auth_idp_webhook(SECRET, timestamp, &first, body));
    assert!(verify_auth_idp_webhook(
        "whsec_ffffffffffffffffffffffffffffffff",
        timestamp,
        &second,
        body
    ));
    assert!(!verify_auth_idp_webhook(SECRET, timestamp, &second, body));
}

// ---------------------------------------------------------------------------
// G. Configuration edges
// ---------------------------------------------------------------------------

#[tokio::test]
async fn g56_g57_auth_base_url_origin_rules() {
    // Query, fragment, and userinfo are rejected at build time.
    for (url, message) in [
        (
            "https://auth.test/?x=1",
            "API base URL cannot contain a query or fragment.",
        ),
        (
            "https://auth.test/#frag",
            "API base URL cannot contain a query or fragment.",
        ),
        (
            "https://user:pass@auth.test",
            "API base URL cannot contain userinfo.",
        ),
        (
            "http://auth.example.com",
            "API endpoints must use HTTPS, except explicit loopback/test HTTP endpoints.",
        ),
        ("", "API base URL cannot be empty."),
    ] {
        let error = config()
            .auth_base_url(url)
            .build()
            .expect_err("unsafe origin");
        assert_eq!(error.kind, ErrorKind::Validation, "url {url:?}");
        assert_eq!(error.message, message, "url {url:?}");
    }
    let not_a_url = config()
        .auth_base_url("not a url")
        .build()
        .expect_err("unparseable origin");
    assert_eq!(not_a_url.code, "invalid_base_url");
    assert!(not_a_url
        .message
        .starts_with("API base URL must be an absolute HTTP(S) URL:"));

    // HTTP is allowed for loopback and .test hosts only.
    for url in [
        "http://localhost:9000",
        "http://127.0.0.1:8080",
        "http://auth.test",
    ] {
        config()
            .auth_base_url(url)
            .build()
            .unwrap_or_else(|error| panic!("loopback/test HTTP must be allowed: {url}: {error}"));
    }

    // pins current behavior: a path in auth_base_url is NOT rejected; it is
    // kept as a prefix when routes are appended.
    let mock = Mock::new();
    mock.push(Response::json(200, &user_json("user_1")));
    let transport: Arc<dyn HttpTransport> = mock.clone();
    let sdk = TerraScaleClient::with_transport(
        config()
            .auth_base_url("https://auth.test/prefix")
            .build()
            .expect("path-prefixed origin"),
        transport,
    );
    sdk.auth_idp(SLUG)
        .expect("client")
        .users()
        .get("user_1")
        .await
        .expect("prefixed route");
    assert_eq!(
        mock.request(0).url,
        "https://auth.test/prefix/v1/acme/users/user_1"
    );
}

#[tokio::test]
async fn g58_trailing_slashes_normalize_to_the_same_urls() {
    for base_url in [
        "https://auth.test",
        "https://auth.test/",
        "https://auth.test///",
    ] {
        let mock = Mock::new();
        mock.push(Response::json(200, &user_json("user_1")));
        let transport: Arc<dyn HttpTransport> = mock.clone();
        let sdk = TerraScaleClient::with_transport(
            config().auth_base_url(base_url).build().expect("config"),
            transport,
        );
        sdk.auth_idp(SLUG)
            .expect("client")
            .users()
            .get("user_1")
            .await
            .expect("get");
        assert_eq!(
            mock.request(0).url,
            "https://auth.test/v1/acme/users/user_1",
            "base_url {base_url:?}"
        );
    }
}

fn resource_claims() -> Value {
    json!({"active":true, "token_type":"Bearer", "iss":"https://auth.test/acme", "sub":"user-1", "aud":"https://api.test/documents?x=%2f", "client_id":"originating-client", "scope":"documents:read", "exp":2000, "iat":1000, "sid":"session-1", "principal_kind":"user"})
}

#[test]
fn resource_introspection_rejects_missing_duplicate_extra_and_malformed_claims() {
    let valid = resource_claims();
    assert!(serde_json::from_value::<OidcResourceIntrospection>(valid.clone()).is_ok());
    for field in [
        "active",
        "token_type",
        "iss",
        "sub",
        "aud",
        "client_id",
        "scope",
        "exp",
        "iat",
        "sid",
        "principal_kind",
    ] {
        let mut candidate = valid.clone();
        candidate.as_object_mut().expect("object").remove(field);
        assert!(
            serde_json::from_value::<OidcResourceIntrospection>(candidate).is_err(),
            "missing {field}"
        );
    }
    for (field, value) in [
        ("aud", json!(["https://api.test/documents?x=%2f"])),
        ("aud", Value::Null),
        ("sid", Value::Null),
        ("sid", json!("")),
        ("principal_kind", json!("admin")),
        ("principal_kind", json!("service")),
        ("token_type", json!("refresh_token")),
        ("exp", json!(1000)),
        ("iat", json!(-1)),
        ("scope", json!("duplicate duplicate")),
        ("scope", json!("has\\slash")),
        ("client_id", json!("")),
        ("sub", json!("")),
    ] {
        let mut candidate = valid.clone();
        candidate[field] = value;
        assert!(
            serde_json::from_value::<OidcResourceIntrospection>(candidate).is_err(),
            "malformed {field}"
        );
    }
    for field in [
        "family_id",
        "token_use",
        "client_auth_epoch",
        "resource_auth_epoch",
        "unexpected",
    ] {
        let mut candidate = valid.clone();
        candidate[field] = json!("secret-value");
        assert!(
            serde_json::from_value::<OidcResourceIntrospection>(candidate).is_err(),
            "extra {field}"
        );
    }
    assert!(
        serde_json::from_str::<OidcResourceIntrospection>(r#"{"active":false,"sid":null}"#)
            .is_err()
    );
    assert!(serde_json::from_str::<OidcResourceIntrospection>(
        r#"{"active":false,"active":false}"#
    )
    .is_err());
    let raw = serde_json::to_string(&valid).expect("json");
    let duplicate = raw.replacen("{", r#"{"sid":"injected","#, 1);
    assert!(serde_json::from_str::<OidcResourceIntrospection>(&duplicate).is_err());
    let mut service = valid;
    service["principal_kind"] = json!("service");
    service["sub"] = service["client_id"].clone();
    service["sid"] = Value::Null;
    assert!(serde_json::from_value::<OidcResourceIntrospection>(service).is_ok());
}

#[test]
fn resource_binding_checks_exact_registered_identity_and_current_time() {
    let OidcResourceIntrospection::Active(claims) =
        serde_json::from_value::<OidcResourceIntrospection>(resource_claims()).expect("claims")
    else {
        panic!("active")
    };
    let issuer = "https://auth.test/acme";
    let audience = "https://api.test/documents?x=%2f";
    assert!(claims
        .check_binding_at(issuer, audience, UNIX_EPOCH + Duration::from_secs(1500))
        .is_ok());
    for (expected_issuer, expected_audience, now) in [
        ("https://other.test/acme", audience, 1500),
        (issuer, "https://api.test/documents?x=%2F", 1500),
        (issuer, audience, 2000),
        (issuer, audience, 999),
    ] {
        assert_eq!(
            claims
                .check_binding_at(
                    expected_issuer,
                    expected_audience,
                    UNIX_EPOCH + Duration::from_secs(now)
                )
                .expect_err("deny")
                .kind,
            ErrorKind::Authorization
        );
    }
    assert_eq!(
        claims
            .check_binding_at(
                "https://AUTH.test/acme",
                audience,
                UNIX_EPOCH + Duration::from_secs(1500)
            )
            .expect_err("noncanonical issuer")
            .kind,
        ErrorKind::Validation
    );
    assert!(claims
        .check_binding_at(issuer, audience, UNIX_EPOCH - Duration::from_secs(1))
        .is_err());
}

#[tokio::test]
async fn resource_validation_denies_before_dispatch_and_preserves_u64_epochs() {
    let mock = Mock::new();
    let auth = client(mock.clone()).auth_idp(SLUG).expect("auth");
    let oidc = auth.oidc();
    let public = OidcClientAuthentication::None {
        client_id: "public",
    };
    let form = OidcClientAuthentication::Form {
        client_id: "client",
        client_secret: "secret",
    };
    for resource in [
        "",
        " https://api.test/docs",
        "https://api.test/docs ",
        "http://api.test/docs",
        "https://user@api.test/docs",
        "https://api.test/docs#fragment",
        "https://api.test/docs?x=%xx",
        "https://api.test/docs?x=ä",
    ] {
        assert_eq!(
            oidc.token_refresh(form, "refresh", Some(resource), None)
                .await
                .expect_err("invalid resource")
                .kind,
            ErrorKind::Validation
        );
    }
    assert_eq!(
        oidc.token_client_credentials(public, "https://api.test/docs", "docs:read")
            .await
            .expect_err("public service")
            .kind,
        ErrorKind::Validation
    );
    assert_eq!(
        oidc.token_client_credentials(form, "https://api.test/docs", "")
            .await
            .expect_err("scope required")
            .kind,
        ErrorKind::Validation
    );
    assert_eq!(
        oidc.token_refresh(form, "refresh", None, Some("dup dup"))
            .await
            .expect_err("duplicate scope")
            .kind,
        ErrorKind::Validation
    );
    assert!(auth
        .oauth_clients()
        .patch("client", PatchAuthIdpOauthClient::new(0))
        .await
        .is_err());
    assert!(auth.oauth_resources().disable("resource", 0).await.is_err());
    assert_eq!(mock.count(), 0);
    let epoch = serde_json::to_value(PatchAuthIdpOauthResource::new(u64::MAX)).expect("epoch");
    assert_eq!(epoch["expected_auth_epoch"].as_u64(), Some(u64::MAX));
}

#[tokio::test]
async fn resource_introspection_denials_are_not_cached_and_keep_server_status() {
    let mock = Mock::new();
    mock.push(Response::json(
        401,
        &json!({"error":"invalid_client","error_description":"Invalid resource credentials."}),
    ));
    mock.push(Response::json(
        500,
        &json!({"error":"server_error","error_description":"Resource store unavailable."}),
    ));
    mock.push(Response::json(200, &json!({"active":false})));
    let oidc = client(mock.clone()).auth_idp(SLUG).expect("auth").oidc();
    let credentials =
        OidcResourceCredentials::new("resource-client", "resource-secret").expect("credentials");
    let denied = oidc
        .introspect_resource(&credentials, "token")
        .await
        .expect_err("401");
    assert_eq!(denied.status, Some(401));
    let unavailable = oidc
        .introspect_resource(&credentials, "token")
        .await
        .expect_err("500");
    assert_eq!(unavailable.status, Some(500));
    assert_eq!(unavailable.kind, ErrorKind::Server);
    assert_eq!(
        oidc.introspect_resource(&credentials, "token")
            .await
            .expect("fresh result"),
        OidcResourceIntrospection::Inactive
    );
    assert_eq!(mock.count(), 3);
    for index in 0..mock.count() {
        assert!(
            mock.request(index)
                .headers
                .iter()
                .any(|(name, value)| name.eq_ignore_ascii_case("cache-control")
                    && value == "no-store")
        );
    }
}
