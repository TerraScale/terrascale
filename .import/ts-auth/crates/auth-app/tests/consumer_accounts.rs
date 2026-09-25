use auth_app::{
    AuthOptions,
    app::{AppState, build_state_with_mode},
    build_router,
    features::email::EmailSender,
};
use auth_contracts::models::User;
use reqwest::{Client, StatusCode};
use serde_json::{Value, json};
use tokio::{net::TcpListener, task::JoinHandle};
mod support;
use support::terrabase_http::{TEST_TERRASCALE_API_KEY, TerraBaseHttpFixture};
#[derive(Default)]
struct Mailbox(std::sync::Mutex<Vec<(String, String)>>);
impl EmailSender for Mailbox {
    fn send<'a>(
        &'a self,
        to: &'a str,
        _: &'a str,
        body: &'a str,
    ) -> std::pin::Pin<Box<dyn std::future::Future<Output = ()> + Send + 'a>> {
        Box::pin(async move {
            self.0.lock().unwrap().push((to.into(), body.into()));
        })
    }
}
struct TestServer {
    origin: String,
    state: AppState,
    mail: std::sync::Arc<Mailbox>,
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
        static NEXT_DATABASE: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1);
        let database = format!(
            "ts-auth-consumer-{}",
            NEXT_DATABASE.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
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
        let mut state = build_state_with_mode(options, true).expect("test state should build");
        let mail = std::sync::Arc::new(Mailbox::default());
        state.email = mail.clone();
        let router = build_router(state.clone());
        let task = tokio::spawn(async move {
            axum::serve(listener, router)
                .await
                .expect("test app should remain available");
        });
        Self {
            origin: format!("http://{address}"),
            state,
            mail,
            task,
            _storage: storage,
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

async fn create_instance(server: &TestServer, client: &Client, slug: &str) -> Value {
    let response = client
        .post(format!("{}/v1/instances", server.origin))
        .bearer_auth("test-only-operator-key")
        .header("Idempotency-Key", format!("federation-{slug}"))
        .json(&json!({ "slug": slug, "name": format!("{slug} test") }))
        .send()
        .await
        .expect("instance creation should reach the service");
    assert_eq!(response.status(), StatusCode::OK);
    response
        .json()
        .await
        .expect("instance response should be JSON")
}

async fn setup(server: &TestServer, client: &Client) -> (String, String, String) {
    let instance = create_instance(server, client, "consumer").await;
    let key = instance["api_key"].as_str().unwrap();
    let response = client.post(format!("{}/v1/consumer/oauth-clients", server.origin)).bearer_auth(key).json(&json!({"name":"Mobile app", "public":true, "redirect_uris":["http://localhost/callback"], "allow_anonymous":true, "allowed_origins":["https://app.example.test"]})).send().await.unwrap();
    assert_eq!(response.status(), StatusCode::OK);
    let body: Value = response.json().await.unwrap();
    (
        body["client"]["client_id"].as_str().unwrap().into(),
        body["client"]["id"].as_str().unwrap().into(),
        key.into(),
    )
}

async fn sign_in(server: &TestServer, client: &Client, id: &str) -> Value {
    let response = client
        .post(format!("{}/consumer/oauth/anonymous", server.origin))
        .header("Origin", "https://app.example.test")
        .form(&[("client_id", id)])
        .send()
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::OK);
    assert_eq!(response.headers()["cache-control"], "no-store");
    assert_eq!(
        response.headers()["access-control-allow-origin"],
        "https://app.example.test"
    );
    response.json().await.unwrap()
}

async fn profile(server: &TestServer, client: &Client, tokens: &Value) -> reqwest::Response {
    client
        .get(format!("{}/consumer/oauth/userinfo", server.origin))
        .bearer_auth(tokens["access_token"].as_str().unwrap())
        .send()
        .await
        .unwrap()
}

async fn challenge(
    server: &TestServer,
    client: &Client,
    tokens: &Value,
    email: &str,
) -> (String, String) {
    let response = client
        .post(format!(
            "{}/consumer/oauth/anonymous/upgrade",
            server.origin
        ))
        .bearer_auth(tokens["access_token"].as_str().unwrap())
        .json(&json!({"email":email}))
        .send()
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::OK);
    let body: Value = response.json().await.unwrap();
    let mail = server.mail.0.lock().unwrap();
    let text = &mail.iter().rev().find(|(to, _)| to == email).unwrap().1;
    let code = text
        .split("code is ")
        .nth(1)
        .unwrap()
        .split('.')
        .next()
        .unwrap()
        .to_owned();
    (body["verification_id"].as_str().unwrap().into(), code)
}

async fn complete(
    server: &TestServer,
    client: &Client,
    tokens: &Value,
    id: &str,
    code: &str,
) -> reqwest::Response {
    client
        .post(format!(
            "{}/consumer/oauth/anonymous/upgrade/complete",
            server.origin
        ))
        .bearer_auth(tokens["access_token"].as_str().unwrap())
        .json(&json!({"verification_id":id, "code":code}))
        .send()
        .await
        .unwrap()
}

#[tokio::test]
async fn guest_conversion_preserves_subject_data_and_invalidates_guest_credentials() {
    let server = TestServer::start().await;
    let client = server.client();
    let (id, _, _) = setup(&server, &client).await;
    let tokens = sign_in(&server, &client, &id).await;
    let before: Value = profile(&server, &client, &tokens)
        .await
        .json()
        .await
        .unwrap();
    assert_eq!(before["is_anonymous"], true);
    let user_id = before["sub"].as_str().unwrap();
    let mut user = server.state.store.get_user(user_id).await.unwrap().unwrap();
    user.public_metadata.insert("cart".into(), json!(["book"]));
    server.state.store.upsert_user(&user).await.unwrap();
    let (proof, code) = challenge(&server, &client, &tokens, "person@example.test").await;
    let response = complete(&server, &client, &tokens, &proof, &code).await;
    assert_eq!(response.status(), StatusCode::OK);
    let upgraded: Value = response.json().await.unwrap();
    let after: Value = profile(&server, &client, &upgraded)
        .await
        .json()
        .await
        .unwrap();
    assert_eq!(after["sub"], before["sub"]);
    assert_eq!(after["is_anonymous"], false);
    assert_eq!(after["email"], "person@example.test");
    assert_eq!(after["email_verified"], true);
    let user = server.state.store.get_user(user_id).await.unwrap().unwrap();
    assert_eq!(user.public_metadata["cart"], json!(["book"]));
    assert_eq!(user.auth_epoch, 1);
    assert_eq!(
        profile(&server, &client, &tokens).await.status(),
        StatusCode::UNAUTHORIZED
    );
    let old_refresh = client
        .post(format!("{}/consumer/oauth/token", server.origin))
        .form(&[
            ("client_id", id.as_str()),
            ("grant_type", "refresh_token"),
            ("refresh_token", tokens["refresh_token"].as_str().unwrap()),
        ])
        .send()
        .await
        .unwrap();
    assert_eq!(old_refresh.status(), StatusCode::BAD_REQUEST);
    assert_eq!(
        complete(&server, &client, &tokens, &proof, &code)
            .await
            .status(),
        StatusCode::UNAUTHORIZED
    );
}

#[tokio::test]
async fn guest_proofs_are_user_bound_attempt_limited_and_never_merge_existing_accounts() {
    let server = TestServer::start().await;
    let client = server.client();
    let (id, _, _) = setup(&server, &client).await;
    let first = sign_in(&server, &client, &id).await;
    let second = sign_in(&server, &client, &id).await;
    let (proof, code) = challenge(&server, &client, &first, "shared@example.test").await;
    assert_eq!(
        complete(&server, &client, &second, &proof, &code)
            .await
            .status(),
        StatusCode::BAD_REQUEST
    );
    for _ in 0..5 {
        let wrong = if code == "00000000" {
            "11111111"
        } else {
            "00000000"
        };
        assert_eq!(
            complete(&server, &client, &first, &proof, wrong)
                .await
                .status(),
            StatusCode::BAD_REQUEST
        );
    }
    assert_eq!(
        complete(&server, &client, &first, &proof, &code)
            .await
            .status(),
        StatusCode::BAD_REQUEST
    );
    let (proof, code) = challenge(&server, &client, &second, "shared@example.test").await;
    let converted = complete(&server, &client, &second, &proof, &code).await;
    assert_eq!(converted.status(), StatusCode::OK);
    let (proof, code) = challenge(&server, &client, &first, "shared@example.test").await;
    assert_eq!(
        complete(&server, &client, &first, &proof, &code)
            .await
            .status(),
        StatusCode::CONFLICT
    );
    let still_guest: Value = profile(&server, &client, &first)
        .await
        .json()
        .await
        .unwrap();
    assert_eq!(still_guest["is_anonymous"], true);
}

#[tokio::test]
async fn browser_origins_and_client_opt_in_are_explicit_and_revocable() {
    let server = TestServer::start().await;
    let client = server.client();
    let (id, row_id, key) = setup(&server, &client).await;
    let url = format!("{}/consumer/oauth/anonymous", server.origin);
    let preflight = client
        .request(reqwest::Method::OPTIONS, &url)
        .header("Origin", "https://app.example.test")
        .header("Access-Control-Request-Method", "POST")
        .header(
            "Access-Control-Request-Headers",
            "authorization, content-type",
        )
        .send()
        .await
        .unwrap();
    assert_eq!(preflight.status(), StatusCode::NO_CONTENT);
    assert!(
        preflight
            .headers()
            .get("Access-Control-Allow-Credentials")
            .is_none()
    );
    let denied = client
        .post(&url)
        .header("Origin", "https://attacker.test")
        .form(&[("client_id", id.as_str())])
        .send()
        .await
        .unwrap();
    assert_eq!(denied.status(), StatusCode::FORBIDDEN);
    assert!(
        denied
            .headers()
            .get("Access-Control-Allow-Origin")
            .is_none()
    );
    let invalid = client
        .patch(format!(
            "{}/v1/consumer/oauth-clients/{row_id}",
            server.origin
        ))
        .bearer_auth(&key)
        .json(&json!({"allowed_origins":["https://app.example.test/path"]}))
        .send()
        .await
        .unwrap();
    assert_eq!(invalid.status(), StatusCode::BAD_REQUEST);
    let disabled = client
        .patch(format!(
            "{}/v1/consumer/oauth-clients/{row_id}",
            server.origin
        ))
        .bearer_auth(&key)
        .json(&json!({"allow_anonymous":false}))
        .send()
        .await
        .unwrap();
    assert_eq!(disabled.status(), StatusCode::OK);
    assert_eq!(
        client
            .post(&url)
            .form(&[("client_id", id.as_str())])
            .send()
            .await
            .unwrap()
            .status(),
        StatusCode::FORBIDDEN
    );
    let users: Vec<User> = server
        .state
        .store
        .query_all("users", &[], 100)
        .await
        .unwrap();
    assert!(
        users.is_empty(),
        "denied admissions must not allocate users or quota seats"
    );
}

#[tokio::test]
async fn concurrent_guest_conversions_cannot_commit_two_mailboxes_or_token_sets() {
    let server = TestServer::start().await;
    let client = server.client();
    let (id, _, _) = setup(&server, &client).await;
    let tokens = sign_in(&server, &client, &id).await;
    let (first_id, first_code) = challenge(&server, &client, &tokens, "first@example.test").await;
    let (second_id, second_code) =
        challenge(&server, &client, &tokens, "second@example.test").await;
    let (first, second) = tokio::join!(
        complete(&server, &client, &tokens, &first_id, &first_code),
        complete(&server, &client, &tokens, &second_id, &second_code)
    );
    assert_eq!(
        [first.status(), second.status()]
            .into_iter()
            .filter(|s| *s == StatusCode::OK)
            .count(),
        1
    );
    let (success, loser) = if first.status() == StatusCode::OK {
        (first, second)
    } else {
        (second, first)
    };
    assert!(matches!(
        loser.status(),
        StatusCode::CONFLICT | StatusCode::UNAUTHORIZED
    ));
    let upgraded: Value = success.json().await.unwrap();
    assert_eq!(
        profile(&server, &client, &upgraded).await.status(),
        StatusCode::OK
    );
    assert_eq!(
        profile(&server, &client, &tokens).await.status(),
        StatusCode::UNAUTHORIZED
    );
}
