// Documents page (DB-03): collections listing, per-collection paginated
// document lists with incremental pagination, and the document viewer — all
// through the typed client. Loading, empty, and error states are real routes.
//
// Two fixtures are exercised:
// - the shared DB-02 fixture (core): the catalog listing and the honest
//   pre-SE-08 empty state for per-collection listings (the core serves only
//   the catalog), plus the session-gate redirect;
// - the documents fixture (app/documents/fixtures/query-listing-fixture.mjs):
//   the unmodified core composed with the per-collection query.execute
//   semantics SE-08 will land, plus query/read delay and failure controls —
//   used until the fixture core grows them (recorded as a collision action).

import { expect, test } from "@playwright/test";
import { spawn, type ChildProcess } from "node:child_process";
import { createServer as createTcpServer } from "node:net";

import { fixture } from "./fixture-control";
import { STUDIO_ROOT, readRuntimeFile } from "./runtime";

const LARGE_BLOB_CHARS = 50_000;
const LARGE_DOCUMENT_VALUE = { label: "large-document", blob: "x".repeat(LARGE_BLOB_CHARS) };

function formatInteger(value: number): string {
  return value.toLocaleString("en-US");
}

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
      correlationId: `e2e-db03-${op}-${Date.now()}-${Math.random().toString(16).slice(2)}`,
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

test.describe("documents on the shared fixture (core)", () => {
  test("collections listing renders through typed reads and navigates into a collection", async ({
    page,
  }) => {
    const { studioUrl, fixtureUrl } = readRuntimeFile();
    const apiRequests: string[] = [];
    page.on("request", (request) => {
      if (request.url().startsWith(fixtureUrl)) {
        apiRequests.push(`${request.method()} ${new URL(request.url()).pathname}`);
      }
    });

    // Shell -> Documents through the nav.
    await page.goto(studioUrl);
    await page.getByRole("link", { name: "Documents" }).click();
    await expect(page.getByRole("heading", { name: "Documents" })).toBeVisible();

    const row = page.getByTestId("collection-row-studio-seed");
    await expect(row).toBeVisible();
    await expect(row.getByRole("link", { name: "studio-seed" })).toBeVisible();
    await expect(page.getByTestId("collection-doc-count-studio-seed")).toHaveText("2");
    await expect(page.getByTestId("collection-row-parity")).toBeVisible();

    // The catalog read runs server-side in the RSC render: the browser made
    // no API call at all beyond the DB-04 live subscription. The listener
    // spans the whole session (the home page's live status island opens the
    // backbone stream before the navigation), so /v1/streams is the one
    // allowed browser-originated entry; the catalog route itself has no
    // islands.
    expect(
      apiRequests.filter((entry) => entry !== "GET /v1/streams"),
      "no browser-originated API traffic on the collections route beyond the live subscription",
    ).toEqual([]);

    // Collection -> the honest pre-SE-08 empty state (the core serves only the
    // catalog, so the typed per-collection listing matches nothing yet).
    await row.getByRole("link", { name: "studio-seed" }).click();
    await expect(page).toHaveURL(new RegExp("/documents/studio-seed$"));
    await expect(page.getByRole("heading", { name: "studio-seed" })).toBeVisible();
    await expect(page.getByTestId("documents-empty")).toBeVisible();
  });

  test("the document viewer renders a core document with metadata and pretty JSON", async ({
    page,
  }) => {
    const { studioUrl } = readRuntimeFile();
    await page.goto(`${studioUrl}/documents/studio-seed/seed-001`);
    await expect(page.getByTestId("document-id")).toHaveText("seed-001");
    await expect(page.getByTestId("document-collection")).toHaveText("studio-seed");
    await expect(page.getByTestId("document-sequence")).toHaveText("3");
    const json = await page.getByTestId("document-json").textContent();
    expect(json).toContain('"label": "first"');
    await expect(page.getByTestId("document-truncated")).toBeHidden();
  });

  test("unauthenticated access redirects documents routes to the host login flow", async ({
    page,
  }) => {
    const { studioUrl, fixtureUrl } = readRuntimeFile();
    const { control } = fixture(fixtureUrl);
    await control({ behavior: "session-gate" });
    try {
      await page.goto(`${studioUrl}/documents`);
      await expect.poll(() => page.url(), { timeout: 10_000 }).toBe(`${fixtureUrl}/fixture/login`);
    } finally {
      await control({ behavior: "session-open" });
      await control({ reset: true });
    }
  });
});

test.describe("documents browsing on the documents fixture", () => {
  let documentsFixtureUrl = "";
  let documentsStudioUrl = "";
  let wrapperChild: ChildProcess | null = null;
  let studioChild: ChildProcess | null = null;

  test.beforeAll(async () => {
    const wrapperPort = await allocatePort();
    wrapperChild = spawn(process.execPath, ["app/documents/fixtures/query-listing-fixture.mjs"], {
      cwd: STUDIO_ROOT,
      env: {
        ...process.env,
        DOCUMENTS_FIXTURE_HOST: "127.0.0.1",
        DOCUMENTS_FIXTURE_PORT: String(wrapperPort),
      },
      stdio: ["ignore", "pipe", "pipe"],
    });
    wrapperChild.stdout?.on("data", (chunk: Buffer) => {
      process.stdout.write(`[documents-fixture] ${chunk.toString("utf8")}`);
    });
    wrapperChild.stderr?.on("data", (chunk: Buffer) => {
      process.stderr.write(`[documents-fixture:err] ${chunk.toString("utf8")}`);
    });
    documentsFixtureUrl = `http://127.0.0.1:${wrapperPort}`;
    await waitForHttp(`${documentsFixtureUrl}/api/v2/_fixture/documents-health`, 15_000);

    const studioPort = await allocatePort();
    studioChild = spawn(process.execPath, ["dist/standalone/server.js"], {
      cwd: STUDIO_ROOT,
      env: {
        ...process.env,
        HOSTNAME: "127.0.0.1",
        PORT: String(studioPort),
        STUDIO_API_BASE_URL: documentsFixtureUrl,
      },
      stdio: ["ignore", "pipe", "pipe"],
    });
    documentsStudioUrl = `http://127.0.0.1:${studioPort}`;
    await waitForHttp(documentsStudioUrl, 30_000);

    // Seed through typed document.write envelopes only (the same typed
    // operation the contract exposes): 23 documents for paging, 105 for the
    // bounded window, one large document for truncation.
    for (let index = 1; index <= 23; index += 1) {
      await typedCall(documentsFixtureUrl, "document.write", {
        document: {
          id: `browse-${String(index).padStart(3, "0")}`,
          collection: "browse-demo",
          value: { n: index },
        },
        operationId: `e2e-browse-demo-${index}`,
      });
    }
    for (let index = 1; index <= 105; index += 1) {
      await typedCall(documentsFixtureUrl, "document.write", {
        document: {
          id: `window-${String(index).padStart(3, "0")}`,
          collection: "browse-window",
          value: { n: index },
        },
        operationId: `e2e-browse-window-${index}`,
      });
    }
    await typedCall(documentsFixtureUrl, "document.write", {
      document: { id: "large-001", collection: "browse-large", value: LARGE_DOCUMENT_VALUE },
      operationId: "e2e-browse-large-1",
    });
  });

  test.afterAll(async () => {
    for (const child of [studioChild, wrapperChild]) {
      child?.kill("SIGTERM");
    }
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 250));
  });

  async function documentsControl(body: Record<string, unknown>): Promise<void> {
    const response = await fetch(`${documentsFixtureUrl}/api/v2/_fixture/documents-control`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(body),
    });
    expect(response.ok, `documents control ${JSON.stringify(body)} failed`).toBeTruthy();
  }

  test("shell -> collection -> document navigation through real routes", async ({ page }) => {
    await page.goto(documentsStudioUrl);
    await page.getByRole("link", { name: "Documents" }).click();
    await expect(page.getByTestId("collection-row-browse-demo")).toBeVisible();

    await page
      .getByTestId("collection-row-browse-demo")
      .getByRole("link", { name: "browse-demo" })
      .click();
    await expect(page).toHaveURL(new RegExp("/documents/browse-demo$"));
    await expect(page.getByTestId("document-row-browse-001")).toBeVisible();
    await expect(page.getByTestId("document-row-browse-010")).toBeVisible();
    await expect(page.getByTestId("document-row-browse-011")).toBeHidden();
    await expect(page.getByTestId("document-list-status")).toContainText(
      "Showing 10 of 23 matched",
    );

    await page
      .getByTestId("document-row-browse-007")
      .getByRole("link", { name: "browse-007" })
      .click();
    await expect(page).toHaveURL(new RegExp("/documents/browse-demo/browse-007$"));
    await expect(page.getByTestId("document-id")).toHaveText("browse-007");
    await expect(page.getByTestId("document-collection")).toHaveText("browse-demo");
    await expect(page.getByTestId("document-sequence")).toHaveText("1");
    await expect(page.getByTestId("document-json")).toContainText('"n": 7');

    await page.getByRole("link", { name: "Back to browse-demo" }).click();
    await expect(page).toHaveURL(new RegExp("/documents/browse-demo$"));
    await expect(page.getByTestId("document-row-browse-001")).toBeVisible();
  });

  test("pagination is incremental, not a full reload", async ({ page }) => {
    await page.goto(`${documentsStudioUrl}/documents/browse-demo`);
    await expect(page.getByTestId("document-row-browse-001")).toBeVisible();

    const firstRow = page.getByTestId("document-row-browse-001");
    await page.evaluate(() => {
      (window as { __db03NavigationMarker?: string }).__db03NavigationMarker = "kept";
    });
    const urlBefore = page.url();

    await page.getByRole("button", { name: "Load more documents" }).click();
    await expect(page.getByTestId("document-row-browse-011")).toBeVisible();
    await expect(page.getByTestId("document-row-browse-020")).toBeVisible();
    await expect(page.getByTestId("document-row-browse-021")).toBeHidden();
    await expect(page.getByTestId("document-list-status")).toContainText(
      "Showing 20 of 23 matched",
    );

    // No full reload: the window marker survives, the URL is unchanged, and
    // the first row element is still the same attached node.
    expect(
      await page.evaluate(
        () => (window as { __db03NavigationMarker?: string }).__db03NavigationMarker,
      ),
    ).toBe("kept");
    expect(page.url()).toBe(urlBefore);
    await expect(firstRow).toBeVisible();

    // The last short page completes the collection.
    await page.getByRole("button", { name: "Load more documents" }).click();
    await expect(page.getByTestId("document-row-browse-023")).toBeVisible();
    await expect(page.getByTestId("document-list-status")).toContainText(
      "All 23 matched documents are loaded",
    );
    await expect(page.getByRole("button", { name: "Load more documents" })).toBeDisabled();
  });

  test("the id filter narrows loaded pages only, with an explicit scope label", async ({
    page,
  }) => {
    await page.goto(`${documentsStudioUrl}/documents/browse-demo`);
    await expect(page.getByTestId("document-row-browse-001")).toBeVisible();
    await expect(page.getByTestId("document-filter-scope")).toContainText("loaded pages only");

    await page.getByLabel("Filter loaded documents by id").fill("browse-01");
    await expect(page.getByTestId("document-row-browse-010")).toBeVisible();
    await expect(page.getByTestId("document-row-browse-020")).toBeHidden();

    await page.getByLabel("Filter loaded documents by id").fill("no-such-document");
    await expect(page.getByTestId("documents-empty")).toBeVisible();
  });

  test("the bounded view window caps retained pages with an explicit indicator", async ({
    page,
  }) => {
    await page.goto(`${documentsStudioUrl}/documents/browse-window`);
    await expect(page.getByTestId("document-row-window-001")).toBeVisible();

    const loadMore = page.getByRole("button", { name: "Load more documents" });
    for (let click = 0; click < 9; click += 1) {
      await loadMore.click();
    }
    await expect(page.getByTestId("document-row-window-100")).toBeVisible();
    await expect(page.getByTestId("document-row-window-101")).toBeHidden();
    await expect(page.getByTestId("document-list-status")).toContainText(
      "Bounded view window reached",
    );
    await expect(page.getByTestId("document-list-status")).toContainText("10 pages retained");
    await expect(loadMore).toBeDisabled();
  });

  test("large documents truncate with an explicit character-count indicator", async ({ page }) => {
    await page.goto(`${documentsStudioUrl}/documents/browse-large/large-001`);
    await expect(page.getByTestId("document-id")).toHaveText("large-001");
    const totalChars = JSON.stringify(LARGE_DOCUMENT_VALUE, null, 2).length;
    await expect(page.getByTestId("document-truncated")).toHaveText(
      `Truncated: showing ${formatInteger(4000)} of ${formatInteger(totalChars)} characters`,
    );
    const json = await page.getByTestId("document-json").textContent();
    expect(json?.length, "the rendered JSON is the truncated prefix only").toBe(4000);
  });

  test("loading states are real routes shown while typed reads are in flight", async ({ page }) => {
    await documentsControl({ queryDelayMs: 1500 });
    try {
      // Navigate from a rendered route (the same shape as the home route's
      // loading test): each segment's loading route is served while its typed
      // read is delayed.
      await page.goto(`${documentsStudioUrl}/live`);
      await expect(page.getByRole("heading", { name: "Live view" })).toBeVisible();
      await page.getByRole("link", { name: "Documents" }).click();
      await expect(page.getByTestId("collections-loading")).toBeVisible({ timeout: 3_000 });
      await expect(page.getByTestId("collection-row-browse-demo")).toBeVisible({ timeout: 15_000 });
      await expect(page.getByTestId("collections-loading")).toBeHidden();

      await page
        .getByTestId("collection-row-browse-demo")
        .getByRole("link", { name: "browse-demo" })
        .click();
      await expect(page.getByTestId("document-list-loading")).toBeVisible({ timeout: 3_000 });
      await expect(page.getByTestId("document-row-browse-001")).toBeVisible({ timeout: 15_000 });

      await documentsControl({ queryDelayMs: 0, readDelayMs: 1500 });
      await page
        .getByTestId("document-row-browse-001")
        .getByRole("link", { name: "browse-001" })
        .click();
      await expect(page.getByTestId("document-loading")).toBeVisible({ timeout: 3_000 });
      await expect(page.getByTestId("document-id")).toBeVisible({ timeout: 15_000 });
    } finally {
      await documentsControl({ queryDelayMs: 0, readDelayMs: 0 });
    }
  });

  test("typed query failures render a designed error route and recover through Try again", async ({
    page,
  }) => {
    await documentsControl({ queryBehavior: "fail" });
    try {
      await page.goto(`${documentsStudioUrl}/documents/browse-demo`);
      await expect(page.getByTestId("documents-error")).toBeVisible();
      await expect(page.getByTestId("documents-error-code")).toHaveText("query.unavailable");
      await expect(page.getByTestId("documents-error-retryable")).toHaveText("true");

      await documentsControl({ queryBehavior: "ok" });
      await page.getByRole("link", { name: "Try again" }).click();
      await expect(page.getByTestId("document-row-browse-001")).toBeVisible();
      await expect(page.getByTestId("documents-error")).toBeHidden();
    } finally {
      await documentsControl({ queryBehavior: "ok" });
    }
  });

  test("an unreachable API host renders the designed transport error route", async ({ page }) => {
    // Last test in this describe: the documents fixture is terminated so the
    // studio's typed reads fail with transport.unreachable.
    wrapperChild?.kill("SIGTERM");
    const exited = new Promise<void>((resolveExit) => {
      wrapperChild?.once("exit", () => resolveExit());
    });
    await exited;

    const response = await page.goto(`${documentsStudioUrl}/documents`);
    expect(response?.status()).toBe(200);
    await expect(page.getByTestId("documents-error")).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId("documents-error-code")).toHaveText("transport.unreachable");
    await expect(page.getByTestId("documents-error-retryable")).toHaveText("true");
  });
});
