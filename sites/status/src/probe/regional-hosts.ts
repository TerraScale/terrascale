import { newId } from "../ids";
import { logEvent } from "../log";
import { check } from "./check";
import { isOrigin } from "./terrabase";

export const REGIONAL_ORIGINS = "https://atlas.terrascale.tech/v1/discovery";
const API_COMPONENT = "01M4E7G1W82VKFPF5FJ9AVK1D2";

interface RegionOrigin {
  id: string;
  api: string;
}

function origins(body: string): RegionOrigin[] {
  const value = JSON.parse(body) as { regions?: unknown } | null;
  if (!Array.isArray(value?.regions) || value.regions.length === 0 || value.regions.length > 32)
    throw new Error("Invalid regional origin discovery");
  const seen = new Set<string>();
  for (const region of value.regions as RegionOrigin[]) {
    if (
      !region ||
      typeof region.id !== "string" ||
      !/^[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(region.id) ||
      seen.has(region.id) ||
      !isOrigin(region.api) ||
      !new URL(region.api).hostname.endsWith(".terrascale.tech")
    )
      throw new Error("Invalid regional origin discovery");
    seen.add(region.id);
  }
  return value.regions as RegionOrigin[];
}

export async function discoverRegionalHosts(db: D1Database, fetcher: typeof fetch = fetch): Promise<void> {
  try {
    const result = await check({ key: REGIONAL_ORIGINS, url: REGIONAL_ORIGINS, headers: "{}", body: true }, fetcher);
    if (result.status !== 200 || !result.body) throw new Error("Regional origin discovery unavailable");
    const regions = origins(result.body);
    const codes = JSON.stringify(regions.map(({ id }) => id.toUpperCase()));
    await db.batch([
      ...regions.flatMap((region) => {
        const code = region.id.toUpperCase();
        const url = region.api + "/healthz";
        const assertions = JSON.stringify([
          { path: "status", op: "eq", value: "ok" },
          { path: "region", op: "eq", value: region.id },
        ]);
        return [
          db
            .prepare("INSERT OR IGNORE INTO regions (code, name, country, map_x, map_y) VALUES (?, ?, '', 0, 0)")
            .bind(code, region.id),
          db
            .prepare(`INSERT INTO endpoints (id, component_id, region_code, cluster, provider, url, expect_status, assertions)
              SELECT ?1, ?2, ?3, ?4, 'other', ?5, '200', ?6
              WHERE EXISTS (SELECT 1 FROM components WHERE id = ?2)
                AND NOT EXISTS (SELECT 1 FROM endpoints WHERE component_id = ?2 AND region_code = ?3 AND url = ?5)`)
            .bind(newId(), API_COMPONENT, code, region.id, url, assertions),
        ];
      }),
      db
        .prepare(`INSERT INTO region_discovery (region_code)
        SELECT value FROM json_each(?1) WHERE true
        ON CONFLICT (region_code) DO UPDATE SET retired = 0 WHERE region_discovery.retired = 1`)
        .bind(codes),
      db
        .prepare(`UPDATE region_discovery SET retired = 1 WHERE retired = 0
        AND region_code NOT IN (SELECT value FROM json_each(?1))`)
        .bind(codes),
    ]);
  } catch (error) {
    logEvent("error", "regional host discovery failed", { component: "regional-hosts", error });
  }
}
