use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use async_trait::async_trait;
use serde_json::{json, Value};
use terrascale::auth_idp::{
    verify_auth_idp_webhook_with_tolerance, AuthIdpOauthClientPatched, AuthIdpUserPatch,
    CreateAuthIdpOauthClient, CreateAuthIdpOauthResource, CreateAuthIdpUser,
    OidcClientAuthentication, OidcDeviceAuthorization, OidcPrincipalKind, OidcResourceCredentials,
    OidcResourceIntrospection, OidcTokenResponse, PatchAuthIdpOauthClient,
    PatchAuthIdpOauthResource,
};
use terrascale::{
    verify_auth_idp_webhook, ClientConfig, ErrorKind, HttpTransport, Method, Request, Response,
    Result, TerraScaleClient,
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
            .auth_base_url("https://auth.test")
            .auth_api_key("sk_test_key")
            .build()
            .expect("config"),
        transport,
    )
}

fn retry_client(mock: Arc<Mock>) -> TerraScaleClient {
    let transport: Arc<dyn HttpTransport> = mock;
    TerraScaleClient::with_transport(
        ClientConfig::builder()
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
async fn decodes_snake_case_oauth_clients_and_camel_case_passkeys() {
    let mock = Mock::new();
    mock.push(Response::json(
        200,
        &json!({
            "client": {
                "id": "clt_1",
                "client_id": "oc_1",
                "name": "Web",
                "redirect_uris": ["https://app.test/cb"],
                "token_endpoint_auth_method": "client_secret_post",
                "public": false,
                "created_at": 1_758_000_000_i64,
                "post_logout_redirect_uris": [],
                "allowed_origins": [],
                "allow_anonymous": false,
                "allowed_resource_audiences": [],
                "allowed_scopes": [],
                "allowed_grant_types": ["authorization_code"],
                "auth_epoch": 1,
                "enabled": true
            },
            "client_secret": "sec_one_time"
        }),
    ));
    mock.push(Response::json(
        200,
        &json!({ "clients": [{
            "id": "clt_2",
            "client_id": "oc_2",
            "name": "Mobile",
            "redirect_uris": ["com.app:/cb"],
            "token_endpoint_auth_method": "none",
            "public": true,
            "created_at": 1_758_000_000_i64,
                "post_logout_redirect_uris": [],
                "allowed_origins": [],
                "allow_anonymous": false,
                "allowed_resource_audiences": [],
                "allowed_scopes": [],
                "allowed_grant_types": ["authorization_code"],
                "auth_epoch": 1,
                "enabled": true
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
        .token_client_credentials(
            OidcClientAuthentication::Form {
                client_id: "oc_1",
                client_secret: "wrong-secret",
            },
            "https://api.test/documents",
            "documents:read",
        )
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
            .auth_base_url("https://auth.test")
            .auth_api_key("sk_test_key")
            .max_retry_attempts(2)
            .retry_delay_ms(0)
            .build()
            .expect("config"),
        transport,
    );

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
    mock.push(empty(503)); // ts-auth attempt
    let transport: Arc<dyn HttpTransport> = mock.clone();
    let sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .auth_base_url("https://auth.test")
            .auth_api_key("sk_test_key")
            .max_retry_attempts(0)
            .build()
            .expect("config"),
        transport,
    );

    let _ = sdk
        .auth_idp(SLUG)
        .expect("ts-auth client")
        .users()
        .get("user_1")
        .await;
    let ts_auth = mock.request(0);
    assert_eq!(header(&ts_auth, "authorization"), ["Bearer sk_test_key"]);
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
            OidcClientAuthentication::Form {
                client_id: "oc_1",
                client_secret: "sec_1",
            },
            "cd_1",
            "https://app.test/cb",
            Some("verifier-1"),
            None,
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
            b"grant_type=authorization_code&code=cd_1\
              &redirect_uri=https%3A%2F%2Fapp.test%2Fcb&code_verifier=verifier-1&client_id=oc_1&client_secret=sec_1"
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

    oidc.revoke(
        OidcClientAuthentication::Form {
            client_id: "oc_1",
            client_secret: "sec_1",
        },
        "at_1",
    )
    .await
    .expect("revoked");
    assert_eq!(mock.request(3).url, "https://auth.test/acme/oauth/revoke");
    assert_eq!(
        mock.request(3).body.as_deref(),
        Some(b"token=at_1&client_id=oc_1&client_secret=sec_1".as_slice()),
        "revoke must authenticate the client with client_secret_post fields"
    );

    let introspection = oidc
        .introspect(
            OidcClientAuthentication::Form {
                client_id: "oc_1",
                client_secret: "sec_1",
            },
            "at_1",
        )
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
        Some(b"token=at_1&client_id=oc_1&client_secret=sec_1".as_slice()),
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
        ClientConfig::builder().build().expect("config"),
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

// Accepted actual handler responses, extracted from raw_body, not fixture wrappers.
// Producer freeze 4104d8dbead261a440de72ee7265118681a78460b8edbedfa9e62563a152504b.
// client-create-1.json, wrapper SHA256 e97dbd898c14254f5394f634eb7ffea544441b09678c0f37fa506370999cc40a.
const CLIENT_CREATE: &str = r###"{"client":{"id":"fd07711b-4cdd-459a-9e4f-44d6e7ac17b8","client_id":"O7WimIKtyVxcispIMQ8HWpud","name":"Resource client","redirect_uris":["https://client.example.test/resource-callback"],"post_logout_redirect_uris":[],"token_endpoint_auth_method":"client_secret_basic","public":false,"allowed_origins":[],"allow_anonymous":false,"created_at":1790748356,"allowed_resource_audiences":["https://api.example.test/management","https://api.example.test/documents"],"allowed_scopes":["openid","email","profile","management:read","management:write"],"allowed_grant_types":["authorization_code","refresh_token","client_credentials","urn:ietf:params:oauth:grant-type:device_code"],"auth_epoch":1,"enabled":true},"client_secret":"CzWIzDGKooK1xwnJMItsLN6hvrrQaC6y8uso4f9Z"}"###;
// client-update-27.json, wrapper SHA256 5ce1ec1eadb8d1db6896610bede36dd144ec37b4d1f3aae796571c20f1e02390.
const CLIENT_PATCH: &str = r###"{"id":"fd07711b-4cdd-459a-9e4f-44d6e7ac17b8","client_id":"O7WimIKtyVxcispIMQ8HWpud","name":"Policy changed","redirect_uris":["https://client.example.test/resource-callback"],"post_logout_redirect_uris":[],"token_endpoint_auth_method":"client_secret_basic","public":false,"allowed_origins":[],"allow_anonymous":false,"created_at":1790748356,"allowed_resource_audiences":["https://api.example.test/management","https://api.example.test/documents"],"allowed_scopes":["openid","email","profile","management:read","management:write"],"allowed_grant_types":["authorization_code","refresh_token","client_credentials","urn:ietf:params:oauth:grant-type:device_code"],"auth_epoch":2,"enabled":true}"###;
// client-update-rotated-30.json, wrapper SHA256 2b451e77ff63379d7588ae9467a81728d2db5be2e7e96d6902e42a2a7bd4116d.
const CLIENT_ROTATE: &str = r###"{"client":{"id":"fd07711b-4cdd-459a-9e4f-44d6e7ac17b8","client_id":"O7WimIKtyVxcispIMQ8HWpud","name":"Policy changed","redirect_uris":["https://client.example.test/resource-callback"],"post_logout_redirect_uris":[],"token_endpoint_auth_method":"client_secret_basic","public":false,"allowed_origins":[],"allow_anonymous":false,"created_at":1790748356,"allowed_resource_audiences":["https://api.example.test/management","https://api.example.test/documents"],"allowed_scopes":["openid","email","profile","management:read","management:write"],"allowed_grant_types":["authorization_code","refresh_token","client_credentials","urn:ietf:params:oauth:grant-type:device_code"],"auth_epoch":3,"enabled":true},"client_secret":"yKuBDE28nGT7y9KSSiffwbL97DjPe8H0ZwPBss8Y"}"###;
// client-list-14.json, wrapper SHA256 ec133bb211b01581d580d1f5bc8c7f9c6ebfbd9f49ab9c91fe72295d7d84a550.
const CLIENT_LIST: &str = r###"{"clients":[{"id":"fd07711b-4cdd-459a-9e4f-44d6e7ac17b8","client_id":"O7WimIKtyVxcispIMQ8HWpud","name":"Resource client","redirect_uris":["https://client.example.test/resource-callback"],"post_logout_redirect_uris":[],"token_endpoint_auth_method":"client_secret_basic","public":false,"allowed_origins":[],"allow_anonymous":false,"created_at":1790748356,"allowed_resource_audiences":["https://api.example.test/management","https://api.example.test/documents"],"allowed_scopes":["openid","email","profile","management:read","management:write"],"allowed_grant_types":["authorization_code","refresh_token","client_credentials","urn:ietf:params:oauth:grant-type:device_code"],"auth_epoch":1,"enabled":true}]}"###;
// resource-create-6.json, wrapper SHA256 d19080007397af17e49c3d1ce8d1b47c7702ab7b83996028de50ea629ea3ffb0.
const RESOURCE_CREATE: &str = r###"{"resource":{"id":"296571fd-9b6f-4084-afa1-3ccfc7b2b191","audience":"https://api.example.test/management","allowed_client_ids":["O7WimIKtyVxcispIMQ8HWpud"],"allowed_grant_types":["authorization_code","refresh_token","client_credentials","urn:ietf:params:oauth:grant-type:device_code"],"allowed_scopes":["openid","email","profile","management:read","management:write"],"introspection_client_id":"4CXJ8Hobd6lNPWXeGNEntzTliAXZSXfD","token_endpoint_auth_method":"client_secret_basic","auth_epoch":1,"enabled":true,"created_at":1790748356,"updated_at":1790748356},"introspection_secret":"ymCNlaeMJQ6kBI7TeirFHYEMy9ba2fR3YhCg27zZ8eI6BPwi"}"###;
// resource-update-18.json, wrapper SHA256 a0f5389bbea608b258411b42a48df81ab99d02e6460790957cf067b6226acb73.
const RESOURCE_PATCH: &str = r###"{"resource":{"id":"296571fd-9b6f-4084-afa1-3ccfc7b2b191","audience":"https://api.example.test/management","allowed_client_ids":["O7WimIKtyVxcispIMQ8HWpud"],"allowed_grant_types":["authorization_code","refresh_token","client_credentials","urn:ietf:params:oauth:grant-type:device_code"],"allowed_scopes":["openid","email","profile","management:read","management:write"],"introspection_client_id":"4CXJ8Hobd6lNPWXeGNEntzTliAXZSXfD","token_endpoint_auth_method":"client_secret_basic","auth_epoch":3,"enabled":true,"created_at":1790748356,"updated_at":1790748356},"introspection_secret":null}"###;
// resource-list-32.json, wrapper SHA256 ec61391b91e0c7d5dbcb19eb983191d33c3ad1b9557a3bc50c84ee2a3f7def24.
const RESOURCE_LIST: &str = r###"{"resources":[{"id":"296571fd-9b6f-4084-afa1-3ccfc7b2b191","audience":"https://api.example.test/management","allowed_client_ids":["O7WimIKtyVxcispIMQ8HWpud"],"allowed_grant_types":["authorization_code","refresh_token","client_credentials","urn:ietf:params:oauth:grant-type:device_code"],"allowed_scopes":["openid","email","profile","management:read","management:write"],"introspection_client_id":"4CXJ8Hobd6lNPWXeGNEntzTliAXZSXfD","token_endpoint_auth_method":"client_secret_basic","auth_epoch":4,"enabled":false,"created_at":1790748356,"updated_at":1790748356},{"id":"652d3a14-bfef-4d2d-91e4-5926d1e613bb","audience":"https://api.example.test/documents","allowed_client_ids":["O7WimIKtyVxcispIMQ8HWpud"],"allowed_grant_types":["authorization_code","refresh_token","client_credentials","urn:ietf:params:oauth:grant-type:device_code"],"allowed_scopes":["openid","email","profile","management:read","management:write"],"introspection_client_id":"m5dk7618Y6pAIGwcynIa0bnRr6qrjl8E","token_endpoint_auth_method":"client_secret_basic","auth_epoch":1,"enabled":true,"created_at":1790748356,"updated_at":1790748356}]}"###;
// resource-introspection-17.json, wrapper SHA256 63a1d84472e9b6cb0b436f2d6e0b2eac4889cfc1545b0bd41395556268b23219.
const RESOURCE_USER: &str = r###"{"active":true,"token_type":"Bearer","iss":"http://localhost:36701/resources","sub":"ab0357f4-db70-4ddd-94fa-95255ceb3317","aud":"https://api.example.test/management","client_id":"JuDmfb6pJsPF8l97tTDUIEQr","scope":"openid management:read management:write","exp":1790751956,"iat":1790748356,"sid":"4fea65ec-3bff-408d-9b0e-b8ec2ab97af6","principal_kind":"user"}"###;
// resource-introspection-13.json, wrapper SHA256 e55cc799fa6a845bca14ee615ac05c3ec8e9302b2f0668e47de70a27e56c143f.
const RESOURCE_SERVICE: &str = r###"{"active":true,"token_type":"Bearer","iss":"http://localhost:43371/resources","sub":"RxrjGqh3AbqzSsmSOT8Xbwvy","aud":"https://api.example.test/management","client_id":"RxrjGqh3AbqzSsmSOT8Xbwvy","scope":"management:read","exp":1790751956,"iat":1790748356,"sid":null,"principal_kind":"service"}"###;
// resource-introspection-19.json, wrapper SHA256 e617e84fc11c7039529b177aae9c77e227c3209267fc6b6969064d9c70421a6a.
const RESOURCE_INACTIVE: &str = r###"{"active":false}"###;

fn raw_response(body: &str) -> Response {
    Response {
        status: 200,
        headers: vec![
            ("content-type".into(), "application/json".into()),
            ("cache-control".into(), "no-store".into()),
        ],
        body: body.to_owned().into(),
    }
}

#[tokio::test]
async fn resource_management_decodes_actual_policy_wire_and_checked_updates() {
    let mock = Mock::new();
    for body in [
        CLIENT_CREATE,
        CLIENT_LIST,
        CLIENT_PATCH,
        CLIENT_ROTATE,
        RESOURCE_CREATE,
        RESOURCE_LIST,
        RESOURCE_PATCH,
        RESOURCE_PATCH,
    ] {
        mock.push(raw_response(body));
    }
    mock.push(empty(204));
    let auth = client(mock.clone()).auth_idp(SLUG).expect("auth");
    let clients = auth.oauth_clients();
    let created = clients
        .create(CreateAuthIdpOauthClient::new(
            "Resource client",
            vec!["https://client.example.test/resource-callback".into()],
        ))
        .await
        .expect("actual create");
    assert_eq!(created.client.auth_epoch, 1);
    assert_eq!(created.client.allowed_resource_audiences.len(), 2);
    assert!(!format!("{created:?}").contains(created.client_secret.as_deref().expect("secret")));
    assert_eq!(clients.list().await.expect("actual list").len(), 1);
    let mut patch = PatchAuthIdpOauthClient::new(1);
    patch.name = Some("Policy changed".into());
    assert!(matches!(
        clients
            .patch(&created.client.id, patch)
            .await
            .expect("bare PATCH"),
        AuthIdpOauthClientPatched::Updated(_)
    ));
    let mut patch = PatchAuthIdpOauthClient::new(2);
    patch.rotate_secret = true;
    let rotated = clients
        .patch(&created.client.id, patch)
        .await
        .expect("rotation PATCH");
    let AuthIdpOauthClientPatched::Rotated(secret) = &rotated else {
        panic!("rotation envelope")
    };
    assert!(!format!("{rotated:?}").contains(&secret.client_secret));
    let resources = auth.oauth_resources();
    let resource = resources
        .create(CreateAuthIdpOauthResource {
            audience: "https://api.example.test/management".into(),
            allowed_client_ids: vec![created.client.client_id.clone()],
            allowed_grant_types: vec!["client_credentials".into()],
            allowed_scopes: vec!["management:read".into()],
        })
        .await
        .expect("resource create");
    assert_eq!(resource.resource.auth_epoch, 1);
    assert!(!format!("{resource:?}").contains(
        resource
            .introspection_secret
            .as_deref()
            .expect("resource secret")
    ));
    assert!(!resources.list().await.expect("resource list").is_empty());
    assert!(resources
        .patch(&resource.resource.id, PatchAuthIdpOauthResource::new(2))
        .await
        .expect("resource PATCH")
        .introspection_secret
        .is_none());
    resources
        .disable(&resource.resource.id, 3)
        .await
        .expect("checked disable");
    resources
        .delete(&resource.resource.id)
        .await
        .expect("provider DELETE");
    let body: Value =
        serde_json::from_slice(mock.request(7).body.as_deref().expect("PATCH body")).expect("json");
    assert_eq!(body["expected_auth_epoch"], 3);
    assert_eq!(body["enabled"], false);
    assert_eq!(mock.request(7).method, Method::Patch);
    assert_eq!(mock.request(8).method, Method::Delete);
    for index in 0..mock.count() {
        assert_eq!(
            header(&mock.request(index), "authorization"),
            ["Bearer sk_test_key"]
        );
    }
}

#[tokio::test]
async fn originating_roles_preserve_exact_resources_and_refresh_scope_presence() {
    use base64::Engine;
    let mock = Mock::new();
    let token = json!({"access_token":"at", "token_type":"Bearer", "expires_in":3600, "scope":""});
    for _ in 0..5 {
        mock.push(Response::json(200, &token));
    }
    mock.push(Response::json(200, &json!({"device_code":"device-secret", "user_code":"ABCD", "verification_uri":"https://auth.test/device", "verification_uri_complete":"https://auth.test/device?user_code=ABCD", "expires_in":600, "interval":5})));
    let oidc = client(mock.clone()).auth_idp(SLUG).expect("auth").oidc();
    let public = OidcClientAuthentication::None {
        client_id: "public-id",
    };
    let basic = OidcClientAuthentication::Basic {
        client_id: "id:+% ü",
        client_secret: " sec:+% ü ",
    };
    let resource = "https://API.example.test:443/a/../b?x=%2f&x=+";
    oidc.token_authorization_code(
        public,
        "code",
        "https://app.test/cb",
        Some("verifier"),
        Some(resource),
    )
    .await
    .expect("public code");
    oidc.token_refresh(basic, "refresh", None, None)
        .await
        .expect("inherit");
    assert_eq!(
        oidc.token_refresh(basic, "refresh", Some(resource), Some(""))
            .await
            .expect("narrow empty")
            .scope,
        ""
    );
    oidc.token_client_credentials(basic, resource, "documents:read")
        .await
        .expect("confidential service");
    oidc.token_device_code(public, "device-secret", None)
        .await
        .expect("device redemption");
    let device = oidc
        .device_authorization(public, Some(resource), Some("documents:read"))
        .await
        .expect("device request");
    assert!(!format!("{device:?}").contains("device-secret"));
    let body = String::from_utf8(mock.request(0).body.expect("code body")).expect("utf8");
    assert!(body.contains(
        "resource=https%3A%2F%2FAPI.example.test%3A443%2Fa%2F..%2Fb%3Fx%3D%252f%26x%3D%2B"
    ));
    assert!(body.contains("client_id=public-id"));
    assert!(!body.contains("client_secret"));
    let basic_request = mock.request(1);
    let authorization = header(&basic_request, "authorization");
    let decoded = base64::engine::general_purpose::STANDARD
        .decode(authorization[0].strip_prefix("Basic ").expect("Basic"))
        .expect("base64");
    assert_eq!(decoded, b"id%3A%2B%25%20%C3%BC:%20sec%3A%2B%25%20%C3%BC%20");
    let inherited = String::from_utf8(mock.request(1).body.expect("refresh body")).expect("utf8");
    assert!(!inherited.contains("client_secret"));
    assert!(!inherited.contains("scope="));
    assert!(!inherited.contains("resource="));
    let narrowed = String::from_utf8(mock.request(2).body.expect("refresh body")).expect("utf8");
    assert!(narrowed.ends_with("&scope="));
    assert_eq!(
        mock.request(5).url,
        "https://auth.test/acme/oauth/device_authorization"
    );
    assert!(header(&mock.request(0), "authorization").is_empty());
    for index in 0..mock.count() {
        let request = mock.request(index);
        assert!(!format!("{request:?}").contains("sec:+%"));
        assert!(!header(&request, "authorization")
            .iter()
            .any(|value| value.contains("sk_test") || value.contains("ts_test")));
    }
}

#[tokio::test]
async fn resource_introspection_decodes_actual_user_service_and_inactive_wire() {
    use base64::Engine;
    let mock = Mock::new();
    for raw in [RESOURCE_USER, RESOURCE_SERVICE, RESOURCE_INACTIVE] {
        mock.push(raw_response(raw));
    }
    let oidc = client(mock.clone()).auth_idp(SLUG).expect("auth").oidc();
    let credentials =
        OidcResourceCredentials::new("resource:id+", "resource:secret %").expect("credentials");
    assert!(!format!("{credentials:?}").contains("resource:secret"));
    let OidcResourceIntrospection::Active(user) = oidc
        .introspect_resource(&credentials, "at user+")
        .await
        .expect("actual user")
    else {
        panic!("active user")
    };
    assert_eq!(user.principal_kind, OidcPrincipalKind::User);
    assert!(user.sid.is_some());
    let OidcResourceIntrospection::Active(service) = oidc
        .introspect_resource(&credentials, "at_service")
        .await
        .expect("actual service")
    else {
        panic!("active service")
    };
    assert_eq!(service.principal_kind, OidcPrincipalKind::Service);
    assert!(service.sid.is_none());
    assert_eq!(service.sub, service.client_id);
    assert_eq!(
        oidc.introspect_resource(&credentials, "refresh_or_id")
            .await
            .expect("actual inactive"),
        OidcResourceIntrospection::Inactive
    );
    assert_eq!(mock.count(), 3);
    let request = mock.request(0);
    assert_eq!(
        request.body.as_deref(),
        Some(b"token=at%20user%2B".as_slice())
    );
    let authorization = header(&request, "authorization");
    assert_eq!(
        base64::engine::general_purpose::STANDARD
            .decode(authorization[0].strip_prefix("Basic ").expect("basic"))
            .expect("base64"),
        b"resource%3Aid%2B:resource%3Asecret%20%25"
    );
    assert_eq!(header(&request, "cache-control"), ["no-store"]);
}

// browser-code-token-18.json, actual wrapper SHA256 3f013d9a91b10840a80a54b11224228326d49ff0c129bc9377687aa83ba82c03.
const ACTUAL_BROWSER_CODE_TOKEN: &str = r###"{"access_token":"eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCIsImtpZCI6InNrZXlfMDhkMzYyYTI2ODBhNmM5ODQ3ODhkYzljZjU5MjNmYTkzZDJiNDJkNjMwZDEzODY5MmNmYTJkMGY5ZTkzM2M1MSJ9.eyJpc3MiOiJodHRwOi8vbG9jYWxob3N0OjQxMDM3L3Jlc291cmNlcyIsInN1YiI6IjMzOTcyNmRkLTYwNzItNGY5MC1hNjExLTUwMDY0YmYzOGQ3MiIsImF1ZCI6IjE3SGYySmpkT3V5SHN3MU5xNUY0T1hyVSIsImNsaWVudF9pZCI6IjE3SGYySmpkT3V5SHN3MU5xNUY0T1hyVSIsInNjb3BlIjoib3BlbmlkIGVtYWlsIiwidG9rZW5fdXNlIjoiYWNjZXNzIiwicHJpbmNpcGFsX2tpbmQiOiJ1c2VyIiwiZ3JhbnRfdHlwZSI6ImF1dGhvcml6YXRpb25fY29kZSIsImNsaWVudF9hdXRoX2Vwb2NoIjoxLCJyZXNvdXJjZV9hdXRoX2Vwb2NoIjpudWxsLCJzaWQiOiJmYjZiYjU3MC0zYzkyLTQ1ZjItYTY0OS0zZTg0OGE4NjYwNjIiLCJmYW1pbHlfaWQiOiJlYjk2NjQ2Ni00NWNiLTQ0NWMtYTA3ZS1jZGRmYWJlODBjODkiLCJhdXRoX2Vwb2NoIjozLCJpc19hbm9ueW1vdXMiOmZhbHNlLCJleHAiOjE3OTA3NTM5NDgsImlhdCI6MTc5MDc1MDM0OH0.w62tniXV47En_7lexXU5MAxx4c7PKzeN_cVR4TwTEVaBhGv-cs7z9iy0ArjdByocuN9fz2UaBIMH2bsZt_jEDQ","token_type":"Bearer","expires_in":3600,"scope":"openid email","refresh_token":"XODp5cb8NNmYhqBECdjUKyF70NRWJ9kORT4FezrVIIVuneTP","id_token":"eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCIsImtpZCI6InNrZXlfMDhkMzYyYTI2ODBhNmM5ODQ3ODhkYzljZjU5MjNmYTkzZDJiNDJkNjMwZDEzODY5MmNmYTJkMGY5ZTkzM2M1MSJ9.eyJpc3MiOiJodHRwOi8vbG9jYWxob3N0OjQxMDM3L3Jlc291cmNlcyIsInN1YiI6IjMzOTcyNmRkLTYwNzItNGY5MC1hNjExLTUwMDY0YmYzOGQ3MiIsImF1ZCI6IjE3SGYySmpkT3V5SHN3MU5xNUY0T1hyVSIsInRva2VuX3VzZSI6ImlkIiwiaXNfYW5vbnltb3VzIjpmYWxzZSwiYXRfaGFzaCI6InpuSVM1LVZ1WkxmaGtXaTVnY1JzSFEiLCJhdXRoX3RpbWUiOjE3OTA3NTAzNDcsInNpZCI6ImZiNmJiNTcwLTNjOTItNDVmMi1hNjQ5LTNlODQ4YTg2NjA2MiIsIm5vbmNlIjoicmVzb3VyY2Utbm9uY2UiLCJlbWFpbCI6InJlc291cmNlQGV4YW1wbGUudGVzdCIsImVtYWlsX3ZlcmlmaWVkIjp0cnVlLCJleHAiOjE3OTA3NTM5NDgsImlhdCI6MTc5MDc1MDM0OH0.YExFCWlfR7IXcY7tejFieKcqW1Wnj8QrVPEKN_drntf7D19hgyNygYrYWvMGvRkpA81MGp1lSkUkYYb6d00rqw"}"###;

// resource-device-approved-token-2.json, actual wrapper SHA256 ef2bebfb0d4ce802c4fa74b97b95c1826e2e9321362b0357b1b1d9fde060baa5.
const ACTUAL_RESOURCE_DEVICE_APPROVED_TOKEN: &str = r###"{"access_token":"eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCIsImtpZCI6InNrZXlfODk2NDIyYjFhYzM3Y2VhZTFjNDllNmM4MWEwZTY0ODg1MGEzMDNjNjNlMjJiM2I1OTcwYWM2MjM0OTlhNmFlNyJ9.eyJpc3MiOiJodHRwOi8vbG9jYWxob3N0OjQyODMzL2RldmljZSIsInN1YiI6ImRldmljZS11c2VyIiwiYXVkIjoiaHR0cHM6Ly9hcGkuZXhhbXBsZS50ZXN0L2RldmljZS1tYW5hZ2VtZW50IiwiY2xpZW50X2lkIjoidGVsZXZpc2lvbiIsInNjb3BlIjoib3BlbmlkIGVtYWlsIiwidG9rZW5fdXNlIjoiYWNjZXNzIiwicHJpbmNpcGFsX2tpbmQiOiJ1c2VyIiwiZ3JhbnRfdHlwZSI6InVybjppZXRmOnBhcmFtczpvYXV0aDpncmFudC10eXBlOmRldmljZV9jb2RlIiwiY2xpZW50X2F1dGhfZXBvY2giOjIsInJlc291cmNlX2F1dGhfZXBvY2giOjEsInNpZCI6ImRldmljZS1zZXNzaW9uIiwiZmFtaWx5X2lkIjoiYWZjMzk5OWMtMzMyYS00N2Q1LTg2MTQtMDIxNTlmNDkzMzlmIiwiYXV0aF9lcG9jaCI6MCwiaXNfYW5vbnltb3VzIjpmYWxzZSwiZXhwIjoxNzkwNzUzOTUzLCJpYXQiOjE3OTA3NTAzNTN9.-ZBSLw9XZvtswVD5DVMMP5eEgOM-WeLcPbwt8-6ntrjQPk7KSfFqzOH4ziSv782Ai5vQHxvXPj7Ny7aXfa9j9g","token_type":"Bearer","expires_in":3600,"scope":"openid email","refresh_token":"FkNqUtKKhiQ7rzkNfEYVlWdjVhZYgMdjJWZB0Gieg0GLVdBc","id_token":"eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCIsImtpZCI6InNrZXlfODk2NDIyYjFhYzM3Y2VhZTFjNDllNmM4MWEwZTY0ODg1MGEzMDNjNjNlMjJiM2I1OTcwYWM2MjM0OTlhNmFlNyJ9.eyJpc3MiOiJodHRwOi8vbG9jYWxob3N0OjQyODMzL2RldmljZSIsInN1YiI6ImRldmljZS11c2VyIiwiYXVkIjoidGVsZXZpc2lvbiIsInRva2VuX3VzZSI6ImlkIiwiaXNfYW5vbnltb3VzIjpmYWxzZSwiYXRfaGFzaCI6Ijc3WXpYaVJncE5wWVRwTVI2WHdOVEEiLCJhdXRoX3RpbWUiOjE3OTA3NTAzNTMsInNpZCI6ImRldmljZS1zZXNzaW9uIiwiZW1haWwiOiJkZXZpY2VAZXhhbXBsZS50ZXN0IiwiZW1haWxfdmVyaWZpZWQiOnRydWUsImV4cCI6MTc5MDc1Mzk1MywiaWF0IjoxNzkwNzUwMzUzfQ.WYFUagAX7tdAHV_Ps4NGOBiKx-YWvDvQU_3M2V8lQTwthJLYpXjxmDsrMEI73iC9gtEfQgk19f04-8r3RZtHiA"}"###;

// resource-device-start-1.json, actual wrapper SHA256 f8b67f1cb8b33984680fcd7ca7957c1edf58f1b882b6664cb8c12fb76a8596a0.
const ACTUAL_RESOURCE_DEVICE_START: &str = r###"{"device_code":"CWPEREC4GL.54jt5Ly4ylfLKz8aojGLXCWNLb6nv25cFuPa1I3JY6aNeoKr0Xmrr7m2NMTCOzZF","user_code":"CWPER-EC4GL","verification_uri":"http://localhost:42833/ui/device/device","verification_uri_complete":"http://localhost:42833/ui/device/device?user_code=CWPER-EC4GL","expires_in":600,"interval":5}"###;

// resource-refresh-narrowed-token-11.json, actual wrapper SHA256 4ddf4c11051843464de279e3bfd8e7b685f8544dcbd804d96cd271500040ed59.
const ACTUAL_RESOURCE_REFRESH_NARROWED_TOKEN: &str = r###"{"access_token":"eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCIsImtpZCI6InNrZXlfMDhkMzYyYTI2ODBhNmM5ODQ3ODhkYzljZjU5MjNmYTkzZDJiNDJkNjMwZDEzODY5MmNmYTJkMGY5ZTkzM2M1MSJ9.eyJpc3MiOiJodHRwOi8vbG9jYWxob3N0OjQxMDM3L3Jlc291cmNlcyIsInN1YiI6IjMzOTcyNmRkLTYwNzItNGY5MC1hNjExLTUwMDY0YmYzOGQ3MiIsImF1ZCI6Imh0dHBzOi8vYXBpLmV4YW1wbGUudGVzdC9tYW5hZ2VtZW50IiwiY2xpZW50X2lkIjoiMTdIZjJKamRPdXlIc3cxTnE1RjRPWHJVIiwic2NvcGUiOiJvcGVuaWQgbWFuYWdlbWVudDpyZWFkIiwidG9rZW5fdXNlIjoiYWNjZXNzIiwicHJpbmNpcGFsX2tpbmQiOiJ1c2VyIiwiZ3JhbnRfdHlwZSI6InJlZnJlc2hfdG9rZW4iLCJjbGllbnRfYXV0aF9lcG9jaCI6MSwicmVzb3VyY2VfYXV0aF9lcG9jaCI6MSwic2lkIjoiZmI2YmI1NzAtM2M5Mi00NWYyLWE2NDktM2U4NDhhODY2MDYyIiwiZmFtaWx5X2lkIjoiMTUxOGY3MWMtNTE0Ni00NjJlLWFiODAtZmMzMmU0YWUxYjVjIiwiYXV0aF9lcG9jaCI6MywiaXNfYW5vbnltb3VzIjpmYWxzZSwiZXhwIjoxNzkwNzUzOTQ3LCJpYXQiOjE3OTA3NTAzNDd9.V-py9psNwYlbXhQguIFspTjSNEdat4xantc6n-lFMGpXOpXQyUQhsE2pe6L_4tZz21d7KzQBJ3uYofD8Trqe8Q","token_type":"Bearer","expires_in":3600,"scope":"openid management:read","refresh_token":"nHUQe2WLeQGzpXt9DQA5ehPgJv1SRy2cNfAzlQD7P7EX9myo"}"###;

// resource-service-token-25.json, actual wrapper SHA256 d781a4e0102a936bfc65aa3701872501cd472833481720cdadff62a525cbbaa6.
const ACTUAL_RESOURCE_SERVICE_TOKEN: &str = r###"{"access_token":"eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCIsImtpZCI6InNrZXlfMzM5MWFmYWQ3YTViZGFkM2FjNzVjMmNmZWYwODU3MzJjN2I1NjZjN2M2OWU5ODc0N2M5YjhkZWY5ZGE1NDFhNSJ9.eyJpc3MiOiJodHRwOi8vbG9jYWxob3N0OjM2MTA3L3Jlc291cmNlcyIsInN1YiI6Ik03RW1rNzhRaXNjV2lPNUJtUE5Tc3NMbiIsImF1ZCI6Imh0dHBzOi8vYXBpLmV4YW1wbGUudGVzdC9tYW5hZ2VtZW50IiwiY2xpZW50X2lkIjoiTTdFbWs3OFFpc2NXaU81Qm1QTlNzc0xuIiwic2NvcGUiOiJtYW5hZ2VtZW50OnJlYWQiLCJ0b2tlbl91c2UiOiJhY2Nlc3MiLCJwcmluY2lwYWxfa2luZCI6InNlcnZpY2UiLCJncmFudF90eXBlIjoiY2xpZW50X2NyZWRlbnRpYWxzIiwiY2xpZW50X2F1dGhfZXBvY2giOjEsInJlc291cmNlX2F1dGhfZXBvY2giOjEsInNpZCI6bnVsbCwiZmFtaWx5X2lkIjpudWxsLCJleHAiOjE3OTA3NTM5NDgsImlhdCI6MTc5MDc1MDM0OH0.FJqam27V8oxIF1mmbhWRVPqyuMuBwywqQbY6zT2ra-gCC6FYZKXKJlPljbF6QYGKg-wBQLF3P5gnCowM37ArFg","token_type":"Bearer","expires_in":3600,"scope":"management:read"}"###;

// resource-user-code-token-4.json, actual wrapper SHA256 2c4f8e6bbfece3971a4c33b27d284d86ac411b2fbeebbb37ab677616b6ba133b.
const ACTUAL_RESOURCE_USER_CODE_TOKEN: &str = r###"{"access_token":"eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCIsImtpZCI6InNrZXlfMDhkMzYyYTI2ODBhNmM5ODQ3ODhkYzljZjU5MjNmYTkzZDJiNDJkNjMwZDEzODY5MmNmYTJkMGY5ZTkzM2M1MSJ9.eyJpc3MiOiJodHRwOi8vbG9jYWxob3N0OjQxMDM3L3Jlc291cmNlcyIsInN1YiI6IjMzOTcyNmRkLTYwNzItNGY5MC1hNjExLTUwMDY0YmYzOGQ3MiIsImF1ZCI6Imh0dHBzOi8vYXBpLmV4YW1wbGUudGVzdC9tYW5hZ2VtZW50IiwiY2xpZW50X2lkIjoiMTdIZjJKamRPdXlIc3cxTnE1RjRPWHJVIiwic2NvcGUiOiJvcGVuaWQgbWFuYWdlbWVudDpyZWFkIG1hbmFnZW1lbnQ6d3JpdGUiLCJ0b2tlbl91c2UiOiJhY2Nlc3MiLCJwcmluY2lwYWxfa2luZCI6InVzZXIiLCJncmFudF90eXBlIjoiYXV0aG9yaXphdGlvbl9jb2RlIiwiY2xpZW50X2F1dGhfZXBvY2giOjEsInJlc291cmNlX2F1dGhfZXBvY2giOjEsInNpZCI6ImZiNmJiNTcwLTNjOTItNDVmMi1hNjQ5LTNlODQ4YTg2NjA2MiIsImZhbWlseV9pZCI6IjE1MThmNzFjLTUxNDYtNDYyZS1hYjgwLWZjMzJlNGFlMWI1YyIsImF1dGhfZXBvY2giOjMsImlzX2Fub255bW91cyI6ZmFsc2UsImV4cCI6MTc5MDc1Mzk0NywiaWF0IjoxNzkwNzUwMzQ3fQ.k5Lsw_Y_k-KRXSSW9WFOb-rc_WSdKOFM1Vk5RcxJ0LhN3EdJ3Lm2DkEO4Oziydj60bfOdY6pIwb7apF6SdBfKw","token_type":"Bearer","expires_in":3600,"scope":"openid management:read management:write","refresh_token":"l7W3WakW3omM4ljMZrc0ni7dCySye03CgcEH1IWXhsHm2f8s","id_token":"eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCIsImtpZCI6InNrZXlfMDhkMzYyYTI2ODBhNmM5ODQ3ODhkYzljZjU5MjNmYTkzZDJiNDJkNjMwZDEzODY5MmNmYTJkMGY5ZTkzM2M1MSJ9.eyJpc3MiOiJodHRwOi8vbG9jYWxob3N0OjQxMDM3L3Jlc291cmNlcyIsInN1YiI6IjMzOTcyNmRkLTYwNzItNGY5MC1hNjExLTUwMDY0YmYzOGQ3MiIsImF1ZCI6IjE3SGYySmpkT3V5SHN3MU5xNUY0T1hyVSIsInRva2VuX3VzZSI6ImlkIiwiaXNfYW5vbnltb3VzIjpmYWxzZSwiYXRfaGFzaCI6IlBkYThXQ2R0LVk0VWxhTV9SNnBUWXciLCJhdXRoX3RpbWUiOjE3OTA3NTAzNDcsInNpZCI6ImZiNmJiNTcwLTNjOTItNDVmMi1hNjQ5LTNlODQ4YTg2NjA2MiIsIm5vbmNlIjoicmVzb3VyY2Utbm9uY2UiLCJleHAiOjE3OTA3NTM5NDcsImlhdCI6MTc5MDc1MDM0N30.N3-Rgi-XCGs9yguAmxC42gvTH_NfOcaCDg7P-UPDgy5fwhHcp4OkIdzhBaNYUleAD67NKOK07dHuCbM0RqJnRQ"}"###;

#[test]
fn actual_accepted_token_and_device_successes_decode_without_invented_optional_fields() {
    for (raw, has_refresh, has_id) in [
        (ACTUAL_RESOURCE_USER_CODE_TOKEN, true, true),
        (ACTUAL_RESOURCE_REFRESH_NARROWED_TOKEN, true, false),
        (ACTUAL_RESOURCE_SERVICE_TOKEN, false, false),
        (ACTUAL_BROWSER_CODE_TOKEN, true, true),
        (ACTUAL_RESOURCE_DEVICE_APPROVED_TOKEN, true, true),
    ] {
        let token: OidcTokenResponse = serde_json::from_str(raw).expect("actual accepted token");
        assert_eq!(token.token_type, "Bearer");
        assert!(token.expires_in > 0);
        assert_eq!(token.refresh_token.is_some(), has_refresh);
        assert_eq!(token.id_token.is_some(), has_id);
        assert!(!format!("{token:?}").contains(&token.access_token));
        let mut missing_scope: Value = serde_json::from_str(raw).expect("json");
        missing_scope
            .as_object_mut()
            .expect("object")
            .remove("scope");
        assert!(serde_json::from_value::<OidcTokenResponse>(missing_scope).is_err());
    }
    let device: OidcDeviceAuthorization =
        serde_json::from_str(ACTUAL_RESOURCE_DEVICE_START).expect("actual device");
    assert!(device.expires_in > 0);
    assert!(device.interval > 0);
    assert!(!format!("{device:?}").contains(&device.device_code));
}
