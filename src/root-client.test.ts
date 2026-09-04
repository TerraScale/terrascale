import assert from "node:assert/strict";
import { describe, test } from "vitest";

import { createTerraScaleClient } from "./factory.js";
import { ReactiveContractGapError } from "./client.js";

function response(value: unknown, status = 200): Response {
  return new Response(value === undefined ? "" : JSON.stringify(value), {
    status,
    headers: { "content-type": "application/json" },
  });
}

describe("root database clients", () => {
  test("exposes non-enumerable document, SQL, and reactive surfaces", () => {
    const client = createTerraScaleClient({ fetch: async () => response({}) });

    assert.ok(client.documents);
    assert.ok(client.sql);
    assert.ok(client.reactive);
    assert.equal(Object.prototype.propertyIsEnumerable.call(client, "documents"), false);
    assert.equal(Object.prototype.propertyIsEnumerable.call(client, "sql"), false);
    assert.equal(Object.prototype.propertyIsEnumerable.call(client, "reactive"), false);
  });

  test("uses raw JSON document bodies and direct document paths", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleClient({
      accessToken: "secret-token",
      fetch: async (input) => {
        const request = new Request(input);
        requests.push(request);
        if (request.method === "GET") return response({ name: "Ada" });
        if (request.method === "DELETE") return new Response(null, { status: 204 });
        return response({ revision: "rev-1" });
      },
    });

    const put = await client.documents.put("prod", "users", "ada/1", "primitive", {
      idempotencyKey: "write-1",
      ifMatch: '"old"',
    });
    const patch = await client.documents.patch("prod", "users", "ada/1", null);
    const got = await client.documents.get("prod", "users", "ada/1");
    const deleted = await client.documents.delete("prod", "users", "ada/1");

    assert.equal(put.ok, true);
    assert.equal(patch.ok, true);
    assert.equal(got.ok, true);
    if (got.ok) assert.deepEqual(got.value, { name: "Ada" });
    assert.equal(deleted.ok, true);
    assert.equal(new URL(requests[0]!.url).pathname, "/api/v1/databases/prod/collections/users/documents/ada%2F1");
    assert.equal(await requests[0]?.text(), '"primitive"');
    assert.equal(requests[0]?.headers.get("authorization"), "Bearer secret-token");
    assert.equal(requests[0]?.headers.get("idempotency-key"), "write-1");
    assert.equal(requests[0]?.headers.get("if-match"), '"old"');
    assert.equal(new URL(requests[0]!.url).search, "");
    assert.equal(requests[1]?.method, "PATCH");
    assert.equal(await requests[1]?.text(), "null");
  });

  test("lists and executes SQL with canonical parameter objects", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleClient({
      fetch: async (input) => {
        const request = new Request(input);
        requests.push(request);
        return request.method === "GET"
          ? response({ documents: [{ id: "a", version: 3, value: { n: 1 } }] })
          : response({ matched: 1, rows: [{ id: "a", value: { n: 1 } }] });
      },
    });

    const listed = await client.documents.list("prod", "users", { limit: 25, after: "a" });
    const queried = await client.sql.execute("prod", {
      sql: "SELECT * FROM users WHERE n = :n",
      parameters: { n: 1 },
    });

    assert.equal(listed.ok, true);
    assert.equal(new URL(requests[0]!.url).search, "?limit=25&after=a");
    assert.equal(queried.ok, true);
    assert.deepEqual(JSON.parse(await requests[1]!.text()), {
      sql: "SELECT * FROM users WHERE n = :n",
      parameters: { n: 1 },
    });
  });

  test("returns typed cancellation without starting an already-aborted fetch", async () => {
    let calls = 0;
    const controller = new AbortController();
    controller.abort();
    const client = createTerraScaleClient({
      fetch: async () => {
        calls++;
        return response({});
      },
    });

    const result = await client.documents.get("prod", "users", "a", { signal: controller.signal });
    assert.equal(calls, 0);
    assert.equal(result.ok, false);
    if (!result.ok) assert.equal(result.error.code, "request_cancelled");
  });

  test("fails closed for the unpublished direct reactive producer", async () => {
    let calls = 0;
    const client = createTerraScaleClient({
      fetch: async () => {
        calls++;
        return response({});
      },
    });

    await assert.rejects(
      client.reactive.subscribe({ database: "prod", query: "SELECT * FROM users" }),
      (error: unknown) => error instanceof ReactiveContractGapError && error.code === "contract_gap",
    );
    assert.equal(calls, 0);
  });

  test("rejects invalid direct identifiers and bounds before fetch", async () => {
    let calls = 0;
    const client = createTerraScaleClient({
      fetch: async () => {
        calls++;
        return response({});
      },
    });

    assert.throws(() => client.documents.list("prod", "users", { limit: 1001 }), TypeError);
    assert.throws(() => client.documents.get("prod", "users", ""), TypeError);
    assert.throws(() => client.sql.execute("prod", { sql: "SELECT 1", timeout_ms: 30_001 }), TypeError);
    assert.equal(calls, 0);
  });
});
