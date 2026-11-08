import { afterEach, describe, expect, it, vi } from "vitest";
import { logEvent } from "./log";

const TRACE = "4bf92f3577b34da6a3ce929d0e0e4736";

function requestWith(traceparent?: string): Request {
  const request = new Request("https://status.test/", traceparent ? { headers: { traceparent } } : {});
  return Object.assign(request, { cf: { colo: "GRU" } });
}

describe("logEvent", () => {
  afterEach(() => vi.restoreAllMocks());

  it("writes one record with the request's colo, trace id and error text", () => {
    const log = vi.spyOn(console, "error").mockImplementation(() => {});
    logEvent("error", "probe round failed", {
      component: "coordinator",
      request: requestWith(`00-${TRACE}-00f067aa0ba902b7-01`),
      error: new Error("boom"),
      round: 7,
    });
    expect(log).toHaveBeenCalledTimes(1);
    expect(log).toHaveBeenCalledWith({
      level: "error",
      msg: "probe round failed",
      service: "ts-status",
      component: "coordinator",
      round: 7,
      region: "GRU",
      trace_id: TRACE,
      error: "boom",
    });
  });

  it("omits region and trace_id when there is no request", () => {
    const log = vi.spyOn(console, "warn").mockImplementation(() => {});
    logEvent("warn", "outbox skipped", { component: "outbox" });
    expect(log).toHaveBeenCalledWith({
      level: "warn",
      msg: "outbox skipped",
      service: "ts-status",
      component: "outbox",
    });
  });

  it("keeps region but omits trace_id for an all-zero traceparent", () => {
    const log = vi.spyOn(console, "warn").mockImplementation(() => {});
    const request = requestWith(`00-${"0".repeat(32)}-00f067aa0ba902b7-01`);
    logEvent("warn", "outbox skipped", { component: "outbox", request });
    expect(log).toHaveBeenCalledWith({
      level: "warn",
      msg: "outbox skipped",
      service: "ts-status",
      component: "outbox",
      region: "GRU",
    });
    expect(log.mock.lastCall?.[0]).not.toHaveProperty("trace_id");
  });
});
