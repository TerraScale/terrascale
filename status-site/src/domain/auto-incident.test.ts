import { describe, expect, it } from "vitest";
import { adopt, step, type Observation, type Tracker } from "./auto-incident";

const M = 60_000;
const seen = (state: Observation["state"], suppressed = false): Observation => ({
  componentId: "01M4E7G1W82VKFPF5FJ9AVK1CV",
  name: "Dashboard",
  state,
  detail: "4 of 4 vantage points failing: HTTP 503",
  suppressed,
});

/** Runs observations 30 seconds apart and collects every action. */
function run(states: Observation[], options: { touched?: Set<string>; start?: Record<string, Tracker> } = {}) {
  let trackers = options.start ?? {};
  let ids = 0;
  const actions = states.flatMap((observation, index) => {
    const result = step(trackers, [observation], index * 30_000, {
      newId: () => `0000000000${String(++ids + 1).padStart(16, "0")}`,
      touched: options.touched ?? new Set(),
    });
    trackers = result.trackers;
    return result.actions.map((action) => ({ at: index * 30_000, ...action }));
  });
  return { actions, trackers };
}

describe("step", () => {
  it("opens after a minute down, monitors on recovery and resolves after five healthy minutes", () => {
    const { actions, trackers } = run([
      ...Array.from({ length: 3 }, () => seen("major_outage")),
      ...Array.from({ length: 12 }, () => seen("operational")),
    ]);
    expect(actions.map((action) => [action.at / 1000, action.type, "status" in action ? action.status : ""])).toEqual([
      [60, "open", ""],
      [90, "update", "monitoring"],
      [390, "resolve", ""],
    ]);
    expect(actions[0]).toMatchObject({ title: "Dashboard is down", impact: "critical" });
    expect(trackers).toEqual({});
  });

  it("ignores blips shorter than the threshold", () => {
    expect(run([seen("major_outage"), seen("operational"), seen("major_outage"), seen("operational")]).actions).toEqual(
      [],
    );
  });

  it("waits ten minutes before opening a degraded incident, then raises its impact", () => {
    const states = [...Array.from({ length: 21 }, () => seen("degraded")), seen("major_outage")];
    const { actions } = run(states);
    expect(actions.map((action) => [action.at / M, action.type])).toEqual([
      [10, "open"],
      [10.5, "update"],
    ]);
    expect(actions[0]).toMatchObject({ impact: "minor", title: "Dashboard is degraded" });
    expect(actions[1]).toMatchObject({ status: "investigating", impact: "critical" });
  });

  it("returns to investigating when checks fail again while monitoring", () => {
    const { actions } = run([
      seen("major_outage"),
      seen("major_outage"),
      seen("major_outage"),
      seen("operational"),
      seen("major_outage"),
    ]);
    expect(actions.map((action) => ("status" in action ? action.status : action.type))).toEqual([
      "open",
      "monitoring",
      "investigating",
    ]);
  });

  it("posts changed failure details without changing status or impact and deduplicates repeats", () => {
    const changed = { ...seen("major_outage"), detail: "4 of 4 vantage points failing: Expected peers[0] exists" };
    const { actions, trackers } = run([
      ...Array.from({ length: 3 }, () => seen("major_outage")),
      changed,
      changed,
      seen("major_outage"),
      seen("major_outage"),
    ]);
    expect(actions).toHaveLength(3);
    expect(actions[1]).toMatchObject({
      type: "update",
      status: "investigating",
      impact: null,
      body: `Health checks are still failing: ${changed.detail}. We are investigating.`,
    });
    expect(actions[2]).toMatchObject({
      type: "update",
      status: "investigating",
      impact: null,
      body: `Health checks are still failing: ${seen("major_outage").detail}. We are investigating.`,
    });
    expect(trackers[changed.componentId]).toMatchObject({ since: 0, impact: "critical", healthySince: null });
    const persisted = JSON.parse(JSON.stringify(trackers));
    expect(
      step(persisted, [seen("major_outage")], 7 * 30_000, { newId: () => "unused", touched: new Set() }).actions,
    ).toEqual([]);
  });

  it("refreshes legacy or adopted trackers once, but never during maintenance or operator ownership", () => {
    const observation = seen("major_outage");
    const start = adopt({}, [
      {
        id: "00000000000000000000000019",
        componentId: observation.componentId,
        impact: "critical",
        createdAt: 0,
      },
    ]);
    expect(run([seen("major_outage", true)], { start }).actions).toEqual([]);
    expect(run([observation], { start, touched: new Set(["00000000000000000000000019"]) }).actions).toEqual([]);
    const { actions } = run([observation, observation], { start });
    expect(actions).toHaveLength(1);
    expect(actions[0]).toMatchObject({ type: "update", status: "investigating", impact: null });
  });

  it("never opens during maintenance and lets go of operator-edited incidents", () => {
    expect(run(Array.from({ length: 5 }, () => seen("major_outage", true))).actions).toEqual([]);
    const start = {
      "01M4E7G1W82VKFPF5FJ9AVK1CV": {
        state: "major_outage",
        since: 0,
        incidentId: "00000000000000000000000019",
        impact: "critical",
        healthySince: null,
      },
    } as const;
    const { actions, trackers } = run([seen("operational")], {
      touched: new Set(["00000000000000000000000019"]),
      start: { ...start },
    });
    expect(actions).toEqual([]);
    expect(trackers).toEqual({});
  });

  it("counts no unmeasured round toward a recovery's five healthy minutes", () => {
    const { actions } = run([
      ...Array.from({ length: 3 }, () => seen("major_outage")),
      seen("operational"),
      // Unconfigured, or too few vantages, for five minutes.
      ...Array.from({ length: 10 }, () => seen("none")),
      ...Array.from({ length: 11 }, () => seen("operational")),
    ]);
    expect(actions.map((action) => [action.at / 1000, action.type, "status" in action ? action.status : ""])).toEqual([
      [60, "open", ""],
      [90, "update", "monitoring"],
      // Five minutes after the last unmeasured round (390 s), not at the first check back.
      [690, "resolve", ""],
    ]);
  });

  it("closes an incident the monitor itself cannot measure for five minutes", () => {
    const quota = { ...seen("none"), monitorFault: "Canary quota exhausted" };
    const { actions, trackers } = run([
      ...Array.from({ length: 3 }, () => seen("major_outage")),
      ...Array.from({ length: 11 }, () => quota),
    ]);
    expect(actions.map((action) => [action.at / 1000, action.type])).toEqual([
      [60, "open"],
      [390, "resolve"],
    ]);
    expect(actions[1]).toMatchObject({ body: expect.stringContaining("(Canary quota exhausted)") });
    expect(trackers).toEqual({});
  });

  it("restarts the unmeasured five minutes once the monitor measures again", () => {
    const quota = { ...seen("none"), monitorFault: "Canary quota exhausted" };
    const { actions, trackers } = run([
      ...Array.from({ length: 3 }, () => seen("major_outage")),
      ...Array.from({ length: 9 }, () => quota),
      seen("major_outage"),
      ...Array.from({ length: 9 }, () => quota),
    ]);
    expect(actions.map((action) => action.type)).toEqual(["open"]);
    expect(trackers["01M4E7G1W82VKFPF5FJ9AVK1CV"]).toMatchObject({ unmeasuredSince: 13 * 30_000 });
  });

  it("keeps its memory while nothing is measured", () => {
    const { trackers } = run([seen("major_outage"), seen("none")]);
    expect(trackers["01M4E7G1W82VKFPF5FJ9AVK1CV"]?.since).toBe(0);
  });
});

describe("adopt", () => {
  it("tracks open automatic incidents it has forgotten", () => {
    expect(
      adopt({}, [
        { id: "00000000000000000000000005", componentId: "01M4E7G1W82VKFPF5FJ9AVK1CZ", impact: "major", createdAt: 5 },
      ]),
    ).toEqual({
      "01M4E7G1W82VKFPF5FJ9AVK1CZ": {
        state: "partial_outage",
        since: 5,
        incidentId: "00000000000000000000000005",
        impact: "major",
        healthySince: null,
      },
    });
  });

  it("lets go of incidents closed or deleted outside the machine", () => {
    const tracked: Tracker = {
      state: "major_outage",
      since: 0,
      incidentId: "000000000000000000000000ZZ",
      impact: "critical",
      healthySince: null,
    };
    const pending: Tracker = { state: "degraded", since: 0, incidentId: null, impact: null, healthySince: null };
    expect(adopt({ "01M4E7G1W82VKFPF5FJ9AVK1CZ": tracked, docs: pending }, [])).toEqual({ docs: pending });
  });
});
