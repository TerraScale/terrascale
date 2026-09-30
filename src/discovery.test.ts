import { describe, expect, it } from "vitest";
import {
  discoveryActive,
  fetchRegionCatalog,
  fetchRoutingDocument,
  parseRegionCatalog,
  parseRoutingDatabase,
  parseRoutingDocument,
  RegionRttTracker,
  routingDocumentFromPlacement,
  TerraScaleDiscoveryError,
  TerraScaleDiscoveryRouter,
  TerraScaleRegionCatalogCache,
  TerraScaleRoutingCache,
} from "./discovery.js";

function validDocumentPayload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    v: 1,
    database: "orders",
    branch: "main",
    revision: 7,
    updated: 1_725_300_000,
    residency: null,
    placement: "known",
    refreshSeconds: 300,
    endpoints: [
      { region: "FRA", url: "https://fra.terrascale.tech", priority: 1, capabilities: ["read", "write", "sql", "reactive"] },
      { region: "GRU", url: "https://gru.terrascale.tech", priority: 2, capabilities: ["read", "sql", "reactive"] },
    ],
    ...overrides,
  };
}

describe("parseRoutingDocument", () => {
  it("accepts a well-formed document", () => {
    const doc = parseRoutingDocument(validDocumentPayload(), { database: "orders", branch: "main" });
    expect(doc).not.toBeNull();
    expect(doc?.endpoints[0]?.region).toBe("FRA");
    expect(doc?.endpoints[1]?.capabilities).not.toContain("write");
  });

  it("accepts residency as a label or an explicit region list", () => {
    const labelled = parseRoutingDocument(validDocumentPayload({ residency: "eu-only" }), { database: "orders", branch: "main" });
    expect(labelled?.residency).toBe("eu-only");
    const listed = parseRoutingDocument(validDocumentPayload({ residency: ["FRA", "GRU"] }), { database: "orders", branch: "main" });
    expect(listed?.residency).toEqual(["FRA", "GRU"]);
    expect(parseRoutingDocument(validDocumentPayload({ residency: ["fra"] }), { database: "orders", branch: "main" })).toBeNull();
    expect(parseRoutingDocument(validDocumentPayload({ residency: 7 }), { database: "orders", branch: "main" })).toBeNull();
  });

  it("clamps refreshSeconds into the 60–3600 window", () => {
    const short = parseRoutingDocument(validDocumentPayload({ refreshSeconds: 1 }), { database: "orders", branch: "main" });
    expect(short?.refreshSeconds).toBe(60);
    const long = parseRoutingDocument(validDocumentPayload({ refreshSeconds: 10_000 }), { database: "orders", branch: "main" });
    expect(long?.refreshSeconds).toBe(3_600);
  });

  it("rejects identity mismatch, wrong version, and structural doubts", () => {
    const parse = (payload: unknown): ReturnType<typeof parseRoutingDocument> =>
      parseRoutingDocument(payload, { database: "orders", branch: "main" });
    expect(parse(validDocumentPayload({ database: "other" }))).toBeNull();
    expect(parse(validDocumentPayload({ v: 2 }))).toBeNull();
    expect(parse(validDocumentPayload({ placement: "elsewhere" }))).toBeNull();
    expect(parse(validDocumentPayload({ revision: -1 }))).toBeNull();
    expect(parse(validDocumentPayload({ endpoints: [] }))).toBeNull();
    expect(parse(validDocumentPayload({ endpoints: [{ region: "fra", url: "https://fra.terrascale.tech", priority: 1, capabilities: ["read"] }] }))).toBeNull();
    expect(parse(validDocumentPayload({ endpoints: [
      { region: "FRA", url: "https://fra.terrascale.tech", priority: 1, capabilities: ["read"] },
      { region: "FRA", url: "https://fra.terrascale.tech", priority: 2, capabilities: ["read"] },
    ] }))).toBeNull();
    expect(parse(validDocumentPayload({ endpoints: [{ region: "FRA", url: "https://evil.example.com", priority: 1, capabilities: ["read"] }] }))).toBeNull();
    expect(parse(validDocumentPayload({ endpoints: [{ region: "FRA", url: "https://user:pass@fra.terrascale.tech", priority: 1, capabilities: ["read"] }] }))).toBeNull();
    expect(parse(validDocumentPayload({ endpoints: [{ region: "FRA", url: "https://fra.terrascale.tech", priority: 1, capabilities: ["federation"] }] }))).toBeNull();
    expect(parse(validDocumentPayload({ endpoints: [{ region: "FRA", url: "https://fra.terrascale.tech", priority: 2, capabilities: ["read"] }] }))).toBeNull();
  });

  it("never admits plain-http endpoints for public clients but admits loopback for dev clients", () => {
    const loopback = validDocumentPayload({ endpoints: [
      { region: "GRU", url: "http://127.0.0.1:7101", priority: 1, capabilities: ["read", "write"] },
    ] });
    expect(parseRoutingDocument(loopback, { database: "orders", branch: "main" }, false)).toBeNull();
    expect(parseRoutingDocument(loopback, { database: "orders", branch: "main" }, true)?.endpoints[0]?.url).toBe("http://127.0.0.1:7101");
  });

  it("admits the dev worker's *.localhost regional origins for loopback clients only", () => {
    const dev = validDocumentPayload({ endpoints: [
      { region: "GRU", url: "http://gru.api.localhost:8787", priority: 1, capabilities: ["read", "write"] },
    ] });
    expect(parseRoutingDocument(dev, { database: "orders", branch: "main" }, false)).toBeNull();
    expect(parseRoutingDocument(dev, { database: "orders", branch: "main" }, true)?.endpoints[0]?.url).toBe("http://gru.api.localhost:8787");
    // `.localhost` must be a hostname suffix, not a substring.
    expect(parseRoutingDocument(validDocumentPayload({ endpoints: [
      { region: "GRU", url: "http://localhost.evil.example:8787", priority: 1, capabilities: ["read"] },
    ] }), { database: "orders", branch: "main" }, true)).toBeNull();
  });
});

describe("discoveryActive", () => {
  it("defaults on only for the public global origin", () => {
    expect(discoveryActive(undefined, "https://api.terrascale.tech")).toBe(true);
    expect(discoveryActive(undefined, "https://dash.terrascale.tech")).toBe(false);
    expect(discoveryActive(undefined, "http://127.0.0.1:7000")).toBe(false);
    expect(discoveryActive(undefined, "http://127.0.0.1")).toBe(false);
  });

  it("honours explicit opt-in and opt-out", () => {
    expect(discoveryActive({ enabled: false }, "https://api.terrascale.tech")).toBe(false);
    expect(discoveryActive({ enabled: true }, "http://127.0.0.1:7000")).toBe(true);
    expect(discoveryActive({ baseUrl: "http://127.0.0.1:7000" }, "http://127.0.0.1:8900")).toBe(true);
  });
});

describe("parseRoutingDatabase", () => {
  it("follows the fixed path table", () => {
    expect(parseRoutingDatabase("api/v1/databases/orders")).toBe("orders");
    expect(parseRoutingDatabase("api/v1/databases/orders/collections/users/documents/x")).toBe("orders");
    expect(parseRoutingDatabase("api/v1/databases")).toBeNull();
    expect(parseRoutingDatabase("api/v1/reactive/views")).toBeNull();
    expect(parseRoutingDatabase("api/v1/organizations/org")).toBeNull();
    expect(parseRoutingDatabase("api/v1/databases/NotAName/documents")).toBeNull();
  });
});

function catalogPayload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    regions: [
      { code: "FRA", name: "Frankfurt", country: "DE", latitude: 50.1109, longitude: 8.6821, available: true },
      { code: "GRU", name: "Sao Paulo", country: "BR", latitude: -23.5475, longitude: -46.6361, available: false },
    ],
    hosts: {
      FRA: "fra.terrascale.tech",
      GRU: "gru.terrascale.tech",
    },
    ...overrides,
  };
}

describe("parseRegionCatalog", () => {
  it("accepts a well-formed catalog", () => {
    const catalog = parseRegionCatalog(catalogPayload());
    expect(catalog?.regions).toHaveLength(2);
    expect(catalog?.hosts.FRA).toBe("https://fra.terrascale.tech");
  });

  it("rejects unknown regions, duplicates, and structural doubts", () => {
    expect(parseRegionCatalog(catalogPayload({ regions: [
      { code: "fra", name: "Frankfurt", country: "DE", latitude: 1, longitude: 1, available: true },
    ], hosts: { fra: "https://fra.terrascale.tech" } }))).toBeNull();
    expect(parseRegionCatalog(catalogPayload({ regions: [
      { code: "FRA", name: "Frankfurt", country: "DE", latitude: 1, longitude: 1, available: true },
      { code: "FRA", name: "Frankfurt", country: "DE", latitude: 1, longitude: 1, available: false },
    ] }))).toBeNull();
    expect(parseRegionCatalog(catalogPayload({ regions: [] }))).toBeNull();
    expect(parseRegionCatalog(catalogPayload({ regions: [
      { code: "FRA", name: "Frankfurt", country: "DE", latitude: 91, longitude: 1, available: true },
    ] }))).toBeNull();
    expect(parseRegionCatalog(catalogPayload({ regions: [
      { code: "FRA", name: "Frankfurt", country: "DE", latitude: 1, longitude: 1, available: "yes" },
    ] }))).toBeNull();
    expect(parseRegionCatalog(catalogPayload({ hosts: { SFO: "https://sfo.terrascale.tech", FRA: "https://fra.terrascale.tech", GRU: "https://gru.terrascale.tech" } }))).toBeNull();
  });

  it("rejects missing hosts entries and non-trusted hosts", () => {
    expect(parseRegionCatalog(catalogPayload({ hosts: { FRA: "https://fra.terrascale.tech" } }))).toBeNull();
    expect(parseRegionCatalog(catalogPayload({ hosts: { FRA: "https://evil.example.com", GRU: "https://gru.terrascale.tech" } }))).toBeNull();
    expect(parseRegionCatalog(catalogPayload({ hosts: { FRA: "http://fra.terrascale.tech", GRU: "https://gru.terrascale.tech" } }))).toBeNull();
    expect(parseRegionCatalog(catalogPayload({ hosts: { FRA: "fra.api.terrascale.tech", GRU: "gru.terrascale.tech" } }))).toBeNull();
  });

  it("admits loopback hosts only for loopback clients", () => {
    const loopback = catalogPayload({ hosts: { FRA: "http://127.0.0.1:7101", GRU: "http://gru.api.localhost:8787" } });
    expect(parseRegionCatalog(loopback)).toBeNull();
    expect(parseRegionCatalog(loopback, true)?.hosts.FRA).toBe("http://127.0.0.1:7101");
  });
});

describe("TerraScaleRegionCatalogCache", () => {
  it("expires entries after five minutes", () => {
    const cache = new TerraScaleRegionCatalogCache();
    let now = 1_000;
    cache.setClock(() => now);
    const catalog = parseRegionCatalog(catalogPayload())!;
    cache.set("https://atlas.terrascale.tech", catalog);
    expect(cache.get("https://atlas.terrascale.tech")).toBe(catalog);
    now += 301_000;
    expect(cache.get("https://atlas.terrascale.tech")).toBeUndefined();
  });

  it("shares one in-flight catalog fetch per origin", async () => {
    const cache = new TerraScaleRegionCatalogCache();
    let fetches = 0;
    const catalog = parseRegionCatalog(catalogPayload())!;
    const initiate = async (): Promise<typeof catalog> => {
      fetches++;
      await new Promise((resolve) => setTimeout(resolve, 0));
      return catalog;
    };
    const [a, b] = await Promise.all([
      cache.coalesce("https://atlas.terrascale.tech", initiate),
      cache.coalesce("https://atlas.terrascale.tech", initiate),
    ]);
    expect(fetches).toBe(1);
    expect(a).toBe(b);
  });
});

describe("TerraScaleRoutingCache", () => {
  const baseScope = { origin: "https://api.terrascale.tech", database: "orders", branch: "main" };

  function documentFor(database: string): ReturnType<typeof routingDocumentFromPlacement> {
    return routingDocumentFromPlacement({ ...baseScope, database }, ["FRA", "GRU"], 1);
  }

  it("expires entries at the refresh policy", () => {
    const cache = new TerraScaleRoutingCache();
    let now = 1_000;
    cache.setClock(() => now);
    cache.set(baseScope, documentFor("orders")!);
    expect(cache.get(baseScope)).toBeDefined();
    now += 61_000;
    expect(cache.get(baseScope)).toBeUndefined();
  });

  it("isolates scopes by database, project, and tenant", () => {
    const cache = new TerraScaleRoutingCache();
    cache.setClock(() => 1_000);
    cache.set(baseScope, documentFor("orders")!);
    expect(cache.get({ ...baseScope, database: "billing" })).toBeUndefined();
    expect(cache.get({ ...baseScope, projectId: "p2" })).toBeUndefined();
    expect(cache.get({ ...baseScope, tenantId: "t2" })).toBeUndefined();
    expect(cache.get(baseScope)).toBeDefined();
  });

  it("demotes a failed region behind the alternatives and re-prioritizes", () => {
    const cache = new TerraScaleRoutingCache();
    cache.setClock(() => 1_000);
    cache.set(baseScope, documentFor("orders")!);
    cache.demoteRegion(baseScope, "FRA");
    const doc = cache.get(baseScope)!;
    expect(doc.endpoints[0]?.region).toBe("GRU");
    expect(doc.endpoints[0]?.priority).toBe(1);
  });

  it("keeps the full list when every region is demoted", () => {
    const cache = new TerraScaleRoutingCache();
    cache.setClock(() => 1_000);
    cache.set(baseScope, documentFor("orders")!);
    cache.demoteRegion(baseScope, "FRA");
    cache.demoteRegion(baseScope, "GRU");
    expect(cache.get(baseScope)?.endpoints).toHaveLength(2);
  });

  it("marks and consumes exactly one corrective lookup", () => {
    const cache = new TerraScaleRoutingCache();
    cache.markStale(baseScope);
    expect(cache.consumeStale(baseScope)).toBe(true);
    expect(cache.consumeStale(baseScope)).toBe(false);
  });
});

describe("routingDocumentFromPlacement", () => {
  it("preserves placement order and grants full capabilities", () => {
    const scope = { origin: "https://api.terrascale.tech", database: "orders", branch: "main" };
    const doc = routingDocumentFromPlacement(scope, ["GRU", "SFO", "FRA"], 3);
    expect(doc?.endpoints.map((endpoint) => endpoint.region)).toEqual(["GRU", "SFO", "FRA"]);
    expect(doc?.endpoints[0]?.capabilities).toContain("write");
    expect(doc?.placement).toBe("known");
  });

  it("declines synthesis for loopback clients", () => {
    const scope = { origin: "http://127.0.0.1:7000", database: "orders", branch: "main" };
    expect(routingDocumentFromPlacement(scope, ["GRU"], 1)).toBeNull();
  });

  it("declines synthesis outside the public global API origin", () => {
    // A custom origin must never cache synthesized production URLs.
    expect(routingDocumentFromPlacement(
      { origin: "https://api.example.com", database: "orders", branch: "main" }, ["GRU"], 1,
    )).toBeNull();
    expect(routingDocumentFromPlacement(
      { origin: "http://gru.api.localhost:8787", database: "orders", branch: "main" }, ["GRU"], 1,
    )).toBeNull();
  });
});

describe("TerraScaleDiscoveryRouter", () => {
  function routerWith(
    fetchImpl: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>,
    extra: { preferredRegion?: "GRU" } = {},
  ): TerraScaleDiscoveryRouter {
    return new TerraScaleDiscoveryRouter({
      origin: "https://api.terrascale.tech",
      discoveryBaseUrl: "https://api.terrascale.tech",
      fetch: fetchImpl,
      timeoutMs: 30_000,
      discoveryTimeoutMs: 500,
      ...(extra.preferredRegion === undefined ? {} : { preferredRegion: extra.preferredRegion }),
    }, new TerraScaleRoutingCache());
  }

  it("resolves a direct regional base and filters by capability", async () => {
    let seen: Request | undefined;
    const router = routerWith(async (input) => {
      seen = new Request(input);
      return Response.json(validDocumentPayload());
    });
    const read = await router.resolveBase("orders", false);
    expect(read?.baseUrl).toBe("https://fra.terrascale.tech");
    const write = await router.resolveBase("orders", true);
    expect(write?.baseUrl).toBe("https://fra.terrascale.tech");
    expect(new URL(seen!.url).searchParams.get("database")).toBe("orders");
    expect(new URL(seen!.url).searchParams.get("branch")).toBe("main");
  });

  it("moves writes past a draining endpoint and keeps the moved-to region sticky", async () => {
    const router = routerWith(async () => Response.json(validDocumentPayload({
      endpoints: [
        { region: "FRA", url: "https://fra.terrascale.tech", priority: 1, capabilities: ["read", "sql", "reactive"] },
        { region: "GRU", url: "https://gru.terrascale.tech", priority: 2, capabilities: ["read", "write"] },
      ],
    })));
    expect((await router.resolveBase("orders", true))?.baseUrl).toBe("https://gru.terrascale.tech");
    // GRU stays pinned for reads while it remains eligible (ADR 0050 §5.5).
    expect((await router.resolveBase("orders", false))?.baseUrl).toBe("https://gru.terrascale.tech");
  });

  it("caches per scope and shares one in-flight discovery", async () => {
    let calls = 0;
    const router = routerWith(async () => {
      calls++;
      return Response.json(validDocumentPayload());
    });
    const [a, b] = await Promise.all([
      router.resolveBase("orders", false),
      router.resolveBase("orders", false),
    ]);
    expect(calls).toBe(1);
    expect(a?.baseUrl).toBe(b?.baseUrl);
    await router.resolveBase("orders", false);
    expect(calls).toBe(1);
  });

  it("falls back to undefined on discovery failure and backs off", async () => {
    let clock = 1_000;
    const cache = new TerraScaleRoutingCache();
    cache.setClock(() => clock);
    const router = new TerraScaleDiscoveryRouter({
      origin: "https://api.terrascale.tech",
      discoveryBaseUrl: "https://api.terrascale.tech",
      fetch: async () => new Response("boom", { status: 503 }),
      timeoutMs: 30_000,
      discoveryTimeoutMs: 500,
    }, cache);
    expect(await router.resolveBase("orders", false)).toBeUndefined();
    expect(await router.resolveBase("orders", false)).toBeUndefined();
    clock += 2_000;
    expect(await router.resolveBase("orders", false)).toBeUndefined();
  });

  it("resolves the catalog-selected regional origin and caches it five minutes", async () => {
    const catalogCache = new TerraScaleRegionCatalogCache();
    let clock = 1_000;
    catalogCache.setClock(() => clock);
    let catalogFetches = 0;
    const router = new TerraScaleDiscoveryRouter({
      origin: "https://api.terrascale.tech",
      discoveryBaseUrl: "https://atlas.terrascale.tech",
      fetch: async () => {
        catalogFetches++;
        return Response.json(catalogPayload());
      },
      timeoutMs: 30_000,
      discoveryTimeoutMs: 500,
    }, new TerraScaleRoutingCache(), catalogCache);
    // GRU is listed second but unavailable; the first available region wins.
    expect(await router.resolveCatalogBase()).toBe("https://fra.terrascale.tech");
    expect(await router.resolveCatalogBase()).toBe("https://fra.terrascale.tech");
    expect(catalogFetches).toBe(1);
    clock += 301_000;
    await router.resolveCatalogBase();
    expect(catalogFetches).toBe(2);
  });

  it("returns undefined on catalog failure and retries on the next call", async () => {
    const catalogCache = new TerraScaleRegionCatalogCache();
    let fail = true;
    let catalogFetches = 0;
    const router = new TerraScaleDiscoveryRouter({
      origin: "https://api.terrascale.tech",
      discoveryBaseUrl: "https://atlas.terrascale.tech",
      fetch: async () => {
        catalogFetches++;
        return fail ? new Response("unavailable", { status: 503 }) : Response.json(catalogPayload());
      },
      timeoutMs: 30_000,
      discoveryTimeoutMs: 500,
    }, new TerraScaleRoutingCache(), catalogCache);
    expect(await router.resolveCatalogBase()).toBeUndefined();
    fail = false;
    // Refresh on failure: nothing was cached, so the next call refetches.
    expect(await router.resolveCatalogBase()).toBe("https://fra.terrascale.tech");
    expect(catalogFetches).toBe(2);
  });

  it("honours a preferred region only when it is eligible", async () => {
    const router = routerWith(async () => Response.json(validDocumentPayload()), { preferredRegion: "GRU" });
    expect((await router.resolveBase("orders", false))?.region).toBe("GRU");
    // GRU is read-only in the fixture, so a write must stay on FRA.
    expect((await router.resolveBase("orders", true))?.region).toBe("FRA");
  });

  it("keeps the pinned region across a re-ranking refresh (ADR 0050 §5.5)", async () => {
    const cache = new TerraScaleRoutingCache();
    let clock = 1_000;
    cache.setClock(() => clock);
    let gruFirst = false;
    const router = new TerraScaleDiscoveryRouter({
      origin: "https://api.terrascale.tech",
      discoveryBaseUrl: "https://api.terrascale.tech",
      fetch: async () => Response.json(validDocumentPayload(gruFirst ? {
        endpoints: [
          { region: "GRU", url: "https://gru.terrascale.tech", priority: 1, capabilities: ["read", "write", "sql", "reactive"] },
          { region: "FRA", url: "https://fra.terrascale.tech", priority: 2, capabilities: ["read", "write", "sql", "reactive"] },
        ],
      } : {})),
      timeoutMs: 30_000,
      discoveryTimeoutMs: 500,
    }, cache);
    expect((await router.resolveBase("orders", false))?.region).toBe("FRA");
    // A routine refresh now ranks GRU first; the healthy pin stays on FRA.
    gruFirst = true;
    clock += 301_000;
    expect((await router.resolveBase("orders", false))?.region).toBe("FRA");
  });

  it("moves selection when the pinned region leaves the document", async () => {
    const cache = new TerraScaleRoutingCache();
    let clock = 1_000;
    cache.setClock(() => clock);
    let withFra = true;
    const router = new TerraScaleDiscoveryRouter({
      origin: "https://api.terrascale.tech",
      discoveryBaseUrl: "https://api.terrascale.tech",
      fetch: async () => Response.json(withFra ? validDocumentPayload() : validDocumentPayload({
        endpoints: [{ region: "GRU", url: "https://gru.terrascale.tech", priority: 1, capabilities: ["read", "write", "sql", "reactive"] }],
      })),
      timeoutMs: 30_000,
      discoveryTimeoutMs: 500,
    }, cache);
    expect((await router.resolveBase("orders", false))?.region).toBe("FRA");
    withFra = false;
    clock += 301_000;
    expect((await router.resolveBase("orders", false))?.region).toBe("GRU");
  });

  it("moves selection when the pinned region is demoted", async () => {
    const router = routerWith(async () => Response.json(validDocumentPayload()));
    expect((await router.resolveBase("orders", false))?.region).toBe("FRA");
    router.reportRegionFailure("orders", "FRA");
    expect((await router.resolveBase("orders", false))?.region).toBe("GRU");
  });

  it("discards an in-flight discovery result when a 421 placement lands mid-flight", async () => {
    let releaseFetch!: (response: Response) => void;
    const router = routerWith(() => new Promise<Response>(resolve => {
      releaseFetch = resolve;
    }));
    const pending = router.resolveBase("orders", false);
    await new Promise(resolve => setTimeout(resolve, 0));
    router.notePlacement("orders", ["SFO"], 5);
    // The fetch started before the 421 and still answers with the old ranking.
    releaseFetch(Response.json(validDocumentPayload()));
    expect(await pending).toBeUndefined();
    expect(router.cachedDocument("orders")?.endpoints[0]?.region).toBe("SFO");
    expect((await router.resolveBase("orders", false))?.region).toBe("SFO");
  });

  it("sends measured region RTT and stale corrections", async () => {
    const requests: Request[] = [];
    const cache = new TerraScaleRoutingCache();
    let clock = 1_000;
    cache.setClock(() => clock);
    const router = new TerraScaleDiscoveryRouter({
      origin: "https://api.terrascale.tech",
      discoveryBaseUrl: "https://api.terrascale.tech",
      fetch: async (input) => {
        requests.push(new Request(input));
        return Response.json(validDocumentPayload({ revision: 9 }));
      },
      timeoutMs: 30_000,
      discoveryTimeoutMs: 500,
    }, cache);
    router.recordLatency("FRA", 90);
    await router.resolveBase("orders", false);
    clock += 301_000;
    await router.resolveBase("orders", false);
    expect(requests).toHaveLength(2);
    expect(requests[1]?.headers.get("x-terrascale-region-rtt")).toBe("FRA=90");
    // ADR 0050 §2: refreshes carry the cached routing revision as `rev`.
    expect(new URL(requests[1]!.url).searchParams.get("rev")).toBe("9");
    router.reportRejectedRoute("orders");
    clock += 301_000;
    await router.resolveBase("orders", false);
    expect(new URL(requests[2]!.url).searchParams.get("stale")).toBe("1");
  });
});

describe("fetchRoutingDocument", () => {
  it("maps worker failures to typed discovery errors", async () => {
    const notFound = await fetchRoutingDocument({
      origin: "https://api.terrascale.tech",
      database: "orders",
      branch: "main",
      fetch: async () => new Response(null, { status: 404 }),
      timeoutMs: 500,
    }).catch((error: unknown) => error);
    expect(notFound).toBeInstanceOf(TerraScaleDiscoveryError);
    expect((notFound as TerraScaleDiscoveryError).code).toBe("not_found");

    const malformed = await fetchRoutingDocument({
      origin: "https://api.terrascale.tech",
      database: "orders",
      branch: "main",
      fetch: async () => Response.json({ hello: "world" }),
      timeoutMs: 500,
    }).catch((error: unknown) => error);
    expect((malformed as TerraScaleDiscoveryError).code).toBe("invalid_document");
  });
});

describe("fetchRegionCatalog", () => {
  it("maps catalog failures to typed discovery errors", async () => {
    const unavailable = await fetchRegionCatalog({
      origin: "https://atlas.terrascale.tech",
      fetch: async () => new Response("boom", { status: 503 }),
      timeoutMs: 500,
    }).catch((error: unknown) => error);
    expect(unavailable).toBeInstanceOf(TerraScaleDiscoveryError);
    expect((unavailable as TerraScaleDiscoveryError).code).toBe("unavailable");

    const malformed = await fetchRegionCatalog({
      origin: "https://atlas.terrascale.tech",
      fetch: async () => Response.json({ hello: "world" }),
      timeoutMs: 500,
    }).catch((error: unknown) => error);
    expect((malformed as TerraScaleDiscoveryError).code).toBe("invalid_document");
  });
});

describe("RegionRttTracker", () => {
  it("converges an EWMA and rejects out-of-band samples", () => {
    const tracker = new RegionRttTracker();
    tracker.observe("FRA", 100, 0);
    tracker.observe("FRA", 200, 60_000);
    const value = tracker.snapshot().get("FRA")!;
    expect(value).toBeGreaterThan(100);
    expect(value).toBeLessThan(200);
    tracker.observe("FRA", 999_999, 61_000);
    expect(tracker.snapshot().get("FRA")).toBe(value);
  });
});
