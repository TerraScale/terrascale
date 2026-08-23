import assert from "node:assert/strict";
import { describe, test } from "node:test";

import { createTerraScaleClient, resolveRuntimeConfig } from "./index.js";
import { toUlid } from "./ids.js";

const repositoryId = toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAV");
const otherRepositoryId = toUlid("01CZ5ZZKBKACTAV9WEVGEMMVRZ");
const documentId = toUlid("01BX5ZZKBKACTAV9WEVGEMMVRZ");

describe("regional document consistency", () => {
  test("rejects invalid or incomplete strict regional configuration", () => {
    assert.throws(
      () => resolveRuntimeConfig({ preferredRegion: "bad region", fetch: globalThis.fetch }),
      /valid TerraScale region code/u,
    );
    assert.throws(
      () => resolveRuntimeConfig({ regionAffinity: "strict", fetch: globalThis.fetch }),
      /requires a preferredRegion/u,
    );
    assert.throws(
      () => resolveRuntimeConfig({ regionAffinity: "unsupported", fetch: globalThis.fetch } as never),
      /regionAffinity must be either/u,
    );
  });

  test("resolves global, failover, and strict regional runtime boundaries", () => {
    const global = resolveRuntimeConfig({ fetch: globalThis.fetch });
    assert.equal(global.apiBaseUrl, "https://api.terrascale.tech");
    assert.equal(global.preferredRegion, undefined);
    assert.equal(global.regionAffinity, "failover");

    const failover = resolveRuntimeConfig({
      preferredRegion: "FRA",
      regionAffinity: "failover",
      fetch: globalThis.fetch,
    });
    assert.equal(failover.apiBaseUrl, "https://api.terrascale.tech");
    assert.equal(failover.preferredRegion, "fra");

    const strict = resolveRuntimeConfig({
      preferredRegion: "SFO",
      regionAffinity: "strict",
      fetch: globalThis.fetch,
    });
    assert.equal(strict.apiBaseUrl, "https://sfo.api.terrascale.tech");
    assert.equal(strict.preferredRegion, "sfo");
  });

  test("keeps session and retry policy in the resolved runtime boundary", () => {
    const defaults = resolveRuntimeConfig({ fetch: globalThis.fetch });
    assert.equal(defaults.sessionConsistency, "none");
    assert.equal(defaults.maxRetryAttempts, 0);
    assert.equal(defaults.retryDelayMs, 250);

    const configured = resolveRuntimeConfig({
      sessionConsistency: "automatic",
      maxRetryAttempts: 2,
      retryDelayMs: 0,
      fetch: globalThis.fetch,
    });
    assert.equal(configured.sessionConsistency, "automatic");
    assert.equal(configured.maxRetryAttempts, 2);
    assert.equal(configured.retryDelayMs, 0);
  });

  test("keeps stale-read controls on single-document reads only", () => {
    const client = createTerraScaleClient({ fetch: globalThis.fetch });

    if (false) {
      // @ts-expect-error list does not support the single-document stale-read contract
      void client.documents.list({ repositoryId }, undefined, { allowStale: true });
      // @ts-expect-error TQL does not support the single-document stale-read contract
      void client.query.execute({ repositoryId }, { query: "from users" }, { allowStale: true });
      void client.documents.get({ repositoryId }, documentId, { allowStale: true });
    }
  });

  test("constructs preferred failover headers and strict regional hosts", async () => {
    const requests: Request[] = [];
    const fetch = async (input: RequestInfo | URL): Promise<Response> => {
      requests.push(input instanceof Request ? input : new Request(input));
      return Response.json({});
    };

    const failover = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.tech",
      preferredRegion: "FRA",
      regionAffinity: "failover",
      fetch,
    });
    await failover.documents.get({ repositoryId }, documentId);

    const strict = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.tech",
      preferredRegion: "SFO",
      regionAffinity: "strict",
      fetch,
    });
    await strict.documents.get({ repositoryId }, documentId);

    assert.equal(new URL(requests[0]!.url).hostname, "api.terrascale.tech");
    assert.equal(requests[0]!.headers.get("x-terrascale-preferred-region"), "fra");
    assert.equal(new URL(requests[1]!.url).hostname, "sfo.api.terrascale.tech");
    assert.equal(requests[1]!.headers.has("x-terrascale-preferred-region"), false);
  });

  test("propagates write session tokens only to the matching document scope", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      sessionConsistency: "automatic",
      fetch: async input => {
        requests.push(input instanceof Request ? input : new Request(input));
        return Response.json({
          repositoryId,
          repository: "users",
          id: documentId,
          data: { name: "Ada" },
          sessionConsistencyToken: "session-document-a",
        });
      },
    });

    await client.documents.create({ repositoryId }, { name: "Ada" });
    await client.documents.get({ repositoryId }, documentId);
    await client.documents.get({ repositoryId: otherRepositoryId }, documentId);

    assert.equal(requests[0]!.headers.has("x-terrascale-session-consistency-token"), false);
    assert.equal(
      requests[1]!.headers.get("x-terrascale-session-consistency-token"),
      "session-document-a",
    );
    assert.equal(requests[2]!.headers.has("x-terrascale-session-consistency-token"), false);
  });

  test("sends AllowStale only for an opted-in single-document read", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async input => {
        requests.push(input instanceof Request ? input : new Request(input));
        return Response.json({});
      },
    });

    await client.documents.get({ repositoryId }, documentId, { allowStale: true });
    await client.documents.get({ repositoryId }, documentId, { allowStale: false });
    await client.documents.list({ repositoryId });

    assert.equal(requests[0]!.headers.get("x-terrascale-allow-stale"), "true");
    assert.equal(requests[1]!.headers.has("x-terrascale-allow-stale"), false);
    assert.equal(requests[2]!.headers.has("x-terrascale-allow-stale"), false);
  });

  test("preserves typed document conflicts and public degraded routing metadata", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async input => {
        requests.push(input instanceof Request ? input : new Request(input));
        return Response.json(
          {
            code: "document_change_conflict",
            message: "The document changed before this write.",
            target: "document",
          },
          {
            status: 409,
            headers: {
              "x-request-id": "request-123",
              "x-terrascale-edge-region": "FRA",
              "x-terrascale-serving-region": "SFO",
              "x-terrascale-route-mode": "preferred-failover",
              "x-terrascale-failover-from-region": "FRA",
              "x-terrascale-operator-node": "operator.internal",
            },
          },
        );
      },
    });

    const result = await client.documents.put(
      { repositoryId },
      documentId,
      { name: "Ada" },
      { expectedChangeToken: "stale-change-token" },
    );

    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.equal(result.status, 409);
      assert.equal(result.error.kind, "conflict");
      assert.equal(result.error.code, "document_change_conflict");
      assert.equal(result.error.target, "document");
      assert.equal(result.error.requestId, "request-123");
      assert.deepEqual(result.routing, {
        edgeRegion: "fra",
        servingRegion: "sfo",
        routeMode: "preferred-failover",
        failoverFromRegion: "fra",
        degraded: true,
      });
      assert.equal("operatorNode" in result.routing!, false);
    }
    assert.equal(requests[0]!.headers.get("if-match"), "stale-change-token");
  });

  test("forwards conditional tokens for patch and delete conflicts", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async input => {
        requests.push(input instanceof Request ? input : new Request(input));
        return Response.json(
          {
            code: "document_change_conflict",
            message: "The document changed before this mutation.",
            target: "document",
          },
          { status: 409 },
        );
      },
    });

    const patchResult = await client.documents.patch(
      { repositoryId },
      documentId,
      { name: "Ada" },
      { expectedChangeToken: "patch-change-token" },
    );
    const deleteResult = await client.documents.delete(
      { repositoryId },
      documentId,
      { expectedChangeToken: "delete-change-token" },
    );

    for (const result of [patchResult, deleteResult]) {
      assert.equal(result.ok, false);
      if (!result.ok) {
        assert.equal(result.status, 409);
        assert.equal(result.error.kind, "conflict");
        assert.equal(result.error.code, "document_change_conflict");
        assert.equal(result.error.target, "document");
      }
    }
    assert.equal(requests[0]!.method, "PATCH");
    assert.equal(requests[0]!.headers.get("if-match"), "patch-change-token");
    assert.equal(requests[1]!.method, "DELETE");
    assert.equal(requests[1]!.headers.get("if-match"), "delete-change-token");
  });

  test("keeps strict-affinity retries on the selected regional host after 503 or transport failure", async () => {
    for (const failure of ["503", "transport"] as const) {
      const requests: Request[] = [];
      const client = createTerraScaleClient({
        apiBaseUrl: "https://api.terrascale.tech",
        preferredRegion: "SFO",
        regionAffinity: "strict",
        maxRetryAttempts: 1,
        retryDelayMs: 0,
        fetch: async input => {
          requests.push(input instanceof Request ? input : new Request(input));
          if (failure === "transport") {
            throw new Error("regional connection failed");
          }
          return Response.json(
            { code: "regional_failure", message: "The regional service is unavailable." },
            { status: 503 },
          );
        },
      });

      const result = await client.documents.get({ repositoryId }, documentId);

      assert.equal(result.ok, false);
      if (!result.ok) {
        assert.equal(result.error.kind, failure === "transport" ? "transport" : "server");
      }
      assert.equal(requests.length, 2);
      assert.deepEqual(
        requests.map(request => new URL(request.url).hostname),
        ["sfo.api.terrascale.tech", "sfo.api.terrascale.tech"],
      );
      assert.equal(requests.some(request => new URL(request.url).hostname === "api.terrascale.tech"), false);
    }
  });

  test("exposes only normalized public serving metadata on successful reads", async () => {
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async () =>
        Response.json(
          { repositoryId, repository: "users", id: documentId, data: { name: "Ada" } },
          {
            headers: {
              "x-terrascale-edge-region": "GRU",
              "x-terrascale-serving-region": "GRU",
              "x-terrascale-route-mode": "regional-strict",
              "x-terrascale-operator-host": "node-17.internal",
            },
          },
        ),
    });

    const result = await client.documents.get({ repositoryId }, documentId);

    assert.equal(result.ok, true);
    if (result.ok) {
      assert.deepEqual(result.routing, {
        edgeRegion: "gru",
        servingRegion: "gru",
        routeMode: "regional-strict",
        degraded: false,
      });
      assert.equal("operatorHost" in result.routing!, false);
    }
  });
});
