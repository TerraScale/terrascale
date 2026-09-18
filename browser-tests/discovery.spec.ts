import { expect, test } from "@playwright/test";

/**
 * Exercises the packed SDK's discovery flow in a real browser (ADR 0050):
 * one metadata discovery request, then database requests connect directly
 * to the ranked regional endpoint. Playwright route interception stands in
 * for the discovery Worker and the regional member, so no external origin
 * is required; the assertions run against the real bundled module in the
 * page, not a unit harness.
 */

const DISCOVERY = "https://api.terrascale.tech/api/v1/discovery*";
const REGIONAL = /^https:\/\/(fra|gru)\.api\.terrascale\.tech\/.*/;

test("discovery routes document requests directly to the ranked regional endpoint", async ({ page }) => {
  const hits: string[] = [];
  await page.route(DISCOVERY, async (route) => {
    hits.push(`discovery:${new URL(route.request().url()).searchParams.get("database")}`);
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        v: 1,
        database: "browser-db",
        branch: "main",
        revision: 5,
        updated: 1_725_300_000,
        residency: null,
        placement: "known",
        refreshSeconds: 300,
        endpoints: [
          { region: "FRA", url: "https://fra.api.terrascale.tech", priority: 1, capabilities: ["read", "write", "sql", "reactive"] },
        ],
      }),
    });
  });
  await page.route(REGIONAL, async (route) => {
    hits.push(`regional:${new URL(route.request().url()).host}`);
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ ok: true }) });
  });

  await page.goto("/browser-tests/fixture.html");
  const first = await page.evaluate(async () => {
    const sdk = await import("/candidate/dist/index.js");
    const client = sdk.createTerraScaleClient({ accessToken: "browser-token" });
    return client.documents.get("browser-db", "users", "one");
  });
  expect(first.ok).toBe(true);
  const second = await page.evaluate(async () => {
    const sdk = await import("/candidate/dist/index.js");
    const client = sdk.createTerraScaleClient({ accessToken: "browser-token" });
    return client.documents.get("browser-db", "users", "one");
  });
  expect(second.ok).toBe(true);

  // Two data requests, exactly one discovery call, both direct to FRA.
  expect(hits.filter((hit) => hit.startsWith("discovery:"))).toEqual(["discovery:browser-db"]);
  expect(hits.filter((hit) => hit.startsWith("regional:"))).toEqual([
    "regional:fra.api.terrascale.tech",
    "regional:fra.api.terrascale.tech",
  ]);
});

test("discovery failure falls back to the global origin", async ({ page }) => {
  const hits: string[] = [];
  await page.route(DISCOVERY, (route) => route.fulfill({ status: 503 }));
  await page.route("https://api.terrascale.tech/api/v1/**", async (route) => {
    hits.push(`global:${new URL(route.request().url()).pathname}`);
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ ok: true }) });
  });

  await page.goto("/browser-tests/fixture.html");
  const result = await page.evaluate(async () => {
    const sdk = await import("/candidate/dist/index.js");
    const client = sdk.createTerraScaleClient({ accessToken: "browser-token" });
    return client.documents.get("browser-fallback", "users", "one");
  });
  expect(result.ok).toBe(true);
  expect(hits).toContain("global:/api/v1/databases/browser-fallback/collections/users/documents/one");
});
