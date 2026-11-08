import { describe, expect, it } from "vitest";
import { holds, judge, parsePath, parseSpec, select, statusMatches, type CheckSpec } from "./assertions";

const atlas = {
  regions: [
    { code: "GRU", available: true, lag_ms: 12 },
    { code: "SFO", available: false },
  ],
  hosts: { GRU: "gru.api.terrascale.tech" },
};

describe("paths", () => {
  it("parses keys, indexes and selectors", () => {
    expect(parsePath("regions[code=GRU].available")).toEqual([
      { key: "regions" },
      { field: "code", equals: "GRU" },
      { key: "available" },
    ]);
    expect(parsePath("items[0].name")).toEqual([{ key: "items" }, { index: 0 }, { key: "name" }]);
    expect(parsePath("a..b")).toBeNull();
    expect(parsePath("a.")).toBeNull();
    expect(parsePath("")).toBeNull();
  });

  it("selects values and misses gracefully", () => {
    expect(select(atlas, "regions[code=GRU].lag_ms")).toBe(12);
    expect(select(atlas, "regions[1].available")).toBe(false);
    expect(select(atlas, "hosts.GRU")).toBe("gru.api.terrascale.tech");
    expect(select(atlas, "regions[code=FRA].available")).toBeUndefined();
    expect(select(atlas, "hosts.GRU.deeper")).toBeUndefined();
  });
});

describe("statusMatches", () => {
  it("accepts classes, codes and lists", () => {
    expect(statusMatches("2xx", 204)).toBe(true);
    expect(statusMatches("2xx", 301)).toBe(false);
    expect(statusMatches("200, 301", 301)).toBe(true);
  });
});

describe("judge", () => {
  const spec: CheckSpec = {
    expectStatus: "2xx",
    keyword: null,
    assertions: [{ path: "regions[code=GRU].available", op: "eq", value: true }],
    extract: { lag_ms: "regions[code=GRU].lag_ms" },
    indirect: false,
  };
  const body = JSON.stringify(atlas);

  it("passes and extracts values", () => {
    expect(judge(spec, { status: 200, error: null, body })).toEqual({
      outcome: "ok",
      error: null,
      values: { lag_ms: 12 },
    });
  });

  it("fails a broken assertion with a readable reason", () => {
    const sfo = { ...spec, assertions: [{ path: "regions[code=SFO].available", op: "eq" as const, value: true }] };
    expect(judge(sfo, { status: 200, error: null, body })).toMatchObject({
      outcome: "fail",
      error: "Expected regions[code=SFO].available eq true",
    });
  });

  it("reports an unreachable indirect source as unknown, not down", () => {
    expect(judge({ ...spec, indirect: true }, { status: 503, error: null, body: "" }).outcome).toBe("unknown");
    expect(judge({ ...spec, indirect: true }, { status: null, error: "Timed out" }).outcome).toBe("unknown");
    expect(judge(spec, { status: 503, error: null, body: "" })).toMatchObject({ outcome: "fail", error: "HTTP 503" });
  });

  it("reports an indirect source that does not mention the component as unknown", () => {
    const fra = {
      ...spec,
      indirect: true,
      assertions: [{ path: "regions[code=FRA].available", op: "eq" as const, value: true }],
    };
    expect(judge(fra, { status: 200, error: null, body })).toMatchObject({ outcome: "unknown", error: "Not reported" });
    expect(judge({ ...spec, indirect: true }, { status: 200, error: null, body }).outcome).toBe("ok");
  });

  it("checks keywords and needs JSON only for assertions", () => {
    const keyword = { ...spec, keyword: "TerraScale", assertions: [], extract: {} };
    expect(judge(keyword, { status: 200, error: null, body: "<title>TerraScale</title>" }).outcome).toBe("ok");
    expect(judge(keyword, { status: 200, error: null, body: "<title>Oops</title>" }).error).toBe("Keyword missing");
    expect(judge(spec, { status: 200, error: null, body: "not json" }).error).toBe("Invalid JSON");
    expect(judge({ ...spec, assertions: [] }, { status: 200, error: null, body: "not json" })).toEqual({
      outcome: "ok",
      error: null,
      values: {},
    });
  });

  it("requires finite numbers for numeric comparisons without coercing values", () => {
    for (const op of ["lt", "gt"] as const) {
      for (const value of [null, undefined, "4", true, {}, [], NaN, Infinity, -Infinity]) {
        expect(holds({ path: "lag", op, value: 0 }, value)).toBe(false);
        expect(holds({ path: "lag", op, value }, 0)).toBe(false);
      }
    }
  });

  it("covers every operator", () => {
    const run = (op: CheckSpec["assertions"][number]["op"], value?: unknown, path = "regions[code=GRU].lag_ms") =>
      judge({ ...spec, extract: {}, assertions: [{ path, op, value }] }, { status: 200, error: null, body }).outcome;
    expect(run("lt", 50)).toBe("ok");
    expect(run("gt", 50)).toBe("fail");
    expect(run("ne", 13)).toBe("ok");
    expect(run("exists")).toBe("ok");
    expect(run("exists", undefined, "regions[code=SFO].lag_ms")).toBe("fail");
    expect(run("contains", "gru", "hosts.GRU")).toBe("ok");
  });
});

describe("parseSpec", () => {
  it("drops malformed assertions and extractions", () => {
    expect(
      parseSpec({
        expectStatus: "2xx",
        keyword: "",
        assertions:
          '[{"path":"status","op":"eq","value":"ok"},{"path":"a..b","op":"eq"},{"path":"x","op":"nope"},{"path":"x","op":"eq"},{"path":"x","op":"lt","value":"1"}]',
        extract: '{"version":"version","bad":"a..","n":1}',
        indirect: 0,
      }),
    ).toEqual({
      expectStatus: "2xx",
      keyword: null,
      assertions: [{ path: "status", op: "eq", value: "ok" }],
      extract: { version: "version" },
      indirect: false,
    });
  });
});

describe("blocked probes", () => {
  const spec: CheckSpec = {
    expectStatus: "200",
    keyword: null,
    indirect: false,
    assertions: [{ path: "status", op: "eq", value: "ok" }],
    extract: {},
  };

  it("classifies a configured refusal as a probe error, not an outage", () => {
    const blocked = { ...spec, blockedStatus: 403 };
    expect(judge(blocked, { status: 403, error: null, body: "error code: 1010" })).toEqual({
      outcome: "unknown",
      error: "Probe blocked (HTTP 403)",
      values: {},
    });
    expect(judge(blocked, { status: 401, error: null }).outcome).toBe("fail");
    expect(judge(blocked, { status: 503, error: null }).outcome).toBe("fail");
  });

  it("still fails an unexpected 403 on endpoints not known to be blocked", () => {
    expect(judge(spec, { status: 403, error: null })).toEqual({ outcome: "fail", error: "HTTP 403", values: {} });
  });
});
