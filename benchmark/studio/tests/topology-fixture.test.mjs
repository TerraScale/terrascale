// Unit tests for the DB-07 topology fixture reads (TerraBase/TerraBaseV2#19).
//
// The topology page renders Regions, Members, Failure Domains, and per-range
// ownership with ownership epochs from typed topology reads. Before the
// replication wave lands, those reads are served by the studio dev fixture
// under the topology ticket's own route namespace (`topology.*` operations),
// populated by a synthetic-lane demo (ADR-0004 D-4: three synthetic Regions,
// no production claims). These tests state the read contract: envelope
// discipline identical to the shared operations, payload shapes in the
// engine's public report vocabulary, determinism of the synthetic lane, and
// the no-private-control-plane rule.
//
// Run with: node --test tests/topology-fixture.test.mjs
// (integration into `vp run unit:test` is a one-line vite.config.ts addition
// owned by the integrator; the studio config file is shared across the
// parallel studio tickets.)

import assert from "node:assert/strict";
import test from "node:test";

import { createFixtureServer } from "../scripts/fixture-server.mjs";
import {
  ACKNOWLEDGEMENT_LEVELS,
  BUCKET_SPACE,
  MEMBER_STATUSES,
  SEQS_PER_WRITE,
  TICKS_PER_READ,
  convergenceAt,
  recentWritesAt,
  topologyAt,
} from "../app/topology/fixtures/topology-demo.mjs";

async function withFixture(options, run) {
  const { server, state } = createFixtureServer({ host: "127.0.0.1", ...options });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address();
  const baseUrl = `http://127.0.0.1:${port}`;
  try {
    await run(baseUrl);
  } finally {
    state.dispose();
    server.closeAllConnections();
    server.close();
  }
}

async function callTopologyOperation(baseUrl, op, request = {}, overrides = {}) {
  const response = await fetch(`${baseUrl}/api/v2/operations/${op}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      v: overrides.version ?? 2,
      op: overrides.op ?? op,
      correlationId: overrides.correlationId ?? "topology-unit-1",
      causationId: null,
      idempotencyKey: null,
      request,
    }),
  });
  return { status: response.status, body: await response.json() };
}

/** Reads one operation until the synthetic lane reaches `tick`. */
async function readUntil(baseUrl, op, tick) {
  let body;
  for (;;) {
    const response = await callTopologyOperation(baseUrl, op);
    assert.equal(response.status, 200);
    body = response.body;
    if (body.result.laneTick >= tick) {
      return body.result;
    }
  }
}

test("topology.read returns the synthetic-lane topology snapshot", async () => {
  await withFixture({}, async (baseUrl) => {
    const result = (await callTopologyOperation(baseUrl, "topology.read")).body.result;

    assert.equal(result.lane, "synthetic");
    assert.equal(typeof result.laneTick, "number");
    assert.ok(result.laneTick >= TICKS_PER_READ, "each read advances the lane");
    assert.deepEqual(result.regions, ["r-alpha", "r-beta", "r-gamma"]);

    assert.equal(result.members.length, 6);
    const seated = new Set();
    for (const seat of result.members) {
      assert.ok(result.regions.includes(seat.region), `${seat.member} seats in a named Region`);
      assert.equal(typeof seat.failureDomain, "string");
      assert.ok(seat.failureDomain.length > 0);
      assert.ok(MEMBER_STATUSES.includes(seat.status), `${seat.status} is in the closed set`);
      seated.add(seat.member);
    }
    // A Failure Domain may not span Regions (engine TopologyView rule).
    const domainRegions = new Map();
    for (const seat of result.members) {
      const regions = domainRegions.get(seat.failureDomain) ?? new Set();
      regions.add(seat.region);
      domainRegions.set(seat.failureDomain, regions);
      assert.ok(regions.size === 1, `domain ${seat.failureDomain} spans one Region`);
    }

    // Bucket Range entries partition [0, 2^20) exactly, adjacent-merged, one
    // owning Member each at an ownership epoch.
    const sorted = [...result.ranges].sort((a, b) => a.start - b.start);
    assert.equal(sorted[0].start, 0);
    assert.equal(sorted.at(-1).end, BUCKET_SPACE);
    let covered = 0;
    for (let i = 0; i < sorted.length; i += 1) {
      const entry = sorted[i];
      assert.ok(entry.end > entry.start, "range is a non-empty [start, end) interval");
      assert.match(entry.shard, /^shard\/[1-9][0-9]*$/, "canonical shard identity");
      assert.ok(seated.has(entry.owner), `${entry.owner} is a seated Member`);
      assert.ok(Number.isInteger(entry.epoch) && entry.epoch >= 1, "epoch is u64 >= 1");
      covered += entry.end - entry.start;
      if (i + 1 < sorted.length) {
        assert.equal(sorted[i + 1].start, entry.end, "table partitions without gaps");
        assert.notEqual(sorted[i + 1].shard, entry.shard, "adjacent-same-shard merged");
      }
    }
    assert.equal(covered, BUCKET_SPACE, "the table covers [0, 2^20) exactly");

    assert.ok(Array.isArray(result.limits) && result.limits.length > 0);
  });
});

test("topology.recent-writes returns AcknowledgementReport facts verbatim", async () => {
  await withFixture({}, async (baseUrl) => {
    const steady = await readUntil(baseUrl, "topology.recent-writes", 102);
    assert.equal(steady.writes.length, 4, "the demo scenario writes four operations");

    for (const write of steady.writes) {
      assert.ok(ACKNOWLEDGEMENT_LEVELS.includes(write.requested));
      assert.ok(ACKNOWLEDGEMENT_LEVELS.includes(write.observed));
      assert.equal(typeof write.operation, "string");
      assert.equal(typeof write.observedAt, "number", "observedAt is a lane tick");
      assert.ok(write.normalization === null, "three Regions: no degenerate normalization");
      for (const contribution of write.contributed) {
        assert.equal(typeof contribution.region, "string");
        assert.ok(contribution.members.length > 0);
      }
    }

    const byOperation = new Map(steady.writes.map((write) => [write.operation, write]));
    const local = byOperation.get("op-1");
    assert.equal(local.requested, "local");
    assert.equal(local.observed, "local");
    assert.deepEqual(local.contributed, [{ region: "r-alpha", members: ["m-a1"] }]);

    // The scenario's all-regions request is not observed at that level: the
    // report names the weaker level actually observed.
    const allRegions = byOperation.get("op-4");
    assert.equal(allRegions.requested, "all-regions");
    assert.notEqual(allRegions.observed, "all-regions");
  });
});

test("topology.convergence returns the Convergence observation, condition, lag, definitions", async () => {
  await withFixture({}, async (baseUrl) => {
    const early = await readUntil(baseUrl, "topology.convergence", 20);
    assert.equal(
      early.convergence,
      null,
      "no Convergence observation before the scenario reaches it",
    );
    assert.equal(typeof early.condition, "string");
    assert.ok(early.condition.length > 0);
    assert.equal(typeof early.definitions.convergence, "string");
    assert.equal(typeof early.definitions.lag, "string");

    const steady = await readUntil(baseUrl, "topology.convergence", 160);
    assert.equal(steady.convergence.operation, "op-4");
    // Derived from the demo model: m-g2 stops answering catch-up from lane
    // tick 46 (stale) through 96 (unavailable, then stale recovery), so every
    // Member holds op-4 (published at 78) only from tick 100; the Convergence
    // observation follows 8 ticks later (never inferred from an ack).
    assert.equal(steady.convergence.observedAt, 108, "observedAt is a lane tick");
    // Each holding is the Member's own local publication fact.
    assert.equal(steady.convergence.holdings.length, 6);
    for (const holding of steady.convergence.holdings) {
      assert.equal(holding.level, "local");
    }

    assert.equal(steady.lag.length, 6);
    for (const entry of steady.lag) {
      assert.equal(typeof entry.member, "string");
      assert.ok(Number.isInteger(entry.sourceHighWater));
      assert.ok(Number.isInteger(entry.memberHighWater));
      assert.equal(
        entry.distance,
        Math.max(0, entry.sourceHighWater - entry.memberHighWater),
        "lag is the storage-sequence distance",
      );
      assert.equal(entry.distance, 0, `steady state: ${entry.member} caught up to the source`);
    }
    assert.ok(steady.lag.some((entry) => entry.sourceHighWater >= SEQS_PER_WRITE));
  });
});

test("the synthetic lane is deterministic and advances on reads", async () => {
  await withFixture({}, async (baseUrl) => {
    const first = (await callTopologyOperation(baseUrl, "topology.read")).body.result;
    const second = (await callTopologyOperation(baseUrl, "topology.read")).body.result;
    assert.ok(second.laneTick > first.laneTick, "each read advances the lane clock");

    // Pure functions of the lane tick: same tick, same facts.
    assert.deepEqual(topologyAt(60), topologyAt(60));
    assert.deepEqual(recentWritesAt(60), recentWritesAt(60));
    assert.deepEqual(convergenceAt(60), convergenceAt(60));
  });
});

test("member health follows the scheduled transitions (status enum unchanged)", () => {
  const statusOf = (tick, member) =>
    topologyAt(tick).members.find((seat) => seat.member === member).status;

  assert.equal(statusOf(2, "m-a1"), "assigned");
  assert.equal(statusOf(6, "m-a1"), "active");
  assert.equal(statusOf(12, "m-a1"), "responsive");
  // m-g2: stale, then unavailable, then stale, then responsive again.
  assert.equal(statusOf(50, "m-g2"), "stale");
  assert.equal(statusOf(60, "m-g2"), "unavailable");
  assert.equal(statusOf(90, "m-g2"), "stale");
  assert.equal(statusOf(100, "m-g2"), "responsive");
  // m-b2 records a rejection: degraded, then responsive again.
  assert.equal(statusOf(75, "m-b2"), "degraded");
  assert.equal(statusOf(90, "m-b2"), "responsive");
  const observed = new Set(topologyAt(160).members.map((seat) => seat.status));
  for (const status of observed) {
    assert.ok(MEMBER_STATUSES.includes(status), `${status} is in the closed set`);
  }
});

test("envelope discipline on the topology route namespace", async () => {
  await withFixture({}, async (baseUrl) => {
    const get = await fetch(`${baseUrl}/api/v2/operations/topology.read`);
    assert.equal(get.status, 405);
    assert.equal((await get.json()).error.code, "method.not-allowed");

    const badVersion = await callTopologyOperation(baseUrl, "topology.read", {}, { version: 1 });
    assert.equal(badVersion.status, 400);
    assert.equal(badVersion.body.error.code, "protocol.version-unsupported");

    const opMismatch = await callTopologyOperation(
      baseUrl,
      "topology.read",
      {},
      { op: "engine.status" },
    );
    assert.equal(opMismatch.status, 400);
    assert.equal(opMismatch.body.error.code, "protocol.envelope-invalid");

    const noCorrelation = await callTopologyOperation(
      baseUrl,
      "topology.read",
      {},
      {
        correlationId: "",
      },
    );
    assert.equal(noCorrelation.status, 400);
    assert.equal(noCorrelation.body.error.code, "protocol.envelope-invalid");

    const unknown = await callTopologyOperation(baseUrl, "topology.rebalance");
    assert.equal(unknown.status, 404);
    assert.equal(unknown.body.error.code, "operation.unknown");

    const echoed = await callTopologyOperation(
      baseUrl,
      "topology.read",
      {},
      {
        correlationId: "topology-echo-7",
      },
    );
    assert.equal(echoed.status, 200);
    assert.equal(echoed.body.correlationId, "topology-echo-7");
    assert.equal(echoed.body.op, "topology.read");
  });
});

test("the host-enforced session gate and status behaviors apply to topology reads", async () => {
  await withFixture({}, async (baseUrl) => {
    const control = (body) =>
      fetch(`${baseUrl}/api/v2/_fixture/control`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(body),
      });
    await control({ behavior: "session-gate" });
    const gated = await callTopologyOperation(baseUrl, "topology.read");
    assert.equal(gated.status, 401);
    assert.equal(gated.body.error.code, "session.unauthenticated");
    assert.equal(gated.body.error.loginUrl, "/fixture/login");

    await control({ behavior: "session-open" });
    await control({ behavior: "status-fail" });
    const failed = await callTopologyOperation(baseUrl, "topology.read");
    assert.equal(failed.status, 503);
    assert.equal(failed.body.error.code, "engine.unavailable");
    assert.equal(failed.body.error.retryable, true);
  });
});

test("no private control-plane data appears in any topology payload", async () => {
  await withFixture({}, async (baseUrl) => {
    const snapshot = await readUntil(baseUrl, "topology.read", 160);
    const writes = await readUntil(baseUrl, "topology.recent-writes", 160);
    const convergence = await readUntil(baseUrl, "topology.convergence", 160);
    const serialized = JSON.stringify({ snapshot, writes, convergence }).toLowerCase();
    // Platform provisioning, billing, routing, placement, and lifecycle are
    // control-plane concerns (CONTEXT: Control Plane) and never appear here.
    for (const banned of [
      "provision",
      "billing",
      "invoice",
      "routing",
      "placement",
      "price",
      "quota",
    ]) {
      assert.ok(!serialized.includes(banned), `payload mentions control-plane field ${banned}`);
    }
  });
});
