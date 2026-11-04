import { expect, test } from "vitest";
import * as sdk from "./index.js";

test("root exposes one project-scoped C# client without retired transports", () => {
  const token = "eyJ0eXAiOiJhdCtqd3QifQ.eyJwcm9qZWN0X2lkIjoicHJvamVjdCJ9.c2lnbmF0dXJl";
  const client = sdk.createTerraBaseClient({
    project: "01ARZ3NDEKTSV4RRFFQ69G5FA1", baseUrl: "http://127.0.0.1:18447", token,
  });
  expect(client).toBeInstanceOf(sdk.TerraBaseV1Client);
  expect(typeof client.database("main").execute).toBe("function");
  for (const name of ["read", "subscription", "prepareCommit", "transactionStatus", "request", "http"])
    expect(name in client).toBe(false);
  for (const name of ["TerraBaseDiscoveryRouter", "createDatabaseQuery", "createTerraScaleSqliteRuntime", "PostgresClient", "raw"])
    expect(name in sdk).toBe(false);
  expect(JSON.stringify(client)).not.toContain(token);
});
