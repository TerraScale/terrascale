// Live View (DB-04): the recent-changes feed over the backbone stream —
// every selector kind renders with V1-parity filters, conflicts render
// under the ConflictRecord restriction (metadata only, never bodies),
// host-shed drops surface with the resume boundary — and the documents
// page updates rows live in the same session as browsing (event-driven;
// no polling).

import { expect, test } from "@playwright/test";
import { spawn, type ChildProcess } from "node:child_process";
import { createServer as createTcpServer } from "node:net";

import { fixture } from "./fixture-control";
import { STUDIO_ROOT, readRuntimeFile } from "./runtime";

function allocatePort(): Promise<number> {
  return new Promise((resolveListen, rejectListen) => {
    const server = createTcpServer();
    server.once("error", rejectListen);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      if (address === null || typeof address === "string") {
        server.close(() => rejectListen(new Error("no port allocated")));
        return;
      }
      const { port } = server.address();
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
  const correlationId = `e2e-db04-${op}-${Date.now()}-${Math.random().toString(16).slice(2)}`;
  const response = await fetch(`${baseUrl}/api/v2/operations/${op}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      v: 2,
      op,
      correlationId,
      causationId: null,
      idempotencyKey: null,
      request,
    }),
  });
  const body = (await response.json()) as { result?: T; error?: { code: string; message: string } };
  if (body.error !== undefined) {
    throw new Error(`typed ${op} failed: ${body.error.code} ${body.error.message}`);
  }
  return body.result as T;
}

test.describe("live recent-changes feed (shared fixture)", () => {
  test("renders every selector kind with filters and conflict restrictions", async ({ page }) => {
    const { studioUrl, fixtureUrl } = readRuntimeFile();
    const { control } = fixture(fixtureUrl);
    await page.goto(`${studioUrl}/live`);

    await expect(page.getByTestId("changes-connection-state")).toHaveAttribute(
      "data-connection-state",
      "live",
      { timeout: 15_000 },
    );

    await control({
      streamEmit: [
        { kind: "document", collection: "orders", document: "d-01" },
        { kind: "index", collection: null, document: null },
        { kind: "counter", collection: "orders", document: "d-01" },
        { kind: "time-series", collection: "orders", document: "d-01" },
        { kind: "operation", collection: null, document: null },
        { kind: "database", collection: null, document: null },
        {
          kind: "conflict",
          collection: null,
          document: null,
          conflict: {
            tenant: "tenant-a",
            operations: ["op-1", "op-2"],
            members: ["member-a", "member-b"],
            clocks: ["clock-1", "clock-2"],
            outcome: "winner:member-a",
            // A hostile extra key that must NEVER surface: the conflict
            // projection is an allowlist.
            body: { secret: "must-not-render" },
          },
        },
      ],
    });

    const list = page.getByTestId("changes-list");
    for (const kind of [
      "document",
      "index",
      "counter",
      "time-series",
      "operation",
      "database",
      "conflict",
    ]) {
      await expect(list.locator(`li[data-kind="${kind}"]`)).toBeVisible({ timeout: 10_000 });
    }

    // The conflict row renders its ConflictRecord metadata — tenant,
    // operations, outcome — and never a body.
    const conflictRow = list.locator('li[data-kind="conflict"]');
    await expect(conflictRow.getByTestId("conflict-metadata")).toContainText("tenant-a");
    await expect(conflictRow.getByTestId("conflict-metadata")).toContainText("winner:member-a");
    await expect(conflictRow).not.toContainText("must-not-render");

    // The kind filters behave like V1's Changes selectors: unchecking
    // documents removes the document row while the conflict row stays.
    await page.getByTestId("changes-filter-document").uncheck();
    await expect(list.locator('li[data-kind="document"]')).toHaveCount(0, { timeout: 10_000 });
    await expect(list.locator('li[data-kind="conflict"]')).toBeVisible();

    // The collection filter scopes to collection identities; conflicts
    // carry none, so a set filter excludes them (the recorded limit).
    // Documents stays unchecked from the step above: the three orders
    // events are the document, counter, and time-series kinds, so the
    // orders-scoped view shows the counter and time-series rows.
    await page.getByLabel("Filter changes by collection prefix").fill("orders");
    await expect(list.locator("li")).toHaveCount(2, { timeout: 10_000 });
    await expect(list.locator('li[data-kind="conflict"]')).toHaveCount(0);
    await expect(list.locator('li[data-kind="counter"]')).toBeVisible();
    await expect(list.locator('li[data-kind="time-series"]')).toBeVisible();

    // Restoring the documents selector brings the document row back: the
    // filters compose like V1's selector refinements.
    await page.getByTestId("changes-filter-document").check();
    await expect(list.locator('li[data-kind="document"]')).toBeVisible({ timeout: 10_000 });

    // Live counters observed the events.
    const events = await page
      .locator('[data-counter="Events"] dd')
      .textContent()
      .then((text) => Number.parseInt((text ?? "0").replace(/,/g, ""), 10));
    expect(events).toBeGreaterThanOrEqual(7);
  });

  test("surfaces host-shed drops in-band with the resume boundary", async ({ page }) => {
    const { studioUrl, fixtureUrl } = readRuntimeFile();
    const { control } = fixture(fixtureUrl);
    await page.goto(`${studioUrl}/live`);

    await expect(page.getByTestId("changes-connection-state")).toHaveAttribute(
      "data-connection-state",
      "live",
      { timeout: 15_000 },
    );

    // The deterministic slow-consumer shed: hold delivery, queue a change,
    // shed it oldest-first with the in-band marker.
    await control({ streamHold: true });
    await control({
      streamEmit: [{ kind: "document", collection: "orders", document: "d-drop" }],
    });
    await control({ streamShed: true });

    const notice = page.getByTestId("changes-drop-notice");
    await expect(notice).toBeVisible({ timeout: 10_000 });
    await expect(notice).toContainText(/dropped/);
    await expect(notice).toContainText(/sequence \d+/);
  });
});

test.describe("documents rows update live in the browsing session", () => {
  let documentsFixtureUrl = "";
  let documentsStudioUrl = "";
  let wrapperChild: ChildProcess | null = null;
  let studioChild: ChildProcess | null = null;

  test.beforeAll(async () => {
    // The DB-03 documents fixture composes the fixture core (which owns the
    // DB-04 changes stream) with per-collection listings — so the live-rows
    // flow runs against real typed writes through real query listings.
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

    await typedCall(documentsFixtureUrl, "document.write", {
      document: { id: "live-001", collection: "live-demo", value: { n: 1 } },
      operationId: "e2e-db04-live-001",
    });
    await typedCall(documentsFixtureUrl, "document.write", {
      document: { id: "live-002", collection: "live-demo", value: { n: 2 } },
      operationId: "e2e-db04-live-002",
    });
  });

  test.afterAll(async () => {
    for (const child of [studioChild, wrapperChild]) {
      child?.kill("SIGTERM");
    }
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 250));
  });

  test("a typed write appears as a live row without any reload", async ({ page }) => {
    await page.goto(`${documentsStudioUrl}/documents/live-demo`);

    const liveStatus = page.getByTestId("document-live-status");
    await expect(liveStatus).toHaveAttribute("data-connection-state", "live", {
      timeout: 15_000,
    });
    await expect(page.getByTestId("document-row-live-001")).toBeVisible();
    await expect(page.getByTestId("document-row-live-002")).toBeVisible();
    await expect(page.getByTestId("document-row-live-003")).toHaveCount(0);

    // The write lands AFTER the browsing session opened: the backbone event
    // drives the refresh — no reload, no polling.
    await typedCall(documentsFixtureUrl, "document.write", {
      document: { id: "live-003", collection: "live-demo", value: { n: 3 } },
      operationId: "e2e-db04-live-003",
    });

    await expect(page.getByTestId("document-row-live-003")).toBeVisible({
      timeout: 15_000,
    });
    // The live refresh counter advanced at least once, proving the update
    // rode the event path.
    await expect(liveStatus).toContainText(/1 refresh this session|refreshes this session/);
    // A reload would lose this assertion's meaning: the page was never
    // re-navigated (the row appeared in the same session).
    expect(page.url()).toContain("/documents/live-demo");
  });
});
