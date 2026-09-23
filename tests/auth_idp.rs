use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use async_trait::async_trait;
use serde_json::{json, Value};
use terrascale::auth_idp::{
    verify_auth_idp_webhook_with_tolerance, AuthIdpUserPatch, CreateAuthIdpOauthClient,
    CreateAuthIdpUser,
};
use terrascale::{
    verify_auth_idp_webhook, ClientConfig, ErrorKind, HttpTransport, Method, RegionCode, Request,
    Response, Result, TerraScaleClient,
};

const SLUG: &str = "acme";

#[derive(Debug, Default)]
struct Mock {
    requests: Mutex<Vec<Request>>,
    responses: Mutex<VecDeque<Response>>,
}

impl Mock {
    fn new() -> Arc<Self> {
        Arc::new(Self::default())
    }

    fn push(&self, response: Response) {
        self.responses
            .lock()
            .expect("response mutex")
            .push_back(response);
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
    async fn send(&self, request: Request) -> Result<Response> {
        self.requests.lock().expect("request mutex").push(request);
        self.responses
            .lock()
            .expect("response mutex")
            .pop_front()
            .ok_or_else(|| terrascale::Error::transport("mock ran out of responses"))
    }
}

fn client(mock: Arc<Mock>) -> TerraScaleClient {
    let transport: Arc<dyn HttpTransport> = mock;
    TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://api.test")
            .api_key("ts_test_key")
            .auth_base_url("https://auth.test")
            .auth_api_key("sk_test_key")
            .build()
            .expect("config"),
        transport,
    )
}

#[tokio::test]
async fn public_auth_origin_discovers_regions_and_fails_over_safe_reads() {
    let mock = Mock::new();
    mock.push(Response::json(
        200,
        &json!({
            "v": 1, "refreshSeconds": 60, "endpoints": [
                {"region": "SFO", "url": "https://sfo.auth.terrascale.tech"},
                {"region": "GRU", "url": "https://gru.auth.terrascale.tech"}
            ]
        }),
    ));
    mock.push(Response::json(
        503,
        &json!({"error": {"code": "busy", "message": "busy"}}),
    ));
    mock.push(Response::json(200, &json!({"data": []})));
    let sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://api.test")
            .auth_base_url("https://auth.terrascale.tech")
            .auth_api_key("sk_test_key")
            .build()
            .expect("config"),
        mock.clone(),
    );
    let auth = sdk.auth_idp(SLUG).expect("auth client");
    auth.users()
        .list(Some(20), None)
        .await
        .expect("regional read");
    assert_eq!(mock.count(), 3);
    assert_eq!(
        mock.request(0).url,
        "https://atlas.terrascale.tech/api/v1/auth/discovery"
    );
    assert!(header(&mock.request(0), "authorization").is_empty());
    assert!(mock
        .request(1)
        .url
        .starts_with("https://sfo.auth.terrascale.tech/"));
    assert!(mock
        .request(2)
        .url
        .starts_with("https://gru.auth.terrascale.tech/"));
    assert_eq!(
        header(&mock.request(1), "authorization"),
        ["Bearer sk_test_key"]
    );

    mock.push(Response::json(
        503,
        &json!({"error": {"code": "busy", "message": "busy"}}),
    ));
    assert!(auth.users().delete("usr_1").await.is_err());
    assert_eq!(mock.count(), 4);
    assert!(mock
        .request(3)
        .url
        .starts_with("https://sfo.auth.terrascale.tech/"));
}

#[tokio::test]
async fn public_auth_proxy_opt_in_skips_discovery() {
    let mock = Mock::new();
    mock.push(Response::json(200, &json!({"data": []})));
    let sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://api.test")
            .auth_base_url("https://auth.terrascale.tech")
            .auth_reverse_proxy(true)
            .auth_api_key("sk_test_key")
            .build()
            .expect("config"),
        mock.clone(),
    );
    sdk.auth_idp(SLUG)
        .expect("auth client")
        .users()
        .list(None, None)
        .await
        .expect("proxy read");
    assert_eq!(mock.count(), 1);
    assert!(mock
        .request(0)
        .url
        .starts_with("https://auth.terrascale.tech/"));
}

#[tokio::test]
async fn auth_discovery_rejects_a_mismatched_origin_before_credentials() {
    let mock = Mock::new();
    mock.push(Response::json(
        200,
        &json!({
            "v": 1, "refreshSeconds": 60,
            "endpoints": [{"region": "SFO", "url": "https://gru.auth.terrascale.tech"}]
        }),
    ));
    let sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://api.test")
            .auth_base_url("https://auth.terrascale.tech")
            .auth_api_key("sk_test_key")
            .build()
            .expect("config"),
        mock.clone(),
    );
    assert!(sdk
        .auth_idp(SLUG)
        .expect("auth client")
        .users()
        .list(None, None)
        .await
        .is_err());
    assert_eq!(mock.count(), 1);
    assert!(header(&mock.request(0), "authorization").is_empty());
}

fn retry_client(mock: Arc<Mock>) -> TerraScaleClient {
    let transport: Arc<dyn HttpTransport> = mock;
    TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://api.test")
            .api_key("ts_test_key")
            .auth_base_url("https://auth.test")
            .auth_api_key("sk_test_key")
            .max_retry_attempts(1)
            .retry_delay_ms(0)
            .build()
            .expect("config"),
        transport,
    )
}

fn header<'a>(request: &'a Request, name: &str) -> Vec<&'a str> {
    request
        .headers
        .iter()
        .filter(|(key, _)| key.eq_ignore_ascii_case(name))
        .map(|(_, value)| value.as_str())
        .collect()
}

fn body_json(request: &Request) -> Value {
    serde_json::from_slice(request.body.as_deref().unwrap_or_default()).expect("json body")
}

fn empty(status: u16) -> Response {
    Response {
        status,
        headers: Vec::new(),
        body: Vec::new().into(),
    }
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

#[tokio::test]
async fn management_routes_carry_the_ts_auth_bearer_and_accept_headers() {
    let mock = Mock::new();
    mock.push(Response::json(201, &user_json("user_1")));
    mock.push(Response::json(
        200,
        &json!({ "data": [user_json("user_2")] }),
    ));
    let sdk = client(Arc::clone(&mock));
    let auth = sdk.auth_idp(SLUG).expect("ts-auth client");

    auth.users()
        .create(CreateAuthIdpUser {
            email: Some("ada@lovelace.test".to_owned()),
            ..CreateAuthIdpUser::default()
        })
        .await
        .expect("created user");
    auth.users()
        .list(Some(2), Some("user_1"))
        .await
        .expect("page");

    let create = mock.request(0);
    assert_eq!(create.method, Method::Post);
    assert_eq!(create.url, "https://auth.test/v1/acme/users");
    assert_eq!(header(&create, "authorization"), ["Bearer sk_test_key"]);
    assert_eq!(header(&create, "accept"), ["application/json"]);
    assert_eq!(header(&create, "content-type"), ["application/json"]);
    assert_eq!(body_json(&create), json!({ "email": "ada@lovelace.test" }));

    let list = mock.request(1);
    assert_eq!(list.method, Method::Get);
    assert_eq!(
        list.url,
        "https://auth.test/v1/acme/users?limit=2&starting_after=user_1"
    );
    assert_eq!(header(&list, "authorization"), ["Bearer sk_test_key"]);
}

#[tokio::test]
async fn user_patch_is_tri_state_on_the_wire() {
    let mock = Mock::new();
    mock.push(Response::json(200, &user_json("user_1")));
    mock.push(Response::json(200, &user_json("user_1")));
    let sdk = client(Arc::clone(&mock));
    let users = sdk.auth_idp(SLUG).expect("ts-auth client").users();

    users
        .patch(
            "user_1",
            AuthIdpUserPatch::new()
                .with_name("Ada")
                .unset_ban_reason()
                .with_banned(true),
        )
        .await
        .expect("patched");
    users
        .patch("user_1", AuthIdpUserPatch::new().with_username("countess"))
        .await
        .expect("patched username");

    let tri_state = mock.request(0);
    assert_eq!(tri_state.method, Method::Patch);
    assert_eq!(tri_state.url, "https://auth.test/v1/acme/users/user_1");
    assert_eq!(
        body_json(&tri_state),
        json!({ "name": "Ada", "ban_reason": null, "banned": true })
    );

    let single = mock.request(1);
    assert_eq!(body_json(&single), json!({ "username": "countess" }));
}

#[tokio::test]
async fn decodes_snake_case_users_and_the_data_envelope() {
    let mock = Mock::new();
    mock.push(Response::json(200, &user_json("user_1")));
    mock.push(Response::json(
        200,
        &json!({ "data": [{
            "id": "key_1",
            "name": "CI",
            "scopes": ["users:read"],
            "created_at": 1_758_000_000_i64,
            "revoked": false
        }]}),
    ));
    let sdk = client(Arc::clone(&mock));
    let auth = sdk.auth_idp(SLUG).expect("ts-auth client");

    let user = auth.users().get("user_1").await.expect("user");
    assert_eq!(user.id, "user_1");
    assert_eq!(user.email.as_deref(), Some("ada@lovelace.test"));
    assert_eq!(user.email_verified, Some(true));
    assert!(user.ban_reason.is_none());
    assert_eq!(user.public_metadata, json!({ "tier": "gold" }));
    assert_eq!(user.private_metadata, Some(json!({ "flag": true })));

    let keys = auth.api_keys().list().await.expect("api keys");
    assert_eq!(keys.len(), 1);
    assert_eq!(keys[0].id, "key_1");
    assert_eq!(keys[0].scopes, ["users:read"]);
    assert!(!keys[0].revoked);
    assert_eq!(mock.request(1).url, "https://auth.test/v1/acme/api-keys");
}

#[tokio::test]
async fn decodes_camel_case_oauth_clients_and_passkeys() {
    let mock = Mock::new();
    mock.push(Response::json(
        200,
        &json!({
            "client": {
                "id": "clt_1",
                "clientId": "oc_1",
                "name": "Web",
                "redirectUris": ["https://app.test/cb"],
                "tokenEndpointAuthMethod": "client_secret_post",
                "public": false,
                "createdAt": 1_758_000_000_i64
            },
            "client_secret": "sec_one_time"
        }),
    ));
    mock.push(Response::json(
        200,
        &json!({ "clients": [{
            "id": "clt_2",
            "clientId": "oc_2",
            "name": "Mobile",
            "redirectUris": ["com.app:/cb"],
            "tokenEndpointAuthMethod": "none",
            "public": true,
            "createdAt": 1_758_000_000_i64
        }]}),
    ));
    mock.push(Response::json(
        200,
        &json!({ "passkeys": [{
            "id": "pk_1",
            "nickname": "YubiKey",
            "algorithm": "ES256",
            "createdAt": 1_758_000_000_i64,
            "lastUsedAt": 1_758_000_100_i64
        }]}),
    ));
    let sdk = client(Arc::clone(&mock));
    let auth = sdk.auth_idp(SLUG).expect("ts-auth client");

    let created = auth
        .oauth_clients()
        .create(CreateAuthIdpOauthClient::new(
            "Web",
            vec!["https://app.test/cb".to_owned()],
        ))
        .await
        .expect("created client");
    assert_eq!(created.client.id, "clt_1");
    assert_eq!(created.client.client_id, "oc_1");
    assert_eq!(created.client.redirect_uris, ["https://app.test/cb"]);
    assert_eq!(
        created.client.token_endpoint_auth_method,
        "client_secret_post"
    );
    assert_eq!(created.client.created_at, 1_758_000_000);
    assert_eq!(created.client_secret.as_deref(), Some("sec_one_time"));
    // One-time secrets never surface through Debug.
    assert!(!format!("{created:?}").contains("sec_one_time"));

    let clients = auth.oauth_clients().list().await.expect("clients");
    assert_eq!(clients.len(), 1);
    assert!(clients[0].public);

    let passkeys = auth
        .users()
        .list_passkeys("user_1")
        .await
        .expect("passkeys");
    assert_eq!(passkeys.len(), 1);
    assert_eq!(passkeys[0].nickname, "YubiKey");
    assert_eq!(passkeys[0].last_used_at, Some(1_758_000_100));
    assert_eq!(
        mock.request(2).url,
        "https://auth.test/v1/acme/users/user_1/passkeys"
    );
}

#[tokio::test]
async fn decodes_bare_array_families() {
    let mock = Mock::new();
    mock.push(Response::json(
        200,
        &json!([{ "id": "org_1", "name": "Acme", "slug": "acme", "created_at": 1_758_000_000_i64 }]),
    ));
    mock.push(Response::json(
        200,
        &json!([{
            "id": "jt_1",
            "name": "session",
            "claims": { "sub": "{{user.id}}" },
            "lifetime_seconds": 300,
            "created_at": 1_758_000_000_i64
        }]),
    ));
    mock.push(Response::json(
        200,
        &json!([{
            "id": "wh_1",
            "url": "https://hooks.test/deliver",
            "events": ["user.created", "org.*"],
            "enabled": true,
            "created_at": 1_758_000_000_i64
        }]),
    ));
    mock.push(Response::json(
        200,
        &json!([{
            "id": "evt_1",
            "instance_id": "inst_1",
            "type": "api_key.created",
            "actor_id": "key_1",
            "data": { "key": "key_1" },
            "created_at": 1_758_000_000_i64,
            "integrity_version": 7,
            "integrity_hash": "aa",
            "integrity_signature": "bb",
            "integrity_key_fingerprint": "cc"
        }]),
    ));
    let sdk = client(Arc::clone(&mock));
    let auth = sdk.auth_idp(SLUG).expect("ts-auth client");

    let orgs = auth.organizations().list().await.expect("orgs");
    let org = orgs.first().expect("one organization");
    assert_eq!(
        (org.id.as_str(), org.name.as_str(), org.slug.as_str()),
        ("org_1", "Acme", "acme")
    );
    assert_eq!(org.created_at, 1_758_000_000);

    let templates = auth.jwt_templates().list().await.expect("templates");
    assert_eq!(templates[0].claims.get("sub"), Some(&json!("{{user.id}}")));
    assert_eq!(templates[0].lifetime_seconds, 300);

    let webhooks = auth.webhooks().list().await.expect("webhooks");
    assert_eq!(webhooks[0].events, ["user.created", "org.*"]);
    assert!(webhooks[0].enabled);

    let audit = auth.audit_events().list(Some(50)).await.expect("audit");
    assert_eq!(audit[0].event_type, "api_key.created");
    assert_eq!(audit[0].actor_id.as_deref(), Some("key_1"));
    assert_eq!(audit[0].integrity_version, 7);
    assert_eq!(audit[0].integrity_signature.as_deref(), Some("bb"));
    assert_eq!(audit[0].integrity_key_fingerprint.as_deref(), Some("cc"));
    assert_eq!(
        mock.request(3).url,
        "https://auth.test/v1/acme/audit-events?limit=50"
    );
}

#[tokio::test]
async fn maps_nested_management_errors() {
    let mock = Mock::new();
    mock.push(Response::json(
        409,
        &json!({ "error": { "code": "email_taken", "message": "Email already taken." } }),
    ));
    let sdk = client(Arc::clone(&mock));

    let error = sdk
        .auth_idp(SLUG)
        .expect("ts-auth client")
        .users()
        .create(CreateAuthIdpUser {
            email: Some("ada@lovelace.test".to_owned()),
            ..CreateAuthIdpUser::default()
        })
        .await
        .expect_err("conflict");
    assert_eq!(error.kind, ErrorKind::Conflict);
    assert_eq!(error.code, "email_taken");
    assert_eq!(error.message, "Email already taken.");
    assert_eq!(error.status, Some(409));
}

#[tokio::test]
async fn maps_flat_oauth_token_errors() {
    let mock = Mock::new();
    mock.push(Response::json(
        401,
        &json!({ "error": "invalid_client", "error_description": "Client authentication failed." }),
    ));
    let sdk = client(Arc::clone(&mock));

    let error = sdk
        .auth_idp(SLUG)
        .expect("ts-auth client")
        .oidc()
        .token_client_credentials("oc_1", "wrong-secret", None)
        .await
        .expect_err("invalid client");
    assert_eq!(error.kind, ErrorKind::Authentication);
    assert_eq!(error.code, "invalid_client");
    assert_eq!(error.message, "Client authentication failed.");
    assert_eq!(error.status, Some(401));
}

#[tokio::test]
async fn retries_reads_but_never_mutations() {
    let mock = Mock::new();
    mock.push(empty(503));
    mock.push(Response::json(200, &json!({ "data": [] })));
    let sdk = retry_client(Arc::clone(&mock));
    sdk.auth_idp(SLUG)
        .expect("ts-auth client")
        .users()
        .list(None, None)
        .await
        .expect("second attempt succeeds");
    assert_eq!(mock.count(), 2);

    let mock = Mock::new();
    mock.push(empty(503));
    let sdk = retry_client(Arc::clone(&mock));
    let error = sdk
        .auth_idp(SLUG)
        .expect("ts-auth client")
        .api_keys()
        .create("CI", Some(&["users:read".to_owned()]))
        .await
        .expect_err("mutations never retry");
    assert_eq!(error.status, Some(503));
    assert_eq!(mock.count(), 1);
}

#[tokio::test]
async fn list_all_walks_the_id_cursor_to_a_short_page() {
    let mock = Mock::new();
    mock.push(Response::json(
        200,
        &json!({ "data": [user_json("user_1"), user_json("user_2")] }),
    ));
    mock.push(Response::json(
        200,
        &json!({ "data": [user_json("user_3")] }),
    ));
    let sdk = client(Arc::clone(&mock));

    let users = sdk
        .auth_idp(SLUG)
        .expect("ts-auth client")
        .users()
        .list_all(2)
        .await
        .expect("walked");
    assert_eq!(
        users
            .iter()
            .map(|user| user.id.as_str())
            .collect::<Vec<_>>(),
        ["user_1", "user_2", "user_3"]
    );
    // A short page is the last page; no third request is issued.
    assert_eq!(mock.count(), 2);
    assert_eq!(
        mock.request(1).url,
        "https://auth.test/v1/acme/users?limit=2&starting_after=user_2"
    );
}

#[tokio::test]
async fn client_idempotency_key_never_reaches_ts_auth_mutations() {
    let mock = Mock::new();
    mock.push(empty(503));
    let transport: Arc<dyn HttpTransport> = mock.clone();
    let sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://api.test")
            .api_key("ts_test_key")
            .auth_base_url("https://auth.test")
            .auth_api_key("sk_test_key")
            .max_retry_attempts(2)
            .retry_delay_ms(0)
            .build()
            .expect("config"),
        transport,
    )
    .with_idempotency_key("stable-key")
    .expect("idempotency key");

    let error = sdk
        .auth_idp(SLUG)
        .expect("ts-auth client")
        .users()
        .create(CreateAuthIdpUser::default())
        .await
        .expect_err("503 surfaces");
    assert_eq!(error.status, Some(503));
    assert_eq!(mock.count(), 1, "ts-auth mutations never retry");
    assert!(
        header(&mock.request(0), "idempotency-key").is_empty(),
        "the shared idempotency key must not leak to ts-auth requests"
    );
}

#[tokio::test]
async fn terrabase_headers_do_not_leak_to_ts_auth_requests() {
    let mock = Mock::new();
    mock.push(empty(503)); // TerraBase lease attempt
    mock.push(empty(503)); // ts-auth attempt
    let transport: Arc<dyn HttpTransport> = mock.clone();
    let sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://api.test")
            .api_key("ts_test_key")
            .auth_base_url("https://auth.test")
            .auth_api_key("sk_test_key")
            .tenant_id("tenant_1")
            .project_id("project_1")
            .preferred_region(RegionCode::Gru)
            .max_retry_attempts(0)
            .build()
            .expect("config"),
        transport,
    );

    // The same pipeline serves both services: a TerraBase route carries the
    // tenant, project, and preferred-region headers...
    let _ = sdk.tenants().authorization_lease("tenant_1").await;
    let terrabase = mock.request(0);
    assert_eq!(header(&terrabase, "x-terrascale-tenant-id"), ["tenant_1"]);
    assert_eq!(header(&terrabase, "x-terrascale-project-id"), ["project_1"]);
    assert_eq!(header(&terrabase, "x-terrascale-preferred-region"), ["GRU"]);

    // ...and the ts-auth route carries none of them.
    let _ = sdk
        .auth_idp(SLUG)
        .expect("ts-auth client")
        .users()
        .get("user_1")
        .await;
    let ts_auth = mock.request(1);
    assert!(header(&ts_auth, "x-terrascale-tenant-id").is_empty());
    assert!(header(&ts_auth, "x-terrascale-project-id").is_empty());
    assert!(header(&ts_auth, "x-terrascale-preferred-region").is_empty());
}

#[tokio::test]
async fn oidc_routes_are_public_and_post_exact_form_bodies() {
    let mock = Mock::new();
    mock.push(Response::json(
        200,
        &json!({
            "access_token": "at_1",
            "token_type": "Bearer",
            "expires_in": 3600,
            "scope": "openid",
            "id_token": "eyJhbGciOiJFUzI1NiJ9"
        }),
    ));
    mock.push(Response::json(
        200,
        &json!({
            "issuer": "https://auth.test/acme",
            "authorization_endpoint": "https://auth.test/acme/oauth/authorize",
            "token_endpoint": "https://auth.test/acme/oauth/token",
            "userinfo_endpoint": "https://auth.test/acme/oauth/userinfo",
            "jwks_uri": "https://auth.test/acme/.well-known/jwks.json",
            "revocation_endpoint": "https://auth.test/acme/oauth/revoke",
            "introspection_endpoint": "https://auth.test/acme/oauth/introspect",
            "end_session_endpoint": "https://auth.test/acme/logout",
            "response_types_supported": ["code"],
            "grant_types_supported": ["authorization_code", "refresh_token", "client_credentials"],
            "code_challenge_methods_supported": ["S256"],
            "subject_types_supported": ["public"],
            "id_token_signing_alg_values_supported": ["ES256"],
            "token_endpoint_auth_methods_supported": ["client_secret_post"],
            "scopes_supported": ["openid", "email", "profile"]
        }),
    ));
    mock.push(Response::json(
        200,
        &json!({ "keys": [{
            "kty": "EC",
            "crv": "P-256",
            "alg": "ES256",
            "use": "sig",
            "kid": "key-1",
            "x": "eXBl",
            "y": "eXBl"
        }]}),
    ));
    mock.push(empty(200));
    mock.push(Response::json(
        200,
        &json!({ "active": true, "sub": "user_1", "client_id": "oc_1" }),
    ));
    let sdk = client(Arc::clone(&mock));
    let oidc = sdk.auth_idp(SLUG).expect("ts-auth client").oidc();

    let token = oidc
        .token_authorization_code(
            "oc_1",
            "sec_1",
            "cd_1",
            "https://app.test/cb",
            Some("verifier-1"),
        )
        .await
        .expect("token");
    assert_eq!(token.token_type, "Bearer");
    assert_eq!(token.expires_in, 3600);
    assert_eq!(token.id_token.as_deref(), Some("eyJhbGciOiJFUzI1NiJ9"));
    assert!(token.refresh_token.is_none());

    let token_request = mock.request(0);
    assert_eq!(token_request.method, Method::Post);
    assert_eq!(token_request.url, "https://auth.test/acme/oauth/token");
    assert_eq!(
        token_request
            .headers
            .iter()
            .find(|(name, _)| name.eq_ignore_ascii_case("content-type"))
            .map(|(_, value)| value.as_str()),
        Some("application/x-www-form-urlencoded")
    );
    assert_eq!(
        token_request.body.as_deref(),
        Some(
            b"grant_type=authorization_code&client_id=oc_1&client_secret=sec_1&code=cd_1\
              &redirect_uri=https%3A%2F%2Fapp.test%2Fcb&code_verifier=verifier-1"
                .as_slice()
        )
    );

    let discovery = oidc.discovery().await.expect("discovery");
    assert_eq!(discovery.issuer, "https://auth.test/acme");
    assert_eq!(
        discovery.token_endpoint_auth_methods_supported,
        ["client_secret_post"]
    );
    let discovery_request = mock.request(1);
    assert_eq!(
        discovery_request.url,
        "https://auth.test/acme/.well-known/openid-configuration"
    );

    let jwks = oidc.jwks().await.expect("jwks");
    assert_eq!(jwks.keys[0].kid, "key-1");
    assert_eq!(jwks.keys[0].key_use, "sig");
    assert_eq!(
        mock.request(2).url,
        "https://auth.test/acme/.well-known/jwks.json"
    );

    oidc.revoke("oc_1", "sec_1", "at_1").await.expect("revoked");
    assert_eq!(mock.request(3).url, "https://auth.test/acme/oauth/revoke");
    assert_eq!(
        mock.request(3).body.as_deref(),
        Some(b"client_id=oc_1&client_secret=sec_1&token=at_1".as_slice()),
        "revoke must authenticate the client with client_secret_post fields"
    );

    let introspection = oidc
        .introspect("oc_1", "sec_1", "at_1")
        .await
        .expect("introspected");
    assert!(introspection.active);
    assert_eq!(introspection.sub.as_deref(), Some("user_1"));
    assert_eq!(
        mock.request(4).url,
        "https://auth.test/acme/oauth/introspect"
    );
    assert_eq!(
        mock.request(4).body.as_deref(),
        Some(b"client_id=oc_1&client_secret=sec_1&token=at_1".as_slice()),
        "introspect must authenticate the client with client_secret_post fields"
    );

    // Public OIDC routes carry no bearer credential and no skip marker.
    for index in 0..5 {
        let request = mock.request(index);
        assert!(
            header(&request, "authorization").is_empty(),
            "OIDC request {index} must not carry an Authorization header"
        );
        assert!(request
            .headers
            .iter()
            .all(|(name, _)| name != "x-terrascale-sdk-skip-auth"));
    }
}

#[test]
fn webhook_verifier_accepts_only_valid_deliveries() {
    // HMAC-SHA256 rebuilt here from RFC 2104 directly over sha2.
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

    let secret = "whsec_0123456789abcdef0123456789abcdef";
    let timestamp = "1758000000";
    let body = br#"{"id":"evt_1","type":"user.created","created_at":1758000000,"data":{"user_id":"user_1"}}"#;
    let message = [timestamp.as_bytes(), b".", body.as_slice()].concat();
    let signature = format!("sha256={}", hex(&hmac_sha256(secret.as_bytes(), &message)));

    assert!(verify_auth_idp_webhook(secret, timestamp, &signature, body));
    // Re-serialized JSON does not verify: compare the raw bytes.
    let tampered = br#"{"created_at":1758000000,"data":{"user_id":"user_1"},"id":"evt_1","type":"user.created"}"#;
    assert!(!verify_auth_idp_webhook(
        secret, timestamp, &signature, tampered
    ));
    assert!(!verify_auth_idp_webhook(
        "whsec_wrong",
        timestamp,
        &signature,
        body
    ));
    assert!(!verify_auth_idp_webhook(
        secret,
        "1758000001",
        &signature,
        body
    ));
    assert!(!verify_auth_idp_webhook(
        secret,
        timestamp,
        "sha256=zz",
        body
    ));
    assert!(!verify_auth_idp_webhook(
        secret,
        timestamp,
        &signature[..39],
        body
    ));

    let at = |seconds: u64| UNIX_EPOCH + Duration::from_secs(seconds);
    assert!(verify_auth_idp_webhook_with_tolerance(
        secret,
        timestamp,
        &signature,
        body,
        Duration::from_secs(300),
        at(1_758_000_200)
    ));
    assert!(!verify_auth_idp_webhook_with_tolerance(
        secret,
        timestamp,
        &signature,
        body,
        Duration::from_secs(300),
        at(1_758_003_600)
    ));
    assert!(!verify_auth_idp_webhook_with_tolerance(
        secret,
        "yesterday",
        &signature,
        body,
        Duration::from_secs(300),
        SystemTime::now()
    ));
}

#[tokio::test]
async fn validates_configuration_and_inputs_before_transport() {
    let mock = Mock::new();

    let plain = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://api.test")
            .api_key("ts_test_key")
            .build()
            .expect("config"),
        Arc::clone(&mock) as Arc<dyn HttpTransport>,
    );
    let error = plain.auth_idp(SLUG).expect_err("missing auth_base_url");
    assert_eq!(error.kind, ErrorKind::Validation);
    assert_eq!(error.code, "auth_base_url_not_configured");
    assert_eq!(error.message, "auth_base_url is not configured.");

    for slug in ["", "ac me", "ac/me", "ac\tme"] {
        let error = plain.auth_idp(slug).expect_err("bad slug");
        assert_eq!(error.kind, ErrorKind::Validation);
        assert_eq!(error.code, "invalid_instance_slug");
    }

    let sdk = client(Arc::clone(&mock));
    let auth = sdk.auth_idp(SLUG).expect("ts-auth client");

    let error = auth
        .users()
        .patch("user_1", AuthIdpUserPatch::new())
        .await
        .expect_err("empty patch");
    assert_eq!(error.kind, ErrorKind::Validation);
    assert_eq!(error.code, "invalid_user_patch");

    let error = auth
        .users()
        .list(Some(0), None)
        .await
        .expect_err("zero limit");
    assert_eq!(error.code, "invalid_limit");

    let error = auth
        .users()
        .patch("user_1", AuthIdpUserPatch::new().with_password("short"))
        .await
        .expect_err("short password");
    assert_eq!(error.code, "invalid_password");

    let no_key = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://api.test")
            .auth_base_url("https://auth.test")
            .build()
            .expect("config"),
        Arc::clone(&mock) as Arc<dyn HttpTransport>,
    );
    let error = no_key
        .auth_idp(SLUG)
        .expect("client builds without a management key")
        .users()
        .list(None, None)
        .await
        .expect_err("management needs the key");
    assert_eq!(error.kind, ErrorKind::Validation);
    assert_eq!(error.code, "auth_api_key_not_configured");

    assert_eq!(mock.count(), 0);
}
