import { expect, test } from "@playwright/test";

test("packed browser exposes the C# client and exact decimal values", async ({ page }) => {
  await page.goto("/browser-tests/fixture.html");
  const observation = await page.evaluate(async () => {
    const candidate = "/candidate/src/index.js";
    const sdk = /** @type {typeof import("../src/index.js")} */ (await import(candidate));
    const client = sdk.createTerraBaseClient({
      project: "01ARZ3NDEKTSV4RRFFQ69G5FA1", baseUrl: "https://database.example.test", token: `tb_${"a".repeat(43)}`,
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

test("packed browser generates monotonic ULIDs and rejects noncanonical ids", async ({ page }) => {
  await page.goto("/browser-tests/fixture.html");
  const valid = await page.evaluate(async () => {
    const candidate = "/candidate/src/index.js";
    const sdk = /** @type {typeof import("../src/index.js")} */ (await import(candidate));
    const first = sdk.generateId();
    const second = sdk.generateId();
    return sdk.isValidId(first) && sdk.isValidId(second) && first < second &&
      !sdk.isValidId(first.toLowerCase()) && !sdk.isValidId("org_" + first) &&
      !sdk.isValidId("8" + first.slice(1)) && !("documentIdFor" in sdk);
  });
  expect(valid).toBe(true);
});
