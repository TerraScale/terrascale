import assert from "node:assert/strict";
import { describe, test } from "node:test";

import { createTerraScaleClient, resolveRuntimeConfig } from "./index.js";
import { toUlid } from "./ids.js";

const projectId = toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAV");
const databaseId = toUlid("01BX5ZZKBKACTAV9WEVGEMMVRZ");

describe("regional routing consistency", () => {
  test("rejects invalid or incomplete strict regional configuration", () => {
    for (const preferredRegion of ["gru", "SJC", " GRU", "GRU ", "GR", "GRUU", "京", "JFK"]) {
      assert.throws(
        () => resolveRuntimeConfig({ preferredRegion: preferredRegion as never, fetch: globalThis.fetch }),
        /preferredRegion must be exactly one of GRU, SFO, or FRA/u,
      );
    }
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
    assert.equal(failover.preferredRegion, "FRA");

    const strict = resolveRuntimeConfig({
      preferredRegion: "SFO",
      regionAffinity: "strict",
      fetch: globalThis.fetch,
    });
    assert.equal(strict.apiBaseUrl, "https://sfo.api.terrascale.tech");
    assert.equal(strict.preferredRegion, "SFO");
  });

  test("keeps retry policy in the resolved runtime boundary", () => {
    const defaults = resolveRuntimeConfig({ fetch: globalThis.fetch });
    assert.equal(defaults.maxRetryAttempts, 0);
    assert.equal(defaults.retryDelayMs, 250);

    const configured = resolveRuntimeConfig({
      maxRetryAttempts: 2,
      retryDelayMs: 0,
      fetch: globalThis.fetch,
    });
    assert.equal(configured.maxRetryAttempts, 2);
    assert.equal(configured.retryDelayMs, 0);
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
    await failover.databases.get(databaseId, projectId);

    const strict = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.tech",
      preferredRegion: "SFO",
      regionAffinity: "strict",
      fetch,
    });
    await strict.databases.get(databaseId, projectId);

    assert.equal(new URL(requests[0]!.url).hostname, "api.terrascale.tech");
    assert.equal(requests[0]!.headers.get("x-terrascale-preferred-region"), "FRA");
    assert.equal(new URL(requests[1]!.url).hostname, "sfo.api.terrascale.tech");
    assert.equal(requests[1]!.headers.has("x-terrascale-preferred-region"), false);
  });

  test("preserves typed conflicts and public degraded routing metadata", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async input => {
        requests.push(input instanceof Request ? input : new Request(input));
        return Response.json(
          {
            code: "database_change_conflict",
            message: "The database changed before this request.",
            target: "database",
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

    const result = await client.databases.get(databaseId, projectId);

    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.equal(result.status, 409);
      assert.equal(result.error.kind, "conflict");
      assert.equal(result.error.code, "database_change_conflict");
      assert.equal(result.error.target, "database");
      assert.equal(result.error.requestId, "request-123");
      assert.deepEqual(result.routing, {
        edgeRegion: "FRA",
        servingRegion: "SFO",
        routeMode: "preferred-failover",
        failoverFromRegion: "FRA",
        degraded: true,
      });
      assert.equal("operatorNode" in result.routing!, false);
    }
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

      const result = await client.backups.executeBackup(
        databaseId,
        { operationSessionId: "backup-operation-session", confirmationToken: "backup-confirmation-token" },
        "backup-execution-key",
        projectId,
      );

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

  test("exposes only exact public serving metadata on successful reads", async () => {
    const client = createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async () =>
        Response.json(
          { databaseId, projectId, name: "primary" },
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

    const result = await client.databases.get(databaseId, projectId);

    assert.equal(result.ok, true);
    if (result.ok) {
      assert.deepEqual(result.routing, {
        edgeRegion: "GRU",
        servingRegion: "GRU",
        routeMode: "regional-strict",
        degraded: false,
      });
      assert.equal("operatorHost" in result.routing!, false);
    }
  });

  test("does not normalize invalid routing RegionCode headers", async () => {
    const client = createTerraScaleClient({
      fetch: async () => Response.json(
        { databaseId, projectId, name: "primary" },
        {
          headers: {
            "x-terrascale-edge-region": "gru",
            "x-terrascale-serving-region": "S FO",
            "x-terrascale-failover-from-region": "SJC",
            "x-terrascale-route-mode": "preferred-failover",
          },
        },
      ),
    });

    const result = await client.databases.get(databaseId, projectId);

    assert.equal(result.ok, true);
    if (result.ok) {
      assert.deepEqual(result.routing, {
        routeMode: "preferred-failover",
        degraded: true,
      });
    }
  });
});
