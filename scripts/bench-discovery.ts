/**
 * Local harness comparing ADR 0050 discovery + direct regional connections
 * against the ADR 0043 global-proxy first-contact path (ADR 0050 validation:
 * cold first-use, warm p50/p95/p99, failover time, discovery calls/session).
 *
 * Everything runs on loopback with plain node:http servers, so the numbers
 * measure the *protocol overhead delta* (extra hop, discovery request,
 * failure handling), not production WAN latency. Direct routing is not
 * claimed to be always faster; see the printed caveats.
 *
 * Usage: node scripts/bench-discovery.ts
 */
import http from "node:http";
import { performance } from "node:perf_hooks";
import { TerraScaleHttp } from "../dist/http.js";

const REGION_PORTS = { FRA: 18_091, GRU: 18_092 } as const;
const EDGE_PORT = 18_090;
const DATABASE = "bench-db";

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
 * The "edge" server plays both roles on one port:
 * - GET /api/v1/discovery answers the ADR 0050 routing document (dev origins).
 * - Any other /api/v1 request is PROXIED to the first healthy region,
 *   modelling the ADR 0043 per-request global proxy (a real second hop).
 */
function edgeServer(fra: http.Server, gru: http.Server): Promise<{ server: http.Server; discoveryHits: () => number; proxyHits: () => number }> {
  let discoveryHits = 0;
  let proxyHits = 0;
  return new Promise((resolve) => {
    const server = http.createServer((request, response) => {
      const url = new URL(request.url ?? "/", `http://127.0.0.1:${EDGE_PORT}`);
      if (url.pathname === "/api/v1/discovery") {
        discoveryHits++;
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
      proxyHits++;
      const origin = `http://127.0.0.1:${REGION_PORTS.FRA}`;
      const proxied = http.request(`${origin}${request.url}`, { method: request.method }, (upstream) => {
        response.writeHead(upstream.statusCode ?? 502, upstream.headers);
        upstream.pipe(response);
      });
      proxied.on("error", () => json(response, 502, { error: { code: "bad_gateway", message: "origin unreachable" } }));
      request.pipe(proxied);
    });
    server.listen(EDGE_PORT, "127.0.0.1", () => resolve({ server, discoveryHits: () => discoveryHits, proxyHits: () => proxyHits }));
    void fra;
    void gru;
  });
}

/**
 * A passthrough transport: each request's own URL decides the hop — global
 * and discovery requests reach the edge origin, direct regional requests
 * reach the regional origins the routing document named.
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

async function timedRead(client: TerraScaleHttp): Promise<number> {
  const started = performance.now();
  const result = await client.request<unknown>({
    method: "GET",
    path: `api/v1/databases/${DATABASE}/collections/users/documents/ada`,
    routeName: "documents.get",
  });
  const elapsed = performance.now() - started;
  if (!result.ok) throw new Error(`bench read failed: ${String(result)}`);
  return elapsed;
}

async function main(): Promise<void> {
  const fraServer = await regionServer(REGION_PORTS.FRA, "FRA");
  const gruServer = await regionServer(REGION_PORTS.GRU, "GRU");
  const edge = await edgeServer(fraServer, gruServer);
  const path = `api/v1/databases/${DATABASE}/collections/users/documents/ada`;

  try {
    // --- Cold first use: discovery + first direct request vs proxy path.
    const coldDirect = new TerraScaleHttp({ apiBaseUrl: `http://127.0.0.1:${EDGE_PORT}`, fetch: fetchLike(), routing: { enabled: true } });
    const coldProxy = new TerraScaleHttp({ apiBaseUrl: `http://127.0.0.1:${EDGE_PORT}`, fetch: fetchLike(), routing: { enabled: false } });
    const coldDirectMs = await timedRead(coldDirect);
    const coldProxyMs = await timedRead(coldProxy);

    // --- Warm steady state over 200 requests each.
    const directClient = new TerraScaleHttp({ apiBaseUrl: `http://127.0.0.1:${EDGE_PORT}`, fetch: fetchLike(), routing: { enabled: true } });
    const proxyClient = new TerraScaleHttp({ apiBaseUrl: `http://127.0.0.1:${EDGE_PORT}`, fetch: fetchLike(), routing: { enabled: false } });
    await timedRead(directClient);
    await timedRead(proxyClient);
    const directWarm: number[] = [];
    const proxyWarm: number[] = [];
    for (let i = 0; i < 200; i++) {
      directWarm.push(await timedRead(directClient));
      proxyWarm.push(await timedRead(proxyClient));
    }

    // --- Discovery calls per session: fresh client, 100 requests.
    const before = edge.discoveryHits();
    const session = new TerraScaleHttp({ apiBaseUrl: `http://127.0.0.1:${EDGE_PORT}`, fetch: fetchLike(), routing: { enabled: true } });
    for (let i = 0; i < 100; i++) {
      await session.request<unknown>({
        method: "GET",
        path: `api/v1/databases/${DATABASE}-session/collections/users/documents/ada`,
        routeName: "documents.get",
      });
    }
    const discoveryCalls = edge.discoveryHits() - before;

    // --- Failover: kill FRA; measure time until a read succeeds via GRU.
    await fetch(`http://127.0.0.1:${REGION_PORTS.FRA}/__die`, { method: "POST" });
    const failoverStart = performance.now();
    let failoverMs: number | undefined;
    const failoverClient = new TerraScaleHttp({
      apiBaseUrl: `http://127.0.0.1:${EDGE_PORT}`,
      fetch: fetchLike(),
      routing: { enabled: true },
      maxRetryAttempts: 2,
      retryDelayMs: 5,
    });
    for (let i = 0; i < 10 && failoverMs === undefined; i++) {
      const result = await failoverClient.request<unknown>({ method: "GET", path, routeName: "documents.get" });
      if (result.ok) failoverMs = performance.now() - failoverStart;
    }

    const report = {
      harness: "loopback node:http; measures protocol overhead delta, not WAN latency",
      coldFirstUseMs: { discoveryPlusDirect: round(coldDirectMs), proxyPath: round(coldProxyMs) },
      warmDirectMs: stats(directWarm),
      warmProxyMs: stats(proxyWarm),
      discoveryCallsPer100RequestSession: discoveryCalls,
      failoverToAlternativeMs: failoverMs === undefined ? "did not recover" : round(failoverMs),
      caveats: [
        "loopback hops are ~free compared with production edges; absolute values are not predictions",
        "the proxy path here is one extra local hop; a deployed Worker adds compute and PoP distance",
        "warm 'direct' requests still cross one loopback hop; production adds Cloudflare regional entry",
      ],
    };
    console.log(JSON.stringify(report, null, 2));
  } finally {
    edge.server.close();
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
