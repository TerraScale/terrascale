import { test, expect } from "@playwright/test";
import { login, logout, registerAndOnboard } from "./support/journey";

test("account creation → onboarding → dashboard works end to end", async ({ page }) => {
  const email = `e2e-journey-${Date.now()}@example.test`;
  const password = "E2e-journey-123!";

  await registerAndOnboard(page, { email, password, displayName: "E2E Journey" });
  await expect(page.getByRole("heading", { name: "Overview" })).toBeVisible();
  await expect(page.getByText("E2E Journey").first()).toBeVisible();

  // Session survives navigation across the inner dashboard.
  await page.goto("/databases");
  await expect(page.getByRole("heading", { name: "Databases", exact: true })).toBeVisible();

  // Logout clears the session; the dashboard redirects anonymous users.
  await logout(page);
  await page.goto("/");
  await page.waitForURL("**/login**");
});

test("registered account can sign in again with its password", async ({ page }) => {
  const email = `e2e-return-${Date.now()}@example.test`;
  const password = "E2e-return-123!";
  await registerAndOnboard(page, { email, password, displayName: "E2E Return" });
  await logout(page);
  await login(page, email, password);
  await expect(page.getByRole("heading", { name: "Overview" })).toBeVisible();
});

test("member can create a database and open its detail page", async ({ page }) => {
  await registerAndOnboard(page, {
    email: `e2e-db-${Date.now()}@example.test`,
    password: "E2e-dbcreate-123!",
    displayName: "E2E Db",
  });

  await page.goto("/databases");
  // Databases has no inline create panel: the floating action button is the
  // only entry point and deep-links to the auto-opened create dialog.
  await page.getByRole("link", { exact: true, name: "Create database" }).click();
  await expect(page).toHaveURL(/\/databases\?create=1$/);
  const dialog = page.getByRole("dialog", { name: "Create a database" });
  await dialog.getByLabel("Database name").fill("journey-primary");
  await dialog.getByRole("button", { exact: true, name: "Create database" }).click();
  await expect(page.getByRole("link", { name: "journey-primary" })).toBeVisible();

  await page.click('a[href^="/databases/db_"]');
  await expect(page.getByText("journey-primary").first()).toBeVisible();
});

test("member can create and revoke an API key", async ({ page }) => {
  await registerAndOnboard(page, {
    email: `e2e-key-${Date.now()}@example.test`,
    password: "E2e-apikey-123!",
    displayName: "E2E Key",
  });

  await page.goto("/api-keys?create=key");
  await page.fill("#key-name", "service credential");
  await page.fill("#key-scopes", "database:read");
  await page.getByRole("button", { name: "Create key" }).click();
  await expect(page.locator("table tbody tr").filter({ hasText: "service credential" })).toBeVisible();
  await page.getByRole("dialog", { name: "Credential secret" }).getByRole("link", { name: "Done" }).click();

  await page.locator("#credential-kind-value").click();
  await page.locator('[name="kind"] + details [data-filter-value="key"]').click();
  await expect(page).toHaveURL(/kind=key/);
  await page.locator("#credential-status-value").click();
  await page.locator('[name="status"] + details [data-filter-value="Active"]').click();
  await expect(page).toHaveURL(/kind=key.*status=Active/);
  await expect(page.locator("#credential-kind-value")).toHaveText("API keys");
  await expect(page.locator("#credential-status-value")).toHaveText("Active");
});

test("member can invite a teammate from the team page", async ({ page }) => {
  await registerAndOnboard(page, {
    email: `e2e-invite-${Date.now()}@example.test`,
    password: "E2e-invite-123!",
    displayName: "E2E Invite",
  });

  await page.goto("/access");
  await page.fill("#invite-email", "teammate@example.test");
  await page.getByRole("button", { name: "Send invite" }).click();
  await expect(page.getByText("teammate@example.test").first()).toBeVisible();
});

test("member can update profile name and read notifications", async ({ page }) => {
  await registerAndOnboard(page, {
    email: `e2e-profile-${Date.now()}@example.test`,
    password: "E2e-profile-123!",
    displayName: "E2E Profile",
  });

  await page.goto("/profile");
  await expect(page.getByRole("heading", { name: "Profile", exact: true })).toBeVisible();

  await page.goto("/notifications");
  await expect(page.getByRole("heading", { name: "Notifications" })).toBeVisible();
});
