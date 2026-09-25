import { mkdirSync } from "node:fs";
import { test, expect } from "./support/test";
import { login, logout, registerAndOnboard, type AccountCredentials } from "./support/journey";
import { captureResponsiveScreenshots } from "./support/screenshots";

function credentials(label: string): AccountCredentials {
  return {
    email: `e2e-${label}-${Date.now()}@example.test`,
    password: "E2e-journey-123!",
    displayName: `E2E ${label}`,
  };
}

async function dashboardSessionToken(page: import("@playwright/test").Page): Promise<string> {
  const session = (await page.context().cookies()).find((cookie) => cookie.name === "ts_dashboard_session");
  expect(session, "account creation must establish an HttpOnly dashboard session").toBeDefined();
  return session!.value;
}

test("fresh account registration, demo onboarding, session, and sign-in work end to end", async ({ page }) => {
  const account = credentials("journey");
  mkdirSync("/tmp/dsh-198-after", { recursive: true });
  await page.goto("/login");
  const highlightedMap = page.locator('.ts-region-map[data-highlights="SFO,GRU,FRA"]');
  await expect(highlightedMap).toHaveAttribute("aria-hidden", "true");
  await expect(highlightedMap.locator(".ts-region-node")).toHaveCount(3);
  const mapImage = highlightedMap.locator("img.ts-region-map-image");
  await expect(mapImage).toHaveAttribute("src", "/brand/region-map.svg");
  await expect(mapImage).toHaveAttribute("loading", "lazy");
  await captureResponsiveScreenshots(page, "login");
  const loginPassword = page.locator("#password");
  const passwordToggle = page.locator('button[data-password-toggle][aria-controls="password"]');
  await expect(passwordToggle).toBeHidden();
  await loginPassword.fill("reveal-control-check");
  await expect(passwordToggle).toBeVisible();
  await expect(passwordToggle).toHaveAccessibleName("Show password");
  await passwordToggle.click();
  await expect(loginPassword).toHaveAttribute("type", "text");
  await expect(passwordToggle).toHaveAttribute("aria-pressed", "true");
  await expect(passwordToggle).toHaveAccessibleName("Hide password");
  await passwordToggle.press("Space");
  await expect(loginPassword).toHaveAttribute("type", "password");
  await expect(passwordToggle).toHaveAttribute("aria-pressed", "false");
  await expect(passwordToggle).toHaveAccessibleName("Show password");
  await loginPassword.clear();
  await expect(passwordToggle).toBeHidden();
  await page.goto("/onboarding");
  await captureResponsiveScreenshots(page, "onboarding");
  await registerAndOnboard(page, account);

  await captureResponsiveScreenshots(page, "dashboard");
  await expect(page.getByRole("heading", { name: "Overview" })).toBeVisible();
  await expect(page.getByText(account.displayName).first()).toBeVisible();
  await page.screenshot({ path: "/tmp/dsh-198-after/member-desktop.png", fullPage: true });

  const currentUser = await page.request.get("/api/v1/auth/me");
  expect(currentUser.status()).toBe(200);
  expect(await currentUser.json()).toMatchObject({
    user: { email: account.email.toLowerCase(), name: account.displayName, onboardingCompleted: true },
  });

  await logout(page);
  await login(page, account.email, account.password);
  await expect(page.getByRole("heading", { name: "Overview" })).toBeVisible();
});

test("a database and project key stay inside the account that created them", async ({ browser, baseURL }) => {
  const owner = await browser.newContext({ baseURL });
  const other = await browser.newContext({ baseURL });
  const anonymous = await browser.newContext({ baseURL });
  try {
    const ownerPage = await owner.newPage();
    const ownerAccount = credentials("tenant-a");
    await registerAndOnboard(ownerPage, ownerAccount);
    await ownerPage.goto("/databases");

    const projectId = await ownerPage.locator("main[data-project-id]").getAttribute("data-project-id");
    expect(projectId, "database page must expose its selected project identity").toBeTruthy();

    const databaseDisclosure = ownerPage.locator("#create-database");
    await databaseDisclosure.locator(":scope > summary").click();
    await expect(databaseDisclosure).toHaveAttribute("open", "");
    const create = ownerPage.locator('form[action="/api/v1/dashboard/databases"]');
    const databaseName = `e2e-${Date.now()}`;
    await create.locator('[name="name"]').fill(databaseName);
    await create.locator('[name="region"]').selectOption("GRU");
    await create.getByRole("button", { name: "Create database" }).click();
    await ownerPage.waitForURL((url) => url.pathname.startsWith("/databases/db_"));
    await expect(ownerPage.getByRole("heading", { name: databaseName })).toBeVisible();

    const databaseResponse = await ownerPage.request.get("/api/v1/dashboard/databases");
    expect(databaseResponse.status()).toBe(200);
    const databases = await databaseResponse.json() as { id: string; name: string }[];
    const database = databases.find((item) => item.name === databaseName);
    expect(database, "created database must remain listed for its owning account").toBeDefined();

    const session = await dashboardSessionToken(ownerPage);
    const keyPath = `/api/v1/projects/${projectId}/auth/api-keys`;
    const createdKeyResponse = await ownerPage.request.post(keyPath, {
      headers: { Authorization: `Bearer ${session}` },
      data: { name: "journey key", scopes: ["database:read"], databaseId: database!.id },
    });
    expect(createdKeyResponse.status()).toBe(201);
    const createdKey = await createdKeyResponse.json() as {
      apiKey: { databaseId: string; scopes: string[]; isActive: boolean };
      secret: string;
    };
    expect(createdKey.secret).toMatch(/^tsk_demo_/);
    expect(createdKey.apiKey).toMatchObject({ databaseId: database!.id, scopes: ["database:read"], isActive: true });

    const listedKeysResponse = await ownerPage.request.get(keyPath, {
      headers: { Authorization: `Bearer ${session}` },
    });
    expect(listedKeysResponse.status()).toBe(200);
    expect(await listedKeysResponse.json()).toMatchObject({ apiKeys: [expect.objectContaining({ name: "journey key" })] });

    const otherPage = await other.newPage();
    await registerAndOnboard(otherPage, credentials("tenant-b"));
    await otherPage.goto("/databases");
    const otherDatabases = await otherPage.request.get("/api/v1/dashboard/databases");
    expect(otherDatabases.status()).toBe(200);
    expect(await otherDatabases.json()).toEqual([]);

    const otherSession = await dashboardSessionToken(otherPage);
    const crossAccountKeys = await otherPage.request.get(keyPath, {
      headers: { Authorization: `Bearer ${otherSession}` },
    });
    expect(crossAccountKeys.status()).toBe(404);

    const anonymousPage = await anonymous.newPage();
    const anonymousKeys = await anonymousPage.request.get(keyPath);
    expect(anonymousKeys.status()).toBe(401);
  } finally {
    await owner.close();
    await other.close();
    await anonymous.close();
  }
});
