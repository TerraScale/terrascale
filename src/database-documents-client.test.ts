import assert from "node:assert/strict";
import { describe, test } from "vitest";

import { createTerraScaleClient } from "./factory.js";
import { toUlid } from "./ids.js";

const scope = { database: "customer database", collection: "users/v1" } as const;
const documentId = "user #1";
const idempotencyKey = toUlid("01ARZ3NDEKTSV4RRFFQ69G5FB0");

describe("DatabaseDocumentsClient", () => {
  test("matches every published Database API document operation", async () => {
    const requests: Request[] = [];
    const responses = [
      {
        database: scope.database,
        collection: scope.collection,
        id: documentId,
        version: "18446744073709551615",
        body: { name: "Ada" },
        position: "18446744073709551615",
      },
      ...Array.from({ length: 3 }, (_, index) => ({
        database: scope.database,
        collection: scope.collection,
        id: documentId,
        version: String(index + 2),
        sequence: index === 0 ? "18446744073709551615" : String(index + 10),
        operationId: `operation-${index + 1}`,
        acknowledgement: index === 0 ? "global" : "regional",
        backup: "full",
      })),
    ];
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      accessToken: "database-token",
      fetch: async input => {
        requests.push((input instanceof Request ? input : new Request(input)).clone());
        const response = responses.shift();
        assert.notEqual(response, undefined);
        return Response.json(response, { status: requests.length === 2 ? 201 : 200 });
      },
    });

    const read = await client.databaseDocuments.read<{ readonly name: string }>(scope, documentId);
    const created = await client.databaseDocuments.create(
      scope,
      documentId,
      { body: { name: "Ada" }, durability: "global" },
      { idempotencyKey },
    );
    const patched = await client.databaseDocuments.patch(
      scope,
      documentId,
      { body: { title: "Engineer", nickname: null }, durability: "regional" },
      { idempotencyKey },
    );
    const deleted = await client.databaseDocuments.delete(scope, documentId, {
      idempotencyKey,
      durability: "global",
    });

    assert.equal(read.ok && read.value.body.name, "Ada");
    assert.equal(read.ok && read.value.version, "18446744073709551615");
    assert.equal(read.ok && read.value.position, "18446744073709551615");
    assert.equal(created.ok && created.value.acknowledgement, "global");
    assert.equal(created.ok && created.value.sequence, "18446744073709551615");
    assert.equal(patched.ok && patched.value.version, "3");
    assert.equal(deleted.ok && deleted.value.operationId, "operation-3");

    const expectedPath = "/api/v1/databases/customer%20database/collections/users%2Fv1/documents/user%20%231";
    assert.deepEqual(requests.map(request => [request.method, new URL(request.url).pathname]), [
      ["GET", expectedPath],
      ["PUT", expectedPath],
      ["PATCH", expectedPath],
      ["DELETE", expectedPath],
    ]);
    assert.deepEqual(await requests[1]?.json(), {
      body: { name: "Ada" },
      durability: "global",
    });
    assert.deepEqual(await requests[2]?.json(), {
      body: { title: "Engineer", nickname: null },
      durability: "regional",
    });
    assert.equal(await requests[3]?.text(), "");
    assert.equal(new URL(requests[3]!.url).searchParams.get("durability"), "global");
    for (const request of requests) {
      assert.equal(request.headers.get("authorization"), "Bearer database-token");
    }
    assert.equal(requests[0]?.headers.get("idempotency-key"), null);
    for (const request of requests.slice(1)) {
      assert.equal(request.headers.get("idempotency-key"), idempotencyKey);
    }
  });

  test("preserves exact uint64 boundaries across all four document routes", async () => {
    const boundaries = {
      safeMaximum: "9007199254740991",
      firstUnsafe: "9007199254740992",
      uint64Maximum: "18446744073709551615",
    } as const;
    const responses = [
      {
        database: scope.database,
        collection: scope.collection,
        id: documentId,
        version: boundaries.safeMaximum,
        body: { name: "Ada" },
        position: boundaries.firstUnsafe,
      },
      {
        database: scope.database,
        collection: scope.collection,
        id: documentId,
        version: boundaries.firstUnsafe,
        sequence: boundaries.uint64Maximum,
        operationId: "operation-create",
        acknowledgement: "regional",
        backup: "none",
      },
      {
        database: scope.database,
        collection: scope.collection,
        id: documentId,
        version: boundaries.uint64Maximum,
        sequence: boundaries.safeMaximum,
        operationId: "operation-patch",
        acknowledgement: "global",
        backup: "full",
      },
      {
        database: scope.database,
        collection: scope.collection,
        id: documentId,
        version: boundaries.safeMaximum,
        sequence: boundaries.firstUnsafe,
        operationId: "operation-delete",
        acknowledgement: "backup",
        backup: "partial",
      },
    ];
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async () => {
        const response = responses.shift();
        assert.notEqual(response, undefined);
        return Response.json(response);
      },
    });

    const read = await client.databaseDocuments.read(scope, documentId);
    const created = await client.databaseDocuments.create(
      scope,
      documentId,
      { body: { name: "Ada" } },
      { idempotencyKey },
    );
    const patched = await client.databaseDocuments.patch(
      scope,
      documentId,
      { body: { name: "Ada" } },
      { idempotencyKey },
    );
    const deleted = await client.databaseDocuments.delete(scope, documentId, { idempotencyKey });

    assert.equal(read.ok, true);
    assert.equal(created.ok, true);
    assert.equal(patched.ok, true);
    assert.equal(deleted.ok, true);
    if (read.ok && created.ok && patched.ok && deleted.ok) {
      assert.equal(read.value.version, boundaries.safeMaximum);
      assert.equal(read.value.position, boundaries.firstUnsafe);
      assert.equal(created.value.version, boundaries.firstUnsafe);
      assert.equal(created.value.sequence, boundaries.uint64Maximum);
      assert.equal(patched.value.version, boundaries.uint64Maximum);
      assert.equal(patched.value.sequence, boundaries.safeMaximum);
      assert.equal(deleted.value.version, boundaries.safeMaximum);
      assert.equal(deleted.value.sequence, boundaries.firstUnsafe);
    }
  });

  test("reuses the explicit mutation ULID across documented transient retries", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      maxRetryAttempts: 1,
      retryDelayMs: 0,
      fetch: async input => {
        requests.push((input instanceof Request ? input : new Request(input)).clone());
        return requests.length === 1
          ? Response.json({ code: "engine_unavailable", detail: "retry" }, { status: 503 })
          : Response.json({
            database: scope.database,
            collection: scope.collection,
            id: documentId,
            version: "1",
            sequence: "1",
            operationId: "operation-1",
            acknowledgement: "regional",
            backup: "none",
          }, { status: 201 });
      },
    });

    const result = await client.databaseDocuments.create(
      scope,
      documentId,
      { body: { name: "Ada" } },
      { idempotencyKey },
    );

    assert.equal(result.ok, true);
    assert.equal(requests.length, 2);
    assert.deepEqual(
      requests.map(request => request.headers.get("idempotency-key")),
      [idempotencyKey, idempotencyKey],
    );
  });

  test("accepts zero and nullable position in a valid read response", async () => {
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async () => Response.json({
        database: scope.database,
        collection: scope.collection,
        id: documentId,
        version: "0",
        body: { name: "Ada", count: 0 },
        position: null,
      }),
    });

    const result = await client.databaseDocuments.read(scope, documentId);

    assert.equal(result.ok, true);
    if (result.ok) {
      assert.equal(result.value.version, "0");
      assert.equal(result.value.position, null);
      assert.deepEqual(result.value.body, { name: "Ada", count: 0 });
    }
  });

  test("projects the standardized Database API error without conflating request and trace IDs", async () => {
    const responseBody = {
      error: {
        code: "TERRASCALE-2000",
        message: "The document does not satisfy the active schema.",
        category: "Schema",
        detail: "profile.email must be a string.",
        suggestedAction: "Correct the document to match the active schema.",
        docsUrl: "https://docs.terrascale.tech/errors/TERRASCALE-2000",
        traceId: "trace-database-1",
        fieldErrors: [{
          field: "profile.email",
          constraint: "String",
          message: "Expected a string.",
        }],
      },
    } as const;
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async () => Response.json(responseBody, {
        status: 422,
        headers: { "x-request-id": "request-database-1" },
      }),
    });

    const result = await client.databaseDocuments.create(
      scope,
      documentId,
      { body: { profile: { email: 42 } } },
      { idempotencyKey },
    );

    assert.equal(result.ok, false);
    if (result.ok) return;
    assert.equal(result.error.kind, "validation");
    assert.equal(result.error.code, "TERRASCALE-2000");
    assert.equal(result.error.message, responseBody.error.message);
    assert.equal(result.error.category, "Schema");
    assert.equal(result.error.detail, responseBody.error.detail);
    assert.equal(result.error.suggestedAction, responseBody.error.suggestedAction);
    assert.equal(result.error.docsUrl, responseBody.error.docsUrl);
    assert.equal(result.error.traceId, "trace-database-1");
    assert.equal(result.error.requestId, "request-database-1");
    assert.deepEqual(result.error.validation, [{
      path: "profile.email",
      code: "String",
      message: "Expected a string.",
    }]);
    assert.deepEqual(result.error.details, responseBody);
  });

  test("rejects noncanonical or overflowing successful metadata", async () => {
    const validRead = {
      database: scope.database,
      collection: scope.collection,
      id: documentId,
      version: "1",
      body: { name: "Ada" },
      position: null,
    };
    const validWrite = {
      database: scope.database,
      collection: scope.collection,
      id: documentId,
      version: "1",
      sequence: "1",
      operationId: "operation-1",
      acknowledgement: "regional",
      backup: "none",
    };
    const cases = [
      { operation: "read", body: { ...validRead, version: 0 } },
      { operation: "read", body: { ...validRead, version: "" } },
      { operation: "read", body: { ...validRead, version: "00" } },
      { operation: "read", body: { ...validRead, version: "01" } },
      { operation: "read", body: { ...validRead, version: "-1" } },
      { operation: "read", body: { ...validRead, version: "+1" } },
      { operation: "read", body: { ...validRead, version: "1.0" } },
      { operation: "read", body: { ...validRead, version: "1e3" } },
      { operation: "read", body: { ...validRead, version: " 1" } },
      { operation: "read", body: { ...validRead, version: "1 " } },
      { operation: "read", body: { ...validRead, version: null } },
      { operation: "read", body: { ...validRead, version: "18446744073709551616" } },
      { operation: "read", body: { ...validRead, version: "184467440737095516150" } },
      { operation: "read", body: { ...validRead, position: 0 } },
      { operation: "read", body: { ...validRead, position: "18446744073709551616" } },
      { operation: "read", body: { ...validRead, position: "01" } },
      { operation: "read", body: { ...validRead, database: "other" } },
      { operation: "read", body: { ...validRead, unexpected: true } },
      { operation: "create", body: { ...validWrite, sequence: 0 } },
      { operation: "create", body: { ...validWrite, sequence: "18446744073709551616" } },
      { operation: "create", body: { ...validWrite, sequence: "00" } },
      { operation: "create", body: { ...validWrite, sequence: " 1" } },
      { operation: "create", body: { ...validWrite, sequence: null } },
      { operation: "create", body: { ...validWrite, version: 1.5 } },
      { operation: "create", body: { ...validWrite, version: "-1" } },
      { operation: "create", body: { ...validWrite, acknowledgement: "eventual" } },
      { operation: "create", body: { ...validWrite, backup: "unknown" } },
    ] as const;

    for (const candidate of cases) {
      const client = createTerraScaleClient({
        apiBaseUrl: "https://api.terrascale.test",
        fetch: async () => Response.json(candidate.body, { headers: { "x-request-id": "bad-metadata" } }),
      });
      const result = candidate.operation === "read"
        ? await client.databaseDocuments.read(scope, documentId)
        : await client.databaseDocuments.create(
          scope,
          documentId,
          { body: { name: "Ada" } },
          { idempotencyKey },
        );

      assert.equal(result.ok, false, JSON.stringify(candidate.body));
      if (!result.ok) {
        assert.equal(result.status, 200);
        assert.equal(result.error.kind, "invalid-response");
        assert.equal(result.error.code, "invalid_response");
        assert.equal(result.error.requestId, "bad-metadata");
        assert.deepEqual(result.body, candidate.body);
      }
    }
  });

  test("rejects invalid path identifiers and noncanonical mutation keys before fetch", () => {
    let fetches = 0;
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async () => {
        fetches++;
        return Response.json({});
      },
    });

    assert.throws(
      () => client.databaseDocuments.read({ ...scope, database: "" }, documentId),
      /database must contain between 1 and 64 UTF-8 bytes/u,
    );
    assert.throws(
      () => client.databaseDocuments.read(scope, "line\nbreak"),
      /documentId must not contain control characters/u,
    );
    assert.throws(
      () => client.databaseDocuments.read(scope, "x".repeat(65)),
      /documentId must contain between 1 and 64 UTF-8 bytes/u,
    );
    assert.throws(
      () => client.databaseDocuments.delete(scope, documentId, {
        idempotencyKey: "81ARZ3NDEKTSV4RRFFQ69G5FB0" as never,
      }),
      /idempotencyKey must be a canonical uppercase ULID/u,
    );
    assert.equal(fetches, 0);
  });

  test("honors caller cancellation without retrying the mutation", async () => {
    const controller = new AbortController();
    controller.abort(new Error("cancelled by caller"));
    let fetches = 0;
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      maxRetryAttempts: 2,
      fetch: async () => {
        fetches++;
        return Response.json({});
      },
    });

    const result = await client.databaseDocuments.patch(
      scope,
      documentId,
      { body: { active: true } },
      { idempotencyKey, signal: controller.signal },
    );

    assert.equal(result.ok, false);
    assert.equal(result.ok ? undefined : result.error.kind, "transport");
    assert.equal(fetches, 0);
  });
});
