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
      { region: "FRA", url: "https://fra.api.terrascale.tech", priority: 1, capabilities: ["read", "write", "sql", "reactive"] },
      { region: "GRU", url: "https://gru.api.terrascale.tech", priority: 2, capabilities: ["read", "write", "sql", "reactive"] },
    ],
    ...overrides,
  };
}

type ScriptedResponse = (request: Request) => Response | Promise<Response>;

/**
 * A fetch fake that records every request and answers discovery separately.
 * Each test uses a distinct database name so the process-wide routing cache
 * stays hermetic across tests.
 */
function scriptedFetch(database: string, options: {
  discovery?: (request: Request) => Response;
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
}): TerraScaleHttp {
  return new TerraScaleHttp({
    fetch: options.fetch,
    ...(options.accessToken === undefined ? {} : { accessToken: options.accessToken }),
    ...(options.projectId === undefined ? {} : { projectId: options.projectId as never }),
    ...(options.maxRetryAttempts === undefined ? {} : { maxRetryAttempts: options.maxRetryAttempts }),
    ...(options.retryDelayMs === undefined ? {} : { retryDelayMs: options.retryDelayMs }),
  });
}

function documentPath(database: string): string {
  return `api/v1/databases/${database}/collections/users/documents/ada`;
}

describe("discovery-driven request routing (ADR 0050)", () => {
  test("database-scoped requests discover once, then connect directly", async () => {
    const transport = scriptedFetch("orders-once");
    const client = http({ fetch: transport.fetch });
    const path = documentPath("orders-once");

    const first = await client.request<unknown>({ method: "GET", path, routeName: "documents.get" });
    assert.equal(first.ok, true);
    const second = await client.request<unknown>({ method: "GET", path, routeName: "documents.get" });
    assert.equal(second.ok, true);

    const urls = transport.requests.map((request) => new URL(request.url).origin + new URL(request.url).pathname);
    assert.deepEqual(urls, [
      "https://api.terrascale.tech/api/v1/discovery",
      `https://fra.api.terrascale.tech/${path}`,
      `https://fra.api.terrascale.tech/${path}`,
    ]);
  });

  test("database-less requests keep the global origin without discovery", async () => {
    const transport = scriptedFetch("orders-list");
    const client = http({ fetch: transport.fetch });
    const result = await client.request<unknown>({ method: "GET", path: "api/v1/databases", routeName: "databases.list" });
    assert.equal(result.ok, true);
    assert.equal(new URL(transport.requests[0]!.url).origin, "https://api.terrascale.tech");
    assert.equal(transport.requests.length, 1);
  });

  test("discovery failure falls back to the global proxy path", async () => {
    const transport = scriptedFetch("orders-fallback", {
      discovery: () => new Response("unavailable", { status: 503 }),
    });
    const client = http({ fetch: transport.fetch });
    const result = await client.request<unknown>({ method: "GET", path: documentPath("orders-fallback"), routeName: "documents.get" });
    assert.equal(result.ok, true);
    assert.equal(new URL(transport.requests[1]!.url).origin, "https://api.terrascale.tech");
  });

  test("writes skip a draining endpoint", async () => {
    const transport = scriptedFetch("orders-drain", {
      discovery: () => Response.json(discoveryPayload("orders-drain", {
        endpoints: [
          { region: "FRA", url: "https://fra.api.terrascale.tech", priority: 1, capabilities: ["read", "sql", "reactive"] },
          { region: "GRU", url: "https://gru.api.terrascale.tech", priority: 2, capabilities: ["read", "write", "sql", "reactive"] },
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
    assert.equal(new URL(transport.requests[1]!.url).origin, "https://gru.api.terrascale.tech");
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
    assert.equal(new URL(transport.requests[1]!.url).origin, "https://fra.api.terrascale.tech");
    assert.equal(new URL(transport.requests[2]!.url).origin, "https://sfo.api.terrascale.tech");

    // The placement header now routes follow-ups directly to SFO.
    await client.request<unknown>({ method: "GET", path, routeName: "documents.get" });
    assert.equal(new URL(transport.requests[3]!.url).origin, "https://sfo.api.terrascale.tech");
    assert.equal(transport.requests.filter((request) => request.url.includes("/api/v1/discovery")).length, 1);
  });

  test("a retryable regional failure fails over to the next endpoint", async () => {
    let gruCalls = 0;
    const transport = scriptedFetch("orders-failover", {
      data: (request) => {
        const origin = new URL(request.url).origin;
        if (origin === "https://fra.api.terrascale.tech") {
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
      "https://api.terrascale.tech",
      "https://fra.api.terrascale.tech",
      "https://gru.api.terrascale.tech",
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
});
