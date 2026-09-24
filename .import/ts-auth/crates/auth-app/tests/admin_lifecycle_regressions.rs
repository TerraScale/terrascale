use std::{
    sync::atomic::{AtomicU64, Ordering},
    time::{Duration, SystemTime, UNIX_EPOCH},
};

use auth_app::{
    AuthOptions,
    app::{AppState, build_router, build_state_with_mode},
};
use auth_security::{secretbox, tokens::sha256_hex};
use axum::http::header;
use reqwest::{Client, StatusCode};
use serde_json::{Value, json};
use tokio::{net::TcpListener, task::JoinHandle, time::timeout};

mod support;

use support::terrabase_http::{TEST_TERRASCALE_API_KEY, TerraBaseHttpFixture};

const ADMIN_USER_ID: &str = "usr_connection_race_admin";
const ADMIN_SESSION_TOKEN: &str = "connection-race-admin-session";
const ADMIN_ORGANIZATION_ID: &str = "org_connection_race_admin";
const UNAUTHORIZED_ORGANIZATION_ID: &str = "org_connection_race_other";
const CONNECTION_ID: &str = "con_connection_race";

struct TestServer {
    origin: String,
    task: JoinHandle<()>,
    state: AppState,
    storage: TerraBaseHttpFixture,
}

impl TestServer {
    async fn start() -> Self {
        let storage = TerraBaseHttpFixture::start().await;
        let listener = TcpListener::bind("127.0.0.1:0")
            .await
            .expect("admin lifecycle test listener should bind");
        let address = listener
            .local_addr()
            .expect("admin lifecycle test listener should have an address");
        static NEXT_DATABASE: AtomicU64 = AtomicU64::new(1);
        let database_id = NEXT_DATABASE.fetch_add(1, Ordering::Relaxed);
        let origin = format!("http://{address}");
        let options = AuthOptions {
            listen: "http://127.0.0.1:0".to_owned(),
            public_origin: origin.clone(),
            terrascale_api_url: storage.origin.clone(),
            terrascale_api_key: TEST_TERRASCALE_API_KEY.to_owned(),
            terrascale_project_id: None,
            terrascale_tenant_id: None,
            database: format!("ts-auth-admin-lifecycle-{database_id}"),
            master_key: vec![7; 32],
            audit_signing_key: Some(vec![8; 32]),
            audit_verification_keys: Vec::new(),
            operator_key: "test-only-operator-key".to_owned(),
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
        let state =
            build_state_with_mode(options, true).expect("admin lifecycle test state should build");
        let router = build_router(state.clone());
        let task = tokio::spawn(async move {
            axum::serve(listener, router)
                .await
                .expect("admin lifecycle test server should remain available");
        });

        Self {
            origin,
            task,
            state,
            storage,
        }
    }

    fn client(&self) -> Client {
        Client::builder()
            .no_proxy()
            .redirect(reqwest::redirect::Policy::none())
            .build()
            .expect("admin lifecycle HTTP client should build")
    }
}

impl Drop for TestServer {
    fn drop(&mut self) {
        self.task.abort();
    }
}

struct AdminFixture {
    server: TestServer,
    client: Client,
    slug: String,
    instance_id: String,
    management_key: String,
}

impl AdminFixture {
    async fn start(slug: &str) -> Self {
        let server = TestServer::start().await;
        let client = server.client();
        let instance_response = client
            .post(format!("{}/v1/instances", server.origin))
            .bearer_auth("test-only-operator-key")
            .header("Idempotency-Key", format!("admin-lifecycle-{slug}"))
            .json(&json!({ "slug": slug, "name": "Admin lifecycle test" }))
            .send()
            .await
            .expect("operator instance request should reach the test server");
        assert_eq!(instance_response.status(), StatusCode::OK);
        let instance: Value = instance_response
            .json()
            .await
            .expect("operator instance response should be JSON");
        let instance_id = instance["instance"]["id"]
            .as_str()
            .expect("created instance has an id")
            .to_owned();
        let management_key = instance["api_key"]
            .as_str()
            .expect("created instance returns its initial management key")
            .to_owned();
        let now = unix_now();

        seed_record(
            &server,
            "users",
            ADMIN_USER_ID,
            &json!({
                "id": ADMIN_USER_ID,
                "instanceId": instance_id,
                "email": "connection-admin@example.test",
                "emailVerified": true,
                "banned": false,
                "deleted": false,
                "createdAt": now,
                "updatedAt": now,
            }),
        )
        .await;
        seed_record(
            &server,
            "sessions",
            "ses_connection_race_admin",
            &json!({
                "id": "ses_connection_race_admin",
                "instanceId": instance_id,
                "userId": ADMIN_USER_ID,
                "tokenKey": sha256_hex(ADMIN_SESSION_TOKEN),
                "status": "active",
                "createdAt": now,
                "expiresAt": now + 3_600,
                "lastActiveAt": now,
            }),
        )
        .await;
        seed_record(
            &server,
            "organizations",
            ADMIN_ORGANIZATION_ID,
            &json!({
                "id": ADMIN_ORGANIZATION_ID,
                "instanceId": instance_id,
                "name": "Connection Race Organization",
                "slugKey": format!("{instance_id}:connection-race"),
                "status": "active",
                "createdAt": now,
            }),
        )
        .await;
        seed_record(
            &server,
            "memberships",
            "mem_connection_race_admin",
            &json!({
                "id": "mem_connection_race_admin",
                "instanceId": instance_id,
                "orgId": ADMIN_ORGANIZATION_ID,
                "userId": ADMIN_USER_ID,
                "memberKey": format!("{ADMIN_ORGANIZATION_ID}:{ADMIN_USER_ID}"),
                "role": "admin",
                "createdAt": now,
            }),
        )
        .await;
        seed_record(
            &server,
            "connections",
            CONNECTION_ID,
            &json!({
                "id": CONNECTION_ID,
                "instanceId": instance_id,
                "type": "oauth",
                "name": "Race Test Provider",
                "enabled": false,
                "orgId": ADMIN_ORGANIZATION_ID,
                "clientId": "race-test-client",
                "clientSecretBox": secretbox::encrypt(&[7; 32], b"initial-secret")
                    .expect("initial provider secret should encrypt"),
                "createdAt": now,
            }),
        )
        .await;

        Self {
            server,
            client,
            slug: slug.to_owned(),
            instance_id,
            management_key,
        }
    }

    fn admin_toggle(&self) -> tokio::task::JoinHandle<reqwest::Result<reqwest::Response>> {
        let client = self.client.clone();
        let origin = self.server.origin.clone();
        let slug = self.slug.clone();
        tokio::spawn(async move {
            client
                .post(format!(
                    "{origin}/ui/{slug}/admin/connections/{CONNECTION_ID}/enable"
                ))
                .header(header::ORIGIN, &origin)
                .header(
                    header::COOKIE,
                    format!("tsauth_session={ADMIN_SESSION_TOKEN}"),
                )
                .send()
                .await
        })
    }

    fn management_connection_url(&self) -> String {
        format!(
            "{}/v1/{}/connections/{CONNECTION_ID}",
            self.server.origin, self.slug
        )
    }
}

#[tokio::test]
async fn admin_enable_does_not_recreate_connection_deleted_during_cas() {
    let fixture = AdminFixture::start("admin-delete-race").await;
    let mut pause = fixture
        .server
        .storage
        .pause_next_compare_and_put("connections");
    let admin_toggle = fixture.admin_toggle();
    timeout(Duration::from_secs(10), pause.wait_until_paused())
        .await
        .expect("admin enable should reach its conditional storage write");

    let deleted = fixture
        .client
        .delete(fixture.management_connection_url())
        .bearer_auth(&fixture.management_key)
        .send()
        .await
        .expect("concurrent management delete should reach the test server");
    pause.resume();
    let admin_response = admin_toggle
        .await
        .expect("admin request task should complete")
        .expect("admin request should reach the test server");

    assert_eq!(deleted.status(), StatusCode::NO_CONTENT);
    assert_eq!(admin_response.status(), StatusCode::NOT_FOUND);
    assert!(
        fixture
            .server
            .state
            .store
            .get_connection(CONNECTION_ID)
            .await
            .expect("connection lookup should succeed")
            .is_none()
    );
}

#[tokio::test]
async fn admin_enable_does_not_mutate_connection_reparented_during_cas_retry() {
    let fixture = AdminFixture::start("admin-reparent-race").await;
    let mut pause = fixture
        .server
        .storage
        .pause_next_compare_and_put("connections");
    let admin_toggle = fixture.admin_toggle();
    timeout(Duration::from_secs(10), pause.wait_until_paused())
        .await
        .expect("admin enable should reach its conditional storage write");

    let mut connection = fixture
        .server
        .state
        .store
        .get_connection(CONNECTION_ID)
        .await
        .expect("connection lookup should succeed")
        .expect("connection should remain present");
    connection.org_id = Some(UNAUTHORIZED_ORGANIZATION_ID.to_owned());
    fixture
        .server
        .state
        .store
        .upsert_connection(&connection)
        .await
        .expect("concurrent reparent should update the connection");

    pause.resume();
    let admin_response = admin_toggle
        .await
        .expect("admin request task should complete")
        .expect("admin request should reach the test server");
    let connection = fixture
        .server
        .state
        .store
        .get_connection(CONNECTION_ID)
        .await
        .expect("connection lookup should succeed")
        .expect("reparented connection should remain present");

    assert_eq!(admin_response.status(), StatusCode::NOT_FOUND);
    assert_eq!(
        connection.org_id.as_deref(),
        Some(UNAUTHORIZED_ORGANIZATION_ID)
    );
    assert!(!connection.enabled);
}

#[tokio::test]
async fn admin_access_ignores_scim_membership_without_an_active_provenance_link() {
    let fixture = AdminFixture::start("admin-scim-eligibility").await;
    seed_record(
        &fixture.server,
        "scim_membership_claims",
        &format!("{ADMIN_ORGANIZATION_ID}:{ADMIN_USER_ID}"),
        &json!({
            "id": format!("{ADMIN_ORGANIZATION_ID}:{ADMIN_USER_ID}"),
            "instanceId": fixture.instance_id,
            "orgId": ADMIN_ORGANIZATION_ID,
            "userId": ADMIN_USER_ID,
            "membershipId": "mem_connection_race_admin",
        }),
    )
    .await;

    let response = fixture
        .admin_toggle()
        .await
        .expect("admin request task should complete")
        .expect("admin request should reach the test server");
    let connection = fixture
        .server
        .state
        .store
        .get_connection(CONNECTION_ID)
        .await
        .expect("connection lookup should succeed")
        .expect("connection should remain present");

    assert_eq!(response.status(), StatusCode::FORBIDDEN);
    assert!(!connection.enabled);
}

#[tokio::test]
async fn admin_enable_retries_after_concurrent_secret_rotation_without_losing_either_change() {
    let fixture = AdminFixture::start("admin-secret-race").await;
    let mut pause = fixture
        .server
        .storage
        .pause_next_compare_and_put("connections");
    let admin_toggle = fixture.admin_toggle();
    timeout(Duration::from_secs(10), pause.wait_until_paused())
        .await
        .expect("admin enable should reach its conditional storage write");

    let rotated = fixture
        .client
        .patch(fixture.management_connection_url())
        .bearer_auth(&fixture.management_key)
        .json(&json!({ "client_secret": "rotated-secret" }))
        .send()
        .await
        .expect("concurrent secret rotation should reach the test server");
    pause.resume();
    let admin_response = admin_toggle
        .await
        .expect("admin request task should complete")
        .expect("admin request should reach the test server");

    assert_eq!(rotated.status(), StatusCode::OK);
    assert_eq!(admin_response.status(), StatusCode::SEE_OTHER);
    let connection = fixture
        .server
        .state
        .store
        .get_connection(CONNECTION_ID)
        .await
        .expect("connection lookup should succeed")
        .expect("connection should remain present");
    assert!(connection.enabled);
    let secret_box = connection
        .client_secret_box
        .expect("rotated provider secret should remain stored");
    let secret =
        secretbox::decrypt(&[7; 32], &secret_box).expect("stored provider secret should decrypt");
    assert_eq!(secret.as_slice(), b"rotated-secret");
}

async fn seed_record(server: &TestServer, collection: &str, id: &str, value: &Value) {
    server
        .state
        .store
        .create(collection, id, value)
        .await
        .unwrap_or_else(|error| panic!("seed {collection}/{id} through SDK fixture: {error}"));
}

fn unix_now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("system time should follow the Unix epoch")
        .as_secs() as i64
}
