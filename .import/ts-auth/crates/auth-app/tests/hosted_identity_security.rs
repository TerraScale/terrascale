use auth_app::{
    AppState, AuthOptions,
    app::build_state_with_mode,
    build_router,
    features::users::{self, UserChanges},
};
use auth_contracts::models::{
    Instance, MfaFactor, OrgDomain, Organization, OrganizationStatus, ScimProvisioningPhase,
};
use auth_security::{secretbox, tokens};
use axum::http::header;
use reqwest::{Client, StatusCode};
use serde_json::json;
use std::time::{SystemTime, UNIX_EPOCH};
use tokio::{net::TcpListener, task::JoinHandle};

mod support;

use support::terrabase_http::{TEST_TERRASCALE_API_KEY, TerraBaseHttpFixture};

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
            .expect("app listener should bind");
        let address = listener
            .local_addr()
            .expect("app listener should have an address");
        let public_origin = format!("http://localhost:{}", address.port());
        static NEXT_DATABASE: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1);
        let database = format!(
            "ts-auth-hosted-identity-{}",
            NEXT_DATABASE.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
        );
        let options = AuthOptions {
            listen: "http://127.0.0.1:0".into(),
            public_origin: public_origin.clone(),
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
        let state = build_state_with_mode(options, true).expect("test app state should build");
        let router = build_router(state.clone());
        let task = tokio::spawn(async move {
            axum::serve(listener, router)
                .await
                .expect("test app should remain available");
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
            .redirect(reqwest::redirect::Policy::none())
            .build()
            .expect("client should build")
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

#[tokio::test]
async fn signup_compensates_user_session_and_quota_when_verification_token_creation_fails() {
    let server = TestServer::start().await;
    let instance = create_instance(&server, "signup-rollback").await;
    server
        ._storage
        .fail_next_create_only_before_commit("verification_tokens");

    let response = server
        .client()
        .post(format!("{}/ui/{}/sign-up", server.origin, instance.slug))
        .header(header::ORIGIN, &server.public_origin)
        .form(&[
            ("email", "rollback@example.com"),
            ("password", "a-valid-password"),
            ("name", "Rollback User"),
        ])
        .send()
        .await
        .expect("sign-up should reach app");
    assert_eq!(response.status(), StatusCode::SERVICE_UNAVAILABLE);

    assert!(
        server
            .state
            .store
            .find_user_by_email(&instance.id, "rollback@example.com")
            .await
            .expect("email lookup should succeed")
            .is_none(),
        "failed registration should release the email claim"
    );
    let (user_records, _) = server
        .state
        .store
        .list_users(&instance.id, 100, None)
        .await
        .expect("user listing should succeed");
    assert_eq!(
        user_records.len(),
        1,
        "the soft-deleted user record should remain available"
    );
    let rolled_back = &user_records[0];
    assert!(rolled_back.deleted, "the incomplete user should be deleted");
    assert!(
        server
            .state
            .store
            .list_sessions_by_user(&rolled_back.id)
            .await
            .expect("session listing should succeed")
            .is_empty(),
        "the incomplete registration must not retain a session"
    );

    let replacement = users::create_user(
        &server.state,
        &instance.id,
        Some("rollback@example.com"),
        None,
        None,
        None,
    )
    .await
    .expect("released email and quota claims should support a replacement user");
    assert_eq!(replacement.quota_seat, Some(0));
    users::deprovision_user(&server.state, &instance.id, &replacement.id)
        .await
        .expect("replacement user should deprovision");
}

async fn create_instance(server: &TestServer, slug: &str) -> Instance {
    let instance = Instance {
        id: format!("ins_{slug}"),
        slug: slug.into(),
        name: format!("{slug} test"),
        logo_url: None,
        theme_color: None,
        stripe_customer_id: None,
        created_at: now(),
    };
    server
        .state
        .store
        .create_instance(&instance)
        .await
        .expect("instance should persist");
    instance
}

#[tokio::test]
async fn email_rekey_invalidates_old_mailbox_bound_links() {
    let server = TestServer::start().await;
    let instance = create_instance(&server, "email-rekey").await;
    let user = users::create_user(
        &server.state,
        &instance.id,
        Some("old@example.com"),
        None,
        Some("original-password"),
        Some("Old Address"),
    )
    .await
    .expect("user should be created");
    let old_magic = users::issue_token(
        &server.state,
        &instance.id,
        Some(&user.id),
        "magic_link",
        600,
    )
    .await
    .expect("magic token should be issued");
    let old_reset = users::issue_token(
        &server.state,
        &instance.id,
        Some(&user.id),
        "reset_password",
        600,
    )
    .await
    .expect("reset token should be issued");
    let old_verification = users::issue_token(
        &server.state,
        &instance.id,
        Some(&user.id),
        "verify_email",
        600,
    )
    .await
    .expect("verification token should be issued");

    let changes = UserChanges {
        email: Some(Some("new@example.com".into())),
        ..UserChanges::default()
    };
    users::update_user(&server.state, &instance.id, &user.id, changes)
        .await
        .expect("email should be rekeyed");

    let client = server.client();
    let magic = client
        .get(format!(
            "{}/ui/{}/magic-link/consume",
            server.origin, instance.slug
        ))
        .query(&[("token", old_magic.as_str())])
        .send()
        .await
        .expect("magic-link request should reach app");
    assert_eq!(magic.status(), StatusCode::BAD_REQUEST);
    assert!(
        magic.headers().get(header::SET_COOKIE).is_none(),
        "old mailbox must not receive a session"
    );

    let verification = client
        .get(format!(
            "{}/ui/{}/verify-email",
            server.origin, instance.slug
        ))
        .query(&[("token", old_verification.as_str())])
        .send()
        .await
        .expect("email-verification request should reach app");
    assert_eq!(verification.status(), StatusCode::BAD_REQUEST);
    assert!(
        !server
            .state
            .store
            .get_user(&user.id)
            .await
            .expect("user lookup should succeed")
            .expect("user should remain")
            .email_verified,
        "a previous mailbox must not verify the rekeyed address"
    );

    let reset = client
        .post(format!(
            "{}/ui/{}/reset-password",
            server.origin, instance.slug
        ))
        .header(header::ORIGIN, &server.public_origin)
        .form(&[
            ("token", old_reset.as_str()),
            ("password", "attacker-chosen-password"),
        ])
        .send()
        .await
        .expect("password-reset request should reach app");
    assert_eq!(reset.status(), StatusCode::BAD_REQUEST);
    let current = server
        .state
        .store
        .get_user(&user.id)
        .await
        .expect("user lookup should succeed")
        .expect("user should remain");
    assert_eq!(
        current.password_hash, user.password_hash,
        "old mailbox must not reset the password"
    );
}

#[tokio::test]
async fn mfa_recovery_link_carries_pending_sign_in_without_a_session() {
    let server = TestServer::start().await;
    let instance = create_instance(&server, "mfa-recovery").await;
    let user = users::create_user(
        &server.state,
        &instance.id,
        Some("mfa@example.com"),
        None,
        Some("correct-password"),
        Some("MFA User"),
    )
    .await
    .expect("user should be created");
    let recovery_code = "one-use-recovery-code";
    let secret_box = secretbox::encrypt(&server.state.options.master_key, &[42; 20])
        .expect("TOTP seed should encrypt");
    server
        .state
        .store
        .create_mfa_factor(&MfaFactor {
            id: "mfa_totp_test".into(),
            instance_id: instance.id.clone(),
            user_id: user.id.clone(),
            r#type: "totp".into(),
            secret_box: Some(secret_box),
            recovery_codes: None,
            confirmed: true,
            created_at: now(),
            last_used_totp_step: None,
        })
        .await
        .expect("TOTP factor should persist");
    server
        .state
        .store
        .create_mfa_factor(&MfaFactor {
            id: "mfa_recovery_test".into(),
            instance_id: instance.id.clone(),
            user_id: user.id.clone(),
            r#type: "recovery".into(),
            secret_box: None,
            recovery_codes: Some(vec![tokens::sha256_hex(recovery_code)]),
            confirmed: true,
            created_at: now(),
            last_used_totp_step: None,
        })
        .await
        .expect("recovery factor should persist");

    let client = server.client();
    let sign_in = client
        .post(format!("{}/ui/{}/sign-in", server.origin, instance.slug))
        .header(header::ORIGIN, &server.public_origin)
        .form(&[
            ("email", "mfa@example.com"),
            ("password", "correct-password"),
        ])
        .send()
        .await
        .expect("password sign-in should reach app");
    assert_eq!(sign_in.status(), StatusCode::SEE_OTHER);
    let location = sign_in
        .headers()
        .get(header::LOCATION)
        .expect("MFA challenge should redirect")
        .to_str()
        .expect("location should be text");
    assert!(location.starts_with(&format!("/ui/{}/mfa/verify?token=", instance.slug)));
    let challenge_url = reqwest::Url::parse(&format!("{}{}", server.origin, location))
        .expect("challenge URL should parse");
    let pending_token = challenge_url
        .query_pairs()
        .find(|(key, _)| key == "token")
        .map(|(_, value)| value.into_owned())
        .expect("pending token should be present");

    let recovery_page = client
        .get(format!(
            "{}/ui/{}/mfa/recovery",
            server.origin, instance.slug
        ))
        .query(&[
            ("token", pending_token.as_str()),
            ("next", "/ui/mfa-recovery/account"),
        ])
        .send()
        .await
        .expect("recovery page should reach app");
    assert_eq!(
        recovery_page.status(),
        StatusCode::OK,
        "recovery works before a session exists"
    );
    let html = recovery_page
        .text()
        .await
        .expect("page body should be text");
    assert!(
        html.contains("name=\"token\""),
        "recovery form must preserve its pending challenge"
    );

    let recovery = client
        .post(format!(
            "{}/ui/{}/mfa/recovery/consume",
            server.origin, instance.slug
        ))
        .header(header::ORIGIN, &server.public_origin)
        .form(&[
            ("token", pending_token.as_str()),
            ("next", "/ui/mfa-recovery/account"),
            ("code", recovery_code),
        ])
        .send()
        .await
        .expect("recovery submit should reach app");
    assert_eq!(recovery.status(), StatusCode::SEE_OTHER);
    assert_eq!(
        recovery
            .headers()
            .get(header::LOCATION)
            .and_then(|value| value.to_str().ok()),
        Some("/ui/mfa-recovery/account")
    );
    assert!(
        recovery.headers().get(header::SET_COOKIE).is_some(),
        "successful recovery should establish the session"
    );
    let factor = server
        .state
        .store
        .get_mfa_factor("mfa_recovery_test")
        .await
        .expect("factor lookup should succeed")
        .expect("factor should remain");
    assert_eq!(
        factor.recovery_codes,
        Some(Vec::new()),
        "the recovery code should be single-use"
    );
}

#[tokio::test]
async fn email_domain_membership_waits_until_the_mailbox_is_verified() {
    let server = TestServer::start().await;
    let instance = create_instance(&server, "verified-domain").await;
    server
        .state
        .store
        .create_organization(&Organization {
            id: "org_domain_verified".into(),
            instance_id: instance.id.clone(),
            name: "Verified domain organization".into(),
            slug_key: format!("{}:verified-domain", instance.id),
            status: OrganizationStatus::Active,
            public_metadata: Default::default(),
            created_at: now(),
        })
        .await
        .expect("domain organization should persist");
    server
        .state
        .store
        .create_org_domain(&OrgDomain {
            id: "domain_verified_example".into(),
            instance_id: instance.id.clone(),
            org_id: "org_domain_verified".into(),
            domain: "example.com".into(),
            domain_key: format!("{}:example.com", instance.id),
            created_at: now(),
        })
        .await
        .expect("verified domain policy should persist");
    let client = server.client();
    let sign_up = client
        .post(format!("{}/ui/{}/sign-up", server.origin, instance.slug))
        .header(header::ORIGIN, &server.public_origin)
        .form(&[
            ("email", "member@example.com"),
            ("password", "a-valid-password"),
            ("name", "Unverified User"),
        ])
        .send()
        .await
        .expect("sign-up should reach app");
    assert_eq!(sign_up.status(), StatusCode::SEE_OTHER);
    let user = server
        .state
        .store
        .find_user_by_email(&instance.id, "member@example.com")
        .await
        .expect("email lookup should succeed")
        .expect("sign-up should create the user");
    assert!(!user.email_verified);
    assert!(
        server
            .state
            .store
            .list_memberships_by_user(&user.id)
            .await
            .expect("membership lookup should succeed")
            .is_empty(),
        "an unverified suffix claim must not grant membership"
    );

    let token = users::issue_token(
        &server.state,
        &instance.id,
        Some(&user.id),
        "verify_email",
        600,
    )
    .await
    .expect("verification token should be issued");
    let verified = client
        .get(format!(
            "{}/ui/{}/verify-email",
            server.origin, instance.slug
        ))
        .query(&[("token", token.as_str())])
        .send()
        .await
        .expect("verification link should reach app");
    assert_eq!(verified.status(), StatusCode::OK);
    let verify_form = verified.text().await.expect("form body should be text");
    assert!(verify_form.contains("name=\"password\""));
    assert!(verify_form.contains("name=\"name\""));
    let completed = client
        .post(format!(
            "{}/ui/{}/verify-email",
            server.origin, instance.slug
        ))
        .header(header::ORIGIN, &server.public_origin)
        .form(&[
            ("token", token.as_str()),
            ("password", "verified-mailbox-password"),
            ("name", "Verified User"),
        ])
        .send()
        .await
        .expect("verification completion should reach app");
    assert_eq!(completed.status(), StatusCode::OK);
    let tombstoned = server
        .state
        .store
        .get_user(&user.id)
        .await
        .expect("original user lookup should succeed")
        .expect("tombstoned user record should remain");
    assert!(tombstoned.deleted, "untrusted signup ID should be retired");
    let verified_user = server
        .state
        .store
        .find_user_by_email(&instance.id, "member@example.com")
        .await
        .expect("current mailbox lookup should succeed")
        .expect("verified user should own the mailbox");
    assert_ne!(verified_user.id, user.id);
    assert!(verified_user.email_verified);
    assert!(verified_user.identity_established);
    assert_eq!(verified_user.name.as_deref(), Some("Verified User"));
    let memberships = server
        .state
        .store
        .list_memberships_by_user(&verified_user.id)
        .await
        .expect("membership lookup should succeed");
    assert_eq!(
        memberships.len(),
        1,
        "verification should enroll under the current org-domain policy exactly once"
    );

    let federated = users::find_or_create_verified_user(
        &server.state,
        &instance.id,
        "member@example.com",
        Some("Verified User"),
    )
    .await
    .expect("a verified sign-in should remain idempotent");
    assert_eq!(federated.id, verified_user.id);
    assert_eq!(
        server
            .state
            .store
            .list_memberships_by_user(&verified_user.id)
            .await
            .expect("membership lookup should succeed")
            .len(),
        1
    );
}

#[tokio::test]
async fn user_metadata_updates_preserve_the_internal_quota_claim() {
    let server = TestServer::start().await;
    let instance = create_instance(&server, "metadata-quota").await;
    let user = users::create_user(
        &server.state,
        &instance.id,
        Some("seat@example.com"),
        None,
        None,
        None,
    )
    .await
    .expect("user should be created");
    assert!(!user.deleted, "normal users remain active at creation");
    assert!(
        user.scim_provisioning.is_none(),
        "normal users have no provisional SCIM owner"
    );
    assert!(user.scim_instance_wide_directory_ids.is_empty());
    assert_eq!(user.quota_seat, Some(0));
    let changes = UserChanges {
        private_metadata: Some(
            serde_json::from_value(json!({"department": "support"}))
                .expect("metadata object should decode"),
        ),
        ..UserChanges::default()
    };
    let updated = users::update_user(&server.state, &instance.id, &user.id, changes)
        .await
        .expect("metadata should update");
    assert_eq!(
        updated
            .private_metadata
            .get("department")
            .and_then(|value| value.as_str()),
        Some("support")
    );
    assert_eq!(
        updated.quota_seat,
        Some(0),
        "customer metadata must not carry or overwrite quota ownership"
    );

    users::deprovision_user(&server.state, &instance.id, &user.id)
        .await
        .expect("user should deprovision");
    let reclaimed = server
        .state
        .store
        .claim_quota_seat(&instance.id, 1, "quota-probe")
        .await
        .expect("deleted user's quota seat should be reusable");
    assert_eq!(reclaimed, 0);
    server
        .state
        .store
        .release_quota_seat_if_owned(&instance.id, reclaimed, "quota-probe")
        .await
        .expect("probe seat should release");
}

#[tokio::test]
async fn scim_user_creation_starts_hidden_and_owned_by_its_directory() {
    let server = TestServer::start().await;
    let instance = create_instance(&server, "scim-provisional").await;
    let provisional = users::create_provisional_scim_user(
        &server.state,
        &instance.id,
        "dir_scim_provisional",
        "provisional@example.com",
        Some("Provisioning User"),
    )
    .await
    .expect("provisional SCIM user should be created");

    assert!(provisional.deleted, "provisional identities are not active");
    let provisioning = provisional
        .scim_provisioning
        .as_ref()
        .expect("provisional SCIM owner should be persisted");
    assert_eq!(provisioning.directory_id, "dir_scim_provisional");
    assert_eq!(provisioning.phase, ScimProvisioningPhase::Pending);
    assert!(!provisional.banned);
    let stored = server
        .state
        .store
        .get::<auth_contracts::models::User>("users", &provisional.id)
        .await
        .expect("provisional user lookup should succeed")
        .expect("provisional user should be persisted");
    assert!(stored.deleted);
    assert_eq!(
        stored
            .scim_provisioning
            .as_ref()
            .map(|provisioning| provisioning.directory_id.as_str()),
        Some("dir_scim_provisional")
    );
    assert!(
        server
            .state
            .store
            .get_user(&provisional.id)
            .await
            .expect("normal user lookup should succeed")
            .is_none(),
        "provisional identities stay hidden from normal user lookup"
    );
}
