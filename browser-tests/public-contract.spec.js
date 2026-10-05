import { expect, test } from "@playwright/test";

test("packed browser exposes the C# client and exact decimal values", async ({ page }) => {
  await page.goto("/browser-tests/fixture.html");
  const observation = await page.evaluate(async () => {
    const candidate = "/candidate/src/index.js";
    const sdk = /** @type {typeof import("../src/index.js")} */ (await import(candidate));
    const client = sdk.createTerraBaseClient({
      project: "project", baseUrl: "https://database.example.test", token: `tb_${"a".repeat(43)}`,
    });
    const collection = client.database("main").collection("users");
    return {
      methods: ["get", "put", "create", "patch", "delete", "query"].every(name =>
        typeof /** @type {Record<string, unknown>} */ (/** @type {unknown} */ (collection))[name] === "function"),
      exact: new sdk.ExactDecimal("9007199254740993").toString(),
      credential: JSON.stringify(client).includes("tb_"),
      retired: ["TerraBaseDiscoveryRouter", "createDatabaseQuery", "createTerraScaleSqliteRuntime", "PostgresClient"].filter(name => name in sdk),
    };
  });
  expect(observation).toEqual({ methods: true, exact: "9007199254740993", credential: false, retired: [] });
});

test("packed shape decoder preserves BOM-prefixed continuation ids", async ({ page }) => {
  await page.goto("/browser-tests/fixture.html");
  const preserved = await page.evaluate(async () => {
    const candidate = "/candidate/src/v1/shapes.js";
    const shapes = /** @type {typeof import("../src/v1/shapes.js")} */ (await import(candidate));
    const encoded = btoa(String.fromCharCode(...new TextEncoder().encode("\uFEFFlegacy")))
      .replace(/\+/gu, "-").replace(/\//gu, "_").replace(/=+$/u, "");
    const offset = `snap.42.${encoded}`;
    return shapes.requireShapeOffset(offset) === offset;
  });
  expect(preserved).toBe(true);
});
