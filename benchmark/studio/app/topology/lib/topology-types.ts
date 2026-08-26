// Wire types for the topology reads (DB-07, TerraBase/TerraBaseV2#19).
//
// These mirror the engine's public replication report types verbatim
// (src/replication/mod.rs, re-exported from src/lib.rs): the shapes and field
// names follow `TopologyView` (Regions, Member seats with Failure Domains,
// per-range ownership with ownership epochs), `AcknowledgementReport`,
// `Convergence`, `ReplicationStatus`, and the `ReplicationCursor` lag fact.
// The wire names of the closed enums are the engine variant names in
// lowercase-hyphen form (`MajorityRegions` -> `majority-regions`), the same
// convention the studio contract uses for its enums.
//
// Pre-landing note: the engine PublicOperation set does not carry topology
// reads yet, so these travel on the topology ticket's own fixture route
// namespace (`topology.*`) until the contract and the generated typed client
// gain them at integration (see fixtures/topology-contract.fragment.json).
// The shapes here are the ones that fragment declares.

/** Acknowledgement Level (engine `AcknowledgementLevel`), wire names. */
export type AcknowledgementLevelWire =
  | "local"
  | "region-quorum"
  | "majority-regions"
  | "all-regions";

/** Member health (engine `ReplicationStatus`), wire names. The set is closed. */
export type MemberStatusWire =
  | "assigned"
  | "active"
  | "responsive"
  | "stale"
  | "unavailable"
  | "degraded"
  | "unknown";

/** The lane a report comes from; the fixture reports from the synthetic lane. */
export type TopologyLane = "synthetic";

/** One Member's seat in the topology: its Region, Failure Domain, and health. */
export interface TopologyMemberSeat {
  readonly member: string;
  readonly region: string;
  readonly failureDomain: string;
  readonly status: MemberStatusWire;
}

/** One Bucket Range entry with its owning Member at its ownership epoch. */
export interface RangeOwnership {
  readonly start: number;
  readonly end: number;
  readonly shard: string;
  readonly owner: string;
  readonly epoch: number;
}

/** The topology snapshot: `TopologyView` projected for display. */
export interface TopologySnapshot {
  readonly lane: TopologyLane;
  readonly laneTick: number;
  readonly regions: readonly string[];
  readonly members: readonly TopologyMemberSeat[];
  readonly ranges: readonly RangeOwnership[];
  readonly limits: readonly string[];
}

/** One Region and the Members that contributed to an acknowledgement. */
export interface RegionContribution {
  readonly region: string;
  readonly members: readonly string[];
}

/**
 * The observed acknowledgement for one replicated operation — the engine's
 * `AcknowledgementReport` verbatim: requested and observed levels, the
 * contributing Regions with their Members, the observation lane tick, and the
 * degenerate normalization (null when the topology distinguishes the levels).
 */
export interface AcknowledgementReportWire {
  readonly operation: string;
  readonly requested: AcknowledgementLevelWire;
  readonly observed: AcknowledgementLevelWire;
  readonly contributed: readonly RegionContribution[];
  readonly observedAt: number;
  readonly normalization: readonly [AcknowledgementLevelWire, AcknowledgementLevelWire] | null;
}

/** Recent writes with their acknowledgement facts, newest first (bounded). */
export interface RecentWritesReport {
  readonly lane: TopologyLane;
  readonly laneTick: number;
  readonly writes: readonly AcknowledgementReportWire[];
  readonly limits: readonly string[];
}

/** One Member's holding of a converged operation (its own local publication). */
export interface ConvergenceHolding {
  readonly member: string;
  readonly level: AcknowledgementLevelWire;
}

/** The engine's `Convergence` observation, verbatim. */
export interface ConvergenceWire {
  readonly operation: string;
  readonly holdings: readonly ConvergenceHolding[];
  readonly observedAt: number;
}

/** The storage-sequence distance between one Member's replication cursor and the source. */
export interface MemberLag {
  readonly member: string;
  readonly sourceHighWater: number;
  readonly memberHighWater: number;
  readonly distance: number;
}

/** The convergence report: observation, condition, lag table, definitions, limits. */
export interface ConvergenceReport {
  readonly lane: TopologyLane;
  readonly laneTick: number;
  readonly convergence: ConvergenceWire | null;
  readonly condition: string;
  readonly lag: readonly MemberLag[];
  readonly definitions: { readonly convergence: string; readonly lag: string };
  readonly limits: readonly string[];
}

/** The closed acknowledgement level set, wire names (engine enum order). */
export const ACKNOWLEDGEMENT_LEVELS: readonly AcknowledgementLevelWire[] = [
  "local",
  "region-quorum",
  "majority-regions",
  "all-regions",
];

/** The closed member health set, wire names (engine enum order). */
export const MEMBER_STATUSES: readonly MemberStatusWire[] = [
  "assigned",
  "active",
  "responsive",
  "stale",
  "unavailable",
  "degraded",
  "unknown",
];
