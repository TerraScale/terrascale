use auth_app::{
    AuthOptions,
    app::{AppState, build_router, build_state_with_mode},
    features::users::{self, UserChanges},
};
use auth_contracts::models::{OrgMembership, ScimUserLink, User};
use auth_store::Predicate;
use reqwest::{Client, StatusCode, header};
use serde_json::{Value, json};
use std::time::Duration;
use tokio::{net::TcpListener, task::JoinHandle, time::timeout};

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
            .expect("test app listener should bind");
        let address = listener
            .local_addr()
            .expect("test app listener should have an address");
        let public_origin = format!("http://localhost:{}", address.port());
        static NEXT_DATABASE: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1);
        let database = format!(
            "ts-auth-scim-identity-{}",
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
            .expect("test client should build")
    }
}

impl Drop for TestServer {
    fn drop(&mut self) {
        self.task.abort();
    }
}

async fn instance_wide_directory(
    server: &TestServer,
    client: &Client,
    slug: &str,
) -> (String, String, String) {
    let response = client
        .post(format!("{}/v1/instances", server.origin))
        .bearer_auth("test-only-operator-key")
        .header("Idempotency-Key", format!("scim-identity-{slug}"))
        .json(&json!({ "slug": slug, "name": "SCIM identity regression" }))
        .send()
        .await
        .expect("instance creation should reach the service");
    assert_eq!(response.status(), StatusCode::OK);
    let bootstrap: Value = response
        .json()
        .await
        .expect("instance response should be JSON");
    let instance_id = bootstrap["instance"]["id"]
        .as_str()
        .expect("instance ID is returned")
        .to_owned();
    let api_key = bootstrap["api_key"]
        .as_str()
        .expect("initial API key is returned");

    let response = client
        .post(format!("{}/v1/{slug}/directories", server.origin))
        .bearer_auth(api_key)
        .json(&json!({ "instance_wide": true }))
        .send()
        .await
        .expect("directory creation should reach the service");
    assert_eq!(response.status(), StatusCode::CREATED);
    let directory: Value = response
        .json()
        .await
        .expect("directory response should be JSON");
    (
        instance_id,
        directory["id"]
            .as_str()
            .expect("directory ID is returned")
            .to_owned(),
        directory["token"]
            .as_str()
            .expect("directory token is returned")
            .to_owned(),
    )
}

async fn public_signup(server: &TestServer, client: &Client, slug: &str, email: &str) -> User {
    let response = client
        .post(format!("{}/ui/{slug}/sign-up", server.origin))
        .header(header::ORIGIN, &server.public_origin)
        .form(&[
            ("email", email),
            ("password", "a-valid-password"),
            ("name", "Public Signup"),
        ])
        .send()
        .await
        .expect("public signup should reach the service");
    assert_eq!(response.status(), StatusCode::SEE_OTHER);
    let instance = server
        .state
        .store
        .find_instance_by_slug(slug)
        .await
        .expect("instance lookup should succeed")
        .expect("instance should exist");
    let user = server
        .state
        .store
        .find_user_by_email(&instance.id, email)
        .await
        .expect("signup user lookup should succeed")
        .expect("signup user should exist");
    assert!(!user.email_verified);
    assert!(!user.identity_established);
    assert!(
        user.password_hash.is_some(),
        "signup must retain a password"
    );
    user
}

async fn scim_post(
    origin: &str,
    client: &Client,
    directory_id: &str,
    directory_token: &str,
    email: &str,
) -> reqwest::Response {
    client
        .post(format!("{origin}/scim/{directory_id}/Users"))
        .bearer_auth(directory_token)
        .json(&json!({
            "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
            "userName": email,
            "active": true,
            "name": { "givenName": "Public", "familyName": "Signup" },
        }))
        .send()
        .await
        .expect("SCIM create should reach the service")
}

async fn mark_as_verified_legacy_user(server: &TestServer, user_id: &str) {
    // A pre-marker stored record can be verified without carrying the new
    // identity_established field. Bypass the current store's promotion rule to
    // reproduce that historical document exactly.
    let (mut user, version) = server
        .state
        .store
        .get_versioned::<User>("users", user_id)
        .await
        .expect("legacy user should be readable")
        .expect("legacy user should exist");
    user.email_verified = true;
    assert!(!user.identity_established);
    assert!(
        server
            .state
            .store
            .compare_and_put("users", user_id, &user, &version)
            .await
            .expect("legacy verification should persist")
    );
}

#[tokio::test]
async fn scim_cannot_attach_an_unverified_public_signup() {
    let server = TestServer::start().await;
    let client = server.client();
    let slug = "scim-unverified-signup";
    let email = "unverified@example.test";
    let (instance_id, directory_id, token) = instance_wide_directory(&server, &client, slug).await;
    let signup = public_signup(&server, &client, slug, email).await;
    assert_eq!(signup.instance_id, instance_id);

    let response = scim_post(&server.origin, &client, &directory_id, &token, email).await;
    assert_eq!(response.status(), StatusCode::CONFLICT);
    let body: Value = response.json().await.expect("SCIM conflict should be JSON");
    assert_eq!(body["scimType"], "uniqueness");

    let after = server
        .state
        .store
        .get_user(&signup.id)
        .await
        .expect("signup user should be readable")
        .expect("signup user should remain");
    assert!(!after.email_verified);
    assert!(!after.identity_established);
    assert_eq!(after.password_hash, signup.password_hash);
    assert!(after.scim_instance_wide_directory_ids.is_empty());
    assert!(
        server
            .state
            .store
            .get::<ScimUserLink>("scim_user_links", &format!("{directory_id}:{}", signup.id))
            .await
            .expect("SCIM link should be queryable")
            .is_none(),
        "an unverified signup must not gain a SCIM link"
    );
    let memberships: Vec<OrgMembership> = server
        .state
        .store
        .query_all(
            "memberships",
            &[Predicate::equal("userId", &signup.id).expect("user ID is valid")],
            100,
        )
        .await
        .expect("memberships should be queryable");
    assert!(memberships.is_empty());
}

#[tokio::test]
async fn scim_establishes_currently_verified_legacy_signup_before_linking() {
    let server = TestServer::start().await;
    let client = server.client();
    let slug = "scim-verified-legacy";
    let email = "verified@example.test";
    let (instance_id, directory_id, token) = instance_wide_directory(&server, &client, slug).await;
    let signup = public_signup(&server, &client, slug, email).await;
    assert_eq!(signup.instance_id, instance_id);
    mark_as_verified_legacy_user(&server, &signup.id).await;
    let legacy = server
        .state
        .store
        .get_user(&signup.id)
        .await
        .expect("legacy user should be readable")
        .expect("legacy user should exist");
    assert!(legacy.email_verified);
    assert!(!legacy.identity_established);

    let response = scim_post(&server.origin, &client, &directory_id, &token, email).await;
    assert_eq!(response.status(), StatusCode::CREATED);
    let body: Value = response.json().await.expect("SCIM user should be JSON");
    assert_eq!(body["id"], signup.id);

    let after = server
        .state
        .store
        .get_user(&signup.id)
        .await
        .expect("attached user should be readable")
        .expect("attached user should exist");
    assert!(after.identity_established);
    assert!(after.email_verified);
    assert_eq!(after.password_hash, signup.password_hash);
    assert!(
        after
            .scim_instance_wide_directory_ids
            .contains(&directory_id)
    );
    let link = server
        .state
        .store
        .get::<ScimUserLink>("scim_user_links", &format!("{directory_id}:{}", signup.id))
        .await
        .expect("SCIM link should be readable")
        .expect("verified user should gain a SCIM link");
    assert!(link.active);
}

#[tokio::test]
async fn scim_legacy_promotion_cannot_attach_after_email_rekey() {
    let server = TestServer::start().await;
    let client = server.client();
    let slug = "scim-legacy-rekey-race";
    let old_email = "old-mailbox@example.test";
    let new_email = "new-mailbox@example.test";
    let (instance_id, directory_id, token) = instance_wide_directory(&server, &client, slug).await;
    let signup = public_signup(&server, &client, slug, old_email).await;
    mark_as_verified_legacy_user(&server, &signup.id).await;

    let mut promotion = server._storage.pause_next_compare_and_put("users");
    let request_origin = server.origin.clone();
    let request_client = client.clone();
    let request_directory = directory_id.clone();
    let request_token = token.clone();
    let request = tokio::spawn(async move {
        scim_post(
            &request_origin,
            &request_client,
            &request_directory,
            &request_token,
            old_email,
        )
        .await
    });
    timeout(Duration::from_secs(3), promotion.wait_until_paused())
        .await
        .expect("SCIM should reach legacy identity promotion");

    users::update_user(
        &server.state,
        &instance_id,
        &signup.id,
        UserChanges {
            email: Some(Some(new_email.to_owned())),
            ..UserChanges::default()
        },
    )
    .await
    .expect("verified user email should rekey before SCIM promotion");
    promotion.resume();

    let response = timeout(Duration::from_secs(5), request)
        .await
        .expect("SCIM request should finish")
        .expect("SCIM request task should complete");
    assert_eq!(response.status(), StatusCode::CONFLICT);
    let after = server
        .state
        .store
        .get_user(&signup.id)
        .await
        .expect("rekeyed user should be readable")
        .expect("rekeyed user should exist");
    assert_eq!(after.email.as_deref(), Some(new_email));
    assert!(!after.email_verified);
    assert!(after.identity_established);
    assert!(after.scim_instance_wide_directory_ids.is_empty());
    assert!(
        server
            .state
            .store
            .get::<ScimUserLink>("scim_user_links", &format!("{directory_id}:{}", signup.id))
            .await
            .expect("SCIM link should be queryable")
            .is_none(),
        "a stale mailbox match must not create a SCIM link"
    );
}
