import { expect, test } from "vitest";
import * as sdk from "./index.js";

test("root exposes one project-scoped C# client without retired transports", () => {
  const client = sdk.createTerraBaseClient({
    project: "project", baseUrl: "http://127.0.0.1:18447", token: `tb_${"a".repeat(43)}`,
  });
  expect(client).toBeInstanceOf(sdk.TerraBaseV1Client);
  const db = client.database("main");
  const collection = db.collection("users");
  for (const name of ["get", "put", "create", "patch", "delete", "query"])
    expect(typeof /** @type {Record<string, unknown>} */ (/** @type {unknown} */ (collection))[name]).toBe("function");
  for (const name of ["read", "subscription", "prepareCommit", "transactionStatus", "request", "http"])
    expect(name in client).toBe(false);
  for (const name of ["TerraBaseDiscoveryRouter", "createDatabaseQuery", "createTerraScaleSqliteRuntime", "PostgresClient", "raw"])
    expect(name in sdk).toBe(false);
  expect(JSON.stringify(client)).not.toContain("tb_");
});
