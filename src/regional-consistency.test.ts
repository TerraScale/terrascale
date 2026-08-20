import assert from "node:assert/strict";
import { describe, test } from "node:test";

import { createTerraScaleClient } from "./index.js";
import { toUlid } from "./ids.js";

const repositoryId = toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAV");
const documentId = toUlid("01BX5ZZKBKACTAV9WEVGEMMVRZ");
const otherDocumentId = toUlid("01H00000000000000000000000");

describe("regional document consistency", () => {
  test("rejects invalid or incomplete strict regional configuration", () => {
    assert.throws(
      () => createTerraScaleClient({ preferredRegion: "bad region", fetch: capture([]) }),
      /valid TerraScale region code/u,
    );
    assert.throws(
      () => createTerraScaleClient({ regionAffinity: "strict", fetch: capture([]) }),
      /requires a preferredRegion/u,
    );
    assert.throws(
      () => createTerraScaleClient({ regionAffinity: "unsupported", fetch: capture([]) } as never),
      /regionAffinity must be either/u,
    );
  });

  test("uses the global endpoint by default and a regional hostname for strict affinity", async () => {
    const globalRequests: Request[] = [];
    const globalClient = createTerraScaleClient({
      fetch: capture(globalRequests),
    });

    await globalClient.documents.get({ repositoryId }, documentId);

    assert.equal(globalRequests[0]?.url, `https://api.terrascale.tech/api/v1/repositories/${repositoryId}/documents/${documentId}`);
    assert.equal(globalRequests[0]?.headers.get("x-terrascale-preferred-region"), null);

    const strictRequests: Request[] = [];
    const strictClient = createTerraScaleClient({
      preferredRegion: "SFO",
      regionAffinity: "strict",
      fetch: capture(strictRequests),
    });

    await strictClient.documents.get({ repositoryId }, documentId);

    assert.equal(strictRequests[0]?.url, `https://sfo.api.terrascale.tech/api/v1/repositories/${repositoryId}/documents/${documentId}`);
    assert.equal(strictRequests[0]?.headers.get("x-terrascale-preferred-region"), null);
  });

  test("passes preferred-region and stale-read controls on the global route", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleClient({
      preferredRegion: "FRA",
      regionAffinity: "failover",
      fetch: capture(requests),
    });

    await client.documents.get({ repositoryId }, documentId, { allowStale: true });

    assert.equal(requests[0]?.headers.get("x-terrascale-preferred-region"), "fra");
    assert.equal(requests[0]?.headers.get("x-terrascale-allow-stale"), "true");
  });

  test("keeps stale-read controls on single-document reads only", () => {
    const client = createTerraScaleClient({ fetch: capture([]) });

    if (false) {
      // @ts-expect-error list does not support the single-document stale-read contract
      void client.documents.list({ repositoryId }, undefined, { allowStale: true });
      // @ts-expect-error TQL does not support the single-document stale-read contract
      void client.query.execute({ repositoryId }, { query: "from users" }, { allowStale: true });
    }
  });

  test("sends expected change tokens and carries session consistency tokens to later reads", async () => {
    const requests: Request[] = [];
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
    const client = createTerraScaleClient({
      sessionConsistency: "automatic",
      fetch: async input => {
        requests.push(input instanceof Request ? input : new Request(input));
        return Response.json(responses.shift());
      },
    });

    const write = await client.documents.put(
      { repositoryId },
      documentId,
      { name: "Grace" },
      { expectedChangeToken: "change-1" },
    );
    const read = await client.documents.get({ repositoryId }, documentId);

    assert.equal(write.ok && write.value.sessionConsistencyToken, "session-2");
    assert.equal(read.ok, true);
    assert.equal(requests[0]?.headers.get("if-match"), "change-1");
    assert.equal(requests[1]?.headers.get("x-terrascale-session-consistency-token"), "session-2");
  });

  test("scopes automatic session tokens to the document that produced them", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleClient({
      sessionConsistency: "automatic",
      fetch: async input => {
        requests.push(input instanceof Request ? input : new Request(input));
        return Response.json({
          repositoryId,
          repository: "users",
          id: documentId,
          data: { name: "Grace" },
          changeToken: "change-2",
          sessionConsistencyToken: "session-2",
        });
      },
    });

    await client.documents.put({ repositoryId }, documentId, { name: "Grace" });
    await client.documents.get({ repositoryId }, otherDocumentId);
    await client.documents.get({ repositoryId }, documentId);

    assert.equal(requests[1]?.headers.get("x-terrascale-session-consistency-token"), null);
    assert.equal(requests[2]?.headers.get("x-terrascale-session-consistency-token"), "session-2");
  });

  test("propagates session tokens from batch writes to the matching document reads", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleClient({
      sessionConsistency: "automatic",
      fetch: async input => {
        const request = input instanceof Request ? input : new Request(input);
        requests.push(request);
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
    });

    await client.documents.batch({ repositoryId }, {
      operations: [
        { operation: "upsert", documentId, data: { name: "Grace" } },
        { operation: "upsert", documentId: otherDocumentId, data: { name: "Ada" } },
      ],
    });
    await client.documents.get({ repositoryId }, documentId);
    await client.documents.get({ repositoryId }, otherDocumentId);

    assert.equal(requests[1]?.headers.get("x-terrascale-session-consistency-token"), "session-a");
    assert.equal(requests[2]?.headers.get("x-terrascale-session-consistency-token"), "session-b");
  });

  test("retries transient reads with stable regional headers but never retries an unkeyed mutation", async () => {
    const readRequests: Request[] = [];
    let readAttempts = 0;
    const readClient = createTerraScaleClient({
      preferredRegion: "FRA",
      maxRetryAttempts: 1,
      retryDelayMs: 0,
      fetch: async input => {
        const request = input instanceof Request ? input : new Request(input);
        readRequests.push(request);
        readAttempts++;
        return readAttempts === 1
          ? Response.json({ error: { code: "temporarily_unavailable", message: "Retry" } }, { status: 503 })
          : Response.json({ repositoryId, repository: "users", id: documentId, data: {} });
      },
    });

    const read = await readClient.documents.get({ repositoryId }, documentId);
    assert.equal(read.ok, true);
    assert.equal(readRequests.length, 2);
    assert.equal(readRequests[0]?.headers.get("x-terrascale-preferred-region"), "fra");
    assert.equal(readRequests[1]?.headers.get("x-terrascale-preferred-region"), "fra");

    const mutationRequests: Request[] = [];
    const mutationClient = createTerraScaleClient({
      maxRetryAttempts: 1,
      retryDelayMs: 0,
      fetch: async input => {
        mutationRequests.push(input instanceof Request ? input : new Request(input));
        return Response.json({ error: { code: "temporarily_unavailable", message: "Retry" } }, { status: 503 });
      },
    });

    const mutation = await mutationClient.documents.put({ repositoryId }, documentId, { name: "Ada" });
    assert.equal(mutation.ok, false);
    assert.equal(mutationRequests.length, 1);
  });

  test("retries a transient TQL read without treating it as a retryable mutation", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleClient({
      maxRetryAttempts: 1,
      retryDelayMs: 0,
      fetch: async input => {
        const request = input instanceof Request ? input : new Request(input);
        requests.push(request);
        return requests.length === 1
          ? Response.json({ error: { code: "temporarily_unavailable", message: "Retry" } }, { status: 503 })
          : Response.json({ skip: 0, take: 100, total: 0, items: [] });
      },
    });

    const result = await client.query.execute({ repositoryId }, { query: "from users" });

    assert.equal(result.ok, true);
    assert.equal(requests.length, 2);
    assert.equal(requests[0]?.method, "POST");
    assert.equal(requests[0]?.headers.get("idempotency-key"), null);
  });

  test("types conditional conflicts and routing observations without remote diagnostics", async () => {
    const conflictClient = createTerraScaleClient({
      fetch: async () => Response.json(
        { error: { code: "document_change_token_conflict", message: "The document changed." } },
        { status: 409 },
      ),
    });
    const conflict = await conflictClient.documents.delete(
      { repositoryId },
      documentId,
      { expectedChangeToken: "stale" },
    );

    assert.equal(conflict.ok, false);
    if (!conflict.ok) {
      assert.equal(conflict.error.kind, "conflict");
      assert.equal(conflict.error.code, "document_change_token_conflict");
    }

    const routedClient = createTerraScaleClient({
      fetch: async () => Response.json({}, {
        headers: {
          "x-terrascale-edge-region": "gru",
          "x-terrascale-serving-region": "sfo",
          "x-terrascale-route-mode": "preferred-failover",
          "x-terrascale-failover-from-region": "fra",
        },
      }),
    });
    const routed = await routedClient.documents.get({ repositoryId }, documentId);

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

  test("types a strict regional outage as unavailable", async () => {
    const client = createTerraScaleClient({
      preferredRegion: "gru",
      regionAffinity: "strict",
      fetch: async () => Response.json(
        { error: { code: "project_region_unavailable", message: "The selected region is unavailable." } },
        { status: 503 },
      ),
    });

    const result = await client.documents.get({ repositoryId }, documentId);

    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.equal(result.error.kind, "unavailable");
      assert.equal(result.error.code, "project_region_unavailable");
    }
  });
});

function capture(requests: Request[]): typeof fetch {
  return async input => {
    requests.push(input instanceof Request ? input : new Request(input));
    return Response.json({});
  };
}
