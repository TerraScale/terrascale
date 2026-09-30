export const terraScaleRegionCodes = ["GRU", "SFO", "FRA"] as const;

/** Region codes are admitted by the published catalog and may grow over time. */
export type TerraScaleRegionCode = string;

/** The public global REST router; regional APIs use <region>.terrascale.tech. */
export const terraScaleGlobalApiHostname = "api.terrascale.tech";

/**
 * The metadata origin (ADR 0050 §8): `ts-atlas` serves discovery and the
 * region catalog on `atlas.terrascale.tech` and forwards nothing.
 */
export const terraScaleDiscoveryHostname = "atlas.terrascale.tech";

/** The default discovery/catalog origin, independent of `apiBaseUrl`. */
export const terraScaleDiscoveryBaseUrl = `https://${terraScaleDiscoveryHostname}`;

/** Return the canonical public hostname for a Region's REST member. */
export function regionalApiHostname(region: TerraScaleRegionCode): string {
  return `${region.toLowerCase()}.terrascale.tech`;
}

/**
 * Rewrites the production global API hostname to a regional member hostname.
 * Custom origins (tests, local proxies, and dashboard service bindings) stay
 * unchanged; the caller still sends the region as a typed request hint.
 */
export function regionalApiBaseUrl(
  baseUrl: string,
  region: TerraScaleRegionCode,
): string {
  let url: URL;
  try {
    url = new URL(`${baseUrl}/`);
  } catch {
    return baseUrl;
  }

  if (url.hostname.toLowerCase() !== terraScaleGlobalApiHostname) {
    return baseUrl;
  }

  url.hostname = regionalApiHostname(region);
  return url.toString().replace(/\/$/u, "");
}

export type TerraScaleRegion = {
  readonly code: TerraScaleRegionCode;
  readonly displayName: string;
  readonly latitude: number;
  readonly longitude: number;
  readonly available: boolean;
};

export const terraScaleRegions = [
  {
    code: "GRU",
    displayName: "Sao Paulo",
    latitude: -23.5475,
    longitude: -46.6361,
    available: true,
  },
  {
    code: "SFO",
    displayName: "San Francisco",
    latitude: 37.7749,
    longitude: -122.4194,
    available: true,
  },
  {
    code: "FRA",
    displayName: "Frankfurt",
    latitude: 50.1109,
    longitude: 8.6821,
    available: true,
  },
] as const satisfies readonly TerraScaleRegion[];

const regionsByCode = new Map<TerraScaleRegionCode, TerraScaleRegion>(
  terraScaleRegions.map((region) => [region.code, region]),
);

export function canonicalRegionCode(code: string | undefined): TerraScaleRegionCode | undefined {
  return code !== undefined && isRegionCode(code) ? code : undefined;
}

export function findRegion(code: string | undefined): TerraScaleRegion | undefined {
  const canonical = canonicalRegionCode(code);
  return canonical === undefined ? undefined : regionsByCode.get(canonical);
}

const reservedServiceLabels = new Set(["API", "ATLAS", "AUTH", "SYNC", "STATUS", "WWW", "APP", "DASH", "DOCS", "GIT", "CDN", "ADMIN", "MCP", "MAIL"]);

export function isRegionCode(code: unknown): code is TerraScaleRegionCode {
  return typeof code === "string" && /^[A-Z0-9][A-Z0-9-]{0,15}$/u.test(code) && !reservedServiceLabels.has(code);
}

export function parseRegionCode(code: unknown, fieldName = "regionCode"): TerraScaleRegionCode {
  if (!isRegionCode(code)) {
    throw new TypeError(`${fieldName} must be an uppercase Region code of 1 to 16 letters, digits, or hyphens.`);
  }

  return code;
}

export function normalizeRegionCodes(
  codes: readonly string[] | undefined,
): readonly TerraScaleRegionCode[] {
  if (codes === undefined || codes.length === 0 || codes.length > 20) {
    throw new TypeError("regions must contain between 1 and 20 exact TerraScale region codes.");
  }

  const selected = new Set<TerraScaleRegionCode>();

  for (const code of codes) {
    selected.add(parseRegionCode(code, "regions[]"));
  }

  return [...selected];
}
