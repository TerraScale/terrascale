import type { Topology, Placement } from "./contract";
import type { Env } from "./env";

const MEMORY_TTL_MS = 5_000;
const KV_TTL_MS = 60_000;
export const placementKey = (project: string, database: string) => `placement:${project}:${database}`;

type Document = Topology | Placement;
interface CacheEntry<T> {
  at: number;
  doc: T | null;
}
interface MemoryEntry extends CacheEntry<Document> {
  remembered: number;
}
const memory = new Map<string, MemoryEntry>();

export function invalidateMemory(): void {
  memory.clear();
}

export function cacheDocument<T>(doc: T | null): CacheEntry<T> {
  return { at: Date.now(), doc };
}

function remember(key: string, entry: CacheEntry<Document>): void {
  if (memory.size >= 512) memory.delete(memory.keys().next().value!);
  memory.set(key, { ...entry, remembered: Date.now() });
}

function current(at: number, ttl: number): boolean {
  const age = Date.now() - at;
  return age >= 0 && age < ttl;
}

export interface ReadResult<T> {
  doc: T | null;
  cache: "HIT" | "MISS";
  stale: boolean;
}

async function load<T extends Document>(
  env: Env,
  ctx: ExecutionContext,
  key: string,
  fresh: boolean,
  read: () => Promise<T | null>,
): Promise<ReadResult<T>> {
  let cached = memory.get(key);
  if (!fresh) {
    if (cached !== undefined && current(cached.remembered, MEMORY_TTL_MS) && current(cached.at, KV_TTL_MS)) {
      return { doc: cached.doc as T | null, cache: "HIT", stale: false };
    }
    try {
      const kv = await env.ATLAS_KV.get<CacheEntry<T>>(key, "json");
      if (kv !== null && typeof kv.at === "number" && "doc" in kv) {
        if (current(kv.at, KV_TTL_MS)) {
          remember(key, kv);
          return { doc: kv.doc, cache: "HIT", stale: false };
        }
        if (cached === undefined || kv.at > cached.at) cached = { ...kv, remembered: 0 };
      }
    } catch (error) {
      console.error("atlas KV read failed", error);
    }
  }
  let doc: T | null;
  try {
    doc = await read();
  } catch (error) {
    if (!fresh && cached !== undefined) return { doc: cached.doc as T | null, cache: "HIT", stale: true };
    throw error;
  }
  const entry = cacheDocument(doc);
  remember(key, entry);
  ctx.waitUntil(
    env.ATLAS_KV.put(key, JSON.stringify(entry)).catch((error: unknown) => {
      console.error("atlas KV refill failed", error);
    }),
  );
  return { doc, cache: "MISS", stale: false };
}

export function getTopology(env: Env, ctx: ExecutionContext, fresh: boolean): Promise<ReadResult<Topology>> {
  return load(env, ctx, "topology", fresh, async () => {
    const row = await env.ATLAS_DB.prepare("SELECT doc FROM topology WHERE id = 1").first<{ doc: string }>();
    return row === null ? null : (JSON.parse(row.doc) as Topology);
  });
}

export function getPlacement(
  env: Env,
  ctx: ExecutionContext,
  project: string,
  database: string,
  fresh: boolean,
): Promise<ReadResult<Placement>> {
  return load(env, ctx, placementKey(project, database), fresh, async () => {
    const row = await env.ATLAS_DB.prepare("SELECT region, updated FROM placements WHERE project = ? AND database = ?")
      .bind(project, database)
      .first<{ region: string | null; updated: number }>();
    return row === null ? null : { v: 1, project, database, ...row };
  });
}

export function staleAfterS(env: Env): number {
  const parsed = Number(env.STALE_AFTER_S ?? "60");
  return Number.isFinite(parsed) && parsed > 0 ? parsed : 60;
}

export function topologyAge(topology: Topology): number {
  return Math.max(0, Math.floor(Date.now() / 1000) - topology.updated);
}
