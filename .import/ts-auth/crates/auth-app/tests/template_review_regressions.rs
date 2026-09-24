use std::sync::atomic::{AtomicU64, Ordering};

use auth_app::{
    AuthOptions,
    app::{AppState, build_router, build_state_with_mode},
    features::users::{self, UserChanges},
};
use auth_contracts::models::{ApiKey, Instance};
use auth_security::tokens::sha256_hex;
use reqwest::{Client, StatusCode};
use serde_json::{Value, json};
use tokio::{net::TcpListener, task::JoinHandle};

mod support;

use support::terrabase_http::{TEST_TERRASCALE_API_KEY, TerraBaseHttpFixture};

struct TestServer {
    origin: String,
    slug: String,
    instance_id: String,
    management_key: String,
    state: AppState,
    task: JoinHandle<()>,
    storage: TerraBaseHttpFixture,
}

impl TestServer {
    async fn start() -> Self {
        let storage = TerraBaseHttpFixture::start().await;
        let listener = TcpListener::bind("127.0.0.1:0")
            .await
            .expect("template review listener should bind");
        let address = listener
            .local_addr()
            .expect("template review listener should have an address");
        static NEXT_DATABASE: AtomicU64 = AtomicU64::new(1);
        let id = NEXT_DATABASE.fetch_add(1, Ordering::Relaxed);
        let origin = format!("http://{address}");
        let public_origin = format!("http://localhost:{}", address.port());
        let options = AuthOptions {
            listen: "http://127.0.0.1:0".into(),
            public_origin,
            terrascale_api_url: storage.origin.clone(),
            terrascale_api_key: TEST_TERRASCALE_API_KEY.into(),
            terrascale_project_id: None,
            terrascale_tenant_id: None,
            database: format!("ts-auth-template-review-{id}"),
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
        let state =
            build_state_with_mode(options, true).expect("template review app state should build");
        let slug = format!("template-review-{id}");
        let instance = Instance {
            id: format!("ins_template-review-{id}"),
            slug: slug.clone(),
            name: "Template review test".into(),
            logo_url: None,
            theme_color: None,
            stripe_customer_id: None,
            created_at: now(),
        };
        state
            .store
            .create_instance(&instance)
            .await
            .expect("review instance should persist");
        let instance_id = instance.id.clone();
        let management_key = format!("sk_template_review_{id}");
        let api_key = ApiKey {
            id: format!("key_template_review_{id}"),
            instance_id: instance.id,
            name: "Template review key".into(),
            secret_key_hash: sha256_hex(&management_key),
            scopes: vec!["jwt-templates".into()],
            created_at: now(),
            last_used_at: None,
            revoked: false,
        };
        state
            .store
            .create("api_keys", &api_key.id, &api_key)
            .await
            .expect("review management key should persist");
        let router = build_router(state.clone());
        let task = tokio::spawn(async move {
            axum::serve(listener, router)
                .await
                .expect("template review server should remain available");
        });

        Self {
            origin,
            slug,
            instance_id,
            management_key,
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
            .expect("template review HTTP client should build")
    }

    fn collection_url(&self) -> String {
        format!("{}/v1/{}/jwt-templates", self.origin, self.slug)
    }

    fn template_url(&self, name: &str) -> String {
        format!("{}/{}", self.collection_url(), name)
    }
}

impl Drop for TestServer {
    fn drop(&mut self) {
        self.task.abort();
    }
}

fn now() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .expect("system time should be valid")
        .as_secs() as i64
}

async fn create_template(
    client: &Client,
    server: &TestServer,
    name: &str,
    claims: Value,
) -> reqwest::Response {
    client
        .post(server.collection_url())
        .bearer_auth(&server.management_key)
        .json(&json!({"name": name, "claims": claims}))
        .send()
        .await
        .expect("template create request should reach the app")
}

async fn list_templates(client: &Client, server: &TestServer) -> Value {
    client
        .get(server.collection_url())
        .bearer_auth(&server.management_key)
        .send()
        .await
        .expect("template list request should reach the app")
        .json()
        .await
        .expect("template list response should be JSON")
}

#[tokio::test]
async fn unknown_template_placeholders_are_rejected_on_create_and_update() {
    let server = TestServer::start().await;
    let client = server.client();

    let invalid_create = create_template(
        &client,
        &server,
        "unknown-create",
        json!({"sub": "{{user.mail}}"}),
    )
    .await;
    assert_eq!(invalid_create.status(), StatusCode::BAD_REQUEST);
    let error: Value = invalid_create
        .json()
        .await
        .expect("placeholder error should be JSON");
    assert_eq!(error["error"]["code"], "invalid_placeholder");
    assert!(
        error["error"]["message"]
            .as_str()
            .is_some_and(|message| message.contains("user.mail"))
    );

    let valid = create_template(
        &client,
        &server,
        "known",
        json!({"sub": "{{user.id}}", "org": "{{org.id}}"}),
    )
    .await;
    assert_eq!(valid.status(), StatusCode::OK);
    let invalid_update = client
        .patch(server.template_url("known"))
        .bearer_auth(&server.management_key)
        .json(&json!({"claims": {"sub": "{{user.mail}}"}}))
        .send()
        .await
        .expect("template update request should reach the app");
    assert_eq!(invalid_update.status(), StatusCode::BAD_REQUEST);
    let error: Value = invalid_update
        .json()
        .await
        .expect("placeholder error should be JSON");
    assert_eq!(error["error"]["code"], "invalid_placeholder");

    let malformed_literal = create_template(
        &client,
        &server,
        "literal",
        json!({"sub": "{{User.ID}}/{{user.id"}),
    )
    .await;
    assert_eq!(malformed_literal.status(), StatusCode::OK);
    let templates = list_templates(&client, &server).await;
    assert_eq!(templates.as_array().expect("list is an array").len(), 2);
    let known = templates
        .as_array()
        .expect("list is an array")
        .iter()
        .find(|template| template["name"] == "known")
        .expect("valid template should remain stored");
    assert_eq!(known["claims"]["sub"], "{{user.id}}");
    assert_eq!(known["claims"]["org"], "{{org.id}}");
    let literal = templates
        .as_array()
        .expect("list is an array")
        .iter()
        .find(|template| template["name"] == "literal")
        .expect("malformed placeholder text should remain literal");
    assert_eq!(literal["claims"]["sub"], "{{User.ID}}/{{user.id");
}

#[tokio::test]
async fn combined_email_and_username_conflict_has_a_generic_identifier_code() {
    let server = TestServer::start().await;
    let first = users::create_user(
        &server.state,
        &server.instance_id,
        Some("first@example.test"),
        Some("first"),
        None,
        None,
    )
    .await
    .expect("first user should be created");
    let second = users::create_user(
        &server.state,
        &server.instance_id,
        Some("second@example.test"),
        Some("second"),
        None,
        None,
    )
    .await
    .expect("second user should be created");

    let error = users::update_user(
        &server.state,
        &server.instance_id,
        &first.id,
        UserChanges {
            email: Some(Some("replacement@example.test".into())),
            username: Some(Some(second.username.expect("second user has a username"))),
            ..UserChanges::default()
        },
    )
    .await
    .expect_err("duplicate username should conflict");
    assert_eq!(error.status, StatusCode::CONFLICT);
    assert_eq!(error.code, "identifier_taken");
}

#[tokio::test]
async fn template_create_keeps_ambiguous_claims_and_accepts_late_visible_commit() {
    let server = TestServer::start().await;
    let client = server.client();

    server
        .storage
        .fail_next_create_only_before_commit("jwt_templates");
    let failed =
        create_template(&client, &server, "uncertain", json!({"sub": "{{user.id}}"})).await;
    assert_eq!(failed.status(), StatusCode::INTERNAL_SERVER_ERROR);
    assert!(
        list_templates(&client, &server)
            .await
            .as_array()
            .expect("list is an array")
            .is_empty()
    );

    let retry = create_template(&client, &server, "uncertain", json!({"sub": "{{user.id}}"})).await;
    assert_eq!(retry.status(), StatusCode::CONFLICT);

    server
        .storage
        .fail_next_create_only_after_commit("jwt_templates");
    let committed =
        create_template(&client, &server, "committed", json!({"sub": "{{user.id}}"})).await;
    assert_eq!(committed.status(), StatusCode::OK);
    let templates = list_templates(&client, &server).await;
    assert_eq!(templates.as_array().expect("list is an array").len(), 1);
    let duplicate =
        create_template(&client, &server, "committed", json!({"sub": "{{user.id}}"})).await;
    assert_eq!(duplicate.status(), StatusCode::CONFLICT);
}

#[tokio::test]
async fn ambiguous_template_rename_keeps_claim_until_commit_is_known() {
    let server = TestServer::start().await;
    let client = server.client();
    let created =
        create_template(&client, &server, "original", json!({"sub": "{{user.id}}"})).await;
    assert_eq!(created.status(), StatusCode::OK);

    server
        .storage
        .fail_next_compare_and_put_before_commit("jwt_templates");
    let failed_rename = client
        .patch(server.template_url("original"))
        .bearer_auth(&server.management_key)
        .json(&json!({"name": "renamed"}))
        .send()
        .await
        .expect("template rename request should reach the app");
    assert_eq!(failed_rename.status(), StatusCode::INTERNAL_SERVER_ERROR);

    let retry_as_new_template =
        create_template(&client, &server, "renamed", json!({"sub": "{{user.id}}"})).await;
    assert_eq!(retry_as_new_template.status(), StatusCode::CONFLICT);
    let templates = list_templates(&client, &server).await;
    assert_eq!(templates.as_array().expect("list is an array").len(), 1);
    assert_eq!(templates[0]["name"], "original");
}
