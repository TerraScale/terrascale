use auth_app::{
    AuthOptions,
    app::{AppState, build_state_with_mode},
    build_router,
};
use auth_contracts::models::{OAuthClient, Session, User};
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
            .expect("test app listener should bind");
        let address = listener
            .local_addr()
            .expect("test app listener should have an address");
        static NEXT_DATABASE: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1);
        let database = format!(
            "ts-auth-device-{}",
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
        let state = build_state_with_mode(options, true).expect("test state should build");
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

async fn setup(server: &TestServer, http: &Client) -> String {
    let created = create_instance(server, http, "device").await;
    let instance_id = created["instance"]["id"].as_str().unwrap();
    for client_id in ["television", "other-client"] {
        let client = OAuthClient {
            allow_anonymous: false,
            id: format!("client_{client_id}"),
            instance_id: instance_id.into(),
            client_id: client_id.into(),
            client_key: format!("{instance_id}:{client_id}"),
            name: "Living room <TV>".into(),
            client_secret_hash: None,
            redirect_uris: vec!["http://localhost/callback".into()],
            allowed_origins: vec![],
            token_endpoint_auth_method: "none".into(),
            r#public: true,
            created_at: now(),
        };
        server
            .state
            .store
            .create_oauth_client(&client)
            .await
            .unwrap();
    }
    let user: User = serde_json::from_value(json!({"id":"device-user", "instanceId":instance_id, "email":"device@example.test", "emailVerified":true})).unwrap();
    server
        .state
        .store
        .create("users", &user.id, &user)
        .await
        .unwrap();
    let session = Session {
        id: "device-session".into(),
        instance_id: instance_id.into(),
        user_id: user.id,
        auth_epoch: 0,
        token_key: sha256_hex("device-session-secret"),
        status: "active".into(),
        created_at: now(),
        expires_at: now() + 3600,
        last_active_at: None,
        user_agent: None,
        ip_address: None,
    };
    server
        .state
        .store
        .create("sessions", &session.id, &session)
        .await
        .unwrap();
    instance_id.into()
}

async fn issue(server: &TestServer, client: &Client) -> Value {
    let response = client
        .post(format!(
            "{}/device/oauth/device_authorization",
            server.origin
        ))
        .form(&[
            ("client_id", "television"),
            ("scope", "openid email profile"),
        ])
        .send()
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::OK);
    assert_eq!(response.headers()["cache-control"], "no-store");
    response.json().await.unwrap()
}

async fn poll(
    server: &TestServer,
    client: &Client,
    grant: &Value,
    client_id: &str,
) -> reqwest::Response {
    client
        .post(format!("{}/device/oauth/token", server.origin))
        .form(&[
            ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
            ("client_id", client_id),
            ("device_code", grant["device_code"].as_str().unwrap()),
        ])
        .send()
        .await
        .unwrap()
}

async fn approval_page(server: &TestServer, client: &Client, grant: &Value) -> String {
    let response = client
        .get(format!("{}/ui/device/device", server.origin))
        .query(&[("user_code", grant["user_code"].as_str().unwrap())])
        .header("Cookie", "tsauth_session=device-session-secret")
        .send()
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::OK);
    let html = response.text().await.unwrap();
    assert!(html.contains("Living room &lt;TV&gt;"));
    assert!(html.contains("data-on:submit"));
    html.split("name=\"csrf_token\" value=\"")
        .nth(1)
        .unwrap()
        .split('"')
        .next()
        .unwrap()
        .to_owned()
}

async fn decide(
    server: &TestServer,
    client: &Client,
    grant: &Value,
    csrf: &str,
    decision: &str,
) -> reqwest::Response {
    client
        .post(format!("{}/ui/device/device", server.origin))
        .header("Cookie", "tsauth_session=device-session-secret")
        .header("Origin", &server.state.options.public_origin)
        .header("Datastar-Request", "true")
        .form(&[
            ("user_code", grant["user_code"].as_str().unwrap()),
            ("csrf_token", csrf),
            ("decision", decision),
        ])
        .send()
        .await
        .unwrap()
}

async fn edit_grant(
    server: &TestServer,
    instance_id: &str,
    grant: &Value,
    edit: impl FnOnce(&mut Value),
) {
    let code = grant["user_code"].as_str().unwrap().replace('-', "");
    let id = format!("device_{}", sha256_hex(&format!("{instance_id}:{code}")));
    let (mut record, version) = server
        .state
        .store
        .get_versioned::<Value>("oauth_device_grants", &id)
        .await
        .unwrap()
        .unwrap();
    edit(&mut record);
    assert!(
        server
            .state
            .store
            .compare_and_put("oauth_device_grants", &id, &record, &version)
            .await
            .unwrap()
    );
}

#[tokio::test]
async fn device_flow_requires_explicit_bound_approval_and_redeems_once() {
    let server = TestServer::start().await;
    let client = server.client();
    let instance_id = setup(&server, &client).await;
    let metadata: Value = client
        .get(format!(
            "{}/device/.well-known/openid-configuration",
            server.origin
        ))
        .send()
        .await
        .unwrap()
        .json()
        .await
        .unwrap();
    assert!(
        metadata["device_authorization_endpoint"]
            .as_str()
            .unwrap()
            .ends_with("/oauth/device_authorization")
    );
    let grant = issue(&server, &client).await;
    let pending: Value = poll(&server, &client, &grant, "television")
        .await
        .json()
        .await
        .unwrap();
    assert_eq!(pending["error"], "authorization_pending");
    let fast: Value = poll(&server, &client, &grant, "television")
        .await
        .json()
        .await
        .unwrap();
    assert_eq!(fast["error"], "slow_down");
    let wrong: Value = poll(&server, &client, &grant, "other-client")
        .await
        .json()
        .await
        .unwrap();
    assert_eq!(wrong["error"], "invalid_grant");
    let csrf = approval_page(&server, &client, &grant).await;
    assert_eq!(
        decide(&server, &client, &grant, "unbound", "approve")
            .await
            .status(),
        StatusCode::FORBIDDEN
    );
    let response = client
        .post(format!("{}/ui/device/device", server.origin))
        .header("Cookie", "tsauth_session=device-session-secret")
        .header("Origin", "https://attacker.test")
        .form(&[
            ("user_code", grant["user_code"].as_str().unwrap()),
            ("csrf_token", csrf.as_str()),
            ("decision", "approve"),
        ])
        .send()
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::FORBIDDEN);
    let approved = decide(&server, &client, &grant, &csrf, "approve").await;
    assert_eq!(approved.status(), StatusCode::OK);
    assert_eq!(approved.headers()["content-type"], "text/event-stream");
    assert!(
        approved
            .text()
            .await
            .unwrap()
            .contains("datastar-patch-elements")
    );
    assert_eq!(
        decide(&server, &client, &grant, &csrf, "deny")
            .await
            .status(),
        StatusCode::CONFLICT
    );
    edit_grant(&server, &instance_id, &grant, |v| {
        v["lastPolledAt"] = json!(now() - 60)
    })
    .await;
    let (first, second) = tokio::join!(
        poll(&server, &client, &grant, "television"),
        poll(&server, &client, &grant, "television")
    );
    assert_eq!(
        [first.status(), second.status()]
            .into_iter()
            .filter(|s| *s == StatusCode::OK)
            .count(),
        1
    );
    let success = if first.status() == StatusCode::OK {
        first
    } else {
        second
    };
    assert_eq!(success.headers()["cache-control"], "no-store");
    let token: Value = success.json().await.unwrap();
    assert!(token["refresh_token"].is_string());
    assert!(token["id_token"].is_string());
    let profile = client
        .get(format!("{}/device/oauth/userinfo", server.origin))
        .bearer_auth(token["access_token"].as_str().unwrap())
        .send()
        .await
        .unwrap();
    assert_eq!(profile.status(), StatusCode::OK);
    assert_eq!(profile.json::<Value>().await.unwrap()["sub"], "device-user");
    let replay: Value = poll(&server, &client, &grant, "television")
        .await
        .json()
        .await
        .unwrap();
    assert_eq!(replay["error"], "invalid_grant");
}

#[tokio::test]
async fn denial_expiry_and_revoked_approving_sessions_prevent_tokens() {
    let server = TestServer::start().await;
    let client = server.client();
    let instance_id = setup(&server, &client).await;
    let denied = issue(&server, &client).await;
    let csrf = approval_page(&server, &client, &denied).await;
    assert_eq!(
        decide(&server, &client, &denied, &csrf, "deny")
            .await
            .status(),
        StatusCode::OK
    );
    assert_eq!(
        poll(&server, &client, &denied, "television")
            .await
            .json::<Value>()
            .await
            .unwrap()["error"],
        "access_denied"
    );
    let expired = issue(&server, &client).await;
    edit_grant(&server, &instance_id, &expired, |v| {
        v["expiresAt"] = json!(now() - 1)
    })
    .await;
    assert_eq!(
        poll(&server, &client, &expired, "television")
            .await
            .json::<Value>()
            .await
            .unwrap()["error"],
        "expired_token"
    );
    let approved = issue(&server, &client).await;
    let csrf = approval_page(&server, &client, &approved).await;
    assert_eq!(
        decide(&server, &client, &approved, &csrf, "approve")
            .await
            .status(),
        StatusCode::OK
    );
    server
        .state
        .store
        .try_revoke_session("device-session", &instance_id, Some("device-user"))
        .await
        .unwrap();
    assert_eq!(
        poll(&server, &client, &approved, "television")
            .await
            .json::<Value>()
            .await
            .unwrap()["error"],
        "access_denied"
    );
}

#[tokio::test]
#[ignore = "requires TS_AUTH_PLAYWRIGHT_MODULE and TS_AUTH_CHROMIUM_PATH; also run by CI browser acceptance"]
async fn browser_confirms_a_device_with_datastar_without_navigation() {
    let server = TestServer::start().await;
    let client = server.client();
    setup(&server, &client).await;
    let grant = issue(&server, &client).await;
    let script = r#"
import { pathToFileURL } from 'node:url';
const { chromium } = await import(pathToFileURL(process.env.TS_AUTH_PLAYWRIGHT_MODULE).href);
const [origin, code] = process.argv.slice(2);
const browser = await chromium.launch({
  executablePath: process.env.TS_AUTH_CHROMIUM_PATH,
  headless: true,
  timeout: 60000,
  args: ['--no-sandbox', '--disable-dev-shm-usage'],
});
try {
  const context = await browser.newContext();
  await context.addCookies([{name:'tsauth_session', value:'device-session-secret', url:origin, httpOnly:true, sameSite:'Lax'}]);
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.goto(`${origin}/ui/device/device?user_code=${code}`);
  await page.getByRole('heading', {name:'Authorize Living room <TV>'}).waitFor();
  await page.evaluate(() => { window.navigationSentinel = 'original-page'; });
  const responsePromise = page.waitForResponse(r => r.request().method() === 'POST' && r.url().endsWith('/ui/device/device'));
  await page.getByRole('button', {name:'Authorize device', exact:true}).click();
  const response = await responsePromise;
  if (response.status() !== 200 || !response.headers()['content-type'].startsWith('text/event-stream')) throw new Error(`approval failed: ${response.status()} ${await response.text()}`);
  await page.getByRole('heading', {name:'Device authorized', exact:true}).waitFor();
  if (await page.evaluate(() => window.navigationSentinel) !== 'original-page') throw new Error('Approval navigated instead of applying the Datastar patch');
  if (errors.length) throw new Error(errors.join('; '));
  console.log(JSON.stringify({approved:true, datastar:true, errors}));
} finally { await browser.close(); }
"#;
    let path = std::env::temp_dir().join(format!(
        "ts-auth-device-browser-{}.mjs",
        auth_security::tokens::random_token(20)
    ));
    std::fs::write(&path, script).unwrap();
    let origin = server.state.options.public_origin.clone();
    let code = grant["user_code"].as_str().unwrap().to_owned();
    let output = tokio::task::spawn_blocking(move || {
        let output = std::process::Command::new("node")
            .arg(&path)
            .arg(origin)
            .arg(code)
            .output();
        let _ = std::fs::remove_file(path);
        output.unwrap()
    })
    .await
    .unwrap();
    assert!(
        output.status.success(),
        "browser failed: {} {}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    println!("{}", String::from_utf8_lossy(&output.stdout));
    assert_eq!(
        poll(&server, &client, &grant, "television").await.status(),
        StatusCode::OK
    );
}

#[tokio::test]
async fn authorization_codes_and_access_tokens_preserve_session_revocation() {
    use base64::Engine;
    use sha2::{Digest, Sha256};
    let server = TestServer::start().await;
    let client = server.client();
    let instance_id = setup(&server, &client).await;
    let verifier = "a".repeat(64);
    let challenge = base64::engine::general_purpose::URL_SAFE_NO_PAD
        .encode(Sha256::digest(verifier.as_bytes()));
    let response = client
        .get(format!("{}/device/oauth/authorize", server.origin))
        .header("Cookie", "tsauth_session=device-session-secret")
        .query(&[
            ("client_id", "television"),
            ("redirect_uri", "http://localhost/callback"),
            ("response_type", "code"),
            ("code_challenge_method", "S256"),
            ("code_challenge", challenge.as_str()),
        ])
        .send()
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::SEE_OTHER);
    let callback = reqwest::Url::parse(response.headers()["location"].to_str().unwrap()).unwrap();
    let code = callback
        .query_pairs()
        .find(|(key, _)| key == "code")
        .unwrap()
        .1
        .into_owned();
    let device = issue(&server, &client).await;
    let csrf = approval_page(&server, &client, &device).await;
    assert_eq!(
        decide(&server, &client, &device, &csrf, "approve")
            .await
            .status(),
        StatusCode::OK
    );
    let tokens: Value = poll(&server, &client, &device, "television")
        .await
        .json()
        .await
        .unwrap();
    server
        .state
        .store
        .try_revoke_session("device-session", &instance_id, Some("device-user"))
        .await
        .unwrap();
    let redeemed = client
        .post(format!("{}/device/oauth/token", server.origin))
        .form(&[
            ("client_id", "television"),
            ("grant_type", "authorization_code"),
            ("code", code.as_str()),
            ("redirect_uri", "http://localhost/callback"),
            ("code_verifier", verifier.as_str()),
        ])
        .send()
        .await
        .unwrap();
    assert_eq!(redeemed.status(), StatusCode::BAD_REQUEST);
    assert_eq!(
        redeemed.json::<Value>().await.unwrap()["error"],
        "invalid_grant"
    );
    let profile = client
        .get(format!("{}/device/oauth/userinfo", server.origin))
        .bearer_auth(tokens["access_token"].as_str().unwrap())
        .send()
        .await
        .unwrap();
    assert_eq!(profile.status(), StatusCode::UNAUTHORIZED);
}

#[tokio::test]
async fn sign_out_everywhere_revokes_oauth_refresh_tokens_too() {
    let server = TestServer::start().await;
    let client = server.client();
    setup(&server, &client).await;
    let device = issue(&server, &client).await;
    let csrf = approval_page(&server, &client, &device).await;
    assert_eq!(
        decide(&server, &client, &device, &csrf, "approve")
            .await
            .status(),
        StatusCode::OK
    );
    let tokens: Value = poll(&server, &client, &device, "television")
        .await
        .json()
        .await
        .unwrap();
    let response = client
        .post(format!(
            "{}/ui/device/account/sessions/revoke-all",
            server.origin
        ))
        .header("Cookie", "tsauth_session=device-session-secret")
        .header("Origin", &server.state.options.public_origin)
        .send()
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::SEE_OTHER);
    let refreshed = client
        .post(format!("{}/device/oauth/token", server.origin))
        .form(&[
            ("client_id", "television"),
            ("grant_type", "refresh_token"),
            ("refresh_token", tokens["refresh_token"].as_str().unwrap()),
        ])
        .send()
        .await
        .unwrap();
    assert_eq!(refreshed.status(), StatusCode::BAD_REQUEST);
    assert_eq!(
        refreshed.json::<Value>().await.unwrap()["error"],
        "invalid_grant"
    );
    let profile = client
        .get(format!("{}/device/oauth/userinfo", server.origin))
        .bearer_auth(tokens["access_token"].as_str().unwrap())
        .send()
        .await
        .unwrap();
    assert_eq!(profile.status(), StatusCode::UNAUTHORIZED);
}
