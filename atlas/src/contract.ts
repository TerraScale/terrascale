export interface Region {
  id: string;
  name: string;
  country: string;
  lat: number;
  lon: number;
  api: string;
  auth: string;
  ok: boolean;
}

export interface Service {
  id: string;
  name: string;
  url: string;
  health?: string;
}

export interface Topology {
  v: 1;
  updated: number;
  regions: Region[];
  services: Service[];
}

export interface Placement {
  v: 1;
  project: string;
  database: string;
  region: string | null;
  updated: number;
}

export type PublishKind = "topology" | "placements" | "both";

export interface PublishRequest {
  topology?: Topology;
  placements?: Placement[];
}

export type PublishParse =
  | { ok: true; kind: PublishKind; request: PublishRequest }
  | { ok: false; code: "empty_publish" | "invalid_topology" | "invalid_placement" | "future_updated" };

const PROJECT_ID_RE = /^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$/;
const REGION_ID_RE = /^[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?$/;
const SERVICE_ID_RE = REGION_ID_RE;
const API_ORIGIN_RE = /^https:\/\/[a-z0-9-]+\.api\.terrascale\.tech$/;
const TERRASCALE_ORIGIN_RE = /^https:\/\/([a-z0-9-]+\.)*terrascale\.tech$/;
const LOOPBACK_ORIGIN_RE = /^http:\/\/(127\.0\.0\.1|localhost):[1-9][0-9]{0,4}$/;
const DATABASE_NAME_RE = REGION_ID_RE;
const REQUIRED_SERVICES = ["terrabase", "auth", "dashboard", "status", "docs"] as const;
const FUTURE_S = 300;

export function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function acceptedUrl(url: unknown, env: { DEV_ORIGINS?: string }, api = false, origin = true): url is string {
  if (typeof url !== "string" || url.length > 512) return false;
  try {
    const parsed = new URL(url);
    const host = url.match(/^[a-z]+:\/\/[^/]+/)?.[0] ?? "";
    if (parsed.username || parsed.password || parsed.search || parsed.hash || url.includes("?") || url.includes("#"))
      return false;
    if (origin && host !== url) return false;
    if ((api ? API_ORIGIN_RE : TERRASCALE_ORIGIN_RE).test(host)) return true;
    return env.DEV_ORIGINS !== undefined && LOOPBACK_ORIGIN_RE.test(host);
  } catch {
    return false;
  }
}

export function isFutureUpdated(updated: unknown, now: number): boolean {
  return typeof updated === "number" && Number.isInteger(updated) && updated > now + FUTURE_S;
}

export function parseTopology(input: unknown, env: { DEV_ORIGINS?: string }): Topology | null {
  if (!isObject(input)) return null;
  const { v, updated, regions, services } = input;
  if (v !== 1 || typeof updated !== "number" || !Number.isSafeInteger(updated) || updated < 0) return null;
  if (!Array.isArray(regions) || regions.length < 1 || regions.length > 64) return null;
  if (!Array.isArray(services) || services.length > 32) return null;
  const seen = new Set<string>();
  const parsedRegions: Region[] = [];
  for (const raw of regions) {
    const region = parseRegion(raw, env);
    if (region === null || seen.has(region.id)) return null;
    seen.add(region.id);
    parsedRegions.push(region);
  }
  const serviceIds = new Set<string>();
  const parsedServices: Service[] = [];
  for (const raw of services) {
    const service = parseService(raw, env);
    if (service === null || serviceIds.has(service.id)) return null;
    serviceIds.add(service.id);
    parsedServices.push(service);
  }
  for (const id of REQUIRED_SERVICES) {
    if (!serviceIds.has(id)) return null;
  }
  return { v: 1, updated, regions: parsedRegions, services: parsedServices };
}

function parseRegion(input: unknown, env: { DEV_ORIGINS?: string }): Region | null {
  if (!isObject(input)) return null;
  const { id, name, country, lat, lon, api, auth, ok } = input;
  if (typeof id !== "string" || !REGION_ID_RE.test(id)) return null;
  if (typeof name !== "string" || name.length < 1 || name.length > 128) return null;
  if (typeof country !== "string" || !/^[A-Z]{2}$/.test(country)) return null;
  if (typeof lat !== "number" || !Number.isFinite(lat) || lat < -90 || lat > 90) return null;
  if (typeof lon !== "number" || !Number.isFinite(lon) || lon < -180 || lon > 180) return null;
  if (!acceptedUrl(api, env, true)) return null;
  if (!acceptedUrl(auth, env)) return null;
  if (typeof ok !== "boolean") return null;
  return { id, name, country, lat, lon, api, auth, ok };
}

function parseService(input: unknown, env: { DEV_ORIGINS?: string }): Service | null {
  if (!isObject(input)) return null;
  const { id, name, url, health } = input;
  if (typeof id !== "string" || !SERVICE_ID_RE.test(id)) return null;
  if (typeof name !== "string" || name.length < 1 || name.length > 128) return null;
  if (!acceptedUrl(url, env, false, false)) return null;
  if (health !== undefined && typeof health !== "string") return null;
  if (typeof health === "string" && !acceptedUrl(health, env, false, false)) return null;
  return { id, name, url, ...(health === undefined ? {} : { health }) };
}

export function parsePlacement(input: unknown): Placement | null {
  if (!isObject(input)) return null;
  const { v, project, database, region, updated } = input;
  if (v !== 1) return null;
  if (typeof project !== "string" || !projectId(project)) return null;
  if (typeof database !== "string" || !databaseName(database)) return null;
  if (typeof updated !== "number" || !Number.isSafeInteger(updated) || updated < 0) return null;
  if (region !== null && (typeof region !== "string" || !REGION_ID_RE.test(region))) return null;
  return { v: 1, project, database, region, updated };
}

export function parsePublishBody(input: unknown, env: { DEV_ORIGINS?: string }, now: number): PublishParse {
  if (!isObject(input)) return { ok: false, code: "empty_publish" };
  const hasTopology = "topology" in input;
  const hasPlacements = "placements" in input;
  if (!hasTopology && !hasPlacements) return { ok: false, code: "empty_publish" };
  const request: PublishRequest = {};
  if (hasTopology) {
    if (isFutureUpdated(isObject(input.topology) ? input.topology.updated : undefined, now)) {
      return { ok: false, code: "future_updated" };
    }
    const topology = parseTopology(input.topology, env);
    if (topology === null) return { ok: false, code: "invalid_topology" };
    request.topology = topology;
  }
  if (hasPlacements) {
    if (!Array.isArray(input.placements)) return { ok: false, code: "invalid_placement" };
    if (input.placements.length === 0 && !hasTopology) return { ok: false, code: "empty_publish" };
    if (input.placements.length > 64) return { ok: false, code: "invalid_placement" };
    const databases = new Set<string>();
    const placements: Placement[] = [];
    for (const raw of input.placements) {
      if (isFutureUpdated(isObject(raw) ? raw.updated : undefined, now)) {
        return { ok: false, code: "future_updated" };
      }
      const placement = parsePlacement(raw);
      if (placement === null) return { ok: false, code: "invalid_placement" };
      const key = `${placement.project}:${placement.database}`;
      if (databases.has(key)) return { ok: false, code: "invalid_placement" };
      databases.add(key);
      placements.push(placement);
    }
    request.placements = placements;
  }
  const kind: PublishKind = hasTopology && hasPlacements ? "both" : hasTopology ? "topology" : "placements";
  return { ok: true, kind, request };
}

export function projectId(project: string): boolean {
  return PROJECT_ID_RE.test(project);
}

export function databaseName(database: string): boolean {
  return DATABASE_NAME_RE.test(database);
}
