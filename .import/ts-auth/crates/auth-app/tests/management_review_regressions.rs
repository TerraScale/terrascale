use auth_app::{
    AuthOptions,
    app::{AppState, build_router, build_state_with_mode},
};
use auth_contracts::models::Session;
use auth_store::Predicate;
use reqwest::{Client, StatusCode, header};
use serde_json::{Value, json};
use std::{
    collections::HashSet,
    net::SocketAddr,
    sync::atomic::{AtomicUsize, Ordering},
};
use tokio::{net::TcpListener, task::JoinHandle};

mod support;

use support::terrabase_http::{TEST_TERRASCALE_API_KEY, TerraBaseHttpFixture};

const OPERATOR_KEY: &str = "management-review-operator-key";
const BODY_LIMIT: usize = 1024 * 1024;

struct TestServer {
    origin: String,
    state: AppState,
    task: JoinHandle<()>,
    storage: TerraBaseHttpFixture,
}

impl TestServer {
    async fn start() -> Self {
        let storage = TerraBaseHttpFixture::start().await;
        let listener = TcpListener::bind("127.0.0.1:0")
            .await
            .expect("management regression listener should bind");
        let address = listener
            .local_addr()
            .expect("management regression listener should have an address");
        static NEXT_DATABASE: AtomicUsize = AtomicUsize::new(1);
        let database = format!(
            "ts-auth-management-review-{}",
            NEXT_DATABASE.fetch_add(1, Ordering::Relaxed)
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
            operator_key: OPERATOR_KEY.into(),
            metrics_key: None,
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
        let state =
            build_state_with_mode(options, true).expect("management regression state should build");
        let app = build_router(state.clone()).into_make_service_with_connect_info::<SocketAddr>();
        let task = tokio::spawn(async move {
            axum::serve(listener, app)
                .await
                .expect("management regression server should remain available");
        });
        Self {
            origin: format!("http://{address}"),
            state,
            task,
            storage,
        }
    }

    fn client(&self) -> Client {
        Client::builder()
            .no_proxy()
            .redirect(reqwest::redirect::Policy::none())
            .build()
            .expect("management regression client should build")
    }
}

impl Drop for TestServer {
    fn drop(&mut self) {
        self.task.abort();
    }
}

async fn bootstrap(server: &TestServer, client: &Client, slug: &str) -> (String, String) {
    let response = client
        .post(format!("{}/v1/instances", server.origin))
        .header(header::AUTHORIZATION, format!("bEaReR {OPERATOR_KEY}"))
        .header("Idempotency-Key", format!("management-review-{slug}"))
        .json(&json!({ "slug": slug, "name": format!("{slug} test") }))
        .send()
        .await
        .expect("instance bootstrap request should reach the service");
    assert_eq!(response.status(), StatusCode::OK);
    let body: Value = response
        .json()
        .await
        .expect("instance bootstrap response should be JSON");
    (
        body["instance"]["id"]
            .as_str()
            .expect("bootstrap response should include instance id")
            .to_owned(),
        body["api_key"]
            .as_str()
            .expect("bootstrap response should include API key")
            .to_owned(),
    )
}

async fn create_user(
    server: &TestServer,
    client: &Client,
    slug: &str,
    api_key: &str,
    email: &str,
    username: &str,
) -> Value {
    let response = client
        .post(format!("{}/v1/{slug}/users", server.origin))
        .bearer_auth(api_key)
        .json(&json!({ "email": email, "username": username }))
        .send()
        .await
        .expect("management user request should reach the service");
    assert_eq!(response.status(), StatusCode::CREATED);
    response
        .json()
        .await
        .expect("management user response should be JSON")
}

#[tokio::test]
async fn management_and_bootstrap_http_contracts_validate_passwords_and_body_limits() {
    let server = TestServer::start().await;
    let client = server.client();
    let (instance_id, api_key) = bootstrap(&server, &client, "review-http-contracts").await;
    let stored_keys = server
        .state
        .store
        .list_api_keys(&instance_id)
        .await
        .expect("bootstrapped API key should be readable");
    let stored_key = serde_json::to_value(
        stored_keys
            .first()
            .expect("bootstrap should persist one API key"),
    )
    .expect("API key record should serialize");
    assert!(stored_key.get("secretKey").is_some());
    assert!(stored_key.get("secret_key_hash").is_none());
    assert_ne!(stored_key["secretKey"].as_str(), Some(api_key.as_str()));

    let wrong_slug = client
        .get(format!("{}/v1/another-instance/instance", server.origin))
        .bearer_auth(&api_key)
        .send()
        .await
        .expect("cross-instance management request should reach the service");
    assert_eq!(wrong_slug.status(), StatusCode::UNAUTHORIZED);

    let empty_password = client
        .post(format!("{}/v1/review-http-contracts/users", server.origin))
        .bearer_auth(&api_key)
        .json(&json!({ "email": "empty@example.test", "password": "" }))
        .send()
        .await
        .expect("empty-password request should reach the service");
    assert_eq!(empty_password.status(), StatusCode::BAD_REQUEST);
    let empty_password: Value = empty_password
        .json()
        .await
        .expect("empty-password response should be JSON");
    assert_eq!(empty_password["error"]["code"], "invalid_password");

    let omitted_password = client
        .post(format!("{}/v1/review-http-contracts/users", server.origin))
        .bearer_auth(&api_key)
        .json(&json!({ "email": "passwordless@example.test" }))
        .send()
        .await
        .expect("omitted-password request should reach the service");
    assert_eq!(omitted_password.status(), StatusCode::CREATED);

    let oversized = "x".repeat(BODY_LIMIT + 1);
    let oversized_management = client
        .post(format!("{}/v1/review-http-contracts/users", server.origin))
        .bearer_auth(&api_key)
        .header(header::CONTENT_TYPE, "application/json")
        .body(oversized.clone())
        .send()
        .await
        .expect("oversized management request should reach the service");
    assert_eq!(oversized_management.status(), StatusCode::PAYLOAD_TOO_LARGE);

    let malformed_management = client
        .post(format!("{}/v1/review-http-contracts/users", server.origin))
        .bearer_auth(&api_key)
        .header(header::CONTENT_TYPE, "application/json")
        .body("{")
        .send()
        .await
        .expect("malformed management request should reach the service");
    assert_eq!(malformed_management.status(), StatusCode::BAD_REQUEST);

    let oversized_bootstrap = client
        .post(format!("{}/v1/instances", server.origin))
        .bearer_auth(OPERATOR_KEY)
        .header("Idempotency-Key", "management-review-oversized")
        .header(header::CONTENT_TYPE, "application/json")
        .body(oversized)
        .send()
        .await
        .expect("oversized bootstrap request should reach the service");
    assert_eq!(oversized_bootstrap.status(), StatusCode::PAYLOAD_TOO_LARGE);

    let malformed_bootstrap = client
        .post(format!("{}/v1/instances", server.origin))
        .bearer_auth(OPERATOR_KEY)
        .header("Idempotency-Key", "management-review-malformed")
        .header(header::CONTENT_TYPE, "application/json")
        .body("{")
        .send()
        .await
        .expect("malformed bootstrap request should reach the service");
    assert_eq!(malformed_bootstrap.status(), StatusCode::BAD_REQUEST);
}

#[tokio::test]
async fn combined_identifier_patch_reports_a_neutral_conflict() {
    let server = TestServer::start().await;
    let client = server.client();
    let (_, api_key) = bootstrap(&server, &client, "review-conflict").await;
    let first = create_user(
        &server,
        &client,
        "review-conflict",
        &api_key,
        "first@example.test",
        "first",
    )
    .await;
    let first_id = first["id"]
        .as_str()
        .expect("created user should include its id");
    create_user(
        &server,
        &client,
        "review-conflict",
        &api_key,
        "second@example.test",
        "second",
    )
    .await;

    let response = client
        .patch(format!(
            "{}/v1/review-conflict/users/{}",
            server.origin, first_id
        ))
        .bearer_auth(&api_key)
        .json(&json!({
            "email": "new@example.test",
            "username": "second"
        }))
        .send()
        .await
        .expect("combined identifier patch should reach the service");
    assert_eq!(response.status(), StatusCode::CONFLICT);
    let body: Value = response
        .json()
        .await
        .expect("identifier conflict response should be JSON");
    assert_eq!(body["error"]["code"], "identifier_taken");
}

#[tokio::test]
async fn committed_user_mutations_return_success_when_audit_storage_fails() {
    let server = TestServer::start().await;
    let client = server.client();
    let (_, api_key) = bootstrap(&server, &client, "review-audit-success").await;

    server
        .storage
        .fail_next_create_only_before_commit("audit_events");
    let created = client
        .post(format!("{}/v1/review-audit-success/users", server.origin))
        .bearer_auth(&api_key)
        .json(&json!({ "email": "created@example.test" }))
        .send()
        .await
        .expect("user create with failed audit should reach the service");
    assert_eq!(created.status(), StatusCode::CREATED);
    let created: Value = created
        .json()
        .await
        .expect("committed user response should be JSON");
    let created_id = created["id"]
        .as_str()
        .expect("created user should include its id")
        .to_owned();
    assert!(
        server
            .state
            .store
            .get_user(&created_id)
            .await
            .expect("committed user should be readable")
            .is_some()
    );

    server
        .storage
        .fail_next_create_only_before_commit("audit_events");
    let updated = client
        .patch(format!(
            "{}/v1/review-audit-success/users/{created_id}",
            server.origin
        ))
        .bearer_auth(&api_key)
        .json(&json!({ "name": "Committed name" }))
        .send()
        .await
        .expect("user update with failed audit should reach the service");
    assert_eq!(updated.status(), StatusCode::OK);
    assert_eq!(
        server
            .state
            .store
            .get_user(&created_id)
            .await
            .expect("updated user should be readable")
            .expect("updated user should remain present")
            .name
            .as_deref(),
        Some("Committed name")
    );

    server
        .storage
        .fail_next_create_only_before_commit("audit_events");
    let deleted = client
        .delete(format!(
            "{}/v1/review-audit-success/users/{created_id}",
            server.origin
        ))
        .bearer_auth(&api_key)
        .send()
        .await
        .expect("user delete with failed audit should reach the service");
    assert_eq!(deleted.status(), StatusCode::NO_CONTENT);
    assert!(
        server
            .state
            .store
            .get_user(&created_id)
            .await
            .expect("deleted user should be readable")
            .expect("deleted user record should remain present")
            .deleted
    );
}

#[tokio::test]
async fn impersonation_uses_peer_ip_and_revokes_an_unreturned_session_on_audit_failure() {
    let server = TestServer::start().await;
    let client = server.client();
    let (_, api_key) = bootstrap(&server, &client, "review-impersonation").await;
    let user = create_user(
        &server,
        &client,
        "review-impersonation",
        &api_key,
        "impersonated@example.test",
        "impersonated",
    )
    .await;
    let user_id = user["id"]
        .as_str()
        .expect("created user should include its id")
        .to_owned();

    let successful = client
        .post(format!(
            "{}/v1/review-impersonation/users/{user_id}/impersonate",
            server.origin
        ))
        .bearer_auth(&api_key)
        .header("X-Forwarded-For", "203.0.113.19")
        .send()
        .await
        .expect("impersonation request should reach the service");
    assert_eq!(successful.status(), StatusCode::CREATED);
    let sessions = server
        .state
        .store
        .query_all::<Session>(
            "sessions",
            &[Predicate::equal("userId", &user_id).expect("user predicate should build")],
            100,
        )
        .await
        .expect("impersonation session should be readable");
    assert_eq!(sessions.len(), 1);
    assert_eq!(sessions[0].ip_address.as_deref(), Some("127.0.0.1"));
    assert_eq!(sessions[0].status, "active");
    let successful_session_id = sessions[0].id.clone();
    let existing_session_ids: HashSet<_> =
        sessions.iter().map(|session| session.id.clone()).collect();

    server
        .storage
        .fail_next_create_only_before_commit("audit_events");
    let failed_audit = client
        .post(format!(
            "{}/v1/review-impersonation/users/{user_id}/impersonate",
            server.origin
        ))
        .bearer_auth(&api_key)
        .header("X-Forwarded-For", "203.0.113.19")
        .send()
        .await
        .expect("impersonation request with failed audit should reach the service");
    assert_eq!(failed_audit.status(), StatusCode::INTERNAL_SERVER_ERROR);
    let body: Value = failed_audit
        .json()
        .await
        .expect("failed impersonation response should be readable");
    assert!(body.get("token").is_none());

    let sessions = server
        .state
        .store
        .query_all::<Session>(
            "sessions",
            &[Predicate::equal("userId", &user_id).expect("user predicate should build")],
            100,
        )
        .await
        .expect("all impersonation sessions should be readable");
    assert_eq!(sessions.len(), 2);
    assert!(
        sessions
            .iter()
            .all(|session| session.ip_address.as_deref() == Some("127.0.0.1"))
    );
    let new_sessions: Vec<_> = sessions
        .iter()
        .filter(|session| !existing_session_ids.contains(&session.id))
        .collect();
    assert_eq!(new_sessions.len(), 1);
    assert_eq!(new_sessions[0].status, "revoked");
    let successful_session = sessions
        .iter()
        .find(|session| session.id == successful_session_id)
        .expect("the earlier successful session should remain present");
    assert_eq!(successful_session.status, "active");
}
