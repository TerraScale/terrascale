import { expect, test } from "@playwright/test";

/**
 * Exercises the packed SDK's discovery flow in a real browser (ADR 0050):
 * one metadata discovery request against atlas.terrascale.tech, then
 * database requests connect directly to the ranked regional endpoint.
 * Playwright route interception stands in for the metadata Worker
 * (ts-atlas), the regional members, and the dashboard-owned product
 * origin, so no external origin is required; the assertions run against
 * the real bundled module in the page, not a unit harness.
 */

const DISCOVERY = "https://atlas.terrascale.tech/api/v1/discovery*";
const CATALOG = "https://atlas.terrascale.tech/api/v1/regions*";
const PRODUCT = "https://api.terrascale.tech/api/v1/**";
const REGIONAL = /^https:\/\/(fra|gru)\.terrascale\.tech\/.*/;

function catalogBody(): string {
  return JSON.stringify({
    regions: [
      { code: "FRA", name: "Frankfurt", country: "DE", latitude: 50.1109, longitude: 8.6821, available: true },
      { code: "GRU", name: "Sao Paulo", country: "BR", latitude: -23.5475, longitude: -46.6361, available: true },
    ],
    hosts: {
      FRA: "https://fra.terrascale.tech",
      GRU: "https://gru.terrascale.tech",
    },
  });
}

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
          { region: "FRA", url: "https://fra.terrascale.tech", priority: 1, capabilities: ["read", "write", "sql", "reactive"] },
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

  // Two data requests, exactly one atlas discovery call, both direct to FRA.
  expect(hits.filter((hit) => hit.startsWith("discovery:"))).toEqual(["discovery:browser-db"]);
  expect(hits.filter((hit) => hit.startsWith("regional:"))).toEqual([
    "regional:fra.terrascale.tech",
    "regional:fra.terrascale.tech",
  ]);
});

test("discovery failure degrades to catalog-selected regional routing (no proxy)", async ({ page }) => {
  const hits: string[] = [];
  await page.route(DISCOVERY, (route) => route.fulfill({ status: 503 }));
  await page.route(CATALOG, async (route) => {
    hits.push("catalog");
    await route.fulfill({ status: 200, contentType: "application/json", body: catalogBody() });
  });
  await page.route(REGIONAL, async (route) => {
    hits.push(`regional:${new URL(route.request().url()).host}`);
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ ok: true }) });
  });

  await page.goto("/browser-tests/fixture.html");
  const result = await page.evaluate(async () => {
    const sdk = await import("/candidate/dist/index.js");
    const client = sdk.createTerraScaleClient({ accessToken: "browser-token" });
    return client.documents.get("browser-fallback", "users", "one");
  });
  expect(result.ok).toBe(true);
  // The retired proxy never appears: the degraded request still goes
  // directly to the catalog-selected regional member.
  expect(hits).toEqual(["catalog", "regional:fra.terrascale.tech"]);
});

test("product routes target the product origin, distinct from atlas and the members", async ({ page }) => {
  const hits: string[] = [];
  await page.route(PRODUCT, async (route) => {
    hits.push(`product:${new URL(route.request().url()).pathname}`);
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify([]) });
  });

  await page.goto("/browser-tests/fixture.html");
  const result = await page.evaluate(async () => {
    const sdk = await import("/candidate/dist/index.js");
    const client = sdk.createTerraScaleClient({ accessToken: "browser-token" });
    return client.organizations.list();
  });
  expect(result.ok).toBe(true);
  expect(hits).toEqual(["product:/api/v1/organizations"]);
});
