use auth_app::{
    AuthOptions,
    app::{AppState, build_state_with_mode},
    build_router,
};
use auth_contracts::models::{
    ApiKey, Instance, OrgMembership, Organization, OrganizationStatus, User,
};
use auth_security::tokens::sha256_hex;
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
            .expect("app listener should bind");
        let address = listener
            .local_addr()
            .expect("app listener should have an address");
        let public_origin = format!("http://localhost:{}", address.port());
        static NEXT_DATABASE: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1);
        let database = format!(
            "ts-auth-authorization-{}",
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

async fn key(server: &TestServer, instance: &Instance, scope: &str) -> String {
    let secret = format!("sk_{}_{}", instance.id, scope);
    let record = ApiKey {
        id: secret.clone(),
        instance_id: instance.id.clone(),
        name: scope.into(),
        secret_key_hash: sha256_hex(&secret),
        scopes: vec![scope.into()],
        created_at: now(),
        last_used_at: None,
        revoked: false,
    };
    server
        .state
        .store
        .create("api_keys", &record.id, &record)
        .await
        .unwrap();
    secret
}

async fn seeded_membership(server: &TestServer, instance: &Instance) -> (String, String) {
    let org = Organization {
        id: format!("org_{}", instance.id),
        instance_id: instance.id.clone(),
        name: "Acme".into(),
        slug_key: format!("{}:acme", instance.id),
        status: OrganizationStatus::Active,
        public_metadata: Default::default(),
        created_at: now(),
    };
    server.state.store.create_organization(&org).await.unwrap();
    let user: User = serde_json::from_value(json!({"id":format!("user_{}", instance.id), "instanceId": instance.id, "emailVerified":true})).unwrap();
    server
        .state
        .store
        .create("users", &user.id, &user)
        .await
        .unwrap();
    let membership = OrgMembership {
        id: format!("mem_{}", instance.id),
        instance_id: instance.id.clone(),
        org_id: org.id.clone(),
        user_id: user.id.clone(),
        member_key: format!("{}:{}", org.id, user.id),
        role: "editor".into(),
        created_at: now(),
    };
    server
        .state
        .store
        .create_membership(&membership)
        .await
        .unwrap();
    (org.id, user.id)
}

#[tokio::test]
async fn roles_are_scoped_versioned_and_retirement_cannot_restore_old_grants() {
    let server = TestServer::start().await;
    let client = server.client();
    let instance = create_instance(&server, "rbac").await;
    let secret = key(&server, &instance, "*").await;
    let other = create_instance(&server, "other").await;
    let other_secret = key(&server, &other, "*").await;
    let role_url = format!("{}/v1/rbac/roles/editor", server.origin);
    let roles_url = format!("{}/v1/rbac/roles", server.origin);
    let definition = json!({"key":"editor", "name":"Editor", "permissions":["documents:read", "documents:write", "documents:read"]});
    let response = client
        .post(&roles_url)
        .bearer_auth(&secret)
        .json(&definition)
        .send()
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::CREATED);
    let role: Value = response.json().await.unwrap();
    assert_eq!(
        role["permissions"],
        json!(["documents:read", "documents:write"])
    );
    assert_eq!(
        client
            .post(&roles_url)
            .bearer_auth(&secret)
            .json(&definition)
            .send()
            .await
            .unwrap()
            .status(),
        StatusCode::CONFLICT
    );
    assert_eq!(
        client
            .get(&role_url)
            .bearer_auth(&other_secret)
            .send()
            .await
            .unwrap()
            .status(),
        StatusCode::UNAUTHORIZED
    );
    assert_eq!(
        client
            .get(format!("{}/v1/other/roles/editor", server.origin))
            .bearer_auth(&other_secret)
            .send()
            .await
            .unwrap()
            .status(),
        StatusCode::NOT_FOUND
    );
    let response = client
        .get(&role_url)
        .bearer_auth(&secret)
        .send()
        .await
        .unwrap();
    let etag = response.headers()["etag"].to_str().unwrap().to_owned();
    let update = json!({"name":"Reader", "permissions":["documents:read"]});
    assert_eq!(
        client
            .put(&role_url)
            .bearer_auth(&secret)
            .json(&update)
            .send()
            .await
            .unwrap()
            .status(),
        StatusCode::PRECONDITION_REQUIRED
    );
    assert_eq!(
        client
            .put(&role_url)
            .bearer_auth(&secret)
            .header("If-Match", &etag)
            .json(&update)
            .send()
            .await
            .unwrap()
            .status(),
        StatusCode::OK
    );
    assert_eq!(
        client
            .put(&role_url)
            .bearer_auth(&secret)
            .header("If-Match", &etag)
            .json(&update)
            .send()
            .await
            .unwrap()
            .status(),
        StatusCode::PRECONDITION_FAILED
    );
    assert_eq!(
        client
            .delete(&role_url)
            .bearer_auth(&secret)
            .header("If-Match", &etag)
            .send()
            .await
            .unwrap()
            .status(),
        StatusCode::PRECONDITION_FAILED
    );
    let fresh = client
        .get(&role_url)
        .bearer_auth(&secret)
        .send()
        .await
        .unwrap();
    let etag = fresh.headers()["etag"].to_str().unwrap();
    assert_eq!(
        client
            .delete(&role_url)
            .bearer_auth(&secret)
            .header("If-Match", etag)
            .send()
            .await
            .unwrap()
            .status(),
        StatusCode::NO_CONTENT
    );
    assert_eq!(
        client
            .get(&role_url)
            .bearer_auth(&secret)
            .send()
            .await
            .unwrap()
            .status(),
        StatusCode::NOT_FOUND
    );
    assert_eq!(
        client
            .post(&roles_url)
            .bearer_auth(&secret)
            .json(&definition)
            .send()
            .await
            .unwrap()
            .status(),
        StatusCode::CONFLICT
    );
    let list: Value = client
        .get(&roles_url)
        .bearer_auth(&secret)
        .send()
        .await
        .unwrap()
        .json()
        .await
        .unwrap();
    assert_eq!(list["items"], json!([]));
}

#[tokio::test]
async fn authorization_checks_live_identity_membership_and_explicit_permissions() {
    let server = TestServer::start().await;
    let client = server.client();
    let instance = create_instance(&server, "decisions").await;
    let roles_key = key(&server, &instance, "roles").await;
    let check_key = key(&server, &instance, "authorization").await;
    let (org_id, user_id) = seeded_membership(&server, &instance).await;
    let url = format!("{}/v1/decisions/authorization/check", server.origin);
    let body =
        json!({"organization_id":org_id, "user_id":user_id, "permissions":["documents:read"]});
    assert_eq!(
        client
            .post(&url)
            .bearer_auth(&roles_key)
            .json(&body)
            .send()
            .await
            .unwrap()
            .status(),
        StatusCode::FORBIDDEN
    );
    let denied: Value = client
        .post(&url)
        .bearer_auth(&check_key)
        .json(&body)
        .send()
        .await
        .unwrap()
        .json()
        .await
        .unwrap();
    assert_eq!(denied["allowed"], false);
    let response = client
        .post(format!("{}/v1/decisions/roles", server.origin))
        .bearer_auth(&roles_key)
        .json(&json!({"key":"editor", "name":"Editor", "permissions":["documents:read"]}))
        .send()
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::CREATED);
    let allowed: Value = client
        .post(&url)
        .bearer_auth(&check_key)
        .json(&body)
        .send()
        .await
        .unwrap()
        .json()
        .await
        .unwrap();
    assert_eq!(allowed["allowed"], true);
    assert_eq!(allowed["role"], "editor");
    let mut excessive = body.clone();
    excessive["permissions"] = json!(["documents:read", "documents:delete"]);
    let denied: Value = client
        .post(&url)
        .bearer_auth(&check_key)
        .json(&excessive)
        .send()
        .await
        .unwrap()
        .json()
        .await
        .unwrap();
    assert_eq!(denied["allowed"], false);
    let mut empty = body.clone();
    empty["permissions"] = json!([]);
    assert_eq!(
        client
            .post(&url)
            .bearer_auth(&check_key)
            .json(&empty)
            .send()
            .await
            .unwrap()
            .status(),
        StatusCode::BAD_REQUEST
    );
    let mut user = server
        .state
        .store
        .get_user(&user_id)
        .await
        .unwrap()
        .unwrap();
    user.banned = true;
    server.state.store.upsert_user(&user).await.unwrap();
    let denied: Value = client
        .post(&url)
        .bearer_auth(&check_key)
        .json(&body)
        .send()
        .await
        .unwrap()
        .json()
        .await
        .unwrap();
    assert_eq!(denied["allowed"], false);
    user.banned = false;
    server.state.store.upsert_user(&user).await.unwrap();
    server
        .state
        .store
        .delete_membership(&format!("mem_{}", instance.id))
        .await
        .unwrap();
    let denied: Value = client
        .post(&url)
        .bearer_auth(&check_key)
        .json(&body)
        .send()
        .await
        .unwrap()
        .json()
        .await
        .unwrap();
    assert_eq!(denied["allowed"], false);
}
