import type { AtlasStore } from "./store";

export interface Env {
  ATLAS_DB: D1Database;
  ATLAS_KV: KVNamespace;
  ATLAS_STORE: DurableObjectNamespace<AtlasStore>;
  TOPOLOGY_PUBLISH_KEYS?: string;
  PLACEMENT_PUBLISH_KEYS?: string;
  PIN_SIGNING_KEY?: string;
  DEV_ORIGINS?: string;
  STALE_AFTER_S?: string;
  PIN_TTL_S?: string;
  VERSION?: string;
}
