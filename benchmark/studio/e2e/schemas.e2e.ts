// Schemas page (SM-04): every collection's Document Schema state — enforced
// version, rule count, validation status, and Schema Migration state — read
// through the typed schema.status operation.
//
// Two fixtures are exercised:
// - the shared DB-02 fixture (core): the honest pre-integration state — the
//   core does not serve schema.status and answers operation.unknown, so every
//   row renders its typed-unavailable cell instead of coercing — plus the
//   session-gate redirect;
// - the schemas fixture (app/schemas/fixtures/schemas-demo.mjs): the
//   unmodified core composed with the typed read the engine-backed host
//   integration will land, seeding one collection per migration state.

import { expect, test } from "@playwright/test";
import { spawn, type ChildProcess } from "node:child_process";
import { createServer as createTcpServer } from "node:net";

import { STUDIO_ROOT, readRuntimeFile } from "./runtime";

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

test.describe("schemas on the shared fixture (core)", () => {
  test("the schemas route is linked and renders the honest pre-integration rows", async ({
    page,
  }) => {
    const { studioUrl } = readRuntimeFile();
    await page.goto(studioUrl);
    await page.getByRole("link", { name: "Schemas" }).click();
    await expect(page).toHaveURL(new RegExp("/schemas$"));
    await expect(page.getByRole("heading", { name: "Schemas" })).toBeVisible();

    // The core seeds real collections; its schema.status answer is the
    // designed operation.unknown, so every row says so — never a guess.
    const rows = page.getByTestId("schema-collection-row");
    await expect(rows.first()).toBeVisible();
    const count = await rows.count();
    expect(count).toBeGreaterThan(0);
    for (let index = 0; index < count; index += 1) {
      await expect(rows.nth(index).getByTestId("schema-collection-unavailable")).toBeVisible();
      await expect(rows.nth(index).getByTestId("schema-collection-unavailable")).toContainText(
        "operation.unknown",
      );
    }
    await expect(page.getByTestId("schema-live-seam")).toBeVisible();
    await expect(page.getByTestId("schema-live-seam")).toHaveAttribute("data-seam-owner", "DB-04");
  });

  test("unauthenticated access redirects the schemas route to the host login flow", async ({
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
      await page.goto(`${studioUrl}/schemas`);
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

test.describe("schema states on the schemas fixture", () => {
  let schemasFixtureUrl = "";
  let schemasStudioUrl = "";
  let wrapperChild: ChildProcess | null = null;
  let studioChild: ChildProcess | null = null;

  test.beforeAll(async () => {
    const wrapperPort = await allocatePort();
    wrapperChild = spawn(process.execPath, ["app/schemas/fixtures/schemas-demo.mjs"], {
      cwd: STUDIO_ROOT,
      env: {
        ...process.env,
        SCHEMAS_FIXTURE_HOST: "127.0.0.1",
        SCHEMAS_FIXTURE_PORT: String(wrapperPort),
      },
      stdio: ["ignore", "pipe", "pipe"],
    });
    wrapperChild.stdout?.on("data", (chunk: Buffer) => {
      process.stdout.write(`[schemas-fixture] ${chunk.toString("utf8")}`);
    });
    wrapperChild.stderr?.on("data", (chunk: Buffer) => {
      process.stderr.write(`[schemas-fixture:err] ${chunk.toString("utf8")}`);
    });
    schemasFixtureUrl = `http://127.0.0.1:${wrapperPort}`;
    await waitForHttp(`${schemasFixtureUrl}/api/v2/_fixture/schemas-health`, 15_000);

    const studioPort = await allocatePort();
    studioChild = spawn(process.execPath, ["dist/standalone/server.js"], {
      cwd: STUDIO_ROOT,
      env: {
        ...process.env,
        HOSTNAME: "127.0.0.1",
        PORT: String(studioPort),
        STUDIO_API_BASE_URL: schemasFixtureUrl,
      },
      stdio: ["ignore", "pipe", "pipe"],
    });
    schemasStudioUrl = `http://127.0.0.1:${studioPort}`;
    await waitForHttp(schemasStudioUrl, 30_000);
  });

  test.afterAll(async () => {
    for (const child of [studioChild, wrapperChild]) {
      child?.kill("SIGTERM");
    }
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 250));
  });

  test("the catalog table renders every migration state of the closed set", async ({ page }) => {
    await page.goto(`${schemasStudioUrl}/schemas`);
    const rows = page.getByTestId("schema-collection-row");
    await expect(rows.first()).toBeVisible();

    const expected: Record<string, { version: string; migration: string }> = {
      users: { version: "2", migration: "complete" },
      events: { version: "1", migration: "in_progress" },
      audit: { version: "3", migration: "rolling_back" },
      "studio-seed": { version: "—", migration: "none" },
      parity: { version: "1", migration: "none" },
    };
    for (const [collection, state] of Object.entries(expected)) {
      const row = rows.filter({ has: page.getByRole("link", { name: collection, exact: true }) });
      await expect(row).toHaveCount(1);
      await expect(row).toHaveAttribute("data-migration-kind", state.migration);
      await expect(row.getByRole("link", { name: collection, exact: true })).toBeVisible();
    }
    // No row is unavailable against the composed fixture.
    await expect(page.getByTestId("schema-collection-unavailable")).toHaveCount(0);
  });

  test("selecting a collection through the form renders its drive facts", async ({ page }) => {
    await page.goto(`${schemasStudioUrl}/schemas`);
    await page.getByLabel("Collection name").fill("users");
    await page.getByRole("button", { name: "Open schema state" }).click();
    await expect(page).toHaveURL(new RegExp("/schemas\\?collection=users$"));

    const panel = page.getByTestId("schema-state-panel");
    await expect(panel).toBeVisible();
    await expect(panel).toHaveAttribute("data-collection", "users");
    await expect(panel).toHaveAttribute("data-migration-kind", "complete");
    await expect(panel).toHaveAttribute("data-validation", "enforced");
    await expect(page.getByTestId("schema-drive-facts")).toContainText("Manifest");
    await expect(page.getByTestId("schema-drive-facts")).toContainText("1 → 2");
    await expect(page.getByTestId("schema-drive-facts")).toContainText("128");
    // The engine's own label for the completed state rides along.
    await expect(panel).toContainText("A migration completed with the recorded receipt.");
    await expect(page.getByTestId("schema-live-seam")).toHaveAttribute("data-seam-landed", "false");
  });

  test("a collection outside the catalog renders the honest hint, not an error", async ({
    page,
  }) => {
    await page.goto(`${schemasStudioUrl}/schemas?collection=never-declared`);
    await expect(page.getByTestId("schema-select-hint")).toBeVisible();
    await expect(page.getByTestId("schema-select-hint")).toContainText("never-declared");
    await expect(page.getByTestId("schema-state-panel")).toHaveCount(0);
    await expect(page.getByTestId("schemas-error")).toHaveCount(0);
  });
});
