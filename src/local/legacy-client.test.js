import { expect, test } from "vitest";
import { createTerraBaseClient } from "./legacy-client.js";

test("retired SQLite transport refuses remote server origins", () => {
  const scope = { tenant: "00000000-0000-0000-0000-000000000001", database: "00000000-0000-0000-0000-000000000002", shard: 0n, apiKey: "local-only" };
  for (const endpoint of ["https://database.example.test", "http://database.example.test", "http://localhost@remote.example.test", "http://127.0.0.1/path"])
    expect(() => createTerraBaseClient({ ...scope, endpoint })).toThrow(/local emulator/u);
  expect(createTerraBaseClient({ ...scope, endpoint: "http://127.0.0.1:18447" }).scope).toEqual({ tenant: scope.tenant, database: scope.database, shard: 0n });
});

test("local cancellation takes precedence over the abort reason's error type", async () => {
  const client = createTerraBaseClient({ tenant: "00000000-0000-0000-0000-000000000001", database: "00000000-0000-0000-0000-000000000002", shard: 0n, apiKey: "local-only" });
  const abort = new AbortController();
  abort.abort(new TypeError("cancel before dispatch"));
  expect(await client.read("00000000-0000-0000-0000-000000000003", "00000000-0000-0000-0000-000000000004", { signal: abort.signal }))
    .toEqual({ ok: false, error: { code: "cancelled" } });
});
