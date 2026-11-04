import { expect, test } from "@playwright/test";

test("packed browser exposes the SQL client and exact decimal values", async ({ page }) => {
  await page.goto("/browser-tests/fixture.html");
  const observation = await page.evaluate(async () => {
    const candidate = "/candidate/src/index.js";
    const sdk = /** @type {typeof import("../src/index.js")} */ (await import(candidate));
    const client = sdk.createTerraBaseClient({
      project: "01ARZ3NDEKTSV4RRFFQ69G5FA1", baseUrl: "https://database.example.test", token: `tb_${"a".repeat(43)}`,
    });
    const db = client.database("main");
    return {
      methods: ["execute", "query", "batch", "connect", "shape"].every(name =>
        typeof /** @type {Record<string, unknown>} */ (/** @type {unknown} */ (db))[name] === "function"),
      exact: new sdk.ExactDecimal("9007199254740993").toString(),
      credential: JSON.stringify(client).includes("tb_"),
      retired: ["TerraBaseDiscoveryRouter", "createDatabaseQuery", "createTerraScaleSqliteRuntime", "PostgresClient"].filter(name => name in sdk),
    };
  });
  expect(observation).toEqual({ methods: true, exact: "9007199254740993", credential: false, retired: [] });
});
