import assert from "node:assert/strict";
import { describe, test } from "vitest";

import { TerraScaleHttp } from "./http.js";

function discoveryPayload(database: string, overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    v: 1,
    database,
    branch: "main",
    revision: 11,
    updated: 1_725_300_000,
    residency: null,
    placement: "known",
    refreshSeconds: 300,
    endpoints: [
      { region: "FRA", url: "https://fra.terrascale.tech", priority: 1, capabilities: ["read", "write", "sql", "reactive"] },
      { region: "GRU", url: "https://gru.terrascale.tech", priority: 2, capabilities: ["read", "write", "sql", "reactive"] },
    ],
    ...overrides,
  };
}

type ScriptedResponse = (request: Request) => Response | Promise<Response>;

function catalogPayload(overrides: {
  regions?: Record<string, unknown>[];
  hosts?: Record<string, string>;
} = {}): Record<string, unknown> {
  return {
    regions: overrides.regions ?? [
      { code: "FRA", name: "Frankfurt", country: "DE", latitude: 50.1109, longitude: 8.6821, available: true },
      { code: "GRU", name: "Sao Paulo", country: "BR", latitude: -23.5475, longitude: -46.6361, available: true },
    ],
    hosts: overrides.hosts ?? {
      FRA: "fra.terrascale.tech",
      GRU: "gru.terrascale.tech",
    },
  };
}

/**
 * A fetch fake that records every request and answers discovery and the
 * region catalog separately. Each test uses a distinct database name (and,
 * for catalog-dependent tests, a distinct `routing.baseUrl`) so the
 * process-wide routing and catalog caches stay hermetic across tests.
 */
function scriptedFetch(database: string, options: {
  discovery?: (request: Request) => Response | Promise<Response>;
  catalog?: (request: Request) => Response | Promise<Response>;
  data?: ScriptedResponse;
} = {}): { fetch: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>; requests: Request[] } {
  const requests: Request[] = [];
  return {
    requests,
    fetch: async (input) => {
      const request = new Request(input);
      requests.push(request.clone());
      const url = new URL(request.url);
      if (url.pathname === "/api/v1/discovery") {
        return options.discovery?.(request) ?? Response.json(discoveryPayload(database));
      }
      if (url.pathname === "/api/v1/regions") {
        return options.catalog?.(request) ?? Response.json(catalogPayload());
      }
      return options.data?.(request) ?? Response.json({ ok: true });
    },
  };
}

function http(options: {
  fetch: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;
  accessToken?: string | (() => Promise<string | undefined>);
  projectId?: string;
  maxRetryAttempts?: number;
  retryDelayMs?: number;
  routingBaseUrl?: string;
}): TerraScaleHttp {
  return new TerraScaleHttp({
    fetch: options.fetch,
    ...(options.accessToken === undefined ? {} : { accessToken: options.accessToken }),
    ...(options.projectId === undefined ? {} : { projectId: options.projectId as never }),
    ...(options.maxRetryAttempts === undefined ? {} : { maxRetryAttempts: options.maxRetryAttempts }),
    ...(options.retryDelayMs === undefined ? {} : { retryDelayMs: options.retryDelayMs }),
    ...(options.routingBaseUrl === undefined ? {} : { routing: { baseUrl: options.routingBaseUrl } }),
  });
}

function documentPath(database: string): string {
  return `api/v1/databases/${database}/collections/users/documents/ada`;
}

describe("discovery-driven request routing (ADR 0050)", () => {
  test("database-scoped requests discover once at atlas, then connect directly", async () => {
    const transport = scriptedFetch("orders-once");
    const client = http({ fetch: transport.fetch });
    const path = documentPath("orders-once");

    const first = await client.request<unknown>({ method: "GET", path, routeName: "documents.get" });
    assert.equal(first.ok, true);
    const second = await client.request<unknown>({ method: "GET", path, routeName: "documents.get" });
    assert.equal(second.ok, true);

    const urls = transport.requests.map((request) => new URL(request.url).origin + new URL(request.url).pathname);
    assert.deepEqual(urls, [
      "https://atlas.terrascale.tech/api/v1/discovery",
      `https://fra.terrascale.tech/${path}`,
      `https://fra.terrascale.tech/${path}`,
    ]);
  });

  test("database-less member routes select a regional endpoint from the catalog", async () => {
    const transport = scriptedFetch("orders-list");
    const client = http({ fetch: transport.fetch, routingBaseUrl: "https://atlas-member.test" });
    const result = await client.request<unknown>({ method: "GET", path: "api/v1/databases", routeName: "databases.list" });
    assert.equal(result.ok, true);
    const urls = transport.requests.map((request) => new URL(request.url).origin + new URL(request.url).pathname);
    assert.deepEqual(urls, [
      "https://atlas-member.test/api/v1/regions",
      "https://fra.terrascale.tech/api/v1/databases",
    ]);
  });

  test("discovery failure degrades to catalog selection, never to a proxy", async () => {
    const transport = scriptedFetch("orders-fallback", {
      discovery: () => new Response("unavailable", { status: 503 }),
    });
    const client = http({ fetch: transport.fetch, routingBaseUrl: "https://atlas-fallback.test" });
    const result = await client.request<unknown>({ method: "GET", path: documentPath("orders-fallback"), routeName: "documents.get" });
    assert.equal(result.ok, true);
    const urls = transport.requests.map((request) => new URL(request.url).origin + new URL(request.url).pathname);
    assert.deepEqual(urls, [
      "https://atlas-fallback.test/api/v1/discovery",
      "https://atlas-fallback.test/api/v1/regions",
      `https://fra.terrascale.tech/${documentPath("orders-fallback")}`,
    ]);
    // The retired per-request proxy: no data request ever targets the product origin.
    assert.equal(urls.some((url) => url.startsWith("https://api.terrascale.tech/")), false);
  });

  test("writes skip a draining endpoint", async () => {
    const transport = scriptedFetch("orders-drain", {
      discovery: () => Response.json(discoveryPayload("orders-drain", {
        endpoints: [
          { region: "FRA", url: "https://fra.terrascale.tech", priority: 1, capabilities: ["read", "sql", "reactive"] },
          { region: "GRU", url: "https://gru.terrascale.tech", priority: 2, capabilities: ["read", "write", "sql", "reactive"] },
        ],
      })),
    });
    const client = http({ fetch: transport.fetch, accessToken: "secret" });
    const result = await client.request<unknown>({
      method: "PUT",
      path: documentPath("orders-drain"),
      routeName: "documents.put",
      body: { name: "Ada" },
      idempotencyKey: "write-1",
    });
    assert.equal(result.ok, true);
    assert.equal(new URL(transport.requests[1]!.url).origin, "https://gru.terrascale.tech");
  });

  test("a member 421 adopts placement and repairs once", async () => {
    let dataCalls = 0;
    const transport = scriptedFetch("orders-421", {
      data: () => {
        dataCalls++;
        if (dataCalls === 1) {
          return new Response(JSON.stringify({ error: "misdirected", message: "not hosted in FRA" }), {
            status: 421,
            headers: { "x-terrascale-placement": "SFO,GRU", "content-type": "application/json" },
          });
        }
        return Response.json({ ok: true });
      },
    });
    const client = http({ fetch: transport.fetch });
    const path = documentPath("orders-421");
    const result = await client.request<unknown>({ method: "GET", path, routeName: "documents.get" });
    assert.equal(result.ok, true);

    // Discovery, misdirected regional attempt, repaired replay on SFO.
    assert.equal(new URL(transport.requests[0]!.url).pathname, "/api/v1/discovery");
    assert.equal(new URL(transport.requests[1]!.url).origin, "https://fra.terrascale.tech");
    assert.equal(new URL(transport.requests[2]!.url).origin, "https://sfo.terrascale.tech");

    // The placement header now routes follow-ups directly to SFO.
    await client.request<unknown>({ method: "GET", path, routeName: "documents.get" });
    assert.equal(new URL(transport.requests[3]!.url).origin, "https://sfo.terrascale.tech");
    assert.equal(transport.requests.filter((request) => request.url.includes("/api/v1/discovery")).length, 1);
  });

  test("a retryable regional failure fails over to the next endpoint", async () => {
    let gruCalls = 0;
    const transport = scriptedFetch("orders-failover", {
      data: (request) => {
        const origin = new URL(request.url).origin;
        if (origin === "https://fra.terrascale.tech") {
          return Response.json({ error: { code: "database_unavailable", message: "down" } }, { status: 503 });
        }
        gruCalls++;
        return Response.json({ ok: true });
      },
    });
    const client = http({ fetch: transport.fetch, maxRetryAttempts: 1, retryDelayMs: 0 });
    const result = await client.request<unknown>({ method: "GET", path: documentPath("orders-failover"), routeName: "documents.get" });
    assert.equal(result.ok, true);
    assert.equal(gruCalls, 1);
    const origins = transport.requests.map((request) => new URL(request.url).origin);
    assert.deepEqual(origins, [
      "https://atlas.terrascale.tech",
      "https://fra.terrascale.tech",
      "https://gru.terrascale.tech",
    ]);
  });

  test("an expired token refreshes exactly once and replays", async () => {
    // Resolution sequence: expired on the first attempt, refreshed after the
    // 401, then stuck on a second dead token with nothing newer to offer.
    const resolutions = ["expired-token", "fresh-token", "fresh-token", "still-expired"];
    let resolutionsServed = 0;
    let dataCalls = 0;
    const transport = scriptedFetch("orders-auth", {
      data: (request) => {
        dataCalls++;
        if (request.headers.get("authorization") === "Bearer fresh-token") {
          return Response.json({ ok: true });
        }
        return Response.json({ error: { code: "unauthenticated", message: "expired" } }, { status: 401 });
      },
    });
    const client = http({
      fetch: transport.fetch,
      accessToken: () => Promise.resolve(resolutions[Math.min(resolutionsServed++, resolutions.length - 1)]),
    });
    const first = await client.request<unknown>({ method: "GET", path: documentPath("orders-auth"), routeName: "documents.get" });
    assert.equal(first.ok, true);
    assert.equal(dataCalls, 2);

    // A provider that keeps returning the same dead token surfaces the 401.
    const second = await client.request<unknown>({ method: "GET", path: documentPath("orders-auth"), routeName: "documents.get" });
    assert.equal(second.ok, false);
    assert.equal(dataCalls, 3);
  });

  test("a 401 replays a rotated token even when a concurrent request already resolved it", async () => {
    // Resolution order: this request's attempt (t1), a concurrent request
    // (t2), then the 401 re-resolution (t2 again). The replay must compare
    // against the token the failing attempt actually sent, not a shared
    // latest value that the concurrent request already moved to t2.
    const tokens = ["t1", "t2", "t2"];
    let served = 0;
    let dataCalls = 0;
    let releaseData!: (response: Response) => void;
    const transport = scriptedFetch("orders-rotate", {
      data: (request) => {
        dataCalls++;
        if (request.headers.get("authorization") === "Bearer t2") return Response.json({ ok: true });
        if (dataCalls === 1) return new Promise<Response>(resolve => { releaseData = resolve; });
        return Response.json({ error: { code: "unauthenticated", message: "expired" } }, { status: 401 });
      },
    });
    const client = http({
      fetch: transport.fetch,
      accessToken: () => Promise.resolve(tokens[Math.min(served++, tokens.length - 1)]),
    });
    const pending = client.request<unknown>({ method: "GET", path: documentPath("orders-rotate"), routeName: "documents.get" });
    await new Promise<void>(resolve => {
      const check = () => dataCalls >= 1 ? resolve() : setTimeout(check, 0);
      check();
    });
    // A concurrent request resolves the provider to the rotated token. The
    // product route skips discovery and the catalog entirely.
    const concurrent = await client.request<unknown>({ method: "GET", path: "api/v1/organizations", routeName: "organizations.list" });
    assert.equal(concurrent.ok, true);
    // Only now does the first attempt's 401 arrive.
    releaseData(Response.json({ error: { code: "unauthenticated", message: "expired" } }, { status: 401 }));
    const result = await pending;
    assert.equal(result.ok, true);
    assert.equal(dataCalls, 3);
    assert.equal(transport.requests[transport.requests.length - 1]!.headers.get("authorization"), "Bearer t2");
  });

  test("caller cancellation does not abort or poison the shared discovery request", async () => {
    const discoverySignals: AbortSignal[] = [];
    let releaseDiscovery!: (response: Response) => void;
    const transport = scriptedFetch("orders-cancel", {
      discovery: (request) => {
        discoverySignals.push(request.signal);
        return new Promise<Response>(resolve => { releaseDiscovery = resolve; });
      },
    });
    const client = http({ fetch: transport.fetch });
    const controller = new AbortController();
    const pending = client.request<unknown>({
      method: "GET", path: documentPath("orders-cancel"), routeName: "documents.get", signal: controller.signal,
    });
    await new Promise<void>(resolve => {
      const check = () => discoverySignals.length >= 1 ? resolve() : setTimeout(check, 0);
      check();
    });
    controller.abort(new Error("caller cancelled"));
    // One caller's cancellation never aborted the shared discovery fetch.
    assert.equal(discoverySignals[0]!.aborted, false);
    releaseDiscovery(Response.json(discoveryPayload("orders-cancel")));
    const result = await pending;
    assert.equal(result.ok, false);
    // The completed discovery served the next request directly: no failure
    // backoff was armed, so no proxy fallback and no re-discovery.
    const next = await client.request<unknown>({ method: "GET", path: documentPath("orders-cancel"), routeName: "documents.get" });
    assert.equal(next.ok, true);
    assert.equal(transport.requests.filter((request) => request.url.includes("/api/v1/discovery")).length, 1);
    assert.equal(new URL(transport.requests[transport.requests.length - 1]!.url).origin, "https://fra.terrascale.tech");
  });

  test("an ambiguous write timeout on an unkeyed mutation is never replayed", async () => {
    let dataCalls = 0;
    const transport = scriptedFetch("orders-ambiguous", {
      data: () => {
        dataCalls++;
        return new Promise((_resolve, reject) => reject(new TypeError("connection reset mid-flight")));
      },
    });
    const client = http({ fetch: transport.fetch, maxRetryAttempts: 3, retryDelayMs: 0 });
    const result = await client.request<unknown>({
      method: "POST",
      path: "api/v1/databases/orders-ambiguous/collections/users/documents",
      routeName: "documents.create",
      body: { name: "Ada" },
    });
    assert.equal(result.ok, false);
    assert.equal(dataCalls, 1);
  });

  test("routing caches are isolated per authorization scope", async () => {
    const discoveryDatabases: string[] = [];
    const transport = scriptedFetch("orders-scoped", {
      discovery: (request) => {
        discoveryDatabases.push(new URL(request.url).searchParams.get("database") ?? "");
        return Response.json(discoveryPayload("orders-scoped"));
      },
    });
    const projectA = http({ fetch: transport.fetch, projectId: "01ARZ3NDEKTSV4RRFFQ69G5FAV" });
    const projectB = http({ fetch: transport.fetch, projectId: "01BX5ZZKBKACTAV9WEVGEMMVRZ" });
    await projectA.request<unknown>({ method: "GET", path: documentPath("orders-scoped"), routeName: "documents.get" });
    await projectB.request<unknown>({ method: "GET", path: documentPath("orders-scoped"), routeName: "documents.get" });
    // Two scopes, two discovery lookups; neither served the other.
    assert.equal(discoveryDatabases.length, 2);
  });

  test("product routes target the product origin without discovery or catalog", async () => {
    for (const path of [
      "api/v1/organizations",
      "api/v1/organizations/01ARZ3NDEKTSV4RRFFQ69G5FAV/projects",
      "api/v1/projects",
      "api/v1/regions/FRA",
    ]) {
      const transport = scriptedFetch("orders-product");
      const client = http({ fetch: transport.fetch });
      const result = await client.request<unknown>({ method: "GET", path, routeName: "organizations.list" });
      assert.equal(result.ok, true);
      assert.deepEqual(transport.requests.map((request) => new URL(request.url).origin), ["https://api.terrascale.tech"]);
    }
  });

  test("the region catalog form itself targets the discovery origin", async () => {
    const transport = scriptedFetch("orders-catalog-route");
    const client = http({ fetch: transport.fetch });
    const result = await client.request<unknown>({ method: "GET", path: "api/v1/regions", routeName: "regions.list" });
    assert.equal(result.ok, true);
    assert.deepEqual(transport.requests.map((request) => new URL(request.url).origin), ["https://atlas.terrascale.tech"]);
    assert.equal(new URL(transport.requests[0]!.url).pathname, "/api/v1/regions");
  });

  test("the catalog is fetched once per five minutes across requests", async () => {
    const catalogCalls: string[] = [];
    const transport = scriptedFetch("orders-catalog-reuse", {
      catalog: (request) => {
        catalogCalls.push(new URL(request.url).origin);
        return Response.json(catalogPayload());
      },
    });
    const client = http({ fetch: transport.fetch, routingBaseUrl: "https://atlas-reuse.test" });
    for (let i = 0; i < 3; i++) {
      const result = await client.request<unknown>({ method: "GET", path: "api/v1/databases", routeName: "databases.list" });
      assert.equal(result.ok, true);
    }
    assert.deepEqual(catalogCalls, ["https://atlas-reuse.test"]);
    assert.equal(transport.requests.filter((request) => new URL(request.url).pathname === "/api/v1/regions").length, 1);
    assert.equal(
      transport.requests.filter((request) => new URL(request.url).origin === "https://fra.terrascale.tech").length,
      3,
    );
  });

  test("catalog hosts are validated: an evil host is never contacted", async () => {
    const transport = scriptedFetch("orders-evil-host", {
      catalog: () => Response.json(catalogPayload({ hosts: { FRA: "https://evil.example.com", GRU: "https://gru.terrascale.tech" } })),
    });
    const client = http({ fetch: transport.fetch, routingBaseUrl: "https://atlas-evil.test" });
    const result = await client.request<unknown>({ method: "GET", path: "api/v1/databases", routeName: "databases.list" });
    // The rejected catalog leaves the configured origin as the last resort.
    assert.equal(result.ok, true);
    assert.deepEqual(transport.requests.map((request) => new URL(request.url).origin), [
      "https://atlas-evil.test",
      "https://api.terrascale.tech",
    ]);
    assert.equal(transport.requests.some((request) => new URL(request.url).hostname === "evil.example.com"), false);
  });
});
