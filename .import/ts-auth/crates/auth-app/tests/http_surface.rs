use auth_app::{
    AuthOptions,
    app::{AppState, build_router, build_state_with_mode},
    features::users,
};
use auth_contracts::models::{
    AuditEvent, MfaFactor, OrgMembership, Organization, OrganizationStatus, WebhookDelivery,
    WebhookEndpoint,
};
use auth_security::{
    secretbox,
    tokens::{hmac_sha256, sha256_hex},
    totp,
};
use axum::{
    Json, Router,
    extract::Path,
    routing::{get, post},
};
use base64::{Engine, engine::general_purpose::URL_SAFE_NO_PAD};
use reqwest::{Client, StatusCode, header};
use saml_rs::{
    AcsEndpoint, AssertionSignaturePolicy, AudienceValidationPolicy, AuthnRequest,
    AuthnRequestSigningPolicy, BrowserInput, CertificatePem, Credentials, EntityId, IdpConfig,
    IdpValidationPolicy, MetadataTrustPolicy, NameId, NameIdFormat, PrivateKeyPem, RespondSso,
    ResponseSignaturePolicy, Saml, SamlValidationContext, SpConfig, SpDescriptor,
    SpValidationPolicy, SsoEndpoint, Subject,
};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use std::{
    collections::VecDeque,
    sync::{Arc, Mutex},
};
use tokio::{
    net::TcpListener,
    sync::oneshot,
    task::JoinHandle,
    time::{sleep, timeout},
};

mod support;

use support::terrabase_http::{TEST_TERRASCALE_API_KEY, TerraBaseHttpFixture};

struct TestServer {
    origin: String,
    task: JoinHandle<()>,
    state: AppState,
    _storage_fixture: Option<TerraBaseHttpFixture>,
}

impl TestServer {
    async fn start() -> Self {
        Self::start_with_config(|_| {}).await
    }

    async fn start_without_storage() -> Self {
        Self::start_with_storage_config("http://127.0.0.1:1".to_owned(), None, |_| {}).await
    }

    async fn start_with_config(configure: impl FnOnce(&mut AuthOptions)) -> Self {
        let storage_fixture = TerraBaseHttpFixture::start().await;
        Self::start_with_storage_config(
            storage_fixture.origin.clone(),
            Some(storage_fixture),
            configure,
        )
        .await
    }

    async fn start_with_storage_config(
        storage_url: String,
        storage_fixture: Option<TerraBaseHttpFixture>,
        configure: impl FnOnce(&mut AuthOptions),
    ) -> Self {
        let listener = TcpListener::bind("127.0.0.1:0")
            .await
            .expect("loopback listener should bind");
        let address = listener.local_addr().expect("listener has a local address");
        static NEXT_DATABASE: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1);
        let database_id = NEXT_DATABASE.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        let database = format!("ts-auth-contract-test-{database_id}");
        let public_origin = format!("http://localhost:{}", address.port());
        let mut options = test_options(storage_url, database, public_origin);
        configure(&mut options);
        let state = build_state_with_mode(options, true).expect("local test state should be valid");
        let router = build_router(state.clone());
        let task = tokio::spawn(async move {
            axum::serve(listener, router)
                .await
                .expect("test HTTP server should remain available");
        });

        Self {
            origin: format!("http://{address}"),
            task,
            state,
            _storage_fixture: storage_fixture,
        }
    }

    fn client(&self) -> Client {
        Client::builder()
            .redirect(reqwest::redirect::Policy::none())
            .build()
            .expect("HTTP client should build")
    }
}

struct StripeApiFixture {
    origin: String,
    task: JoinHandle<()>,
    checkout_ids: Arc<Mutex<VecDeque<String>>>,
    checkout_requests: Arc<std::sync::atomic::AtomicUsize>,
    subscription_prices: Arc<Mutex<VecDeque<String>>>,
}

#[derive(Clone)]
struct StripeApiState {
    checkout_ids: Arc<Mutex<VecDeque<String>>>,
    checkout_requests: Arc<std::sync::atomic::AtomicUsize>,
    subscription_prices: Arc<Mutex<VecDeque<String>>>,
}

impl StripeApiFixture {
    async fn start() -> Self {
        let checkout_ids = Arc::new(Mutex::new(VecDeque::new()));
        let checkout_requests = Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let subscription_prices = Arc::new(Mutex::new(VecDeque::new()));
        let state = StripeApiState {
            checkout_ids: checkout_ids.clone(),
            checkout_requests: checkout_requests.clone(),
            subscription_prices: subscription_prices.clone(),
        };
        let app = Router::new()
            .route("/v1/subscriptions/{id}", get(stripe_subscription))
            .route("/v1/checkout/sessions", post(stripe_checkout_session))
            .with_state(state);
        let listener = TcpListener::bind("127.0.0.1:0")
            .await
            .expect("Stripe API fixture should bind loopback");
        let address = listener
            .local_addr()
            .expect("Stripe API fixture should have a bound address");
        let task = tokio::spawn(async move {
            axum::serve(listener, app)
                .await
                .expect("Stripe API fixture should remain available");
        });
        Self {
            origin: format!("http://{address}"),
            task,
            checkout_ids,
            checkout_requests,
            subscription_prices,
        }
    }

    fn queue_checkout_session_id(&self, id: &str) {
        self.checkout_ids
            .lock()
            .expect("Stripe fixture checkout-ID queue is healthy")
            .push_back(id.to_owned());
    }

    fn queue_subscription_price(&self, id: &str) {
        self.subscription_prices
            .lock()
            .expect("Stripe fixture subscription-price queue is healthy")
            .push_back(id.to_owned());
    }

    fn checkout_request_count(&self) -> usize {
        self.checkout_requests
            .load(std::sync::atomic::Ordering::SeqCst)
    }
}

impl Drop for StripeApiFixture {
    fn drop(&mut self) {
        self.task.abort();
    }
}

async fn stripe_subscription(
    axum::extract::State(state): axum::extract::State<StripeApiState>,
    Path(id): Path<String>,
) -> Json<Value> {
    let price = state
        .subscription_prices
        .lock()
        .expect("Stripe fixture subscription-price queue is healthy")
        .pop_front();
    let mut body = json!({
        "id": id,
        "customer": "cus_checkout_race",
        "status": "active",
        "current_period_end": 2_000_000_000_i64,
        "items": { "data": [] },
    });
    if let Some(price) = price {
        body["items"]["data"] = json!([{ "price": { "id": price } }]);
    }
    Json(body)
}

async fn stripe_checkout_session(
    axum::extract::State(state): axum::extract::State<StripeApiState>,
) -> Json<Value> {
    state
        .checkout_requests
        .fetch_add(1, std::sync::atomic::Ordering::SeqCst);
    let id = state
        .checkout_ids
        .lock()
        .expect("Stripe fixture checkout-ID queue is healthy")
        .pop_front()
        .expect("test queued a checkout session ID");
    Json(json!({
        "id": id,
        "url": format!("https://checkout.stripe.test/{id}"),
        "expires_at": unix_now() + 3_600,
    }))
}

impl Drop for TestServer {
    fn drop(&mut self) {
        self.task.abort();
    }
}

fn test_options(storage_url: String, database: String, public_origin: String) -> AuthOptions {
    AuthOptions {
        listen: "http://127.0.0.1:0".to_owned(),
        public_origin,
        terrascale_api_url: storage_url,
        terrascale_api_key: TEST_TERRASCALE_API_KEY.to_owned(),
        terrascale_project_id: None,
        terrascale_tenant_id: None,
        database,
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
    }
}

async fn create_instance(server: &TestServer, client: &Client, slug: &str) -> Value {
    let response = client
        .post(format!("{}/v1/instances", server.origin))
        .bearer_auth("test-only-operator-key")
        .header("Idempotency-Key", format!("test-bootstrap-{slug}"))
        .json(&json!({ "slug": slug, "name": format!("{slug} test") }))
        .send()
        .await
        .expect("operator instance request should reach the service");
    assert_eq!(response.status(), StatusCode::OK);
    response
        .json()
        .await
        .expect("operator instance response should be JSON")
}

#[tokio::test]
async fn operator_bootstrap_recovers_an_ambiguous_api_key_write_idempotently() {
    let server = TestServer::start().await;
    let client = server.client();
    server
        ._storage_fixture
        .as_ref()
        .expect("contract fixture is available")
        .fail_next_create_only_after_commit("api_keys");
    let endpoint = format!("{}/v1/instances", server.origin);
    let idempotency_key = "bootstrap-replay-test-key";
    let request_body = json!({
        "slug": "bootstrap-replay-test",
        "name": "Bootstrap Replay Test"
    });

    let ambiguous = client
        .post(&endpoint)
        .bearer_auth("test-only-operator-key")
        .header("Idempotency-Key", idempotency_key)
        .json(&request_body)
        .send()
        .await
        .expect("first operator bootstrap should reach the service");
    assert_eq!(ambiguous.status(), StatusCode::SERVICE_UNAVAILABLE);

    let recovered = client
        .post(&endpoint)
        .bearer_auth("test-only-operator-key")
        .header("Idempotency-Key", idempotency_key)
        .json(&request_body)
        .send()
        .await
        .expect("same-key bootstrap retry should reach the service");
    assert_eq!(recovered.status(), StatusCode::OK);
    let recovered: Value = recovered
        .json()
        .await
        .expect("recovered bootstrap response should be JSON");
    assert_eq!(recovered["instance"]["slug"], "bootstrap-replay-test");
    let initial_api_key = recovered["api_key"]
        .as_str()
        .expect("bootstrap should return its initial API key");

    let replay = client
        .post(&endpoint)
        .bearer_auth("test-only-operator-key")
        .header("Idempotency-Key", idempotency_key)
        .json(&request_body)
        .send()
        .await
        .expect("idempotent bootstrap replay should reach the service");
    assert_eq!(replay.status(), StatusCode::OK);
    let replay: Value = replay.json().await.expect("replay response should be JSON");
    assert_eq!(replay["instance"]["id"], recovered["instance"]["id"]);
    assert_eq!(replay["api_key"], initial_api_key);

    let mismatch = client
        .post(&endpoint)
        .bearer_auth("test-only-operator-key")
        .header("Idempotency-Key", idempotency_key)
        .json(&json!({
            "slug": "bootstrap-replay-test",
            "name": "Changed Payload"
        }))
        .send()
        .await
        .expect("mismatched same-key bootstrap should reach the service");
    assert_eq!(mismatch.status(), StatusCode::CONFLICT);
    let mismatch: Value = mismatch
        .json()
        .await
        .expect("idempotency mismatch should be JSON");
    assert_eq!(mismatch["error"]["code"], "idempotency_key_reused");

    let stored = server
        .state
        .store
        .find_instance_by_slug("bootstrap-replay-test")
        .await
        .expect("bootstrapped instance should remain readable")
        .expect("one bootstrap instance should exist");
    assert_eq!(stored.id, recovered["instance"]["id"]);
}

async fn create_directory(client: &Client, origin: &str, slug: &str, api_key: &str) -> Value {
    let response = client
        .post(format!("{origin}/v1/{slug}/directories"))
        .bearer_auth(api_key)
        .json(&json!({ "instance_wide": true }))
        .send()
        .await
        .expect("SCIM directory request should reach the service");
    assert_eq!(response.status(), StatusCode::CREATED);
    response
        .json()
        .await
        .expect("SCIM directory response should be JSON")
}

async fn create_organization(
    client: &Client,
    origin: &str,
    slug: &str,
    api_key: &str,
    name: &str,
    organization_slug: &str,
) -> Value {
    let response = client
        .post(format!("{origin}/v1/{slug}/organizations"))
        .bearer_auth(api_key)
        .json(&json!({ "name": name, "slug": organization_slug }))
        .send()
        .await
        .expect("organization creation should reach the service");
    assert_eq!(response.status(), StatusCode::OK);
    response
        .json()
        .await
        .expect("organization response should be JSON")
}

async fn create_organization_directory(
    client: &Client,
    origin: &str,
    slug: &str,
    api_key: &str,
    organization_id: &str,
) -> Value {
    let response = client
        .post(format!("{origin}/v1/{slug}/directories"))
        .bearer_auth(api_key)
        .json(&json!({ "org_id": organization_id }))
        .send()
        .await
        .expect("organization directory creation should reach the service");
    assert_eq!(response.status(), StatusCode::CREATED);
    response
        .json()
        .await
        .expect("organization directory response should be JSON")
}

async fn provision_scim_user(
    client: &Client,
    origin: &str,
    directory_id: &str,
    token: &str,
    user_name: &str,
    given_name: &str,
    family_name: &str,
) -> Value {
    let response = client
        .post(format!("{origin}/scim/{directory_id}/Users"))
        .bearer_auth(token)
        .json(&json!({
            "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
            "userName": user_name,
            "active": true,
            "name": { "givenName": given_name, "familyName": family_name },
        }))
        .send()
        .await
        .expect("SCIM user provisioning should reach the service");
    assert_eq!(response.status(), StatusCode::CREATED);
    response
        .json()
        .await
        .expect("SCIM user response should be JSON")
}

async fn get_scim_user(
    client: &Client,
    origin: &str,
    directory_id: &str,
    token: &str,
    user_id: &str,
) -> Value {
    let response = client
        .get(format!("{origin}/scim/{directory_id}/Users/{user_id}"))
        .bearer_auth(token)
        .send()
        .await
        .expect("SCIM user lookup should reach the service");
    assert_eq!(response.status(), StatusCode::OK);
    response
        .json()
        .await
        .expect("SCIM user response should be JSON")
}

async fn sign_in_as(
    client: &Client,
    origin: &str,
    public_origin: &str,
    slug: &str,
    email: &str,
    password: &str,
) -> reqwest::Response {
    let next = format!("/ui/{slug}/account");
    client
        .post(format!("{origin}/ui/{slug}/sign-in"))
        .header(header::ORIGIN, public_origin)
        .form(&[
            ("email", email),
            ("password", password),
            ("next", next.as_str()),
        ])
        .send()
        .await
        .expect("password sign-in should reach the service")
}

fn session_cookie(response: &reqwest::Response) -> String {
    response
        .headers()
        .get(header::SET_COOKIE)
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.split(';').next())
        .and_then(|value| value.strip_prefix("tsauth_session="))
        .expect("successful sign-in should set a session cookie")
        .to_owned()
}

async fn patch_scim_user_active(
    client: &Client,
    origin: &str,
    directory_id: &str,
    token: &str,
    user_id: &str,
    active: bool,
) -> reqwest::Response {
    client
        .patch(format!("{origin}/scim/{directory_id}/Users/{user_id}"))
        .bearer_auth(token)
        .json(&json!({
            "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
            "Operations": [{ "op": "replace", "path": "active", "value": active }],
        }))
        .send()
        .await
        .expect("SCIM active-state update should reach the service")
}

async fn patch_scim_user_name(
    client: &Client,
    origin: &str,
    directory_id: &str,
    token: &str,
    user_id: &str,
    user_name: &str,
) -> reqwest::Response {
    client
        .patch(format!("{origin}/scim/{directory_id}/Users/{user_id}"))
        .bearer_auth(token)
        .json(&json!({
            "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
            "Operations": [{ "op": "replace", "path": "userName", "value": user_name }],
        }))
        .send()
        .await
        .expect("SCIM profile update should reach the service")
}

async fn put_scim_user_and_activate(
    client: &Client,
    origin: &str,
    directory_id: &str,
    token: &str,
    user_id: &str,
    user_name: &str,
) -> reqwest::Response {
    client
        .put(format!("{origin}/scim/{directory_id}/Users/{user_id}"))
        .bearer_auth(token)
        .json(&json!({
            "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
            "userName": user_name,
            "active": true,
            "name": { "givenName": "Reactivated", "familyName": "User" },
        }))
        .send()
        .await
        .expect("SCIM full profile update should reach the service")
}

async fn mint_template_token(
    client: &Client,
    origin: &str,
    slug: &str,
    api_key: &str,
    template_name: &str,
    user_id: &str,
) -> reqwest::Response {
    client
        .post(format!(
            "{origin}/v1/{slug}/jwt-templates/{template_name}/tokens"
        ))
        .bearer_auth(api_key)
        .json(&json!({ "user_id": user_id }))
        .send()
        .await
        .expect("JWT template token mint should reach the service")
}

async fn issue_pkce_refresh_token(
    server: &TestServer,
    client: &Client,
    slug: &str,
    instance_id: &str,
    session_token: &str,
) -> String {
    const CLIENT_ID: &str = "scim-auth-gate-client";
    const REDIRECT_URI: &str = "https://client.example.test/scim-auth-gate";
    const VERIFIER: &str = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
    seed_record(
        server,
        "oauth_clients",
        "cli_scim_auth_gate",
        &json!({
            "id": "cli_scim_auth_gate",
            "instanceId": instance_id,
            "clientId": CLIENT_ID,
            "clientKey": format!("{instance_id}:{CLIENT_ID}"),
            "name": "SCIM authorization gate client",
            "redirectUris": [REDIRECT_URI],
            "allowedOrigins": [],
            "tokenEndpointAuthMethod": "none",
            "public": true,
            "createdAt": unix_now(),
        }),
    )
    .await;
    let challenge = URL_SAFE_NO_PAD.encode(Sha256::digest(VERIFIER.as_bytes()));
    let authorize = client
        .get(format!("{}/{slug}/oauth/authorize", server.origin))
        .query(&[
            ("client_id", CLIENT_ID),
            ("redirect_uri", REDIRECT_URI),
            ("response_type", "code"),
            ("scope", "openid email"),
            ("state", "scim-auth-gate-state"),
            ("code_challenge", challenge.as_str()),
            ("code_challenge_method", "S256"),
        ])
        .header(header::COOKIE, format!("tsauth_session={session_token}"))
        .send()
        .await
        .expect("OAuth authorize request should reach the service");
    assert_eq!(authorize.status(), StatusCode::SEE_OTHER);
    let location = authorize
        .headers()
        .get(header::LOCATION)
        .and_then(|value| value.to_str().ok())
        .expect("OAuth authorize should redirect to the registered callback");
    let callback = url::Url::parse(location).expect("OAuth callback should be an absolute URL");
    let code = callback
        .query_pairs()
        .find_map(|(name, value)| (name == "code").then(|| value.into_owned()))
        .expect("OAuth callback should contain an authorization code");
    let exchanged = client
        .post(format!("{}/{slug}/oauth/token", server.origin))
        .form(&[
            ("grant_type", "authorization_code"),
            ("client_id", CLIENT_ID),
            ("code", code.as_str()),
            ("redirect_uri", REDIRECT_URI),
            ("code_verifier", VERIFIER),
        ])
        .send()
        .await
        .expect("OAuth token exchange should reach the service");
    assert_eq!(exchanged.status(), StatusCode::OK);
    exchanged
        .json::<Value>()
        .await
        .expect("OAuth token response should be JSON")["refresh_token"]
        .as_str()
        .expect("OAuth token response should include a refresh token")
        .to_owned()
}

async fn create_checkout_session(
    server: &TestServer,
    client: &Client,
    slug: &str,
    api_key: &str,
    idempotency_key: &str,
) -> Value {
    let response = client
        .post(format!("{}/v1/{slug}/billing/checkout", server.origin))
        .bearer_auth(api_key)
        .header("Idempotency-Key", idempotency_key)
        .json(&json!({ "plan": "pro" }))
        .send()
        .await
        .expect("billing checkout request should reach the service");
    assert_eq!(response.status(), StatusCode::OK);
    response
        .json()
        .await
        .expect("billing checkout response should be JSON")
}

async fn checkout_intent_nonce(server: &TestServer, instance_id: &str) -> String {
    let id = format!("checkout_{}", sha256_hex(instance_id));
    let intent = server
        .state
        .store
        .get::<Value>("stripe_checkout_intents", &id)
        .await
        .expect("durable checkout intent should be readable")
        .expect("checkout should persist an intent");
    assert_eq!(intent["instanceId"], instance_id);
    assert_eq!(intent["status"], "open");
    intent["checkoutNonce"]
        .as_str()
        .expect("checkout intent carries a nonce")
        .to_owned()
}

async fn seed_record(server: &TestServer, collection: &str, id: &str, value: &Value) {
    server
        .state
        .store
        .create(collection, id, value)
        .await
        .unwrap_or_else(|error| panic!("seed {collection}/{id} through SDK fixture: {error}"));
}

async fn seed_dispatch_endpoint(server: &TestServer, instance_id: &str, endpoint_id: &str) {
    seed_dispatch_endpoint_at(server, instance_id, endpoint_id, unix_now() - 60).await;
}

async fn seed_dispatch_endpoint_at(
    server: &TestServer,
    instance_id: &str,
    endpoint_id: &str,
    created_at: i64,
) {
    let secret_box = auth_security::secretbox::encrypt(
        &server.state.options.master_key,
        b"webhook dispatcher test secret",
    )
    .expect("test webhook secret should encrypt");
    let endpoint = WebhookEndpoint {
        id: endpoint_id.to_owned(),
        instance_id: instance_id.to_owned(),
        // The dispatcher rejects loopback destinations before connecting. This
        // keeps the runtime test local while still exercising durable fanout.
        url: "https://127.0.0.1:443/never-connect".to_owned(),
        events: vec!["organization.*".to_owned()],
        signing_secret_box: secret_box,
        enabled: true,
        created_at,
    };
    server
        .state
        .store
        .create_webhook_endpoint(&endpoint)
        .await
        .expect("dispatcher endpoint should be stored");
}

async fn seed_dispatch_event(
    server: &TestServer,
    instance_id: &str,
    event_id: &str,
    created_at: i64,
) {
    let event = AuditEvent {
        id: event_id.to_owned(),
        instance_id: instance_id.to_owned(),
        r#type: "organization.updated".to_owned(),
        actor_id: None,
        data: serde_json::Map::new(),
        created_at,
        integrity_version: None,
        integrity_hash: None,
        integrity_signature: None,
        integrity_key_fingerprint: None,
    };
    server
        .state
        .store
        .create_audit_event(&event)
        .await
        .expect("pending audit event should be stored");
}

fn spawn_webhook_dispatcher(server: &TestServer) -> (oneshot::Sender<()>, JoinHandle<()>) {
    let (shutdown_tx, shutdown_rx) = oneshot::channel();
    let dispatcher = tokio::spawn(auth_app::features::webhooks::run_dispatcher(
        server.state.clone(),
        async move {
            let _ = shutdown_rx.await;
        },
    ));
    (shutdown_tx, dispatcher)
}

async fn wait_for_deliveries(
    server: &TestServer,
    instance_id: &str,
    expected_count: usize,
) -> Vec<WebhookDelivery> {
    timeout(Duration::from_secs(6), async {
        loop {
            let deliveries = server
                .state
                .store
                .list_webhook_deliveries(instance_id)
                .await
                .expect("webhook deliveries should be readable");
            if deliveries.len() >= expected_count {
                return deliveries;
            }
            sleep(Duration::from_millis(20)).await;
        }
    })
    .await
    .expect("dispatcher should persist the expected delivery jobs")
}

async fn wait_for_dispatch_completion(
    server: &TestServer,
    instance_id: &str,
    event_ids: &[&str],
) -> Vec<WebhookDelivery> {
    timeout(Duration::from_secs(6), async {
        loop {
            let deliveries = server
                .state
                .store
                .list_webhook_deliveries(instance_id)
                .await
                .expect("webhook deliveries should be readable");
            let mut all_complete = deliveries.len() == event_ids.len();
            for event_id in event_ids {
                let value = server
                    .state
                    .store
                    .get::<Value>("audit_events", event_id)
                    .await
                    .expect("audit dispatch state should be readable");
                all_complete &= value.and_then(|value| value.get("dispatchPending").cloned())
                    == Some(Value::Bool(false));
            }
            if all_complete {
                return deliveries;
            }
            sleep(Duration::from_millis(20)).await;
        }
    })
    .await
    .expect("dispatcher should durably complete audit fanout")
}

async fn stop_webhook_dispatcher(shutdown: oneshot::Sender<()>, dispatcher: JoinHandle<()>) {
    let _ = shutdown.send(());
    timeout(Duration::from_secs(2), dispatcher)
        .await
        .expect("dispatcher should stop promptly")
        .expect("dispatcher task should stop cleanly");
}

fn unix_now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("system clock should be after UNIX epoch")
        .as_secs() as i64
}

const TEST_SAML_IDP_ENTITY_ID: &str = "https://idp.example.test/metadata";
const TEST_SAML_IDP_SSO: &str = "https://idp.example.test/sso";
const TEST_SAML_KEY: &str = r#"-----BEGIN PRIVATE KEY-----
MIIEwgIBADANBgkqhkiG9w0BAQEFAASCBKwwggSoAgEAAoIBAgC6Jl6iPARyB13z
JEJ12SEuGgpJwhMmXVVxStswVrvrKD/0oWM6CJjbtjhzMvvVcrLcvnYZBdyvJeE5
eu3613wCuwNX4BNWYu38TDVVOYbmMpuwlB7e1iC6W92XqswTvK0LW+TjQsYC0HTx
8/gdtVQs5LocSwVa3C0rEjbChWB8JTfuck4LP1JVawzFHCOY2fWXI6ct3MdnzvJT
ojIgZELPAuE3T2NWhgrfpco+X5cXaZN44MBcnlsYfMF9LU8uGL3Mbtzy3TAsxVtL
voLPpOkr+hMmdMEybPBnqwuWtsrdfLuB5FXuuFZ8SZj3tZeQYtRreMp+rktMB/re
vPlmPOB5nwIDAQABAoIBAgCwsQD8n1lc3y9HPjCzafE7sE35qvTAYrFag0JAxONE
mAT08Eeea1CkpHc6qbcu6Ntr+oFgyRarTZpWFCBWDDnS4a6Pt8rDIc5hv/iTt7Ib
SQhM+JvAyqFwIwjYEK/7QAlFEenV6ajIPRP0Ia5ujJKktksN1gv0La/WBUjjJPTr
gdKDgP4SgEHwhDF3rgzk/284Dbmr4pOxvi290AnHrVER+oPlxy/WS4mOEBYVU2Rq
TbP07ej+ifYO/w9ZjJAc5l9UK2NDsv3JCUgui0PBgZ27z3dZ2TD2VbZXqSS4jahG
UUhwCT0sOzIWVsZypv4PvTHJl/uvgS1Be01n/FAtcJGAKQKBgQ49glSkdtfJtjQm
blqbacabkox+EM8EGkc9iveveD81E0gUYTL7AjVawowVYKQnYSZfsCdU7feLPQH2
bwjzAgbiY+QbbFcTT9FJu5D31qM4jqk24Bu2vjecHpEy8sJKkvoqv0CdWTUKhk98
7enh63LWo4Hjs6FVMAh8QKwsHjeMfQKBgQ0Sc4eO4ZSFuwCx8L4gG7MlvIeqVbHC
8kwDzJY3qECxDaDD1ogbGuDVa9p8bnZNmhHE6+nyep7AGvXKzWUzWqYYz2nqxwuC
/MAQHN6ruKFDaWzrDCfBMfwLsif4SgpcKkJj+1Y+LjPe1tgBw45cco9cWxZLPvVL
yxEAVTbFWXFlSwKBgQu7F/ZqVYyGGpbzYc06YfS+jAc4gthG5O7y/9vyrPhE3NFw
GHJK3RLe5Y1Ivwf7eMiH4zFDgZV/Go7XV7jjlzPco7Vx8dn5irM6Lk3KHQLwwHUd
Q5kQ/boJ3hR3CAyOKm3zcQHlnWtYdDRfEg6tkaxUrPV/gqbQ6nTTBuPOpEXWcQKB
gQnqSvMxr21mmleGoOK1nA0gvIXy75ksE3kREKeIg/i902Zz5U/Lr3GGsI5C/86A
QjLkOUV0hQnRESIKuAzhDQsbmofuaxgSPQC5uAw2GI9JgLf6+XdWFUHm5TVoIVEG
Y4+EIuphs83oYvHpNJnRCZwwI28fmBubZ+X3aKtoudVHTQKBgQCKN34ZGQUgs3OY
0VNlrlM5BMqO4uJD/kv5BgepqfbVEud2Mwmu+W1nPKv0rdWyIZunL2xVGD/9yRk0
ruC2q4dCQvFV2TsxN7Pkf+bvm9Ep9XokQE79g7EVty3mf8vYVv5KXDXEH808YxqU
gFQYnB76DUsI08o13SQtjDCm/oLxSA==
-----END PRIVATE KEY-----"#;
const TEST_SAML_CERT: &str = r#"-----BEGIN CERTIFICATE-----
MIID0TCCArigAwIBAgIBADANBgkqhkiG9w0BAQsFADCBgTELMAkGA1UEBhMCaGsx
EjAQBgNVBAgMCUhvbmcgS29uZzETMBEGA1UECgwKbm9kZS1zYW1sMjEXMBUGA1UE
AwwOc2FtbGlmeS5qcy5vcmcxCzAJBgNVBAcMAkhLMSMwIQYJKoZIhvcNAQkBFhRu
b2RlLnNhbWwyQGdtYWlsLmNvbTAeFw0yMzAxMDUyMjQ3NTBaFw0zMzAxMDIyMjQ3
NTBaMIGBMQswCQYDVQQGEwJoazESMBAGA1UECAwJSG9uZyBLb25nMRMwEQYDVQQK
DApub2RlLXNhbWwyMRcwFQYDVQQDDA5zYW1saWZ5LmpzLm9yZzELMAkGA1UEBwwC
SEsxIzAhBgkqhkiG9w0BCQEWFG5vZGUuc2FtbDJAZ21haWwuY29tMIIBIzANBgkq
hkiG9w0BAQEFAAOCARAAMIIBCwKCAQIAuiZeojwEcgdd8yRCddkhLhoKScITJl1V
cUrbMFa76yg/9KFjOgiY27Y4czL71XKy3L52GQXcryXhOXrt+td8ArsDV+ATVmLt
/Ew1VTmG5jKbsJQe3tYgulvdl6rME7ytC1vk40LGAtB08fP4HbVULOS6HEsFWtwt
KxI2woVgfCU37nJOCz9SVWsMxRwjmNn1lyOnLdzHZ87yU6IyIGRCzwLhN09jVoYK
36XKPl+XF2mTeODAXJ5bGHzBfS1PLhi9zG7c8t0wLMVbS76Cz6TpK/oTJnTBMmzw
Z6sLlrbK3Xy7geRV7rhWfEmY97WXkGLUa3jKfq5LTAf63rz5ZjzgeZ8CAwEAAaNQ
ME4wHQYDVR0OBBYEFIW8z2CmYsUvTbX8IFHE+IdbkcsHMB8GA1UdIwQYMBaAFIW8
z2CmYsUvTbX8IFHE+IdbkcsHMAwGA1UdEwQFMAMBAf8wDQYJKoZIhvcNAQELBQAD
ggECAAvSnm6IQYmPPVpoUvwC3K3jh28HQ/8S5QMgRGw+RcqgJbo5wIAn0OOUJLfR
rLtcx2UfaFPn3QKvGOrX71ekb6FF0hzEq+U3Mbv2KEWBEwbu5D+N93uvf3ryeM7o
X4t6Qi5gKe486PrOBu2CyUtveFBqECWCGRG5yN+/4bd2zh/hug3lD28hicA7rsnA
FOrEmnhnDhzWzoG+ih1uCzjui21gFGeKRspWkXf+4Vgc7lL+tYCI0sVfe1huRwFh
SkTC5yT/7mYw4aLlRAKOxAEMHQjj5bFyr8NZa296YIcdBbdRm8oqGDqWcK4wcu/I
yFtbTMIWwNL9n/lLh695eNwDmg28
-----END CERTIFICATE-----"#;

fn signed_saml_response(redirect_url: &str, acs_url: &str, email: &str) -> (String, String) {
    let mut sp_policy = SpValidationPolicy::strict();
    sp_policy.authn_requests = AuthnRequestSigningPolicy::DoNotSignForCompatibility;
    sp_policy.assertions = AssertionSignaturePolicy::AllowUnsignedForCompatibility;
    sp_policy.responses = ResponseSignaturePolicy::RequireForEncryptedCbc;
    sp_policy.audience = AudienceValidationPolicy::Validate;
    let sp = Saml::sp(
        SpConfig::builder(EntityId::try_new(acs_url).expect("ACS entity ID should parse"))
            .acs_endpoint(AcsEndpoint::post(acs_url).expect("ACS endpoint should parse"))
            .validation(sp_policy)
            .build()
            .expect("test SP configuration should build"),
    )
    .expect("test SP should build");
    let sp_descriptor = SpDescriptor::from_metadata_xml_for(
        EntityId::try_new(acs_url).expect("ACS entity ID should parse"),
        sp.metadata_xml(),
        MetadataTrustPolicy::UnsignedForCompatibility,
    )
    .expect("test SP metadata should parse");
    let idp = Saml::idp(
        IdpConfig::builder(
            EntityId::try_new(TEST_SAML_IDP_ENTITY_ID).expect("test IdP entity ID should parse"),
        )
        .sso_endpoint(
            SsoEndpoint::redirect(TEST_SAML_IDP_SSO).expect("test IdP SSO endpoint should parse"),
        )
        .credentials(Credentials {
            signing_key: Some(PrivateKeyPem::new(TEST_SAML_KEY)),
            signing_certificate: Some(CertificatePem::new(TEST_SAML_CERT)),
            ..Credentials::default()
        })
        .validation(IdpValidationPolicy::compatibility())
        .build()
        .expect("test IdP configuration should build"),
    )
    .expect("test IdP should build");
    let redirect = reqwest::Url::parse(redirect_url).expect("IdP redirect URL should parse");
    let received = idp
        .receive_sso(
            &sp_descriptor,
            BrowserInput::<AuthnRequest>::redirect(
                redirect
                    .query()
                    .expect("IdP redirect should carry a request"),
            ),
            SamlValidationContext::new(
                SystemTime::now(),
                saml_rs::ReplayPolicy::DisabledForCompatibility,
            ),
        )
        .expect("test IdP should accept the app's SSO request");
    let post = idp
        .respond_sso(
            &sp_descriptor,
            &received,
            Subject::new(
                NameId::new(email, Some(NameIdFormat::EmailAddress)),
                Vec::new(),
            ),
            RespondSso::post(),
        )
        .expect("test IdP should sign its SAML response");
    let fields = post
        .post_form()
        .expect("IdP response should use HTTP POST binding")
        .fields()
        .to_vec();
    let field = |name: &str| {
        fields
            .iter()
            .find(|field| field.name() == name)
            .map(|field| field.value().to_owned())
            .unwrap_or_else(|| panic!("signed IdP form should contain {name}"))
    };
    (field("SAMLResponse"), field("RelayState"))
}

#[tokio::test]
async fn openapi_and_scalar_are_reachable_over_the_application_socket() {
    let server = TestServer::start().await;
    let client = server.client();

    let document_response = client
        .get(format!("{}/openapi/v1.json", server.origin))
        .send()
        .await
        .expect("OpenAPI request should reach the service");
    assert_eq!(document_response.status(), StatusCode::OK);
    assert!(
        document_response
            .headers()
            .get(header::CONTENT_TYPE)
            .and_then(|value| value.to_str().ok())
            .is_some_and(|value| value.starts_with("application/json"))
    );
    let document: serde_json::Value = document_response
        .json()
        .await
        .expect("OpenAPI endpoint should return JSON");
    assert_eq!(document["info"]["title"], "TerraScale Auth API");
    assert!(document["openapi"].as_str().is_some());
    let paths = document["paths"]
        .as_object()
        .expect("OpenAPI document should expose its HTTP paths");
    assert!(paths.contains_key("/healthz"));
    assert!(paths.contains_key("/readyz"));
    assert!(paths.contains_key("/ui/{slug}/sign-in"));
    assert!(paths.contains_key("/v1/{slug}/api-keys"));
    assert!(paths.contains_key("/scim/{directory_id}/Users"));

    let scalar_response = client
        .get(format!("{}/scalar", server.origin))
        .send()
        .await
        .expect("Scalar request should reach the service");
    assert_eq!(scalar_response.status(), StatusCode::OK);
    assert!(
        scalar_response
            .headers()
            .get(header::CONTENT_TYPE)
            .and_then(|value| value.to_str().ok())
            .is_some_and(|value| value.starts_with("text/html"))
    );
    let scalar_html = scalar_response
        .text()
        .await
        .expect("Scalar should return its HTML shell");
    assert!(scalar_html.contains("<title>Scalar</title>"));
    assert!(scalar_html.contains("id=\"api-reference\""));
    assert!(scalar_html.contains("\"/healthz\""));
}

#[tokio::test]
async fn saml_acs_waits_for_local_mfa_and_restores_the_safe_continuation() {
    let server = TestServer::start_with_config(|options| {
        options.public_origin = options.public_origin.replacen("http://", "https://", 1);
    })
    .await;
    let client = server.client();
    let bootstrap = create_instance(&server, &client, "saml-local-mfa").await;
    let instance_id = bootstrap["instance"]["id"]
        .as_str()
        .expect("instance ID is returned");
    let slug = bootstrap["instance"]["slug"]
        .as_str()
        .expect("instance slug is returned");
    let api_key = bootstrap["api_key"]
        .as_str()
        .expect("initial API key is returned");
    let email = "saml-local-mfa@example.test";
    let user = users::find_or_create_verified_user(
        &server.state,
        instance_id,
        email,
        Some("SAML Local MFA User"),
    )
    .await
    .expect("verified local identity should be seedable");
    let seed = b"saml-local-mfa-totp-seed";
    let secret_box = secretbox::encrypt(&server.state.options.master_key, seed)
        .expect("TOTP seed should encrypt");
    server
        .state
        .store
        .create_mfa_factor(&MfaFactor {
            id: "mfa_saml_local_mfa".to_owned(),
            instance_id: instance_id.to_owned(),
            user_id: user.id.clone(),
            r#type: "totp".to_owned(),
            secret_box: Some(secret_box),
            recovery_code_hashes: None,
            confirmed: true,
            created_at: unix_now(),
            last_used_totp_step: None,
        })
        .await
        .expect("confirmed local TOTP factor should be seedable");

    let connection_response = client
        .post(format!("{}/v1/{slug}/connections", server.origin))
        .bearer_auth(api_key)
        .json(&json!({
            "type": "saml",
            "name": "SAML local MFA fixture",
            "idp_entity_id": TEST_SAML_IDP_ENTITY_ID,
            "sso_redirect_url": TEST_SAML_IDP_SSO,
            "x509_certificate_pem": TEST_SAML_CERT,
        }))
        .send()
        .await
        .expect("SAML connection creation should reach the service");
    assert_eq!(connection_response.status(), StatusCode::CREATED);
    let connection: Value = connection_response
        .json()
        .await
        .expect("SAML connection response should be JSON");
    let connection_id = connection["id"]
        .as_str()
        .expect("SAML connection ID is returned");
    let continuation = format!("/ui/{slug}/account?source=saml");

    let login = client
        .get(format!(
            "{}/ui/{slug}/connections/{connection_id}/saml/login",
            server.origin
        ))
        .query(&[("next", continuation.as_str())])
        .send()
        .await
        .expect("SAML login should reach the service");
    assert_eq!(login.status(), StatusCode::SEE_OTHER);
    let redirect_url = login
        .headers()
        .get(header::LOCATION)
        .and_then(|value| value.to_str().ok())
        .expect("SAML login should redirect to the configured IdP")
        .to_owned();
    let redirect = reqwest::Url::parse(&redirect_url).expect("IdP redirect URL should parse");
    let relay = redirect
        .query_pairs()
        .find(|(key, _)| key == "RelayState")
        .map(|(_, value)| value.into_owned())
        .expect("SAML login should include RelayState");
    let correlation_cookie = login
        .headers()
        .get_all(header::SET_COOKIE)
        .iter()
        .filter_map(|value| value.to_str().ok())
        .find(|value| value.starts_with("tsauth_saml_state="))
        .and_then(|value| value.split(';').next())
        .expect("SAML login should bind its correlation cookie to the ACS")
        .to_owned();
    assert_eq!(
        correlation_cookie,
        format!("tsauth_saml_state={}", sha256_hex(&relay))
    );

    let acs_url = format!(
        "{}/{slug}/connections/{connection_id}/saml/acs",
        server.state.options.public_origin.trim_end_matches('/')
    );
    let (saml_response, response_relay) = signed_saml_response(&redirect_url, &acs_url, email);
    assert_eq!(response_relay, relay);
    let acs = client
        .post(format!(
            "{}/{slug}/connections/{connection_id}/saml/acs",
            server.origin
        ))
        .header(header::COOKIE, correlation_cookie)
        .form(&[
            ("SAMLResponse", saml_response.as_str()),
            ("RelayState", relay.as_str()),
        ])
        .send()
        .await
        .expect("signed SAML response should reach the ACS");
    assert_eq!(acs.status(), StatusCode::SEE_OTHER);
    assert!(
        !acs.headers()
            .get_all(header::SET_COOKIE)
            .iter()
            .filter_map(|value| value.to_str().ok())
            .any(|value| value.starts_with("tsauth_session=")),
        "the SAML first factor must not establish a session before local MFA"
    );
    let challenge_location = acs
        .headers()
        .get(header::LOCATION)
        .and_then(|value| value.to_str().ok())
        .expect("SAML ACS should redirect to the local MFA challenge");
    let challenge_url = reqwest::Url::parse(&format!("{}{}", server.origin, challenge_location))
        .expect("MFA challenge URL should parse");
    assert_eq!(challenge_url.path(), format!("/ui/{slug}/mfa/verify"));
    let pending_token = challenge_url
        .query_pairs()
        .find(|(key, _)| key == "token")
        .map(|(_, value)| value.into_owned())
        .expect("local MFA challenge should carry its pending token");
    let pending = server
        .state
        .store
        .find_verification_token(&sha256_hex(&pending_token))
        .await
        .expect("local MFA challenge should be readable")
        .expect("SAML ACS should persist its local MFA challenge");
    assert_eq!(pending.purpose, "mfa.pending");
    assert_eq!(
        pending.authentication_method.as_deref(),
        Some(format!("saml:{connection_id}").as_str())
    );
    assert_eq!(
        pending.authentication_next.as_deref(),
        Some(continuation.as_str())
    );
    assert_eq!(pending.email_snapshot.as_deref(), Some(email));
    assert_eq!(pending.user_auth_epoch, Some(user.auth_epoch));

    let verification_page = client
        .get(format!(
            "{}/ui/{slug}/mfa/verify?token={pending_token}",
            server.origin
        ))
        .send()
        .await
        .expect("local MFA page should reach the service");
    assert_eq!(verification_page.status(), StatusCode::OK);
    let verification_html = verification_page
        .text()
        .await
        .expect("local MFA page should be HTML");
    assert!(verification_html.contains(&format!("name=\"next\" value=\"{continuation}\"")));

    let code = totp::compute(seed, unix_now()).expect("current local TOTP code should generate");
    let completed = client
        .post(format!("{}/ui/{slug}/mfa/verify", server.origin))
        .header(header::ORIGIN, &server.state.options.public_origin)
        .form(&[
            ("token", pending_token.as_str()),
            ("code", code.as_str()),
            ("next", "/ui/attacker/account"),
        ])
        .send()
        .await
        .expect("local TOTP verification should reach the service");
    assert_eq!(completed.status(), StatusCode::SEE_OTHER);
    assert_eq!(
        completed
            .headers()
            .get(header::LOCATION)
            .and_then(|value| value.to_str().ok()),
        Some(continuation.as_str()),
        "MFA should restore the validated SAML continuation instead of posted input"
    );
    assert!(
        completed
            .headers()
            .get_all(header::SET_COOKIE)
            .iter()
            .filter_map(|value| value.to_str().ok())
            .any(|value| value.starts_with("tsauth_session=")),
        "a session should be established after the local TOTP succeeds"
    );
}

#[tokio::test]
async fn management_user_pagination_advances_past_deleted_rows() {
    let server = TestServer::start().await;
    let client = server.client();
    let bootstrap = create_instance(&server, &client, "users-page-deleted").await;
    let api_key = bootstrap["api_key"]
        .as_str()
        .expect("initial API key is returned");
    let instance_id = bootstrap["instance"]["id"]
        .as_str()
        .expect("instance id is returned");

    for index in 0..120 {
        let id = format!("usr_{index:03}");
        seed_record(
            &server,
            "users",
            &id,
            &json!({
                "id": id,
                "instanceId": instance_id,
                "email": format!("{id}@example.test"),
                "emailVerified": true,
                "deleted": index < 99,
                "createdAt": index,
            }),
        )
        .await;
    }

    let first_page = client
        .get(format!(
            "{}/v1/users-page-deleted/users?limit=20",
            server.origin
        ))
        .bearer_auth(api_key)
        .send()
        .await
        .expect("first management-user page should reach the service");
    assert_eq!(first_page.status(), StatusCode::OK);
    let first_page: Value = first_page
        .json()
        .await
        .expect("first management-user page should be JSON");
    let first_ids: Vec<_> = first_page["data"]
        .as_array()
        .expect("first page contains user rows")
        .iter()
        .map(|user| user["id"].as_str().expect("user row has an id"))
        .collect();
    assert_eq!(first_ids.len(), 20);
    assert_eq!(first_ids.first(), Some(&"usr_099"));
    assert_eq!(first_ids.last(), Some(&"usr_118"));

    let second_page = client
        .get(format!(
            "{}/v1/users-page-deleted/users?limit=20&starting_after=usr_118",
            server.origin
        ))
        .bearer_auth(api_key)
        .send()
        .await
        .expect("cursor management-user page should reach the service");
    assert_eq!(second_page.status(), StatusCode::OK);
    let second_page: Value = second_page
        .json()
        .await
        .expect("cursor management-user page should be JSON");
    let second_ids: Vec<_> = second_page["data"]
        .as_array()
        .expect("second page contains user rows")
        .iter()
        .map(|user| user["id"].as_str().expect("user row has an id"))
        .collect();
    assert_eq!(second_ids, ["usr_119"]);
}

#[tokio::test]
async fn jwt_template_uses_the_first_active_membership_and_empties_org_claims_without_one() {
    let server = TestServer::start().await;
    let client = server.client();
    let bootstrap = create_instance(&server, &client, "template-active-org-fallback").await;
    let api_key = bootstrap["api_key"]
        .as_str()
        .expect("initial API key is returned");
    let instance_id = bootstrap["instance"]["id"]
        .as_str()
        .expect("instance id is returned");
    let created_at = unix_now();

    for (id, status, created) in [
        ("org_deleted_template", "deleted", created_at - 3),
        ("org_deleting_template", "deleting", created_at - 2),
        ("org_active_template", "active", created_at - 1),
        ("org_active_later_template", "active", created_at),
    ] {
        seed_record(
            &server,
            "organizations",
            id,
            &json!({
                "id": id,
                "instanceId": instance_id,
                "name": id,
                "slugKey": format!("{instance_id}:{id}"),
                "status": status,
                "createdAt": created,
            }),
        )
        .await;
    }
    for (user_id, organization_id, role, created) in [
        (
            "usr_template_active",
            "org_deleted_template",
            "stale-deleted-role",
            created_at - 3,
        ),
        (
            "usr_template_active",
            "org_deleting_template",
            "stale-deleting-role",
            created_at - 2,
        ),
        (
            "usr_template_active",
            "org_active_template",
            "active-role",
            created_at - 1,
        ),
        (
            "usr_template_active",
            "org_active_later_template",
            "later-active-role",
            created_at,
        ),
        (
            "usr_template_without_active_org",
            "org_deleted_template",
            "stale-deleted-role",
            created_at - 3,
        ),
        (
            "usr_template_without_active_org",
            "org_deleting_template",
            "stale-deleting-role",
            created_at - 2,
        ),
    ] {
        let membership_id = format!("mem_{organization_id}_{user_id}");
        seed_record(
            &server,
            "memberships",
            &membership_id,
            &json!({
                "id": membership_id,
                "instanceId": instance_id,
                "orgId": organization_id,
                "userId": user_id,
                "memberKey": format!("{organization_id}:{user_id}"),
                "role": role,
                "createdAt": created,
            }),
        )
        .await;
    }
    for user_id in ["usr_template_active", "usr_template_without_active_org"] {
        seed_record(
            &server,
            "users",
            user_id,
            &json!({
                "id": user_id,
                "instanceId": instance_id,
                "email": format!("{user_id}@example.test"),
                "emailVerified": true,
                "deleted": false,
                "banned": false,
                "createdAt": created_at,
            }),
        )
        .await;
    }

    let template = client
        .post(format!(
            "{}/v1/template-active-org-fallback/jwt-templates",
            server.origin
        ))
        .bearer_auth(api_key)
        .json(&json!({
            "name": "membership-context",
            "claims": {
                "org_id": "{{ org.id }}",
                "org_role": "{{  org.role  }}",
            },
            "lifetime_seconds": 300,
        }))
        .send()
        .await
        .expect("management API key should create the JWT template");
    assert_eq!(template.status(), StatusCode::OK);

    let active_token = mint_template_token(
        &client,
        &server.origin,
        "template-active-org-fallback",
        api_key,
        "membership-context",
        "usr_template_active",
    )
    .await;
    assert_eq!(active_token.status(), StatusCode::OK);
    let active_token: Value = active_token
        .json()
        .await
        .expect("minted JWT response should be JSON");
    let active_claims = auth_security::jwt::decode_unverified(
        active_token["jwt"]
            .as_str()
            .expect("minted JWT is returned"),
    )
    .expect("minted JWT payload should decode");
    assert_eq!(active_claims["org_id"], "org_active_template");
    assert_eq!(active_claims["org_role"], "active-role");

    let no_active_token = mint_template_token(
        &client,
        &server.origin,
        "template-active-org-fallback",
        api_key,
        "membership-context",
        "usr_template_without_active_org",
    )
    .await;
    assert_eq!(no_active_token.status(), StatusCode::OK);
    let no_active_token: Value = no_active_token
        .json()
        .await
        .expect("minted JWT response should be JSON");
    let no_active_claims = auth_security::jwt::decode_unverified(
        no_active_token["jwt"]
            .as_str()
            .expect("minted JWT is returned"),
    )
    .expect("minted JWT payload should decode");
    assert_eq!(no_active_claims["org_id"], "");
    assert_eq!(no_active_claims["org_role"], "");
}

#[tokio::test]
async fn liveness_and_metrics_authentication_work_over_the_application_socket() {
    let server = TestServer::start().await;
    let client = server.client();

    let liveness = client
        .get(format!("{}/healthz", server.origin))
        .send()
        .await
        .expect("liveness request should reach the service");
    assert_eq!(liveness.status(), StatusCode::OK);
    let body: serde_json::Value = liveness.json().await.expect("liveness body is JSON");
    assert_eq!(body["status"], "ok");
    assert_eq!(body["version"], support::expected_version());

    let missing_key = client
        .get(format!("{}/metrics", server.origin))
        .send()
        .await
        .expect("unauthenticated metrics request should reach the service");
    assert_eq!(missing_key.status(), StatusCode::UNAUTHORIZED);

    let wrong_key = client
        .get(format!("{}/metrics", server.origin))
        .header("X-TerraScale-Metrics-Key", "wrong-key")
        .send()
        .await
        .expect("invalid metrics credentials should reach the service");
    assert_eq!(wrong_key.status(), StatusCode::UNAUTHORIZED);

    let authorized = client
        .get(format!("{}/metrics", server.origin))
        .header("X-TerraScale-Metrics-Key", "test-only-metrics-key")
        .send()
        .await
        .expect("authorized metrics request should reach the service");
    assert_eq!(authorized.status(), StatusCode::OK);
    let metrics = authorized.text().await.expect("metrics body is text");
    assert!(metrics.contains("ts_auth_liveness 1"));
    assert!(!metrics.contains("test-only-metrics-key"));
}

#[tokio::test]
async fn readiness_fails_closed_when_terrabase_cannot_be_reached() {
    let server = TestServer::start_without_storage().await;
    let client = server.client();

    let response = client
        .get(format!("{}/readyz", server.origin))
        .send()
        .await
        .expect("readiness request should reach the service");
    assert_eq!(response.status(), StatusCode::SERVICE_UNAVAILABLE);
    let body: serde_json::Value = response.json().await.expect("readiness body is JSON");
    assert_eq!(body["status"], "not_ready");
    assert_eq!(body["version"], support::expected_version());
}

#[tokio::test]
async fn hosted_ui_rejects_cross_origin_mutations_and_sets_protected_session_cookie() {
    let server = TestServer::start().await;
    let client = server.client();
    create_instance(&server, &client, "hosted-cookie-test").await;
    let public_origin = server.state.options.public_origin.as_str();

    let rejected = client
        .post(format!("{}/ui/hosted-cookie-test/sign-up", server.origin))
        .header(header::ORIGIN, "https://attacker.example")
        .form(&[
            ("email", "blocked@example.test"),
            ("password", "correct horse battery staple"),
        ])
        .send()
        .await
        .expect("cross-origin sign-up should reach the service");
    assert_eq!(rejected.status(), StatusCode::FORBIDDEN);
    let instance = server
        .state
        .store
        .find_instance_by_slug("hosted-cookie-test")
        .await
        .expect("instance lookup should use the fixture");
    let instance_id = instance.expect("operator route created the instance").id;
    assert!(
        server
            .state
            .store
            .find_user_by_email(&instance_id, "blocked@example.test")
            .await
            .expect("rejected sign-up should leave storage readable")
            .is_none()
    );

    let accepted = client
        .post(format!("{}/ui/hosted-cookie-test/sign-up", server.origin))
        .header(header::ORIGIN, public_origin)
        .form(&[
            ("email", "member@example.test"),
            ("password", "correct horse battery staple"),
        ])
        .send()
        .await
        .expect("same-origin sign-up should reach the service");
    assert_eq!(accepted.status(), StatusCode::SEE_OTHER);
    assert_eq!(
        accepted
            .headers()
            .get(header::LOCATION)
            .and_then(|value| value.to_str().ok()),
        Some("/ui/hosted-cookie-test/account")
    );
    let cookie = accepted
        .headers()
        .get(header::SET_COOKIE)
        .expect("successful sign-up should set a session cookie")
        .to_str()
        .expect("session cookie is valid header text")
        .to_owned();
    let attributes = cookie.to_ascii_lowercase();
    assert!(attributes.starts_with("tsauth_session="));
    assert!(attributes.contains("; path=/"));
    assert!(attributes.contains("; secure"));
    assert!(attributes.contains("; httponly"));
    assert!(attributes.contains("; samesite=lax"));
    assert!(!attributes.contains("; domain="));
    assert!(!attributes.contains("; max-age="));
    let cookie_pair = cookie
        .split(';')
        .next()
        .expect("cookie has a name and value");

    let account = client
        .get(format!("{}/ui/hosted-cookie-test/account", server.origin))
        .header(header::COOKIE, cookie_pair)
        .send()
        .await
        .expect("the issued browser cookie should reach the account route");
    assert_eq!(account.status(), StatusCode::OK);
    assert!(
        account
            .text()
            .await
            .expect("account page should be HTML")
            .contains("Your account")
    );

    let cross_site_sign_out = client
        .post(format!("{}/ui/hosted-cookie-test/sign-out", server.origin))
        .header(header::ORIGIN, "https://attacker.example")
        .header(header::COOKIE, cookie_pair)
        .send()
        .await
        .expect("cross-origin sign-out should reach the service");
    assert_eq!(cross_site_sign_out.status(), StatusCode::FORBIDDEN);

    let get_sign_out = client
        .get(format!("{}/ui/hosted-cookie-test/sign-out", server.origin))
        .send()
        .await
        .expect("GET sign-out should reach the router");
    assert_eq!(get_sign_out.status(), StatusCode::METHOD_NOT_ALLOWED);

    let signed_out = client
        .post(format!("{}/ui/hosted-cookie-test/sign-out", server.origin))
        .header(header::ORIGIN, public_origin)
        .header(header::COOKIE, cookie_pair)
        .send()
        .await
        .expect("same-origin sign-out should reach the service");
    assert_eq!(signed_out.status(), StatusCode::SEE_OTHER);
    let cleared = signed_out
        .headers()
        .get(header::SET_COOKIE)
        .expect("sign-out should clear the session cookie")
        .to_str()
        .expect("cleared cookie is valid header text")
        .to_ascii_lowercase();
    assert!(cleared.contains("; secure"));
    assert!(cleared.contains("; httponly"));
    assert!(cleared.contains("; samesite=lax"));
    assert!(cleared.contains("; max-age=0"));
}

#[tokio::test]
async fn management_keys_enforce_scope_tenant_and_per_key_rate_limit() {
    let server = TestServer::start().await;
    let client = server.client();
    let alpha = create_instance(&server, &client, "management-alpha").await;
    create_instance(&server, &client, "management-bravo").await;
    let initial_key = alpha["api_key"].as_str().expect("initial key is returned");

    let restricted = client
        .post(format!("{}/v1/management-alpha/api-keys", server.origin))
        .bearer_auth(initial_key)
        .json(&json!({ "name": "users-only", "scopes": ["users"] }))
        .send()
        .await
        .expect("management key creation should reach the service");
    assert_eq!(restricted.status(), StatusCode::CREATED);
    let restricted: Value = restricted
        .json()
        .await
        .expect("management key response should be JSON");
    let restricted_key = restricted["api_key"].as_str().expect("new key is returned");

    let allowed = client
        .get(format!("{}/v1/management-alpha/users", server.origin))
        .bearer_auth(restricted_key)
        .send()
        .await
        .expect("in-scope management request should reach the service");
    assert_eq!(allowed.status(), StatusCode::OK);

    let out_of_scope = client
        .get(format!("{}/v1/management-alpha/api-keys", server.origin))
        .bearer_auth(restricted_key)
        .send()
        .await
        .expect("out-of-scope management request should reach the service");
    assert_eq!(out_of_scope.status(), StatusCode::FORBIDDEN);
    let body: Value = out_of_scope.json().await.expect("scope error is JSON");
    assert_eq!(body["error"]["code"], "insufficient_scope");

    let wrong_tenant = client
        .get(format!("{}/v1/management-bravo/users", server.origin))
        .bearer_auth(restricted_key)
        .send()
        .await
        .expect("cross-tenant management request should reach the service");
    assert_eq!(wrong_tenant.status(), StatusCode::UNAUTHORIZED);

    let limited_server = TestServer::start_with_config(|options| {
        options.rate_limit_per_window = 1;
    })
    .await;
    let limited_client = limited_server.client();
    let limited_alpha = create_instance(&limited_server, &limited_client, "limit-alpha").await;
    let limited_bravo = create_instance(&limited_server, &limited_client, "limit-bravo").await;
    let alpha_key = limited_alpha["api_key"]
        .as_str()
        .expect("initial key is returned");
    let bravo_key = limited_bravo["api_key"]
        .as_str()
        .expect("initial key is returned");
    let first = limited_client
        .get(format!("{}/v1/limit-alpha/users", limited_server.origin))
        .bearer_auth(alpha_key)
        .send()
        .await
        .expect("first rate-limited request should reach the service");
    assert_eq!(first.status(), StatusCode::OK);
    let second = limited_client
        .get(format!("{}/v1/limit-alpha/users", limited_server.origin))
        .bearer_auth(alpha_key)
        .send()
        .await
        .expect("over-limit request should reach the service");
    assert_eq!(second.status(), StatusCode::TOO_MANY_REQUESTS);
    assert!(
        second
            .headers()
            .get(header::RETRY_AFTER)
            .and_then(|value| value.to_str().ok())
            .and_then(|value| value.parse::<u64>().ok())
            .is_some_and(|value| value > 0)
    );
    assert_eq!(
        second
            .headers()
            .get("x-ratelimit-remaining")
            .and_then(|value| value.to_str().ok()),
        Some("0")
    );
    let independent_key = limited_client
        .get(format!("{}/v1/limit-bravo/users", limited_server.origin))
        .bearer_auth(bravo_key)
        .send()
        .await
        .expect("a different API key has an independent rate bucket");
    assert_eq!(independent_key.status(), StatusCode::OK);
}

#[tokio::test]
async fn ambiguous_api_key_create_is_revoked_when_storage_reports_failure() {
    let server = TestServer::start().await;
    let client = server.client();
    let instance = create_instance(&server, &client, "management-ambiguous-key").await;
    server
        ._storage_fixture
        .as_ref()
        .expect("contract fixture is available")
        .fail_next_create_only_after_commit("api_keys");

    let response = client
        .post(format!(
            "{}/v1/management-ambiguous-key/api-keys",
            server.origin
        ))
        .bearer_auth(
            instance["api_key"]
                .as_str()
                .expect("initial key is returned"),
        )
        .json(&json!({ "name": "ambiguous-write", "scopes": ["users"] }))
        .send()
        .await
        .expect("ambiguous API-key write should reach the service");
    assert_eq!(response.status(), StatusCode::INTERNAL_SERVER_ERROR);
    let response_body = response
        .text()
        .await
        .expect("failed API-key response should be readable");
    assert!(!response_body.contains("sk_"));
    assert!(!response_body.contains("api_key"));

    let listed = client
        .get(format!(
            "{}/v1/management-ambiguous-key/api-keys",
            server.origin
        ))
        .bearer_auth(
            instance["api_key"]
                .as_str()
                .expect("initial key is returned"),
        )
        .send()
        .await
        .expect("API keys should remain listable");
    assert_eq!(listed.status(), StatusCode::OK);
    let listed: Value = listed.json().await.expect("API-key list should be JSON");
    assert!(listed["data"].as_array().is_some_and(|keys| {
        keys.iter()
            .any(|key| key["name"] == "ambiguous-write" && key["revoked"] == true)
    }));
}

#[tokio::test]
async fn api_key_is_revoked_when_its_audit_write_fails() {
    let server = TestServer::start().await;
    let client = server.client();
    let instance = create_instance(&server, &client, "management-key-audit-failure").await;
    server
        ._storage_fixture
        .as_ref()
        .expect("contract fixture is available")
        .fail_next_create_only_before_commit("audit_events");

    let response = client
        .post(format!(
            "{}/v1/management-key-audit-failure/api-keys",
            server.origin
        ))
        .bearer_auth(
            instance["api_key"]
                .as_str()
                .expect("initial key is returned"),
        )
        .json(&json!({ "name": "audit-fails", "scopes": ["users"] }))
        .send()
        .await
        .expect("API-key request should reach the service");
    assert_eq!(response.status(), StatusCode::INTERNAL_SERVER_ERROR);
    let response_body = response
        .text()
        .await
        .expect("failed API-key response should be readable");
    assert!(!response_body.contains("sk_"));
    assert!(!response_body.contains("api_key"));

    let listed = client
        .get(format!(
            "{}/v1/management-key-audit-failure/api-keys",
            server.origin
        ))
        .bearer_auth(
            instance["api_key"]
                .as_str()
                .expect("initial key is returned"),
        )
        .send()
        .await
        .expect("API keys should remain listable");
    assert_eq!(listed.status(), StatusCode::OK);
    let listed: Value = listed.json().await.expect("API-key list should be JSON");
    assert!(listed["data"].as_array().is_some_and(|keys| {
        keys.iter()
            .any(|key| key["name"] == "audit-fails" && key["revoked"] == true)
    }));
}

#[tokio::test]
async fn oauth_pkce_authorization_code_is_consumed_once_over_http() {
    let server = TestServer::start().await;
    let client = server.client();
    let instance = create_instance(&server, &client, "pkce-flow-test").await["instance"].clone();
    let instance_id = instance["id"].as_str().expect("instance id is returned");
    let instance_slug = instance["slug"]
        .as_str()
        .expect("instance slug is returned");
    let now = unix_now();
    let session_token = "session-cookie-for-pkce-test";
    let session_id = "ses_pkce_acceptance";
    seed_record(
        &server,
        "users",
        "usr_pkce_acceptance",
        &json!({
            "id": "usr_pkce_acceptance",
            "instanceId": instance_id,
            "email": "pkce@example.test",
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
        session_id,
        &json!({
            "id": session_id,
            "instanceId": instance_id,
            "userId": "usr_pkce_acceptance",
            "tokenKey": sha256_hex(session_token),
            "status": "active",
            "createdAt": now,
            "expiresAt": now + 3_600,
            "lastActiveAt": now,
        }),
    )
    .await;
    seed_record(
        &server,
        "oauth_clients",
        "cli_pkce_acceptance",
        &json!({
            "id": "cli_pkce_acceptance",
            "instanceId": instance_id,
            "clientId": "public-pkce-client",
            "clientKey": format!("{instance_id}:public-pkce-client"),
            "name": "PKCE acceptance client",
            "redirectUris": ["https://client.example.test/callback"],
            "allowedOrigins": [],
            "tokenEndpointAuthMethod": "none",
            "public": true,
            "createdAt": now,
        }),
    )
    .await;

    let verifier = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
    let challenge = URL_SAFE_NO_PAD.encode(Sha256::digest(verifier.as_bytes()));
    let authorize = client
        .get(format!("{}/{instance_slug}/oauth/authorize", server.origin))
        .query(&[
            ("client_id", "public-pkce-client"),
            ("redirect_uri", "https://client.example.test/callback"),
            ("response_type", "code"),
            ("scope", "openid email"),
            ("state", "csrf-state-value"),
            ("code_challenge", challenge.as_str()),
            ("code_challenge_method", "S256"),
        ])
        .header(header::COOKIE, format!("tsauth_session={session_token}"))
        .send()
        .await
        .expect("PKCE authorization request should reach the service");
    assert_eq!(authorize.status(), StatusCode::SEE_OTHER);
    let location = authorize
        .headers()
        .get(header::LOCATION)
        .and_then(|value| value.to_str().ok())
        .expect("authorization should redirect to the registered client");
    let callback = url::Url::parse(location).expect("redirect should be an absolute URL");
    let code = callback
        .query_pairs()
        .find_map(|(name, value)| (name == "code").then(|| value.into_owned()))
        .expect("successful authorization should return a code");
    assert_eq!(
        callback
            .query_pairs()
            .find_map(|(name, value)| (name == "state").then(|| value.into_owned()))
            .as_deref(),
        Some("csrf-state-value")
    );

    let token_fields = [
        ("grant_type", "authorization_code"),
        ("client_id", "public-pkce-client"),
        ("code", code.as_str()),
        ("redirect_uri", "https://client.example.test/callback"),
        ("code_verifier", verifier),
    ];
    let first_exchange = client
        .post(format!("{}/{instance_slug}/oauth/token", server.origin))
        .form(&token_fields)
        .send()
        .await
        .expect("first token exchange should reach the service");
    assert_eq!(first_exchange.status(), StatusCode::OK);
    let tokens: Value = first_exchange
        .json()
        .await
        .expect("successful token exchange should be JSON");
    assert!(
        tokens["access_token"]
            .as_str()
            .is_some_and(|value| !value.is_empty())
    );
    assert!(
        tokens["id_token"]
            .as_str()
            .is_some_and(|value| !value.is_empty())
    );

    let replay = client
        .post(format!("{}/{instance_slug}/oauth/token", server.origin))
        .form(&token_fields)
        .send()
        .await
        .expect("authorization code replay should reach the service");
    assert_eq!(replay.status(), StatusCode::BAD_REQUEST);
    let body: Value = replay.json().await.expect("replay error should be JSON");
    assert_eq!(body["error"], "invalid_grant");
}

#[tokio::test]
async fn scim_org_directories_isolate_profiles_and_own_only_their_memberships() {
    let server = TestServer::start().await;
    let client = server.client();
    let bootstrap = create_instance(&server, &client, "scim-org-isolation").await;
    let api_key = bootstrap["api_key"]
        .as_str()
        .expect("initial API key is returned");
    let org_a = create_organization(
        &client,
        &server.origin,
        "scim-org-isolation",
        api_key,
        "Directory A Org",
        "directory-a-org",
    )
    .await;
    let org_b = create_organization(
        &client,
        &server.origin,
        "scim-org-isolation",
        api_key,
        "Directory B Org",
        "directory-b-org",
    )
    .await;
    let org_a_id = org_a["id"].as_str().expect("first organization has an id");
    let org_b_id = org_b["id"].as_str().expect("second organization has an id");
    let directory_a1 = create_organization_directory(
        &client,
        &server.origin,
        "scim-org-isolation",
        api_key,
        org_a_id,
    )
    .await;
    let directory_a2 = create_organization_directory(
        &client,
        &server.origin,
        "scim-org-isolation",
        api_key,
        org_a_id,
    )
    .await;
    let directory_b = create_organization_directory(
        &client,
        &server.origin,
        "scim-org-isolation",
        api_key,
        org_b_id,
    )
    .await;
    let directory_a1_id = directory_a1["id"].as_str().expect("directory has an id");
    let directory_a2_id = directory_a2["id"].as_str().expect("directory has an id");
    let directory_b_id = directory_b["id"].as_str().expect("directory has an id");
    let token_a1 = directory_a1["token"]
        .as_str()
        .expect("directory token returned");
    let token_a2 = directory_a2["token"]
        .as_str()
        .expect("directory token returned");
    let token_b = directory_b["token"]
        .as_str()
        .expect("directory token returned");

    let user_a1 = provision_scim_user(
        &client,
        &server.origin,
        directory_a1_id,
        token_a1,
        "shared-person@example.test",
        "Directory",
        "A1",
    )
    .await;
    let user_id = user_a1["id"].as_str().expect("SCIM user has an id");
    assert_eq!(user_a1["userName"], "shared-person@example.test");
    assert_eq!(user_a1["name"]["familyName"], "A1");

    let user_a2 = provision_scim_user(
        &client,
        &server.origin,
        directory_a2_id,
        token_a2,
        "shared-person@example.test",
        "Directory",
        "A2",
    )
    .await;
    assert_eq!(user_a2["id"], user_id);
    assert_eq!(user_a2["name"]["familyName"], "A2");

    let cross_org_adoption = client
        .post(format!("{}/scim/{directory_b_id}/Users", server.origin))
        .bearer_auth(token_b)
        .json(&json!({
            "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
            "userName": "shared-person@example.test",
            "active": true,
        }))
        .send()
        .await
        .expect("cross-organization SCIM adoption should reach the service");
    assert_eq!(cross_org_adoption.status(), StatusCode::CONFLICT);
    let no_b_membership: Option<OrgMembership> = server
        .state
        .store
        .query_one("memberships", "memberKey", &format!("{org_b_id}:{user_id}"))
        .await
        .expect("other organization membership should be queryable");
    assert!(no_b_membership.is_none());

    let manual_membership = client
        .post(format!(
            "{}/v1/scim-org-isolation/organizations/{org_b_id}/memberships",
            server.origin
        ))
        .bearer_auth(api_key)
        .json(&json!({ "user_id": user_id, "role": "member" }))
        .send()
        .await
        .expect("manual organization membership should reach the service");
    assert_eq!(manual_membership.status(), StatusCode::OK);
    let manual_membership: Value = manual_membership
        .json()
        .await
        .expect("manual membership response should be JSON");

    let user_b = provision_scim_user(
        &client,
        &server.origin,
        directory_b_id,
        token_b,
        "shared-person@example.test",
        "Directory",
        "B",
    )
    .await;
    assert_eq!(user_b["id"], user_id);
    assert_eq!(user_b["name"]["familyName"], "B");

    let renamed_a1 = client
        .patch(format!(
            "{}/scim/{directory_a1_id}/Users/{user_id}",
            server.origin
        ))
        .bearer_auth(token_a1)
        .json(&json!({
            "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
            "operations": [{
                "op": "replace",
                "path": "userName",
                "value": "a1-alias@example.test",
            }],
        }))
        .send()
        .await
        .expect("directory-local userName update should reach the service");
    assert_eq!(renamed_a1.status(), StatusCode::OK);
    let renamed_a1: Value = renamed_a1
        .json()
        .await
        .expect("updated SCIM user should be JSON");
    assert_eq!(renamed_a1["userName"], "a1-alias@example.test");
    let unchanged_a2 =
        get_scim_user(&client, &server.origin, directory_a2_id, token_a2, user_id).await;
    let unchanged_b =
        get_scim_user(&client, &server.origin, directory_b_id, token_b, user_id).await;
    assert_eq!(unchanged_a2["userName"], "shared-person@example.test");
    assert_eq!(unchanged_a2["name"]["familyName"], "A2");
    assert_eq!(unchanged_b["userName"], "shared-person@example.test");
    assert_eq!(unchanged_b["name"]["familyName"], "B");

    let instance_id = bootstrap["instance"]["id"]
        .as_str()
        .expect("instance id is returned");
    let core_user = server
        .state
        .store
        .get::<Value>("users", user_id)
        .await
        .expect("shared identity should be readable")
        .expect("shared identity should remain stored");
    assert_eq!(core_user["instanceId"], instance_id);
    assert_eq!(core_user["email"], "shared-person@example.test");

    let membership_a_id = format!("{org_a_id}:{user_id}");
    let membership_a: Option<OrgMembership> = server
        .state
        .store
        .query_one("memberships", "memberKey", &membership_a_id)
        .await
        .expect("SCIM-owned membership should be queryable");
    let membership_a = membership_a.expect("one same-org SCIM membership should exist");
    let membership_claim_id = format!("{org_a_id}:{user_id}");
    let membership_claim_before = server
        .state
        .store
        .get::<Value>("scim_membership_claims", &membership_claim_id)
        .await
        .expect("SCIM membership claim should be queryable")
        .expect("SCIM provisioning should retain the membership ID");
    assert_eq!(membership_claim_before["membershipId"], membership_a.id);

    let deleted_a1 = client
        .delete(format!(
            "{}/v1/scim-org-isolation/directories/{directory_a1_id}",
            server.origin
        ))
        .bearer_auth(api_key)
        .send()
        .await
        .expect("first directory deletion should reach the service");
    assert_eq!(deleted_a1.status(), StatusCode::NO_CONTENT);
    let membership_after_first_directory: Option<OrgMembership> = server
        .state
        .store
        .query_one("memberships", "memberKey", &membership_a_id)
        .await
        .expect("membership should remain queryable");
    assert_eq!(
        membership_after_first_directory
            .as_ref()
            .map(|membership| membership.id.as_str()),
        Some(membership_a.id.as_str())
    );
    let claim_after_first_directory = server
        .state
        .store
        .get::<Value>("scim_membership_claims", &membership_claim_id)
        .await
        .expect("membership claim should remain queryable")
        .expect("membership claim should remain stored");
    assert_eq!(claim_after_first_directory, membership_claim_before);

    let deleted_a2 = client
        .delete(format!(
            "{}/v1/scim-org-isolation/directories/{directory_a2_id}",
            server.origin
        ))
        .bearer_auth(api_key)
        .send()
        .await
        .expect("last same-organization directory deletion should reach the service");
    assert_eq!(deleted_a2.status(), StatusCode::NO_CONTENT);
    let membership_after_last_directory: Option<OrgMembership> = server
        .state
        .store
        .query_one("memberships", "memberKey", &membership_a_id)
        .await
        .expect("membership should remain queryable");
    assert!(membership_after_last_directory.is_none());
    let claim_after_last_directory = server
        .state
        .store
        .get::<Value>("scim_membership_claims", &membership_claim_id)
        .await
        .expect("membership provenance should remain queryable")
        .expect("membership provenance should outlive the membership row");
    assert_eq!(claim_after_last_directory, membership_claim_before);

    let deleted_b = client
        .delete(format!(
            "{}/v1/scim-org-isolation/directories/{directory_b_id}",
            server.origin
        ))
        .bearer_auth(api_key)
        .send()
        .await
        .expect("directory with preexisting manual membership should delete");
    assert_eq!(deleted_b.status(), StatusCode::NO_CONTENT);
    let manual_membership_after_delete: Option<OrgMembership> = server
        .state
        .store
        .get(
            "memberships",
            manual_membership["id"]
                .as_str()
                .expect("manual membership has an id"),
        )
        .await
        .expect("preexisting manual membership should remain readable");
    assert!(manual_membership_after_delete.is_some());
}

#[tokio::test]
async fn scim_deprovision_tombstones_hide_memberships_and_keep_cleanup_exclusive() {
    let server = TestServer::start().await;
    let client = server.client();
    let bootstrap = create_instance(&server, &client, "scim-cleanup-owner").await;
    let api_key = bootstrap["api_key"]
        .as_str()
        .expect("initial API key is returned");
    let instance_id = bootstrap["instance"]["id"]
        .as_str()
        .expect("instance id is returned");
    let organization = create_organization(
        &client,
        &server.origin,
        "scim-cleanup-owner",
        api_key,
        "SCIM Cleanup Owner Org",
        "scim-cleanup-owner-org",
    )
    .await;
    let org_id = organization["id"].as_str().expect("organization has an id");
    let directory = create_organization_directory(
        &client,
        &server.origin,
        "scim-cleanup-owner",
        api_key,
        org_id,
    )
    .await;
    let directory_id = directory["id"].as_str().expect("directory has an id");
    let token = directory["token"]
        .as_str()
        .expect("directory token returned");
    let fixture = server
        ._storage_fixture
        .as_ref()
        .expect("contract fixture is available");

    let preflight_user = provision_scim_user(
        &client,
        &server.origin,
        directory_id,
        token,
        "preflight-retry@example.test",
        "Preflight",
        "Retry",
    )
    .await;
    let preflight_user_id = preflight_user["id"].as_str().expect("SCIM user has an id");
    fixture.fail_next_get("scim_membership_claims");
    let failed_preflight = client
        .delete(format!(
            "{}/scim/{directory_id}/Users/{preflight_user_id}",
            server.origin
        ))
        .bearer_auth(token)
        .send()
        .await
        .expect("preflight deprovision request should reach the service");
    assert_eq!(failed_preflight.status(), StatusCode::INTERNAL_SERVER_ERROR);
    let preflight_link_id = format!("{directory_id}:{preflight_user_id}");
    let preflight_link = server
        .state
        .store
        .get::<Value>("scim_user_links", &preflight_link_id)
        .await
        .expect("preflight tombstone should be readable")
        .expect("preflight failure should retain its link");
    assert_eq!(preflight_link["deleting"], true);
    assert_eq!(preflight_link["cleanupOwner"], Value::Null);
    assert_eq!(preflight_link["cleanupComplete"], false);
    let preflight_membership: Option<OrgMembership> = server
        .state
        .store
        .query_one(
            "memberships",
            "memberKey",
            &format!("{org_id}:{preflight_user_id}"),
        )
        .await
        .expect("preflight membership should be readable");
    assert!(preflight_membership.is_some());

    let preflight_retry = client
        .delete(format!(
            "{}/scim/{directory_id}/Users/{preflight_user_id}",
            server.origin
        ))
        .bearer_auth(token)
        .send()
        .await
        .expect("retry after read-only preflight failure should reach the service");
    assert_eq!(preflight_retry.status(), StatusCode::NO_CONTENT);
    let completed_preflight_link = server
        .state
        .store
        .get::<Value>("scim_user_links", &preflight_link_id)
        .await
        .expect("completed preflight link should be readable")
        .expect("completed link is retained as a version tombstone");
    assert_eq!(completed_preflight_link["cleanupComplete"], true);
    assert_eq!(completed_preflight_link["deleting"], false);
    assert_eq!(completed_preflight_link["active"], false);
    assert_eq!(completed_preflight_link["cleanupOwner"], Value::Null);
    let preflight_membership: Option<OrgMembership> = server
        .state
        .store
        .query_one(
            "memberships",
            "memberKey",
            &format!("{org_id}:{preflight_user_id}"),
        )
        .await
        .expect("preflight membership should be readable after cleanup");
    assert!(preflight_membership.is_none());

    let ambiguous_user = provision_scim_user(
        &client,
        &server.origin,
        directory_id,
        token,
        "ambiguous-child@example.test",
        "Ambiguous",
        "Child",
    )
    .await;
    let ambiguous_user_id = ambiguous_user["id"].as_str().expect("SCIM user has an id");
    let ambiguous_link_id = format!("{directory_id}:{ambiguous_user_id}");
    fixture.fail_next_delete_before_commit("memberships");
    let failed_child_cleanup = client
        .delete(format!(
            "{}/scim/{directory_id}/Users/{ambiguous_user_id}",
            server.origin
        ))
        .bearer_auth(token)
        .send()
        .await
        .expect("child cleanup failure request should reach the service");
    assert_eq!(
        failed_child_cleanup.status(),
        StatusCode::INTERNAL_SERVER_ERROR
    );
    let ambiguous_link = server
        .state
        .store
        .get::<Value>("scim_user_links", &ambiguous_link_id)
        .await
        .expect("ambiguous cleanup tombstone should be readable")
        .expect("ambiguous cleanup must retain its link");
    assert_eq!(ambiguous_link["deleting"], true);
    assert_eq!(ambiguous_link["cleanupComplete"], false);
    assert!(
        ambiguous_link["cleanupOwner"]
            .as_str()
            .is_some_and(|v| !v.is_empty())
    );
    let ambiguous_membership: Option<OrgMembership> = server
        .state
        .store
        .query_one(
            "memberships",
            "memberKey",
            &format!("{org_id}:{ambiguous_user_id}"),
        )
        .await
        .expect("membership should remain readable after an ambiguous child failure");
    assert!(ambiguous_membership.is_some());

    let hidden_get = client
        .get(format!(
            "{}/scim/{directory_id}/Users/{ambiguous_user_id}",
            server.origin
        ))
        .bearer_auth(token)
        .send()
        .await
        .expect("SCIM read after tombstoning should reach the service");
    assert_eq!(hidden_get.status(), StatusCode::NOT_FOUND);
    let listed = client
        .get(format!("{}/scim/{directory_id}/Users", server.origin))
        .bearer_auth(token)
        .send()
        .await
        .expect("SCIM list after tombstoning should reach the service");
    assert_eq!(listed.status(), StatusCode::OK);
    let listed: Value = listed.json().await.expect("SCIM user list should be JSON");
    assert_eq!(listed["totalResults"], 0);

    let projected_memberships = client
        .get(format!(
            "{}/v1/scim-cleanup-owner/users/{ambiguous_user_id}/organizations",
            server.origin
        ))
        .bearer_auth(api_key)
        .send()
        .await
        .expect("organization projection should reach the service");
    assert_eq!(projected_memberships.status(), StatusCode::OK);
    let projected_memberships: Value = projected_memberships
        .json()
        .await
        .expect("organization projection should be JSON");
    assert_eq!(projected_memberships, json!([]));

    let template = client
        .post(format!(
            "{}/v1/scim-cleanup-owner/jwt-templates",
            server.origin
        ))
        .bearer_auth(api_key)
        .json(&json!({
            "name": "membership-projection",
            "claims": {
                "org_id": "{{ org.id }}",
                "org_role": "{{ org.role }}",
            },
            "lifetime_seconds": 300,
        }))
        .send()
        .await
        .expect("management API key should create the membership template");
    assert_eq!(template.status(), StatusCode::OK);
    let minted = mint_template_token(
        &client,
        &server.origin,
        "scim-cleanup-owner",
        api_key,
        "membership-projection",
        ambiguous_user_id,
    )
    .await;
    assert_eq!(minted.status(), StatusCode::OK);
    let minted: Value = minted
        .json()
        .await
        .expect("membership template JWT should be JSON");
    let claims = auth_security::jwt::decode_unverified(
        minted["jwt"].as_str().expect("template JWT is returned"),
    )
    .expect("template JWT payload should decode");
    assert_eq!(claims["org_id"], "");
    assert_eq!(claims["org_role"], "");

    let blocked_retry = client
        .delete(format!(
            "{}/scim/{directory_id}/Users/{ambiguous_user_id}",
            server.origin
        ))
        .bearer_auth(token)
        .send()
        .await
        .expect("retry during retained cleanup ownership should reach the service");
    assert_eq!(blocked_retry.status(), StatusCode::CONFLICT);
    let blocked_retry: Value = blocked_retry
        .json()
        .await
        .expect("cleanup ownership conflict should be SCIM JSON");
    assert_eq!(blocked_retry["status"], "409");
    let blocked_post = client
        .post(format!("{}/scim/{directory_id}/Users", server.origin))
        .bearer_auth(token)
        .json(&json!({
            "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
            "userName": "ambiguous-child@example.test",
            "active": true,
        }))
        .send()
        .await
        .expect("fresh link creation during retained cleanup should reach the service");
    assert_eq!(blocked_post.status(), StatusCode::CONFLICT);
    let ambiguous_membership: Option<OrgMembership> = server
        .state
        .store
        .query_one(
            "memberships",
            "memberKey",
            &format!("{org_id}:{ambiguous_user_id}"),
        )
        .await
        .expect("ambiguous membership should remain readable");
    assert!(ambiguous_membership.is_some());

    // Simulate the documented quiescent repair: the test has joined the failed
    // request and confirmed no child mutation committed, so the stale owner can
    // be conditionally cleared before retrying.
    let (mut repair_link, repair_version) = server
        .state
        .store
        .get_versioned::<Value>("scim_user_links", &ambiguous_link_id)
        .await
        .expect("cleanup tombstone version should be readable")
        .expect("cleanup tombstone should remain present");
    assert!(repair_link["cleanupOwner"].as_str().is_some());
    repair_link["cleanupOwner"] = Value::Null;
    assert!(
        server
            .state
            .store
            .compare_and_put(
                "scim_user_links",
                &ambiguous_link_id,
                &repair_link,
                &repair_version,
            )
            .await
            .expect("quiescent owner repair should use a conditional write")
    );
    let repaired_retry = client
        .delete(format!(
            "{}/scim/{directory_id}/Users/{ambiguous_user_id}",
            server.origin
        ))
        .bearer_auth(token)
        .send()
        .await
        .expect("delete after quiescent owner repair should reach the service");
    assert_eq!(repaired_retry.status(), StatusCode::NO_CONTENT);

    let exclusive_user = provision_scim_user(
        &client,
        &server.origin,
        directory_id,
        token,
        "exclusive-cleanup@example.test",
        "Exclusive",
        "Cleanup",
    )
    .await;
    let exclusive_user_id = exclusive_user["id"].as_str().expect("SCIM user has an id");
    let exclusive_link_id = format!("{directory_id}:{exclusive_user_id}");
    let mut child_delete_pause = fixture.pause_next_delete_before_commit("memberships");
    let request_client = client.clone();
    let request_origin = server.origin.clone();
    let request_directory = directory_id.to_owned();
    let request_token = token.to_owned();
    let request_user = exclusive_user_id.to_owned();
    let owner_request = tokio::spawn(async move {
        request_client
            .delete(format!(
                "{request_origin}/scim/{request_directory}/Users/{request_user}"
            ))
            .bearer_auth(request_token)
            .send()
            .await
    });
    child_delete_pause.wait_until_paused().await;
    let exclusive_link = server
        .state
        .store
        .get::<Value>("scim_user_links", &exclusive_link_id)
        .await
        .expect("in-progress link should be readable")
        .expect("in-progress link should remain stored");
    assert_eq!(exclusive_link["deleting"], true);
    assert!(exclusive_link["cleanupOwner"].as_str().is_some());

    let competing_delete = client
        .delete(format!(
            "{}/scim/{directory_id}/Users/{exclusive_user_id}",
            server.origin
        ))
        .bearer_auth(token)
        .send()
        .await
        .expect("competing cleanup should reach the service");
    assert_eq!(competing_delete.status(), StatusCode::CONFLICT);
    let exclusive_membership: Option<OrgMembership> = server
        .state
        .store
        .query_one(
            "memberships",
            "memberKey",
            &format!("{org_id}:{exclusive_user_id}"),
        )
        .await
        .expect("exclusive membership should remain readable while paused");
    assert!(exclusive_membership.is_some());

    fixture.fail_next_create_only_before_commit("audit_events");
    child_delete_pause.resume();
    let owner_result = owner_request
        .await
        .expect("cleanup owner request task should complete")
        .expect("cleanup owner request should reach the service");
    assert_eq!(owner_result.status(), StatusCode::INTERNAL_SERVER_ERROR);
    let terminal_delete_retry = client
        .delete(format!(
            "{}/scim/{directory_id}/Users/{exclusive_user_id}",
            server.origin
        ))
        .bearer_auth(token)
        .send()
        .await
        .expect("terminal deprovision audit retry should reach the service");
    assert_eq!(terminal_delete_retry.status(), StatusCode::NO_CONTENT);
    let exclusive_link = server
        .state
        .store
        .get::<Value>("scim_user_links", &exclusive_link_id)
        .await
        .expect("terminal link should be readable")
        .expect("terminal link is retained for version fencing");
    assert_eq!(exclusive_link["cleanupComplete"], true);
    assert_eq!(exclusive_link["cleanupOwner"], Value::Null);
    let exclusive_membership: Option<OrgMembership> = server
        .state
        .store
        .query_one(
            "memberships",
            "memberKey",
            &format!("{org_id}:{exclusive_user_id}"),
        )
        .await
        .expect("exclusive membership should be readable after cleanup");
    assert!(exclusive_membership.is_none());
    let audit_predicate = auth_store::Predicate::equal("instanceId", instance_id)
        .expect("audit instance predicate should be valid");
    let audit_events: Vec<AuditEvent> = server
        .state
        .store
        .query_all("audit_events", &[audit_predicate], 100)
        .await
        .expect("deprovision audit rows should be readable");
    let deprovision_audits = audit_events
        .iter()
        .filter(|event| {
            event.r#type == "directory.user_deprovisioned"
                && event.data.get("user_id").and_then(Value::as_str) == Some(exclusive_user_id)
        })
        .count();
    assert_eq!(deprovision_audits, 1);
}

#[tokio::test]
async fn scim_membership_claim_survives_manual_replacement_and_reactivation() {
    let server = TestServer::start().await;
    let client = server.client();
    let bootstrap = create_instance(&server, &client, "scim-membership-provenance").await;
    let api_key = bootstrap["api_key"]
        .as_str()
        .expect("initial API key is returned");
    let organization = create_organization(
        &client,
        &server.origin,
        "scim-membership-provenance",
        api_key,
        "SCIM Membership Provenance Org",
        "scim-membership-provenance-org",
    )
    .await;
    let org_id = organization["id"].as_str().expect("organization has an id");
    let directory = create_organization_directory(
        &client,
        &server.origin,
        "scim-membership-provenance",
        api_key,
        org_id,
    )
    .await;
    let directory_id = directory["id"].as_str().expect("directory has an id");
    let token = directory["token"]
        .as_str()
        .expect("directory token returned");
    let created = provision_scim_user(
        &client,
        &server.origin,
        directory_id,
        token,
        "provenance-cycle@example.test",
        "Provenance",
        "Cycle",
    )
    .await;
    let user_id = created["id"].as_str().expect("SCIM user has an id");
    let claim_id = format!("{org_id}:{user_id}");
    let claim_before = server
        .state
        .store
        .get::<Value>("scim_membership_claims", &claim_id)
        .await
        .expect("SCIM membership claim should be readable")
        .expect("SCIM provisioning should persist a membership claim");
    let link_id = format!("{directory_id}:{user_id}");
    let original_link = server
        .state
        .store
        .get::<Value>("scim_user_links", &link_id)
        .await
        .expect("initial directory link should be readable")
        .expect("initial directory link should exist");
    let original_incarnation = original_link["incarnationId"].clone();
    let scim_membership_id = claim_before["membershipId"]
        .as_str()
        .expect("SCIM claim owns a membership id")
        .to_owned();

    let first_delete = client
        .delete(format!(
            "{}/scim/{directory_id}/Users/{user_id}",
            server.origin
        ))
        .bearer_auth(token)
        .send()
        .await
        .expect("SCIM user deprovision should reach the service");
    assert_eq!(first_delete.status(), StatusCode::NO_CONTENT);
    let old_membership: Option<OrgMembership> = server
        .state
        .store
        .get("memberships", &scim_membership_id)
        .await
        .expect("SCIM membership should be readable after deprovision");
    assert!(old_membership.is_none());
    let retained_claim = server
        .state
        .store
        .get::<Value>("scim_membership_claims", &claim_id)
        .await
        .expect("membership claim should remain readable")
        .expect("deprovisioning retains membership provenance");
    assert_eq!(retained_claim, claim_before);

    let manual = client
        .post(format!(
            "{}/v1/scim-membership-provenance/organizations/{org_id}/memberships",
            server.origin
        ))
        .bearer_auth(api_key)
        .json(&json!({ "user_id": user_id, "role": "admin" }))
        .send()
        .await
        .expect("manual membership replacement should reach the service");
    assert_eq!(manual.status(), StatusCode::OK);
    let manual: Value = manual
        .json()
        .await
        .expect("manual membership response should be JSON");
    let manual_membership_id = manual["id"]
        .as_str()
        .expect("manual membership has an id")
        .to_owned();
    assert_ne!(manual_membership_id, scim_membership_id);

    let template = client
        .post(format!(
            "{}/v1/scim-membership-provenance/jwt-templates",
            server.origin
        ))
        .bearer_auth(api_key)
        .json(&json!({
            "name": "manual-membership-context",
            "claims": {
                "org_id": "{{ org.id }}",
                "org_role": "{{ org.role }}",
            },
            "lifetime_seconds": 300,
        }))
        .send()
        .await
        .expect("management API key should create the organization template");
    assert_eq!(template.status(), StatusCode::OK);

    let reactivated = provision_scim_user(
        &client,
        &server.origin,
        directory_id,
        token,
        "provenance-cycle@example.test",
        "Provenance",
        "Reactivated",
    )
    .await;
    assert_eq!(reactivated["id"], user_id);
    let membership_after_reactivation: Option<OrgMembership> = server
        .state
        .store
        .get("memberships", &manual_membership_id)
        .await
        .expect("manual membership should remain readable");
    assert!(membership_after_reactivation.is_some());
    let new_link = server
        .state
        .store
        .get::<Value>("scim_user_links", &link_id)
        .await
        .expect("reactivated directory link should be readable")
        .expect("reactivation should replace the completed link");
    assert_ne!(new_link["incarnationId"], original_incarnation);
    assert_ne!(new_link["incarnationId"], Value::Null);
    let retained_claim = server
        .state
        .store
        .get::<Value>("scim_membership_claims", &claim_id)
        .await
        .expect("membership claim should remain readable after reactivation")
        .expect("membership claim should remain stored");
    assert_eq!(retained_claim, claim_before);

    let reactivated_delete = client
        .delete(format!(
            "{}/scim/{directory_id}/Users/{user_id}",
            server.origin
        ))
        .bearer_auth(token)
        .send()
        .await
        .expect("reactivated SCIM user should deprovision again");
    assert_eq!(reactivated_delete.status(), StatusCode::NO_CONTENT);
    let preserved_manual: Option<OrgMembership> = server
        .state
        .store
        .get("memberships", &manual_membership_id)
        .await
        .expect("manual membership should remain readable after SCIM cleanup");
    assert!(preserved_manual.is_some());
    let retained_claim_after_delete = server
        .state
        .store
        .get::<Value>("scim_membership_claims", &claim_id)
        .await
        .expect("membership provenance should be readable after repeated cleanup")
        .expect("membership provenance should remain stored");
    assert_eq!(retained_claim_after_delete, claim_before);

    let projected = client
        .get(format!(
            "{}/v1/scim-membership-provenance/users/{user_id}/organizations",
            server.origin
        ))
        .bearer_auth(api_key)
        .send()
        .await
        .expect("manual membership projection should reach the service");
    assert_eq!(projected.status(), StatusCode::OK);
    let projected: Value = projected
        .json()
        .await
        .expect("manual membership projection should be JSON");
    assert_eq!(projected.as_array().map(Vec::len), Some(1));
    assert_eq!(projected[0]["id"], manual_membership_id);
    assert_eq!(projected[0]["role"], "admin");

    let minted = mint_template_token(
        &client,
        &server.origin,
        "scim-membership-provenance",
        api_key,
        "manual-membership-context",
        user_id,
    )
    .await;
    assert_eq!(minted.status(), StatusCode::OK);
    let minted: Value = minted
        .json()
        .await
        .expect("manual membership token response should be JSON");
    let claims = auth_security::jwt::decode_unverified(
        minted["jwt"].as_str().expect("template JWT is returned"),
    )
    .expect("manual membership token payload should decode");
    assert_eq!(claims["org_id"], org_id);
    assert_eq!(claims["org_role"], "admin");
}

#[tokio::test]
async fn scim_unchanged_profile_update_racing_a_rename_releases_the_old_name() {
    let server = TestServer::start().await;
    let client = server.client();
    let bootstrap = create_instance(&server, &client, "scim-name-race").await;
    let api_key = bootstrap["api_key"]
        .as_str()
        .expect("initial API key is returned");
    let organization = create_organization(
        &client,
        &server.origin,
        "scim-name-race",
        api_key,
        "SCIM Name Race Org",
        "scim-name-race-org",
    )
    .await;
    let directory = create_organization_directory(
        &client,
        &server.origin,
        "scim-name-race",
        api_key,
        organization["id"].as_str().expect("organization has an id"),
    )
    .await;
    let directory_id = directory["id"].as_str().expect("directory has an id");
    let token = directory["token"]
        .as_str()
        .expect("directory token returned");
    let original = provision_scim_user(
        &client,
        &server.origin,
        directory_id,
        token,
        "name-x@example.test",
        "Name",
        "X",
    )
    .await;
    let original_id = original["id"].as_str().expect("SCIM user has an id");

    let fixture = server
        ._storage_fixture
        .as_ref()
        .expect("contract fixture is available");
    let mut rename_pause = fixture.pause_next_compare_and_put("scim_user_links");
    let mut unchanged_pause = fixture.pause_next_compare_and_put("scim_user_links");
    let rename_client = client.clone();
    let rename_origin = server.origin.clone();
    let rename_directory_id = directory_id.to_owned();
    let rename_token = token.to_owned();
    let rename_user_id = original_id.to_owned();
    let rename = tokio::spawn(async move {
        rename_client
            .patch(format!(
                "{rename_origin}/scim/{rename_directory_id}/Users/{rename_user_id}"
            ))
            .bearer_auth(rename_token)
            .json(&json!({
                "operations": [{
                    "op": "replace",
                    "path": "userName",
                    "value": "name-y@example.test",
                }],
            }))
            .send()
            .await
    });
    timeout(Duration::from_secs(3), rename_pause.wait_until_paused())
        .await
        .expect("rename compare-and-put should reach the controlled pause");

    let unchanged_client = client.clone();
    let unchanged_origin = server.origin.clone();
    let unchanged_directory_id = directory_id.to_owned();
    let unchanged_token = token.to_owned();
    let unchanged_user_id = original_id.to_owned();
    let unchanged_name_update = tokio::spawn(async move {
        unchanged_client
            .patch(format!(
                "{unchanged_origin}/scim/{unchanged_directory_id}/Users/{unchanged_user_id}"
            ))
            .bearer_auth(unchanged_token)
            .json(&json!({
                "operations": [{
                    "op": "replace",
                    "path": "active",
                    "value": true,
                }],
            }))
            .send()
            .await
    });
    timeout(Duration::from_secs(3), unchanged_pause.wait_until_paused())
        .await
        .expect("unchanged-name compare-and-put should also pause");

    rename_pause.resume();

    let rename = timeout(Duration::from_secs(5), rename)
        .await
        .expect("renamed user request should finish")
        .expect("rename request task should complete")
        .expect("rename request should reach the service");
    assert_eq!(rename.status(), StatusCode::OK);
    let renamed: Value = rename.json().await.expect("renamed user response is JSON");
    assert_eq!(renamed["userName"], "name-y@example.test");

    unchanged_pause.resume();
    let unchanged_name_update = timeout(Duration::from_secs(5), unchanged_name_update)
        .await
        .expect("overlapping active update should finish")
        .expect("active update task should complete")
        .expect("active update should reach the service");
    assert_eq!(unchanged_name_update.status(), StatusCode::OK);
    let unchanged_name_update: Value = unchanged_name_update
        .json()
        .await
        .expect("updated user response should be JSON");
    assert_eq!(unchanged_name_update["userName"], "name-y@example.test");

    let second = provision_scim_user(
        &client,
        &server.origin,
        directory_id,
        token,
        "second-person@example.test",
        "Second",
        "Person",
    )
    .await;
    let second_id = second["id"].as_str().expect("second SCIM user has an id");
    let claimant = client
        .put(format!(
            "{}/scim/{directory_id}/Users/{second_id}",
            server.origin
        ))
        .bearer_auth(token)
        .json(&json!({
            "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
            "userName": "name-x@example.test",
            "active": true,
            "name": { "givenName": "Second", "familyName": "Person" },
        }))
        .send()
        .await
        .expect("second directory link should be able to claim the released name");
    assert_eq!(claimant.status(), StatusCode::OK);
    let claimant: Value = claimant
        .json()
        .await
        .expect("second user response should be JSON");
    assert_eq!(claimant["userName"], "name-x@example.test");
}

#[tokio::test]
async fn scim_cas_conflict_retains_a_name_claim_reservation() {
    let server = TestServer::start().await;
    let client = server.client();
    let bootstrap = create_instance(&server, &client, "scim-name-conflict").await;
    let api_key = bootstrap["api_key"]
        .as_str()
        .expect("initial API key is returned");
    let organization = create_organization(
        &client,
        &server.origin,
        "scim-name-conflict",
        api_key,
        "SCIM Name Conflict Org",
        "scim-name-conflict-org",
    )
    .await;
    let directory = create_organization_directory(
        &client,
        &server.origin,
        "scim-name-conflict",
        api_key,
        organization["id"].as_str().expect("organization has an id"),
    )
    .await;
    let directory_id = directory["id"].as_str().expect("directory has an id");
    let token = directory["token"]
        .as_str()
        .expect("directory token returned");
    let original = provision_scim_user(
        &client,
        &server.origin,
        directory_id,
        token,
        "conflict-x@example.test",
        "Conflict",
        "X",
    )
    .await;
    let original_id = original["id"].as_str().expect("SCIM user has an id");
    server
        ._storage_fixture
        .as_ref()
        .expect("contract fixture is available")
        .conflict_next_compare_and_puts("scim_user_links", 8);

    let failed_rename = client
        .patch(format!(
            "{}/scim/{directory_id}/Users/{original_id}",
            server.origin
        ))
        .bearer_auth(token)
        .json(&json!({
            "operations": [{
                "op": "replace",
                "path": "userName",
                "value": "conflict-y@example.test",
            }],
        }))
        .send()
        .await
        .expect("conflicted rename should reach the service");
    assert_eq!(failed_rename.status(), StatusCode::INTERNAL_SERVER_ERROR);
    let unchanged = get_scim_user(&client, &server.origin, directory_id, token, original_id).await;
    assert_eq!(unchanged["userName"], "conflict-x@example.test");

    let claim_id = auth_store::unique_claim_id(
        "scim-user-name",
        &format!("{directory_id}:conflict-y@example.test"),
    );
    let retained_claim = server
        .state
        .store
        .get::<Value>("uniques", &claim_id)
        .await
        .expect("name reservation should be readable")
        .expect("losing rename should retain its reservation");
    assert_eq!(retained_claim["ownerType"], "scim-user-name");
    assert_eq!(
        retained_claim["ownerId"],
        format!("{directory_id}:{original_id}")
    );
    assert_eq!(retained_claim["released"], false);

    let second = provision_scim_user(
        &client,
        &server.origin,
        directory_id,
        token,
        "conflict-second@example.test",
        "Conflict",
        "Second",
    )
    .await;
    let second_id = second["id"].as_str().expect("second SCIM user has an id");
    let claimant = client
        .put(format!(
            "{}/scim/{directory_id}/Users/{second_id}",
            server.origin
        ))
        .bearer_auth(token)
        .json(&json!({
            "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
            "userName": "conflict-y@example.test",
            "active": true,
            "name": { "givenName": "Conflict", "familyName": "Second" },
        }))
        .send()
        .await
        .expect("second SCIM link should be able to claim the unused name");
    assert_eq!(claimant.status(), StatusCode::CONFLICT);
    let claimant: Value = claimant
        .json()
        .await
        .expect("claim conflict response should be JSON");
    assert_eq!(claimant["scimType"], "uniqueness");
}

#[tokio::test]
async fn scim_link_incarnation_and_delayed_name_release_are_generation_fenced() {
    let server = TestServer::start().await;
    let client = server.client();
    let bootstrap = create_instance(&server, &client, "scim-incarnation-generation").await;
    let api_key = bootstrap["api_key"]
        .as_str()
        .expect("initial API key is returned");
    let organization = create_organization(
        &client,
        &server.origin,
        "scim-incarnation-generation",
        api_key,
        "SCIM Incarnation Generation Org",
        "scim-incarnation-generation-org",
    )
    .await;
    let org_id = organization["id"].as_str().expect("organization has an id");
    let directory = create_organization_directory(
        &client,
        &server.origin,
        "scim-incarnation-generation",
        api_key,
        org_id,
    )
    .await;
    let directory_id = directory["id"].as_str().expect("directory has an id");
    let token = directory["token"]
        .as_str()
        .expect("directory token returned");
    let original = provision_scim_user(
        &client,
        &server.origin,
        directory_id,
        token,
        "generation-a@example.test",
        "Generation",
        "Original",
    )
    .await;
    let original_id = original["id"].as_str().expect("SCIM user has an id");
    let link_id = format!("{directory_id}:{original_id}");
    let original_link = server
        .state
        .store
        .get::<Value>("scim_user_links", &link_id)
        .await
        .expect("initial SCIM link should be readable")
        .expect("initial SCIM link should exist");
    let original_incarnation = original_link["incarnationId"].clone();
    let fixture = server
        ._storage_fixture
        .as_ref()
        .expect("contract fixture is available");

    let mut stale_link_cas = fixture.pause_next_compare_and_put("scim_user_links");
    let request_client = client.clone();
    let request_origin = server.origin.clone();
    let request_directory = directory_id.to_owned();
    let request_token = token.to_owned();
    let request_user = original_id.to_owned();
    let stale_writer = tokio::spawn(async move {
        patch_scim_user_name(
            &request_client,
            &request_origin,
            &request_directory,
            &request_token,
            &request_user,
            "generation-b@example.test",
        )
        .await
    });
    stale_link_cas.wait_until_paused().await;

    let deleted = client
        .delete(format!(
            "{}/scim/{directory_id}/Users/{original_id}",
            server.origin
        ))
        .bearer_auth(token)
        .send()
        .await
        .expect("deprovision should win while the prior link CAS is paused");
    assert_eq!(deleted.status(), StatusCode::NO_CONTENT);
    let manual_membership = client
        .post(format!(
            "{}/v1/scim-incarnation-generation/organizations/{org_id}/memberships",
            server.origin
        ))
        .bearer_auth(api_key)
        .json(&json!({ "user_id": original_id, "role": "member" }))
        .send()
        .await
        .expect("same-organization membership should allow a fresh link");
    assert_eq!(manual_membership.status(), StatusCode::OK);
    let reactivated = provision_scim_user(
        &client,
        &server.origin,
        directory_id,
        token,
        "generation-a@example.test",
        "Generation",
        "Recreated",
    )
    .await;
    assert_eq!(reactivated["id"], original_id);
    let new_link = server
        .state
        .store
        .get::<Value>("scim_user_links", &link_id)
        .await
        .expect("new SCIM incarnation should be readable")
        .expect("fresh POST should replace the terminal link");
    assert_ne!(new_link["incarnationId"], original_incarnation);
    assert_eq!(new_link["cleanupComplete"], false);
    assert_eq!(new_link["profileUserName"], "generation-a@example.test");

    stale_link_cas.resume();
    let stale_result = stale_writer
        .await
        .expect("stale profile writer task should complete");
    assert_eq!(stale_result.status(), StatusCode::CONFLICT);
    let current = get_scim_user(&client, &server.origin, directory_id, token, original_id).await;
    assert_eq!(current["userName"], "generation-a@example.test");
    let abandoned_name_claim = server
        .state
        .store
        .get::<Value>(
            "uniques",
            &auth_store::unique_claim_id(
                "scim-user-name",
                &format!("{directory_id}:generation-b@example.test"),
            ),
        )
        .await
        .expect("losing writer name claim should be readable")
        .expect("losing link writer should retain its reserved name");
    assert_ne!(
        abandoned_name_claim
            .get("released")
            .and_then(Value::as_bool),
        Some(true),
        "a claim is reserved unless its released field is explicitly true"
    );
    assert_eq!(abandoned_name_claim["ownerType"], "scim-user-name");
    assert_eq!(abandoned_name_claim["ownerId"], link_id);

    let mut delayed_old_name_release = fixture.pause_next_compare_and_put("uniques");
    let request_client = client.clone();
    let request_origin = server.origin.clone();
    let request_directory = directory_id.to_owned();
    let request_token = token.to_owned();
    let request_user = original_id.to_owned();
    let first_rename = tokio::spawn(async move {
        patch_scim_user_name(
            &request_client,
            &request_origin,
            &request_directory,
            &request_token,
            &request_user,
            "generation-c@example.test",
        )
        .await
    });
    delayed_old_name_release.wait_until_paused().await;

    let second_rename = patch_scim_user_name(
        &client,
        &server.origin,
        directory_id,
        token,
        original_id,
        "generation-a@example.test",
    )
    .await;
    assert_eq!(second_rename.status(), StatusCode::OK);
    let third_rename = patch_scim_user_name(
        &client,
        &server.origin,
        directory_id,
        token,
        original_id,
        "generation-c@example.test",
    )
    .await;
    assert_eq!(third_rename.status(), StatusCode::OK);

    let second_user = provision_scim_user(
        &client,
        &server.origin,
        directory_id,
        token,
        "generation-d@example.test",
        "Generation",
        "Second",
    )
    .await;
    let second_user_id = second_user["id"]
        .as_str()
        .expect("second SCIM user has an id");
    let second_user_claim = patch_scim_user_name(
        &client,
        &server.origin,
        directory_id,
        token,
        second_user_id,
        "generation-a@example.test",
    )
    .await;
    assert_eq!(second_user_claim.status(), StatusCode::OK);

    delayed_old_name_release.resume();
    let first_rename = first_rename
        .await
        .expect("delayed first rename task should complete");
    assert_eq!(first_rename.status(), StatusCode::OK);

    let third_user = provision_scim_user(
        &client,
        &server.origin,
        directory_id,
        token,
        "generation-e@example.test",
        "Generation",
        "Third",
    )
    .await;
    let third_user_id = third_user["id"]
        .as_str()
        .expect("third SCIM user has an id");
    let stolen_name = patch_scim_user_name(
        &client,
        &server.origin,
        directory_id,
        token,
        third_user_id,
        "generation-a@example.test",
    )
    .await;
    assert_eq!(stolen_name.status(), StatusCode::CONFLICT);
    let claim_id = auth_store::unique_claim_id(
        "scim-user-name",
        &format!("{directory_id}:generation-a@example.test"),
    );
    let active_claim = server
        .state
        .store
        .get::<Value>("uniques", &claim_id)
        .await
        .expect("current name claim should be readable")
        .expect("the second user should own the transferred claim");
    assert_eq!(
        active_claim["ownerId"],
        format!("{directory_id}:{second_user_id}")
    );
    assert_eq!(active_claim["released"], false);
}

#[tokio::test]
async fn inactive_scim_org_user_is_visible_without_organization_membership() {
    let server = TestServer::start().await;
    let client = server.client();
    let bootstrap = create_instance(&server, &client, "scim-inactive-user").await;
    let api_key = bootstrap["api_key"]
        .as_str()
        .expect("initial API key is returned");
    let organization = create_organization(
        &client,
        &server.origin,
        "scim-inactive-user",
        api_key,
        "SCIM Inactive User Org",
        "scim-inactive-user-org",
    )
    .await;
    let organization_id = organization["id"].as_str().expect("organization has an id");
    let directory = create_organization_directory(
        &client,
        &server.origin,
        "scim-inactive-user",
        api_key,
        organization_id,
    )
    .await;
    let directory_id = directory["id"].as_str().expect("directory has an id");
    let token = directory["token"]
        .as_str()
        .expect("directory token returned");
    let created = client
        .post(format!("{}/scim/{directory_id}/Users", server.origin))
        .bearer_auth(token)
        .json(&json!({
            "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
            "userName": "inactive-person@example.test",
            "active": false,
        }))
        .send()
        .await
        .expect("inactive SCIM provisioning should reach the service");
    assert_eq!(created.status(), StatusCode::CREATED);
    let created: Value = created
        .json()
        .await
        .expect("inactive SCIM user response should be JSON");
    assert_eq!(created["active"], false);
    let user_id = created["id"].as_str().expect("SCIM user has an id");
    let instance_id = bootstrap["instance"]["id"]
        .as_str()
        .expect("instance id is returned");
    assert!(
        server
            .state
            .store
            .find_user_by_email(instance_id, "inactive-person@example.test")
            .await
            .expect("inactive identity should be visible to the instance")
            .is_some_and(|user| !user.deleted)
    );
    let membership: Option<OrgMembership> = server
        .state
        .store
        .query_one(
            "memberships",
            "memberKey",
            &format!("{organization_id}:{user_id}"),
        )
        .await
        .expect("inactive membership lookup should succeed");
    assert!(membership.is_none());

    const PASSWORD: &str = "org-local-scim-password";
    let password_hash = users::hash_password(PASSWORD)
        .await
        .expect("test user password should hash");
    assert!(
        server
            .state
            .store
            .try_update_user(user_id, instance_id, |user| {
                user.password_hash = Some(password_hash.clone());
                true
            })
            .await
            .expect("test password should be stored")
    );
    let sign_in = sign_in_as(
        &client,
        &server.origin,
        &server.state.options.public_origin,
        "scim-inactive-user",
        "inactive-person@example.test",
        PASSWORD,
    )
    .await;
    assert_eq!(sign_in.status(), StatusCode::SEE_OTHER);
    let account = client
        .get(format!("{}/ui/scim-inactive-user/account", server.origin))
        .header(
            header::COOKIE,
            format!("tsauth_session={}", session_cookie(&sign_in)),
        )
        .send()
        .await
        .expect("org-local inactive user's account should reach the service");
    assert_eq!(account.status(), StatusCode::OK);
}

#[tokio::test]
async fn instance_wide_scim_activity_gates_and_releases_browser_auth_authority() {
    const PASSWORD: &str = "scim-managed-password";
    let server = TestServer::start().await;
    let client = server.client();
    let bootstrap = create_instance(&server, &client, "scim-auth-gate").await;
    let instance_id = bootstrap["instance"]["id"]
        .as_str()
        .expect("instance id is returned");
    let api_key = bootstrap["api_key"]
        .as_str()
        .expect("initial API key is returned");
    let directory = create_directory(&client, &server.origin, "scim-auth-gate", api_key).await;
    let directory_id = directory["id"].as_str().expect("directory has an id");
    let directory_token = directory["token"]
        .as_str()
        .expect("directory token is returned");
    let second_directory =
        create_directory(&client, &server.origin, "scim-auth-gate", api_key).await;
    let second_directory_id = second_directory["id"]
        .as_str()
        .expect("second directory has an id");
    let second_directory_token = second_directory["token"]
        .as_str()
        .expect("second directory token is returned");
    let created = provision_scim_user(
        &client,
        &server.origin,
        directory_id,
        directory_token,
        "managed-user@example.test",
        "Managed",
        "User",
    )
    .await;
    let user_id = created["id"].as_str().expect("SCIM user has an id");
    let provisioning_audits = server
        .state
        .store
        .list_audit_events(instance_id, 100)
        .await
        .expect("SCIM provisioning audit events should be readable")
        .into_iter()
        .filter(|event| {
            event.r#type == "directory.user_provisioned"
                && event.actor_id.as_deref() == Some(directory_id)
                && event.data.get("user_id").and_then(Value::as_str) == Some(user_id)
        })
        .count();
    assert_eq!(provisioning_audits, 1);
    let second_link = provision_scim_user(
        &client,
        &server.origin,
        second_directory_id,
        second_directory_token,
        "managed-user@example.test",
        "Managed",
        "User",
    )
    .await;
    assert_eq!(second_link["id"], user_id);
    let password_hash = users::hash_password(PASSWORD)
        .await
        .expect("test user password should hash");
    assert!(
        server
            .state
            .store
            .try_update_user(user_id, instance_id, |user| {
                user.password_hash = Some(password_hash.clone());
                true
            })
            .await
            .expect("test password should be stored")
    );

    let public_origin = server.state.options.public_origin.clone();
    let first_sign_in = sign_in_as(
        &client,
        &server.origin,
        &public_origin,
        "scim-auth-gate",
        "managed-user@example.test",
        PASSWORD,
    )
    .await;
    assert_eq!(first_sign_in.status(), StatusCode::SEE_OTHER);
    let cookie = session_cookie(&first_sign_in);
    let account = client
        .get(format!("{}/ui/scim-auth-gate/account", server.origin))
        .header(header::COOKIE, format!("tsauth_session={cookie}"))
        .send()
        .await
        .expect("managed user's account page should reach the service");
    assert_eq!(account.status(), StatusCode::OK);

    let template = client
        .post(format!("{}/v1/scim-auth-gate/jwt-templates", server.origin))
        .bearer_auth(api_key)
        .json(&json!({
            "name": "managed-user-token",
            "claims": { "email": "{{user.email}}" },
            "lifetime_seconds": 300,
        }))
        .send()
        .await
        .expect("management API key should create a JWT template");
    assert_eq!(template.status(), StatusCode::OK);
    let active_mint = mint_template_token(
        &client,
        &server.origin,
        "scim-auth-gate",
        api_key,
        "managed-user-token",
        user_id,
    )
    .await;
    assert_eq!(active_mint.status(), StatusCode::OK);
    let active_mint: Value = active_mint
        .json()
        .await
        .expect("active user's template token response should be JSON");
    assert!(
        active_mint["jwt"]
            .as_str()
            .is_some_and(|jwt| !jwt.is_empty())
    );
    let refresh_token =
        issue_pkce_refresh_token(&server, &client, "scim-auth-gate", instance_id, &cookie).await;

    let deactivated = patch_scim_user_active(
        &client,
        &server.origin,
        directory_id,
        directory_token,
        user_id,
        false,
    )
    .await;
    let deactivation_status = deactivated.status();
    let deactivation_body = deactivated
        .text()
        .await
        .expect("SCIM deactivation response should be readable");
    assert_eq!(
        deactivation_status,
        StatusCode::OK,
        "SCIM deactivation response: {deactivation_body}"
    );
    let inactive_profile = patch_scim_user_name(
        &client,
        &server.origin,
        directory_id,
        directory_token,
        user_id,
        "managed-inactive@example.test",
    )
    .await;
    assert_eq!(inactive_profile.status(), StatusCode::OK);
    let inactive_profile: Value = inactive_profile
        .json()
        .await
        .expect("inactive profile update should be JSON");
    assert_eq!(inactive_profile["active"], false);
    assert_eq!(
        inactive_profile["userName"],
        "managed-inactive@example.test"
    );
    let account = client
        .get(format!("{}/ui/scim-auth-gate/account", server.origin))
        .header(header::COOKIE, format!("tsauth_session={cookie}"))
        .send()
        .await
        .expect("inactive user's account request should reach the service");
    assert_eq!(account.status(), StatusCode::SEE_OTHER);
    let inactive_mint = mint_template_token(
        &client,
        &server.origin,
        "scim-auth-gate",
        api_key,
        "managed-user-token",
        user_id,
    )
    .await;
    assert_eq!(inactive_mint.status(), StatusCode::FORBIDDEN);
    let inactive_mint: Value = inactive_mint
        .json()
        .await
        .expect("inactive user's template denial should be JSON");
    assert_eq!(inactive_mint["error"]["code"], "forbidden");
    let refresh_attempt = client
        .post(format!("{}/scim-auth-gate/oauth/token", server.origin))
        .form(&[
            ("grant_type", "refresh_token"),
            ("client_id", "scim-auth-gate-client"),
            ("refresh_token", refresh_token.as_str()),
        ])
        .send()
        .await
        .expect("OAuth refresh grant should reach the service");
    assert_eq!(refresh_attempt.status(), StatusCode::BAD_REQUEST);
    let refresh_error: Value = refresh_attempt
        .json()
        .await
        .expect("inactive refresh denial should be JSON");
    assert_eq!(refresh_error["error"], "invalid_grant");
    let inactive_sign_in = sign_in_as(
        &client,
        &server.origin,
        &public_origin,
        "scim-auth-gate",
        "managed-inactive@example.test",
        PASSWORD,
    )
    .await;
    assert_ne!(inactive_sign_in.status(), StatusCode::SEE_OTHER);
    assert!(!inactive_sign_in.headers().contains_key(header::SET_COOKIE));

    let user_before_reactivation: Option<Value> = server
        .state
        .store
        .get("users", user_id)
        .await
        .expect("managed user should be readable before reactivation");
    let link_before_reactivation: Option<Value> = server
        .state
        .store
        .get("scim_user_links", &format!("{directory_id}:{user_id}"))
        .await
        .expect("SCIM link should be readable before reactivation");
    let directory_before_reactivation: Option<Value> = server
        .state
        .store
        .get("scim_directories", directory_id)
        .await
        .expect("SCIM directory should be readable before reactivation");
    let reactivated = put_scim_user_and_activate(
        &client,
        &server.origin,
        directory_id,
        directory_token,
        user_id,
        "managed-reactivated@example.test",
    )
    .await;
    let reactivation_status = reactivated.status();
    let reactivation_body = reactivated
        .text()
        .await
        .expect("SCIM reactivation response should be readable");
    assert_eq!(
        reactivation_status,
        StatusCode::OK,
        "SCIM reactivation response: {reactivation_body}; before request: {}",
        json!({
            "user": user_before_reactivation,
            "link": link_before_reactivation,
            "directory": directory_before_reactivation,
        })
    );
    let reactivation_response: Value = serde_json::from_str(&reactivation_body)
        .expect("successful full SCIM reactivation should return JSON");
    assert_eq!(reactivation_response["active"], true);
    assert_eq!(
        reactivation_response["userName"],
        "managed-reactivated@example.test"
    );
    let account = client
        .get(format!("{}/ui/scim-auth-gate/account", server.origin))
        .header(header::COOKIE, format!("tsauth_session={cookie}"))
        .send()
        .await
        .expect("reactivated user's account request should reach the service");
    assert_eq!(account.status(), StatusCode::OK);
    let restored_mint = mint_template_token(
        &client,
        &server.origin,
        "scim-auth-gate",
        api_key,
        "managed-user-token",
        user_id,
    )
    .await;
    assert_eq!(restored_mint.status(), StatusCode::OK);

    assert!(
        server
            .state
            .store
            .try_update_user(user_id, instance_id, |user| {
                user.banned = true;
                true
            })
            .await
            .expect("manual ban should be stored")
    );
    let banned_deactivation = patch_scim_user_active(
        &client,
        &server.origin,
        directory_id,
        directory_token,
        user_id,
        false,
    )
    .await;
    assert_eq!(banned_deactivation.status(), StatusCode::OK);
    let banned_reactivation = patch_scim_user_active(
        &client,
        &server.origin,
        directory_id,
        directory_token,
        user_id,
        true,
    )
    .await;
    assert_eq!(banned_reactivation.status(), StatusCode::OK);
    let banned_account = client
        .get(format!("{}/ui/scim-auth-gate/account", server.origin))
        .header(header::COOKIE, format!("tsauth_session={cookie}"))
        .send()
        .await
        .expect("banned user's account request should reach the service");
    assert_eq!(banned_account.status(), StatusCode::SEE_OTHER);
    let banned_sign_in = sign_in_as(
        &client,
        &server.origin,
        &public_origin,
        "scim-auth-gate",
        "managed-reactivated@example.test",
        PASSWORD,
    )
    .await;
    assert_ne!(banned_sign_in.status(), StatusCode::SEE_OTHER);
    assert!(!banned_sign_in.headers().contains_key(header::SET_COOKIE));
    assert!(
        server
            .state
            .store
            .try_update_user(user_id, instance_id, |user| {
                user.banned = false;
                true
            })
            .await
            .expect("manual ban should be cleared for the lifecycle checks")
    );

    let deleted_user = client
        .delete(format!(
            "{}/scim/{directory_id}/Users/{user_id}",
            server.origin
        ))
        .bearer_auth(directory_token)
        .send()
        .await
        .expect("SCIM user deprovision should reach the service");
    assert_eq!(deleted_user.status(), StatusCode::NO_CONTENT);
    let stale_patch = patch_scim_user_active(
        &client,
        &server.origin,
        directory_id,
        directory_token,
        user_id,
        true,
    )
    .await;
    assert_eq!(stale_patch.status(), StatusCode::NOT_FOUND);
    let stale_link: Value = server
        .state
        .store
        .get("scim_user_links", &format!("{directory_id}:{user_id}"))
        .await
        .expect("stale PATCH must leave the terminal link tombstone readable")
        .expect("completed SCIM cleanup retains its link tombstone");
    assert_eq!(stale_link["active"], false);
    assert_eq!(stale_link["deleting"], false);
    assert_eq!(stale_link["cleanupComplete"], true);
    assert_eq!(stale_link["cleanupOwner"], Value::Null);
    let hidden_user = client
        .get(format!(
            "{}/scim/{directory_id}/Users/{user_id}",
            server.origin
        ))
        .bearer_auth(directory_token)
        .send()
        .await
        .expect("retained terminal link should remain hidden from SCIM reads");
    assert_eq!(hidden_user.status(), StatusCode::NOT_FOUND);
    let detached_account = client
        .get(format!("{}/ui/scim-auth-gate/account", server.origin))
        .header(header::COOKIE, format!("tsauth_session={cookie}"))
        .send()
        .await
        .expect("user without its SCIM link should be checked by the service");
    assert_eq!(detached_account.status(), StatusCode::SEE_OTHER);

    let recreated = provision_scim_user(
        &client,
        &server.origin,
        directory_id,
        directory_token,
        "managed-reactivated@example.test",
        "Managed",
        "User",
    )
    .await;
    assert_eq!(recreated["id"], user_id);
    let recreated_account = client
        .get(format!("{}/ui/scim-auth-gate/account", server.origin))
        .header(header::COOKIE, format!("tsauth_session={cookie}"))
        .send()
        .await
        .expect("fresh SCIM link should restore the user session");
    assert_eq!(recreated_account.status(), StatusCode::OK);

    let deleted_directory = client
        .delete(format!(
            "{}/v1/scim-auth-gate/directories/{directory_id}",
            server.origin
        ))
        .bearer_auth(api_key)
        .send()
        .await
        .expect("SCIM directory deletion should reach the service");
    assert_eq!(deleted_directory.status(), StatusCode::NO_CONTENT);
    let user: Value = server
        .state
        .store
        .get("users", user_id)
        .await
        .expect("detached SCIM user should be readable")
        .expect("SCIM user should remain after directory deletion");
    assert_eq!(
        user["scimInstanceWideDirectoryIds"],
        json!([directory_id, second_directory_id])
    );
    let released_account = client
        .get(format!("{}/ui/scim-auth-gate/account", server.origin))
        .header(header::COOKIE, format!("tsauth_session={cookie}"))
        .send()
        .await
        .expect("user should retain independent account access after directory deletion");
    assert_eq!(released_account.status(), StatusCode::OK);

    let second_deactivated = patch_scim_user_active(
        &client,
        &server.origin,
        second_directory_id,
        second_directory_token,
        user_id,
        false,
    )
    .await;
    assert_eq!(second_deactivated.status(), StatusCode::OK);
    let account = client
        .get(format!("{}/ui/scim-auth-gate/account", server.origin))
        .header(header::COOKIE, format!("tsauth_session={cookie}"))
        .send()
        .await
        .expect("remaining active directory should continue to govern access");
    assert_eq!(account.status(), StatusCode::SEE_OTHER);
    let second_reactivated = patch_scim_user_active(
        &client,
        &server.origin,
        second_directory_id,
        second_directory_token,
        user_id,
        true,
    )
    .await;
    assert_eq!(second_reactivated.status(), StatusCode::OK);
    let account = client
        .get(format!("{}/ui/scim-auth-gate/account", server.origin))
        .header(header::COOKIE, format!("tsauth_session={cookie}"))
        .send()
        .await
        .expect("reactivating the remaining directory should restore access");
    assert_eq!(account.status(), StatusCode::OK);
    let deleted_second_directory = client
        .delete(format!(
            "{}/v1/scim-auth-gate/directories/{second_directory_id}",
            server.origin
        ))
        .bearer_auth(api_key)
        .send()
        .await
        .expect("second directory deletion should reach the service");
    assert_eq!(deleted_second_directory.status(), StatusCode::NO_CONTENT);
    let account = client
        .get(format!("{}/ui/scim-auth-gate/account", server.origin))
        .header(header::COOKIE, format!("tsauth_session={cookie}"))
        .send()
        .await
        .expect("durable deletion of the last authority should restore access");
    assert_eq!(account.status(), StatusCode::OK);
}

#[tokio::test]
async fn scim_directory_tombstone_before_activation_never_returns_an_uncommitted_user() {
    let server = TestServer::start().await;
    let client = server.client();
    let bootstrap = create_instance(&server, &client, "scim-activation-race").await;
    let api_key = bootstrap["api_key"]
        .as_str()
        .expect("initial API key is returned");
    let organization = create_organization(
        &client,
        &server.origin,
        "scim-activation-race",
        api_key,
        "SCIM Activation Race Org",
        "scim-activation-race-org",
    )
    .await;
    let organization_id = organization["id"].as_str().expect("organization has an id");
    let directory = create_organization_directory(
        &client,
        &server.origin,
        "scim-activation-race",
        api_key,
        organization_id,
    )
    .await;
    let directory_id = directory["id"].as_str().expect("directory has an id");
    let token = directory["token"]
        .as_str()
        .expect("directory token returned");
    let email = "tombstone-race@example.test";

    let mut pause = server
        ._storage_fixture
        .as_ref()
        .expect("contract fixture is available")
        .pause_next_compare_and_put("users");
    let create_client = client.clone();
    let create_origin = server.origin.clone();
    let create_directory_id = directory_id.to_owned();
    let create_token = token.to_owned();
    let create = tokio::spawn(async move {
        create_client
            .post(format!("{create_origin}/scim/{create_directory_id}/Users"))
            .bearer_auth(create_token)
            .json(&json!({
                "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
                "userName": email,
                "active": true,
            }))
            .send()
            .await
    });
    timeout(Duration::from_secs(3), pause.wait_until_paused())
        .await
        .expect("provisional user should reach activation compare-and-put");

    let instance_id = bootstrap["instance"]["id"]
        .as_str()
        .expect("instance id is returned");
    let provisional: Value = server
        .state
        .store
        .query_one("users", "emailKey", &format!("{instance_id}:{email}"))
        .await
        .expect("provisional identity should be readable")
        .expect("provisional identity should be stored before activation");
    let user_id = provisional["id"]
        .as_str()
        .expect("provisional user has an id");
    assert_eq!(provisional["deleted"], true);

    let deleted = client
        .delete(format!(
            "{}/v1/scim-activation-race/directories/{directory_id}",
            server.origin
        ))
        .bearer_auth(api_key)
        .send()
        .await
        .expect("directory tombstone should reach the service");
    assert_eq!(deleted.status(), StatusCode::NO_CONTENT);
    pause.resume();

    let created = timeout(Duration::from_secs(5), create)
        .await
        .expect("provisional create request should finish")
        .expect("create request task should complete")
        .expect("create request should reach the service");
    assert!(matches!(
        created.status(),
        StatusCode::CREATED | StatusCode::CONFLICT
    ));
    let stored: Option<Value> = server
        .state
        .store
        .get("users", user_id)
        .await
        .expect("provisioned user state should be readable");
    if created.status() == StatusCode::CREATED {
        let stored = stored.expect("committed user should remain stored");
        assert_eq!(stored["deleted"], false);
    } else {
        assert!(stored.is_none_or(|stored| stored["deleted"] == true));
    }
    let terminal_link = server
        .state
        .store
        .get::<Value>("scim_user_links", &format!("{directory_id}:{user_id}"))
        .await
        .expect("deleted directory link should be readable")
        .expect("directory cleanup retains its terminal link tombstone");
    assert_eq!(terminal_link["active"], false);
    assert_eq!(terminal_link["deleting"], false);
    assert_eq!(terminal_link["cleanupComplete"], true);
    assert_eq!(terminal_link["cleanupOwner"], Value::Null);
    let revoked_directory_read = client
        .get(format!(
            "{}/scim/{directory_id}/Users/{user_id}",
            server.origin
        ))
        .bearer_auth(token)
        .send()
        .await
        .expect("deleted directory must reject its former token");
    assert_eq!(revoked_directory_read.status(), StatusCode::UNAUTHORIZED);
    let membership: Option<OrgMembership> = server
        .state
        .store
        .query_one(
            "memberships",
            "memberKey",
            &format!("{organization_id}:{user_id}"),
        )
        .await
        .expect("deleted directory membership should be queryable");
    assert!(membership.is_none());
}

#[tokio::test]
async fn scim_audit_failure_after_activation_keeps_the_committed_create_response() {
    let server = TestServer::start().await;
    let client = server.client();
    let bootstrap = create_instance(&server, &client, "scim-audit-after-commit").await;
    let api_key = bootstrap["api_key"]
        .as_str()
        .expect("initial API key is returned");
    let organization = create_organization(
        &client,
        &server.origin,
        "scim-audit-after-commit",
        api_key,
        "SCIM Audit Org",
        "scim-audit-org",
    )
    .await;
    let organization_id = organization["id"].as_str().expect("organization has an id");
    let directory = create_organization_directory(
        &client,
        &server.origin,
        "scim-audit-after-commit",
        api_key,
        organization_id,
    )
    .await;
    let directory_id = directory["id"].as_str().expect("directory has an id");
    let token = directory["token"]
        .as_str()
        .expect("directory token returned");
    server
        ._storage_fixture
        .as_ref()
        .expect("contract fixture is available")
        .fail_next_create_only_before_commit("audit_events");

    let created = client
        .post(format!("{}/scim/{directory_id}/Users", server.origin))
        .bearer_auth(token)
        .json(&json!({
            "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
            "userName": "audit-committed@example.test",
            "active": true,
        }))
        .send()
        .await
        .expect("SCIM user creation should reach the service");
    assert_eq!(created.status(), StatusCode::CREATED);
    let created: Value = created
        .json()
        .await
        .expect("committed SCIM response should be JSON");
    let user_id = created["id"].as_str().expect("SCIM user has an id");
    let instance_id = bootstrap["instance"]["id"]
        .as_str()
        .expect("instance id is returned");
    assert!(
        server
            .state
            .store
            .find_user_by_email(instance_id, "audit-committed@example.test")
            .await
            .expect("committed user should be queryable")
            .is_some_and(|user| !user.deleted)
    );
    let membership: Option<OrgMembership> = server
        .state
        .store
        .query_one(
            "memberships",
            "memberKey",
            &format!("{organization_id}:{user_id}"),
        )
        .await
        .expect("committed membership should be queryable");
    assert!(membership.is_some());
}

#[tokio::test]
async fn scim_directory_token_is_rejected_when_its_parent_org_is_deleting() {
    let server = TestServer::start().await;
    let client = server.client();
    let bootstrap = create_instance(&server, &client, "scim-parent-deleting").await;
    let api_key = bootstrap["api_key"]
        .as_str()
        .expect("initial API key is returned");
    let organization = create_organization(
        &client,
        &server.origin,
        "scim-parent-deleting",
        api_key,
        "Deleting Parent Org",
        "deleting-parent-org",
    )
    .await;
    let organization_id = organization["id"].as_str().expect("organization has an id");
    let directory = create_organization_directory(
        &client,
        &server.origin,
        "scim-parent-deleting",
        api_key,
        organization_id,
    )
    .await;
    let directory_id = directory["id"].as_str().expect("directory has an id");
    let token = directory["token"]
        .as_str()
        .expect("directory token returned");

    let (mut parent, version) = server
        .state
        .store
        .get_versioned::<Organization>("organizations", organization_id)
        .await
        .expect("parent organization should be readable")
        .expect("parent organization should exist");
    parent.status = OrganizationStatus::Deleting;
    assert!(
        server
            .state
            .store
            .compare_and_put("organizations", organization_id, &parent, &version)
            .await
            .expect("parent tombstone should be written")
    );

    let list = client
        .get(format!("{}/scim/{directory_id}/Users", server.origin))
        .bearer_auth(token)
        .send()
        .await
        .expect("SCIM list should reach the service");
    assert_eq!(list.status(), StatusCode::UNAUTHORIZED);

    let create = client
        .post(format!("{}/scim/{directory_id}/Users", server.origin))
        .bearer_auth(token)
        .json(&json!({
            "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
            "userName": "late-parent-delete@example.test",
            "active": true,
        }))
        .send()
        .await
        .expect("SCIM write should reach the service");
    assert_eq!(create.status(), StatusCode::UNAUTHORIZED);
    let instance_id = bootstrap["instance"]["id"]
        .as_str()
        .expect("instance id is returned");
    assert!(
        server
            .state
            .store
            .find_user_by_email(instance_id, "late-parent-delete@example.test")
            .await
            .expect("late SCIM user should be queryable")
            .is_none()
    );
}

#[tokio::test]
async fn scim_tokens_are_directory_scoped_and_rotation_and_delete_revoke_them() {
    let server = TestServer::start().await;
    let client = server.client();
    let alpha = create_instance(&server, &client, "scim-alpha").await;
    let bravo = create_instance(&server, &client, "scim-bravo").await;
    let alpha_id = alpha["instance"]["id"]
        .as_str()
        .expect("instance id is returned");
    let alpha_key = alpha["api_key"]
        .as_str()
        .expect("initial API key is returned");
    let bravo_key = bravo["api_key"]
        .as_str()
        .expect("initial API key is returned");
    let directory_a = create_directory(&client, &server.origin, "scim-alpha", alpha_key).await;
    let directory_b = create_directory(&client, &server.origin, "scim-bravo", bravo_key).await;
    let directory_a_id = directory_a["id"]
        .as_str()
        .expect("directory id is returned");
    let directory_b_id = directory_b["id"]
        .as_str()
        .expect("directory id is returned");
    let token_a = directory_a["token"]
        .as_str()
        .expect("directory token is returned");
    let token_b = directory_b["token"]
        .as_str()
        .expect("directory token is returned");

    let unauthenticated = client
        .get(format!("{}/scim/{directory_a_id}/Users", server.origin))
        .send()
        .await
        .expect("unauthenticated SCIM request should reach the service");
    assert_eq!(unauthenticated.status(), StatusCode::UNAUTHORIZED);
    let wrong_directory_token = client
        .get(format!("{}/scim/{directory_a_id}/Users", server.origin))
        .bearer_auth(token_b)
        .send()
        .await
        .expect("wrong-directory SCIM token should reach the service");
    assert_eq!(wrong_directory_token.status(), StatusCode::UNAUTHORIZED);

    let created_user = client
        .post(format!("{}/scim/{directory_a_id}/Users", server.origin))
        .bearer_auth(token_a)
        .json(&json!({
            "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
            "userName": "provisioned@example.test",
            "active": true,
            "externalId": "tenant-alpha-person",
        }))
        .send()
        .await
        .expect("SCIM user creation should reach the service");
    assert_eq!(created_user.status(), StatusCode::CREATED);
    let created_user: Value = created_user
        .json()
        .await
        .expect("SCIM user response should be JSON");
    let user_id = created_user["id"].as_str().expect("created user has an id");
    assert_eq!(created_user["userName"], "provisioned@example.test");
    assert_eq!(
        created_user["meta"]["location"],
        format!("/scim/{directory_a_id}/Users/{user_id}")
    );

    let list_b = client
        .get(format!("{}/scim/{directory_b_id}/Users", server.origin))
        .bearer_auth(token_b)
        .send()
        .await
        .expect("the other tenant's SCIM list should reach the service");
    assert_eq!(list_b.status(), StatusCode::OK);
    let list_b: Value = list_b.json().await.expect("SCIM list should be JSON");
    assert_eq!(list_b["totalResults"], 0);
    let detail_b = client
        .get(format!(
            "{}/scim/{directory_b_id}/Users/{user_id}",
            server.origin
        ))
        .bearer_auth(token_b)
        .send()
        .await
        .expect("cross-tenant user lookup should reach the service");
    assert_eq!(detail_b.status(), StatusCode::NOT_FOUND);

    let rotated = client
        .post(format!(
            "{}/v1/scim-alpha/directories/{directory_a_id}/rotate",
            server.origin
        ))
        .bearer_auth(alpha_key)
        .send()
        .await
        .expect("directory token rotation should reach the service");
    assert_eq!(rotated.status(), StatusCode::OK);
    let rotated: Value = rotated
        .json()
        .await
        .expect("rotated directory response is JSON");
    let rotated_token = rotated["token"]
        .as_str()
        .expect("rotated token is returned");
    assert_ne!(rotated_token, token_a);
    let old_token = client
        .get(format!("{}/scim/{directory_a_id}/Users", server.origin))
        .bearer_auth(token_a)
        .send()
        .await
        .expect("old token use should reach the service");
    assert_eq!(old_token.status(), StatusCode::UNAUTHORIZED);
    let new_token = client
        .get(format!("{}/scim/{directory_a_id}/Users", server.origin))
        .bearer_auth(rotated_token)
        .send()
        .await
        .expect("rotated token use should reach the service");
    assert_eq!(new_token.status(), StatusCode::OK);

    let deleted = client
        .delete(format!(
            "{}/v1/scim-alpha/directories/{directory_a_id}",
            server.origin
        ))
        .bearer_auth(alpha_key)
        .send()
        .await
        .expect("directory deletion should reach the service");
    assert_eq!(deleted.status(), StatusCode::NO_CONTENT);
    let deleted_token = client
        .get(format!("{}/scim/{directory_a_id}/Users", server.origin))
        .bearer_auth(rotated_token)
        .send()
        .await
        .expect("deleted directory token use should reach the service");
    assert_eq!(deleted_token.status(), StatusCode::UNAUTHORIZED);
    let other_tenant_still_works = client
        .get(format!("{}/scim/{directory_b_id}/Users", server.origin))
        .bearer_auth(token_b)
        .send()
        .await
        .expect("unrelated directory should remain available");
    assert_eq!(other_tenant_still_works.status(), StatusCode::OK);
    let owner = server
        .state
        .store
        .find_instance_by_slug("scim-alpha")
        .await
        .expect("instance owner should remain readable")
        .expect("SCIM instance exists");
    assert_eq!(owner.id, alpha_id);
}

#[tokio::test]
async fn admin_provider_setup_returns_safe_datastar_sse_for_seeded_admin_session() {
    let server = TestServer::start().await;
    let client = server.client();
    let instance = create_instance(&server, &client, "admin-sse-test").await["instance"].clone();
    let instance_id = instance["id"].as_str().expect("instance id is returned");
    let now = unix_now();
    let session_token = "admin-session-for-sse-test";
    seed_record(
        &server,
        "users",
        "usr_admin_sse",
        &json!({
            "id": "usr_admin_sse",
            "instanceId": instance_id,
            "email": "admin@example.test",
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
        "ses_admin_sse",
        &json!({
            "id": "ses_admin_sse",
            "instanceId": instance_id,
            "userId": "usr_admin_sse",
            "tokenKey": sha256_hex(session_token),
            "status": "active",
            "createdAt": now,
            "expiresAt": now + 3_600,
            "lastActiveAt": now,
        }),
    )
    .await;
    let organizations = [
        ("org_sse_one", "Operations <script>\nevent: forged"),
        ("org_sse_two", "Research"),
    ];
    for (organization_id, name) in organizations {
        seed_record(
            &server,
            "organizations",
            organization_id,
            &json!({
                "id": organization_id,
                "instanceId": instance_id,
                "name": name,
                "slugKey": format!("{instance_id}:{organization_id}"),
                "createdAt": now,
            }),
        )
        .await;
        seed_record(
            &server,
            "memberships",
            &format!("mem_{organization_id}"),
            &json!({
                "id": format!("mem_{organization_id}"),
                "instanceId": instance_id,
                "orgId": organization_id,
                "userId": "usr_admin_sse",
                "memberKey": format!("{organization_id}:usr_admin_sse"),
                "role": "admin",
                "createdAt": now,
            }),
        )
        .await;
    }

    let response = client
        .get(format!(
            "{}/ui/admin-sse-test/admin/providers/github",
            server.origin
        ))
        .header(header::COOKIE, format!("tsauth_session={session_token}"))
        .send()
        .await
        .expect("admin Datastar request should reach the service");
    assert_eq!(response.status(), StatusCode::OK);
    assert!(
        response
            .headers()
            .get(header::CONTENT_TYPE)
            .and_then(|value| value.to_str().ok())
            .is_some_and(|value| value.starts_with("text/event-stream"))
    );
    let event = response
        .text()
        .await
        .expect("Datastar response should be text");
    assert!(event.starts_with("event: datastar-patch-elements\ndata: elements "));
    assert_eq!(
        event
            .lines()
            .filter(|line| line.starts_with("data:"))
            .count(),
        1
    );
    assert!(event.contains("id=\"provider-setup\""));
    assert!(event.contains("Operations &lt;script&gt;&#10;event: forged"));
    assert!(!event.contains("\nevent: forged"));

    let get_mutation = client
        .get(format!(
            "{}/ui/admin-sse-test/admin/connections",
            server.origin
        ))
        .send()
        .await
        .expect("GET on the admin mutation route should reach the router");
    assert_eq!(get_mutation.status(), StatusCode::METHOD_NOT_ALLOWED);
}

#[tokio::test]
async fn stripe_checkout_events_for_one_customer_race_to_one_tenant_owned_row() {
    const WEBHOOK_SECRET: &str = "whsec_local_contract_fixture";
    let stripe = StripeApiFixture::start().await;
    stripe.queue_checkout_session_id("cs_alpha");
    stripe.queue_checkout_session_id("cs_bravo");
    let stripe_api_url = stripe.origin.clone();
    let server = TestServer::start_with_config(move |options| {
        options.stripe_secret_key = Some("sk_test_contract_fixture".to_owned());
        options.stripe_webhook_secret = Some(WEBHOOK_SECRET.to_owned());
        options.stripe_api_url = stripe_api_url;
        options.price_pro = Some("price_pro_contract".to_owned());
    })
    .await;
    let client = server.client();
    let alpha_bootstrap = create_instance(&server, &client, "stripe-alpha").await;
    let bravo_bootstrap = create_instance(&server, &client, "stripe-bravo").await;
    let alpha = alpha_bootstrap["instance"].clone();
    let bravo = bravo_bootstrap["instance"].clone();
    let alpha_id = alpha["id"].as_str().expect("instance id is returned");
    let alpha_key = alpha_bootstrap["api_key"]
        .as_str()
        .expect("initial API key is returned");
    let bravo_key = bravo_bootstrap["api_key"]
        .as_str()
        .expect("initial API key is returned");
    let bravo_id = bravo["id"].as_str().expect("instance id is returned");
    create_checkout_session(
        &server,
        &client,
        "stripe-alpha",
        alpha_key,
        "checkout-alpha",
    )
    .await;
    let alpha_nonce = checkout_intent_nonce(&server, alpha_id).await;
    create_checkout_session(
        &server,
        &client,
        "stripe-bravo",
        bravo_key,
        "checkout-bravo",
    )
    .await;
    let bravo_nonce = checkout_intent_nonce(&server, bravo_id).await;
    let now = unix_now();
    let payload_a = serde_json::to_vec(&json!({
        "id": "evt_checkout_alpha",
        "type": "checkout.session.completed",
        "created": now,
        "data": { "object": {
            "id": "cs_alpha",
            "mode": "subscription",
            "client_reference_id": alpha_id,
            "customer": "cus_checkout_race",
            "subscription": "sub_checkout_alpha",
            "metadata": {
                "instance_id": alpha_id,
                "plan": "pro",
                "ts_auth_intent_nonce": alpha_nonce.clone()
            }
        }}
    }))
    .expect("first signed Stripe event should serialize");
    let payload_b = serde_json::to_vec(&json!({
        "id": "evt_checkout_bravo",
        "type": "checkout.session.completed",
        "created": now + 1,
        "data": { "object": {
            "id": "cs_alpha",
            "mode": "subscription",
            "client_reference_id": alpha_id,
            "customer": "cus_checkout_race",
            "subscription": "sub_checkout_alpha",
            "metadata": {
                "instance_id": alpha_id,
                "plan": "pro",
                "ts_auth_intent_nonce": alpha_nonce.clone()
            }
        }}
    }))
    .expect("second signed Stripe event should serialize");
    let signature_a = stripe_signature(WEBHOOK_SECRET, &payload_a);
    let signature_b = stripe_signature(WEBHOOK_SECRET, &payload_b);

    let (response_a, response_b) = tokio::join!(
        client
            .post(format!("{}/v1/billing/webhook", server.origin))
            .header("Stripe-Signature", signature_a)
            .header(header::CONTENT_TYPE, "application/json")
            .body(payload_a)
            .send(),
        client
            .post(format!("{}/v1/billing/webhook", server.origin))
            .header("Stripe-Signature", signature_b)
            .header(header::CONTENT_TYPE, "application/json")
            .body(payload_b)
            .send(),
    );
    let response_a = response_a.expect("first webhook should reach the service");
    let response_b = response_b.expect("second webhook should reach the service");
    assert_eq!(response_a.status(), StatusCode::OK);
    assert_eq!(response_b.status(), StatusCode::OK);

    let cross_tenant_payload = serde_json::to_vec(&json!({
        "id": "evt_checkout_cross_tenant",
        "type": "checkout.session.completed",
        "created": now + 2,
        "data": { "object": {
            "id": "cs_bravo",
            "mode": "subscription",
            "client_reference_id": bravo_id,
            "customer": "cus_checkout_race",
            "subscription": "sub_checkout_cross_tenant",
            "metadata": {
                "instance_id": bravo_id,
                "plan": "pro",
                "ts_auth_intent_nonce": bravo_nonce.clone()
            }
        }}
    }))
    .expect("cross-tenant signed Stripe event should serialize");
    let cross_tenant = client
        .post(format!("{}/v1/billing/webhook", server.origin))
        .header(
            "Stripe-Signature",
            stripe_signature(WEBHOOK_SECRET, &cross_tenant_payload),
        )
        .header(header::CONTENT_TYPE, "application/json")
        .body(cross_tenant_payload)
        .send()
        .await
        .expect("cross-tenant customer event should reach the service");
    assert_eq!(cross_tenant.status(), StatusCode::OK);

    let customer = auth_store::Predicate::equal("stripeCustomerId", "cus_checkout_race")
        .expect("customer predicate should be valid");
    let subscriptions: Vec<Value> = server
        .state
        .store
        .query_all("subscriptions", &[customer], 100)
        .await
        .expect("subscription rows should be readable through SDK fixture");
    assert_eq!(subscriptions.len(), 1);
    assert_eq!(subscriptions[0]["instanceId"], alpha_id);
    assert_eq!(subscriptions[0]["stripeCustomerId"], "cus_checkout_race");
    let unlinked_tenant = server
        .state
        .store
        .find_instance_by_slug("stripe-bravo")
        .await
        .expect("second tenant should be readable")
        .expect("second tenant exists");
    assert_ne!(unlinked_tenant.id, alpha_id);
    assert!(unlinked_tenant.stripe_customer_id.is_none());
}

#[tokio::test]
async fn unknown_stripe_price_reconciliation_uses_the_free_plan_and_quota() {
    const WEBHOOK_SECRET: &str = "whsec_unknown_price_contract_fixture";
    let stripe = StripeApiFixture::start().await;
    stripe.queue_subscription_price("price_not_configured");
    let stripe_api_url = stripe.origin.clone();
    let server = TestServer::start_with_config(move |options| {
        options.stripe_secret_key = Some("sk_test_contract_fixture".to_owned());
        options.stripe_webhook_secret = Some(WEBHOOK_SECRET.to_owned());
        options.stripe_api_url = stripe_api_url;
        options.price_pro = Some("price_pro_contract".to_owned());
        options.price_enterprise = Some("price_enterprise_contract".to_owned());
    })
    .await;
    let client = server.client();
    let bootstrap = create_instance(&server, &client, "stripe-unknown-price").await;
    let instance_id = bootstrap["instance"]["id"]
        .as_str()
        .expect("instance id is returned");
    let api_key = bootstrap["api_key"]
        .as_str()
        .expect("initial API key is returned");
    seed_record(
        &server,
        "subscriptions",
        "sub_unknown_price_row",
        &json!({
            "id": "sub_unknown_price_row",
            "instanceId": instance_id,
            "stripeCustomerId": "cus_checkout_race",
            "stripeSubscriptionId": "sub_unknown_price",
            "plan": "enterprise",
            "status": "active",
            "currentPeriodEnd": 2_000_000_000_i64,
            "createdAt": unix_now() - 100,
            "updatedAt": unix_now() - 100,
        }),
    )
    .await;

    let payload = serde_json::to_vec(&json!({
        "id": "evt_unknown_price_reconcile",
        "type": "customer.subscription.updated",
        "created": unix_now(),
        "data": { "object": {
            "id": "sub_unknown_price",
            "customer": "cus_checkout_race",
            "status": "active",
            "current_period_end": 2_000_000_000_i64,
            "items": { "data": [{ "price": { "id": "price_not_configured" } }] },
        }}
    }))
    .expect("signed unknown-price event should serialize");
    let response = client
        .post(format!("{}/v1/billing/webhook", server.origin))
        .header(
            "Stripe-Signature",
            stripe_signature(WEBHOOK_SECRET, &payload),
        )
        .header(header::CONTENT_TYPE, "application/json")
        .body(payload)
        .send()
        .await
        .expect("unknown-price webhook should reach the service");
    assert_eq!(response.status(), StatusCode::OK);

    let billing = client
        .get(format!("{}/v1/stripe-unknown-price/billing", server.origin))
        .bearer_auth(api_key)
        .send()
        .await
        .expect("billing projection should reach the service");
    assert_eq!(billing.status(), StatusCode::OK);
    let billing: Value = billing
        .json()
        .await
        .expect("billing projection should be JSON");
    assert_eq!(billing["plan"], "free");
    assert_eq!(billing["quota"], 100);
}

#[tokio::test]
async fn checkout_rechecks_subscription_after_a_stale_empty_lookup_before_stripe_post() {
    const WEBHOOK_SECRET: &str = "whsec_checkout_claim_race_fixture";
    let stripe = StripeApiFixture::start().await;
    stripe.queue_checkout_session_id("cs_checkout_race_a");
    let stripe_api_url = stripe.origin.clone();
    let server = TestServer::start_with_config(move |options| {
        options.stripe_secret_key = Some("sk_test_contract_fixture".to_owned());
        options.stripe_webhook_secret = Some(WEBHOOK_SECRET.to_owned());
        options.stripe_api_url = stripe_api_url;
        options.price_pro = Some("price_pro_contract".to_owned());
    })
    .await;
    let client = server.client();
    let bootstrap = create_instance(&server, &client, "stripe-checkout-claim-race").await;
    let instance_id = bootstrap["instance"]["id"]
        .as_str()
        .expect("instance id is returned");
    let api_key = bootstrap["api_key"]
        .as_str()
        .expect("initial API key is returned");

    let first_checkout = create_checkout_session(
        &server,
        &client,
        "stripe-checkout-claim-race",
        api_key,
        "checkout-race-a",
    )
    .await;
    assert_eq!(
        first_checkout["checkout_url"],
        "https://checkout.stripe.test/cs_checkout_race_a"
    );
    let nonce = checkout_intent_nonce(&server, instance_id).await;

    let fixture = server
        ._storage_fixture
        .as_ref()
        .expect("contract fixture is available");
    let mut empty_subscription_snapshot = fixture.pause_next_query_response("subscriptions");
    let request_client = client.clone();
    let request_origin = server.origin.clone();
    let request_key = api_key.to_owned();
    let competing_request = tokio::spawn(async move {
        request_client
            .post(format!(
                "{request_origin}/v1/stripe-checkout-claim-race/billing/checkout"
            ))
            .bearer_auth(request_key)
            .header("Idempotency-Key", "checkout-race-b")
            .json(&json!({ "plan": "pro" }))
            .send()
            .await
    });
    empty_subscription_snapshot.wait_until_paused().await;

    let payload = serde_json::to_vec(&json!({
        "id": "evt_checkout_claim_race_a",
        "type": "checkout.session.completed",
        "created": unix_now(),
        "data": { "object": {
            "id": "cs_checkout_race_a",
            "mode": "subscription",
            "client_reference_id": instance_id,
            "customer": "cus_checkout_race",
            "subscription": "sub_checkout_race_a",
            "metadata": {
                "instance_id": instance_id,
                "plan": "pro",
                "ts_auth_intent_nonce": nonce,
            }
        }}
    }))
    .expect("checkout completion event should serialize");
    let completed = client
        .post(format!("{}/v1/billing/webhook", server.origin))
        .header(
            "Stripe-Signature",
            stripe_signature(WEBHOOK_SECRET, &payload),
        )
        .header(header::CONTENT_TYPE, "application/json")
        .body(payload)
        .send()
        .await
        .expect("checkout completion webhook should reach the service");
    assert_eq!(completed.status(), StatusCode::OK);

    // An erroneous checkout POST is counted even if its response fixture fails,
    // making the assertion independent of Stripe's returned body.
    stripe.queue_checkout_session_id("cs_checkout_race_must_not_be_created");
    empty_subscription_snapshot.resume();
    let competing = competing_request
        .await
        .expect("competing checkout request task should complete")
        .expect("competing checkout request should reach the service");
    assert_eq!(competing.status(), StatusCode::CONFLICT);
    let competing: Value = competing
        .json()
        .await
        .expect("competing checkout error should be JSON");
    assert_eq!(competing["error"]["code"], "subscription_exists");
    assert_eq!(stripe.checkout_request_count(), 1);

    let subscription = server
        .state
        .store
        .find_subscription_by_customer("cus_checkout_race")
        .await
        .expect("committed subscription should remain readable")
        .expect("checkout completion should create its subscription");
    assert_eq!(subscription.instance_id, instance_id);
}

#[tokio::test]
async fn webhook_dispatcher_finds_an_eligible_backdated_event_committed_late() {
    let server = TestServer::start().await;
    let instance_id = "inst_late_audit_commit";
    let endpoint_id = "wh_late_audit_commit";
    seed_dispatch_endpoint(&server, instance_id, endpoint_id).await;

    let now = unix_now();
    seed_dispatch_event(&server, instance_id, "evt_created_newer_first", now - 10).await;
    // This eligible event commits after the newer timestamp has already been
    // stored, modeling an audit write whose response or transaction was delayed.
    seed_dispatch_event(
        &server,
        instance_id,
        "evt_committed_late_backdated",
        now - 20,
    )
    .await;

    let (shutdown, dispatcher) = spawn_webhook_dispatcher(&server);
    let deliveries = wait_for_dispatch_completion(
        &server,
        instance_id,
        &["evt_created_newer_first", "evt_committed_late_backdated"],
    )
    .await;
    stop_webhook_dispatcher(shutdown, dispatcher).await;

    assert_eq!(deliveries.len(), 2);
    let mut event_ids: Vec<_> = deliveries
        .iter()
        .map(|delivery| delivery.event_id.as_str())
        .collect();
    event_ids.sort_unstable();
    assert_eq!(
        event_ids,
        ["evt_committed_late_backdated", "evt_created_newer_first"]
    );
    assert!(deliveries.iter().all(|delivery| {
        delivery.endpoint_id == endpoint_id && delivery.instance_id == instance_id
    }));
}

#[tokio::test]
async fn webhook_dispatch_refreshes_endpoints_before_completing_a_pending_event() {
    let server = TestServer::start().await;
    let instance_id = "inst_webhook_endpoint_refresh";
    seed_dispatch_endpoint_at(
        &server,
        instance_id,
        "wh_discovery_snapshot",
        unix_now() - 60,
    )
    .await;
    let mut endpoint_snapshot = server
        ._storage_fixture
        .as_ref()
        .expect("contract fixture is available")
        .pause_next_query_response("webhooks");

    let (shutdown, dispatcher) = spawn_webhook_dispatcher(&server);
    endpoint_snapshot.wait_until_paused().await;

    let endpoint_created_at = unix_now();
    seed_dispatch_endpoint_at(
        &server,
        instance_id,
        "wh_inserted_after_discovery",
        endpoint_created_at,
    )
    .await;
    let event_id = "evt_after_endpoint_insert";
    seed_dispatch_event(&server, instance_id, event_id, endpoint_created_at + 1).await;
    endpoint_snapshot.resume();

    let deliveries = wait_for_deliveries(&server, instance_id, 2).await;
    timeout(Duration::from_secs(6), async {
        loop {
            let event = server
                .state
                .store
                .get::<Value>("audit_events", event_id)
                .await
                .expect("audit dispatch state should be readable")
                .expect("pending audit event should remain stored");
            if event["dispatchPending"] == false {
                break;
            }
            sleep(Duration::from_millis(20)).await;
        }
    })
    .await
    .expect("event marker should clear after both endpoint jobs are stored");
    stop_webhook_dispatcher(shutdown, dispatcher).await;

    assert_eq!(deliveries.len(), 2);
    assert!(deliveries.iter().any(|delivery| {
        delivery.event_id == event_id && delivery.endpoint_id == "wh_discovery_snapshot"
    }));
    assert!(deliveries.iter().any(|delivery| {
        delivery.event_id == event_id && delivery.endpoint_id == "wh_inserted_after_discovery"
    }));
    let completed = server
        .state
        .store
        .get::<Value>("audit_events", event_id)
        .await
        .expect("completed audit dispatch state should be readable")
        .expect("audit event should remain stored");
    assert_eq!(completed["dispatchPending"], false);
}

#[tokio::test]
async fn webhook_dispatch_retry_after_marker_failure_reuses_the_same_delivery_job() {
    let server = TestServer::start().await;
    let instance_id = "inst_audit_marker_retry";
    let endpoint_id = "wh_audit_marker_retry";
    let event_id = "evt_audit_marker_retry";
    seed_dispatch_endpoint(&server, instance_id, endpoint_id).await;
    seed_dispatch_event(&server, instance_id, event_id, unix_now() - 1).await;
    server
        ._storage_fixture
        .as_ref()
        .expect("contract fixture is available")
        .fail_next_compare_and_put_before_commit("audit_events");

    let (shutdown, dispatcher) = spawn_webhook_dispatcher(&server);
    let first_attempt = wait_for_deliveries(&server, instance_id, 1).await;
    assert_eq!(first_attempt.len(), 1);
    assert_eq!(first_attempt[0].event_id, event_id);
    assert_eq!(first_attempt[0].endpoint_id, endpoint_id);
    assert_eq!(first_attempt[0].attempts, 0);
    let pending = server
        .state
        .store
        .get::<Value>("audit_events", event_id)
        .await
        .expect("audit dispatch state should be readable")
        .expect("pending audit event should still exist");
    assert_eq!(pending["dispatchPending"], true);
    let first_delivery_id = first_attempt[0].id.clone();

    let retried = wait_for_dispatch_completion(&server, instance_id, &[event_id]).await;
    stop_webhook_dispatcher(shutdown, dispatcher).await;

    assert_eq!(retried.len(), 1);
    assert_eq!(retried[0].id, first_delivery_id);
    assert_eq!(retried[0].event_id, event_id);
    assert_eq!(retried[0].endpoint_id, endpoint_id);
    let complete = server
        .state
        .store
        .get::<Value>("audit_events", event_id)
        .await
        .expect("completed audit dispatch state should be readable")
        .expect("audit event should remain stored");
    assert_eq!(complete["dispatchPending"], false);
}

fn stripe_signature(secret: &str, payload: &[u8]) -> String {
    let timestamp = unix_now();
    let mut signed = timestamp.to_string().into_bytes();
    signed.push(b'.');
    signed.extend_from_slice(payload);
    let signature = hmac_sha256(secret.as_bytes(), &signed)
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect::<String>();
    format!("t={timestamp},v1={signature}")
}
