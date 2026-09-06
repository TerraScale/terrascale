import { strict as assert } from "node:assert";
import { describe, test } from "vitest";
import { createTerraScaleAuthAdapter } from "./better-auth-factory.js";
import { AuthPersistenceClient } from "./auth-persistence.js";

describe("Better Auth 1.7.2 TerraScale adapter factory", () => {
  test("uses the public page route for transformed reads and keeps joins explicit", async () => {
    const requests: Request[] = [];
    const client = new AuthPersistenceClient({
      apiBaseUrl: "https://api.example.test",
      fetch: async (input, init) => {
        const request = new Request(input, init);
        requests.push(request);
        return Response.json({ record: null, matched: true, records: [{ id: "u1", email: "u@example.test" }] });
      },
    });
    const adapter = createTerraScaleAuthAdapter(client, "auth")({
      appName: "test",
      secret: "test-secret-test-secret-test-secret",
      advanced: { database: { joins: false } },
    });

    const rows = await adapter.findMany<{ id: string; email: string }>({
      model: "user",
      where: [{ field: "email", value: "u@example.test" }],
      limit: 10,
      select: ["id", "email"],
      join: { account: { limit: 2 } },
    });

    assert.equal(rows[0]?.id, "u1");
    assert.equal(rows[0]?.email, "u@example.test");
    const pageRequest = requests.find((request) => new URL(request.url).pathname.endsWith("/findPage"));
    assert.ok(pageRequest);
    const body = await pageRequest.json() as { limit: number; predicates: readonly { field: string }[]; joins?: unknown };
    assert.equal(body.limit, 10);
    assert.equal(body.predicates[0]?.field, "email");
    assert.deepEqual(body.joins, [{ model: "account", outputField: "account", fromField: "id", toField: "userId", relation: "one-to-many", limit: 2 }]);
  });

  test("stages a transaction through begin, scoped operation, and commit", async () => {
    const paths: string[] = [];
    const client = new AuthPersistenceClient({
      apiBaseUrl: "https://api.example.test",
      fetch: async (input, init) => {
        const request = new Request(input, init);
        paths.push(new URL(request.url).pathname);
        const pathname = new URL(request.url).pathname;
        if (pathname.endsWith("/transactions/begin")) return Response.json({ transactionId: "tx#1", expiresAtMs: Date.now() + 30_000, matched: true, record: null });
        if (pathname.endsWith("/transactions/commit")) return Response.json({ matched: true, record: null });
        if (pathname.endsWith("/index")) return Response.json({ matched: true, record: null });
        return Response.json({ matched: true, record: { id: "u1" } });
      },
    });
    const adapter = createTerraScaleAuthAdapter(client, "auth")({ appName: "test", secret: "test-secret-test-secret-test-secret" });
    await adapter.transaction(async (transaction) => {
      await transaction.update<{ id: string }>({ model: "user", where: [{ field: "id", value: "u1" }], update: { id: "u1" } });
    });
    assert.equal(paths.some((path) => path.endsWith("/transactions/begin")), true);
    assert.equal(paths.some((path) => path.endsWith("/transactions/commit")), true);
    assert.equal(paths.some((path) => path.endsWith("/updateOne")), true);
  });

  test("fails closed on index provisioning and retries before the next operation", async () => {
    let denyIndexes = true;
    const paths: string[] = [];
    const client = new AuthPersistenceClient({
      apiBaseUrl: "https://api.example.test",
      fetch: async (input, init) => {
        const request = new Request(input, init);
        const pathname = new URL(request.url).pathname;
        paths.push(pathname);
        if (pathname.endsWith("/index") && denyIndexes) return new Response(JSON.stringify({ error: { code: "forbidden", message: "denied" } }), { status: 403, headers: { "content-type": "application/json" } });
        if (pathname.endsWith("/index")) return Response.json({ matched: true, record: null });
        return Response.json({ matched: true, records: [] });
      },
    });
    const adapter = createTerraScaleAuthAdapter(client, "auth")({ appName: "test", secret: "test-secret-test-secret-test-secret" });

    await assert.rejects(adapter.findMany({ model: "user", limit: 1 }), /denied/);
    assert.equal(paths.some((path) => path.endsWith("/findPage")), false);
    denyIndexes = false;
    await adapter.findMany({ model: "user", limit: 1 });
    assert.equal(paths.some((path) => path.endsWith("/findPage")), true);
  });
});
