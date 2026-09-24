import { test, expect } from "@playwright/test";
import { login, logout, registerAndOnboard, registerAndOpenPaymentStep } from "./support/journey";

type Route = { path: string; expectText: string };

const memberRoutes: Route[] = [
  { path: "/", expectText: "Overview" },
  { path: "/access", expectText: "Team" },
  { path: "/api-keys", expectText: "API keys" },
  { path: "/audit", expectText: "Audit" },
  { path: "/data", expectText: "Tables" },
  { path: "/databases", expectText: "Databases" },
  { path: "/notifications", expectText: "Notifications" },
  { path: "/organization", expectText: "Organization" },
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
  ]) {
    const response = await page.goto(route.path);
    expect(response?.status(), route.path).toBeLessThan(400);
    await expect(page.getByText(route.expectText).first()).toBeVisible();
  }

  // Every onboarding step (registration included) keeps only the TerraScale
  // wordmark in its page header; the localized title stays in the document as a
  // screen-reader-only h1.
  const response = await page.goto("/onboarding");
  expect(response?.status(), "/onboarding").toBeLessThan(400);
  await expect(page.locator(".ts-onboarding-header .ts-brand-logo")).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "Activate your TerraScale account" }),
  ).toBeVisible();
});

test("signed-in onboarding steps keep the logo-only page header", async ({ page }) => {
  await registerAndOpenPaymentStep(page, {
    email: `e2e-header-${Date.now()}@example.test`,
    password: "E2e-header-123!",
    displayName: "E2E Header",
  });

  // Payment step: wordmark only — no lock badge, eyebrow, or visible title.
  await expect(page.locator(".ts-onboarding-header .ts-brand-logo")).toBeVisible();
  await expect(page.locator(".ts-onboarding-lock")).toHaveCount(0);
  await expect(page.locator(".ts-onboarding-title")).toHaveCount(0);
  await expect(page.getByText("TERRASCALE SETUP")).toHaveCount(0);

  // Project step reuses the same wordmark-only header.
  await page.getByRole("button", { name: "Continue without payment" }).click();
  await page.waitForURL((url) => url.pathname === "/onboarding");
  await expect(page.locator(".ts-onboarding-header .ts-brand-logo")).toBeVisible();
  await expect(page.getByText("TERRASCALE SETUP")).toHaveCount(0);
});

test("onboarding password visibility toggles reveal and hide each field", async ({ page }) => {
  await page.goto("/onboarding");

  for (const id of ["password", "confirmPassword"]) {
    const input = page.locator(`#${id}`);
    const toggle = page.locator(`#${id} + button`);
    // Eye-off is the only icon whose path draws the diagonal strike.
    const eyeOffIcon = toggle.locator('span:visible svg path[d="m2 2 20 20"]');

    // Regression: the eye and eye-off icons must never be visible together.
    await expect(toggle).toHaveAttribute("aria-label", "Show password");
    await expect(toggle).toHaveAttribute("aria-pressed", "false");
    await expect(toggle.locator("span:visible")).toHaveCount(1);
    await expect(eyeOffIcon).toHaveCount(0);

    await toggle.click();
    await expect(input).toHaveAttribute("type", "text");
    await expect(toggle).toHaveAttribute("aria-pressed", "true");
    await expect(toggle).toHaveAttribute("aria-label", "Hide password");
    await expect(toggle.locator("span:visible")).toHaveCount(1);
    await expect(eyeOffIcon).toHaveCount(1);

    await toggle.click();
    await expect(input).toHaveAttribute("type", "password");
    await expect(toggle).toHaveAttribute("aria-pressed", "false");
  }
});

test("onboarding payment step keeps monthly and annual pricing separate", async ({ page }) => {
  await registerAndOpenPaymentStep(page, {
    email: `e2e-interval-${Date.now()}@example.test`,
    password: "E2e-interval-123!",
    displayName: "E2E Interval",
  });

  await expect(page.locator(".ts-onboarding-plan")).toHaveCount(3);

  // Each card shows exactly one price row: the one for the active interval.
  // Both rows visible means the Datastar module never loaded (a broken
  // static-asset response makes the browser reject it).
  const visiblePrices = () =>
    page.$$eval(".ts-onboarding-plan", (plans) =>
      plans.map((plan) =>
        [...plan.querySelectorAll(".ts-onboarding-plan-price-row")]
          .filter((row) => row.checkVisibility())
          .map((row) => row.innerText.replace(/\s+/g, " ").trim()),
      ),
    );

  await expect.poll(visiblePrices).toEqual([["$5 / month"], ["$50 / month"], ["$99 / month"]]);

  // The payment section head holds the only billing interval control.
  const interval = page.locator(".ts-onboarding-interval:visible");
  await expect(interval).toHaveCount(1);
  const monthly = interval.getByRole("button", { name: "Monthly" });
  const annual = interval.getByRole("button", { name: "Annual" });
  await expect(monthly).toHaveAttribute("aria-pressed", "true");
  await expect(annual).toHaveAttribute("aria-pressed", "false");

  await annual.click();
  await expect(annual).toHaveAttribute("aria-pressed", "true");
  await expect(monthly).toHaveAttribute("aria-pressed", "false");
  await expect.poll(visiblePrices).toEqual([["$50 / year"], ["$500 / year"], ["$990 / year"]]);

  await monthly.click();
  await expect.poll(visiblePrices).toEqual([["$5 / month"], ["$50 / month"], ["$99 / month"]]);

  // Plan cards stay a radio group while the interval changes the price only.
  await page.locator(".ts-onboarding-plan-pro").click();
  await expect(page.locator(".ts-onboarding-plan-pro")).toHaveAttribute("aria-checked", "true");
  await expect(page.locator(".ts-onboarding-plan-starter")).toHaveAttribute("aria-checked", "false");
});

test("anonymous dashboard access redirects to login", async ({ page }) => {
  await page.goto("/databases");
  await page.waitForURL("**/login**");
});

test("account menu opens upward from the sidebar footer", async ({ page }) => {
  await registerAndOnboard(page, {
    email: `e2e-nav-menu-${Date.now()}@example.test`,
    password: "E2e-nav-menu-123!",
    displayName: "E2E Nav Menu",
  });

  const trigger = page.locator("summary.ts-account-trigger");
  const panel = page.locator("details.ts-account .ts-menu-panel");
  await trigger.click();
  await expect(panel).toBeVisible();

  const triggerBox = (await trigger.boundingBox())!;
  const panelBox = (await panel.boundingBox())!;
  const viewport = page.viewportSize()!;

  // Regression: the panel used to render below the trigger, off the bottom of
  // the viewport, so only a sliver stayed visible.
  expect(panelBox.y + panelBox.height).toBeLessThanOrEqual(triggerBox.y);
  expect(panelBox.y).toBeGreaterThanOrEqual(0);
  expect(panelBox.y + panelBox.height).toBeLessThanOrEqual(viewport.height);

  await logout(page);
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
    // Nav routes keep the shell h1 as a screen-reader-only heading (the
    // visible title was removed in DSH-125); sub-pages use their own heading.
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
  const shim = await page.request.post("/api/v1/testing/dev-shim/reset", { data: { promoteOwner: true } });
  if (shim.ok()) {
    await page.reload();
  } else {
    // Deployed previews run without the DASHBOARD_E2E dev shim, so the fresh
    // account cannot be promoted; the seeded demo account is already a
    // platform owner and exercises the same admin surfaces.
    await logout(page);
    await login(page, "mario@terrascale.tech", "terrascale");
  }

  await page.getByLabel("Account menu").click();
  const adminTabPromise = page.waitForEvent("popup");
  await page.getByRole("link", { name: "Admin", exact: true }).click();
  const adminTab = await adminTabPromise;
  await adminTab.waitForURL("**/admin/users");
  await expect(adminTab.getByRole("navigation", { name: "Admin sections" })).toBeVisible();
  await expect(adminTab.getByRole("link", { name: "Back to dashboard" })).toBeVisible();
  await expect(page).not.toHaveURL(/\/admin/);
  await adminTab.close();
  for (const route of adminRoutes) {
    const response = await page.goto(route.path);
    expect(response?.status(), route.path).toBeLessThan(400);
    // Nav routes keep the shell h1 as a screen-reader-only heading (the
    // visible title was removed in DSH-125); sub-pages use their own heading.
    await expect(
      page.locator("h1, h2").filter({ hasText: route.expectText }).first(),
      route.path,
    ).toBeVisible();
  }
  await logout(page);
});
