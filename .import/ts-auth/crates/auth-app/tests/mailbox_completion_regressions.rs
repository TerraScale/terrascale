use std::{
    sync::OnceLock,
    time::{SystemTime, UNIX_EPOCH},
};

use auth_app::{
    AppState, AuthOptions,
    app::build_state_with_mode,
    build_router,
    features::users::{self, UserChanges},
};
use auth_contracts::models::{
    Instance, MfaFactor, OrgMembership, Organization, OrganizationStatus,
};
use auth_security::{secretbox, tokens};
use axum::http::{StatusCode, header};
use reqwest::Client;
use serde_json::{Value, json};
use tokio::{net::TcpListener, task::JoinHandle};

mod support;

use support::terrabase_http::{TEST_TERRASCALE_API_KEY, TerraBaseHttpFixture};

const OPERATOR_KEY: &str = "test-only-operator-key";

static MAILBOX_SCENARIO_LOCK: OnceLock<tokio::sync::Mutex<()>> = OnceLock::new();

struct TestServer {
    origin: String,
    public_origin: String,
    state: AppState,
    task: JoinHandle<()>,
    _storage: TerraBaseHttpFixture,
}

impl TestServer {
    async fn start() -> Self {
        let storage = TerraBaseHttpFixture::start().await;
        let listener = TcpListener::bind("127.0.0.1:0")
            .await
            .expect("mailbox regression listener should bind");
        let address = listener
            .local_addr()
            .expect("mailbox regression listener should have an address");
        let public_origin = format!("http://localhost:{}", address.port());
        static NEXT_DATABASE: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1);
        let database = format!(
            "ts-auth-mailbox-regressions-{}",
            NEXT_DATABASE.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
        );
        let options = AuthOptions {
            listen: "http://127.0.0.1:0".to_owned(),
            public_origin: public_origin.clone(),
            terrascale_api_url: storage.origin.clone(),
            terrascale_api_key: TEST_TERRASCALE_API_KEY.to_owned(),
            terrascale_project_id: None,
            terrascale_tenant_id: None,
            database,
            master_key: vec![7; 32],
            audit_signing_key: Some(vec![8; 32]),
            audit_verification_keys: Vec::new(),
            operator_key: OPERATOR_KEY.to_owned(),
            metrics_key: Some("test-only-metrics-key".to_owned()),
            rate_limit_per_window: 1_000,
            stripe_secret_key: None,
            stripe_webhook_secret: None,
            stripe_api_url: "http://127.0.0.1:1".to_owned(),
            price_pro: None,
            price_enterprise: None,
            smtp_host: None,
            smtp_port: 587,
            smtp_username: None,
            smtp_password: None,
            smtp_from_address: None,
        };
        let state = build_state_with_mode(options, true)
            .expect("mailbox regression app state should build");
        let router = build_router(state.clone());
        let task = tokio::spawn(async move {
            axum::serve(listener, router)
                .await
                .expect("mailbox regression app should remain available");
        });

        Self {
            origin: format!("http://{address}"),
            public_origin,
            state,
            task,
            _storage: storage,
        }
    }

    fn client(&self) -> Client {
        Client::builder()
            .no_proxy()
            .redirect(reqwest::redirect::Policy::none())
            .build()
            .expect("mailbox regression client should build")
    }

    async fn create_instance(&self, slug: &str) -> Instance {
        let instance = Instance {
            id: format!("ins_{slug}"),
            slug: slug.to_owned(),
            name: format!("{slug} mailbox regression"),
            logo_url: None,
            theme_color: None,
            stripe_customer_id: None,
            created_at: now(),
        };
        self.state
            .store
            .create_instance(&instance)
            .await
            .expect("mailbox regression instance should persist");
        instance
    }

    async fn create_managed_instance(&self, slug: &str) -> (Instance, String) {
        let response = self
            .client()
            .post(format!("{}/v1/instances", self.origin))
            .bearer_auth(OPERATOR_KEY)
            .header("Idempotency-Key", format!("mailbox-email-change-{slug}"))
            .json(&json!({
                "slug": slug,
                "name": format!("{slug} mailbox regression"),
            }))
            .send()
            .await
            .expect("operator instance request should reach the app");
        assert_eq!(response.status(), StatusCode::OK);
        let payload: Value = response
            .json()
            .await
            .expect("operator instance response should be JSON");
        let instance_id = payload["instance"]["id"]
            .as_str()
            .expect("operator response should contain the created instance ID");
        assert_eq!(payload["instance"]["slug"].as_str(), Some(slug));
        let instance = self
            .state
            .store
            .get_instance(instance_id)
            .await
            .expect("created instance lookup should succeed")
            .expect("operator-created instance should be stored");
        let management_key = payload["api_key"]
            .as_str()
            .expect("operator response should return a management key")
            .to_owned();
        (instance, management_key)
    }
}

impl Drop for TestServer {
    fn drop(&mut self) {
        self.task.abort();
    }
}

#[tokio::test]
async fn signup_verification_get_is_read_only_and_post_uses_fresh_credentials() {
    let _scenario_guard = mailbox_scenario_guard().await;
    let server = TestServer::start().await;
    let instance = server.create_instance("mailbox-signup").await;
    let client = server.client();
    let signup = client
        .post(format!("{}/ui/{}/sign-up", server.origin, instance.slug))
        .header(header::ORIGIN, &server.public_origin)
        .form(&[
            ("email", "signup-mailbox@example.test"),
            ("password", "original-signup-password"),
            ("name", "Original Signup Name"),
        ])
        .send()
        .await
        .expect("public signup should reach the app");
    assert_eq!(signup.status(), StatusCode::SEE_OTHER);

    let original = user_by_email(&server, &instance.id, "signup-mailbox@example.test").await;
    assert!(!original.email_verified);
    assert!(!original.identity_established);
    let token = users::issue_token(
        &server.state,
        &instance.id,
        Some(&original.id),
        "verify_email",
        600,
    )
    .await
    .expect("signup verification token should be issued");

    let form = get_verification_form(&server, &instance.slug, &token).await;
    assert_form_has_mailbox_credentials(&form);
    assert_token_unconsumed(&server, &original.id, "verify_email", &token).await;

    let completion = client
        .post(format!(
            "{}/ui/{}/verify-email",
            server.origin, instance.slug
        ))
        .header(header::ORIGIN, &server.public_origin)
        .form(&[
            ("token", token.as_str()),
            ("password", "post-proof-password"),
            ("name", "Post Proof Name"),
        ])
        .send()
        .await
        .expect("same-origin mailbox completion should reach the app");
    assert!(completion.status().is_success());

    let old = server
        .state
        .store
        .get_user(&original.id)
        .await
        .expect("original user lookup should succeed")
        .expect("original user tombstone should remain");
    let verified = user_by_email(&server, &instance.id, "signup-mailbox@example.test").await;
    assert_ne!(verified.id, original.id);
    assert!(old.deleted);
    assert!(verified.email_verified);
    assert!(verified.identity_established);
    assert_eq!(verified.name.as_deref(), Some("Post Proof Name"));
    assert!(verified.username.is_none());
    assert!(verified.public_metadata.is_empty());
    assert!(verified.private_metadata.is_empty());

    let signed_in = sign_in(
        &server,
        &instance.slug,
        "signup-mailbox@example.test",
        "post-proof-password",
    )
    .await;
    assert_eq!(signed_in.status(), StatusCode::SEE_OTHER);
    assert!(signed_in.headers().contains_key(header::SET_COOKIE));
}

#[tokio::test]
async fn unverified_mailbox_completion_replaces_attacker_credentials_and_artifacts() {
    let _scenario_guard = mailbox_scenario_guard().await;
    let server = TestServer::start().await;
    let instance = server.create_instance("mailbox-verify-replacement").await;
    let attacker = create_attacker_seed(&server, &instance, "verify").await;
    let (_, old_session_token) = users::create_session(
        &server.state,
        &instance.id,
        &attacker.id,
        Some("mailbox regression attacker"),
        None,
    )
    .await
    .expect("unverified identity should have a seed session");
    assert_eq!(
        server
            .state
            .store
            .list_mfa_factors(&attacker.id)
            .await
            .expect("attacker MFA factor lookup should succeed")
            .len(),
        1,
        "attacker MFA must be seeded on the unverified identity"
    );
    let token = users::issue_token(
        &server.state,
        &instance.id,
        Some(&attacker.id),
        "verify_email",
        600,
    )
    .await
    .expect("verification token should be issued");

    let form = get_verification_form(&server, &instance.slug, &token).await;
    assert_form_has_mailbox_credentials(&form);
    assert_token_unconsumed(&server, &attacker.id, "verify_email", &token).await;

    let completion = server
        .client()
        .post(format!(
            "{}/ui/{}/verify-email",
            server.origin, instance.slug
        ))
        .header(header::ORIGIN, &server.public_origin)
        .form(&[
            ("token", token.as_str()),
            ("password", "fresh-mailbox-password"),
            ("name", "Mailbox Owner"),
        ])
        .send()
        .await
        .expect("same-origin verification POST should reach the app");
    assert!(completion.status().is_success());

    let old = server
        .state
        .store
        .get_user(&attacker.id)
        .await
        .expect("attacker user lookup should succeed")
        .expect("attacker tombstone should remain");
    let fresh = user_by_email(&server, &instance.id, "verify-attacker@example.test").await;
    assert_ne!(fresh.id, attacker.id);
    assert!(old.deleted);
    assert!(fresh.email_verified);
    assert!(fresh.identity_established);
    assert_eq!(fresh.name.as_deref(), Some("Mailbox Owner"));
    assert!(fresh.username.is_none());
    assert!(fresh.image_url.is_none());
    assert!(fresh.public_metadata.is_empty());
    assert!(fresh.private_metadata.is_empty());
    assert!(
        server
            .state
            .store
            .list_mfa_factors(&fresh.id)
            .await
            .expect("fresh MFA factor lookup should succeed")
            .is_empty()
    );
    assert!(
        server
            .state
            .store
            .list_memberships_by_user(&fresh.id)
            .await
            .expect("fresh membership lookup should succeed")
            .is_empty()
    );
    let old_session = server
        .client()
        .get(format!("{}/ui/{}/account", server.origin, instance.slug))
        .header(
            header::COOKIE,
            format!("tsauth_session={old_session_token}"),
        )
        .send()
        .await
        .expect("old-session account request should reach the app");
    assert_eq!(old_session.status(), StatusCode::SEE_OTHER);
    assert!(old_session
        .headers()
        .get(header::LOCATION)
        .and_then(|location| location.to_str().ok())
        .is_some_and(|location| location.starts_with(&format!("/ui/{}/sign-in", instance.slug))));
    let attacker_password = sign_in(
        &server,
        &instance.slug,
        "verify-attacker@example.test",
        "attacker-controlled-password",
    )
    .await;
    assert_ne!(attacker_password.status(), StatusCode::SEE_OTHER);
    assert!(!attacker_password.headers().contains_key(header::SET_COOKIE));
    let mailbox_password = sign_in(
        &server,
        &instance.slug,
        "verify-attacker@example.test",
        "fresh-mailbox-password",
    )
    .await;
    assert_eq!(mailbox_password.status(), StatusCode::SEE_OTHER);
    assert!(mailbox_password.headers().contains_key(header::SET_COOKIE));
    assert_eq!(
        mailbox_password
            .headers()
            .get(header::LOCATION)
            .and_then(|location| location.to_str().ok()),
        Some(format!("/ui/{}/account", instance.slug).as_str()),
        "the replacement signs in directly instead of inheriting the old MFA challenge"
    );
}

#[tokio::test]
async fn unverified_password_reset_replaces_identity_and_does_not_copy_attacker_state() {
    let _scenario_guard = mailbox_scenario_guard().await;
    let server = TestServer::start().await;
    let instance = server.create_instance("mailbox-reset-replacement").await;
    let attacker = create_attacker_seed(&server, &instance, "reset").await;
    let (_, old_session_token) = users::create_session(
        &server.state,
        &instance.id,
        &attacker.id,
        Some("mailbox regression attacker"),
        None,
    )
    .await
    .expect("unverified identity should have a seed session");
    assert_eq!(
        server
            .state
            .store
            .list_mfa_factors(&attacker.id)
            .await
            .expect("attacker MFA factor lookup should succeed")
            .len(),
        1,
        "attacker MFA must be seeded on the unverified identity"
    );
    let token = users::issue_token(
        &server.state,
        &instance.id,
        Some(&attacker.id),
        "reset_password",
        600,
    )
    .await
    .expect("reset token should be issued");

    let reset_page = server
        .client()
        .get(format!(
            "{}/ui/{}/reset-password",
            server.origin, instance.slug
        ))
        .query(&[("token", token.as_str())])
        .send()
        .await
        .expect("reset page request should reach the app");
    assert_eq!(reset_page.status(), StatusCode::OK);
    let form = reset_page
        .text()
        .await
        .expect("reset page should contain HTML");
    assert!(form.contains("name=\"token\""));
    assert!(form.contains("name=\"password\""));
    assert_token_unconsumed(&server, &attacker.id, "reset_password", &token).await;

    let reset = server
        .client()
        .post(format!(
            "{}/ui/{}/reset-password",
            server.origin, instance.slug
        ))
        .header(header::ORIGIN, &server.public_origin)
        .form(&[
            ("token", token.as_str()),
            ("password", "fresh-reset-password"),
        ])
        .send()
        .await
        .expect("same-origin reset POST should reach the app");
    assert!(reset.status().is_success());

    let old = server
        .state
        .store
        .get_user(&attacker.id)
        .await
        .expect("attacker user lookup should succeed")
        .expect("attacker tombstone should remain");
    let fresh = user_by_email(&server, &instance.id, "reset-attacker@example.test").await;
    assert_ne!(fresh.id, attacker.id);
    assert!(old.deleted);
    assert!(fresh.email_verified);
    assert!(fresh.identity_established);
    assert!(fresh.password_hash.is_some());
    assert!(fresh.name.is_none());
    assert!(fresh.username.is_none());
    assert!(fresh.image_url.is_none());
    assert!(fresh.public_metadata.is_empty());
    assert!(fresh.private_metadata.is_empty());
    assert!(
        server
            .state
            .store
            .list_mfa_factors(&fresh.id)
            .await
            .expect("fresh MFA factor lookup should succeed")
            .is_empty()
    );
    assert!(
        server
            .state
            .store
            .list_memberships_by_user(&fresh.id)
            .await
            .expect("fresh membership lookup should succeed")
            .is_empty()
    );
    let old_session = server
        .client()
        .get(format!("{}/ui/{}/account", server.origin, instance.slug))
        .header(
            header::COOKIE,
            format!("tsauth_session={old_session_token}"),
        )
        .send()
        .await
        .expect("old-session account request should reach the app");
    assert_eq!(old_session.status(), StatusCode::SEE_OTHER);
    assert!(old_session
        .headers()
        .get(header::LOCATION)
        .and_then(|location| location.to_str().ok())
        .is_some_and(|location| location.starts_with(&format!("/ui/{}/sign-in", instance.slug))));

    let attacker_password = sign_in(
        &server,
        &instance.slug,
        "reset-attacker@example.test",
        "attacker-controlled-password",
    )
    .await;
    assert_ne!(attacker_password.status(), StatusCode::SEE_OTHER);
    assert!(!attacker_password.headers().contains_key(header::SET_COOKIE));
    let reset_password = sign_in(
        &server,
        &instance.slug,
        "reset-attacker@example.test",
        "fresh-reset-password",
    )
    .await;
    assert_eq!(reset_password.status(), StatusCode::SEE_OTHER);
    assert!(reset_password.headers().contains_key(header::SET_COOKIE));
    assert_eq!(
        reset_password
            .headers()
            .get(header::LOCATION)
            .and_then(|location| location.to_str().ok()),
        Some(format!("/ui/{}/account", instance.slug).as_str()),
        "the replacement signs in directly instead of inheriting the old MFA challenge"
    );
}

#[tokio::test]
async fn established_email_change_reverifies_same_id_and_preserves_owned_state() {
    let _scenario_guard = mailbox_scenario_guard().await;
    let server = TestServer::start().await;
    let (instance, management_key) = server
        .create_managed_instance("mailbox-established-email-change")
        .await;
    let established = users::find_or_create_verified_user(
        &server.state,
        &instance.id,
        "established-old@example.test",
        Some("Established Profile"),
    )
    .await
    .expect("trusted first proof should establish the mailbox identity");
    assert!(established.identity_established);
    let established = users::update_user(
        &server.state,
        &instance.id,
        &established.id,
        UserChanges {
            username: Some(Some("established-handle".to_owned())),
            password: Some(Some("established-password".to_owned())),
            public_metadata: Some(json!({"team":"identity"}).as_object().unwrap().clone()),
            private_metadata: Some(json!({"retained":true}).as_object().unwrap().clone()),
            ..UserChanges::default()
        },
    )
    .await
    .expect("established account profile should be seeded");
    let membership = add_membership(&server, &instance, &established, "established").await;
    add_totp_factor(&server, &instance, &established).await;
    let old_token = users::issue_token(
        &server.state,
        &instance.id,
        Some(&established.id),
        "verify_email",
        600,
    )
    .await
    .expect("old-address verification token should be issued");

    let email_change = server
        .client()
        .patch(format!(
            "{}/v1/{}/users/{}",
            server.origin, instance.slug, established.id
        ))
        .bearer_auth(&management_key)
        .json(&json!({"email":"established-new@example.test"}))
        .send()
        .await
        .expect("management email change should reach the API");
    assert_eq!(email_change.status(), StatusCode::OK);
    let changed = server
        .state
        .store
        .get_user(&established.id)
        .await
        .expect("established account lookup should succeed")
        .expect("established account should remain present");
    assert_eq!(
        changed.email.as_deref(),
        Some("established-new@example.test")
    );
    assert!(!changed.email_verified);
    assert!(changed.identity_established);
    assert_eq!(changed.password_hash, established.password_hash);
    assert_eq!(
        server
            .state
            .store
            .list_memberships_by_user(&established.id)
            .await
            .expect("membership lookup should succeed")[0]
            .id,
        membership.id
    );
    assert_eq!(
        server
            .state
            .store
            .list_mfa_factors(&established.id)
            .await
            .expect("MFA lookup should succeed")
            .len(),
        1
    );

    let old_link = server
        .client()
        .get(format!(
            "{}/ui/{}/verify-email",
            server.origin, instance.slug
        ))
        .query(&[("token", old_token.as_str())])
        .send()
        .await
        .expect("old-address proof should reach the app");
    assert_eq!(old_link.status(), StatusCode::BAD_REQUEST);

    let token = users::issue_token(
        &server.state,
        &instance.id,
        Some(&established.id),
        "verify_email",
        600,
    )
    .await
    .expect("new-address verification token should be issued");
    let form = get_verification_form(&server, &instance.slug, &token).await;
    assert_form_has_mailbox_credentials(&form);
    assert_token_unconsumed(&server, &established.id, "verify_email", &token).await;
    let completion = server
        .client()
        .post(format!(
            "{}/ui/{}/verify-email",
            server.origin, instance.slug
        ))
        .header(header::ORIGIN, &server.public_origin)
        .form(&[
            ("token", token.as_str()),
            ("password", "ignored-new-password"),
            ("name", "Ignored New Name"),
        ])
        .send()
        .await
        .expect("new-address proof POST should reach the app");
    assert!(completion.status().is_success());

    let verified = server
        .state
        .store
        .get_user(&established.id)
        .await
        .expect("established account lookup should succeed")
        .expect("established account should keep its ID");
    assert_eq!(verified.id, established.id);
    assert!(verified.email_verified);
    assert!(verified.identity_established);
    assert_eq!(
        verified.email.as_deref(),
        Some("established-new@example.test")
    );
    assert_eq!(verified.password_hash, established.password_hash);
    assert_eq!(verified.name.as_deref(), Some("Established Profile"));
    assert_eq!(verified.username.as_deref(), Some("established-handle"));
    assert_eq!(verified.public_metadata, established.public_metadata);
    assert_eq!(verified.private_metadata, established.private_metadata);
    assert_eq!(
        server
            .state
            .store
            .list_memberships_by_user(&established.id)
            .await
            .expect("membership lookup should succeed")
            .len(),
        1
    );
    assert_eq!(
        server
            .state
            .store
            .list_mfa_factors(&established.id)
            .await
            .expect("MFA lookup should succeed")
            .len(),
        1
    );

    let retained_password = sign_in(
        &server,
        &instance.slug,
        "established-new@example.test",
        "established-password",
    )
    .await;
    assert_eq!(retained_password.status(), StatusCode::SEE_OTHER);
    let ignored_password = sign_in(
        &server,
        &instance.slug,
        "established-new@example.test",
        "ignored-new-password",
    )
    .await;
    assert_ne!(ignored_password.status(), StatusCode::SEE_OTHER);
}

#[tokio::test]
async fn verified_password_reset_keeps_mfa_and_advances_auth_epoch() {
    let _scenario_guard = mailbox_scenario_guard().await;
    let server = TestServer::start().await;
    let instance = server.create_instance("mailbox-verified-reset").await;
    let verified = users::find_or_create_verified_user(
        &server.state,
        &instance.id,
        "verified-reset@example.test",
        Some("Verified Reset User"),
    )
    .await
    .expect("trusted mailbox identity should be established");
    let verified = users::update_user(
        &server.state,
        &instance.id,
        &verified.id,
        UserChanges {
            password: Some(Some("previous-password".to_owned())),
            ..UserChanges::default()
        },
    )
    .await
    .expect("verified account should have a password");
    let epoch_before_reset = verified.auth_epoch;
    add_totp_factor(&server, &instance, &verified).await;
    let token = users::issue_token(
        &server.state,
        &instance.id,
        Some(&verified.id),
        "reset_password",
        600,
    )
    .await
    .expect("verified reset token should be issued");

    let reset = server
        .client()
        .post(format!(
            "{}/ui/{}/reset-password",
            server.origin, instance.slug
        ))
        .header(header::ORIGIN, &server.public_origin)
        .form(&[
            ("token", token.as_str()),
            ("password", "verified-new-password"),
        ])
        .send()
        .await
        .expect("verified reset POST should reach the app");
    assert!(reset.status().is_success());

    let after = server
        .state
        .store
        .get_user(&verified.id)
        .await
        .expect("verified account lookup should succeed")
        .expect("verified account should retain its ID");
    assert_eq!(after.id, verified.id);
    assert!(after.email_verified);
    assert!(after.identity_established);
    assert_eq!(after.auth_epoch, epoch_before_reset + 1);
    assert_eq!(
        server
            .state
            .store
            .list_mfa_factors(&verified.id)
            .await
            .expect("MFA factor lookup should succeed")
            .len(),
        1
    );
    let old_password = sign_in(
        &server,
        &instance.slug,
        "verified-reset@example.test",
        "previous-password",
    )
    .await;
    assert_ne!(old_password.status(), StatusCode::SEE_OTHER);
    let new_password = sign_in(
        &server,
        &instance.slug,
        "verified-reset@example.test",
        "verified-new-password",
    )
    .await;
    assert_eq!(new_password.status(), StatusCode::SEE_OTHER);
}

async fn create_attacker_seed(
    server: &TestServer,
    instance: &Instance,
    suffix: &str,
) -> auth_contracts::models::User {
    let email = format!("{suffix}-attacker@example.test");
    let user = users::create_unverified_signup_user(
        &server.state,
        &instance.id,
        Some(&email),
        Some("attacker-controlled-password"),
        Some("Attacker Profile Name"),
    )
    .await
    .expect("attacker-controlled unverified signup should persist");
    assert!(!user.identity_established);
    let user = users::update_user(
        &server.state,
        &instance.id,
        &user.id,
        UserChanges {
            username: Some(Some(format!("attacker-{suffix}"))),
            public_metadata: Some(
                json!({"claim":"attacker"})
                    .as_object()
                    .expect("public profile should be an object")
                    .clone(),
            ),
            private_metadata: Some(
                json!({"secret":"attacker"})
                    .as_object()
                    .expect("private profile should be an object")
                    .clone(),
            ),
            ..UserChanges::default()
        },
    )
    .await
    .expect("attacker-controlled profile should persist");
    let profile_user_id = user.id.clone();
    assert!(
        server
            .state
            .store
            .try_update_user(&profile_user_id, &instance.id, |record| {
                record.image_url = Some(format!("https://attacker.example.test/{suffix}.png"));
                true
            })
            .await
            .expect("attacker image profile should persist")
    );
    let user = server
        .state
        .store
        .get_user(&profile_user_id)
        .await
        .expect("attacker profile lookup should succeed")
        .expect("attacker user should remain present");
    assert!(user.image_url.is_some());
    add_membership(server, instance, &user, suffix).await;
    add_totp_factor(server, instance, &user).await;
    user
}

async fn add_membership(
    server: &TestServer,
    instance: &Instance,
    user: &auth_contracts::models::User,
    suffix: &str,
) -> OrgMembership {
    let org_id = format!("org_mailbox_{suffix}");
    let organization = Organization {
        id: org_id.clone(),
        instance_id: instance.id.clone(),
        name: format!("{suffix} mailbox test organization"),
        slug_key: format!("{}:{suffix}", instance.id),
        status: OrganizationStatus::Active,
        public_metadata: serde_json::Map::new(),
        created_at: now(),
    };
    server
        .state
        .store
        .create_organization(&organization)
        .await
        .expect("mailbox test organization should persist");
    let membership = OrgMembership {
        id: format!("mem_mailbox_{suffix}"),
        instance_id: instance.id.clone(),
        org_id: organization.id,
        user_id: user.id.clone(),
        member_key: format!("org_mailbox_{suffix}:{}", user.id),
        role: "admin".to_owned(),
        created_at: now(),
    };
    server
        .state
        .store
        .create_membership(&membership)
        .await
        .expect("mailbox test membership should persist");
    membership
}

async fn add_totp_factor(
    server: &TestServer,
    instance: &Instance,
    user: &auth_contracts::models::User,
) {
    let secret_box = secretbox::encrypt(&server.state.options.master_key, &[42; 20])
        .expect("test TOTP secret should encrypt");
    server
        .state
        .store
        .create_mfa_factor(&MfaFactor {
            id: format!("mfa_mailbox_{}", user.id),
            instance_id: instance.id.clone(),
            user_id: user.id.clone(),
            r#type: "totp".to_owned(),
            secret_box: Some(secret_box),
            recovery_codes: None,
            confirmed: true,
            created_at: now(),
            last_used_totp_step: None,
        })
        .await
        .expect("test TOTP factor should persist");
}

async fn get_verification_form(server: &TestServer, slug: &str, token: &str) -> String {
    let response = server
        .client()
        .get(format!("{}/ui/{slug}/verify-email", server.origin))
        .query(&[("token", token)])
        .send()
        .await
        .expect("verification GET should reach the app");
    assert_eq!(response.status(), StatusCode::OK);
    response
        .text()
        .await
        .expect("verification form should contain HTML")
}

fn assert_form_has_mailbox_credentials(html: &str) {
    assert!(html.contains("name=\"token\""));
    assert!(html.contains("name=\"password\""));
    assert!(html.contains("name=\"name\""));
}

async fn assert_token_unconsumed(server: &TestServer, user_id: &str, purpose: &str, token: &str) {
    let token_key = tokens::sha256_hex(token);
    let records = server
        .state
        .store
        .list_user_verification_tokens(user_id, purpose)
        .await
        .expect("verification token listing should succeed");
    assert!(
        records
            .iter()
            .any(|record| { record.token_key == token_key && !record.consumed }),
        "GET should not consume the mailbox proof"
    );
}

async fn user_by_email(
    server: &TestServer,
    instance_id: &str,
    email: &str,
) -> auth_contracts::models::User {
    server
        .state
        .store
        .find_user_by_email(instance_id, email)
        .await
        .expect("mailbox user lookup should succeed")
        .expect("mailbox user should exist")
}

async fn sign_in(
    server: &TestServer,
    slug: &str,
    email: &str,
    password: &str,
) -> reqwest::Response {
    server
        .client()
        .post(format!("{}/ui/{slug}/sign-in", server.origin))
        .header(header::ORIGIN, &server.public_origin)
        .form(&[("email", email), ("password", password)])
        .send()
        .await
        .expect("password sign-in should reach the app")
}

fn now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("system time should be after the Unix epoch")
        .as_secs() as i64
}

async fn mailbox_scenario_guard() -> tokio::sync::MutexGuard<'static, ()> {
    MAILBOX_SCENARIO_LOCK
        .get_or_init(|| tokio::sync::Mutex::new(()))
        .lock()
        .await
}
