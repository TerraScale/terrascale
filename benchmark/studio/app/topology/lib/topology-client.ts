// Typed topology reads (DB-07, TerraScale/TerraBaseV2#19).
//
// Speaks the same `terrabase.api.v2` envelope discipline as the generated
// typed client (v2 envelope, POST per operation, correlationId echo, typed
// errors, fail-closed responses) against the topology ticket's route
// namespace (`topology.*`). Reuses `TerrabaseApiError` so typed failures
// reach the same error routes the rest of the studio renders. When the
// contract gains the topology operations at integration, these reads fold
// into the generated client and this module shrinks to re-exports.

import { TerrabaseApiError } from "@/api/generated/client";

import {
  ACKNOWLEDGEMENT_LEVELS,
  MEMBER_STATUSES,
  type AcknowledgementLevelWire,
  type AcknowledgementReportWire,
  type ConvergenceReport,
  type ConvergenceWire,
  type MemberLag,
  type MemberStatusWire,
  type RecentWritesReport,
  type RegionContribution,
  type RangeOwnership,
  type TopologyMemberSeat,
  type TopologySnapshot,
} from "./topology-types";

export interface TopologyClientOptions {
  readonly baseUrl: string;
  readonly fetchImpl?: typeof fetch;
  readonly timeoutMs?: number;
  /** Test seam: clock for the request timeout. */
  readonly timers?: Pick<typeof globalThis, "setTimeout" | "clearTimeout">;
}

const DEFAULT_TIMEOUT_MS = 8000;

interface Envelope {
  readonly v: number;
  readonly op: string;
  readonly correlationId: string;
  readonly result?: unknown;
  readonly error?: {
    readonly code?: unknown;
    readonly message?: unknown;
    readonly retryable?: unknown;
    readonly loginUrl?: unknown;
  };
}

async function callTopologyOperation<TRead>(
  options: TopologyClientOptions,
  op: string,
  validate: (value: unknown) => TRead,
): Promise<TRead> {
  const correlationId = crypto.randomUUID();
  const envelope = {
    v: 2,
    op,
    correlationId,
    causationId: null,
    idempotencyKey: null,
    request: {},
  };
  const doFetch = options.fetchImpl ?? fetch;
  const timers = options.timers ?? globalThis;
  const timeoutMs = options.timeoutMs ?? DEFAULT_TIMEOUT_MS;
  const controller = new AbortController();
  const timeout = timers.setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await doFetch(
      `${options.baseUrl.replace(/\/$/, "")}/api/v2/operations/${op}`,
      {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(envelope),
        signal: controller.signal,
        credentials: "omit",
      },
    );
    const body = (await response.json()) as Envelope;
    if (body.v !== 2) {
      throw new TerrabaseApiError(
        "protocol.version-unsupported",
        `Response for ${op} carries envelope version ${JSON.stringify(body.v)}.`,
        { status: response.status, correlationId },
      );
    }
    if (body.correlationId !== correlationId) {
      throw new TerrabaseApiError(
        "protocol.correlation-mismatch",
        `Response for ${op} did not echo the request correlationId.`,
        { status: response.status, correlationId },
      );
    }
    if (body.error !== undefined && body.error !== null) {
      const error = body.error;
      throw new TerrabaseApiError(
        typeof error.code === "string" ? error.code : "error.code-missing",
        typeof error.message === "string" ? error.message : "The host returned an untyped error.",
        {
          retryable: error.retryable === true,
          status: response.status,
          correlationId,
          loginUrl: typeof error.loginUrl === "string" ? error.loginUrl : null,
        },
      );
    }
    if (response.status < 200 || response.status >= 300) {
      throw new TerrabaseApiError(
        "protocol.status-invalid",
        `Unexpected HTTP ${response.status} for ${op}.`,
        { status: response.status, correlationId },
      );
    }
    try {
      return validate(body.result);
    } catch (cause) {
      throw new TerrabaseApiError(
        "topology.payload-invalid",
        `Response for ${op} is not a valid topology read: ${String(cause)}`,
        { status: response.status, correlationId },
      );
    }
  } catch (cause) {
    if (cause instanceof TerrabaseApiError) {
      throw cause;
    }
    const aborted = cause instanceof Error && cause.name === "AbortError";
    throw new TerrabaseApiError(
      aborted ? "transport.timeout" : "transport.unreachable",
      aborted
        ? `Request for ${op} timed out after ${timeoutMs}ms.`
        : `Request for ${op} failed: ${String(cause)}`,
      { retryable: true, correlationId },
    );
  } finally {
    timers.clearTimeout(timeout);
  }
}

// Fail-closed response validation: every field is checked against the wire
// contract; anything else rejects the whole read rather than rendering a
// half-typed view.

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function stringOf(value: unknown, field: string): string {
  if (typeof value !== "string" || value.length === 0) {
    throw new Error(`${field} is not a non-empty string`);
  }
  return value;
}

function integerOf(value: unknown, field: string, minimum = 0): number {
  if (typeof value !== "number" || !Number.isInteger(value) || value < minimum) {
    throw new Error(`${field} is not an integer >= ${minimum}`);
  }
  return value;
}

function arrayOf(value: unknown, field: string): unknown[] {
  if (!Array.isArray(value)) {
    throw new Error(`${field} is not an array`);
  }
  return value;
}

function stringArrayOf(value: unknown, field: string): string[] {
  return arrayOf(value, field).map((entry, index) => stringOf(entry, `${field}[${index}]`));
}

function levelOf(value: unknown, field: string): AcknowledgementLevelWire {
  const levels: readonly string[] = ACKNOWLEDGEMENT_LEVELS;
  if (typeof value !== "string" || !levels.includes(value)) {
    throw new Error(`${field} is not a known acknowledgement level`);
  }
  return value;
}

function statusOf(value: unknown, field: string): MemberStatusWire {
  const statuses: readonly string[] = MEMBER_STATUSES;
  if (typeof value !== "string" || !statuses.includes(value)) {
    throw new Error(`${field} is not a known member status`);
  }
  return value;
}

function laneOf(value: unknown): "synthetic" {
  if (value !== "synthetic") {
    throw new Error("lane is not 'synthetic'");
  }
  return "synthetic";
}

function memberSeatOf(value: unknown, index: number): TopologyMemberSeat {
  if (!isRecord(value)) {
    throw new Error(`members[${index}] is not an object`);
  }
  return {
    member: stringOf(value.member, `members[${index}].member`),
    region: stringOf(value.region, `members[${index}].region`),
    failureDomain: stringOf(value.failureDomain, `members[${index}].failureDomain`),
    status: statusOf(value.status, `members[${index}].status`),
  };
}

function rangeOf(value: unknown, index: number): RangeOwnership {
  if (!isRecord(value)) {
    throw new Error(`ranges[${index}] is not an object`);
  }
  const start = integerOf(value.start, `ranges[${index}].start`);
  const end = integerOf(value.end, `ranges[${index}].end`);
  if (end <= start) {
    throw new Error(`ranges[${index}] is not a non-empty [start, end) interval`);
  }
  const shard = stringOf(value.shard, `ranges[${index}].shard`);
  if (!/^shard\/[1-9][0-9]*$/.test(shard)) {
    throw new Error(`ranges[${index}].shard is not a canonical shard identity`);
  }
  return {
    start,
    end,
    shard,
    owner: stringOf(value.owner, `ranges[${index}].owner`),
    epoch: integerOf(value.epoch, `ranges[${index}].epoch`, 1),
  };
}

function contributionOf(value: unknown, index: number): RegionContribution {
  if (!isRecord(value)) {
    throw new Error(`contributed[${index}] is not an object`);
  }
  return {
    region: stringOf(value.region, `contributed[${index}].region`),
    members: stringArrayOf(value.members, `contributed[${index}].members`),
  };
}

function acknowledgementOf(value: unknown, index: number): AcknowledgementReportWire {
  if (!isRecord(value)) {
    throw new Error(`writes[${index}] is not an object`);
  }
  let normalization: AcknowledgementReportWire["normalization"] = null;
  if (value.normalization !== null && value.normalization !== undefined) {
    const pair = arrayOf(value.normalization, `writes[${index}].normalization`);
    if (pair.length !== 2) {
      throw new Error(`writes[${index}].normalization is not a level pair`);
    }
    normalization = [
      levelOf(pair[0], `writes[${index}].normalization[0]`),
      levelOf(pair[1], `writes[${index}].normalization[1]`),
    ];
  }
  return {
    operation: stringOf(value.operation, `writes[${index}].operation`),
    requested: levelOf(value.requested, `writes[${index}].requested`),
    observed: levelOf(value.observed, `writes[${index}].observed`),
    contributed: arrayOf(value.contributed, `writes[${index}].contributed`).map(contributionOf),
    observedAt: integerOf(value.observedAt, `writes[${index}].observedAt`),
    normalization,
  };
}

function convergenceOf(value: unknown): ConvergenceWire {
  if (!isRecord(value)) {
    throw new Error("convergence is not an object");
  }
  return {
    operation: stringOf(value.operation, "convergence.operation"),
    holdings: arrayOf(value.holdings, "convergence.holdings").map((entry, index) => {
      if (!isRecord(entry)) {
        throw new Error(`convergence.holdings[${index}] is not an object`);
      }
      return {
        member: stringOf(entry.member, `convergence.holdings[${index}].member`),
        level: levelOf(entry.level, `convergence.holdings[${index}].level`),
      };
    }),
    observedAt: integerOf(value.observedAt, "convergence.observedAt"),
  };
}

function lagEntryOf(value: unknown, index: number): MemberLag {
  if (!isRecord(value)) {
    throw new Error(`lag[${index}] is not an object`);
  }
  const sourceHighWater = integerOf(value.sourceHighWater, `lag[${index}].sourceHighWater`);
  const memberHighWater = integerOf(value.memberHighWater, `lag[${index}].memberHighWater`);
  const distance = integerOf(value.distance, `lag[${index}].distance`);
  if (distance !== Math.max(0, sourceHighWater - memberHighWater)) {
    throw new Error(`lag[${index}].distance is not the storage-sequence distance`);
  }
  return {
    member: stringOf(value.member, `lag[${index}].member`),
    sourceHighWater,
    memberHighWater,
    distance,
  };
}

function validateSnapshot(value: unknown): TopologySnapshot {
  if (!isRecord(value)) {
    throw new Error("result is not an object");
  }
  return {
    lane: laneOf(value.lane),
    laneTick: integerOf(value.laneTick, "laneTick"),
    regions: stringArrayOf(value.regions, "regions"),
    members: arrayOf(value.members, "members").map(memberSeatOf),
    ranges: arrayOf(value.ranges, "ranges").map(rangeOf),
    limits: stringArrayOf(value.limits, "limits"),
  };
}

function validateRecentWrites(value: unknown): RecentWritesReport {
  if (!isRecord(value)) {
    throw new Error("result is not an object");
  }
  return {
    lane: laneOf(value.lane),
    laneTick: integerOf(value.laneTick, "laneTick"),
    writes: arrayOf(value.writes, "writes").map(acknowledgementOf),
    limits: stringArrayOf(value.limits, "limits"),
  };
}

function validateConvergence(value: unknown): ConvergenceReport {
  if (!isRecord(value)) {
    throw new Error("result is not an object");
  }
  return {
    lane: laneOf(value.lane),
    laneTick: integerOf(value.laneTick, "laneTick"),
    convergence:
      value.convergence === null || value.convergence === undefined
        ? null
        : convergenceOf(value.convergence),
    condition: stringOf(value.condition, "condition"),
    lag: arrayOf(value.lag, "lag").map(lagEntryOf),
    definitions: (() => {
      if (!isRecord(value.definitions)) {
        throw new Error("definitions is not an object");
      }
      return {
        convergence: stringOf(value.definitions.convergence, "definitions.convergence"),
        lag: stringOf(value.definitions.lag, "definitions.lag"),
      };
    })(),
    limits: stringArrayOf(value.limits, "limits"),
  };
}

/** Typed topology reads against the API host's topology route namespace. */
export class TopologyClient {
  readonly #options: TopologyClientOptions;

  constructor(options: TopologyClientOptions) {
    this.#options = options;
  }

  get baseUrl(): string {
    return this.#options.baseUrl;
  }

  /** The topology snapshot: Regions, Members, Failure Domains, ownership. */
  readTopology(): Promise<TopologySnapshot> {
    return callTopologyOperation(this.#options, "topology.read", validateSnapshot);
  }

  /** Recent writes with their acknowledgement facts (bounded, newest first). */
  readRecentWrites(): Promise<RecentWritesReport> {
    return callTopologyOperation(this.#options, "topology.recent-writes", validateRecentWrites);
  }

  /** The latest Convergence observation, the lag table, and the definitions. */
  readConvergence(): Promise<ConvergenceReport> {
    return callTopologyOperation(this.#options, "topology.convergence", validateConvergence);
  }
}
