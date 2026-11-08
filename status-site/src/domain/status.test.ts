import { describe, expect, it } from "vitest";
import { emptyHistogram } from "./histogram";
import {
  aggregate,
  componentState,
  endpointStatus,
  incidentWindows,
  isActive,
  isClosed,
  maintenancePhase,
  meanLatency,
  overallStatus,
  probeState,
  probeUptime,
  spans,
  sumCounts,
  typicalLatency,
  worstDuring,
  type Incident,
} from "./status";

const H = 3_600_000;

describe("aggregate", () => {
  it("reports down only when every part is down", () => {
    expect(aggregate(["major_outage", "major_outage"])).toBe("major_outage");
    expect(aggregate(["major_outage", "operational"])).toBe("partial_outage");
    expect(aggregate(["degraded", "operational", "none"])).toBe("degraded");
    expect(aggregate([])).toBe("none");
  });
});

const NOW = 10 * H;

const incident = (overrides: Partial<Incident> = {}): Incident => ({
  id: "00000000000000000000000001",
  kind: "incident",
  title: "",
  status: "resolved",
  impact: "major",
  source: "manual",
  message: "",
  components: [],
  scheduledStart: null,
  scheduledEnd: null,
  createdAt: "1970-01-01T02:00:00Z",
  updatedAt: "1970-01-01T03:00:00Z",
  resolvedAt: "1970-01-01T03:00:00Z",
  ...overrides,
});

const maintenance = (from: number, to: number, components: string[] = []): Incident =>
  incident({
    kind: "maintenance",
    status: "scheduled",
    impact: "none",
    components,
    scheduledStart: new Date(from).toISOString(),
    scheduledEnd: new Date(to).toISOString(),
    resolvedAt: null,
  });

describe("overallStatus", () => {
  it("reports operational when components and incidents are clear", () => {
    expect(overallStatus(["operational", "none"], [], NOW)).toBe("operational");
    expect(overallStatus([], [], NOW)).toBe("operational");
  });

  it("treats one component down as a partial platform outage", () => {
    expect(overallStatus(["major_outage", "operational"], [], NOW)).toBe("partial_outage");
    expect(overallStatus(["major_outage"], [], NOW)).toBe("major_outage");
  });

  it("maps active incident impact onto the headline", () => {
    expect(
      overallStatus(["operational"], [incident({ status: "investigating", impact: "minor", resolvedAt: null })], NOW),
    ).toBe("degraded");
    expect(
      overallStatus(["operational"], [incident({ status: "monitoring", impact: "critical", resolvedAt: null })], NOW),
    ).toBe("major_outage");
    expect(overallStatus(["operational"], [incident({ impact: "critical" })], NOW)).toBe("operational");
  });

  it("counts incidents naming components through those components only", () => {
    const outage = incident({
      status: "investigating",
      impact: "critical",
      resolvedAt: null,
      components: ["01M4E7G1W82VKFPF5FJ9AVK1CZ"],
    });
    expect(overallStatus(["major_outage", "operational"], [outage], NOW)).toBe("partial_outage");
  });

  it("shows maintenance while a window is in progress", () => {
    expect(overallStatus(["operational"], [maintenance(9 * H, 11 * H)], NOW)).toBe("maintenance");
    expect(overallStatus(["operational"], [maintenance(11 * H, 12 * H)], NOW)).toBe("operational");
  });
});

describe("maintenance", () => {
  it("moves through its phases by schedule, or ends early when completed", () => {
    const window = maintenance(9 * H, 11 * H);
    expect(maintenancePhase(window, 8 * H)).toBe("scheduled");
    expect(maintenancePhase(window, NOW)).toBe("in_progress");
    expect(maintenancePhase(window, 11 * H)).toBe("completed");
    expect(maintenancePhase({ ...window, status: "completed" }, NOW)).toBe("completed");
    expect(isActive(window, NOW)).toBe(true);
    expect(isClosed(window, 12 * H)).toBe(true);
  });

  it("overrides what was measured for the components it covers", () => {
    const window = maintenance(9 * H, 11 * H, ["01M4E7G1W82VKFPF5FJ9AVK1CZ"]);
    expect(componentState("major_outage", "01M4E7G1W82VKFPF5FJ9AVK1CZ", [window], NOW)).toBe("maintenance");
    expect(componentState("major_outage", "01M4E7G1W82VKFPF5FJ9AVK1CV", [window], NOW)).toBe("major_outage");
    const outage = incident({
      status: "investigating",
      impact: "critical",
      resolvedAt: null,
      components: ["01M4E7G1W82VKFPF5FJ9AVK1CZ"],
    });
    expect(componentState("operational", "01M4E7G1W82VKFPF5FJ9AVK1CZ", [outage], NOW)).toBe("major_outage");
    expect(componentState("operational", "01M4E7G1W82VKFPF5FJ9AVK1CZ", [window, outage], NOW)).toBe("major_outage");
  });
});

describe("incident windows", () => {
  it("spans an incident from its start to its resolution, or now", () => {
    expect(incidentWindows([incident()], NOW)).toEqual([{ from: 2 * H, to: 3 * H, state: "partial_outage" }]);
    expect(incidentWindows([incident({ resolvedAt: null })], NOW)[0]?.to).toBe(NOW);
    expect(incidentWindows([maintenance(9 * H, 12 * H)], NOW)).toEqual([
      { from: 9 * H, to: NOW, state: "maintenance" },
    ]);
  });

  it("ignores maintenance that has not started", () => {
    const windows = incidentWindows([maintenance(11 * H, 12 * H)], NOW);
    expect(worstDuring(windows, 0, 24 * H)).toBe("none");
  });

  it("takes the worst overlapping window", () => {
    const windows = incidentWindows([incident()], NOW);
    expect(worstDuring(windows, 0, 2 * H)).toBe("none");
    expect(worstDuring(windows, 2.5 * H, 4 * H)).toBe("partial_outage");
  });
});

describe("spans", () => {
  it("cuts a window into equal parts, closing on its end", () => {
    expect(spans(0, 3 * H, 3)).toEqual([
      { start: 0, end: H },
      { start: H, end: 2 * H },
      { start: 2 * H, end: 3 * H },
    ]);
  });
});

describe("probes", () => {
  const now = Date.parse("2026-09-26T10:15:00Z");
  const latest = {
    endpointId: "e",
    at: "2026-09-26T10:14:30Z",
    state: "degraded" as const,
    ok: 3,
    total: 4,
    latencyMs: 40,
    results: [],
  };

  it("takes an endpoint state from its latest round while it is fresh", () => {
    expect(endpointStatus(latest, now)).toBe("degraded");
    expect(endpointStatus({ ...latest, at: "2026-09-26T10:10:00Z" }, now)).toBe("none");
    expect(endpointStatus(undefined, now)).toBe("none");
  });

  it("rates a stretch of time by its failed and degraded rounds", () => {
    expect(probeState(undefined)).toBe("none");
    expect(probeState({ checks: 120, failures: 0, degraded: 0 })).toBe("operational");
    expect(probeState({ checks: 120, failures: 0, degraded: 2 })).toBe("operational");
    expect(probeState({ checks: 120, failures: 0, degraded: 12 })).toBe("degraded");
    expect(probeState({ checks: 120, failures: 2, degraded: 0 })).toBe("degraded");
    expect(probeState({ checks: 120, failures: 30, degraded: 0 })).toBe("partial_outage");
    expect(probeState({ checks: 120, failures: 120, degraded: 0 })).toBe("major_outage");
  });

  it("calls too few failed rounds degraded, not an outage", () => {
    expect(probeState({ checks: 1, failures: 1, degraded: 0 })).toBe("degraded");
    expect(probeState({ checks: 2, failures: 2, degraded: 0 })).toBe("degraded");
    expect(probeState({ checks: 3, failures: 3, degraded: 0 })).toBe("major_outage");
  });

  it("sums counts and averages latency over rounds that were not down", () => {
    const hist = (first: number) => [first, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    const counts = sumCounts([
      { checks: 2, failures: 1, degraded: 0, latencySum: 60, hist: hist(0) },
      { checks: 2, failures: 1, degraded: 1, latencySum: 40, hist: hist(1) },
    ]);
    expect(counts).toEqual({
      checks: 4,
      failures: 2,
      degraded: 1,
      latencySum: 100,
      hist: [1, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
    });
    expect(meanLatency(counts)).toBe(50);
    expect(typicalLatency(counts)).toBe(63);
    expect(typicalLatency({ ...counts, hist: emptyHistogram() })).toBe(50);
    expect(meanLatency({ checks: 2, failures: 2, latencySum: 0 })).toBeNull();
    expect(probeUptime({ checks: 4, failures: 1 })).toBe(0.75);
    expect(probeUptime({ checks: 0, failures: 0 })).toBeNull();
  });
});
