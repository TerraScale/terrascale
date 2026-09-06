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

  test("exposes mapped unique indexes directly and inside serialized transactions", async () => {
    const requests: Request[] = [];
    const client = new AuthPersistenceClient({
      apiBaseUrl: "https://api.example.test",
      fetch: async (input, init) => {
        const request = new Request(input, init);
        requests.push(request);
        const path = new URL(request.url).pathname;
        if (path.endsWith("/transactions/begin")) return Response.json({ transactionId: "tx#1", expiresAtMs: Date.now() + 30_000, matched: true, record: null });
        if (path.endsWith("/transactions/commit")) return Response.json({ matched: true, record: null });
        return Response.json({ matched: true, record: null });
      },
    });
    const adapter = createTerraScaleAuthAdapter(client, "auth")({
      appName: "test",
      secret: "test-secret-test-secret-test-secret",
      user: { modelName: "members", fields: { email: "email_address" } },
    });

    await adapter.index({ model: "user", fields: ["email"] });
    await adapter.transaction(async transaction => transaction.index({ model: "user", fields: ["email"] }));
    const indexes = requests.filter(request => new URL(request.url).pathname.endsWith("/index"));
    assert.ok(indexes.length >= 2);
    const beforeRejectedIndex = indexes.length;
    await assert.rejects(adapter.index({ model: "user", fields: ["email"], unique: false }), /must be unique/u);
    assert.equal(requests.filter(request => new URL(request.url).pathname.endsWith("/index")).length, beforeRejectedIndex);
    const directRequest = indexes.filter(request => !request.headers.has("x-terrascale-auth-transaction")).at(-1)!;
    const transactionRequest = indexes.find(request => request.headers.has("x-terrascale-auth-transaction"))!;
    const directBody = await directRequest.clone().json() as { model: string; fields: string[] };
    assert.deepEqual(directBody, { model: "members", fields: ["email_address"] });
    assert.match(directRequest.headers.get("idempotency-key")!, /^better-auth-index-/u);
    assert.equal(transactionRequest.headers.get("x-terrascale-auth-transaction"), "tx#1");
  });

  test("rejects malformed index success and rolls back a poisoned transaction", async () => {
    const paths: string[] = [];
    const malformed = new AuthPersistenceClient({
      apiBaseUrl: "https://api.example.test",
      fetch: async () => {
        return Response.json({ matched: false, record: { id: "unexpected" } });
      },
    });
    const direct = createTerraScaleAuthAdapter(malformed, "auth")({ appName: "test", secret: "test-secret-test-secret-test-secret" });
    await assert.rejects(direct.index({ model: "user", fields: ["email"] }), /invalid success result/u);

    const transactional = new AuthPersistenceClient({
      apiBaseUrl: "https://api.example.test",
      fetch: async (input, init) => {
        const request = new Request(input, init);
        const path = new URL(request.url).pathname;
        paths.push(path);
        if (path.endsWith("/transactions/begin")) return Response.json({ transactionId: "tx#2", expiresAtMs: Date.now() + 30_000, matched: true, record: null });
        if (path.endsWith("/transactions/rollback")) return Response.json({ matched: true, record: null });
        if (path.endsWith("/transactions/commit")) return Response.json({ matched: true, record: null });
        if (path.endsWith("/index") && request.headers.has("x-terrascale-auth-transaction")) return Response.json({ matched: false, record: { id: "unexpected" } });
        return Response.json({ matched: true, record: null });
      },
    });
    const adapter = createTerraScaleAuthAdapter(transactional, "auth")({ appName: "test", secret: "test-secret-test-secret-test-secret" });
    await assert.rejects(adapter.transaction(async transaction => transaction.index({ model: "user", fields: ["email"] })), /invalid success result/u);
    assert.equal(paths.filter(path => path.endsWith("/transactions/rollback")).length, 1);
    assert.equal(paths.filter(path => path.endsWith("/transactions/commit")).length, 0);
  });
});
