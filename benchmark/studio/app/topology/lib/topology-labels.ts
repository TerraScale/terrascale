// Display labels for the topology view (DB-07).
//
// Labels are the engine's variant names rendered verbatim (Local,
// RegionQuorum, MajorityRegions, AllRegions; Assigned..Unknown), so the
// panels never paraphrase the public report types. Tone marks always ship
// with the text label — color is never the identity of a status (the studio
// design-token rule).

import type { AcknowledgementLevelWire, MemberStatusWire } from "./topology-types";

/** Acknowledgement Level labels: engine `AcknowledgementLevel` variant names. */
export const ACK_LEVEL_LABEL: ReadonlyMap<AcknowledgementLevelWire, string> = new Map([
  ["local", "Local"],
  ["region-quorum", "RegionQuorum"],
  ["majority-regions", "MajorityRegions"],
  ["all-regions", "AllRegions"],
]);

/** Member status labels: engine `ReplicationStatus` variant names. */
export const STATUS_LABEL: ReadonlyMap<MemberStatusWire, string> = new Map([
  ["assigned", "Assigned"],
  ["active", "Active"],
  ["responsive", "Responsive"],
  ["stale", "Stale"],
  ["unavailable", "Unavailable"],
  ["degraded", "Degraded"],
  ["unknown", "Unknown"],
]);

/** Status tone: live (serving), warn (not yet or behind), critical (unreachable). */
export const STATUS_TONE: ReadonlyMap<MemberStatusWire, "live" | "warn" | "critical"> = new Map([
  ["assigned", "warn"],
  ["active", "live"],
  ["responsive", "live"],
  ["stale", "warn"],
  ["unavailable", "critical"],
  ["degraded", "warn"],
  ["unknown", "warn"],
]);
