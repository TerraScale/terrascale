/**
 * Local harness comparing ADR 0050 discovery + direct regional connections
 * against the catalog-degraded path and the retired proxy flow (ADR 0050
 * validation: cold first-use, warm p50/p95/p99, failover time, discovery
 * calls/session).
 *
 * Everything runs on loopback with plain node:http servers, so the numbers
 * measure the *protocol overhead delta* (extra hop, discovery/catalog
 * request, failure handling), not production WAN latency. The proxy leg is
 * only a locally simulated double hop kept for reference: the real
 * per-request proxy is retired (ADR 0050 §8) and no longer exists to
 * measure. Direct routing is not claimed to be always faster; see the
 * printed caveats.
 *
 * Usage: node scripts/bench-discovery.ts
 */
import http from "node:http";
import { performance } from "node:perf_hooks";
import { TerraScaleHttp } from "../dist/http.js";

const REGION_PORTS = { FRA: 18_091, GRU: 18_092 } as const;
const ATLAS_PORT = 18_090;
const PROXY_PORT = 18_093;
const DATABASE = "bench-db";
// A fresh routing scope for the catalog-degraded leg, so its first request
// actually attempts discovery, fails, and pays the catalog fetch.
const CATALOG_DATABASE = "bench-db-catalog";

function json(response: http.ServerResponse, status: number, body: unknown, headers: Record<string, string> = {}): void {
  const payload = JSON.stringify(body);
  response.writeHead(status, { "content-type": "application/json", "content-length": String(payload.length), ...headers });
  response.end(payload);
}

function regionServer(port: number, region: keyof typeof REGION_PORTS): Promise<http.Server> {
  return new Promise((resolve) => {
    const state = { healthy: true };
    const server = http.createServer((request, response) => {
      if (request.url === "/__die") {
        state.healthy = false;
        return json(response, 200, { dead: true });
      }
      if (!state.healthy) {
        response.writeHead(503, { "content-type": "application/json" });
        return response.end(JSON.stringify({ error: { code: "database_unavailable", message: "region down" } }));
      }
      json(response, 200, { region, ok: true }, { "x-terrascale-served-by": region });
    });
    server.listen(port, "127.0.0.1", () => resolve(server));
  });
}

/**
 * The ts-atlas metadata server (ADR 0050 §8): exactly two routes, no
 * forwarding. `/__discovery-off` and `/__discovery-on` toggle the discovery
 * endpoint so the catalog-degraded leg can be measured.
 */
function atlasServer(): Promise<{ server: http.Server; discoveryHits: () => number; catalogHits: () => number }> {
  let discoveryEnabled = true;
  let discoveryHits = 0;
  let catalogHits = 0;
  return new Promise((resolve) => {
    const server = http.createServer((request, response) => {
      const url = new URL(request.url ?? "/", `http://127.0.0.1:${ATLAS_PORT}`);
      if (url.pathname === "/__discovery-off") {
        discoveryEnabled = false;
        return json(response, 200, { discovery: false });
      }
      if (url.pathname === "/__discovery-on") {
        discoveryEnabled = true;
        return json(response, 200, { discovery: true });
      }
      if (url.pathname === "/api/v1/discovery") {
        discoveryHits++;
        if (!discoveryEnabled) return json(response, 503, { error: { code: "region_catalog_unavailable", message: "off" } });
        return json(response, 200, {
          v: 1,
          database: url.searchParams.get("database") ?? DATABASE,
          branch: "main",
          revision: 3,
          updated: Math.floor(Date.now() / 1000),
          residency: null,
          placement: "known",
          refreshSeconds: 300,
          endpoints: [
            { region: "FRA", url: `http://127.0.0.1:${REGION_PORTS.FRA}`, priority: 1, capabilities: ["read", "write", "sql", "reactive"] },
            { region: "GRU", url: `http://127.0.0.1:${REGION_PORTS.GRU}`, priority: 2, capabilities: ["read", "write", "sql", "reactive"] },
          ],
        });
      }
      if (url.pathname === "/api/v1/regions") {
        catalogHits++;
        return json(response, 200, {
          regions: [
            { code: "FRA", name: "Frankfurt", country: "DE", latitude: 50.1109, longitude: 8.6821, available: true },
            { code: "GRU", name: "Sao Paulo", country: "BR", latitude: -23.5475, longitude: -46.6361, available: true },
          ],
          hosts: { FRA: `http://127.0.0.1:${REGION_PORTS.FRA}`, GRU: `http://127.0.0.1:${REGION_PORTS.GRU}` },
        });
      }
      return json(response, 404, { error: { code: "not_found", message: "atlas serves metadata only" } });
    });
    server.listen(ATLAS_PORT, "127.0.0.1", () => resolve({ server, discoveryHits: () => discoveryHits, catalogHits: () => catalogHits }));
  });
}

/**
 * A local double hop standing in for the retired per-request global proxy
 * (ADR 0043). Kept ONLY as a simulation for reference: the real proxy no
 * longer exists (ADR 0050 §8).
 */
function retiredProxyServer(): Promise<http.Server> {
  return new Promise((resolve) => {
    const server = http.createServer((request, response) => {
      const origin = `http://127.0.0.1:${REGION_PORTS.FRA}`;
      const proxied = http.request(`${origin}${request.url}`, { method: request.method }, (upstream) => {
        response.writeHead(upstream.statusCode ?? 502, upstream.headers);
        upstream.pipe(response);
      });
      proxied.on("error", () => json(response, 502, { error: { code: "bad_gateway", message: "origin unreachable" } }));
      request.pipe(proxied);
    });
    server.listen(PROXY_PORT, "127.0.0.1", () => resolve(server));
  });
}

/**
 * A passthrough transport: each request's own URL decides the hop —
 * discovery/catalog requests reach the atlas origin, direct regional
 * requests reach the regional origins the routing document named.
 */
function fetchLike(): (input: RequestInfo | URL, init?: RequestInit) => Promise<Response> {
  return async (input, init) => {
    const request = new Request(input, init);
    const upstream = await fetch(request.url, {
      method: request.method,
      headers: [...request.headers],
      body: request.method === "GET" || request.method === "HEAD" ? undefined : await request.text(),
    });
    return new Response(upstream.body, { status: upstream.status, headers: upstream.headers });
  };
}

function percentile(samples: number[], fraction: number): number {
  const sorted = [...samples].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.floor(fraction * sorted.length))]!;
}

async function timedRead(client: TerraScaleHttp, database = DATABASE): Promise<number> {
  const started = performance.now();
  const result = await client.request<unknown>({
    method: "GET",
    path: `api/v1/databases/${database}/collections/users/documents/ada`,
    routeName: "documents.get",
  });
  const elapsed = performance.now() - started;
  if (!result.ok) throw new Error(`bench read failed: ${String(result)}`);
  return elapsed;
}

async function warmStats(client: TerraScaleHttp, database = DATABASE, count = 200): Promise<Record<string, number>> {
  await timedRead(client, database);
  const samples: number[] = [];
  for (let i = 0; i < count; i++) samples.push(await timedRead(client, database));
  return stats(samples);
}

function directClient(): TerraScaleHttp {
  return new TerraScaleHttp({
    apiBaseUrl: `http://127.0.0.1:${ATLAS_PORT}`,
    fetch: fetchLike(),
    routing: { baseUrl: `http://127.0.0.1:${ATLAS_PORT}`, enabled: true },
  });
}

async function main(): Promise<void> {
  const fraServer = await regionServer(REGION_PORTS.FRA, "FRA");
  const gruServer = await regionServer(REGION_PORTS.GRU, "GRU");
  const atlas = await atlasServer();
  const proxyServer = await retiredProxyServer();
  const path = `api/v1/databases/${DATABASE}/collections/users/documents/ada`;

  try {
    // --- Cold first use: discovery + direct, and (discovery off) the
    // catalog-degraded first request, which pays the catalog fetch instead.
    const coldDirectMs = await timedRead(directClient());
    await fetch(`http://127.0.0.1:${ATLAS_PORT}/__discovery-off`, { method: "POST" });
    const catalogBefore = atlas.catalogHits();
    const coldCatalogMs = await timedRead(directClient(), CATALOG_DATABASE);
    const catalogFetchesOnCold = atlas.catalogHits() - catalogBefore;

    // --- Warm steady state over 200 requests each.
    await fetch(`http://127.0.0.1:${ATLAS_PORT}/__discovery-on`, { method: "POST" });
    const warmDirect = await warmStats(directClient());
    await fetch(`http://127.0.0.1:${ATLAS_PORT}/__discovery-off`, { method: "POST" });
    const warmCatalogDegraded = await warmStats(directClient(), CATALOG_DATABASE);
    await fetch(`http://127.0.0.1:${ATLAS_PORT}/__discovery-on`, { method: "POST" });

    // --- Retired proxy (simulation, for reference): one extra local hop on
    // every request, which is what the ADR 0043 flow paid forever.
    const proxyClient = () => new TerraScaleHttp({
      apiBaseUrl: `http://127.0.0.1:${PROXY_PORT}`,
      fetch: fetchLike(),
      routing: { enabled: false },
    });
    const coldProxyMs = await timedRead(proxyClient());
    const warmProxy = await warmStats(proxyClient());

    // --- Discovery calls per session: fresh client, 100 requests.
    const before = atlas.discoveryHits();
    const session = directClient();
    for (let i = 0; i < 100; i++) {
      await session.request<unknown>({
        method: "GET",
        path: `api/v1/databases/${DATABASE}-session/collections/users/documents/ada`,
        routeName: "documents.get",
      });
    }
    const discoveryCalls = atlas.discoveryHits() - before;

    // --- Failover: kill FRA; measure time until a read succeeds via GRU.
    await fetch(`http://127.0.0.1:${REGION_PORTS.FRA}/__die`, { method: "POST" });
    const failoverStart = performance.now();
    let failoverMs: number | undefined;
    const failoverClient = new TerraScaleHttp({
      apiBaseUrl: `http://127.0.0.1:${ATLAS_PORT}`,
      fetch: fetchLike(),
      routing: { baseUrl: `http://127.0.0.1:${ATLAS_PORT}`, enabled: true },
      maxRetryAttempts: 2,
      retryDelayMs: 5,
    });
    for (let i = 0; i < 10 && failoverMs === undefined; i++) {
      const result = await failoverClient.request<unknown>({ method: "GET", path, routeName: "documents.get" });
      if (result.ok) failoverMs = performance.now() - failoverStart;
    }

    const report = {
      harness: "loopback node:http; measures protocol overhead delta, not WAN latency",
      coldFirstUseMs: {
        discoveryPlusDirect: round(coldDirectMs),
        catalogPlusDirect: round(coldCatalogMs),
        "retiredProxy (simulation, for reference)": round(coldProxyMs),
      },
      warmDirectMs: warmDirect,
      warmCatalogDegradedMs: warmCatalogDegraded,
      "warmRetiredProxyMs (simulation, for reference)": warmProxy,
      catalogFetchesOnDegradedColdStart: catalogFetchesOnCold,
      discoveryCallsPer100RequestSession: discoveryCalls,
      failoverToAlternativeMs: failoverMs === undefined ? "did not recover" : round(failoverMs),
      caveats: [
        "loopback hops are ~free compared with production edges; absolute values are not predictions",
        "the retired-proxy leg is a locally simulated double hop; the real per-request proxy no longer exists (ADR 0050 §8)",
        "the catalog-degraded leg pays its catalog fetch once per five minutes, then matches warm direct",
        "warm requests still cross one loopback hop; production adds Cloudflare regional entry",
      ],
    };
    console.log(JSON.stringify(report, null, 2));
  } finally {
    atlas.server.close();
    proxyServer.close();
    fraServer.close();
    gruServer.close();
  }
}

function stats(samples: number[]): Record<string, number> {
  return {
    p50: round(percentile(samples, 0.5)),
    p95: round(percentile(samples, 0.95)),
    p99: round(percentile(samples, 0.99)),
  };
}

function round(value: number): number {
  return Math.round(value * 100) / 100;
}

void main();
