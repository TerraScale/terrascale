export type TerraScaleRegionCode = string;

export type TerraScaleRegion = {
  readonly code: TerraScaleRegionCode;
  readonly displayName: string;
  readonly latitude: number;
  readonly longitude: number;
  readonly ociRegionCode: string;
  readonly clusterNodeTag?: string;
  readonly available: boolean;
};

export const terraScaleRegions = [
  {
    code: "gru",
    displayName: "Sao Paulo",
    latitude: -23.5475,
    longitude: -46.6361,
    ociRegionCode: "sa-saopaulo-1",
    clusterNodeTag: "A",
    available: true,
  },
  {
    code: "sfo",
    displayName: "San Francisco",
    latitude: 37.7749,
    longitude: -122.4194,
    ociRegionCode: "us-sanjose-1",
    clusterNodeTag: "B",
    available: true,
  },
  {
    code: "fra",
    displayName: "Frankfurt",
    latitude: 50.1109,
    longitude: 8.6821,
    ociRegionCode: "eu-frankfurt-1",
    clusterNodeTag: "C",
    available: true,
  },
] as const satisfies readonly TerraScaleRegion[];

const regionsByCode = new Map<TerraScaleRegionCode, TerraScaleRegion>(
  terraScaleRegions.map((region) => [region.code, region]),
);
const regionCodeValues = new Set<string>(terraScaleRegions.map((region) => region.code));

export function canonicalRegionCode(code: string | undefined): TerraScaleRegionCode | undefined {
  if (code === undefined) {
    return undefined;
  }

  const normalized = code.trim().toLowerCase();
  return /^[a-z0-9][a-z0-9-]{1,31}$/.test(normalized)
    ? normalized
    : undefined;
}

export function findRegion(code: string | undefined): TerraScaleRegion | undefined {
  const canonical = canonicalRegionCode(code);
  return canonical === undefined ? undefined : regionsByCode.get(canonical);
}

export function isRegionCode(code: string): code is TerraScaleRegionCode {
  return regionCodeValues.has(code);
}

export function normalizeRegionCodes(
  codes: readonly string[] | undefined,
): readonly TerraScaleRegionCode[] {
  const selected = new Set<TerraScaleRegionCode>();

  for (const code of codes ?? []) {
    const candidate = code.trim().toLowerCase();
    if (candidate.length === 0) {
      continue;
    }

    // Preserve a caller-supplied malformed or unknown value so the API can
    // reject it. Silently substituting GRU could provision data in a region
    // the caller never selected.
    selected.add(candidate);
  }

  return selected.size === 0 ? ["gru"] : [...selected];
}
