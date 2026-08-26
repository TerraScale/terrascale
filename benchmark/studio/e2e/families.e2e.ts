// Data families page (DB-06): family-aware views for counters, time-series,
// and attachments over one document — per-document family panels with entry
// paging, incremental time-series range selection, and counter values with
// their update semantics labeled — all through typed family operations, with
// the remaining FamilyKinds explicitly marked not yet viewable.
//
// Two fixtures are exercised:
// - the shared DB-02 fixture (core): the honest pre-SE-08 state — the core
//   serves family.read as one opaque state per kind/scope pair, so the panels
//   render their unrecognized notice instead of coercing — plus the
//   session-gate redirect;
// - the families fixture (app/families/fixtures/families-demo.mjs): the
//   unmodified core composed with the per-document family state SE-08 will
//   land, seeded through typed family.write envelopes, plus failure and delay
//   controls — used until the fixture core grows them (recorded as a
//   collision action).

import { expect, test } from "@playwright/test";
import { spawn, type ChildProcess } from "node:child_process";
import { createServer as createTcpServer } from "node:net";

import { STUDIO_ROOT, readRuntimeFile } from "./runtime";

const MINUTE_MS = 60_000;
const T0 = 1_750_000_000_000;

const NOT_YET_VIEWABLE_KINDS = [
  "revisions",
  "compare-exchange",
  "identities",
  "lifecycle",
  "archival",
  "validation",
  "tombstones",
];

async function allocatePort(): Promise<number> {
  return new Promise((resolveListen, rejectListen) => {
    const server = createTcpServer();
    server.once("error", rejectListen);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      if (address === null || typeof address === "string") {
        server.close(() => rejectListen(new Error("no port allocated")));
        return;
      }
      const { port } = address;
      server.close(() => resolveListen(port));
    });
  });
}

async function waitForHttp(url: string, timeoutMs: number): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  let lastError = "no attempt";
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url, { redirect: "manual" });
      if (response.status === 200) {
        return;
      }
      lastError = `HTTP ${response.status}`;
    } catch (error) {
      lastError = error instanceof Error ? error.message : String(error);
    }
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 150));
  }
  throw new Error(`waiting for ${url} timed out: ${lastError}`);
}

async function typedCall<T>(
  baseUrl: string,
  op: string,
  request: Record<string, unknown>,
): Promise<T> {
  const response = await fetch(`${baseUrl}/api/v2/operations/${op}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      v: 2,
      op,
      correlationId: `e2e-db06-${op}-${Date.now()}-${Math.random().toString(16).slice(2)}`,
      causationId: null,
      idempotencyKey: null,
      request,
    }),
  });
  const body = (await response.json()) as { result?: T; error?: { code: string; message: string } };
  if (body.error !== undefined) {
    throw new Error(`${op} failed: ${body.error.code} ${body.error.message}`);
  }
  return body.result as T;
}

async function familiesControl(baseUrl: string, body: Record<string, unknown>): Promise<void> {
  const response = await fetch(`${baseUrl}/api/v2/_fixture/families-control`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body),
  });
  expect(response.ok, `families control ${JSON.stringify(body)} failed`).toBeTruthy();
}

test.describe("data families on the shared fixture (core)", () => {
  test("the families index renders the closed kind set with the honest split", async ({ page }) => {
    const { studioUrl } = readRuntimeFile();
    await page.goto(studioUrl);
    await page.getByRole("link", { name: "Families" }).click();
    await expect(page).toHaveURL(new RegExp("/families$"));
    await expect(page.getByRole("heading", { name: "Families" })).toBeVisible();

    // The three viewable kinds, each with its semantics label.
    for (const kind of ["attachments", "counters", "time-series"]) {
      await expect(
        page.getByTestId("family-kind-viewable").getByText(kind, { exact: true }),
      ).toBeVisible();
    }
    await expect(page.getByTestId("family-kind-viewable")).toContainText(
      "Add a signed delta to a counter, creating it from zero.",
    );

    // The remaining seven kinds: explicitly not yet viewable, non-interactive.
    for (const kind of NOT_YET_VIEWABLE_KINDS) {
      const chip = page.getByTestId(`family-kind-${kind}`);
      await expect(chip).toBeVisible();
      await expect(chip).toContainText("Not yet viewable");
      await expect(chip).toHaveAttribute("aria-disabled", "true");
    }
  });

  test("the panels render the honest unrecognized state against the pre-SE-08 core", async ({
    page,
  }) => {
    const { studioUrl } = readRuntimeFile();
    // The core seeds studio-seed/seed-001: the document scope exists, and the
    // core's opaque family state is not the panels' shape — nothing is coerced.
    await page.goto(`${studioUrl}/families/studio-seed/seed-001`);
    await expect(page.getByTestId("document-id")).toHaveText("seed-001");
    await expect(page.getByTestId("document-collection")).toHaveText("studio-seed");
    await expect(page.getByTestId("counters-unrecognized")).toBeVisible();
    await expect(page.getByTestId("attachments-unrecognized")).toBeVisible();
    await expect(page.getByTestId("timeseries-unrecognized")).toBeVisible();
  });

  test("a missing document renders the designed missing state, not an error", async ({ page }) => {
    const { studioUrl } = readRuntimeFile();
    await page.goto(`${studioUrl}/families/studio-seed/no-such-document`);
    await expect(page.getByTestId("document-scope-missing")).toBeVisible();
    await expect(page.getByTestId("families-error")).toBeHidden();
  });

  test("unauthenticated access redirects the families routes to the host login flow", async ({
    page,
  }) => {
    const { studioUrl, fixtureUrl } = readRuntimeFile();
    const response = await fetch(`${fixtureUrl}/api/v2/_fixture/control`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ behavior: "session-gate" }),
    });
    expect(response.ok).toBeTruthy();
    try {
      await page.goto(`${studioUrl}/families/studio-seed/seed-001`);
      await expect.poll(() => page.url(), { timeout: 10_000 }).toBe(`${fixtureUrl}/fixture/login`);
    } finally {
      await fetch(`${fixtureUrl}/api/v2/_fixture/control`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ behavior: "session-open", reset: true }),
      });
    }
  });
});

test.describe("family panels on the families fixture", () => {
  let familiesFixtureUrl = "";
  let familiesStudioUrl = "";
  let wrapperChild: ChildProcess | null = null;
  let studioChild: ChildProcess | null = null;

  test.beforeAll(async () => {
    const wrapperPort = await allocatePort();
    wrapperChild = spawn(process.execPath, ["app/families/fixtures/families-demo.mjs"], {
      cwd: STUDIO_ROOT,
      env: {
        ...process.env,
        FAMILIES_FIXTURE_HOST: "127.0.0.1",
        FAMILIES_FIXTURE_PORT: String(wrapperPort),
      },
      stdio: ["ignore", "pipe", "pipe"],
    });
    wrapperChild.stdout?.on("data", (chunk: Buffer) => {
      process.stdout.write(`[families-fixture] ${chunk.toString("utf8")}`);
    });
    wrapperChild.stderr?.on("data", (chunk: Buffer) => {
      process.stderr.write(`[families-fixture:err] ${chunk.toString("utf8")}`);
    });
    familiesFixtureUrl = `http://127.0.0.1:${wrapperPort}`;
    await waitForHttp(`${familiesFixtureUrl}/api/v2/_fixture/families-health`, 15_000);

    const studioPort = await allocatePort();
    studioChild = spawn(process.execPath, ["dist/standalone/server.js"], {
      cwd: STUDIO_ROOT,
      env: {
        ...process.env,
        HOSTNAME: "127.0.0.1",
        PORT: String(studioPort),
        STUDIO_API_BASE_URL: familiesFixtureUrl,
      },
      stdio: ["ignore", "pipe", "pipe"],
    });
    familiesStudioUrl = `http://127.0.0.1:${studioPort}`;
    await waitForHttp(familiesStudioUrl, 30_000);

    // Seed through typed envelopes only: one document for the scope identity,
    // 23 counters (one negative), a 26-entry series plus a second series, and
    // 14 attachments.
    await typedCall(familiesFixtureUrl, "document.write", {
      document: {
        id: "sensors-001",
        collection: "families-demo",
        value: { kind: "sensor", site: "rack-a" },
      },
      operationId: "e2e-families-doc-1",
    });

    const operations = [];
    for (let index = 1; index <= 23; index += 1) {
      operations.push({
        kind: "increment",
        name: `counter-${String(index).padStart(3, "0")}`,
        delta: index === 3 ? -7 : index,
      });
    }
    await typedCall(familiesFixtureUrl, "family.write", {
      kind: "counters",
      scope: "doc/families-demo/sensors-001",
      operation: { operations },
      operationId: "e2e-families-counters-1",
    });

    const appends = [];
    for (let index = 0; index < 26; index += 1) {
      appends.push({
        timestamp: T0 + index * MINUTE_MS,
        values: [36.5 + index * 0.1],
        tag: index % 2 === 0 ? "rack-a" : null,
      });
    }
    await typedCall(familiesFixtureUrl, "family.write", {
      kind: "time-series",
      scope: "doc/families-demo/sensors-001/series/cpu-temp",
      operation: { appends },
      operationId: "e2e-families-ts-1",
    });
    await typedCall(familiesFixtureUrl, "family.write", {
      kind: "time-series",
      scope: "doc/families-demo/sensors-001/series/disk-io",
      operation: {
        appends: Array.from({ length: 12 }, (_, index) => ({
          timestamp: T0 + index * MINUTE_MS,
          values: [index, index * 2],
          tag: null,
        })),
      },
      operationId: "e2e-families-ts-2",
    });

    for (let index = 1; index <= 14; index += 1) {
      await typedCall(familiesFixtureUrl, "family.write", {
        kind: "attachments",
        scope: "doc/families-demo/sensors-001",
        operation: {
          mutation: {
            kind: "store",
            name: `log-${String(index).padStart(3, "0")}`,
            contentType: "text/plain",
            size: index * 256,
            hash: `sha256:${String(index).padStart(64, "0")}`,
            replace: false,
          },
        },
        operationId: `e2e-families-att-${index}`,
      });
    }
  });

  test.afterAll(async () => {
    for (const child of [studioChild, wrapperChild]) {
      child?.kill("SIGTERM");
    }
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 250));
  });

  test("document -> family panel flow through real routes from the index form", async ({
    page,
  }) => {
    await page.goto(`${familiesStudioUrl}/families`);
    await expect(page.getByTestId("family-scope-form")).toBeVisible();

    await page.getByLabel("Collection name").fill("families-demo");
    await page.getByLabel("Document id").fill("sensors-001");
    await page.getByRole("button", { name: "Open family panels" }).click();
    await expect(page).toHaveURL(new RegExp("/families/families-demo/sensors-001$"));

    // The document scope identity, from the typed document.read.
    await expect(page.getByTestId("document-id")).toHaveText("sensors-001");
    await expect(page.getByTestId("document-collection")).toHaveText("families-demo");
    await expect(page.getByTestId("document-sequence")).toHaveText("1");

    // All three viewable panels plus the not-yet-viewable kinds.
    await expect(page.getByTestId("counters-panel")).toBeVisible();
    await expect(page.getByTestId("timeseries-panel")).toBeVisible();
    await expect(page.getByTestId("attachments-panel")).toBeVisible();
    for (const kind of NOT_YET_VIEWABLE_KINDS) {
      await expect(page.getByTestId(`family-kind-${kind}`)).toBeVisible();
    }
  });

  test("counter values render with their update semantics labeled", async ({ page }) => {
    await page.goto(`${familiesStudioUrl}/families/families-demo/sensors-001`);
    await expect(page.getByTestId("counter-entry-counter-001")).toBeVisible();
    await expect(page.getByTestId("counter-entry-counter-002")).toContainText("2");
    // The signed delta semantics: a negative value renders signed.
    await expect(page.getByTestId("counter-entry-counter-003")).toContainText("-7");
    await expect(page.getByTestId("counters-update-semantics")).toContainText(
      "Add a signed delta to a counter, creating it from zero.",
    );
    await expect(page.getByTestId("counters-update-semantics")).toContainText(
      "Delete a counter by name.",
    );
  });

  test("counter entry paging is incremental, not a full reload", async ({ page }) => {
    await page.goto(`${familiesStudioUrl}/families/families-demo/sensors-001`);
    await expect(page.getByTestId("counter-entry-counter-001")).toBeVisible();
    await expect(page.getByTestId("counter-entry-counter-010")).toBeVisible();
    await expect(page.getByTestId("counter-entry-counter-011")).toBeHidden();
    await expect(page.getByTestId("counters-status")).toContainText("Showing 10 of 23");

    await page.evaluate(() => {
      (window as { __db06NavigationMarker?: string }).__db06NavigationMarker = "kept";
    });
    const urlBefore = page.url();

    await page.getByRole("button", { name: "Load more counters" }).click();
    await expect(page.getByTestId("counter-entry-counter-011")).toBeVisible();
    await expect(page.getByTestId("counter-entry-counter-020")).toBeVisible();
    await expect(page.getByTestId("counter-entry-counter-021")).toBeHidden();
    await expect(page.getByTestId("counters-status")).toContainText("Showing 20 of 23");

    // No full reload: the window marker survives and the URL is unchanged.
    expect(
      await page.evaluate(
        () => (window as { __db06NavigationMarker?: string }).__db06NavigationMarker,
      ),
    ).toBe("kept");
    expect(page.url()).toBe(urlBefore);

    await page.getByRole("button", { name: "Load more counters" }).click();
    await expect(page.getByTestId("counter-entry-counter-023")).toBeVisible();
    await expect(page.getByTestId("counters-status")).toContainText(
      "All 23 counter entries are loaded",
    );
    await expect(page.getByRole("button", { name: "Load more counters" })).toBeDisabled();
  });

  test("attachment metadata pages with bounded windows", async ({ page }) => {
    await page.goto(`${familiesStudioUrl}/families/families-demo/sensors-001`);
    await expect(page.getByTestId("attachment-entry-log-001")).toBeVisible();
    await expect(page.getByTestId("attachment-entry-log-010")).toBeVisible();
    await expect(page.getByTestId("attachment-entry-log-011")).toBeHidden();
    await expect(page.getByTestId("attachments-status")).toContainText("Showing 10 of 14");
    await expect(page.getByTestId("attachment-entry-log-001")).toContainText("text/plain");

    await page.getByRole("button", { name: "Load more attachments" }).click();
    await expect(page.getByTestId("attachment-entry-log-014")).toBeVisible();
    await expect(page.getByTestId("attachments-status")).toContainText(
      "All 14 attachment entries are loaded",
    );
    await expect(page.getByRole("button", { name: "Load more attachments" })).toBeDisabled();
  });

  test("time-series range selection is incremental and bounded", async ({ page }) => {
    await page.goto(`${familiesStudioUrl}/families/families-demo/sensors-001`);
    await expect(page.getByTestId("timeseries-panel")).toBeVisible();

    // The catalog's first series loads its first window server-side (entries
    // 0..9; the eleventh entry is outside the first window).
    await expect(page.getByTestId("timeseries-entry-" + (T0 + 10 * MINUTE_MS))).toBeHidden();
    await expect(page.getByTestId("timeseries-status")).toContainText("Showing 10 of 26");
    await expect(page.getByTestId("timeseries-applied-range")).toContainText("No range applied");

    // An inverted range is rejected without leaving the route.
    await page.getByLabel("Range start in epoch milliseconds").fill(String(T0 + MINUTE_MS));
    await page.getByLabel("Range end in epoch milliseconds").fill(String(T0));
    await page.getByRole("button", { name: "Apply range" }).click();
    await expect(page.getByTestId("timeseries-range-invalid")).toContainText(
      "start must not be later than its end",
    );

    // A span beyond the view bound is rejected with the explicit bound.
    await page.getByLabel("Range start in epoch milliseconds").fill(String(T0));
    await page
      .getByLabel("Range end in epoch milliseconds")
      .fill(String(T0 + 8 * 24 * 60 * MINUTE_MS));
    await page.getByRole("button", { name: "Apply range" }).click();
    await expect(page.getByTestId("timeseries-range-invalid")).toContainText(
      "exceeds the view bound",
    );

    // A valid inclusive range replaces the entries through a typed read,
    // without a route reload.
    await page.evaluate(() => {
      (window as { __db06RangeMarker?: string }).__db06RangeMarker = "kept";
    });
    await page.getByLabel("Range start in epoch milliseconds").fill(String(T0 + 5 * MINUTE_MS));
    await page.getByLabel("Range end in epoch milliseconds").fill(String(T0 + 8 * MINUTE_MS));
    await page.getByRole("button", { name: "Apply range" }).click();
    await expect(page.getByTestId("timeseries-status")).toContainText("All 4 series entries");
    await expect(page.getByTestId("timeseries-entry-" + (T0 + 5 * MINUTE_MS))).toBeVisible();
    await expect(page.getByTestId("timeseries-entry-" + (T0 + 8 * MINUTE_MS))).toBeVisible();
    await expect(page.getByTestId("timeseries-entry-" + (T0 + 9 * MINUTE_MS))).toBeHidden();
    await expect(page.getByTestId("timeseries-applied-range")).toContainText(
      `${(T0 + 5 * MINUTE_MS).toLocaleString("en-US")} to ${(T0 + 8 * MINUTE_MS).toLocaleString("en-US")}`,
    );
    expect(
      await page.evaluate(() => (window as { __db06RangeMarker?: string }).__db06RangeMarker),
    ).toBe("kept");

    // Clearing the range restores the whole-series read.
    await page.getByRole("button", { name: "Clear range" }).click();
    await expect(page.getByTestId("timeseries-status")).toContainText("Showing 10 of 26");
    await expect(page.getByTestId("timeseries-applied-range")).toContainText("No range applied");

    // Selecting the second series reads it through the typed operation.
    await page.getByLabel("Select the document's series to read").selectOption("disk-io");
    await expect(page.getByTestId("timeseries-status")).toContainText("Showing 10 of 12");
  });

  test("the browser's family traffic is typed family.read only", async ({ page }) => {
    const requests: string[] = [];
    page.on("request", (request) => {
      if (request.url().startsWith(familiesFixtureUrl)) {
        requests.push(`${request.method()} ${new URL(request.url()).pathname}`);
      }
    });
    await page.goto(`${familiesStudioUrl}/families/families-demo/sensors-001`);
    await expect(page.getByTestId("counters-panel")).toBeVisible();
    await page.getByRole("button", { name: "Load more counters" }).click();
    await page.getByRole("button", { name: "Load more attachments" }).click();
    await page.getByLabel("Range start in epoch milliseconds").fill(String(T0));
    await page.getByLabel("Range end in epoch milliseconds").fill(String(T0 + 10 * MINUTE_MS));
    await page.getByRole("button", { name: "Apply range" }).click();
    await expect(page.getByTestId("timeseries-applied-range")).toContainText("Applied range");
    for (const request of requests) {
      expect(request, `unexpected browser-originated traffic: ${request}`).toBe(
        "POST /api/v2/operations/family.read",
      );
    }
  });

  test("typed family failures render the designed error route and recover", async ({ page }) => {
    await familiesControl(familiesFixtureUrl, { familyBehavior: "fail" });
    try {
      await page.goto(`${familiesStudioUrl}/families/families-demo/sensors-001`);
      await expect(page.getByTestId("families-error")).toBeVisible();
      await expect(page.getByTestId("families-error-code")).toHaveText("family.unavailable");
      await expect(page.getByTestId("families-error-retryable")).toHaveText("true");

      await familiesControl(familiesFixtureUrl, { familyBehavior: "ok" });
      await page.getByRole("link", { name: "Try again" }).click();
      await expect(page.getByTestId("counters-panel")).toBeVisible({ timeout: 15_000 });
      await expect(page.getByTestId("families-error")).toBeHidden();
    } finally {
      await familiesControl(familiesFixtureUrl, { familyBehavior: "ok" });
    }
  });

  test("the loading state is a real route shown while the typed reads are in flight", async ({
    page,
  }) => {
    // The index is static, so the loading route is observable on the panels
    // flow: the form submission navigates while the reads are still
    // streaming, and the panel route's loading boundary is served first.
    await page.goto(`${familiesStudioUrl}/families`);
    await familiesControl(familiesFixtureUrl, { familyDelayMs: 3000 });
    try {
      await page.getByLabel("Collection name").fill("families-demo");
      await page.getByLabel("Document id").fill("sensors-001");
      await page.getByRole("button", { name: "Open family panels" }).click();
      await expect(page).toHaveURL(new RegExp("/families/families-demo/sensors-001$"), {
        timeout: 15_000,
      });
      await expect(page.getByTestId("families-document-loading")).toBeVisible({ timeout: 10_000 });
      await expect(page.getByTestId("counters-panel")).toBeVisible({ timeout: 30_000 });
      await expect(page.getByTestId("families-document-loading")).toBeHidden();
    } finally {
      await familiesControl(familiesFixtureUrl, { familyDelayMs: 0 });
    }
  });
});
