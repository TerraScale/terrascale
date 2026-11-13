import { databaseName, projectId, type Region, type Topology } from "./contract";
import type { Env } from "./env";
import { haversineKm } from "./geo";
import { publicJwks, signPin } from "./pin";
import { publish } from "./publish";
import { getPlacement, getTopology, staleAfterS, topologyAge } from "./read";

export { AtlasStore } from "./store";

const CORS_HEADERS: Record<string, string> = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
  "Access-Control-Allow-Headers": "Content-Type, X-Atlas-Timestamp, X-Atlas-Signature",
  "Access-Control-Max-Age": "86400",
};

function errorResponse(code: string, message: string, fix: string, status: number, extra?: Record<string, string>) {
  return Response.json({ error: { code, message, fix } }, { status, headers: { ...CORS_HEADERS, ...extra } });
}

function clientPoint(request: Request): { lat: number; lon: number } | null {
  const cf = request.cf;
  if (cf?.latitude !== undefined && cf.longitude !== undefined) {
    const lat = Number(cf.latitude);
    const lon = Number(cf.longitude);
    if (Number.isFinite(lat) && Number.isFinite(lon) && Math.abs(lat) <= 90 && Math.abs(lon) <= 180)
      return { lat, lon };
  }
  const continents: Record<string, { lat: number; lon: number }> = {
    AF: { lat: 0, lon: 20 },
    AN: { lat: -82, lon: 0 },
    AS: { lat: 35, lon: 105 },
    EU: { lat: 50, lon: 15 },
    NA: { lat: 40, lon: -100 },
    OC: { lat: -25, lon: 135 },
    SA: { lat: -15, lon: -60 },
  };
  return continents[typeof cf?.continent === "string" ? cf.continent : ""] ?? null;
}

function healthOf(region: Region, topology: Topology, staleLimit: number): "ok" | "unknown" | "down" {
  if (topologyAge(topology) > staleLimit) return "unknown";
  return region.ok ? "ok" : "down";
}

function rankedRegions(topology: Topology, point: { lat: number; lon: number } | null, staleLimit: number) {
  const healthRank = { ok: 0, unknown: 1, down: 2 } as const;
  return topology.regions
    .map((region, index) => ({
      region,
      index,
      health: healthOf(region, topology, staleLimit),
      distanceKm: point === null ? null : haversineKm(point, { lat: region.lat, lon: region.lon }),
    }))
    .sort((a, b) => {
      const byHealth = healthRank[a.health] - healthRank[b.health];
      if (byHealth !== 0) return byHealth;
      if (a.distanceKm !== null && b.distanceKm !== null && a.distanceKm !== b.distanceKm)
        return a.distanceKm - b.distanceKm;
      return a.index - b.index;
    });
}

async function discovery(request: Request, env: Env, ctx: ExecutionContext, fresh: boolean): Promise<Response> {
  const { doc: topology, cache, stale } = await getTopology(env, ctx, fresh);
  if (topology === null) {
    return errorResponse(
      "discovery_unavailable",
      "No topology copy is available",
      "Retry later; the orchestrator publishes the topology.",
      503,
      { "Cache-Control": "no-store" },
    );
  }
  const staleLimit = staleAfterS(env);
  const point = clientPoint(request);
  const ranked = rankedRegions(topology, point, staleLimit);
  const closest = ranked.find((entry) => entry.health !== "down") ?? null;
  const body = {
    v: 1,
    updated: topology.updated,
    stale,
    closest: closest === null ? null : closest.region.id,
    regions: ranked.map(({ region, health, distanceKm }) => ({
      id: region.id,
      name: region.name,
      country: region.country,
      lat: region.lat,
      lon: region.lon,
      api: region.api,
      auth: region.auth,
      health,
      distanceKm,
    })),
    services: topology.services,
  };
  return Response.json(body, {
    headers: { ...CORS_HEADERS, "Cache-Control": "no-store", "X-TerraScale-Cache": cache },
  });
}

async function resolve(request: Request, env: Env, ctx: ExecutionContext, url: URL, fresh: boolean): Promise<Response> {
  const project = url.searchParams.get("project") ?? "";
  const database = url.searchParams.get("database");
  if (!projectId(project)) {
    return errorResponse("invalid_project", "project is malformed", "Pass the TerraBase project id.", 400, {
      "Cache-Control": "no-store",
    });
  }
  if (database !== null && !databaseName(database)) {
    return errorResponse("invalid_database", "database is malformed", "Pass a valid database name.", 400, {
      "Cache-Control": "no-store",
    });
  }
  const headers: Record<string, string> = { ...CORS_HEADERS, "Cache-Control": "no-store" };
  const { doc: topology, cache, stale: topologyStale } = await getTopology(env, ctx, fresh);
  headers["X-TerraScale-Cache"] = cache;
  if (topology === null) {
    return errorResponse(
      "discovery_unavailable",
      "No topology copy is available",
      "Retry later; the orchestrator publishes the topology.",
      503,
      headers,
    );
  }
  const placementRead =
    database === null
      ? ({ doc: null, cache: "HIT", stale: false } as const)
      : await getPlacement(env, ctx, project, database, fresh);
  if (placementRead.cache === "MISS") headers["X-TerraScale-Cache"] = "MISS";
  const stale = topologyStale || placementRead.stale;
  const staleLimit = staleAfterS(env);
  const targetId = placementRead.doc?.region ?? null;
  const ranked = rankedRegions(topology, clientPoint(request), staleLimit);
  const selected =
    targetId === null
      ? (ranked.find((entry) => entry.health !== "down")?.region ?? null)
      : (topology.regions.find((region) => region.id === targetId) ?? null);
  const placementState = targetId === null ? "any" : "known";
  if (selected === null) {
    return errorResponse(
      "no_eligible_region",
      "No region is usable",
      "Retry after the next topology publish.",
      503,
      headers,
    );
  }
  const signed = await signPin(
    env,
    { project, ...(database === null ? {} : { database }), region: selected.id, api: selected.api },
    Math.floor(Date.now() / 1000),
  );
  if (signed === null) {
    return errorResponse(
      "pin_unavailable",
      "No pin signing key is configured",
      "Provision the PIN_SIGNING_KEY secret.",
      503,
      headers,
    );
  }
  const body = {
    v: 1,
    project,
    ...(database === null ? {} : { database }),
    region: selected.id,
    api: selected.api,
    auth: selected.auth,
    health: healthOf(selected, topology, staleLimit),
    placement: placementState,
    stale,
    pin: signed.pin,
    expires: signed.exp,
  };
  return Response.json(body, { headers });
}

async function keys(env: Env): Promise<Response> {
  const jwks = await publicJwks(env);
  if (jwks === null) {
    return errorResponse(
      "pin_unavailable",
      "No pin signing key is configured",
      "Provision the PIN_SIGNING_KEY secret.",
      503,
      { ...CORS_HEADERS, "Cache-Control": "no-store" },
    );
  }
  return new Response(jwks, {
    headers: { ...CORS_HEADERS, "Content-Type": "application/json", "Cache-Control": "public, max-age=300" },
  });
}

export default {
  async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    let response: Response;
    const url = new URL(request.url);
    const fresh = url.searchParams.get("fresh") === "1";
    try {
      if (request.method === "OPTIONS") response = new Response(null, { status: 204 });
      else if (request.method === "GET" && url.pathname === "/v1/discovery")
        response = await discovery(request, env, ctx, fresh);
      else if (request.method === "GET" && url.pathname === "/v1/resolve")
        response = await resolve(request, env, ctx, url, fresh);
      else if (request.method === "GET" && url.pathname === "/v1/keys") response = await keys(env);
      else if (request.method === "POST" && url.pathname === "/v1/publish") response = await publish(request, env);
      else response = errorResponse("not_found", "No such route", "Use an Atlas v1 route.", 404);
    } catch (error) {
      console.error("atlas request failed", error);
      response = errorResponse(
        "discovery_unavailable",
        "The authoritative store is unavailable",
        "Retry after the store recovers.",
        503,
      );
    }
    const result = new Response(response.body, response);
    for (const [key, value] of Object.entries(CORS_HEADERS)) result.headers.set(key, value);
    result.headers.set("Access-Control-Expose-Headers", "X-TerraScale-Cache");
    if (!result.headers.has("Cache-Control")) result.headers.set("Cache-Control", "no-store");
    return result;
  },
};
