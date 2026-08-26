// Queries and indexes pages (DB-05): run-query -> results -> index view, the
// streaming execution indicator, the confirmed mutation, and the typed error
// paths — all through typed operations against the fixture host.
//
// The engine-closed and long-running paths need an API host the shared fixture
// cannot simulate (it has no query-failure or slow-query control), so those two
// tests boot their own studio instance against a dead port and against a
// server that accepts connections and never answers.

import { expect, test } from "@playwright/test";
import { spawn } from "node:child_process";
import { createServer as createHttpServer, type Server as HttpServer } from "node:http";
import { createServer as createTcpServer } from "node:net";
import type { AddressInfo } from "node:net";

import { fixture } from "./fixture-control";
import { startIndexCatalogHost } from "../tests/fixtures/queries-fixtures";
import { readRuntimeFile, STUDIO_ROOT } from "./runtime";

test.describe("studio queries and indexes", () => {
  test("run-query renders paginated results and query metadata, then the index view", async ({
    page,
  }) => {
    const { studioUrl, fixtureUrl } = readRuntimeFile();
    const queriesBefore = await fixtureQueryCount(fixtureUrl);

    await page.goto(`${studioUrl}/queries`);
    await expect(page.getByRole("heading", { name: "Queries" })).toBeVisible();

    // An empty query never leaves the studio: the console's own validation
    // answers before any typed operation is dispatched.
    await page.getByTestId("query-console-text").fill("   ");
    await page.getByTestId("query-console-run").click();
    await expect(page.getByTestId("query-error")).toBeVisible();
    await expect(page.getByTestId("query-error-code")).toHaveText("studio.query-empty");
    expect(await fixtureQueryCount(fixtureUrl)).toBe(queriesBefore);

    // The console's default query text matches the fixture's collection
    // summary read, so one Run dispatches exactly one typed query.execute.
    await page.getByTestId("query-console-text").fill("from collection");
    await page.getByTestId("query-console-run").click();

    await expect(page.getByTestId("query-results")).toBeVisible();
    await expect(page.getByTestId("query-results-rows")).toBeVisible();
    const rowIds = await page.getByTestId("query-row-id").allTextContents();
    // Host order is preserved as delivered (the fixture sorts summaries by
    // collection name).
    expect(rowIds).toEqual(["catalog:parity", "catalog:studio-seed"]);

    // Paging and metadata.
    await expect(page.getByTestId("query-results-window")).toHaveText("rows 1–2 of 2");
    await expect(page.getByTestId("query-pager-position")).toHaveText("page 1 of 1");
    await expect(page.getByTestId("query-metadata-matched")).toHaveText("2");
    await expect(page.getByTestId("query-metadata-returned")).toHaveText("2");
    await expect(page.getByTestId("query-staleness-empty")).toBeVisible();
    await expect(page.getByTestId("query-plan-note")).toBeVisible();
    await expect(page.getByTestId("query-metadata")).toBeVisible();

    // Exactly one typed query operation left the studio for this run.
    const queriesAfter = await fixtureQueryCount(fixtureUrl);
    expect(queriesAfter - queriesBefore, "one typed query.execute per run").toBe(1);

    // The index view: statuses distinct by construction, empty catalog stated
    // explicitly rather than invented.
    await page.getByRole("link", { name: "Indexes" }).click();
    await expect(page.getByRole("heading", { name: "Indexes" })).toBeVisible();
    await expect(page.getByTestId("index-catalog-query")).toHaveText("from __index_catalog__");
    await expect(page.getByTestId("index-catalog-empty")).toBeVisible();
    await expect(page.getByTestId("index-catalog-staleness-empty")).toBeVisible();
    for (const status of ["fresh", "stale", "rebuilding", "unknown"]) {
      await expect(
        page.locator(`[data-testid="index-catalog-counts"] [data-index-status-count="${status}"]`),
      ).toHaveText("0");
    }
    await expect(page.getByTestId("index-catalog-contract-note")).toBeVisible();
  });

  test("the index catalog labels stale and rebuilding distinctly with the host's evidence", async ({
    page,
  }) => {
    // The shared fixture host has no index catalog data, so this test runs
    // against the page-owned stand-in host (tests/fixtures/queries-fixtures.ts),
    // which answers the catalog query through the contract envelope.
    const host = await startIndexCatalogHost();
    const studio = await startStudioInstance(host.url);
    try {
      await page.goto(`${studio.url}/queries/indexes`);
      await expect(page.getByTestId("index-catalog")).toBeVisible();

      // Distinct labels, one per status, never shared.
      await expect(
        page.locator('[data-testid="index-entry-status"][data-index-status="fresh"]'),
      ).toHaveText("Fresh");
      await expect(
        page.locator('[data-testid="index-entry-status"][data-index-status="stale"]'),
      ).toHaveText("Stale");
      await expect(
        page.locator('[data-testid="index-entry-status"][data-index-status="rebuilding"]'),
      ).toHaveText("Rebuilding");
      // An unrecognized status stays unknown and carries the host's own value.
      await expect(
        page.locator('[data-testid="index-entry-status"][data-index-status="unknown"]'),
      ).toHaveText("Unknown (warming-up)");

      // Evidence-backed labels: the host's reason renders verbatim.
      await expect(
        page.locator(
          '[data-index-namespace="Auto/studio-seed|by-label"] [data-testid="index-entry-reason"]',
        ),
      ).toHaveText("observed generation 41 is newer than the used generation 39");
      await expect(
        page.locator(
          '[data-index-namespace="Auto/studio-seed|by-kind"] [data-testid="index-entry-reason"]',
        ),
      ).toHaveText("rebuilding: epoch 3, generation 12 of 18 published");

      // Counts agree with the rendered entries, and the malformed row is
      // counted rather than silently dropped.
      for (const [status, count] of [
        ["fresh", "1"],
        ["stale", "1"],
        ["rebuilding", "1"],
        ["unknown", "1"],
      ] as const) {
        await expect(
          page.locator(
            `[data-testid="index-catalog-counts"] [data-index-status-count="${status}"]`,
          ),
        ).toHaveText(count);
      }
      await expect(page.getByTestId("index-catalog-unparsed")).toContainText("1 catalog row");
      await expect(page.getByTestId("index-catalog-staleness")).toBeVisible();
    } finally {
      await studio.stop();
      await host.close();
    }
  });

  test("a long-running query streams its indicator without blocking the UI", async ({ page }) => {
    const blackhole = await startBlackHoleServer();
    const studio = await startStudioInstance(blackhole.url);
    try {
      await page.goto(`${studio.url}/queries`);
      await page.getByTestId("query-console-run").click();

      // While the typed operation is in flight the console stays interactive:
      // the streaming indicator is visible and the console itself still
      // accepts input (no results exist yet on a first run, so the proof uses
      // the always-present console textarea).
      await expect(page.getByTestId("query-running")).toBeVisible({ timeout: 5_000 });
      await page.getByTestId("query-console-text").fill("from collection where label = 'second'");
      await expect(page.getByTestId("query-console-text")).toHaveValue(
        "from collection where label = 'second'",
      );
      await expect(page.getByTestId("query-running")).toBeVisible();

      // The typed client fails closed on its own timeout, and the console
      // renders that typed identity verbatim.
      await expect(page.getByTestId("query-error")).toBeVisible({ timeout: 15_000 });
      await expect(page.getByTestId("query-error-code")).toHaveText("transport.timeout");
      await expect(page.getByTestId("query-error-retryable")).toHaveText("true");
      await expect(page.getByTestId("query-running")).toBeHidden();
    } finally {
      await studio.stop();
      await closeServer(blackhole.server);
    }
  });

  test("an unreachable engine renders the typed transport error verbatim", async ({ page }) => {
    const studio = await startStudioInstance("http://127.0.0.1:1");
    try {
      await page.goto(`${studio.url}/queries`);
      await expect(page.getByRole("heading", { name: "Queries" })).toBeVisible();

      await page.getByTestId("query-console-run").click();
      await expect(page.getByTestId("query-error")).toBeVisible({ timeout: 15_000 });
      await expect(page.getByTestId("query-error-code")).toHaveText("transport.unreachable");
      await expect(page.getByTestId("query-error-retryable")).toHaveText("true");
      await expect(page.getByTestId("query-error-message")).toContainText("query.execute");
    } finally {
      await studio.stop();
    }
  });

  test("the host session gate redirects the query action to the host login flow", async ({
    page,
  }) => {
    const { studioUrl, fixtureUrl } = readRuntimeFile();
    const { control } = fixture(fixtureUrl);
    await control({ behavior: "session-gate" });
    try {
      await page.goto(`${studioUrl}/queries`);
      await page.getByTestId("query-console-run").click();
      await expect.poll(() => page.url(), { timeout: 10_000 }).toBe(`${fixtureUrl}/fixture/login`);
    } finally {
      await control({ behavior: "session-open" });
    }
  });

  test("saving a query is a confirmed, idempotent typed write", async ({ page }) => {
    const { studioUrl, fixtureUrl } = readRuntimeFile();
    await page.goto(`${studioUrl}/queries`);
    await page.getByTestId("query-console-run").click();
    await expect(page.getByTestId("query-results")).toBeVisible();

    // Asking to save only reveals the confirmation step: no write yet.
    const writesBeforeRequest = await fixtureWriteCount(fixtureUrl);
    await page.getByTestId("save-query-request").click();
    await expect(page.getByTestId("save-query-confirm")).toBeVisible();
    expect(await fixtureWriteCount(fixtureUrl)).toBe(writesBeforeRequest);

    // Cancelling still writes nothing.
    await page.getByTestId("save-query-cancel").click();
    await expect(page.getByTestId("save-query-confirm")).toBeHidden();
    expect(await fixtureWriteCount(fixtureUrl)).toBe(writesBeforeRequest);

    // Confirming performs exactly one typed document.write.
    await page.getByTestId("save-query-request").click();
    await page.getByTestId("save-query-confirm-submit").click();
    await expect(page.getByTestId("save-query-receipt")).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId("save-query-receipt-collection")).toHaveText(
      "studio-saved-queries",
    );
    const operationId = await page.getByTestId("save-query-receipt-operation-id").textContent();
    const version = await page.getByTestId("save-query-receipt-version").textContent();
    expect(await fixtureWriteCount(fixtureUrl)).toBe(writesBeforeRequest + 1);

    // Saving the same query again replays the same receipt (idempotent typed
    // operation), rather than writing a second version.
    await page.getByTestId("save-query-confirm-submit").click();
    await expect(page.getByTestId("save-query-receipt")).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId("save-query-receipt-operation-id")).toHaveText(operationId ?? "");
    await expect(page.getByTestId("save-query-receipt-version")).toHaveText(version ?? "");
  });
});

/** Reads the fixture's engine.status counters.queries (one probe read). */
async function fixtureQueryCount(fixtureUrl: string): Promise<number> {
  const body = await fixtureStatus(fixtureUrl);
  return body.counters.queries;
}

/** Reads the fixture's engine.status counters.writes (one probe read). */
async function fixtureWriteCount(fixtureUrl: string): Promise<number> {
  const body = await fixtureStatus(fixtureUrl);
  return body.counters.writes;
}

async function fixtureStatus(
  fixtureUrl: string,
): Promise<{ counters: { queries: number; writes: number } }> {
  const response = await fetch(`${fixtureUrl}/api/v2/operations/engine.status`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      v: 2,
      op: "engine.status",
      correlationId: `e2e-probe-${Date.now()}`,
      causationId: null,
      idempotencyKey: null,
      request: {},
    }),
  });
  expect(response.ok).toBeTruthy();
  const body = (await response.json()) as {
    result: { counters: { queries: number; writes: number } };
  };
  return body.result;
}

/** A local server that accepts connections and never answers: an API host whose requests hang. */
async function startBlackHoleServer(): Promise<{ server: HttpServer; url: string }> {
  const server = createHttpServer(() => {
    // Intentionally never responds.
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address() as AddressInfo;
  return { server, url: `http://127.0.0.1:${port}` };
}

/** Boots one studio instance against an arbitrary API base URL, with cleanup. */
async function startStudioInstance(
  apiBaseUrl: string,
): Promise<{ url: string; stop: () => Promise<void> }> {
  const port = await freePort();
  const child = spawn(process.execPath, ["dist/standalone/server.js"], {
    cwd: STUDIO_ROOT,
    env: {
      ...process.env,
      HOSTNAME: "127.0.0.1",
      PORT: String(port),
      STUDIO_API_BASE_URL: apiBaseUrl,
    },
    stdio: ["ignore", "ignore", "ignore"],
  });
  const url = `http://127.0.0.1:${port}`;
  const deadline = Date.now() + 30_000;
  let lastError = "no attempt";
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url, { redirect: "manual" });
      if (response.status === 200 || response.status === 500) {
        return {
          url,
          stop: async () => {
            child.kill("SIGTERM");
            await new Promise<void>((resolve) => child.once("exit", () => resolve()));
          },
        };
      }
      lastError = `HTTP ${response.status}`;
    } catch (error) {
      lastError = error instanceof Error ? error.message : String(error);
    }
    await new Promise((resolve) => setTimeout(resolve, 150));
  }
  child.kill("SIGKILL");
  throw new Error(`waiting for studio on ${url} timed out: ${lastError}`);
}

async function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const server = createTcpServer();
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      if (address === null || typeof address === "string") {
        server.close(() => reject(new Error("no port allocated")));
        return;
      }
      const { port } = address;
      server.close(() => resolve(port));
    });
  });
}

async function closeServer(server: HttpServer): Promise<void> {
  await new Promise<void>((resolve) => server.close(() => resolve()));
}
