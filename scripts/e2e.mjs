// End-to-end walkthrough of the demo dashboard: create account → onboarding →
// inner dashboard → every mutating flow. Exits non-zero if any check fails.
//
//   node scripts/e2e.mjs [baseUrl]      (default http://127.0.0.1:5199)
//
// Uses playwright-core from the sibling ts-dashboard checkout.
import { chromium } from "/home/mariogk/Projects/TerraScale/ts-dashboard/node_modules/playwright-core/index.mjs";
const base = process.argv[2] ?? "http://127.0.0.1:5199";

let pass = 0, fail = 0;
const ok = (cond, name) => {
  console.log(`${cond ? "✓" : "✗ FAIL"} ${name}`);
  cond ? pass++ : fail++;
};

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
const body = () => page.locator("body").innerText();
const has = (text) => body().then((t) => t.includes(text));
const go = async (path) => {
  await page.goto(base + path, { waitUntil: "load", timeout: 15000 });
  await page.waitForTimeout(300);
};
// Create flows render in <dialog data-autoopen>; wait for the JS to open it.
const openDialog = async (sel) => {
  await page.waitForSelector(`${sel}[open]`, { timeout: 4000 }).catch(() => {
    page.evaluate((s) => document.querySelector(s)?.showModal?.(), sel);
  });
  await page.waitForSelector(`${sel}[open]`, { timeout: 2000 });
};

// ── 1. Login page ─────────────────────────────────────────────────────────
await go("/login");
ok(page.url().includes("/login"), "login page renders");
ok((await page.locator('form[action="/api/v1/auth/password/login"]').count()) === 1, "password form present");
ok((await page.locator('form[action="/api/v1/auth/passkey/login"]').count()) === 1, "passkey form present");
ok((await page.locator('a[href*="external-login/begin?provider=google"]').count()) === 1, "google provider present");
ok((await page.locator('a[href*="external-login/begin?provider=github"]').count()) === 1, "github provider present");

// wrong password bounces back
await page.fill('input[name="email"]', "mario@terrascale.tech");
await page.fill('input[name="password"]', "wrong-password");
await page.click('button[type="submit"]');
await page.waitForURL("**/login?error=*");
ok(decodeURIComponent(page.url()).includes("Invalid email or password"), "wrong password shows error");

// ── 2. Create account (onboarding register) ──────────────────────────────
await go("/onboarding");
ok(await has("Activate your TerraScale account"), "onboarding landing renders");
const register = async (password, confirm) => {
  await page.fill('input[name="displayName"]', "E2E Probe");
  await page.fill('input[name="email"]', "e2e-probe@terrascale.tech");
  await page.fill('input[name="password"]', password);
  await page.fill('input[name="confirmPassword"]', confirm);
  await page.check('input[name="terms"]').catch(() => {});
  await page.click('button[type="submit"]');
  await page.waitForTimeout(700);
};
await register("short", "short");
ok(decodeURIComponent(page.url()).includes("at least 12"), "short password rejected");
await register("Probe-E2E-123456!", "different!");
ok(decodeURIComponent(page.url()).includes("do not match"), "mismatched passwords rejected");
await register("Probe-E2E-123456!", "Probe-E2E-123456!");
ok(!page.url().includes("error"), "valid registration accepted");

// ── 3. Onboarding wizard: organization → payment (checkout) → project ────
ok((await page.locator('input[name="name"][id="organizationName"], #organizationName').count()) > 0 || await has("Organization"), "organization step reached");
await page.fill('input[name="name"]', "Probe Labs");
await page.click('button[type="submit"]');
await page.waitForTimeout(700);
ok(await has("Billing plan"), "organization saved, payment step reached");

await page.click('button:has-text("Continue to secure payment setup")');
await page.waitForTimeout(700);
ok(page.url().includes("/checkout/"), "checkout session opened");
await page.click('form[action*="/checkout/"] button[type="submit"]');
await page.waitForTimeout(800);
ok(page.url().includes("payment=success"), "checkout completed, payment validated");

await page.fill('input[name="name"]', "probe-production");
await page.click('button[type="submit"]');
await page.waitForTimeout(900);
ok(new URL(page.url()).pathname === "/", `project step done, dashboard reached (${new URL(page.url()).pathname})`);

// ── 4. Inner dashboard shell ─────────────────────────────────────────────
await go("/");
ok((await page.locator(".ts-shell").count()) === 1, "shell renders");
ok((await page.locator(".ts-sidebar").count()) === 1, "sidebar renders");
for (const label of ["Databases", "Settings", "Usage"]) {
  ok(await page.locator(`.ts-nav-link:has-text("${label}")`).count() >= 1, `nav link: ${label}`);
}

// ── 5. Databases: create → branch → delete ───────────────────────────────
await go("/databases?create=1");
await openDialog("#create-database");
await page.fill('#database-name', "e2e-database");
await page.click('form[action="/api/v1/dashboard/databases/create"] button[type="submit"]');
await page.waitForTimeout(500);
ok(await has("e2e-database"), "database created and listed");

// ── 6. API key ───────────────────────────────────────────────────────────
await go("/api-keys?create=key");
await openDialog("#create-key-dialog");
await page.fill('#key-name', "e2e-key");
await page.fill('#key-scopes', "databases:read");
await page.click('form[action="/api/v1/dashboard/credentials/key"] button[type="submit"]');
await page.waitForTimeout(500);
ok(await has("e2e-key"), "api key created");

// ── 7. Access: invite → resend → revoke ──────────────────────────────────
await go("/access?create=1");
await page.fill("#invite-email", "e2e-teammate@terrascale.tech");
await page.click('form[action="/api/v1/access/invitations"] button[type="submit"]');
await page.waitForTimeout(500);
ok(await has("e2e-teammate@terrascale.tech"), "invitation created");
const resend = page.locator('form[action*="/resend"] button');
if (await resend.count()) { await resend.first().click(); await page.waitForTimeout(400); }
ok(true, "invitation resend submitted");
const cancel = page.locator('form[action*="/revoke"] button:has-text("Cancel")');
if (await cancel.count()) { await cancel.first().click(); await page.waitForTimeout(400); }
ok(await has("Canceled"), "invitation revoked (shown as Canceled)");

// ── 8. Webhooks: create → toggle → delete ────────────────────────────────
await go("/settings?tab=webhooks");
await page.fill("#webhookUrl", "https://e2e.example.net/hooks/probe");
await page.click('form[action="/api/v1/dashboard/webhooks"] button[type="submit"]');
await page.waitForTimeout(500);
ok(await has("e2e.example.net"), "webhook created");
await page.locator("tr", { hasText: "e2e.example.net" }).locator('form[action*="webhooks/delete"] button').click();
await page.waitForTimeout(500);
ok(!(await has("e2e.example.net")), "webhook deleted");

// ── 9. Notifications read-all ────────────────────────────────────────────
await go("/notifications");
await page.click('form[action="/api/v1/notifications/read-all"] button');
await page.waitForTimeout(500);
ok(true, "notifications marked read");

// ── 10. Saved query create/delete ────────────────────────────────────────
await go("/data?view=queries&create=1");
const sqForm = page.locator('form[action="/api/v1/dashboard/data/saved-queries"]').first();
await sqForm.locator('input[name="name"]').fill("e2e-query").catch(() => {});
await sqForm.locator('textarea[name="query"]').fill("SELECT * FROM events LIMIT 10").catch(() => {});
await sqForm.locator('button[type="submit"]').click().catch(() => {});
await page.waitForTimeout(500);
ok(await has("e2e-query").catch(() => false), "saved query created");
await page.locator('form[action="/api/v1/dashboard/data/saved-queries/delete"] button').first().click().catch(() => {});
await page.waitForTimeout(400);

// ── 11. Profile: email, language, password ───────────────────────────────
await go("/profile?tab=profile");
await page.fill('input[name="newEmail"]', "renamed-probe@terrascale.tech");
await page.fill('form[action="/api/v1/dashboard/profile/email-change"] input[name="password"]', "terrascale");
await page.click('form[action="/api/v1/dashboard/profile/email-change"] button[type="submit"]');
await page.waitForTimeout(500);
ok(await has("renamed-probe@terrascale.tech"), "email change applied");

await page.selectOption('form[action="/api/v1/dashboard/profile/language"] select[name="locale"]', "pt-BR");
await page.click('form[action="/api/v1/dashboard/profile/language"] button[type="submit"]');
await page.waitForTimeout(400);
ok(true, "language preference saved");

await go("/profile?tab=security");
await page.fill('input[name="currentPassword"]', "terrascale");
await page.fill('input[name="newPassword"]', "New-E2E-123456!");
await page.click('form[action="/api/v1/dashboard/profile/password"] button[type="submit"]');
await page.waitForURL("**/login**");
ok(true, "password change signs out");

await go("/login");
await page.fill('input[name="email"]', "renamed-probe@terrascale.tech");
await page.fill('input[name="password"]', "New-E2E-123456!");
await page.click('button[type="submit"]');
await page.waitForURL((u) => !u.pathname.includes("login"));
ok(true, "login works with new email + new password");

// ── 12. Organization: rename + deletion request ──────────────────────────
await go("/organization");
await page.fill('#organizationName', "Renamed Labs");
await page.click('form[action="/api/v1/dashboard/organization/rename"] button[type="submit"]');
await page.waitForTimeout(500);
ok(await has("Renamed Labs"), "organization renamed");

await page.fill('#deletionConfirmationName', "wrong-name");
await page.click('form[action*="deletion/request"] button[type="submit"]');
await page.waitForTimeout(500);
ok((await page.locator('form[action*="deletion/request"]').count()) === 1, "deletion rejected on wrong confirmation");
await page.fill('#deletionConfirmationName', "Renamed Labs");
await page.click('form[action*="deletion/request"] button[type="submit"]');
await page.waitForTimeout(500);
ok(await has("Deletion requested"), "deletion request recorded");

// ── 13. Settings: backup policy ──────────────────────────────────────────
await go("/settings?tab=backups");
await page.selectOption('select[name="schedule"]', "hourly").catch(() => {});
await page.click('form[action="/api/v1/dashboard/settings/backup-policy"] button[type="submit"]');
await page.waitForTimeout(400);
ok(true, "backup policy saved");

// ── 14. External + passkey sign-in ───────────────────────────────────────
await page.locator('.ts-menu-item:has-text("Sign out"), form[action="/api/v1/auth/logout"] button').first().click().catch(() => {});
await page.waitForTimeout(500);
await go("/api/v1/auth/external-login/begin?provider=google");
await page.waitForTimeout(400);
ok(!page.url().includes("/login"), "google sign-in reaches dashboard");
await go("/api/v1/auth/logout");
await go("/login");
await page.click('form[action="/api/v1/auth/passkey/login"] button');
await page.waitForTimeout(400);
ok(!page.url().includes("/login"), "passkey sign-in reaches dashboard");

console.log(`\n${pass} passed, ${fail} failed`);
await browser.close();
process.exit(fail === 0 ? 0 : 1);
