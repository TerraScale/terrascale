// Home page: engine status through typed reads only; loading and error states
// are real, recoverable routes driven by fixture control.

import { expect, test } from "@playwright/test";

import { fixture } from "./fixture-control";
import { readRuntimeFile } from "./runtime";

/** Reads the fixture's engine.status counters.reads (one probe read). */
async function fixtureReadCount(fixtureUrl: string): Promise<number> {
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
  const body = (await response.json()) as { result: { counters: { reads: number } } };
  return body.result.counters.reads;
}

test.describe("studio home", () => {
  test("renders engine status through typed reads only", async ({ page }) => {
    const { studioUrl, fixtureUrl } = readRuntimeFile();
    // The typed read runs server-side in the RSC render, so "typed reads
    // only" is proven at the API host: one engine.status read per page load,
    // and no browser-originated API traffic beyond operation endpoints.
    const readsBefore = await fixtureReadCount(fixtureUrl);
    const apiRequests: string[] = [];
    page.on("request", (request) => {
      if (request.url().startsWith(fixtureUrl)) {
        apiRequests.push(`${request.method()} ${new URL(request.url()).pathname}`);
      }
    });

    const response = await page.goto(studioUrl);
    expect(response?.status(), "home route answers 200").toBe(200);

    await expect(page.getByRole("heading", { name: "Engine status" })).toBeVisible();
    await expect(page.getByTestId("engine-id")).toHaveText("fixture-engine-01");
    await expect(page.getByTestId("engine-version")).toHaveText("0.2.0-fixture");
    await expect(page.getByTestId("engine-role")).toHaveText("single");

    // Exactly one server-side engine.status read for this page load (the
    // delta includes the probe read below), and the browser made no API call
    // at all — the home route has no client-side fetching.
    const readsAfter = await fixtureReadCount(fixtureUrl);
    expect(readsAfter - readsBefore, "one typed engine.status read per render").toBe(2);
    expect(apiRequests, "no browser-originated API traffic on the home route").toEqual([]);
  });

  test("loading state is a real route shown while the typed read is in flight", async ({
    page,
  }) => {
    const { studioUrl, fixtureUrl } = readRuntimeFile();
    const { control } = fixture(fixtureUrl);
    try {
      // The loading route streams during an in-flight read on navigation:
      // start from the live page (rendered), slow the fixture's status read,
      // then navigate Home — the segment's loading route is served while the
      // read is delayed.
      await page.goto(`${studioUrl}/live`);
      await expect(page.getByTestId("tick-connection-state")).toHaveAttribute(
        "data-connection-state",
        "live",
        { timeout: 15_000 },
      );
      await control({ behavior: "status-slow" });
      await page.getByRole("link", { name: "Home" }).click();
      await expect(page.getByTestId("home-loading")).toBeVisible({ timeout: 3_000 });
      await expect(page.getByTestId("engine-id")).toHaveText("fixture-engine-01", {
        timeout: 15_000,
      });
      await expect(page.getByTestId("home-loading")).toBeHidden();
    } finally {
      await control({ reset: true });
    }
  });

  test("error state is a real route and recovers through Try again", async ({ page }) => {
    const { studioUrl, fixtureUrl } = readRuntimeFile();
    const { control } = fixture(fixtureUrl);
    await control({ behavior: "status-fail" });
    try {
      await page.goto(studioUrl);
      await expect(page.getByTestId("home-error")).toBeVisible();
      await expect(page.getByTestId("home-error-code")).toHaveText("engine.unavailable");
      await expect(page.getByTestId("home-error-retryable")).toHaveText("true");

      await control({ behavior: "status-ok" });
      await page.getByRole("link", { name: "Try again" }).click();
      await expect(page.getByTestId("engine-id")).toHaveText("fixture-engine-01");
      await expect(page.getByTestId("home-error")).toBeHidden();
    } finally {
      await control({ reset: true });
    }
  });
});
