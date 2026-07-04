export type TerraScaleRegionCode = "ashburn" | "gru" | "ord";

export type TerraScaleRegion = {
  readonly code: TerraScaleRegionCode;
  readonly displayName: string;
  readonly latitude: number;
  readonly longitude: number;
  readonly ociRegionCode: "sa-saopaulo-1" | "us-ashburn-1" | "us-chicago-1";
  readonly clusterNodeTag: "A" | "B" | "C";
  readonly available: boolean;
};

export const terraScaleRegions = [
  {
    code: "gru",
    displayName: "Sao Paulo",
    latitude: -23.43,
    longitude: -46.47,
    ociRegionCode: "sa-saopaulo-1",
    clusterNodeTag: "A",
    available: true,
  },
  {
    code: "ashburn",
    displayName: "Ashburn",
    latitude: 39.04,
    longitude: -77.49,
    ociRegionCode: "us-ashburn-1",
    clusterNodeTag: "B",
    available: true,
  },
  {
    code: "ord",
    displayName: "Chicago",
    latitude: 41.98,
    longitude: -87.9,
    ociRegionCode: "us-chicago-1",
    clusterNodeTag: "C",
    available: true,
  },
] as const satisfies readonly TerraScaleRegion[];

const regionsByCode = new Map<TerraScaleRegionCode, TerraScaleRegion>(
  terraScaleRegions.map((region) => [region.code, region]),
);

export function findRegion(code: string | undefined): TerraScaleRegion | undefined {
  if (code === undefined) {
    return undefined;
  }

  return regionsByCode.get(code.trim().toLowerCase() as TerraScaleRegionCode);
}

export function isRegionCode(code: string): code is TerraScaleRegionCode {
  return findRegion(code) !== undefined;
}

export function normalizeRegionCodes(
  codes: readonly string[] | undefined,
): readonly TerraScaleRegionCode[] {
  const selected: TerraScaleRegionCode[] = [];

  for (const code of codes ?? []) {
    const region = findRegion(code);

    if (region !== undefined && !selected.includes(region.code)) {
      selected.push(region.code);
    }
  }

  return selected.length === 0 ? ["gru"] : selected;
}
