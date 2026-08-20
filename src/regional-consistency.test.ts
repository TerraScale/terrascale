import assert from "node:assert/strict";
import { describe, test } from "node:test";

import { capturePublicRequest } from "./client-route-capture.js";
import { createTerraScaleClient } from "./index.js";
import { toUlid } from "./ids.js";

const repositoryId = toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAV");
const documentId = toUlid("01BX5ZZKBKACTAV9WEVGEMMVRZ");
const otherDocumentId = toUlid("01H00000000000000000000000");

describe("regional document consistency", () => {
  test("rejects invalid or incomplete strict regional configuration", () => {
    assert.throws(
      () => createTerraScaleClient({ preferredRegion: "bad region", fetch: globalThis.fetch }),
      /valid TerraScale region code/u,
    );
    assert.throws(
      () => createTerraScaleClient({ regionAffinity: "strict", fetch: globalThis.fetch }),
      /requires a preferredRegion/u,
    );
    assert.throws(
      () => createTerraScaleClient({ regionAffinity: "unsupported", fetch: globalThis.fetch } as never),
      /regionAffinity must be either/u,
    );
  });

  test("uses the global endpoint by default and a regional hostname for strict affinity", async () => {
    const globalCapture = await capturePublicRequest(client =>
      client.documents.get({ repositoryId }, documentId));
    const globalRequests = globalCapture.requests;

    assert.equal(globalRequests[0]?.url, `https://api.terrascale.tech/api/v1/repositories/${repositoryId}/documents/${documentId}`);
    assert.equal(globalRequests[0]?.headers.get("x-terrascale-preferred-region"), null);

    const strictCapture = await capturePublicRequest(
      client => client.documents.get({ repositoryId }, documentId),
      { config: { preferredRegion: "SFO", regionAffinity: "strict" } },
    );
    const strictRequests = strictCapture.requests;

    assert.equal(strictRequests[0]?.url, `https://sfo.api.terrascale.tech/api/v1/repositories/${repositoryId}/documents/${documentId}`);
    assert.equal(strictRequests[0]?.headers.get("x-terrascale-preferred-region"), null);
  });

  test("passes preferred-region and stale-read controls on the global route", async () => {
    const capture = await capturePublicRequest(
      client => client.documents.get({ repositoryId }, documentId, { allowStale: true }),
      { config: { preferredRegion: "FRA", regionAffinity: "failover" } },
    );
    const requests = capture.requests;

    assert.equal(requests[0]?.headers.get("x-terrascale-preferred-region"), "fra");
    assert.equal(requests[0]?.headers.get("x-terrascale-allow-stale"), "true");
  });

  test("keeps stale-read controls on single-document reads only", () => {
    const client = createTerraScaleClient({ fetch: globalThis.fetch });

    if (false) {
      // @ts-expect-error list does not support the single-document stale-read contract
      void client.documents.list({ repositoryId }, undefined, { allowStale: true });
      // @ts-expect-error TQL does not support the single-document stale-read contract
      void client.query.execute({ repositoryId }, { query: "from users" }, { allowStale: true });
    }
  });

  test("sends expected change tokens and carries session consistency tokens to later reads", async () => {
    const responses = [
      {
        repositoryId,
        repository: "users",
        id: documentId,
        data: { name: "Grace" },
        changeToken: "change-2",
        sessionConsistencyToken: "session-2",
      },
      {
        repositoryId,
        repository: "users",
        id: documentId,
        data: { name: "Grace" },
        changeToken: "change-2",
      },
    ];
    const capture = await capturePublicRequest(
      async client => {
        const write = await client.documents.put(
          { repositoryId },
          documentId,
          { name: "Grace" },
          { expectedChangeToken: "change-1" },
        );
        const read = await client.documents.get({ repositoryId }, documentId);
        return { write, read };
      },
      {
        config: { sessionConsistency: "automatic" },
        respond: () => Response.json(responses.shift()),
      },
    );
    const { write, read } = capture.result;
    const requests = capture.requests;

    assert.equal(write.ok && write.value.sessionConsistencyToken, "session-2");
    assert.equal(read.ok, true);
    assert.equal(requests[0]?.headers.get("if-match"), "change-1");
    assert.equal(requests[1]?.headers.get("x-terrascale-session-consistency-token"), "session-2");
  });

  test("scopes automatic session tokens to the document that produced them", async () => {
    const capture = await capturePublicRequest(
      async client => {
        await client.documents.put({ repositoryId }, documentId, { name: "Grace" });
        await client.documents.get({ repositoryId }, otherDocumentId);
        return client.documents.get({ repositoryId }, documentId);
      },
      {
        config: { sessionConsistency: "automatic" },
        respond: () => Response.json({
          repositoryId,
          repository: "users",
          id: documentId,
          data: { name: "Grace" },
          changeToken: "change-2",
          sessionConsistencyToken: "session-2",
        }),
      },
    );
    const requests = capture.requests;

    assert.equal(requests[1]?.headers.get("x-terrascale-session-consistency-token"), null);
    assert.equal(requests[2]?.headers.get("x-terrascale-session-consistency-token"), "session-2");
  });

  test("keeps session tokens inside their client and tenant boundary", async () => {
    const firstCapture = await capturePublicRequest(
      client => client.documents.put({ repositoryId }, documentId, { name: "Grace" }),
      {
        config: {
          projectId: toUlid("01J00000000000000000000000"),
          sessionConsistency: "automatic",
        },
        respond: (_request, attempt) => Response.json({
          repositoryId,
          repository: "users",
          id: documentId,
          data: { name: "Grace" },
          changeToken: "change-2",
          ...(attempt === 0 ? { sessionConsistencyToken: "tenant-a-session" } : {}),
        }),
      },
    );
    const secondCapture = await capturePublicRequest(
      client => client.documents.get({ repositoryId }, documentId),
      {
        config: {
          projectId: toUlid("01J00000000000000000000001"),
          sessionConsistency: "automatic",
        },
        respond: () => Response.json({
          repositoryId,
          repository: "users",
          id: documentId,
          data: { name: "Grace" },
          changeToken: "change-2",
        }),
      },
    );
    const requests = [...firstCapture.requests, ...secondCapture.requests];

    assert.equal(requests[0]?.headers.get("x-terrascale-project-id"), "01J00000000000000000000000");
    assert.equal(requests[1]?.headers.get("x-terrascale-project-id"), "01J00000000000000000000001");
    assert.equal(requests[1]?.headers.get("x-terrascale-session-consistency-token"), null);
  });

  test("propagates session tokens from batch writes to the matching document reads", async () => {
    const capture = await capturePublicRequest(
      async client => {
        await client.documents.batch({ repositoryId }, {
          operations: [
            { operation: "upsert", documentId, data: { name: "Grace" } },
            { operation: "upsert", documentId: otherDocumentId, data: { name: "Ada" } },
          ],
        });
        await client.documents.get({ repositoryId }, documentId);
        return client.documents.get({ repositoryId }, otherDocumentId);
      },
      {
        config: { sessionConsistency: "automatic" },
        respond: (request) => {
          if (request.url.endsWith("/documents/batch")) {
          return Response.json({
            repositoryId,
            repository: "users",
            results: [
              {
                index: 0,
                operation: "upsert",
                id: documentId,
                document: {
                  repositoryId,
                  repository: "users",
                  id: documentId,
                  data: { name: "Grace" },
                  changeToken: "change-a",
                  sessionConsistencyToken: "session-a",
                },
                deleted: null,
              },
              {
                index: 1,
                operation: "upsert",
                id: otherDocumentId,
                document: {
                  repositoryId,
                  repository: "users",
                  id: otherDocumentId,
                  data: { name: "Ada" },
                  changeToken: "change-b",
                  sessionConsistencyToken: "session-b",
                },
                deleted: null,
              },
            ],
          });
          }

          return Response.json({
            repositoryId,
            repository: "users",
            id: request.url.endsWith(`/${documentId}`) ? documentId : otherDocumentId,
            data: {},
            changeToken: "change-read",
          });
        },
      },
    );
    const requests = capture.requests;

    assert.equal(requests[1]?.headers.get("x-terrascale-session-consistency-token"), "session-a");
    assert.equal(requests[2]?.headers.get("x-terrascale-session-consistency-token"), "session-b");
  });

  test("retries transient reads with stable regional headers but never retries an unkeyed mutation", async () => {
    const readCapture = await capturePublicRequest(
      client => client.documents.get({ repositoryId }, documentId),
      {
        config: { preferredRegion: "FRA", maxRetryAttempts: 1, retryDelayMs: 0 },
        respond: (_request, attempt) => attempt === 0
          ? Response.json({ error: { code: "temporarily_unavailable", message: "Retry" } }, { status: 503 })
          : Response.json({ repositoryId, repository: "users", id: documentId, data: {} }),
      },
    );
    const read = readCapture.result;
    const readRequests = readCapture.requests;
    assert.equal(read.ok, true);
    assert.equal(readRequests.length, 2);
    assert.equal(readRequests[0]?.headers.get("x-terrascale-preferred-region"), "fra");
    assert.equal(readRequests[1]?.headers.get("x-terrascale-preferred-region"), "fra");

    const mutationCapture = await capturePublicRequest(
      client => client.documents.put({ repositoryId }, documentId, { name: "Ada" }),
      {
        config: { maxRetryAttempts: 1, retryDelayMs: 0 },
        respond: () => Response.json({ error: { code: "temporarily_unavailable", message: "Retry" } }, { status: 503 }),
      },
    );
    const mutation = mutationCapture.result;
    const mutationRequests = mutationCapture.requests;
    assert.equal(mutation.ok, false);
    assert.equal(mutationRequests.length, 1);
  });

  test("retries a transient TQL read without treating it as a retryable mutation", async () => {
    const capture = await capturePublicRequest(
      client => client.query.execute({ repositoryId }, { query: "from users" }),
      {
        config: { maxRetryAttempts: 1, retryDelayMs: 0 },
        respond: (_request, attempt) => attempt === 0
          ? Response.json({ error: { code: "temporarily_unavailable", message: "Retry" } }, { status: 503 })
          : Response.json({ skip: 0, take: 100, total: 0, items: [] }),
      },
    );
    const result = capture.result;
    const requests = capture.requests;

    assert.equal(result.ok, true);
    assert.equal(requests.length, 2);
    assert.equal(requests[0]?.method, "POST");
    assert.equal(requests[0]?.headers.get("idempotency-key"), null);
  });

  test("types conditional conflicts and routing observations without remote diagnostics", async () => {
    const conflictCapture = await capturePublicRequest(
      client => client.documents.delete(
        { repositoryId },
        documentId,
        { expectedChangeToken: "stale" },
      ),
      {
        respond: () => Response.json(
          { error: { code: "document_change_token_conflict", message: "The document changed." } },
          { status: 409 },
        ),
      },
    );
    const conflict = conflictCapture.result;

    assert.equal(conflict.ok, false);
    if (!conflict.ok) {
      assert.equal(conflict.error.kind, "conflict");
      assert.equal(conflict.error.code, "document_change_token_conflict");
    }

    const routedCapture = await capturePublicRequest(
      client => client.documents.get({ repositoryId }, documentId),
      {
        respond: () => Response.json({}, {
          headers: {
            "x-terrascale-edge-region": "gru",
            "x-terrascale-serving-region": "sfo",
            "x-terrascale-route-mode": "preferred-failover",
            "x-terrascale-failover-from-region": "fra",
          },
        }),
      },
    );
    const routed = routedCapture.result;

    assert.equal(routed.ok, true);
    if (routed.ok) {
      assert.deepEqual(routed.routing, {
        edgeRegion: "gru",
        servingRegion: "sfo",
        routeMode: "preferred-failover",
        failoverFromRegion: "fra",
        degraded: true,
      });
    }
  });

  test("drops malformed routing metadata instead of exposing operator values", async () => {
    const capture = await capturePublicRequest(
      client => client.documents.get({ repositoryId }, documentId),
      {
        respond: () => Response.json({}, {
          headers: {
            "x-terrascale-edge-region": "operator-node-17.internal",
            "x-terrascale-serving-region": "sfo",
            "x-terrascale-route-mode": "preferred",
          },
        }),
      },
    );
    const result = capture.result;

    assert.equal(result.ok, true);
    if (result.ok) {
      assert.deepEqual(result.routing, {
        servingRegion: "sfo",
        routeMode: "preferred",
        degraded: false,
      });
    }
  });

  test("aborts a transient retry during its backoff", async () => {
    const controller = new AbortController();
    let attempts = 0;
    const capture = capturePublicRequest(
      client => client.documents.get(
        { repositoryId },
        documentId,
        { signal: controller.signal },
      ),
      {
        config: { maxRetryAttempts: 1, retryDelayMs: 5_000 },
        respond: () => {
          attempts++;
          return Response.json({ error: { code: "temporarily_unavailable" } }, { status: 503 });
        },
      },
    );
    setTimeout(() => controller.abort(), 0);
    const result = (await capture).result;

    assert.equal(attempts, 1);
    assert.equal(result.ok, false);
    assert.equal(result.ok ? undefined : result.error.kind, "transport");
  });

  test("types a strict regional outage as unavailable", async () => {
    const capture = await capturePublicRequest(
      client => client.documents.get({ repositoryId }, documentId),
      {
        config: { preferredRegion: "gru", regionAffinity: "strict" },
        respond: () => Response.json(
          { error: { code: "project_region_unavailable", message: "The selected region is unavailable." } },
          { status: 503 },
        ),
      },
    );
    const result = capture.result;

    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.equal(result.error.kind, "unavailable");
      assert.equal(result.error.code, "project_region_unavailable");
    }
  });
});
