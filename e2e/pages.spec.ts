import { mkdirSync } from "node:fs";
import { test, expect, observeBrowserErrors } from "./support/test";
import { login, registerAndOnboard } from "./support/journey";
import { captureResponsiveScreenshots } from "./support/screenshots";

const parityRouteStates = [
  { key: "overview", path: "/", heading: "Overview" },
  { key: "team", path: "/access", heading: "Team" },
  { key: "admin-databases", path: "/admin/databases", status: 403 },
  { key: "admin-estate", path: "/admin/estate", status: 403 },
  { key: "admin-usage", path: "/admin/usage", status: 403 },
  { key: "admin-users", path: "/admin/users", status: 403 },
  { key: "api-keys", path: "/api-keys", heading: "API keys" },
  { key: "audit", path: "/audit", heading: "Audit" },
  { key: "authentication", path: "/auth", heading: "Authentication" },
  { key: "auth-audit", path: "/auth/audit", heading: "Audit log" },
  { key: "auth-clients", path: "/auth/clients", heading: "OAuth clients" },
  { key: "auth-connections", path: "/auth/connections", heading: "Connections" },
  { key: "auth-keys", path: "/auth/keys", heading: "Management keys" },
  { key: "auth-memberships", path: "/auth/memberships", heading: "Memberships" },
  { key: "auth-organizations", path: "/auth/organizations", heading: "Organizations" },
  { key: "auth-quickstart", path: "/auth/quickstart", heading: "Connect in minutes" },
  { key: "auth-settings", path: "/auth/settings", heading: "Hosted UI branding" },
  { key: "auth-templates", path: "/auth/templates", heading: "JWT templates" },
  { key: "auth-users", path: "/auth/users", heading: "Users" },
  { key: "auth-webhooks", path: "/auth/webhooks", heading: "Webhooks" },
  { key: "backups", path: "/backups", heading: "Backups" },
  { key: "data", path: "/data", heading: "Saved queries" },
  { key: "data-create", path: "/data?create=1", heading: "Save query", mobileHeight: 900 },
  { key: "data-editor", path: "/data?view=editor", heading: "Document editor", mobileHeight: 900 },
  { key: "data-queries", path: "/data?view=queries", heading: "Saved queries", mobileHeight: 900 },
  { key: "data-queries-create", path: "/data?view=queries&create=1", heading: "Save query", mobileHeight: 900 },
  { key: "data-sql", path: "/data?view=sql", heading: "SQL console", mobileHeight: 900 },
  { key: "data-table", path: "/data?view=table", heading: "Collection documents", mobileHeight: 900 },
  { key: "databases", path: "/databases", heading: "Databases" },
  { key: "engine", path: "/engine", heading: "Engine" },
  { key: "history", path: "/history", heading: "Document history" },
  { key: "indexes", path: "/indexes", heading: "Indexes" },
  { key: "notifications", path: "/notifications", heading: "Notifications" },
  { key: "organization", path: "/organization", heading: "Organization" },
  { key: "profile", path: "/profile", heading: "Profile" },
  { key: "schema", path: "/schema", heading: "Schema" },
  { key: "settings", path: "/settings", heading: "Settings" },
  { key: "subscriptions", path: "/subscriptions", heading: "Shared result configuration" },
  { key: "usage", path: "/usage", heading: "Usage" },
  { key: "agent-approve", path: "/agent/approve", heading: "Approve agent access" },
  { key: "cli-approve", path: "/cli/approve", heading: "Approve CLI access" },
  { key: "login", path: "/login", heading: "Sign in" },
  { key: "onboarding", path: "/onboarding", heading: "Activate your TerraScale account" },
  { key: "reset-password", path: "/reset-password", heading: "Reset password" },
] as const;

test("member navigation works with native keyboard controls and on mobile", async ({ page }) => {
  await page.addInitScript(() => {
    document.addEventListener("datastar-ready", () => {
      document.documentElement.dataset.datastarReady = "true";
    }, { once: true });
  });
  await registerAndOnboard(page, {
    email: `e2e-navigation-${Date.now()}@example.test`,
    password: "E2e-navigation-123!",
    displayName: "E2E Navigation",
  });
  const accountMenu = page.locator("details.ts-account");
  const accountTrigger = page.locator('details.ts-account > summary[aria-label="Account menu"]');
  await accountTrigger.focus();

  await expect(accountTrigger).toBeFocused();

  const navigation = page.getByRole("navigation", { name: "Dashboard sections" });
  await expect(navigation.getByRole("link", { name: "Overview" })).toBeVisible();
  for (const label of ["Tables", "Team", "Usage", "Billing", "Settings"]) {
    await expect(navigation.getByRole("link", { name: label, exact: true })).toBeVisible();
  }
  mkdirSync("/tmp/dsh-198-after", { recursive: true });
  await page.screenshot({ path: "/tmp/dsh-198-after/member-desktop-navigation.png", fullPage: true });

  const projectMenu = page.locator("details.ts-context-menu");
  const projectTrigger = page.locator('details.ts-context-menu > summary.ts-context-trigger[aria-label="Switch organization or project, current: Personal / Personal"]');
  await projectTrigger.focus();
  await page.keyboard.press("Enter");
  await expect(projectMenu).toHaveAttribute("open", "");
  await expect(projectMenu.locator("[data-active-project-id]")).toContainText("Personal");

  await accountTrigger.focus();
  await page.keyboard.press("Enter");
  await expect(accountMenu).toHaveAttribute("open", "");
  await expect(accountMenu.getByRole("link", { name: "Profile" })).toBeVisible();
  await expect(accountMenu.getByRole("link", { name: "Administration" })).toHaveCount(0);
  await page.keyboard.press("Escape");
  await expect(accountMenu).not.toHaveAttribute("open", "");
  await expect(accountTrigger).toBeFocused();

  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.locator("html")).toHaveAttribute("data-datastar-ready", "true");
  // Keep a stable DOM locator: opening the drawer hides the FAB's ancestor
  // from accessibility queries, while the FAB itself keeps its Open label.
  const navigationToggle = page.locator("button.ts-fab-nav");
  await expect(navigationToggle).toHaveAttribute("aria-label", "Open navigation");
  await expect(navigationToggle).toHaveAttribute("aria-expanded", "false");
  await navigationToggle.click();
  await expect(page.locator(".ts-shell")).toHaveClass(/nav-open/);
  await expect(navigationToggle).toHaveAttribute("aria-label", "Open navigation");
  await expect(navigationToggle).toHaveAttribute("aria-expanded", "true");
  const closeNavigation = page.getByRole("button", { name: "Close navigation" });
  await expect(closeNavigation).toBeFocused();
  await page.screenshot({ path: "/tmp/dsh-198-after/member-mobile-navigation-open.png", fullPage: true });

  const drawer = page.getByRole("complementary", { name: "Primary navigation" });
  await page.keyboard.press("Tab");
  await expect(drawer).toContainText("Build");
  await page.keyboard.press("Escape");
  await expect(page.locator(".ts-shell")).not.toHaveClass(/nav-open/);
  await expect(navigationToggle).toHaveAttribute("aria-label", "Open navigation");
  await expect(navigationToggle).toHaveAttribute("aria-expanded", "false");
  await expect(navigationToggle).toBeFocused();
  await page.screenshot({ path: "/tmp/dsh-198-after/member-mobile-navigation-closed.png", fullPage: true });

  await page.goto("/databases");
  await captureResponsiveScreenshots(page, "data");
  const billingPage = await page.goto("/billing");
  expect(billingPage?.status()).toBe(200);
  expect(new URL(page.url()).pathname).toBe("/billing");
  await captureResponsiveScreenshots(page, "billing");
});

test("original route and data-mode states render at desktop and mobile sizes", async ({ browser, baseURL }) => {
  test.setTimeout(180_000);
  const context = await browser.newContext({ baseURL });
  const anonymousContext = await browser.newContext({ baseURL });
  const page = await context.newPage();
  const anonymousPage = await anonymousContext.newPage();
  const authenticatedBrowserErrors: string[] = [];
  const anonymousBrowserErrors: string[] = [];
  let activeDeniedURL: string | null = null;
  page.on("console", (message) => {
    if (message.type() !== "error") return;
    const expectedDeniedDocumentError =
      /^Failed to load resource: the server responded with a status of 403 \((?:Forbidden)?\)$/.test(message.text()) &&
      activeDeniedURL !== null && message.location().url === activeDeniedURL;
    if (!expectedDeniedDocumentError) authenticatedBrowserErrors.push(`console: ${message.text()}`);
  });
  page.on("pageerror", (error) => authenticatedBrowserErrors.push(`pageerror: ${error.message}`));
  anonymousPage.on("console", (message) => {
    if (message.type() === "error") anonymousBrowserErrors.push(`console: ${message.text()}`);
  });
  anonymousPage.on("pageerror", (error) => anonymousBrowserErrors.push(`pageerror: ${error.message}`));

  try {
    const account = {
      email: `e2e-parity-${Date.now()}@example.test`,
      password: "E2e-parity-123!",
      displayName: "E2E Route Parity",
    };
    await registerAndOnboard(page, account);
    const appOrigin = new URL(page.url()).origin;
    const output = "/tmp/dsh-198-after/parity";
    mkdirSync(output, { recursive: true });

    for (const route of parityRouteStates) {
      const routePage = route.key === "login" || route.key === "onboarding" ? anonymousPage : page;
      for (const viewport of [
        { name: "desktop-1440x900", width: 1440, height: 900 },
        { name: `mobile-390x${"mobileHeight" in route ? route.mobileHeight : 844}`, width: 390, height: "mobileHeight" in route ? route.mobileHeight : 844 },
      ]) {
        await routePage.setViewportSize({ width: viewport.width, height: viewport.height });
        const expectedStatus = "status" in route ? route.status : 200;
        const expectedURL = new URL(route.path, appOrigin);
        activeDeniedURL = expectedStatus === 403 ? expectedURL.href : null;
        let response: Awaited<ReturnType<typeof page.goto>>;
        try {
          response = await routePage.goto(route.path);
        } finally {
          activeDeniedURL = null;
        }
        expect(response?.status(), `${route.path} should return its expected route status`).toBe(expectedStatus);
        const finalURL = new URL(routePage.url());
        expect(`${finalURL.pathname}${finalURL.search}`, `${route.path} should remain on the requested route`).toBe(`${expectedURL.pathname}${expectedURL.search}`);
        if (expectedStatus === 403) {
          expect(response?.request().isNavigationRequest(), `${route.path} should be a top-level denied navigation`).toBe(true);
          expect(new URL(response!.url()).pathname, `${route.path} should be the denied document URL`).toBe(expectedURL.pathname);
        }
        if ("heading" in route) {
          await expect(routePage.getByRole("heading", { name: route.heading, exact: false }).first()).toBeVisible();
        }
        await routePage.screenshot({ path: `${output}/${route.key}-${viewport.name}.png`, fullPage: true });
      }
    }

    await page.setViewportSize({ width: 390, height: 900 });
    const baseDataResponse = await page.goto("/data");
    expect(baseDataResponse?.status()).toBe(200);
    expect(new URL(page.url()).pathname).toBe("/data");
    await expect(page.getByRole("heading", { name: "Saved queries", exact: false }).first()).toBeVisible();
    await page.screenshot({ path: `${output}/data-base-mobile-390x900.png`, fullPage: true });
    expect(authenticatedBrowserErrors, "unexpected authenticated browser console and uncaught page errors").toEqual([]);
    expect(anonymousBrowserErrors, "unexpected anonymous browser console and uncaught page errors").toEqual([]);
  } finally {
    await Promise.all([context.close(), anonymousContext.close()]);
  }
});

test("region map code is route-lazy and native project region controls work without JavaScript", async ({ browser, baseURL, page }) => {
  await registerAndOnboard(page, {
    email: `e2e-region-map-${Date.now()}@example.test`,
    password: "E2e-region-map-123!",
    displayName: "E2E Region Map",
  });
  const cookies = await page.context().cookies();
  const mapContext = await browser.newContext({ baseURL });
  const nativeContext = await browser.newContext({ baseURL, javaScriptEnabled: false });
  try {
    await mapContext.addCookies(cookies);
    await nativeContext.addCookies(cookies);

    const mapPage = await mapContext.newPage();
    const mapErrors = observeBrowserErrors(mapPage);
    const mapImageRequests: string[] = [];
    mapPage.on("request", (request) => {
      const path = new URL(request.url()).pathname;
      if (path === "/brand/region-map.svg") mapImageRequests.push(path);
    });

    await mapPage.goto("/");
    await expect(mapPage.getByRole("heading", { name: "Overview" })).toBeVisible();
    expect(mapImageRequests).toEqual([]);

    await mapPage.goto("/organization");
    const createProjectDisclosure = mapPage.locator("#create-project > summary");
    await createProjectDisclosure.click();
    const map = mapPage.locator('.ts-region-map[data-select-id="project-region"]');
    await expect(map).toHaveAttribute("aria-hidden", "true");
    const mapImage = map.locator("img");
    await expect(mapImage).toHaveAttribute("src", "/brand/region-map.svg");
    await expect(mapImage).toHaveAttribute("loading", "lazy");
    await mapImage.scrollIntoViewIfNeeded();
    await expect.poll(() => mapImageRequests.length).toBeGreaterThan(0);
    expect(mapErrors).toEqual([]);

    const nativePage = await nativeContext.newPage();
    const nativeErrors = observeBrowserErrors(nativePage);
    await nativePage.goto("/organization");
    await nativePage.locator("#create-project > summary").click();
    const projectForm = nativePage.locator('form[action="/api/v1/dashboard/organization/projects"]');
    const region = projectForm.getByLabel("Primary region");
    await expect(region).toBeVisible();
    const regionOptions = region.locator("option");
    expect(await regionOptions.count()).toBeGreaterThan(1);
    const selectedRegion = await regionOptions.nth(1).getAttribute("value");
    expect(selectedRegion).toBeTruthy();
    await region.selectOption(selectedRegion!);
    await expect(region).toHaveValue(selectedRegion!);
    const projectName = `E2E Native Region ${Date.now()}`;
    await projectForm.locator("#project-name").fill(projectName);

    const createProjectResponse = nativePage.waitForResponse((response) => {
      const request = response.request();
      return request.method() === "POST" && new URL(response.url()).pathname === "/api/v1/dashboard/organization/projects";
    });
    await projectForm.getByRole("button", { name: "Create project", exact: true }).click();
    expect((await createProjectResponse).status()).toBe(303);
    await expect(nativePage).toHaveURL((url) => url.pathname === "/organization");
    const createdProject = nativePage.locator("tbody tr").filter({ hasText: projectName });
    await expect(createdProject).toBeVisible();
    await createdProject.locator("details.ts-rename > summary").click();
    await expect(createdProject.getByLabel("Primary region")).toHaveValue(selectedRegion!);
    expect(nativeErrors).toEqual([]);
  } finally {
    await Promise.all([mapContext.close(), nativeContext.close()]);
  }
});

test("member accounts do not receive administration navigation or access", async ({ page }) => {
  await registerAndOnboard(page, {
    email: `e2e-member-${Date.now()}@example.test`,
    password: "E2e-member-123!",
    displayName: "E2E Member",
  });

  const accountMenu = page.locator("details.ts-account");
  await page.locator('details.ts-account > summary[aria-label="Account menu"]').click();
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
  await page.locator('details.ts-account > summary[aria-label="Account menu"]').click();
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
  await page.locator("#member-search").press("Enter");
  const patchResponse = await patchResponsePromise;
  const patchRequest = patchResponse.request();
  expect(patchRequest.isNavigationRequest()).toBe(false);
  expect(patchRequest.headers()["accept"]).toContain("text/event-stream");
  const patchQuery = new URL(patchRequest.url()).searchParams;
  expect(patchQuery.get("tab")).toBe("members");
  expect(patchQuery.get("q")).toBe(search);
  expect(patchQuery.get("role")).toBe("");
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
    expect(nativeQuery.get("role")).toBe("");
    await expect(nativePage.locator("#member-search")).toHaveValue(search);
    await expect(nativePage.locator("#access-page tbody tr").filter({ hasText: account.displayName })).toHaveCount(1);
    expect(nativeErrors).toEqual([]);
  } finally {
    await nativeContext.close();
  }
});
