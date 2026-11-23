import { newId } from "../ids";
import { logEvent } from "../log";
import { check } from "./check";
import { isOrigin } from "./terrabase";

export const REGIONAL_ORIGINS = "https://api.terrascale.tech/v1/regions/origins";
const API_COMPONENT = "01M4E7G1W82VKFPF5FJ9AVK1D2";

interface RegionOrigin {
  id: string;
  apiUrl: string;
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
      !isOrigin(region.apiUrl) ||
      !new URL(region.apiUrl).hostname.endsWith(".terrascale.tech")
    )
      throw new Error("Invalid regional origin discovery");
    seen.add(region.id);
  }
  return value.regions as RegionOrigin[];
}

/** Cheap anonymous host checks discovered from ts-orch. Retain known probes
 * during discovery outages so an unavailable catalog cannot hide an outage. */
export async function discoverRegionalHosts(db: D1Database, fetcher: typeof fetch = fetch): Promise<void> {
  try {
    const result = await check({ key: REGIONAL_ORIGINS, url: REGIONAL_ORIGINS, headers: "{}", body: true }, fetcher);
    if (result.status !== 200 || !result.body) throw new Error("Regional origin discovery unavailable");
    const regions = origins(result.body);
    await db.batch(
      regions.flatMap((region) => {
        const code = region.id.toUpperCase();
        const url = region.apiUrl + "/healthz";
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
              SELECT ?, ?, ?, ?, 'other', ?, '200', ? WHERE NOT EXISTS
              (SELECT 1 FROM endpoints WHERE component_id = ? AND region_code = ? AND url = ?)`)
            .bind(newId(), API_COMPONENT, code, region.id, url, assertions, API_COMPONENT, code, url),
        ];
      }),
    );
  } catch (error) {
    logEvent("error", "regional host discovery failed", { component: "regional-hosts", error });
  }
}
