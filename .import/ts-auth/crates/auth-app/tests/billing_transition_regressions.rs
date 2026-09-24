use auth_app::{
    AuthOptions,
    app::{build_router, build_state_with_mode},
};
use auth_contracts::models::Subscription;
use auth_security::tokens::hmac_sha256;
use axum::{
    Json, Router,
    extract::{Path, State},
    routing::get,
};
use reqwest::{Client, StatusCode, header};
use serde_json::{Value, json};
use std::{
    collections::VecDeque,
    sync::{
        Arc, Mutex,
        atomic::{AtomicUsize, Ordering},
    },
    time::{Duration, SystemTime, UNIX_EPOCH},
};
use tokio::{net::TcpListener, task::JoinHandle, time::sleep};

mod support;

use support::terrabase_http::{TEST_TERRASCALE_API_KEY, TerraBaseHttpFixture};

const OPERATOR_KEY: &str = "billing-transition-operator-key";
const WEBHOOK_SECRET: &str = "whsec_billing_transition_fixture";
const STRIPE_CUSTOMER: &str = "cus_billing_transition";
const STRIPE_SUBSCRIPTION: &str = "sub_billing_transition";

struct TestServer {
    origin: String,
    state: auth_app::AppState,
    task: JoinHandle<()>,
    _storage: TerraBaseHttpFixture,
}

impl TestServer {
    async fn start(storage: TerraBaseHttpFixture, stripe_url: String) -> Self {
        let listener = TcpListener::bind("127.0.0.1:0")
            .await
            .expect("application test listener should bind");
        let address = listener
            .local_addr()
            .expect("application test listener should have an address");
        static NEXT_DATABASE: AtomicUsize = AtomicUsize::new(1);
        let database = format!(
            "ts-auth-billing-transition-{}",
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
            stripe_secret_key: Some("sk_test_billing_transition".into()),
            stripe_webhook_secret: Some(WEBHOOK_SECRET.into()),
            stripe_api_url: stripe_url,
            price_pro: Some("price_pro_configured".into()),
            price_enterprise: Some("price_enterprise_configured".into()),
            smtp_host: None,
            smtp_port: 587,
            smtp_username: None,
            smtp_password: None,
            smtp_from_address: None,
        };
        let state = build_state_with_mode(options, true)
            .expect("billing transition test state should build");
        let router = build_router(state.clone());
        let task = tokio::spawn(async move {
            axum::serve(listener, router)
                .await
                .expect("billing transition application should remain available");
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
            .expect("billing transition client should build")
    }
}

impl Drop for TestServer {
    fn drop(&mut self) {
        self.task.abort();
    }
}

#[derive(Clone)]
struct StripeState {
    prices: Arc<Mutex<VecDeque<String>>>,
    requests: Arc<AtomicUsize>,
}

struct StripeFixture {
    origin: String,
    task: JoinHandle<()>,
    prices: Arc<Mutex<VecDeque<String>>>,
    requests: Arc<AtomicUsize>,
}

impl StripeFixture {
    async fn start() -> Self {
        let prices = Arc::new(Mutex::new(VecDeque::new()));
        let requests = Arc::new(AtomicUsize::new(0));
        let app = Router::new()
            .route("/v1/subscriptions/{id}", get(stripe_subscription))
            .with_state(StripeState {
                prices: prices.clone(),
                requests: requests.clone(),
            });
        let listener = TcpListener::bind("127.0.0.1:0")
            .await
            .expect("Stripe fixture should bind");
        let address = listener
            .local_addr()
            .expect("Stripe fixture should have an address");
        let task = tokio::spawn(async move {
            axum::serve(listener, app)
                .await
                .expect("Stripe fixture should remain available");
        });
        Self {
            origin: format!("http://{address}"),
            task,
            prices,
            requests,
        }
    }

    fn queue_price(&self, price: &str) {
        self.prices
            .lock()
            .expect("Stripe fixture price queue should be available")
            .push_back(price.to_owned());
    }

    fn request_count(&self) -> usize {
        self.requests.load(Ordering::SeqCst)
    }
}

impl Drop for StripeFixture {
    fn drop(&mut self) {
        self.task.abort();
    }
}

async fn stripe_subscription(
    State(state): State<StripeState>,
    Path(id): Path<String>,
) -> Json<Value> {
    state.requests.fetch_add(1, Ordering::SeqCst);
    let price = state
        .prices
        .lock()
        .expect("Stripe fixture price queue should be available")
        .pop_front()
        .expect("test should queue each Stripe subscription response");
    Json(json!({
        "id": id,
        "customer": STRIPE_CUSTOMER,
        "status": "active",
        "current_period_end": 2_000_000_000_i64,
        "items": { "data": [{ "price": { "id": price } }] },
    }))
}

#[tokio::test]
async fn downgrade_gate_blocks_admission_and_retry_replays_exact_candidate_before_refetch() {
    let storage = TerraBaseHttpFixture::start().await;
    let stripe = StripeFixture::start().await;
    stripe.queue_price("price_not_configured");
    stripe.queue_price("price_not_configured");
    stripe.queue_price("price_not_configured");
    let server = TestServer::start(storage, stripe.origin.clone()).await;
    let client = server.client();

    let bootstrap = client
        .post(format!("{}/v1/instances", server.origin))
        .bearer_auth(OPERATOR_KEY)
        .header("Idempotency-Key", "billing-transition-bootstrap")
        .json(&json!({ "slug": "billing-transition", "name": "Billing Transition" }))
        .send()
        .await
        .expect("instance creation should reach the app");
    assert_eq!(bootstrap.status(), StatusCode::OK);
    let bootstrap: Value = bootstrap
        .json()
        .await
        .expect("instance creation response should be JSON");
    let instance_id = bootstrap["instance"]["id"]
        .as_str()
        .expect("instance ID should be returned")
        .to_owned();
    let api_key = bootstrap["api_key"]
        .as_str()
        .expect("management API key should be returned")
        .to_owned();

    let now = unix_now();
    let subscription = Subscription {
        id: "sub_billing_transition_row".into(),
        instance_id: instance_id.clone(),
        stripe_customer_id: STRIPE_CUSTOMER.into(),
        stripe_subscription_id: Some(STRIPE_SUBSCRIPTION.into()),
        plan: "pro".into(),
        status: "active".into(),
        current_period_end: Some(2_000_000_000),
        created_at: now - 100,
        updated_at: now - 100,
        last_stripe_event_created: Some(now - 100),
        last_stripe_event_id: Some("evt_before_billing_transition".into()),
    };
    server
        .state
        .store
        .create("subscriptions", &subscription.id, &subscription)
        .await
        .expect("initial Pro subscription should be seeded");

    // These live identities were admitted under Pro and keep high seat numbers
    // after the plan's effective cap falls to Free's 100 users.
    for index in 0..101 {
        let id = format!("usr_grandfathered_{index:03}");
        server
            .state
            .store
            .create(
                "users",
                &id,
                &json!({
                    "id": id,
                    "instanceId": instance_id,
                    "deleted": false,
                    "quotaSeat": 10_000 + index,
                    "createdAt": now - 50,
                    "updatedAt": now - 50,
                }),
            )
            .await
            .expect("grandfathered user should be seeded");
    }

    let payload = serde_json::to_vec(&json!({
        "id": "evt_billing_transition_downgrade",
        "type": "customer.subscription.updated",
        "created": now,
        "data": { "object": {
            "id": STRIPE_SUBSCRIPTION,
            "customer": STRIPE_CUSTOMER,
            "status": "active",
            "current_period_end": 2_000_000_000_i64,
            "items": { "data": [{ "price": { "id": "price_not_configured" } }] },
        }}
    }))
    .expect("downgrade event should serialize");
    let endpoint = format!("{}/v1/billing/webhook", server.origin);
    let signature = stripe_signature(WEBHOOK_SECRET, &payload);

    // The first subscription CAS gets a transient failure before commit. The
    // handler must retain the lowered cap because its write result was uncertain.
    server
        ._storage
        .fail_next_compare_and_put_before_commit("subscriptions");
    let failed = client
        .post(&endpoint)
        .header("Stripe-Signature", &signature)
        .header(header::CONTENT_TYPE, "application/json")
        .body(payload.clone())
        .send()
        .await
        .expect("first downgrade event should reach the service");
    assert_eq!(failed.status(), StatusCode::INTERNAL_SERVER_ERROR);

    let pending_subscription: Subscription = server
        .state
        .store
        .get("subscriptions", &subscription.id)
        .await
        .expect("subscription should remain readable")
        .expect("subscription row should remain present");
    assert_eq!(pending_subscription.plan, "pro");
    let (admitted_users, gate_cap) = server
        .state
        .store
        .quota_admission_status(&instance_id, 10_000)
        .await
        .expect("pending quota gate should be readable");
    assert_eq!(admitted_users, 101);
    assert_eq!(gate_cap, 100);

    let enforced = client
        .post(format!(
            "{}/v1/billing-transition/billing/enforce-check",
            server.origin
        ))
        .bearer_auth(&api_key)
        .json(&json!({ "current_users": 0 }))
        .send()
        .await
        .expect("advisory quota check should reach the service");
    assert_eq!(enforced.status(), StatusCode::OK);
    let enforced: Value = enforced
        .json()
        .await
        .expect("quota check response should be JSON");
    assert_eq!(enforced["allowed"], false);
    assert_eq!(enforced["plan"], "pro");
    assert_eq!(enforced["quota"], 100);
    assert_eq!(enforced["plan_quota"], 10_000);

    let create_user = client
        .post(format!("{}/v1/billing-transition/users", server.origin))
        .bearer_auth(&api_key)
        .json(&json!({ "email": "new-after-downgrade@example.test" }))
        .send()
        .await
        .expect("user create request should reach the service");
    assert_eq!(create_user.status(), StatusCode::PAYMENT_REQUIRED);
    let create_user: Value = create_user
        .json()
        .await
        .expect("quota rejection should be JSON");
    assert_eq!(create_user["error"]["code"], "quota_exceeded");

    // Make observed_at differ from the pinned candidate. The retry must write
    // the exact stored row, then fetch Stripe again before it acknowledges.
    sleep(Duration::from_secs(1)).await;
    let retried = client
        .post(&endpoint)
        .header("Stripe-Signature", &signature)
        .header(header::CONTENT_TYPE, "application/json")
        .body(payload)
        .send()
        .await
        .expect("downgrade retry should reach the service");
    assert_eq!(retried.status(), StatusCode::OK);
    assert_eq!(stripe.request_count(), 3);

    let final_subscription: Subscription = server
        .state
        .store
        .get("subscriptions", &subscription.id)
        .await
        .expect("final subscription should be readable")
        .expect("subscription row should remain present");
    assert_eq!(final_subscription.plan, "free");
    let (admitted_users, gate_cap) = server
        .state
        .store
        .quota_admission_status(&instance_id, 100)
        .await
        .expect("settled quota gate should be readable");
    assert_eq!(admitted_users, 101);
    assert_eq!(gate_cap, 100);
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

fn unix_now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("system clock should be after UNIX epoch")
        .as_secs() as i64
}
