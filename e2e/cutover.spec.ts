import type { Page, Response } from "@playwright/test";
import { test, expect } from "./support/test";
import { login, registerAndOnboard } from "./support/journey";

function account(tag: string) {
  const id = `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
  return {
    email: `e2e-cutover-${tag}-${id}@example.test`,
    password: "E2e-cutover-123!",
    displayName: `E2E ${tag}`,
  };
}

const sampleAccount = {
  email: "mario@terrascale.tech",
  password: "terrascale",
};

const adminAccount = {
  email: process.env.DASHBOARD_E2E_ADMIN_EMAIL ?? process.env.DASHBOARD_DEMO_ADMIN_EMAIL ?? "e2e-admin-seed@example.test",
  password: process.env.DASHBOARD_E2E_ADMIN_PASSWORD ?? process.env.DASHBOARD_DEMO_ADMIN_PASSWORD ?? "E2e-admin-seed-123!",
};

async function submitAndExpect(
  page: Page,
  path: string,
  status: number,
  submit: () => Promise<void>,
): Promise<Response> {
  const responsePromise = page.waitForResponse((response) =>
    response.request().method() === "POST" && new URL(response.url()).pathname === path,
  );
  await submit();
  const response = await responsePromise;
  expect(response.status(), `POST ${path} returned ${response.status()}`).toBe(status);
  return response;
}

test("business organization retries canceled annual checkout as monthly and creates its first project", async ({ page }) => {
  await registerAndOnboard(page, account("organization"));
  expect((await page.goto("/organization?newOrganization=organization"))?.status()).toBe(200);
  const wizard = page.locator("dialog#create-organization");
  await expect(wizard).toBeVisible();

  const organizationName = `Cutover business ${Date.now()}`;
  const organization = wizard.locator('form[action="/api/v1/dashboard/organization/new-organization"]')
    .filter({ has: page.locator('input[name="step"][value="organization"]') });
  await organization.locator('[name="name"]').fill(organizationName);
  await submitAndExpect(page, "/api/v1/dashboard/organization/new-organization", 303, () =>
    organization.getByRole("button", { name: "Create organization", exact: true }).click(),
  );
  await expect(wizard.getByRole("heading", { name: "Set up billing" })).toBeVisible();

  const payment = wizard.locator('form[action="/api/v1/dashboard/organization/new-organization"]')
    .filter({ has: page.locator('input[name="step"][value="payment-method"]') });
  await payment.locator('input[name="plan"][value="starter"]').check();
  await payment.locator('input[name="interval"][value="annual"]').check();
  await submitAndExpect(page, "/api/v1/dashboard/organization/new-organization", 307, () =>
    payment.getByRole("button", { name: "Continue to checkout" }).click(),
  );
  await page.waitForURL(/\/checkout\/cs_local_/);
  await expect(page.getByRole("heading", { name: "Local payment setup" })).toBeVisible();
  await expect(page.locator(".ts-flow-subtitle")).toContainText("billed annual");
  await expect(page.getByRole("status")).toContainText("No card details are collected");
  await page.getByRole("button", { name: "Cancel checkout" }).click();

  await page.waitForURL((url) => url.pathname === "/organization" && url.searchParams.get("payment") === "canceled");
  await expect(wizard.getByRole("alert")).toContainText("Payment setup canceled");
  await expect(wizard.getByRole("heading", { name: "Set up billing" })).toBeVisible();
  await payment.locator('input[name="interval"][value="monthly"]').check();
  await submitAndExpect(page, "/api/v1/dashboard/organization/new-organization", 307, () =>
    payment.getByRole("button", { name: "Continue to checkout" }).click(),
  );
  await page.waitForURL(/\/checkout\/cs_local_/);
  await expect(page.locator(".ts-flow-subtitle")).toContainText("billed monthly");
  await page.getByRole("button", { name: "Confirm local demo setup" }).click();

  await page.waitForURL((url) => url.pathname === "/organization" && url.searchParams.get("payment") === "success");
  const project = wizard.locator('form[action="/api/v1/dashboard/organization/new-organization"]')
    .filter({ has: page.locator('input[name="step"][value="project"]') });
  await expect(project).toBeVisible();
  const projectName = `Cutover project ${Date.now()}`;
  await project.locator('[name="name"]').fill(projectName);
  await project.locator('[name="regions"]').selectOption(["GRU", "SFO"]);
  await project.locator('[name="region"]').selectOption("GRU");
  await project.locator('[name="backupRegion"]').selectOption("SFO");
  await submitAndExpect(page, "/api/v1/dashboard/organization/new-organization", 303, () =>
    project.getByRole("button", { name: "Create project", exact: true }).click(),
  );
  await page.waitForURL((url) => url.pathname === "/organization" && !url.search);
  await expect(page.getByRole("heading", { name: organizationName })).toBeVisible();
  await expect(page.locator("#projects tbody tr").filter({ hasText: projectName })).toBeVisible();
});

test("owner creates and revokes an API key, then invites a team member", async ({ page }) => {
  await registerAndOnboard(page, account("credentials"));
  expect((await page.goto("/api-keys?create=key"))?.status()).toBe(200);
  const keyName = `cutover-key-${Date.now()}`;
  const createKey = page.locator('form[action="/api/v1/dashboard/credentials/key"]');
  await createKey.locator('[name="name"]').fill(keyName);
  await createKey.locator('[name="scopes"]').fill("database:read");
  await submitAndExpect(page, "/api/v1/dashboard/credentials/key", 200, () =>
    createKey.getByRole("button", { name: "Create key" }).click(),
  );
  const secret = page.locator('section[role="status"]').filter({ hasText: "Copy this secret now" });
  await expect(secret.locator("code")).toContainText(/^tsk_demo_/);
  const keyRow = page.locator("table.ts-table tbody tr").filter({ hasText: keyName });
  await expect(keyRow).toContainText("Active");
  await submitAndExpect(page, "/api/v1/dashboard/credentials/revoke", 303, () =>
    keyRow.locator('form[action="/api/v1/dashboard/credentials/revoke"] button').click(),
  );
  await expect(page.locator("table.ts-table tbody tr").filter({ hasText: keyName })).toContainText("Revoked or expired");
  await expect(page.locator('section[role="status"]').filter({ hasText: "Copy this secret now" })).toHaveCount(0);

  expect((await page.goto("/access"))?.status()).toBe(200);
  const invitee = account("invitee").email;
  const invite = page.locator('form.ts-access-overview-invite[action="/api/v1/access/invitations"]');
  await invite.locator('input[name="email"]').fill(invitee);
  await submitAndExpect(page, "/api/v1/access/invitations", 200, () =>
    invite.getByRole("button", { name: "Send invite" }).click(),
  );
  const invitation = page.locator('section[aria-labelledby="invitation-link-title"]');
  await expect(invitation).toContainText("Invitation created");
  await expect(invitation.locator('a[href^="/invitations/"]')).toHaveCount(1);
  await expect(page.locator(".ts-access-invitation-summary")).toContainText(invitee);
});

test("profile email and language persist, password change ends the session, and new login lists its current session", async ({ page }) => {
  const credentials = account("profile");
  await registerAndOnboard(page, credentials);
  expect((await page.goto("/profile?tab=profile"))?.status()).toBe(200);
  const newEmail = account("renamed").email;
  const email = page.locator('form[action="/api/v1/dashboard/profile/email-change"]');
  await email.locator('[name="newEmail"]').fill(newEmail);
  await email.locator('[name="password"]').fill(credentials.password);
  await submitAndExpect(page, "/api/v1/dashboard/profile/email-change", 303, () =>
    email.getByRole("button", { name: "Change email" }).click(),
  );
  await expect(page.getByRole("status")).toContainText("Email changed in this demo");
  await expect(page.locator(".ts-profile-heading-group")).toContainText(newEmail);

  const language = page.locator('form[action="/api/v1/dashboard/profile/language"]');
  await language.locator('[name="locale"]').selectOption("pt-BR");
  await submitAndExpect(page, "/api/v1/dashboard/profile/language", 303, () =>
    language.getByRole("button", { name: "Save language" }).click(),
  );
  await expect(page.getByRole("status")).toContainText("Language preference saved");
  await expect(page.locator('form[action="/api/v1/dashboard/profile/language"] [name="locale"]')).toHaveValue("pt-BR");

  expect((await page.goto("/profile?tab=security"))?.status()).toBe(200);
  await expect(page.getByRole("heading", { name: "Change password" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Multi-factor authentication" })).toBeVisible();
  const nextPassword = "E2e-cutover-next-123!";
  const password = page.locator('form[action="/api/v1/dashboard/profile/password"]');
  await password.locator('[name="currentPassword"]').fill(credentials.password);
  await password.locator('[name="newPassword"]').fill(nextPassword);
  await password.locator('[name="confirmPassword"]').fill(nextPassword);
  await submitAndExpect(page, "/api/v1/dashboard/profile/password", 303, () =>
    password.getByRole("button", { name: "Change password and sign out" }).click(),
  );
  await page.waitForURL((url) => url.pathname === "/login");
  await login(page, newEmail, nextPassword);
  expect((await page.goto("/profile?tab=sessions"))?.status()).toBe(200);
  await expect(page.getByRole("heading", { name: "Active sessions" })).toBeVisible();
  await expect(page.getByRole("table", { name: "Active local dashboard sessions with their current status and expiry." })).toContainText("Current session");
});

test("SQL check explains a read-only query and a collection index can be created", async ({ page }) => {
  await registerAndOnboard(page, account("data"));
  expect((await page.goto("/databases?create=1"))?.status()).toBe(200);
  const database = page.locator('form[action="/api/v1/dashboard/databases"]');
  await database.locator('[name="name"]').fill(`cutover-${Date.now()}`);
  await database.locator('[name="region"]').selectOption("GRU");
  await submitAndExpect(page, "/api/v1/dashboard/databases", 303, () =>
    database.getByRole("button", { name: "Create database" }).click(),
  );
  await page.waitForURL((url) => url.pathname.startsWith("/databases/db_"));
  const databaseID = new URL(page.url()).pathname.split("/").at(-1)!;
  expect((await page.goto(`/data?view=sql&databaseId=${encodeURIComponent(databaseID)}&collection=orders`))?.status()).toBe(200);
  const check = page.locator('form[action="/api/v1/dashboard/data/sql/check"]');
  const statement = "SELECT * FROM orders WHERE customer_id = $1 ORDER BY created_at DESC LIMIT 10";
  await check.locator('[name="sql"]').fill(statement);
  await submitAndExpect(page, "/api/v1/dashboard/data/sql/check", 200, () =>
    check.getByRole("button", { name: "Check SQL" }).click(),
  );
  expect(new URL(page.url()).searchParams.has("sql")).toBe(false);
  await expect(page.getByRole("heading", { name: "SQL check" })).toBeVisible();
  await expect(page.getByText("Server check passed.")).toBeVisible();
  await expect(page.getByText("Statement collection: orders")).toBeVisible();
  await expect(page.getByText("customer_id", { exact: true })).toBeVisible();

  expect((await page.goto(`/indexes?databaseId=${encodeURIComponent(databaseID)}&collection=orders`))?.status()).toBe(200);
  const createIndex = page.locator('form[action="/api/v1/dashboard/indexes"]');
  await createIndex.locator('[name="field"]').fill("customer_id");
  await submitAndExpect(page, "/api/v1/dashboard/indexes", 303, () =>
    createIndex.getByRole("button", { name: "Create index" }).click(),
  );
  await expect(page.getByRole("heading", { name: "Collection indexes" }).locator("..").locator("ul")).toContainText("customer_id");
});

test("sample index recommendation records a demo build", async ({ page }) => {
  await login(page, sampleAccount.email, sampleAccount.password);
  expect((await page.goto("/databases"))?.status()).toBe(200);
  const databaseLink = page.getByRole("link", { name: "core-primary" });
  await expect(databaseLink).toBeVisible();
  const databaseHref = await databaseLink.getAttribute("href");
  expect(databaseHref).toBeTruthy();
  const databaseID = new URL(databaseHref!, page.url()).pathname.split("/").at(-1)!;
  expect((await page.goto(`/indexes?tab=recommendations&databaseId=${encodeURIComponent(databaseID)}&collection=orders`))?.status()).toBe(200);
  const recommendation = page.locator('form[action="/api/v1/dashboard/indexes/recommendations/apply"]').first();
  if (await recommendation.count() > 0) {
    await expect(recommendation.locator("..")).toContainText("total");
    await submitAndExpect(page, "/api/v1/dashboard/indexes/recommendations/apply", 303, () =>
      recommendation.getByRole("button", { name: "Apply" }).click(),
    );
  }
  await expect(page.getByRole("heading", { name: "Demo index builds" }).locator("..").locator("ul")).toContainText("total: building");
  expect((await page.goto(`/indexes?databaseId=${encodeURIComponent(databaseID)}&collection=orders`))?.status()).toBe(200);
  await expect(page.getByRole("heading", { name: "Collection indexes" }).locator("..").locator("ul")).toContainText("total");
});

test("sample payment history filters and paginates invoices", async ({ page }) => {
  await login(page, sampleAccount.email, sampleAccount.password);
  expect((await page.goto("/usage?tab=billing"))?.status()).toBe(200);
  await expect(page.getByRole("heading", { name: "Payment history" })).toBeVisible();
  const table = page.getByRole("table", { name: "Invoices for this organization matching the current filters" });
  await expect(table.locator("tbody tr")).toHaveCount(10);
  await expect(page.getByRole("navigation", { name: "Payment history pages" })).toContainText("of 14");
  await page.getByRole("navigation", { name: "Payment history pages" }).getByRole("link", { name: "Next" }).click();
  await expect(table.locator("tbody tr")).toHaveCount(4);
  await expect(page.getByRole("navigation", { name: "Payment history pages" })).toContainText("11–14 of 14");

  const filters = page.getByRole("search", { name: "Filter payment history" });
  await filters.locator('[name="status"]').selectOption("void");
  await filters.getByRole("button", { name: "Apply" }).click();
  await expect(table.locator("tbody tr")).toHaveCount(1);
  await expect(table.locator("tbody tr").first()).toContainText("void");
  await filters.locator('[name="issued"]').selectOption("3m");
  await filters.getByRole("button", { name: "Apply" }).click();
  await expect(page.locator('.ts-empty[role="status"]')).toContainText("No invoices match these filters");
  await filters.getByRole("link", { name: "Clear" }).click();
  await expect(table.locator("tbody tr")).toHaveCount(10);
});

test("admin Estate action records an audit entry without claiming an infrastructure change", async ({ page }) => {
  await login(page, adminAccount.email, adminAccount.password);
  expect((await page.goto("/admin/estate"))?.status()).toBe(200);
  await expect(page.getByRole("heading", { name: "Observed region placement" })).toBeVisible();
  const form = page.locator('form[action="/api/v1/admin/estate/action"]');
  const target = `db_cutover_${Date.now()}`;
  const reason = `E2E audit record ${Date.now()}`;
  await form.locator('[name="action"]').selectOption("pause");
  await form.locator('[name="targetId"]').fill(target);
  await form.locator('[name="reason"]').fill(reason);
  await submitAndExpect(page, "/api/v1/admin/estate/action", 303, () =>
    form.getByRole("button", { name: "Record action" }).click(),
  );
  await expect(page.getByRole("status").filter({ hasText: "Action completed and recorded" })).toContainText("No infrastructure action was taken");
  const log = page.getByRole("table", { name: "Recorded platform administrator estate actions, newest first" });
  await expect(log.locator("tbody tr").filter({ hasText: target })).toContainText(reason);
});

test("unknown browser page loads the illustrated 404 while API requests receive a bare 404", async ({ browser, baseURL }) => {
  const context = await browser.newContext({ baseURL });
  const page = await context.newPage();
  try {
    const response = await page.goto("/cutover-page-that-does-not-exist");
    expect(response?.status(), "GET /cutover-page-that-does-not-exist").toBe(404);
    await expect(page.getByRole("heading", { name: "We can’t find that page." })).toBeVisible();
    await expect(page.locator('img[src="/img/404-number.webp"]')).toBeVisible();
    await expect(page.locator('img[src="/img/404-illustration.webp"]')).toBeVisible();
    for (const source of ["/img/404-number.webp", "/img/404-illustration.webp"]) {
      const image = page.locator(`img[src="${source}"]`);
      await expect.poll(() => image.evaluate((element: HTMLImageElement) => element.complete && element.naturalWidth > 0)).toBe(true);
    }

    const api = await context.request.get("/api/v1/cutover-route-that-does-not-exist", {
      headers: { Accept: "application/json" },
    });
    expect(api.status(), "GET /api/v1/cutover-route-that-does-not-exist").toBe(404);
    expect(api.headers()["content-type"] ?? "").not.toContain("text/html");
    expect(await api.text()).not.toContain("We can’t find that page.");
  } finally {
    await context.close();
  }
});
