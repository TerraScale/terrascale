// Synthetic-lane demo fixture for the studio topology view (DB-07,
// TerraBase/TerraBaseV2#19).
//
// The topology page renders Regions, Members, Failure Domains, per-range
// ownership with ownership epochs, acknowledgement levels per recent write,
// and Convergence plus replication lag in the charter's exact vocabulary
// (CONTEXT.md). Those reads do not exist in the engine PublicOperation set
// yet (the replication data plane lands with the W4-REPLICATION packet), so
// this module populates the view from the qualification topology the
// replication seam already declares: three synthetic Regions, two synthetic
// Members per Region, each Region one Failure Domain (ADR-0004 D-4 — the
// same shape as the crate's three-Region fault lane in
// src/replication/lane.rs, mirroring `LANE_REGIONS`/`LANE_MEMBERS`).
//
// Nothing here is replication machinery and nothing here makes a production
// claim: the acknowledgement and convergence facts are fabricated demo values
// shaped exactly like the engine's public report types
// (`AcknowledgementReport`, `Convergence`, `ReplicationStatus`,
// `ReplicationCursor` lag) so the panels render the real vocabulary. The
// measurement definitions below are the engine's, copied verbatim from
// src/replication/mod.rs (guarded by tests/topology-vocabulary.test.mjs).
//
// Determinism mirrors the seam: the only clock is the synthetic lane clock,
// advanced by topology reads (never wall time), and every fact is a pure
// function of the lane tick.

/** The lane this demo reports from; the label every panel carries. */
export const LANE = "synthetic";

/** Lane ticks advanced per topology read (the demo's read-driven pump). */
export const TICKS_PER_READ = 5;

/** The fixed bucket space [0, 2^20) (src/sharding BUCKET_SPACE). */
export const BUCKET_SPACE = 1 << 20;

/** Storage sequences each demo write occupies (sequence-window spread). */
export const SEQS_PER_WRITE = 4;

/** How many recent writes the acknowledgement panel lists (bounded). */
export const RECENT_WRITES_BOUND = 4;

/** The closed acknowledgement level set, wire names of the engine enum. */
export const ACKNOWLEDGEMENT_LEVELS = ["local", "region-quorum", "majority-regions", "all-regions"];

/** The closed member health set (ReplicationStatus), wire names. */
export const MEMBER_STATUSES = [
  "assigned",
  "active",
  "responsive",
  "stale",
  "unavailable",
  "degraded",
  "unknown",
];

// Engine measurement definitions, verbatim (src/replication/mod.rs
// CONVERGENCE_DEFINITION, LAG_DEFINITION).
export const CONVERGENCE_DEFINITION =
  "The interval from the last healing event to the first Convergence observation naming every surviving Member, in lane ticks of the lane clock, together with the condition checked.";

export const LAG_DEFINITION =
  "The storage-sequence distance between a Member's durable replication cursor and the source's published sequence at observation time; a sequence distance, not a latency claim.";

/** The condition the demo's Convergence observation checked. */
export const CONVERGENCE_CONDITION =
  "every Member in the topology durably holds the operation identity (each holding is the Member's own local publication)";

/** The topology operations this fixture serves (DB-07 route namespace). */
export const TOPOLOGY_OPERATIONS = [
  "topology.read",
  "topology.recent-writes",
  "topology.convergence",
];

const TOPOLOGY_ROUTE_PREFIX = "/api/v2/operations/topology.";

// The qualification topology (ADR-0004 D-4): three synthetic Regions, six
// synthetic Members, each Region one Failure Domain, mirroring the
// three-Region fault lane's LANE_REGIONS/LANE_MEMBERS seating.
const REGIONS = ["r-alpha", "r-beta", "r-gamma"];

const SEATS = [
  { member: "m-a1", region: "r-alpha", failureDomain: "fd-r-alpha" },
  { member: "m-a2", region: "r-alpha", failureDomain: "fd-r-alpha" },
  { member: "m-b1", region: "r-beta", failureDomain: "fd-r-beta" },
  { member: "m-b2", region: "r-beta", failureDomain: "fd-r-beta" },
  { member: "m-g1", region: "r-gamma", failureDomain: "fd-r-gamma" },
  { member: "m-g2", region: "r-gamma", failureDomain: "fd-r-gamma" },
];

// Bucket Range entries: contiguous [start, end) intervals of buckets mapped
// to one Shard in canonical `shard/<number>` identity, each with one owning
// Member at its ownership epoch (the fencing fact of ADR-0004 D-1; the epoch
// value is a data-plane fact — the transition that raised it stays with the
// control plane and never appears in this view).
const RANGES = [
  { start: 0, end: 349_525, shard: "shard/1", owner: "m-a1", epoch: 1 },
  { start: 349_525, end: 743_821, shard: "shard/2", owner: "m-b1", epoch: 2 },
  { start: 743_821, end: BUCKET_SPACE, shard: "shard/3", owner: "m-g1", epoch: 3 },
];

// Member health schedule: seats activate in canonical order, then answer.
const ACTIVATE_TICKS = [4, 6, 8, 10, 12, 14];
const RESPONSIVE_OFFSET = 6;

// Fault schedule (health observations only — never a Convergence fact):
// m-g2 stops answering and cannot be reached, then recovers; m-b2 records a
// rejection and serves degraded, then recovers.
const FAULTS = [
  { member: "m-g2", kind: "stale", from: 46, to: 58 },
  { member: "m-g2", kind: "unavailable", from: 58, to: 87 },
  { member: "m-g2", kind: "stale", from: 87, to: 96 },
  { member: "m-b2", kind: "degraded", from: 70, to: 83 },
];

// The demo's replicated writes. Each entry IS the AcknowledgementReport the
// panel renders: the observed level, the Regions and Members that
// contributed, the observation lane tick, and the degenerate normalization
// (null — three Regions distinguish every level).
const WRITES = [
  {
    operation: "op-1",
    observedAt: 12,
    requested: "local",
    observed: "local",
    contributed: [{ region: "r-alpha", members: ["m-a1"] }],
    normalization: null,
  },
  {
    operation: "op-2",
    observedAt: 34,
    requested: "region-quorum",
    observed: "region-quorum",
    contributed: [{ region: "r-alpha", members: ["m-a1", "m-a2"] }],
    normalization: null,
  },
  {
    operation: "op-3",
    observedAt: 56,
    requested: "majority-regions",
    observed: "majority-regions",
    contributed: [
      { region: "r-alpha", members: ["m-a1", "m-a2"] },
      { region: "r-beta", members: ["m-b1", "m-b2"] },
    ],
    normalization: null,
  },
  {
    // Requested all-regions while one Region had no quorum and one Member
    // served degraded: the report names the weaker level actually observed.
    operation: "op-4",
    observedAt: 78,
    requested: "all-regions",
    observed: "region-quorum",
    contributed: [{ region: "r-alpha", members: ["m-a1", "m-a2"] }],
    normalization: null,
  },
];

const SNAPSHOT_LIMITS = [
  "Synthetic lane (ADR-0004 D-4): three synthetic Regions, six synthetic Members; no production claim.",
  "Member status is a health observation (ReplicationStatus); it never carries or implies a Convergence fact.",
];

const WRITES_LIMITS = [
  "Synthetic lane (ADR-0004 D-4): fabricated demo observations; no production claim.",
  "Acknowledgement Level is part of the public contract and is distinct from local receipt.",
];

const CONVERGENCE_LIMITS = [
  "Synthetic lane (ADR-0004 D-4): fabricated demo observations; no production claim.",
  "Convergence is its own observation, never inferred from an acknowledgement.",
];

/** Health precedence mirrors member_health: unavailable, then degraded, then stale. */
const FAULT_PRECEDENCE = ["unavailable", "degraded", "stale"];

function activeFault(member, tick) {
  for (const kind of FAULT_PRECEDENCE) {
    const fault = FAULTS.find(
      (entry) =>
        entry.member === member && entry.kind === kind && tick >= entry.from && tick < entry.to,
    );
    if (fault !== undefined) {
      return fault;
    }
  }
  return null;
}

function memberStatus(member, tick) {
  const fault = activeFault(member, tick);
  if (fault !== null) {
    return fault.kind;
  }
  const index = SEATS.findIndex((seat) => seat.member === member);
  if (tick >= ACTIVATE_TICKS[index] + RESPONSIVE_OFFSET) {
    return "responsive";
  }
  if (tick >= ACTIVATE_TICKS[index]) {
    return "active";
  }
  return "assigned";
}

/** Windows during which a Member stopped answering catch-up (stale or worse). */
function frozenWindows(member) {
  return FAULTS.filter(
    (fault) => fault.member === member && (fault.kind === "stale" || fault.kind === "unavailable"),
  ).map((fault) => [fault.from, fault.to]);
}

function overlapLength(from, to, [windowFrom, windowTo]) {
  return Math.max(0, Math.min(to, windowTo) - Math.max(from, windowFrom));
}

/** Sequences of one write a Member's replication cursor has durably recorded. */
function recordedSequences(member, write, tick) {
  if (tick <= write.observedAt) {
    return 0;
  }
  let elapsed = tick - write.observedAt;
  for (const window of frozenWindows(member)) {
    elapsed -= overlapLength(write.observedAt, tick, window);
  }
  return Math.max(0, Math.min(SEQS_PER_WRITE, elapsed));
}

function sourceHighWater(tick) {
  return WRITES.filter((write) => write.observedAt <= tick).length * SEQS_PER_WRITE;
}

function memberHighWater(member, tick) {
  return WRITES.reduce(
    (total, write) =>
      total + (write.observedAt <= tick ? recordedSequences(member, write, tick) : 0),
    0,
  );
}

/** The lane tick at which every Member durably holds `write` (its Convergence). */
function firstHeldTick(write) {
  let tick = write.observedAt;
  for (;;) {
    const held = SEATS.every(
      (seat) => recordedSequences(seat.member, write, tick) === SEQS_PER_WRITE,
    );
    if (held) {
      return tick;
    }
    tick += 1;
  }
}

// The Convergence observation is its own seam event (never inferred from an
// acknowledgement): the demo observes it this many lane ticks after every
// Member holds the write.
const CONVERGENCE_OBSERVATION_DELAY = 8;

/** The topology snapshot at a lane tick (TopologyView projected for display). */
export function topologyAt(tick) {
  return {
    lane: LANE,
    laneTick: tick,
    regions: [...REGIONS],
    members: SEATS.map((seat) => ({ ...seat, status: memberStatus(seat.member, tick) })),
    ranges: RANGES.map((entry) => ({ ...entry })),
    limits: [...SNAPSHOT_LIMITS],
  };
}

/** Recent writes with their AcknowledgementReport facts, newest first (bounded). */
export function recentWritesAt(tick) {
  return {
    lane: LANE,
    laneTick: tick,
    writes: WRITES.filter((write) => write.observedAt <= tick)
      .slice(-RECENT_WRITES_BOUND)
      .reverse()
      .map((write) => ({
        ...write,
        contributed: write.contributed.map((part) => ({ ...part, members: [...part.members] })),
      })),
    limits: [...WRITES_LIMITS],
  };
}

/** The latest Convergence observation, the lag table, and the definitions. */
export function convergenceAt(tick) {
  const observedAtOf = (write) => firstHeldTick(write) + CONVERGENCE_OBSERVATION_DELAY;
  const converged = WRITES.filter((write) => observedAtOf(write) <= tick);
  const latest = converged.at(-1) ?? null;
  return {
    lane: LANE,
    laneTick: tick,
    convergence:
      latest === null
        ? null
        : {
            operation: latest.operation,
            holdings: SEATS.map((seat) => ({ member: seat.member, level: "local" })),
            observedAt: observedAtOf(latest),
          },
    condition: CONVERGENCE_CONDITION,
    lag: SEATS.map((seat) => {
      const source = sourceHighWater(tick);
      const member = memberHighWater(seat.member, tick);
      return {
        member: seat.member,
        sourceHighWater: source,
        memberHighWater: member,
        distance: source - member,
      };
    }),
    definitions: { convergence: CONVERGENCE_DEFINITION, lag: LAG_DEFINITION },
    limits: [...CONVERGENCE_LIMITS],
  };
}

// --- Fixture wiring ---------------------------------------------------------
//
// The studio's shared dev-fixture script (studio/scripts/, DB-02) delegates
// its
// `topology.*` route namespace to this module. The handler keeps the shared
// envelope discipline (POST only, version check, op/correlationId echo,
// operation.unknown inside the namespace, the host-enforced session gate,
// and the status-slow/status-fail behaviors) so the topology reads behave
// exactly like every other operation of the contract.

const demoLanes = new WeakMap();

function laneTickOf(state, options) {
  let lane = demoLanes.get(state);
  if (lane === undefined) {
    lane = { tick: 0 };
    demoLanes.set(state, lane);
  }
  const step = options.topologyTicksPerRead ?? TICKS_PER_READ;
  lane.tick += step;
  return lane.tick;
}

/**
 * Handles one request under the topology route namespace.
 *
 * `request` carries the method and pathname; `context` carries the fixture
 * request/response pair, the fixture state (session gate, status behaviors),
 * the resolved options, and the fixture's envelope helpers. Returns true when
 * the request was answered; false when the path is outside the namespace (the
 * fixture's shared routing then applies unchanged).
 */
export async function handleTopologyOperation(request, context) {
  const { res, state, options, helpers, req } = context;
  if (!request.pathname.startsWith(TOPOLOGY_ROUTE_PREFIX)) {
    return false;
  }
  const op = request.pathname.slice("/api/v2/operations/".length);

  if (request.method !== "POST") {
    helpers.sendJson(res, 405, {
      v: 2,
      op,
      correlationId: null,
      error: {
        code: "method.not-allowed",
        message: "Operations accept POST only.",
        retryable: false,
      },
    });
    return true;
  }

  let body;
  try {
    body = JSON.parse((await helpers.readBody(req)) || "null");
  } catch {
    const failure = helpers.errorEnvelope(
      op,
      null,
      "protocol.envelope-invalid",
      "Request body is not JSON.",
      false,
      400,
    );
    helpers.sendJson(res, failure.status, failure.body);
    return true;
  }

  const correlationId = body?.correlationId;
  if (body?.v !== 2) {
    const failure = helpers.errorEnvelope(
      op,
      typeof correlationId === "string" ? correlationId : null,
      "protocol.version-unsupported",
      `Envelope version ${JSON.stringify(body?.v)} is not supported; this host speaks v2.`,
      false,
      400,
    );
    helpers.sendJson(res, failure.status, failure.body);
    return true;
  }
  if (typeof correlationId !== "string" || correlationId.length === 0 || body.op !== op) {
    const failure = helpers.errorEnvelope(
      op,
      typeof correlationId === "string" ? correlationId : null,
      "protocol.envelope-invalid",
      "Envelope requires v, op matching the route, and a non-empty correlationId.",
      false,
      400,
    );
    helpers.sendJson(res, failure.status, failure.body);
    return true;
  }

  if (!TOPOLOGY_OPERATIONS.includes(op)) {
    const failure = helpers.errorEnvelope(
      op,
      correlationId,
      "operation.unknown",
      `Operation '${op}' is not part of terrabase.api.v2.`,
      false,
      404,
    );
    helpers.sendJson(res, failure.status, failure.body);
    return true;
  }

  if (state.sessionGate) {
    const failure = helpers.errorEnvelope(
      op,
      correlationId,
      "session.unauthenticated",
      "The host requires an operator session (fixture behavior: session-gate).",
      false,
      401,
      { loginUrl: "/fixture/login" },
    );
    helpers.sendJson(res, failure.status, failure.body);
    return true;
  }

  if (state.statusBehavior === "status-fail") {
    const failure = helpers.errorEnvelope(
      op,
      correlationId,
      "engine.unavailable",
      "The engine is not reachable (fixture behavior: status-fail).",
      true,
      503,
    );
    helpers.sendJson(res, failure.status, failure.body);
    return true;
  }

  if (state.statusBehavior === "status-slow") {
    await new Promise((resolve) => setTimeout(resolve, options.statusDelayMs));
  }

  const tick = laneTickOf(state, options);
  const result =
    op === "topology.read"
      ? topologyAt(tick)
      : op === "topology.recent-writes"
        ? recentWritesAt(tick)
        : convergenceAt(tick);
  helpers.sendJson(res, 200, { v: 2, op, correlationId, result });
  return true;
}
