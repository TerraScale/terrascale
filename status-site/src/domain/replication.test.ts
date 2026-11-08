import { describe, expect, it } from "vitest";
import type { Observation } from "./assertions";
import { judgeReplication } from "./replication";

const peer = (region = "future-region", overrides: Record<string, unknown> = {}) => ({
  region,
  status: "ok",
  lagSeconds: 4,
  ...overrides,
});
const report = (overrides: Record<string, unknown> = {}) => ({
  region: "observer",
  status: "ok",
  maxLagSeconds: 4,
  peers: [peer()],
  ...overrides,
});
const observed = (body: unknown): Observation => ({ status: 200, error: null, body: JSON.stringify(body) });

describe("judgeReplication", () => {
  it("accepts omitted or null scope and extracts the aggregate lag", () => {
    const observation = observed(report());
    for (const scope of [undefined, null]) {
      expect(judgeReplication(observation, scope)).toEqual({ outcome: "ok", error: null, values: { lag: 4 } });
    }
  });

  it("discovers arbitrary peers and checks every reported peer", () => {
    const peers = [peer("new-continent"), peer("another-future-region", { status: "degraded" })];
    expect(judgeReplication(observed(report({ peers })))).toMatchObject({
      outcome: "fail",
      error: expect.stringContaining("another-future-region"),
    });
    peers[1] = peer("another-future-region");
    expect(judgeReplication(observed(report({ peers }))).outcome).toBe("ok");
  });

  it.each([0, 30])("accepts inclusive lag boundary %s", (lag) => {
    expect(
      judgeReplication(observed(report({ peers: [peer("future-region", { lagSeconds: lag })], maxLagSeconds: lag })))
        .outcome,
    ).toBe("ok");
  });

  it.each([undefined, null, -0.01, 30.01, "4", true, {}, []])("rejects invalid peer lag %j", (lagSeconds) => {
    for (const scope of [undefined, "FUTURE-REGION"]) {
      expect(
        judgeReplication(observed(report({ peers: [peer("future-region", { lagSeconds })] })), scope),
      ).toMatchObject({
        outcome: "fail",
        error: expect.stringContaining("future-region"),
      });
    }
  });

  it.each([undefined, null, -1, 30.01, "4", true, {}, []])("rejects invalid aggregate lag %j", (maxLagSeconds) => {
    expect(judgeReplication(observed(report({ maxLagSeconds })))).toMatchObject({
      outcome: "fail",
      error: expect.stringContaining("maxLagSeconds"),
    });
  });

  it.each(["1e400", "-1e400"])("rejects nonfinite JSON numbers %s in peers and aggregate", (number) => {
    const body = JSON.stringify(report());
    for (const invalid of [
      body.replace('"lagSeconds":4', `"lagSeconds":${number}`),
      body.replace('"maxLagSeconds":4', `"maxLagSeconds":${number}`),
    ]) {
      expect(judgeReplication({ status: 200, error: null, body: invalid }).outcome).toBe("fail");
    }
  });

  it("requires the aggregate to cover peer lag without requiring float equality", () => {
    expect(judgeReplication(observed(report({ maxLagSeconds: 3.99 }))).outcome).toBe("fail");
    expect(judgeReplication(observed(report({ maxLagSeconds: 4.001 }))).outcome).toBe("ok");
  });

  it.each([null, 0])("accepts native empty topology with aggregate %j", (maxLagSeconds) => {
    expect(judgeReplication(observed(report({ peers: [], maxLagSeconds })))).toEqual({
      outcome: "ok",
      error: null,
      values: { lag: maxLagSeconds },
    });
  });

  it.each([undefined, 1, -1, "0"])("rejects invalid empty-topology aggregate %j", (maxLagSeconds) => {
    expect(judgeReplication(observed(report({ peers: [], maxLagSeconds }))).outcome).toBe("fail");
  });

  it.each([undefined, null, "degraded", "lagging", "OK"])("requires reported peer status ok, received %j", (status) => {
    for (const scope of [undefined, "future-region"]) {
      expect(judgeReplication(observed(report({ peers: [peer("future-region", { status })] })), scope)).toMatchObject({
        outcome: "fail",
        error: expect.stringContaining("future-region"),
      });
    }
  });

  it.each([undefined, null, "degraded", "OK"])("requires root status ok, received %j", (status) => {
    expect(judgeReplication(observed(report({ status }))).outcome).toBe("fail");
  });

  it.each([undefined, null, {}, "peers", 1])("rejects missing or malformed peers %j for all scopes", (peers) => {
    for (const scope of [undefined, "future-region", "unreported"]) {
      expect(judgeReplication(observed(report({ peers })), scope)).toMatchObject({
        outcome: "fail",
        error: expect.stringContaining("peers"),
      });
    }
  });

  it.each([null, [], 1, "peer", {}, { region: "" }, { region: "  " }, { region: 1 }])(
    "rejects malformed peer %j",
    (invalid) => {
      expect(judgeReplication(observed(report({ peers: [peer(), invalid] })), "future-region").outcome).toBe("fail");
    },
  );

  it("rejects duplicate and self peers case insensitively for every scope", () => {
    for (const peers of [
      [peer(), peer("FUTURE-REGION")],
      [peer(), peer("OBSERVER")],
    ]) {
      for (const scope of [undefined, "future-region", "unreported"]) {
        expect(judgeReplication(observed(report({ peers })), scope).outcome).toBe("fail");
      }
    }
  });

  it("judges a selected peer independently of root aggregate and other peers' health", () => {
    const observation = observed(
      report({
        status: "degraded",
        maxLagSeconds: null,
        peers: [peer(), peer("broken-peer", { status: "degraded", lagSeconds: undefined })],
      }),
    );
    expect(judgeReplication(observation, "FUTURE-REGION")).toEqual({ outcome: "ok", error: null, values: { lag: 4 } });
    expect(judgeReplication(observation, "broken-peer").outcome).toBe("fail");
    expect(judgeReplication(observation).outcome).toBe("fail");
  });

  it("validates the full observer report when scope matches the root region", () => {
    expect(judgeReplication(observed(report()), "OBSERVER").outcome).toBe("ok");
    for (const overrides of [
      { status: "degraded" },
      { maxLagSeconds: null },
      { peers: [peer("bad", { lagSeconds: 31 })] },
    ]) {
      expect(judgeReplication(observed(report(overrides)), "OBSERVER").outcome).toBe("fail");
    }
  });

  it("returns Not reported only for a region absent from a valid report", () => {
    for (const peers of [[peer()], []]) {
      expect(judgeReplication(observed(report({ peers })), "unreported")).toEqual({
        outcome: "unknown",
        error: "Not reported",
        values: {},
      });
    }
  });

  it.each([null, [], true, "report", {}, { region: "" }, { region: " " }, { region: 1 }])(
    "rejects malformed report %j",
    (body) => {
      expect(judgeReplication(observed(body)).outcome).toBe("fail");
    },
  );

  it.each([undefined, "", "not json", "{"])("rejects malformed JSON %j", (body) => {
    expect(judgeReplication({ status: 200, error: null, body })).toMatchObject({
      outcome: "fail",
      error: "Invalid JSON",
    });
    expect(judgeReplication({ status: 200, error: null, body }, "future-region").outcome).toBe("unknown");
  });

  it.each([201, 204, 301, 401, 403, 500, 503])("honors HTTP %s even with a healthy report body", (status) => {
    const observation = { ...observed(report()), status };
    expect(judgeReplication(observation)).toEqual({ outcome: "fail", error: `HTTP ${status}`, values: {} });
    for (const scope of ["future-region", "observer"]) {
      expect(judgeReplication(observation, scope)).toEqual({ outcome: "unknown", error: `HTTP ${status}`, values: {} });
    }
  });

  it("treats an unreachable observer as root failure and regional uncertainty", () => {
    for (const error of [null, "Timed out"]) {
      const observation = { status: null, error };
      expect(judgeReplication(observation)).toEqual({ outcome: "fail", error: error ?? "Unreachable", values: {} });
      expect(judgeReplication(observation, "future-region")).toEqual({
        outcome: "unknown",
        error: error ?? "Unreachable",
        values: {},
      });
    }
  });
});
