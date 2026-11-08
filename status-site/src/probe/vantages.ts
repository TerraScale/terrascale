/* US West and South America use explicitly placed private Workers. Europe
   and Asia Pacific retain their Durable Objects. Every answer must still
   prove its actual data center; placement alone never proves coverage. */

import type { Env } from "../env";

export interface Vantage {
  code: "sfo" | "gru" | "fra" | "nrt";
  name: string;
  /** Explicit Worker region or Durable Object location hint. */
  placement: DurableObjectLocationHint | "aws:us-west-1" | "aws:sa-east-1";
  /** Data centers that count as this vantage's area. An answer from anywhere
      else is a coverage failure, never relabeled as this vantage.
      shortcut: hand-kept IATA lists, review only for actual regional colos. */
  colos: readonly string[];
  /** Position in region map viewBox units (MAP_WIDTH × MAP_HEIGHT in
      views/charts/geometry), just off the hub of the region it sits in. */
  mapX: number;
  mapY: number;
  /** The region hub it sits beside, which gets no probe path from it. */
  region: string | null;
}

export const VANTAGES: readonly Vantage[] = [
  {
    code: "sfo",
    name: "US West",
    placement: "aws:us-west-1",
    colos: ["SJC", "SFO", "LAX", "SEA", "PDX"],
    mapX: 18.8,
    mapY: 31.4,
    region: "SJC",
  },
  {
    code: "gru",
    name: "South America",
    placement: "aws:sa-east-1",
    colos: ["GRU", "GIG", "VCP", "EZE", "SCL", "BOG", "LIM"],
    mapX: 47.4,
    mapY: 62.4,
    region: "GRU",
  },
  {
    code: "fra",
    name: "Europe",
    placement: "weur",
    colos: [
      "FRA",
      "AMS",
      "CDG",
      "LHR",
      "MUC",
      "DUS",
      "HAM",
      "ZRH",
      "VIE",
      "WAW",
      "PRG",
      "ARN",
      "CPH",
      "MAD",
      "MXP",
      "BRU",
      "DUB",
    ],
    mapX: 69.3,
    mapY: 23.6,
    region: "FRA",
  },
  {
    code: "nrt",
    name: "Asia Pacific",
    placement: "apac",
    colos: ["NRT", "KIX", "HND", "ICN", "HKG", "TPE", "SIN"],
    mapX: 120.5,
    mapY: 29.8,
    region: null,
  },
];

export function vantageStub(
  env: Pick<Env, "VANTAGE" | "VANTAGE_SFO" | "VANTAGE_GRU">,
  vantage: Pick<Vantage, "code" | "placement">,
) {
  if (vantage.placement === "aws:us-west-1") return env.VANTAGE_SFO;
  if (vantage.placement === "aws:sa-east-1") return env.VANTAGE_GRU;
  return env.VANTAGE.get(env.VANTAGE.idFromName(vantage.code), { locationHint: vantage.placement });
}
