import { mkdirSync } from "node:fs";
import { test, expect, observeBrowserErrors } from "./support/test";
import { login, registerAndOnboard } from "./support/journey";
import { captureResponsiveScreenshots } from "./support/screenshots";

test("member navigation works with native keyboard controls and on mobile", async ({ page }) => {
  await registerAndOnboard(page, {
    email: `e2e-navigation-${Date.now()}@example.test`,
    password: "E2e-navigation-123!",
    displayName: "E2E Navigation",
  });

  const navigation = page.getByRole("navigation", { name: "Dashboard sections" });
  await expect(navigation.getByRole("link", { name: "Overview" })).toBeVisible();
  for (const label of ["Data", "Usage", "Access", "Billing", "Settings"]) {
    await expect(navigation.getByRole("link", { name: label, exact: true })).toBeVisible();
  }
  mkdirSync("/tmp/dsh-198-after", { recursive: true });
  await page.screenshot({ path: "/tmp/dsh-198-after/member-desktop-navigation.png", fullPage: true });

  const projectMenu = page.locator("details.ts-project-menu:not(.ts-organization-menu)");
  const projectTrigger = projectMenu.locator(":scope > summary");
  await projectTrigger.focus();
  await page.keyboard.press("Enter");
  await expect(projectMenu).toHaveAttribute("open", "");
  await expect(projectMenu.locator("[data-active-project-id]")).toContainText("Personal");

  const accountMenu = page.locator("details.ts-account");
  const accountTrigger = accountMenu.locator(":scope > summary");
  await accountTrigger.focus();
  await page.keyboard.press("Enter");
  await expect(accountMenu).toHaveAttribute("open", "");
  await expect(accountMenu.getByRole("link", { name: "Profile" })).toBeVisible();
  await expect(accountMenu.getByRole("link", { name: "Administration" })).toHaveCount(0);

  await page.setViewportSize({ width: 390, height: 844 });
  const navigationToggle = page.locator(".ts-fab-nav");
  await expect(navigationToggle).toHaveAttribute("aria-expanded", "false");
  await navigationToggle.click();
  await expect(page.locator(".ts-shell")).toHaveClass(/nav-open/);
  await expect(navigationToggle).toHaveAttribute("aria-expanded", "true");
  const closeNavigation = page.getByRole("button", { name: "Close navigation" });
  await expect(closeNavigation).toBeFocused();
  await page.screenshot({ path: "/tmp/dsh-198-after/member-mobile-navigation-open.png", fullPage: true });

  const drawer = page.getByRole("complementary", { name: "Primary navigation" });
  await page.keyboard.press("Tab");
  await expect(drawer).toContainText("Workspace");
  await page.keyboard.press("Escape");
  await expect(page.locator(".ts-shell")).not.toHaveClass(/nav-open/);
  await expect(navigationToggle).toHaveAttribute("aria-expanded", "false");
  await expect(navigationToggle).toBeFocused();
  await page.screenshot({ path: "/tmp/dsh-198-after/member-mobile-navigation-closed.png", fullPage: true });

  await page.goto("/databases");
  await captureResponsiveScreenshots(page, "data");
  await page.goto("/subscriptions");
  await captureResponsiveScreenshots(page, "billing");
});

test("member accounts do not receive administration navigation or access", async ({ page }) => {
  await registerAndOnboard(page, {
    email: `e2e-member-${Date.now()}@example.test`,
    password: "E2e-member-123!",
    displayName: "E2E Member",
  });

  const accountMenu = page.locator("details.ts-account");
  await accountMenu.locator(":scope > summary").click();
  await expect(accountMenu).toHaveAttribute("open", "");
  await expect(accountMenu.getByRole("link", { name: "Administration" })).toHaveCount(0);

  const response = await page.request.get("/admin", { maxRedirects: 0 });
  expect([302, 303, 401, 403, 404]).toContain(response.status());
});

test("the configured demo administrator receives administration navigation", async ({ page }) => {
  const email = process.env.DASHBOARD_E2E_ADMIN_EMAIL ?? process.env.DASHBOARD_DEMO_ADMIN_EMAIL ?? "e2e-admin-seed@example.test";
  const password = process.env.DASHBOARD_E2E_ADMIN_PASSWORD ?? process.env.DASHBOARD_DEMO_ADMIN_PASSWORD ?? "E2e-admin-seed-123!";
  await login(page, email, password);

  const accountMenu = page.locator("details.ts-account");
  await accountMenu.locator(":scope > summary").click();
  await expect(accountMenu).toHaveAttribute("open", "");
  await page.getByRole("link", { name: "Administration" }).click();
  await expect(page).toHaveURL(/\/admin(?:\/|$)/);
  const adminNavigation = page.getByRole("navigation", { name: "Admin sections" });
  await expect(adminNavigation.getByRole("link", { name: "Users", exact: true })).toBeVisible();
  await expect(adminNavigation.getByRole("link", { name: "Organizations", exact: true })).toBeVisible();
  await expect(adminNavigation.getByRole("link", { name: "Plans", exact: true })).toBeVisible();
});

test("member filtering patches the team page through Datastar without a document navigation", async ({ page }) => {
  const account = {
    email: `e2e-datastar-${Date.now()}@example.test`,
    password: "E2e-datastar-123!",
    displayName: `E2E Datastar ${Date.now()}`,
  };
  await registerAndOnboard(page, account);

  const pageResponse = await page.goto("/access?tab=members");
  expect(pageResponse?.status()).toBe(200);
  const contentSecurityPolicy = pageResponse?.headers()["content-security-policy"] ?? "";
  const pageHTML = await pageResponse!.text();
  const nonce = pageHTML.match(/<html[^>]*data-nonce="([^"]+)"/u)?.[1];
  expect(nonce, "the team page must expose its per-response script nonce").toBeTruthy();
  expect(contentSecurityPolicy).toContain(`'nonce-${nonce}'`);

  await page.evaluate(() => {
    const target = window as Window & { __cspViolations?: string[] };
    target.__cspViolations = [];
    document.addEventListener("securitypolicyviolation", (event) => {
      target.__cspViolations?.push(event.violatedDirective);
    });
  });
  const documentNavigations: string[] = [];
  page.on("framenavigated", (frame) => {
    if (frame === page.mainFrame()) documentNavigations.push(frame.url());
  });

  const search = account.displayName;
  await page.locator("#member-search").fill(search);
  const patchResponsePromise = page.waitForResponse((response) => {
    const request = response.request();
    const url = new URL(response.url());
    return url.pathname === "/access" && url.searchParams.get("q") === search &&
      request.method() === "GET" && request.headers()["datastar-request"] === "true";
  });
  await page.locator("#access-filter-form").getByRole("button", { name: "Search" }).click();
  const patchResponse = await patchResponsePromise;
  const patchRequest = patchResponse.request();
  expect(patchRequest.isNavigationRequest()).toBe(false);
  expect(patchRequest.headers()["accept"]).toContain("text/event-stream");
  const patchQuery = new URL(patchRequest.url()).searchParams;
  expect(patchQuery.get("tab")).toBe("members");
  expect(patchQuery.get("q")).toBe(search);
  expect(patchQuery.get("role")).toBe("all");
  expect(patchResponse.status()).toBe(200);
  expect(patchResponse.headers()["content-type"]).toContain("text/event-stream");

  const patch = await patchResponse.text();
  expect(patch).toContain("event: datastar-patch-elements");
  expect(patch).toContain('id="access-page"');
  await expect(page.locator("#member-search")).toHaveValue(search);
  await expect(page.locator("#access-page tbody tr").filter({ hasText: account.displayName })).toHaveCount(1);
  await expect(page.locator("#access-page")).toHaveCount(1);
  expect(documentNavigations).toEqual([]);
  expect(await page.evaluate(() => (window as Window & { __cspViolations?: string[] }).__cspViolations ?? [])).toEqual([]);
});

test("member filtering still submits as a native GET without JavaScript", async ({ browser, baseURL, page }) => {
  const account = {
    email: `e2e-native-filter-${Date.now()}@example.test`,
    password: "E2e-native-filter-123!",
    displayName: `E2E Native Filter ${Date.now()}`,
  };
  await registerAndOnboard(page, account);

  const nativeContext = await browser.newContext({ baseURL, javaScriptEnabled: false });
  try {
    await nativeContext.addCookies(await page.context().cookies());
    const nativePage = await nativeContext.newPage();
    const nativeErrors = observeBrowserErrors(nativePage);
    await nativePage.goto("/access?tab=members");

    const search = account.displayName;
    await nativePage.locator("#member-search").fill(search);
    const navigationResponsePromise = nativePage.waitForResponse((response) => {
      const request = response.request();
      const url = new URL(response.url());
      return url.pathname === "/access" && url.searchParams.get("q") === search && request.isNavigationRequest();
    });
    await nativePage.locator("#member-search").press("Enter");
    const navigationResponse = await navigationResponsePromise;

    expect(navigationResponse.status()).toBe(200);
    expect(navigationResponse.headers()["content-type"]).toContain("text/html");
    expect(navigationResponse.request().headers()["datastar-request"]).toBeUndefined();
    const nativeQuery = new URL(nativePage.url()).searchParams;
    expect(nativeQuery.get("tab")).toBe("members");
    expect(nativeQuery.get("q")).toBe(search);
    expect(nativeQuery.get("role")).toBe("all");
    await expect(nativePage.locator("#member-search")).toHaveValue(search);
    await expect(nativePage.locator("#access-page tbody tr").filter({ hasText: account.displayName })).toHaveCount(1);
    expect(nativeErrors).toEqual([]);
  } finally {
    await nativeContext.close();
  }
});
