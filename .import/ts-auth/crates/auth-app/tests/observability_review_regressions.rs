use std::time::Duration;

use auth_app::{
    AuthOptions,
    app::{build_router, build_state_with_mode},
};
use reqwest::{Client, StatusCode};
use tokio::{
    net::TcpListener,
    sync::oneshot,
    task::JoinHandle,
    time::{sleep, timeout},
};

mod support;

use support::terrabase_http::{TEST_TERRASCALE_API_KEY, TerraBaseHttpFixture};

const METRICS_KEY: &str = "test-only-metrics-key";
const READY_CACHE_REFRESH: Duration = Duration::from_millis(1_100);

struct TestServer {
    origin: String,
    task: JoinHandle<()>,
    fixture: TerraBaseHttpFixture,
}

impl TestServer {
    async fn start() -> Self {
        Self::start_with_metrics_key(Some(METRICS_KEY.to_owned())).await
    }

    async fn start_with_metrics_key(metrics_key: Option<String>) -> Self {
        let fixture = TerraBaseHttpFixture::start().await;
        let listener = TcpListener::bind("127.0.0.1:0")
            .await
            .expect("test listener should bind");
        let address = listener
            .local_addr()
            .expect("test listener should have an address");
        let mut options = test_options(
            fixture.origin.clone(),
            "observability-review-regressions".to_owned(),
            format!("http://localhost:{}", address.port()),
        );
        options.metrics_key = metrics_key;
        let state = build_state_with_mode(options, true).expect("test state should be valid");
        let router = build_router(state.clone());
        let task = tokio::spawn(async move {
            axum::serve(listener, router)
                .await
                .expect("test server should remain available");
        });

        Self {
            origin: format!("http://{address}"),
            task,
            fixture,
        }
    }

    fn client(&self) -> Client {
        Client::builder()
            .no_proxy()
            .build()
            .expect("test client should build")
    }
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
        metrics_key: Some(METRICS_KEY.to_owned()),
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

async fn ready_status(client: &Client, origin: &str) -> StatusCode {
    client
        .get(format!("{origin}/readyz"))
        .send()
        .await
        .expect("readiness request should reach the app")
        .status()
}

async fn scrape(client: &Client, origin: &str) -> (StatusCode, String) {
    let response = client
        .get(format!("{origin}/metrics"))
        .header("X-TerraScale-Metrics-Key", METRICS_KEY)
        .send()
        .await
        .expect("metrics request should reach the app");
    let status = response.status();
    let body = response
        .text()
        .await
        .expect("metrics body should be readable");
    (status, body)
}

#[tokio::test]
async fn metrics_auth_and_exposition_keep_the_documented_contract() {
    let server = TestServer::start().await;
    let client = server.client();

    let health = client
        .get(format!("{}/healthz", server.origin))
        .send()
        .await
        .expect("liveness request should reach the app");
    assert_eq!(health.status(), StatusCode::OK);

    let expected_len = METRICS_KEY.len();
    for presented in [
        String::new(),
        "wrong-key".to_owned(),
        "x".repeat(expected_len.saturating_sub(1)),
        "x".repeat(expected_len),
        "x".repeat(expected_len + 1),
    ] {
        let response = client
            .get(format!("{}/metrics", server.origin))
            .header("X-TerraScale-Metrics-Key", presented)
            .send()
            .await
            .expect("invalid-key metrics request should reach the app");
        assert_eq!(response.status(), StatusCode::UNAUTHORIZED);
    }

    let (status, body) = scrape(&client, &server.origin).await;
    assert_eq!(status, StatusCode::OK);
    assert!(body.contains("ts_auth_liveness 1\n"));
    assert!(body.contains("ts_auth_ready 0\n"));
    assert!(body.contains("ts_auth_dependency_ready{dependency=\"terrabase\"} 0\n"));
    assert!(body.contains("latest completed /readyz observation found the process ready"));
    assert!(
        body.contains("latest completed /readyz observation found a required dependency ready")
    );
    assert!(!body.contains("ts_auth_events_total"));
    assert!(body.contains("ts_auth_http_requests_total{method=\"GET\",status_class=\"2xx\"}"));

    assert_eq!(ready_status(&client, &server.origin).await, StatusCode::OK);
    let (status, body) = scrape(&client, &server.origin).await;
    assert_eq!(status, StatusCode::OK);
    assert!(body.contains("ts_auth_ready 1\n"));
    assert!(body.contains("ts_auth_dependency_ready{dependency=\"terrabase\"} 1\n"));
}

#[tokio::test]
async fn metrics_fail_closed_when_no_key_is_configured() {
    let server = TestServer::start_with_metrics_key(None).await;
    let client = server.client();
    let response = client
        .get(format!("{}/metrics", server.origin))
        .header("X-TerraScale-Metrics-Key", METRICS_KEY)
        .send()
        .await
        .expect("metrics request should reach the app");
    assert_eq!(response.status(), StatusCode::UNAUTHORIZED);
}

#[tokio::test]
async fn healthz_stays_live_when_dependency_readiness_fails() {
    let server = TestServer::start().await;
    let client = server.client();
    server.fixture.set_health_healthy(false);

    assert_eq!(
        ready_status(&client, &server.origin).await,
        StatusCode::SERVICE_UNAVAILABLE
    );
    let health = client
        .get(format!("{}/healthz", server.origin))
        .send()
        .await
        .expect("liveness request should reach the app while TerraBase is unhealthy");
    assert_eq!(health.status(), StatusCode::OK);

    let (status, body) = scrape(&client, &server.origin).await;
    assert_eq!(status, StatusCode::OK);
    assert!(body.contains("ts_auth_liveness 1\n"));
    assert!(body.contains("ts_auth_ready 0\n"));
    assert!(body.contains("ts_auth_dependency_ready{dependency=\"terrabase\"} 0\n"));
}

#[tokio::test]
async fn readiness_coalesces_concurrent_probes_and_caches_both_outcomes() {
    let server = TestServer::start().await;
    let client = server.client();
    let mut paused = server.fixture.pause_next_health_check();
    let origin = server.origin.clone();
    let first_client = client.clone();
    let first_origin = origin.clone();
    let first = tokio::spawn(async move { ready_status(&first_client, &first_origin).await });
    paused.wait_until_paused().await;
    assert_eq!(server.fixture.health_request_count(), 1);

    let mut started = Vec::new();
    let mut waiters = Vec::new();
    for _ in 0..8 {
        let (started_tx, started_rx) = oneshot::channel();
        let waiter_client = client.clone();
        let waiter_origin = origin.clone();
        started.push(started_rx);
        waiters.push(tokio::spawn(async move {
            let _ = started_tx.send(());
            ready_status(&waiter_client, &waiter_origin).await
        }));
    }
    for started_rx in started {
        started_rx.await.expect("waiter should begin its request");
    }
    sleep(Duration::from_millis(30)).await;
    assert_eq!(server.fixture.health_request_count(), 1);

    paused.resume();
    assert_eq!(first.await.expect("leader should finish"), StatusCode::OK);
    for waiter in waiters {
        assert_eq!(waiter.await.expect("waiter should finish"), StatusCode::OK);
    }
    assert_eq!(server.fixture.health_request_count(), 1);

    server.fixture.set_health_healthy(false);
    assert_eq!(ready_status(&client, &origin).await, StatusCode::OK);
    assert_eq!(server.fixture.health_request_count(), 1);
    sleep(READY_CACHE_REFRESH).await;
    assert_eq!(
        ready_status(&client, &origin).await,
        StatusCode::SERVICE_UNAVAILABLE
    );
    assert_eq!(server.fixture.health_request_count(), 2);

    server.fixture.set_health_healthy(true);
    assert_eq!(
        ready_status(&client, &origin).await,
        StatusCode::SERVICE_UNAVAILABLE
    );
    assert_eq!(server.fixture.health_request_count(), 2);
    sleep(READY_CACHE_REFRESH).await;
    assert_eq!(ready_status(&client, &origin).await, StatusCode::OK);
    assert_eq!(server.fixture.health_request_count(), 3);
}

#[tokio::test]
async fn readiness_timeout_is_cached_and_caller_cancellation_does_not_strand_waiters() {
    let server = TestServer::start().await;
    let client = server.client();
    let mut paused = server.fixture.pause_next_health_check();
    let origin = server.origin.clone();
    let first_client = client.clone();
    let first_origin = origin.clone();
    let canceled = tokio::spawn(async move { ready_status(&first_client, &first_origin).await });
    paused.wait_until_paused().await;

    let waiter_client = client.clone();
    let waiter_origin = origin.clone();
    let waiter = tokio::spawn(async move { ready_status(&waiter_client, &waiter_origin).await });
    tokio::task::yield_now().await;
    canceled.abort();
    let _ = canceled.await;

    let status = timeout(Duration::from_secs(3), waiter)
        .await
        .expect("timed out readiness probe should release waiters")
        .expect("readiness waiter should not panic");
    assert_eq!(status, StatusCode::SERVICE_UNAVAILABLE);
    assert_eq!(server.fixture.health_request_count(), 1);
    assert_eq!(
        ready_status(&client, &origin).await,
        StatusCode::SERVICE_UNAVAILABLE
    );
    assert_eq!(server.fixture.health_request_count(), 1);

    paused.resume();
    sleep(READY_CACHE_REFRESH).await;
    assert_eq!(ready_status(&client, &origin).await, StatusCode::OK);
    assert_eq!(server.fixture.health_request_count(), 2);
}
