import { expect, test } from "vitest";
import * as sdk from "./index.js";
test("root exposes current native typed operations and pure builders", () => {
  const client = sdk.createTerraBaseClient({
    tenant: "00000000-0000-0000-0000-000000000001",
    database: "00000000-0000-0000-0000-000000000002",
    shard: 1n,
    endpoint: "https://gru.api.terrascale.tech",
    apiKey: "constructor-only",
  });
  for (const name of [
    "read",
    "query",
    "subscription",
    "prepareCommit",
    "commit",
    "transactionStatus",
  ])
    expect(typeof (client as unknown as Record<string, unknown>)[name]).toBe(
      "function",
    );
  for (const name of [
    "http",
    "request",
    "documents",
    "executeSql",
    "sync",
    "sessionToken",
  ])
    expect(name in client).toBe(false);
  for (const name of [
    "createTerraScaleClient",
    "TerraScaleHttp",
    "TerraScaleSyncV1Client",
    "toUlid",
    "createAuthPersistenceClient",
  ])
    expect(name in sdk).toBe(false);
  expect(typeof sdk.collection).toBe("function");
  expect(typeof sdk.raw).toBe("function");
});
