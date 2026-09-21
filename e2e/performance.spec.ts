import { test, expect } from "@playwright/test";

test.beforeEach(async ({ page }) => {
  await page.request.post("/api/v1/auth/password/login", {
    form: { email: "mario@terrascale.tech", password: "terrascale" },
  });
});

test("critical resources reuse the font and versioned assets", async ({ page }) => {
  await page.goto("/");
  await page.evaluate(() => document.fonts.ready);
  const css = await page.locator('link[rel="stylesheet"]').getAttribute("href");
  expect(css).toMatch(/^\/css\/terrascale(?:\.[a-z0-9]+)?\.css$/);
  const response = await page.request.get(css!, { headers: { "Accept-Encoding": "br, gzip" } });
  expect(response.ok()).toBeTruthy();
  expect(response.headers()["etag"]).toBeTruthy();
  expect(response.headers()["content-encoding"]).toMatch(/^(br|gzip)$/);
  if (css !== "/css/terrascale.css") expect(response.headers()["cache-control"]).toContain("immutable");
  const font = await page.evaluate(() => performance.getEntriesByType("resource")
    .filter(entry => entry.name.includes("oxanium")));
  expect(font).toHaveLength(1);
  const conditional = await page.request.get(css!, {
    headers: { "Accept-Encoding": "br, gzip", "If-None-Match": response.headers()["etag"] },
  });
  expect(conditional.status()).toBe(304);
});

test("speculation is restricted and does not change the account", async ({ page }) => {
  await page.goto("/settings");
  const candidates = await page.locator("a[data-prefetch]").evaluateAll(
    links => links.map(link => link.getAttribute("href")));
  expect(candidates.sort()).toEqual(["/", "/data", "/databases"]);
  expect(await page.locator('script[type="speculationrules"]').textContent()).not.toContain("prerender");
  const before = await page.request.get("/organization");
  for (const path of candidates) {
    const response = await page.request.get(path!, { headers: { "Sec-Purpose": "prefetch" } });
    expect(response.status()).toBe(200);
  }
  for (const path of ["/api-keys", "/checkout/session", "/data?view=sql", "/databases?create=1", "/login"]) {
    expect((await page.request.get(path, { headers: { "Sec-Purpose": "prefetch" } })).status()).toBe(204);
  }
  const after = await page.request.get("/organization");
  expect(await after.text()).toBe(await before.text());
});

test("Chrome consumes an intent prefetch and mutations invalidate it", async ({ page }) => {
  const cdp = await page.context().newCDPSession(page);
  await cdp.send("Preload.enable");
  const statuses: string[] = [];
  const requests: { id: string; status: string }[] = [];
  cdp.on("Preload.prefetchStatusUpdated", event => {
    if (event.prefetchUrl.endsWith("/databases")) {
      statuses.push(event.prefetchStatus);
      requests.push({ id: event.requestId, status: event.prefetchStatus });
    }
  });
  await page.goto("/");
  await page.locator('.ts-nav a[href="/databases"]').hover();
  await expect.poll(() => statuses).toContain("PrefetchSuccessfulButNotUsed");
  await page.locator('.ts-nav a[href="/databases"]').click();
  await page.waitForURL("**/databases");
  await expect.poll(() => statuses).toContain("PrefetchResponseUsed");
  await page.mouse.move(1400, 800);
  statuses.length = 0;
  await page.goto("/");
  await page.locator('.ts-nav a[href="/databases"]').hover();
  await expect.poll(() => statuses).toContain("PrefetchSuccessfulButNotUsed");
  const oldRequestId = requests.at(-1)!.id;
  // Use a real browser request so Clear-Site-Data is processed by Chromium.
  // An APIRequestContext response does not run browser header side effects.
  await page.evaluate(async () => {
    const response = await fetch("/api/v1/dashboard/switch-project", {
      method: "POST", body: new URLSearchParams({ projectId: "missing" }),
    });
    if (!response.ok) throw new Error("Mutation failed");
  });
  await page.locator('.ts-nav a[href="/databases"]').click();
  await page.waitForURL("**/databases");
  await expect.poll(() => requests.filter(request => request.id === oldRequestId).map(request => request.status))
    .toContain("PrefetchEvictedAfterBrowsingDataRemoved");
  expect(requests.some(request => request.id === oldRequestId && request.status === "PrefetchResponseUsed")).toBeFalsy();
});

test("navigation feedback respects anchors, prevented events and confirmation cancellation", async ({ page }) => {
  await page.goto("/");
  await page.evaluate(() => {
    const anchor = document.createElement("a");
    anchor.href = "#test-anchor";
    anchor.id = "test-anchor";
    anchor.textContent = "Anchor";
    document.body.append(anchor);
    anchor.click();
  });
  await expect(page.locator("html")).not.toHaveAttribute("data-nav-pending");
  await page.evaluate(() => {
    const link = document.querySelector<HTMLAnchorElement>('a[href="/databases"]')!;
    link.addEventListener("click", event => event.preventDefault(), { once: true });
    link.click();
  });
  await expect(page.locator("html")).not.toHaveAttribute("data-nav-pending");
  page.once("dialog", dialog => dialog.dismiss());
  await page.evaluate(() => {
    const form = document.createElement("form");
    form.dataset.confirm = "Cancel this test";
    form.method = "post";
    form.action = "/not-a-real-action";
    document.body.append(form);
    form.requestSubmit();
  });
  await expect(page.locator("html")).not.toHaveAttribute("data-nav-pending");
});

test("native navigation displays feedback and restores cleanly", async ({ page }) => {
  await page.goto("/");
  let pendingObserved = false;
  await page.exposeFunction("recordPending", () => { pendingObserved = true; });
  await page.evaluate(() => {
    new MutationObserver(() => {
      if (document.documentElement.hasAttribute("data-nav-pending")) {
        (window as unknown as { recordPending(): void }).recordPending();
      }
    }).observe(document.documentElement, { attributes: true, attributeFilter: ["data-nav-pending"] });
  });
  await page.route("**/databases", async route => {
    await new Promise(resolve => setTimeout(resolve, 500));
    await route.continue();
  });
  await page.locator('.ts-nav a[href="/databases"]').click({ noWaitAfter: true });
  await page.waitForURL("**/databases");
  expect(pendingObserved).toBeTruthy();
  await expect(page.locator("html")).not.toHaveAttribute("data-nav-pending");
  await page.goBack();
  await expect(page.locator("html")).not.toHaveAttribute("data-nav-pending");
});

test("reduced motion and JavaScript-free links preserve MPA navigation", async ({ browser, baseURL }) => {
  const context = await browser.newContext({ baseURL, javaScriptEnabled: false, reducedMotion: "reduce" });
  try {
    await context.request.post("/api/v1/auth/password/login", {
      form: { email: "mario@terrascale.tech", password: "terrascale" },
    });
    const page = await context.newPage();
    await page.goto("/");
    await page.locator('.ts-nav a[href="/databases"]').click();
    await expect(page.locator("h1")).toHaveText("Databases");
  } finally {
    await context.close();
  }
});

test("mobile shell and flow bindings initialize without script errors", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(error.message));
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/");
  await page.getByRole("button", { name: "Open navigation" }).click();
  await expect(page.locator(".ts-shell")).toHaveClass(/nav-open/);
  await expect(page.getByRole("button", { name: "Open navigation" })).toHaveAttribute("aria-expanded", "true");
  await page.locator(".ts-nav-backdrop").click({ position: { x: 350, y: 200 } });
  await expect(page.locator(".ts-shell")).not.toHaveClass(/nav-open/);
  await page.request.post("/api/v1/auth/logout");
  await page.context().clearCookies();
  await page.goto("/onboarding");
  await page.getByRole("button", { name: "Show password", exact: true }).first().click();
  await expect(page.locator("#password")).toHaveAttribute("type", "text");
  expect(errors).toEqual([]);
});

test("deferred read-only charts render when scrolled into view", async ({ page }) => {
  await page.goto("/usage");
  const charts = page.locator(".ts-render-deferred");
  await expect(charts).toHaveCount(4);
  for (const chart of await charts.all()) {
    await chart.scrollIntoViewIfNeeded();
    await expect(chart.locator("h3")).toBeVisible();
    expect(await chart.evaluate(node => node.getBoundingClientRect().height)).toBeGreaterThan(50);
  }
});
