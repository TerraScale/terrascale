use auth_app::{
    AuthOptions,
    app::{AppState, build_router, build_state_with_mode},
};
use auth_contracts::models::{
    AuthorizationCode, Connection, MfaFactor, OAuthClient, RefreshToken, ScimDirectory,
    ScimDirectoryStatus, ScimUserLink, User, VerificationToken,
};
use auth_security::tokens::sha256_hex;
use axum::http::header;
use reqwest::{Client, StatusCode};
use serde_json::{Value, json};
use std::time::{SystemTime, UNIX_EPOCH};
use tokio::{net::TcpListener, task::JoinHandle};

mod support;

use support::terrabase_http::{TEST_TERRASCALE_API_KEY, TerraBaseHttpFixture};

struct TestServer {
    origin: String,
    state: AppState,
    task: JoinHandle<()>,
    _storage: TerraBaseHttpFixture,
}

impl TestServer {
    async fn start() -> Self {
        let storage = TerraBaseHttpFixture::start().await;
        let listener = TcpListener::bind("127.0.0.1:0")
            .await
            .expect("test app listener should bind");
        let address = listener
            .local_addr()
            .expect("test app listener should have an address");
        static NEXT_DATABASE: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1);
        let database = format!(
            "ts-auth-federation-{}",
            NEXT_DATABASE.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
        );
        let options = AuthOptions {
            listen: "http://127.0.0.1:0".into(),
            public_origin: format!("http://localhost:{}", address.port()),
            terrascale_api_url: storage.origin.clone(),
            terrascale_api_key: TEST_TERRASCALE_API_KEY.into(),
            terrascale_project_id: None,
            terrascale_tenant_id: None,
            database,
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
        let state = build_state_with_mode(options, true).expect("test state should build");
        let router = build_router(state.clone());
        let task = tokio::spawn(async move {
            axum::serve(listener, router)
                .await
                .expect("test app should remain available");
        });
        Self {
            origin: format!("http://{address}"),
            state,
            task,
            _storage: storage,
        }
    }

    fn client(&self) -> Client {
        Client::builder()
            .redirect(reqwest::redirect::Policy::none())
            .build()
            .expect("test client should build")
    }
}

impl Drop for TestServer {
    fn drop(&mut self) {
        self.task.abort();
    }
}

fn now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("system time should be valid")
        .as_secs() as i64
}

async fn create_instance(server: &TestServer, client: &Client, slug: &str) -> Value {
    let response = client
        .post(format!("{}/v1/instances", server.origin))
        .bearer_auth("test-only-operator-key")
        .header("Idempotency-Key", format!("federation-{slug}"))
        .json(&json!({ "slug": slug, "name": format!("{slug} test") }))
        .send()
        .await
        .expect("instance creation should reach the service");
    assert_eq!(response.status(), StatusCode::OK);
    response
        .json()
        .await
        .expect("instance response should be JSON")
}

#[tokio::test]
async fn refresh_token_replay_revokes_the_family_over_http() {
    let server = TestServer::start().await;
    let client = server.client();
    let instance =
        create_instance(&server, &client, "refresh-replay-test").await["instance"].clone();
    let instance_id = instance["id"].as_str().expect("instance ID is returned");
    let slug = instance["slug"]
        .as_str()
        .expect("instance slug is returned");
    let timestamp = now();
    let client_id = "public-refresh-client";
    let user_id = "usr_refresh_replay";
    let family_id = "rtf_refresh_replay";
    let raw_refresh = "initial-refresh-token-for-replay-test";

    server
        .state
        .store
        .create(
            "users",
            user_id,
            &json!({
                "id": user_id,
                "instanceId": instance_id,
                "banned": false,
                "deleted": false,
                "createdAt": timestamp,
                "updatedAt": timestamp,
            }),
        )
        .await
        .expect("active user should be seedable");
    server
        .state
        .store
        .create_oauth_client(&OAuthClient {
            id: "cli_refresh_replay".to_owned(),
            instance_id: instance_id.to_owned(),
            client_id: client_id.to_owned(),
            client_key: format!("{instance_id}:{client_id}"),
            name: "Refresh replay acceptance client".to_owned(),
            client_secret_hash: None,
            redirect_uris: Vec::new(),
            allowed_origins: Vec::new(),
            token_endpoint_auth_method: "none".to_owned(),
            r#public: true,
            created_at: timestamp,
        })
        .await
        .expect("public client should be seedable");
    server
        .state
        .store
        .create_refresh_token(&RefreshToken {
            id: "rtk_refresh_replay_initial".to_owned(),
            instance_id: instance_id.to_owned(),
            client_id: client_id.to_owned(),
            user_id: user_id.to_owned(),
            user_auth_epoch: 0,
            token_key: sha256_hex(raw_refresh),
            status: "active".to_owned(),
            scopes: vec!["openid".to_owned(), "email".to_owned()],
            created_at: timestamp,
            expires_at: timestamp + 3_600,
            rotated_to: None,
            family_id: family_id.to_owned(),
        })
        .await
        .expect("initial refresh token should be seedable");

    let token_endpoint = format!("{}/{slug}/oauth/token", server.origin);
    let first_rotation = client
        .post(&token_endpoint)
        .form(&[
            ("grant_type", "refresh_token"),
            ("client_id", client_id),
            ("refresh_token", raw_refresh),
        ])
        .send()
        .await
        .expect("refresh exchange should reach the service");
    assert_eq!(first_rotation.status(), StatusCode::OK);
    let tokens: Value = first_rotation
        .json()
        .await
        .expect("refresh response should be JSON");
    let rotated_refresh = tokens["refresh_token"]
        .as_str()
        .expect("rotation should issue a successor refresh token");
    assert_ne!(rotated_refresh, raw_refresh);
    assert!(
        tokens["access_token"]
            .as_str()
            .is_some_and(|value| !value.is_empty()),
        "rotation should issue an access token"
    );

    let replay = client
        .post(&token_endpoint)
        .form(&[
            ("grant_type", "refresh_token"),
            ("client_id", client_id),
            ("refresh_token", raw_refresh),
        ])
        .send()
        .await
        .expect("refresh replay should reach the service");
    assert_eq!(replay.status(), StatusCode::BAD_REQUEST);
    let replay_body: Value = replay.json().await.expect("replay error should be JSON");
    assert_eq!(replay_body["error"], "invalid_grant");

    assert!(
        server
            .state
            .store
            .is_refresh_family_revoked(instance_id, client_id, family_id)
            .await
            .expect("family status should be readable"),
        "replaying a rotated token should durably revoke its family"
    );
    let introspection = client
        .post(format!("{}/{slug}/oauth/introspect", server.origin))
        .form(&[("client_id", client_id), ("token", rotated_refresh)])
        .send()
        .await
        .expect("refresh introspection should reach the service");
    assert_eq!(introspection.status(), StatusCode::OK);
    let introspection: Value = introspection
        .json()
        .await
        .expect("introspection result should be JSON");
    assert_eq!(introspection["active"], false);

    let successor_exchange = client
        .post(&token_endpoint)
        .form(&[
            ("grant_type", "refresh_token"),
            ("client_id", client_id),
            ("refresh_token", rotated_refresh),
        ])
        .send()
        .await
        .expect("successor exchange should reach the service");
    assert_eq!(successor_exchange.status(), StatusCode::BAD_REQUEST);
    let successor_body: Value = successor_exchange
        .json()
        .await
        .expect("successor error should be JSON");
    assert_eq!(successor_body["error"], "invalid_grant");
}

#[tokio::test]
async fn password_reset_invalidates_oauth_refresh_tokens_over_http() {
    let server = TestServer::start().await;
    let client = server.client();
    let instance =
        create_instance(&server, &client, "password-reset-token-epoch-test").await["instance"]
            .clone();
    let instance_id = instance["id"].as_str().expect("instance ID is returned");
    let slug = instance["slug"]
        .as_str()
        .expect("instance slug is returned");
    let timestamp = now();
    let user_id = "usr_password_reset_epoch";
    let client_id = "password-reset-epoch-client";
    let client_secret = "password-reset-epoch-secret";
    let redirect_uri = "https://client.example.test/callback";
    let raw_code = "password-reset-epoch-code";
    let unused_code = "password-reset-epoch-unused-code";
    let reset_token = "password-reset-epoch-link-token";
    let email = "password-reset-epoch@example.test";

    server
        .state
        .store
        .create(
            "users",
            user_id,
            &User {
                id: user_id.to_owned(),
                instance_id: instance_id.to_owned(),
                email: Some(email.to_owned()),
                email_verified: true,
                identity_established: true,
                phone: None,
                username: None,
                password_hash: None,
                auth_epoch: 0,
                name: Some("Password Reset Epoch".to_owned()),
                image_url: None,
                banned: false,
                ban_reason: None,
                deleted: false,
                scim_provisioning: None,
                scim_instance_wide_directory_ids: Vec::new(),
                public_metadata: Default::default(),
                private_metadata: Default::default(),
                quota_seat: None,
                created_at: timestamp,
                updated_at: timestamp,
                last_sign_in_at: None,
                email_key: None,
                username_key: None,
            },
        )
        .await
        .expect("user should be seedable");
    server
        .state
        .store
        .create_oauth_client(&OAuthClient {
            id: "cli_password_reset_epoch".to_owned(),
            instance_id: instance_id.to_owned(),
            client_id: client_id.to_owned(),
            client_key: format!("{instance_id}:{client_id}"),
            name: "Password reset epoch acceptance client".to_owned(),
            client_secret_hash: Some(sha256_hex(client_secret)),
            redirect_uris: vec![redirect_uri.to_owned()],
            allowed_origins: Vec::new(),
            token_endpoint_auth_method: "client_secret_post".to_owned(),
            r#public: false,
            created_at: timestamp,
        })
        .await
        .expect("OAuth client should be seedable");
    server
        .state
        .store
        .create_authorization_code(&AuthorizationCode {
            id: "oauth-code-password-reset-epoch".to_owned(),
            instance_id: instance_id.to_owned(),
            client_id: client_id.to_owned(),
            user_id: user_id.to_owned(),
            user_auth_epoch: 0,
            code_key: sha256_hex(raw_code),
            redirect_uri: redirect_uri.to_owned(),
            scopes: vec!["openid".to_owned(), "email".to_owned()],
            code_challenge: None,
            code_challenge_method: None,
            nonce: None,
            expires_at: timestamp + 3_600,
            consumed: false,
        })
        .await
        .expect("authorization code should be seedable");
    server
        .state
        .store
        .create_authorization_code(&AuthorizationCode {
            id: "oauth-code-password-reset-epoch-unused".to_owned(),
            instance_id: instance_id.to_owned(),
            client_id: client_id.to_owned(),
            user_id: user_id.to_owned(),
            user_auth_epoch: 0,
            code_key: sha256_hex(unused_code),
            redirect_uri: redirect_uri.to_owned(),
            scopes: vec!["openid".to_owned()],
            code_challenge: None,
            code_challenge_method: None,
            nonce: None,
            expires_at: timestamp + 3_600,
            consumed: false,
        })
        .await
        .expect("unused authorization code should be seedable");

    let token_endpoint = format!("{}/{slug}/oauth/token", server.origin);
    let token_response = client
        .post(&token_endpoint)
        .form(&[
            ("grant_type", "authorization_code"),
            ("client_id", client_id),
            ("client_secret", client_secret),
            ("code", raw_code),
            ("redirect_uri", redirect_uri),
        ])
        .send()
        .await
        .expect("authorization-code exchange should reach the service");
    assert_eq!(token_response.status(), StatusCode::OK);
    let issued: Value = token_response
        .json()
        .await
        .expect("token response should be JSON");
    let refresh_token = issued["refresh_token"]
        .as_str()
        .expect("authorization-code exchange should issue a refresh token");
    let access_token = issued["access_token"]
        .as_str()
        .expect("authorization-code exchange should issue an access token");

    server
        .state
        .store
        .create_verification_token(&VerificationToken {
            id: "vrf_password_reset_epoch".to_owned(),
            instance_id: instance_id.to_owned(),
            user_id: Some(user_id.to_owned()),
            purpose: "reset_password".to_owned(),
            authentication_method: None,
            authentication_next: None,
            user_auth_epoch: Some(0),
            token_key: sha256_hex(reset_token),
            expires_at: timestamp + 1_800,
            consumed: false,
            failed_attempts: 0,
            created_at: timestamp,
            email_snapshot: Some(email.to_owned()),
        })
        .await
        .expect("password-reset token should be seedable");
    let reset = client
        .post(format!("{}/ui/{slug}/reset-password", server.origin))
        .header(header::ORIGIN, server.state.options.public_origin.as_str())
        .form(&[("token", reset_token), ("password", "a-new-password-123")])
        .send()
        .await
        .expect("password reset should reach the service");
    assert_eq!(reset.status(), StatusCode::OK);

    let user = server
        .state
        .store
        .get_user(user_id)
        .await
        .expect("reset user should be readable")
        .expect("reset user should remain present");
    assert_eq!(user.auth_epoch, 1, "reset increments the auth epoch");

    let rejected_refresh = client
        .post(&token_endpoint)
        .form(&[
            ("grant_type", "refresh_token"),
            ("client_id", client_id),
            ("client_secret", client_secret),
            ("refresh_token", refresh_token),
        ])
        .send()
        .await
        .expect("refresh exchange should reach the service");
    assert_eq!(rejected_refresh.status(), StatusCode::BAD_REQUEST);
    assert_eq!(
        rejected_refresh.json::<Value>().await.unwrap()["error"],
        "invalid_grant"
    );

    let introspection = client
        .post(format!("{}/{slug}/oauth/introspect", server.origin))
        .form(&[
            ("client_id", client_id),
            ("client_secret", client_secret),
            ("token", refresh_token),
        ])
        .send()
        .await
        .expect("refresh introspection should reach the service");
    assert_eq!(introspection.status(), StatusCode::OK);
    assert_eq!(
        introspection.json::<Value>().await.unwrap()["active"],
        false,
        "a refresh token minted before password reset is inactive"
    );
    let access_introspection = client
        .post(format!("{}/{slug}/oauth/introspect", server.origin))
        .form(&[
            ("client_id", client_id),
            ("client_secret", client_secret),
            ("token", access_token),
        ])
        .send()
        .await
        .expect("access-token introspection should reach the service");
    assert_eq!(access_introspection.status(), StatusCode::OK);
    assert_eq!(
        access_introspection.json::<Value>().await.unwrap()["active"],
        false,
        "an access token minted before password reset is inactive"
    );
    let userinfo = client
        .get(format!("{}/{slug}/oauth/userinfo", server.origin))
        .bearer_auth(access_token)
        .send()
        .await
        .expect("userinfo should reach the service");
    assert_eq!(userinfo.status(), StatusCode::UNAUTHORIZED);

    let stale_code = client
        .post(&token_endpoint)
        .form(&[
            ("grant_type", "authorization_code"),
            ("client_id", client_id),
            ("client_secret", client_secret),
            ("code", unused_code),
            ("redirect_uri", redirect_uri),
        ])
        .send()
        .await
        .expect("stale authorization-code exchange should reach the service");
    assert_eq!(stale_code.status(), StatusCode::BAD_REQUEST);
    assert_eq!(
        stale_code.json::<Value>().await.unwrap()["error"],
        "invalid_grant",
        "an authorization code minted before password reset cannot be exchanged"
    );
}

#[tokio::test]
async fn federated_first_factor_waits_for_local_mfa_and_persists_continuation() {
    let server = TestServer::start().await;
    let client = server.client();
    let instance =
        create_instance(&server, &client, "federated-first-factor-mfa-test").await["instance"]
            .clone();
    let instance_id = instance["id"].as_str().expect("instance ID is returned");
    let slug = instance["slug"]
        .as_str()
        .expect("instance slug is returned");
    let user_id = "usr_federated_first_factor_mfa";
    let email = "federated-mfa@example.test";
    let timestamp = now();
    let user = User {
        id: user_id.to_owned(),
        instance_id: instance_id.to_owned(),
        email: Some(email.to_owned()),
        email_verified: true,
        identity_established: true,
        phone: None,
        username: None,
        password_hash: None,
        auth_epoch: 0,
        name: Some("Federated MFA User".to_owned()),
        image_url: None,
        banned: false,
        ban_reason: None,
        deleted: false,
        scim_provisioning: None,
        scim_instance_wide_directory_ids: Vec::new(),
        public_metadata: Default::default(),
        private_metadata: Default::default(),
        quota_seat: None,
        created_at: timestamp,
        updated_at: timestamp,
        last_sign_in_at: None,
        email_key: None,
        username_key: None,
    };
    server
        .state
        .store
        .create("users", user_id, &user)
        .await
        .expect("verified user should be seedable");
    server
        .state
        .store
        .create_mfa_factor(&MfaFactor {
            id: "mfa_federated_first_factor".to_owned(),
            instance_id: instance_id.to_owned(),
            user_id: user_id.to_owned(),
            r#type: "totp".to_owned(),
            secret_box: Some("unused-test-factor-secret".to_owned()),
            recovery_codes: None,
            confirmed: true,
            created_at: timestamp,
            last_used_totp_step: None,
        })
        .await
        .expect("confirmed TOTP factor should be seedable");
    let instance = server
        .state
        .store
        .find_instance_by_slug(slug)
        .await
        .expect("instance should be readable")
        .expect("instance should exist");
    let continuation = format!("/{slug}/oauth/authorize?state=rp-state&code_challenge=rp-pkce");

    let outcome = auth_app::features::users::complete_first_factor(
        &server.state,
        &instance,
        &user,
        "oidc:con_federated_test",
        Some("federation-acceptance-test"),
        Some(email),
        Some(&continuation),
    )
    .await
    .expect("verified federated identity should begin the local MFA step");
    let auth_app::features::users::FirstFactorOutcome::MfaPending { token } = outcome else {
        panic!("a federated first factor must not establish a session before MFA");
    };
    let pending = server
        .state
        .store
        .find_verification_token(&sha256_hex(&token))
        .await
        .expect("MFA challenge should be readable")
        .expect("MFA challenge should be persisted");
    assert_eq!(pending.purpose, "mfa.pending");
    assert_eq!(
        pending.authentication_method.as_deref(),
        Some("oidc:con_federated_test")
    );
    assert_eq!(
        pending.authentication_next.as_deref(),
        Some(continuation.as_str())
    );
    assert_eq!(pending.email_snapshot.as_deref(), Some(email));
    assert_eq!(pending.user_auth_epoch, Some(0));
}

#[tokio::test]
async fn oidc_callback_requires_browser_correlation_before_consuming_state() {
    let server = TestServer::start().await;
    let client = server.client();
    let instance =
        create_instance(&server, &client, "oidc-correlation-test").await["instance"].clone();
    let instance_id = instance["id"].as_str().expect("instance ID is returned");
    let slug = instance["slug"]
        .as_str()
        .expect("instance slug is returned");
    let connection_id = "con_oidc_correlation";
    let timestamp = now();
    server
        .state
        .store
        .create_connection(&Connection {
            id: connection_id.to_owned(),
            instance_id: instance_id.to_owned(),
            r#type: "oidc".to_owned(),
            name: "Correlation test provider".to_owned(),
            enabled: true,
            org_id: None,
            issuer_url: Some("https://issuer.example.test".to_owned()),
            client_id: Some("oidc-correlation-client".to_owned()),
            client_secret_box: None,
            authorization_endpoint: Some("https://issuer.example.test/authorize".to_owned()),
            token_endpoint: Some("https://issuer.example.test/token".to_owned()),
            userinfo_endpoint: Some("https://issuer.example.test/userinfo".to_owned()),
            idp_entity_id: None,
            sso_redirect_url: None,
            x509_certificate_pem: None,
            sp_entity_id: None,
            created_at: timestamp,
        })
        .await
        .expect("OIDC connection should be seedable");

    let continuation = format!("/{slug}/oauth/authorize?state=rp-state&code_challenge=rp-pkce");
    let picker = client
        .get(format!("{}/ui/{slug}/connections/list", server.origin))
        .query(&[("next", continuation.as_str())])
        .send()
        .await
        .expect("hosted connection picker should respond");
    assert_eq!(picker.status(), StatusCode::OK);
    let picker_html = picker.text().await.expect("picker response should be HTML");
    assert!(
        picker_html.contains(&format!(
            "name=\"next\" value=\"/{slug}/oauth/authorize?state=rp-state&amp;code_challenge=rp-pkce\""
        )),
        "the picker should safely preserve a validated relying party continuation"
    );
    let authorize = client
        .get(format!(
            "{}/ui/{slug}/connections/{connection_id}/oidc/authorize",
            server.origin
        ))
        .query(&[("next", continuation.as_str())])
        .send()
        .await
        .expect("OIDC authorize route should respond");
    assert_eq!(authorize.status(), StatusCode::SEE_OTHER);
    let location = authorize
        .headers()
        .get(header::LOCATION)
        .and_then(|value| value.to_str().ok())
        .expect("authorize route should redirect upstream");
    let upstream = url::Url::parse(location).expect("upstream redirect should be valid");
    let state = upstream
        .query_pairs()
        .find_map(|(name, value)| (name == "state").then(|| value.into_owned()))
        .expect("upstream redirect should contain state");
    assert!(
        upstream
            .query_pairs()
            .any(|(name, value)| name == "nonce" && !value.is_empty()),
        "upstream redirect should contain an independent nonce"
    );
    assert!(
        upstream
            .query_pairs()
            .any(|(name, value)| name == "code_challenge" && !value.is_empty()),
        "upstream redirect should contain a PKCE challenge"
    );
    let set_cookie = authorize
        .headers()
        .get(header::SET_COOKIE)
        .and_then(|value| value.to_str().ok())
        .expect("authorize route should set browser correlation cookie");
    assert!(set_cookie.contains("Secure; HttpOnly; SameSite=Lax"));
    assert!(set_cookie.contains("Max-Age=600"));
    assert!(set_cookie.contains(&format!(
        "Path=/ui/{slug}/connections/{connection_id}/oidc/callback"
    )));
    let browser_cookie = set_cookie
        .split(';')
        .next()
        .expect("Set-Cookie should have a name and value")
        .to_owned();
    let callback = format!(
        "{}/ui/{slug}/connections/{connection_id}/oidc/callback",
        server.origin
    );
    let state_key = sha256_hex(&state);
    let state_record = server
        .state
        .store
        .find_verification_token(&state_key)
        .await
        .expect("OIDC state should be readable")
        .expect("OIDC state should exist");
    assert_eq!(
        state_record.authentication_next.as_deref(),
        Some(continuation.as_str()),
        "federation state should retain the relying party continuation"
    );

    let without_cookie = client
        .get(&callback)
        .query(&[("state", state.as_str()), ("error", "access_denied")])
        .send()
        .await
        .expect("missing-cookie callback should reach the service");
    assert_eq!(without_cookie.status(), StatusCode::BAD_REQUEST);
    assert!(
        !server
            .state
            .store
            .find_verification_token(&state_key)
            .await
            .expect("state should remain queryable")
            .expect("OIDC state should still exist")
            .consumed,
        "a callback without the browser cookie must not consume state"
    );

    let wrong_cookie = client
        .get(&callback)
        .query(&[("state", state.as_str()), ("error", "access_denied")])
        .header(header::COOKIE, "tsauth_oidc_state=incorrect")
        .send()
        .await
        .expect("wrong-cookie callback should reach the service");
    assert_eq!(wrong_cookie.status(), StatusCode::BAD_REQUEST);
    assert!(
        !server
            .state
            .store
            .find_verification_token(&state_key)
            .await
            .expect("state should remain queryable")
            .expect("OIDC state should still exist")
            .consumed,
        "a callback with a mismatched browser cookie must not consume state"
    );

    let correlated_error = client
        .get(&callback)
        .query(&[("state", state.as_str()), ("error", "access_denied")])
        .header(header::COOKIE, browser_cookie.as_str())
        .send()
        .await
        .expect("correlated upstream error should reach the service");
    assert_eq!(correlated_error.status(), StatusCode::BAD_REQUEST);
    assert!(
        correlated_error
            .headers()
            .get_all(header::SET_COOKIE)
            .iter()
            .filter_map(|value| value.to_str().ok())
            .any(|value| value.starts_with("tsauth_oidc_state=") && value.contains("Max-Age=0")),
        "completed callback should clear the browser correlation cookie"
    );
    assert!(
        server
            .state
            .store
            .find_verification_token(&state_key)
            .await
            .expect("state should remain queryable")
            .expect("OIDC state should still exist")
            .consumed,
        "a correlated callback should consume the one-time state"
    );

    let replay = client
        .get(&callback)
        .query(&[("state", state.as_str()), ("error", "access_denied")])
        .header(header::COOKIE, browser_cookie.as_str())
        .send()
        .await
        .expect("callback replay should reach the service");
    assert_eq!(replay.status(), StatusCode::BAD_REQUEST);
}

#[tokio::test]
async fn scim_directory_deactivation_disables_oauth_user_tokens() {
    let server = TestServer::start().await;
    let client = server.client();
    let instance =
        create_instance(&server, &client, "oauth-scim-eligibility-test").await["instance"].clone();
    let instance_id = instance["id"].as_str().expect("instance ID is returned");
    let slug = instance["slug"]
        .as_str()
        .expect("instance slug is returned");
    let timestamp = now();
    let user_id = "usr_oauth_scim_eligibility";
    let directory_id = "scd_oauth_scim_eligibility";
    let client_id = "oauth-scim-eligibility-client";
    let client_secret = "oauth-scim-eligibility-secret";
    let redirect_uri = "https://client.example.test/callback";
    let raw_code = "oauth-scim-eligibility-code";

    server
        .state
        .store
        .create_scim_directory(&ScimDirectory {
            id: directory_id.to_owned(),
            instance_id: instance_id.to_owned(),
            org_id: None,
            instance_wide: true,
            status: ScimDirectoryStatus::Active,
            token_key: sha256_hex("oauth-scim-eligibility-directory-token"),
            created_at: timestamp,
        })
        .await
        .expect("instance-wide SCIM directory should be seedable");
    server
        .state
        .store
        .create(
            "users",
            user_id,
            &User {
                id: user_id.to_owned(),
                instance_id: instance_id.to_owned(),
                email: Some("scim-eligible@example.test".to_owned()),
                email_verified: true,
                identity_established: true,
                phone: None,
                username: None,
                password_hash: None,
                auth_epoch: 0,
                name: Some("SCIM Eligible".to_owned()),
                image_url: None,
                banned: false,
                ban_reason: None,
                deleted: false,
                scim_provisioning: None,
                scim_instance_wide_directory_ids: vec![directory_id.to_owned()],
                public_metadata: Default::default(),
                private_metadata: Default::default(),
                quota_seat: None,
                created_at: timestamp,
                updated_at: timestamp,
                last_sign_in_at: None,
                email_key: None,
                username_key: None,
            },
        )
        .await
        .expect("SCIM managed user should be seedable");
    let link_id = format!("{directory_id}:{user_id}");
    server
        .state
        .store
        .create(
            "scim_user_links",
            &link_id,
            &ScimUserLink {
                id: link_id.clone(),
                instance_id: instance_id.to_owned(),
                directory_id: directory_id.to_owned(),
                user_id: user_id.to_owned(),
                external_id: Some("external-scim-user".to_owned()),
                active: true,
                deleting: false,
                cleanup_owner: None,
                incarnation_id: Some(format!("incarnation-{link_id}")),
                cleanup_complete: false,
                profile_user_name: None,
                profile_name: None,
                profile_initialized: false,
                created_at: timestamp,
            },
        )
        .await
        .expect("active SCIM user link should be seedable");
    server
        .state
        .store
        .create_oauth_client(&OAuthClient {
            id: "cli_oauth_scim_eligibility".to_owned(),
            instance_id: instance_id.to_owned(),
            client_id: client_id.to_owned(),
            client_key: format!("{instance_id}:{client_id}"),
            name: "OAuth SCIM eligibility acceptance client".to_owned(),
            client_secret_hash: Some(sha256_hex(client_secret)),
            redirect_uris: vec![redirect_uri.to_owned()],
            allowed_origins: Vec::new(),
            token_endpoint_auth_method: "client_secret_post".to_owned(),
            r#public: false,
            created_at: timestamp,
        })
        .await
        .expect("confidential OAuth client should be seedable");
    server
        .state
        .store
        .create_authorization_code(&AuthorizationCode {
            id: "oauth-code-scim-eligibility".to_owned(),
            instance_id: instance_id.to_owned(),
            client_id: client_id.to_owned(),
            user_id: user_id.to_owned(),
            user_auth_epoch: 0,
            code_key: sha256_hex(raw_code),
            redirect_uri: redirect_uri.to_owned(),
            scopes: vec![
                "openid".to_owned(),
                "profile".to_owned(),
                "email".to_owned(),
            ],
            code_challenge: None,
            code_challenge_method: None,
            nonce: None,
            expires_at: timestamp + 60,
            consumed: false,
        })
        .await
        .expect("authorization code should be seedable");

    let token_endpoint = format!("{}/{slug}/oauth/token", server.origin);
    let token_response = client
        .post(&token_endpoint)
        .form(&[
            ("grant_type", "authorization_code"),
            ("client_id", client_id),
            ("client_secret", client_secret),
            ("code", raw_code),
            ("redirect_uri", redirect_uri),
        ])
        .send()
        .await
        .expect("authorization-code exchange should reach the service");
    assert_eq!(token_response.status(), StatusCode::OK);
    let issued: Value = token_response
        .json()
        .await
        .expect("token response should be JSON");
    let access_token = issued["access_token"]
        .as_str()
        .expect("authorization-code exchange should issue an access token");
    let refresh_token = issued["refresh_token"]
        .as_str()
        .expect("authorization-code exchange should issue a refresh token");
    let client_credentials_response = client
        .post(&token_endpoint)
        .form(&[
            ("grant_type", "client_credentials"),
            ("client_id", client_id),
            ("client_secret", client_secret),
            ("scope", "service.read"),
        ])
        .send()
        .await
        .expect("client-credentials exchange should reach the service");
    assert_eq!(client_credentials_response.status(), StatusCode::OK);
    let client_credentials: Value = client_credentials_response
        .json()
        .await
        .expect("client-credentials response should be JSON");
    let client_credentials_token = client_credentials["access_token"]
        .as_str()
        .expect("client-credentials exchange should issue an access token");

    let introspect = |token: &str| {
        client
            .post(format!("{}/{slug}/oauth/introspect", server.origin))
            .form(&[
                ("client_id", client_id),
                ("client_secret", client_secret),
                ("token", token),
            ])
    };
    let active_access = introspect(access_token)
        .send()
        .await
        .expect("access-token introspection should reach the service");
    assert_eq!(active_access.status(), StatusCode::OK);
    assert_eq!(active_access.json::<Value>().await.unwrap()["active"], true);
    let active_userinfo = client
        .get(format!("{}/{slug}/oauth/userinfo", server.origin))
        .bearer_auth(access_token)
        .send()
        .await
        .expect("userinfo should reach the service");
    assert_eq!(active_userinfo.status(), StatusCode::OK);
    let active_refresh = introspect(refresh_token)
        .send()
        .await
        .expect("refresh-token introspection should reach the service");
    assert_eq!(active_refresh.status(), StatusCode::OK);
    assert_eq!(
        active_refresh.json::<Value>().await.unwrap()["active"],
        true
    );

    let (mut directory, version) = server
        .state
        .store
        .get_versioned::<ScimDirectory>("scim_directories", directory_id)
        .await
        .expect("directory should be readable with its version")
        .expect("directory should exist");
    directory.status = ScimDirectoryStatus::Deleting;
    assert!(
        server
            .state
            .store
            .compare_and_put("scim_directories", directory_id, &directory, &version)
            .await
            .expect("directory transition should be writable")
    );

    let inactive_access = introspect(access_token)
        .send()
        .await
        .expect("access-token introspection should reach the service");
    assert_eq!(inactive_access.status(), StatusCode::OK);
    assert_eq!(
        inactive_access.json::<Value>().await.unwrap()["active"],
        false
    );
    let inactive_userinfo = client
        .get(format!("{}/{slug}/oauth/userinfo", server.origin))
        .bearer_auth(access_token)
        .send()
        .await
        .expect("userinfo should reach the service");
    assert_eq!(inactive_userinfo.status(), StatusCode::UNAUTHORIZED);
    let inactive_refresh = introspect(refresh_token)
        .send()
        .await
        .expect("refresh-token introspection should reach the service");
    assert_eq!(inactive_refresh.status(), StatusCode::OK);
    assert_eq!(
        inactive_refresh.json::<Value>().await.unwrap()["active"],
        false
    );

    let client_credentials_introspection = introspect(client_credentials_token)
        .send()
        .await
        .expect("client-credentials introspection should reach the service");
    assert_eq!(client_credentials_introspection.status(), StatusCode::OK);
    assert_eq!(
        client_credentials_introspection
            .json::<Value>()
            .await
            .unwrap()["active"],
        true,
        "client-credentials tokens have no user and remain active"
    );

    let inactive_raw_code = "oauth-scim-inactive-code";
    server
        .state
        .store
        .create_authorization_code(&AuthorizationCode {
            id: "oauth-code-scim-inactive".to_owned(),
            instance_id: instance_id.to_owned(),
            client_id: client_id.to_owned(),
            user_id: user_id.to_owned(),
            user_auth_epoch: 0,
            code_key: sha256_hex(inactive_raw_code),
            redirect_uri: redirect_uri.to_owned(),
            scopes: vec!["openid".to_owned()],
            code_challenge: None,
            code_challenge_method: None,
            nonce: None,
            expires_at: now() + 60,
            consumed: false,
        })
        .await
        .expect("second authorization code should be seedable");
    let inactive_code_response = client
        .post(&token_endpoint)
        .form(&[
            ("grant_type", "authorization_code"),
            ("client_id", client_id),
            ("client_secret", client_secret),
            ("code", inactive_raw_code),
            ("redirect_uri", redirect_uri),
        ])
        .send()
        .await
        .expect("inactive authorization-code exchange should reach the service");
    assert_eq!(inactive_code_response.status(), StatusCode::BAD_REQUEST);
    assert_eq!(
        inactive_code_response.json::<Value>().await.unwrap()["error"],
        "invalid_grant"
    );
}
