use auth_app::{
    AuthOptions,
    app::{AppState, build_state_with_mode},
    build_router,
};
use auth_contracts::models::{
    ApiKey, Instance, Invitation, Organization, OrganizationStatus, ScimDirectory,
    ScimDirectoryStatus,
};
use auth_security::tokens::sha256_hex;
use auth_store::{StoreError, unique_claim_id};
use axum::http::StatusCode;
use reqwest::Client;
use serde_json::{Value, json};
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use tokio::{net::TcpListener, task::JoinHandle, time::timeout};

mod support;

use support::terrabase_http::{TEST_TERRASCALE_API_KEY, TerraBaseHttpFixture};

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
            .expect("app listener should bind");
        let address = listener
            .local_addr()
            .expect("app listener should have an address");
        let public_origin = format!("http://localhost:{}", address.port());
        static NEXT_DATABASE: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1);
        let database = format!(
            "ts-auth-lifecycle-review-{}",
            NEXT_DATABASE.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
        );
        let options = AuthOptions {
            listen: "http://127.0.0.1:0".into(),
            public_origin,
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
            storage,
        }
    }

    fn client(&self) -> Client {
        Client::builder()
            .no_proxy()
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

async fn create_instance_and_key(
    server: &TestServer,
    slug: &str,
    scopes: &[&str],
) -> (Instance, String) {
    let created_at = now();
    let instance = Instance {
        id: format!("ins_{slug}"),
        slug: slug.to_owned(),
        name: format!("{slug} lifecycle test"),
        logo_url: None,
        theme_color: None,
        stripe_customer_id: None,
        created_at,
    };
    server
        .state
        .store
        .create_instance(&instance)
        .await
        .expect("instance should persist");

    let secret = format!("sk_lifecycle_{slug}");
    let api_key = ApiKey {
        id: format!("key_lifecycle_{slug}"),
        instance_id: instance.id.clone(),
        name: "lifecycle review regression".into(),
        secret_key_hash: sha256_hex(&secret),
        scopes: scopes.iter().map(|scope| (*scope).to_owned()).collect(),
        created_at,
        last_used_at: None,
        revoked: false,
    };
    server
        .state
        .store
        .create("api_keys", &api_key.id, &api_key)
        .await
        .expect("scoped API key should persist");
    (instance, secret)
}

async fn create_scim_directory(
    server: &TestServer,
    slug: &str,
) -> (Instance, String, ScimDirectory) {
    let (instance, secret) = create_instance_and_key(server, slug, &["directories"]).await;
    let directory = ScimDirectory {
        id: format!("dir_{slug}"),
        instance_id: instance.id.clone(),
        org_id: None,
        instance_wide: true,
        status: ScimDirectoryStatus::Active,
        token_key: sha256_hex(&format!("scim_lifecycle_token_{slug}")),
        created_at: now(),
    };
    server
        .state
        .store
        .create_scim_directory(&directory)
        .await
        .expect("SCIM directory and token claim should persist");
    (instance, secret, directory)
}

async fn token_claim_ids(server: &TestServer, owner_id: &str) -> Vec<String> {
    let mut ids: Vec<_> = server
        .state
        .store
        .list_raw("uniques", 100, None)
        .await
        .expect("unique claims should be readable")
        .into_iter()
        .filter(|(_, claim)| {
            claim.get("ownerType").and_then(Value::as_str) == Some("scim-token")
                && claim.get("ownerId").and_then(Value::as_str) == Some(owner_id)
        })
        .map(|(id, _)| id)
        .collect();
    ids.sort();
    ids
}

async fn scim_group_ids(server: &TestServer, directory_id: &str) -> Vec<String> {
    let mut ids: Vec<_> = server
        .state
        .store
        .list_raw("scim_groups", 100, None)
        .await
        .expect("SCIM groups should be readable")
        .into_iter()
        .filter(|(_, group)| group.get("directoryId").and_then(Value::as_str) == Some(directory_id))
        .map(|(id, _)| id)
        .collect();
    ids.sort();
    ids
}

#[tokio::test]
async fn organization_bounded_child_sweep_returns_503_and_keeps_retryable_tombstone() {
    let server = TestServer::start().await;
    let (instance, secret) =
        create_instance_and_key(&server, "org-sweep", &["organizations"]).await;
    let organization = Organization {
        id: "org_lifecycle_sweep".into(),
        instance_id: instance.id.clone(),
        name: "Lifecycle sweep test".into(),
        slug_key: format!("{}:lifecycle-sweep", instance.id),
        status: OrganizationStatus::Active,
        public_metadata: Default::default(),
        created_at: now(),
    };
    server
        .state
        .store
        .create_organization(&organization)
        .await
        .expect("organization should persist");

    let seed_invitation = |index: usize| Invitation {
        id: format!("inv_lifecycle_sweep_{index}"),
        instance_id: instance.id.clone(),
        org_id: organization.id.clone(),
        email: format!("invite-{index}@example.test"),
        role: "member".into(),
        status: "pending".into(),
        expires_at: now() + 3_600,
        created_at: now(),
    };
    server
        .state
        .store
        .create("invitations", "inv_lifecycle_sweep_0", &seed_invitation(0))
        .await
        .expect("initial invitation should persist");

    let client = server.client();
    let url = format!(
        "{}/v1/{}/organizations/{}",
        server.origin, instance.slug, organization.id
    );
    let request_client = client.clone();
    let request_url = url.clone();
    let request_secret = secret.clone();
    let mut next_pause = Some(server.storage.pause_next_query_response("invitations"));
    let deletion = tokio::spawn(async move {
        request_client
            .delete(request_url)
            .bearer_auth(request_secret)
            .send()
            .await
            .expect("organization deletion should reach the app")
    });

    // Each paused query has already captured its page. Add one new invitation
    // before releasing it so every bounded sweep pass sees another child.
    for pass in 0..=8 {
        let mut pause = next_pause
            .take()
            .expect("the next invitation query should be paused");
        timeout(Duration::from_secs(3), pause.wait_until_paused())
            .await
            .expect("deletion should reach the next invitation sweep pass");
        if pass < 8 {
            next_pause = Some(server.storage.pause_next_query_response("invitations"));
            let next = seed_invitation(pass + 1);
            server
                .state
                .store
                .create("invitations", &next.id, &next)
                .await
                .expect("racing invitation should persist");
        }
        pause.resume();
    }

    let failed = timeout(Duration::from_secs(5), deletion)
        .await
        .expect("bounded organization deletion should return")
        .expect("organization deletion task should complete");
    assert_eq!(failed.status(), StatusCode::SERVICE_UNAVAILABLE);
    let failed_body: Value = failed
        .json()
        .await
        .expect("contention response should be JSON");
    assert_eq!(
        failed_body["error"]["code"],
        "organization_deletion_in_progress"
    );
    assert_eq!(
        server
            .state
            .store
            .get_organization_for_deletion(&organization.id)
            .await
            .expect("organization tombstone should remain readable")
            .expect("failed sweep must retain the organization tombstone")
            .status,
        OrganizationStatus::Deleting
    );
    assert!(
        server
            .state
            .store
            .get::<Invitation>("invitations", "inv_lifecycle_sweep_8")
            .await
            .expect("remaining invitation should be readable")
            .is_some()
    );

    let retried = client
        .delete(url)
        .bearer_auth(&secret)
        .send()
        .await
        .expect("organization deletion retry should reach the app");
    assert_eq!(retried.status(), StatusCode::NO_CONTENT);
    assert_eq!(
        server
            .state
            .store
            .get_organization_for_deletion(&organization.id)
            .await
            .expect("deleted organization should be readable")
            .expect("organization tombstone should remain after deletion")
            .status,
        OrganizationStatus::Deleted
    );
    assert!(
        server
            .state
            .store
            .get::<Invitation>("invitations", "inv_lifecycle_sweep_8")
            .await
            .expect("swept invitation should be readable")
            .is_none()
    );
}

#[tokio::test]
async fn scim_delete_contention_returns_503_and_retry_releases_owned_claim() {
    let server = TestServer::start().await;
    let (_instance, secret, directory) = create_scim_directory(&server, "delete-contention").await;
    let claim_id = unique_claim_id("scim-token", &directory.token_key);
    let client = server.client();
    let url = format!(
        "{}/v1/{}/directories/{}",
        server.origin, "delete-contention", directory.id
    );

    let mut pause = server.storage.pause_next_delete_before_commit("uniques");
    let request_client = client.clone();
    let request_url = url.clone();
    let request_secret = secret.clone();
    let deletion = tokio::spawn(async move {
        request_client
            .delete(request_url)
            .bearer_auth(request_secret)
            .send()
            .await
            .expect("SCIM directory deletion should reach the app")
    });
    timeout(Duration::from_secs(3), pause.wait_until_paused())
        .await
        .expect("directory deletion should reach owned token-claim cleanup");

    // Advance the claim version without changing its owner. The in-flight
    // compare-and-delete must fail closed as retryable contention.
    server
        .state
        .store
        .upsert(
            "uniques",
            &claim_id,
            &json!({
                "ownerType": "scim-token",
                "ownerId": directory.id.clone(),
                "fixtureRevision": 2,
            }),
        )
        .await
        .expect("same-owner claim version should advance");
    pause.resume();

    let failed = timeout(Duration::from_secs(5), deletion)
        .await
        .expect("bounded claim cleanup should return")
        .expect("SCIM deletion task should complete");
    assert_eq!(failed.status(), StatusCode::SERVICE_UNAVAILABLE);
    let failed_body: Value = failed
        .json()
        .await
        .expect("contention response should be JSON");
    assert_eq!(
        failed_body["error"]["code"],
        "directory_deletion_in_progress"
    );
    assert_eq!(
        server
            .state
            .store
            .get::<ScimDirectory>("scim_directories", &directory.id)
            .await
            .expect("directory tombstone should remain readable")
            .expect("failed cleanup must retain the directory tombstone")
            .status,
        ScimDirectoryStatus::Deleted
    );
    assert!(
        server
            .state
            .store
            .get::<Value>("uniques", &claim_id)
            .await
            .expect("owned token claim should remain readable")
            .is_some()
    );

    let retried = client
        .delete(url)
        .bearer_auth(&secret)
        .send()
        .await
        .expect("SCIM deletion retry should reach the app");
    assert_eq!(retried.status(), StatusCode::NO_CONTENT);
    assert!(
        server
            .state
            .store
            .get::<Value>("uniques", &claim_id)
            .await
            .expect("released token claim should be readable")
            .is_none()
    );
}

#[tokio::test]
async fn late_scim_group_cleanup_failure_returns_503_and_deleted_retry_sweeps_it() {
    let server = TestServer::start().await;
    let (instance, management_secret, directory) =
        create_scim_directory(&server, "late-group-create").await;
    let client = server.client();
    let create_url = format!("{}/scim/{}/Groups", server.origin, directory.id);
    let delete_url = format!(
        "{}/v1/{}/directories/{}",
        server.origin, instance.slug, directory.id
    );
    let scim_token = "scim_lifecycle_token_late-group-create";

    // Capture an Active directory during token authentication, then let the
    // management deletion finish before the SCIM request can create its group.
    let mut active_directory_snapshot =
        server.storage.pause_next_query_response("scim_directories");
    let create_client = client.clone();
    let create_request_url = create_url.clone();
    let create = tokio::spawn(async move {
        create_client
            .post(create_request_url)
            .bearer_auth(scim_token)
            .json(&json!({ "displayName": "late group" }))
            .send()
            .await
            .expect("late SCIM group create should reach the app")
    });
    timeout(
        Duration::from_secs(3),
        active_directory_snapshot.wait_until_paused(),
    )
    .await
    .expect("group creation should capture the active directory");

    let deleted = client
        .delete(&delete_url)
        .bearer_auth(&management_secret)
        .send()
        .await
        .expect("directory deletion should reach the app");
    assert_eq!(deleted.status(), StatusCode::NO_CONTENT);

    // The late group write succeeds, but its compensating delete fails before
    // commit. The create response must surface that cleanup needs a retry.
    server.storage.fail_next_delete_before_commit("scim_groups");
    active_directory_snapshot.resume();
    let failed_create = timeout(Duration::from_secs(5), create)
        .await
        .expect("late group cleanup should return")
        .expect("late group create task should complete");
    assert_eq!(failed_create.status(), StatusCode::SERVICE_UNAVAILABLE);
    let failed_body: Value = failed_create
        .json()
        .await
        .expect("cleanup failure should be a SCIM error document");
    assert_eq!(failed_body["status"], "503");
    let late_group_ids = scim_group_ids(&server, &directory.id).await;
    assert_eq!(late_group_ids.len(), 1);
    let late_group_id = late_group_ids[0].clone();
    assert!(
        server
            .state
            .store
            .get::<Value>("scim_groups", &late_group_id)
            .await
            .expect("late SCIM group should be readable")
            .is_some()
    );

    // The retained Deleted directory is still a valid cleanup cursor: DELETE
    // retries child sweeps even though the first deletion returned 204.
    let retried = client
        .delete(delete_url)
        .bearer_auth(&management_secret)
        .send()
        .await
        .expect("directory cleanup retry should reach the app");
    assert_eq!(retried.status(), StatusCode::NO_CONTENT);
    assert!(scim_group_ids(&server, &directory.id).await.is_empty());
    assert!(
        server
            .state
            .store
            .get::<Value>("scim_groups", &late_group_id)
            .await
            .expect("late SCIM group should be readable")
            .is_none()
    );
}

#[tokio::test]
async fn scim_token_rotation_releases_only_directory_owned_claims() {
    let server = TestServer::start().await;
    let (_instance, secret, directory) = create_scim_directory(&server, "rotate-owner").await;
    let old_claim_id = unique_claim_id("scim-token", &directory.token_key);
    let client = server.client();
    let url = format!(
        "{}/v1/{}/directories/{}/rotate",
        server.origin, "rotate-owner", directory.id
    );

    server
        .storage
        .conflict_next_compare_and_puts("scim_directories", 1);
    let conflicted = client
        .post(&url)
        .bearer_auth(&secret)
        .send()
        .await
        .expect("conflicted SCIM token rotation should reach the app");
    assert_eq!(conflicted.status(), StatusCode::CONFLICT);
    assert_eq!(
        token_claim_ids(&server, &directory.id).await,
        vec![old_claim_id.clone()]
    );

    let rotated = client
        .post(&url)
        .bearer_auth(&secret)
        .send()
        .await
        .expect("SCIM token rotation should reach the app");
    assert_eq!(rotated.status(), StatusCode::OK);
    let rotated: Value = rotated
        .json()
        .await
        .expect("rotated SCIM token response should be JSON");
    let new_token = rotated["token"]
        .as_str()
        .expect("rotated plaintext token should be returned");
    let new_token_key = sha256_hex(new_token);
    let new_claim_id = unique_claim_id("scim-token", &new_token_key);
    assert!(
        server
            .state
            .store
            .get::<Value>("uniques", &old_claim_id)
            .await
            .expect("old token claim should be readable")
            .is_none()
    );
    assert_eq!(
        token_claim_ids(&server, &directory.id).await,
        vec![new_claim_id.clone()]
    );

    server
        .storage
        .fail_next_compare_and_put_before_commit("scim_directories");
    let errored = client
        .post(&url)
        .bearer_auth(&secret)
        .send()
        .await
        .expect("failed SCIM token rotation should reach the app");
    assert_eq!(errored.status(), StatusCode::INTERNAL_SERVER_ERROR);
    assert_eq!(
        token_claim_ids(&server, &directory.id).await,
        vec![new_claim_id.clone()]
    );

    let wrong_owner = server
        .state
        .store
        .release_unique("scim-token", &new_token_key, "dir_foreign_owner")
        .await;
    assert!(matches!(wrong_owner, Err(StoreError::Precondition)));
    let claim = server
        .state
        .store
        .get::<Value>("uniques", &new_claim_id)
        .await
        .expect("new token claim should remain readable")
        .expect("foreign owner must not release the directory claim");
    assert_eq!(claim["ownerId"], directory.id);
}
