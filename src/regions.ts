export const terraScaleRegionCodes = ["GRU", "SFO", "FRA"] as const;

export type TerraScaleRegionCode = (typeof terraScaleRegionCodes)[number];

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

export function isRegionCode(code: unknown): code is TerraScaleRegionCode {
  return typeof code === "string" && regionsByCode.has(code as TerraScaleRegionCode);
}

export function parseRegionCode(code: unknown, fieldName = "regionCode"): TerraScaleRegionCode {
  if (!isRegionCode(code)) {
    throw new TypeError(`${fieldName} must be exactly one of GRU, SFO, or FRA.`);
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
