// Topology view (DB-07): the membership picture renders from typed topology
// reads — Regions, Members with Failure Domains, Bucket Range ownership with
// ownership epochs, the Acknowledgement Level per recent write, and
// Convergence with replication lag — with member status transitions on the
// polled fallback (pre-DB-04) and the lane/qualification limits visible. The
// synthetic-lane demo advances its lane clock five ticks per topology read,
// so a deterministic transition is staged by driving the lane to a known
// tick before loading the route.

import { expect, test } from "@playwright/test";

import { fixture } from "./fixture-control";
import { readRuntimeFile } from "./runtime";

const STATUSES = [
  "assigned",
  "active",
  "responsive",
  "stale",
  "unavailable",
  "degraded",
  "unknown",
] as const;

/** One typed topology read against the fixture (also advances the lane). */
async function topologyRead(
  fixtureUrl: string,
  op: "topology.read" | "topology.recent-writes" | "topology.convergence",
): Promise<{ laneTick: number }> {
  const response = await fetch(`${fixtureUrl}/api/v2/operations/${op}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      v: 2,
      op,
      correlationId: `topology-e2e-${Date.now()}`,
      causationId: null,
      idempotencyKey: null,
      request: {},
    }),
  });
  expect(response.ok).toBeTruthy();
  const body = (await response.json()) as { result: { laneTick: number } };
  return body.result;
}

/** Drives the synthetic lane to an exact tick (reads advance it by 5). */
async function driveLaneTo(fixtureUrl: string, tick: number): Promise<void> {
  for (let guard = 0; guard < 200; guard += 1) {
    const { laneTick } = await topologyRead(fixtureUrl, "topology.read");
    if (laneTick >= tick) {
      expect(laneTick).toBe(tick);
      return;
    }
  }
  throw new Error(`lane never reached tick ${tick}`);
}

test.describe("studio topology", () => {
  test("renders the synthetic-lane topology through typed reads", async ({ page }) => {
    const { studioUrl } = readRuntimeFile();
    await page.goto(studioUrl);
    await page.getByRole("link", { name: "Topology" }).click();
    await expect(page).toHaveURL(new RegExp("/topology$"));
    await expect(page.getByRole("heading", { name: "Topology" })).toBeVisible();

    // Regions with their Failure Domains.
    await expect(
      page.getByTestId("topology-regions").getByText("r-alpha", { exact: true }),
    ).toBeVisible();
    await expect(
      page.getByTestId("topology-regions").getByText("r-beta", { exact: true }),
    ).toBeVisible();
    await expect(
      page.getByTestId("topology-regions").getByText("r-gamma", { exact: true }),
    ).toBeVisible();
    await expect(page.getByTestId("topology-regions")).toContainText("Failure Domain fd-r-alpha");

    // Six Members, each with a status from the closed set.
    const memberRows = page.locator('[data-testid="topology-members"] tbody tr');
    await expect(memberRows).toHaveCount(6);
    for (const status of await page
      .locator('[data-testid="topology-members"] [data-status]')
      .all()) {
      const value = await status.getAttribute("data-status");
      expect(STATUSES).toContain(value);
    }

    // Bucket Range ownership with ownership epochs.
    await expect(page.getByTestId("topology-ownership")).toContainText("Bucket Range");
    await expect(page.getByTestId("topology-ownership")).toContainText("shard/1");
    await expect(page.getByTestId("topology-ownership")).toContainText("shard/2");
    await expect(page.getByTestId("topology-ownership")).toContainText("shard/3");
    await expect(page.getByTestId("topology-ownership")).toContainText("ownership epoch");

    // Acknowledgement Level panel with engine level names.
    await expect(page.getByRole("heading", { name: "Acknowledgement Level" })).toBeVisible();

    // Convergence panel with the engine definitions verbatim.
    await expect(page.getByRole("heading", { name: "Convergence" })).toBeVisible();
    await expect(page.getByTestId("topology-definitions")).toContainText(
      "in lane ticks of the lane clock",
    );
    await expect(page.getByTestId("topology-lag")).toContainText("storage-sequence distance");

    // Lane and qualification limits: no production claims.
    await expect(page.getByText("no production claim").first()).toBeVisible();
    await expect(page.getByTestId("topology-poll-note")).toContainText("Polled fallback");

    // No private control-plane vocabulary anywhere on the route.
    const bodyText = await page.locator("body").innerText();
    expect(bodyText, "control-plane data never appears").not.toMatch(
      /billing|provisioning|placement|routing/i,
    );
  });

  test("member status transitions render through the polled fallback", async ({ page }) => {
    const { studioUrl, fixtureUrl } = readRuntimeFile();
    // Stage the lane at tick 45: the page's three typed reads land on 60, and
    // m-b2's degraded window (lane ticks 70..83) arrives within two polls.
    await driveLaneTo(fixtureUrl, 45);
    await page.goto(`${studioUrl}/topology`);
    await expect(page.getByTestId("topology-members")).toBeVisible();

    const mB2 = page.getByTestId("topology-status-m-b2");
    await expect(mB2).toHaveAttribute("data-status", "responsive");
    await expect(page.getByTestId("topology-poll-count")).not.toHaveText("0", { timeout: 15_000 });
    await expect(mB2, "the scheduled transition renders live on the poll").toHaveAttribute(
      "data-status",
      "degraded",
      { timeout: 15_000 },
    );
    await expect(page.getByTestId("topology-lane-tick")).not.toHaveText("60");
  });

  test("the browser talks only to the topology read operation", async ({ page }) => {
    const { studioUrl, fixtureUrl } = readRuntimeFile();
    const requests: string[] = [];
    page.on("request", (request) => {
      if (request.url().startsWith(fixtureUrl)) {
        requests.push(`${request.method()} ${new URL(request.url()).pathname}`);
      }
    });
    await page.goto(`${studioUrl}/topology`);
    await expect(page.getByTestId("topology-poll-count")).not.toHaveText("0", {
      timeout: 15_000,
    });
    for (const request of requests) {
      expect(request, `unexpected browser-originated traffic: ${request}`).toBe(
        "POST /api/v2/operations/topology.read",
      );
    }
  });

  test("typed failures render the error route and recover", async ({ page }) => {
    const { studioUrl, fixtureUrl } = readRuntimeFile();
    const { control } = fixture(fixtureUrl);
    await control({ behavior: "status-fail" });
    try {
      await page.goto(`${studioUrl}/topology`);
      await expect(page.getByTestId("topology-error")).toBeVisible();
      await expect(page.getByTestId("topology-error-code")).toHaveText("engine.unavailable");
      await expect(page.getByTestId("topology-error-retryable")).toHaveText("true");

      await control({ reset: true });
      await page.getByRole("link", { name: "Try again" }).click();
      await expect(page.getByTestId("topology-members")).toBeVisible({ timeout: 15_000 });
      await expect(page.getByTestId("topology-error")).toBeHidden();
    } finally {
      await control({ reset: true });
    }
  });

  test("loading state is a real route shown while the reads are in flight", async ({ page }) => {
    const { studioUrl, fixtureUrl } = readRuntimeFile();
    const { control } = fixture(fixtureUrl);
    try {
      await page.goto(studioUrl);
      await expect(page.getByTestId("engine-id")).toHaveText("fixture-engine-01");
      await control({ behavior: "status-slow" });
      await page.getByRole("link", { name: "Topology" }).click();
      await expect(page.getByTestId("topology-loading")).toBeVisible({ timeout: 5_000 });
      await expect(page.getByTestId("topology-members")).toBeVisible({ timeout: 20_000 });
      await expect(page.getByTestId("topology-loading")).toBeHidden();
    } finally {
      await control({ reset: true });
    }
  });
});
