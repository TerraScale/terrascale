import { test, expect } from "@playwright/test";
import { logout, registerAndOnboard } from "./support/journey";

type Route = { path: string; expectText: string };

const memberRoutes: Route[] = [
  { path: "/", expectText: "Overview" },
  { path: "/access", expectText: "Team" },
  { path: "/api-keys", expectText: "API keys" },
  { path: "/audit", expectText: "Logs" },
  { path: "/data", expectText: "Tables" },
  { path: "/databases", expectText: "Databases" },
  { path: "/notifications", expectText: "Notifications" },
  { path: "/organization", expectText: "Organization" },
  { path: "/organizations/new", expectText: "Create organization" },
  { path: "/profile", expectText: "Profile" },
  { path: "/settings", expectText: "Settings" },
  { path: "/subscriptions", expectText: "Subscriptions" },
  { path: "/usage", expectText: "Usage" },
];

const adminRoutes: Route[] = [
  { path: "/admin", expectText: "Users" },
  { path: "/admin/users", expectText: "Users" },
  { path: "/admin/databases", expectText: "Databases" },
  { path: "/admin/estate", expectText: "Estate" },
  { path: "/admin/organizations", expectText: "Organizations" },
  { path: "/admin/usage", expectText: "Usage" },
  { path: "/admin/pricing", expectText: "Pricing" },
];


test.beforeAll(async ({ browser }) => {
  const context = await browser.newContext();
  const page = await context.newPage();
  await registerAndOnboard(page, {
    email: `e2e-smoke-${Date.now()}@example.test`,
    password: "E2e-smoke-123!",
    displayName: "E2E Smoke",
  });
  await page.request.post("/api/v1/testing/dev-shim/reset", { data: { promoteOwner: true } });
  await context.close();
});

test("anonymous routes render", async ({ page }) => {
  for (const route of [
    { path: "/login", expectText: "Sign in" },
    { path: "/reset-password", expectText: "Reset password" },
    { path: "/onboarding", expectText: "Activate your TerraScale account" },
  ]) {
    const response = await page.goto(route.path);
    expect(response?.status(), route.path).toBeLessThan(400);
    await expect(page.getByText(route.expectText).first()).toBeVisible();
  }
});

test("anonymous dashboard access redirects to login", async ({ page }) => {
  await page.goto("/databases");
  await page.waitForURL("**/login**");
});

test("member pages render", async ({ page }) => {
  await registerAndOnboard(page, {
    email: `e2e-pages-${Date.now()}@example.test`,
    password: "E2e-pages-123!",
    displayName: "E2E Pages",
  });
  for (const route of memberRoutes) {
    const response = await page.goto(route.path);
    expect(response?.status(), route.path).toBeLessThan(400);
    // Nav routes render the shell h1; sub-pages use their own visible heading.
    await expect(
      page.locator("h1, h2").filter({ hasText: route.expectText }).first(),
      route.path,
    ).toBeVisible();
  }
  await logout(page);
});

test("promoted owner sees admin pages", async ({ page }) => {
  await registerAndOnboard(page, {
    email: `e2e-admin-${Date.now()}@example.test`,
    password: "E2e-admin-123!",
    displayName: "E2E Admin",
  });
  await page.request.post("/api/v1/testing/dev-shim/reset", { data: { promoteOwner: true } });
  for (const route of adminRoutes) {
    const response = await page.goto(route.path);
    expect(response?.status(), route.path).toBeLessThan(400);
    // Nav routes render the shell h1; sub-pages use their own visible heading.
    await expect(
      page.locator("h1, h2").filter({ hasText: route.expectText }).first(),
      route.path,
    ).toBeVisible();
  }
  await logout(page);
});
