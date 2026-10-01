//! Explicit browser acceptance for the hosted admin Datastar interaction.
//!
//! This test uses the local cached Playwright and Chromium binaries rather than
//! adding browser-runtime dependencies to the normal CI test target. Run it with:
//!
//! ```text
//! TS_AUTH_PLAYWRIGHT_MODULE=/path/to/playwright/index.mjs \
//! TS_AUTH_CHROMIUM_PATH=/path/to/chromium \
//! mise exec -- cargo test --locked -p auth-app --test admin_browser -- --ignored --nocapture
//! ```

use std::{
    fs,
    path::PathBuf,
    process::Command,
    sync::atomic::{AtomicU64, Ordering},
    time::{SystemTime, UNIX_EPOCH},
};

use auth_app::{
    AuthOptions,
    app::{AppState, build_router, build_state_with_mode},
};
use auth_security::tokens::{random_token, sha256_hex};
use reqwest::{Client, StatusCode, header};
use serde_json::{Value, json};
use tokio::{net::TcpListener, task::JoinHandle};

mod support;

use support::terrabase_http::{TEST_TERRASCALE_API_KEY, TerraBaseHttpFixture};

const SLUG: &str = "admin-browser-test";
const SESSION_TOKEN: &str = "admin-browser-session-token";
const USER_ID: &str = "usr_admin_browser";
const ORGANIZATION_ID: &str = "org_admin_browser";

const BROWSER_DRIVER: &str = r#"
import { pathToFileURL } from 'node:url';

const [origin, slug, sessionToken] = process.argv.slice(2);
const modulePath = process.env.TS_AUTH_PLAYWRIGHT_MODULE;
const chromiumPath = process.env.TS_AUTH_CHROMIUM_PATH;
if (!origin || !slug || !sessionToken || !modulePath || !chromiumPath) {
  throw new Error('origin, slug, session token, TS_AUTH_PLAYWRIGHT_MODULE and TS_AUTH_CHROMIUM_PATH are required');
}

const { chromium } = await import(pathToFileURL(modulePath).href);
const browser = await chromium.launch({
  executablePath: chromiumPath,
  headless: true,
  // Shared CI runners can spend most of 20 seconds starting Chromium.
  // Keep startup bounded independently of the interaction deadlines below.
  timeout: 60000,
  args: ['--no-sandbox', '--disable-dev-shm-usage'],
});
const consoleErrors = [];
const pageErrors = [];
const networkResponses = [];
try {
  const context = await browser.newContext();
  await context.addCookies([{
    name: 'tsauth_session',
    value: sessionToken,
    url: origin,
    httpOnly: true,
    sameSite: 'Lax',
  }]);
  await context.addInitScript(() => {
    window.__adminBrowserCspViolations = [];
    document.addEventListener('securitypolicyviolation', (event) => {
      window.__adminBrowserCspViolations.push({
        directive: event.effectiveDirective,
        blockedURI: event.blockedURI,
      });
    });
  });
  const page = await context.newPage();
  page.on('console', (message) => {
    if (message.type() === 'error') consoleErrors.push(message.text());
  });
  page.on('pageerror', (error) => pageErrors.push(error.message));
  page.on('response', (response) => {
    const path = new URL(response.url()).pathname;
    if (path === '/admin/datastar.js' || path === '/admin/admin.css' ||
        path === `/ui/${slug}/admin/providers/github`) {
      networkResponses.push({ path, status: response.status(), contentType: response.headers()['content-type'] ?? '' });
    }
  });

  const response = await page.goto(`${origin}/ui/${slug}/admin`, {
    waitUntil: 'load',
    timeout: 20000,
  });
  if (!response || response.status() !== 200) {
    throw new Error(`admin portal navigation returned ${response?.status() ?? 'no response'}`);
  }

  const providerResponsePromise = page.waitForResponse(
    (candidate) => candidate.url().includes(`/ui/${slug}/admin/providers/github`),
    { timeout: 15000 },
  );
  await page.getByRole('button', { name: /GitHub/ }).click({ timeout: 10000 });
  const providerResponse = await providerResponsePromise;
  if (providerResponse.status() !== 200) {
    throw new Error(`provider fragment returned ${providerResponse.status()}`);
  }
  if (!(providerResponse.headers()['content-type'] ?? '').startsWith('text/event-stream')) {
    throw new Error(`provider fragment content type was ${providerResponse.headers()['content-type']}`);
  }

  const form = page.locator('#provider-setup form');
  await form.waitFor({ state: 'visible', timeout: 10000 });
  if (await form.count() !== 1) throw new Error('Datastar did not patch exactly one provider setup form');
  if (await form.locator('input[name="client_secret"]').count() !== 1) {
    throw new Error('patched provider form is missing its client-secret field');
  }
  const cspViolations = await page.evaluate(() => window.__adminBrowserCspViolations);
  const missingAssets = ['/admin/datastar.js', '/admin/admin.css'].filter((path) =>
    !networkResponses.some((item) => item.path === path && item.status === 200),
  );
  const result = { providerStatus: providerResponse.status(), providerContentType: providerResponse.headers()['content-type'], formCount: await form.count(), cspViolations, consoleErrors, pageErrors, networkResponses };
  console.log(JSON.stringify(result));
  if (cspViolations.length) throw new Error(`CSP violations: ${JSON.stringify(cspViolations)}`);
  if (consoleErrors.length) throw new Error(`browser console errors: ${JSON.stringify(consoleErrors)}`);
  if (pageErrors.length) throw new Error(`browser page errors: ${JSON.stringify(pageErrors)}`);
  if (missingAssets.length) throw new Error(`admin assets did not load: ${missingAssets.join(', ')}`);
} finally {
  await browser.close();
}
"#;

struct TestServer {
    origin: String,
    task: JoinHandle<()>,
    state: AppState,
    _storage_fixture: TerraBaseHttpFixture,
}

impl TestServer {
    async fn start() -> Self {
        let storage_fixture = TerraBaseHttpFixture::start().await;
        let listener = TcpListener::bind("127.0.0.1:0")
            .await
            .expect("admin browser test listener should bind");
        let address = listener
            .local_addr()
            .expect("admin browser test listener should have an address");
        static NEXT_DATABASE: AtomicU64 = AtomicU64::new(1);
        let database_id = NEXT_DATABASE.fetch_add(1, Ordering::Relaxed);
        let database = format!("ts-auth-admin-browser-{database_id}");
        let origin = format!("http://localhost:{}", address.port());
        let options = AuthOptions {
            listen: "http://127.0.0.1:0".to_owned(),
            public_origin: origin.clone(),
            terrascale_api_url: storage_fixture.origin.clone(),
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
        };
        let state =
            build_state_with_mode(options, true).expect("admin browser test state should be valid");
        let router = build_router(state.clone());
        let task = tokio::spawn(async move {
            axum::serve(listener, router)
                .await
                .expect("admin browser test server should remain available");
        });

        Self {
            origin,
            task,
            state,
            _storage_fixture: storage_fixture,
        }
    }

    fn client(&self) -> Client {
        Client::builder()
            .no_proxy()
            .redirect(reqwest::redirect::Policy::none())
            .build()
            .expect("admin browser test HTTP client should build")
    }
}

impl Drop for TestServer {
    fn drop(&mut self) {
        self.task.abort();
    }
}

struct TemporaryDriver(PathBuf);

impl TemporaryDriver {
    fn create() -> std::io::Result<Self> {
        let path =
            std::env::temp_dir().join(format!("ts-auth-admin-browser-{}.mjs", random_token(24)));
        fs::write(&path, BROWSER_DRIVER)?;
        Ok(Self(path))
    }
}

impl Drop for TemporaryDriver {
    fn drop(&mut self) {
        let _ = fs::remove_file(&self.0);
    }
}

#[tokio::test]
#[ignore = "requires the local Playwright and Chromium binaries; run explicitly with the documented environment variables"]
async fn chromium_click_loads_provider_fragment_under_the_real_admin_csp() {
    let playwright_module = std::env::var("TS_AUTH_PLAYWRIGHT_MODULE")
        .expect("set TS_AUTH_PLAYWRIGHT_MODULE to Playwright's index.mjs entry");
    let chromium_path = std::env::var("TS_AUTH_CHROMIUM_PATH")
        .expect("set TS_AUTH_CHROMIUM_PATH to the cached Chromium executable");
    let server = TestServer::start().await;
    let client = server.client();

    let instance_response = client
        .post(format!("{}/v1/instances", server.origin))
        .bearer_auth("test-only-operator-key")
        .header("Idempotency-Key", "admin-browser-test-bootstrap")
        .json(&json!({ "slug": SLUG, "name": "Admin browser test" }))
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
        .expect("created instance has an id");
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("system time should follow the Unix epoch")
        .as_secs() as i64;

    seed_record(
        &server,
        "users",
        USER_ID,
        &json!({
            "id": USER_ID,
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
        "ses_admin_browser",
        &json!({
            "id": "ses_admin_browser",
            "instanceId": instance_id,
            "userId": USER_ID,
            "tokenKey": sha256_hex(SESSION_TOKEN),
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
        ORGANIZATION_ID,
        &json!({
            "id": ORGANIZATION_ID,
            "instanceId": instance_id,
            "name": "Browser Test Organization",
            "slugKey": format!("{instance_id}:browser-test"),
            "status": "active",
            "createdAt": now,
        }),
    )
    .await;
    seed_record(
        &server,
        "memberships",
        "mem_admin_browser",
        &json!({
            "id": "mem_admin_browser",
            "instanceId": instance_id,
            "orgId": ORGANIZATION_ID,
            "userId": USER_ID,
            "memberKey": format!("{ORGANIZATION_ID}:{USER_ID}"),
            "role": "admin",
            "createdAt": now,
        }),
    )
    .await;

    let portal_response = client
        .get(format!("{}/ui/{SLUG}/admin", server.origin))
        .header(header::COOKIE, format!("tsauth_session={SESSION_TOKEN}"))
        .send()
        .await
        .expect("admin portal request should reach the test server");
    assert_eq!(portal_response.status(), StatusCode::OK);
    let csp = portal_response
        .headers()
        .get("content-security-policy")
        .expect("admin portal should set Content-Security-Policy")
        .to_str()
        .expect("Content-Security-Policy should be valid text")
        .to_owned();
    let html = portal_response
        .text()
        .await
        .expect("admin portal should return HTML");
    let nonce = html
        .split_once("data-nonce=\"")
        .expect("admin HTML should set Datastar data-nonce")
        .1
        .split_once('"')
        .expect("Datastar nonce attribute should be closed")
        .0;
    assert!(!nonce.is_empty());
    assert!(csp.contains(&format!("'nonce-{nonce}'")));
    assert!(!csp.contains("unsafe-eval"));

    for asset in ["admin.css", "datastar.js"] {
        let response = client
            .get(format!("{}/admin/{asset}", server.origin))
            .send()
            .await
            .unwrap_or_else(|error| panic!("admin asset {asset} request failed: {error}"));
        assert_eq!(response.status(), StatusCode::OK, "admin asset {asset}");
    }

    let driver = TemporaryDriver::create().expect("temporary browser driver should be written");
    let driver_path = driver.0.clone();
    let origin = server.origin.clone();
    let result = tokio::task::spawn_blocking(move || {
        Command::new("node")
            .arg(driver_path)
            .arg(origin)
            .arg(SLUG)
            .arg(SESSION_TOKEN)
            .env("TS_AUTH_PLAYWRIGHT_MODULE", playwright_module)
            .env("TS_AUTH_CHROMIUM_PATH", chromium_path)
            .output()
    })
    .await
    .expect("Node browser driver task should complete")
    .expect("Node should launch the Playwright browser driver");
    assert!(
        result.status.success(),
        "Chromium admin interaction failed.\nstdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&result.stdout),
        String::from_utf8_lossy(&result.stderr),
    );
    println!("{}", String::from_utf8_lossy(&result.stdout));
}

async fn seed_record(server: &TestServer, collection: &str, id: &str, value: &Value) {
    server
        .state
        .store
        .create(collection, id, value)
        .await
        .unwrap_or_else(|error| panic!("seed {collection}/{id} through SDK fixture: {error}"));
}
