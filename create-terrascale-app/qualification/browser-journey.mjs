// Connected qualification only: no route interception, local store, or API substitute.
import assert from "node:assert/strict";
import { readFile, writeFile } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import { chromium } from "playwright";

const configPath = process.env.TERRASCALE_STARTER_JOURNEY_CONFIG;
assert.ok(configPath, "Set TERRASCALE_STARTER_JOURNEY_CONFIG to a private fixture JSON file");
let config;
try { config = JSON.parse(await readFile(configPath, "utf8")); }
catch { throw new Error("The private fixture JSON could not be read."); }
for (const name of ["appUrl", "authUrl"]) {
  assert.ok(["http:", "https:"].includes(new URL(config[name]).protocol), `${name} must be an HTTP URL`);
}
assert.equal(config.accounts?.length, 2, "Provide two accounts (the same user may open independent sessions)");
assert.ok(config.revocationBoundMs > 0 && config.revocationBoundMs <= 300_000,
  "Declare the deployed revocation bound in milliseconds (at most five minutes)");
assert.ok(config.sources && config.artifacts, "Identify source revisions and qualified artifact SHA-256 digests");
for (const component of ["starter", "sdk", "database", "auth"]) {
  assert.ok(typeof config.sources[component] === "string" && config.sources[component].length > 0, `Identify ${component} source`);
  assert.match(config.artifacts[component], /^[a-f0-9]{64}$/u, `Identify ${component} artifact SHA-256`);
}
const browser = await chromium.launch({ headless: true });
const results = [];
const pendingEvidence = [];
const evidenceErrors = [];
let stage = "sign-in and shared view";
let failed = false;
const startedAt = new Date().toISOString();
const contexts = [];
const pages = [];
const evidence = [{ refreshes: 0, views: [] }, { refreshes: 0, views: [] }];
const authOrigin = new URL(config.authUrl).origin;
const appOrigin = new URL(config.appUrl).origin;

async function login(context, account) {
  const response = await context.request.post(new URL("/api/auth/sign-in/email", config.authUrl).href, {
    headers: { origin: authOrigin }, data: account,
  });
  assert.equal(response.status(), 200, "Real account sign-in must succeed");
}
async function connect(page) {
  await page.goto(config.appUrl);
  await page.getByRole("button", { name: "Sign in with TerraScale", exact: true }).click();
  // Installed authority fixtures expose the real consent API without a UI.
  // Keep that qualification mode explicit; the default exercises dashboard UI.
  if (config.consentMode === "public-api") {
    const needsConsent = await Promise.race([
      page.waitForURL((url) => url.origin === authOrigin && url.pathname === "/oauth/consent").then(() => true),
      page.getByRole("button", { name: "Add post", exact: true }).waitFor().then(() => false),
    ]);
    if (needsConsent) {
      const response = await page.context().request.post(new URL("/api/auth/oauth2/consent", config.authUrl).href, {
        headers: { origin: authOrigin },
        data: { accept: true, oauth_query: new URL(page.url()).search.slice(1), scope: "openid offline_access database:read database:write" },
      });
      assert.equal(response.status(), 200, "Actual OAuth consent endpoint must approve access");
      const body = await response.json();
      const callback = new URL(body.redirect_uri ?? body.url);
      const expected = new URL(config.appUrl);
      assert.equal(callback.origin, expected.origin, "Consent callback must return to the starter");
      assert.equal(callback.pathname, expected.pathname, "Consent callback path must match the registered starter");
      await page.goto(callback.href);
    }
  } else {
    const needsConsent = await Promise.race([
      page.getByRole("button", { name: "Approve access", exact: true }).waitFor().then(() => true),
      page.getByRole("button", { name: "Add post", exact: true }).waitFor().then(() => false),
    ]);
    if (needsConsent) await page.getByRole("button", { name: "Approve access", exact: true }).click();
  }
  await page.getByRole("button", { name: "Add post", exact: true }).waitFor({ timeout: 30_000 });
  await page.waitForFunction(() => [...document.querySelectorAll("button")]
    .some((button) => button.textContent === "Add post" && !button.disabled), undefined, { timeout: 30_000 });
}
async function post(page, title) {
  await page.getByRole("textbox", { name: "Post title", exact: true }).fill(title);
  await page.getByRole("button", { name: "Add post", exact: true }).click();
  await page.getByText("Post saved. The live query will show it in both tabs.", { exact: true }).waitFor();
}
async function visibleInBoth(title) {
  await Promise.all(pages.map((page) => page.getByRole("listitem").filter({ hasText: title }).waitFor({ timeout: 15_000 })));
}
function passed(scenario) {
  results.push({ scenario, passedAt: new Date().toISOString() });
  console.log(`PASS ${scenario}`);
}
try {
  for (let index = 0; index < 2; index++) {
    const context = await browser.newContext({ locale: "en-US" });
    contexts.push(context);
    await login(context, config.accounts[index]);
    const page = await context.newPage();
    page.setDefaultTimeout(30_000);
    page.on("response", (response) => {
      const url = new URL(response.url());
      if (url.origin === authOrigin && url.pathname === "/api/auth/oauth2/token" && response.status() === 200 &&
          new URLSearchParams(response.request().postData() ?? "").get("grant_type") === "refresh_token") evidence[index].refreshes++;
      if (url.pathname === "/api/v1/reactive/views" && response.status() === 200) {
        pendingEvidence.push(response.json().then((body) => {
          if (typeof body.view !== "string") evidenceErrors.push("Missing shared view identity");
          else evidence[index].views.push(body.view);
        }).catch(() => { evidenceErrors.push("Attach response could not be read"); }));
      }
    });
    pages.push(page);
    await connect(page);
  }
  const prefix = `starter-journey-${randomUUID()}`;
  await post(pages[0], `${prefix}-first`);
  await visibleInBoth(`${prefix}-first`);
  await post(pages[1], `${prefix}-second`);
  await visibleInBoth(`${prefix}-second`);
  await Promise.all(pendingEvidence);
  assert.equal(evidenceErrors.length, 0, "Attach responses must identify the shared view");
  assert.ok(evidence[0].views.some((view) => evidence[1].views.includes(view)), "Both readers must attach the same authorized shared view");
  passed("two independent sign-ins, shared view, writes visible in both browsers");

  stage = "OAuth refresh";
  // Wait beyond the producer's 60-second OAuth access-token lifetime.
  await pages[0].waitForTimeout(65_000);
  await post(pages[0], `${prefix}-refreshed`);
  await post(pages[1], `${prefix}-peer-refreshed`);
  await visibleInBoth(`${prefix}-refreshed`);
  await visibleInBoth(`${prefix}-peer-refreshed`);
  assert.ok(evidence.every((item) => item.refreshes > 0), "Both browsers must use actual OAuth refresh grants");
  passed("rotating refresh permits writes and reactive delivery beyond access-token expiry");

  stage = "network reconnect";
  await contexts[1].setOffline(true);
  await post(pages[0], `${prefix}-offline`);
  await contexts[1].setOffline(false);
  await visibleInBoth(`${prefix}-offline`);
  passed("disconnected reader catches up after network recovery");

  stage = "session revocation";
  const logout = await contexts[0].request.post(new URL("/api/auth/sign-out", config.authUrl).href, {
    headers: { origin: authOrigin }, data: {},
  });
  assert.equal(logout.status(), 200, "Real authority session revocation must succeed");
  await pages[0].waitForTimeout(config.revocationBoundMs);
  const add = pages[0].getByRole("button", { name: "Add post", exact: true });
  if (await add.isEnabled()) {
    await pages[0].getByRole("textbox", { name: "Post title", exact: true }).fill(`${prefix}-forbidden`);
    await add.click();
    await pages[0].getByRole("alert").waitFor();
    assert.equal(await pages[0].getByText("Post saved. The live query will show it in both tabs.", { exact: true }).count(), 0);
  }
  await post(pages[1], `${prefix}-after-revocation`);
  await pages[1].getByRole("listitem").filter({ hasText: `${prefix}-after-revocation` }).waitFor();
  await pages[0].waitForTimeout(5_000);
  assert.equal(await pages[0].getByRole("listitem").filter({ hasText: `${prefix}-after-revocation` }).count(), 0,
    "Revoked reader must not receive changes after the declared bound");
  assert.equal(await pages[1].getByRole("listitem").filter({ hasText: `${prefix}-forbidden` }).count(), 0,
    "Revoked session must not write");
  passed("session revocation stops writes and delivery without affecting the peer");

  stage = "fresh sign-in after revocation";
  await pages[0].getByRole("button", { name: "Disconnect and clear credential", exact: true }).click();
  await login(contexts[0], config.accounts[0]);
  await connect(pages[0]);
  await visibleInBoth(`${prefix}-after-revocation`);
  await post(pages[0], `${prefix}-reconnected`);
  await visibleInBoth(`${prefix}-reconnected`);
  passed("disconnect, fresh sign-in, catch-up, and new writes");
} catch {
  // Playwright errors can include OAuth callback URLs or request headers.
  // Keep credentials out of qualification output, including failure output.
  failed = true;
  process.exitCode = 1;
  console.error(`FAIL ${stage}; the connected scenario did not satisfy its assertions.`);
} finally {
  await browser.close();
  if (config.resultPath) await writeFile(config.resultPath, JSON.stringify({
    startedAt, finishedAt: new Date().toISOString(), sources: config.sources, artifacts: config.artifacts,
    appOrigin, authOrigin, consentMode: config.consentMode ?? "dashboard-ui", revocationBoundMs: config.revocationBoundMs, results,
    complete: !failed && results.length === 5,
    ...(failed ? { failedStage: stage } : {}),
  }, null, 2) + "\n");
}
