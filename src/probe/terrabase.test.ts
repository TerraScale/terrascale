import { describe, expect, it, vi } from "vitest";
import { isId } from "../ids";
import { isConfigured, isTerraBaseConfig, notConfiguredSample, parseTerraBase, runTerraBase } from "./terrabase";

const origin = "https://api.terrascale.tech";
const canary = {
  origin,
  region: "gru",
  project: "01M4E7G1W82VKFPF5FJ9AVK1CQ",
  database: "canary",
  collection: "canary",
  document: "01M4E7G1W82VKFPF5FJ9AVK1D9",
  issuer: "https://auth.example.test/terrascale",
  key: `01M4E7G1W82VKFPF5FJ9AVK1CQ_${"a".repeat(52)}`,
};

describe("TerraBase probe config", () => {
  it("validates the new config and treats absent and malformed secrets differently", () => {
    expect(isTerraBaseConfig("documents", canary)).toBe(true);
    expect(isTerraBaseConfig("health", { origin, region: "gru" })).toBe(true);
    expect(isTerraBaseConfig("documents", { ...canary, key: "tbk1_retired" })).toBe(false);
    expect(isConfigured("documents", { origin, region: "gru" })).toBe(false);
    expect(isConfigured("health", undefined)).toBe(false);
    expect(isConfigured("documents", null)).toBe(true);
    vi.spyOn(console, "error").mockImplementation(() => {});
    expect(parseTerraBase(undefined)).toEqual({});
    for (const secret of ["not json", "[]", "null"]) expect(parseTerraBase(secret)).toBeNull();
    expect(parseTerraBase(JSON.stringify({ "01M4E7G1W82VKFPF5FJ9AVK1DF": canary }))).toEqual({
      "01M4E7G1W82VKFPF5FJ9AVK1DF": canary,
    });
    expect(notConfiguredSample("01M4E7G1W82VKFPF5FJ9AVK1DF")).toMatchObject({
      state: "none",
      ok: 0,
      total: 0,
      latencyMs: null,
    });
    vi.restoreAllMocks();
  });
});

describe("TerraBase probes", () => {
  const grant = { access_token: "header.payload.signature", token_type: "Bearer", expires_in: 900 };
  const sql = `${origin}/v1/projects/${canary.project}/databases/canary/sql/execute`;
  type Step = "INSERT" | "SELECT" | "COUNT" | "DELETE";
  const result = (rows: unknown[][], affected = 0) => Response.json({
    results: [{ columns: [], rows, rows_affected: affected, last_insert_rowid: null }],
  });

  /** A TerraBase SQL fake holding the canary rows; `fail` swaps one step's answer. */
  function fake(fail: Partial<Record<Step, Response>> = {}) {
    const rows = new Map<string, string>();
    return vi.fn<typeof fetch>(async (input, init) => {
      if (String(input).endsWith("/oauth/token")) return Response.json(grant);
      const { sql: text, args } = (JSON.parse(String(init?.body)) as { statements: { sql: string; args: string[] }[] })
        .statements[0]!;
      const step: Step = text.startsWith("INSERT") ? "INSERT" : text.startsWith("DELETE") ? "DELETE"
        : text.includes("count(*)") ? "COUNT" : "SELECT";
      if (fail[step]) return fail[step]!;
      const [id, marker] = args;
      switch (step) {
        case "INSERT":
          rows.set(id!, marker!);
          return result([], 1);
        case "SELECT":
          return result(rows.has(id!) ? [[rows.get(id!)]] : []);
        case "COUNT":
          return result([[rows.get(id!) === marker ? 1 : 0]]);
        case "DELETE":
          return result([], rows.delete(id!) ? 1 : 0);
      }
    });
  }

  it("inserts, reads, counts and deletes a fresh row with the exchanged token", async () => {
    const fetcher = fake();
    expect(await runTerraBase("documents", canary, "round", fetcher)).toMatchObject({ ok: true });
    const statements = fetcher.mock.calls.slice(1).map(([url, init]) => {
      expect([init?.method, String(url)]).toEqual(["POST", sql]);
      return (JSON.parse(String(init?.body)) as { statements: { sql: string; args: unknown[] }[] }).statements;
    });
    const id = statements[0]![0]!.args[0] as string;
    expect(isId(id)).toBe(true);
    expect(statements).toEqual([
      [{ sql: 'INSERT INTO "canary" (id, canary) VALUES (?, ?)', args: [id, "round"] }],
      [{ sql: 'SELECT canary FROM "canary" WHERE id = ?', args: [id] }],
      [{ sql: 'SELECT count(*) FROM "canary" WHERE id = ? AND canary = ?', args: [id, "round"] }],
      [{ sql: 'DELETE FROM "canary" WHERE id = ?', args: [id] }],
    ]);
    expect(String(fetcher.mock.calls[0]![1]?.body)).toContain("scope=documents%3Aread+documents%3Awrite");
    for (const [, init] of fetcher.mock.calls.slice(1)) {
      expect(init).toMatchObject({ redirect: "manual" });
      expect(new Headers(init?.headers).get("authorization")).toBe(`Bearer ${grant.access_token}`);
      expect(new Headers(init?.headers).get("X-TerraScale-Region-Pin")).toBe("gru");
    }
    expect(JSON.stringify(fetcher.mock.calls.slice(1))).not.toContain(canary.key);
  });

  it("fails a step it cannot prove and still deletes the row", async () => {
    for (const [fail, error] of [
      [{ INSERT: Response.json({ error: { code: "forbidden_scope" } }, { status: 403 }) }, "HTTP 403 forbidden_scope"],
      [{ INSERT: result([], 0) }, "Invalid response"],
      [{ SELECT: Response.json({ error: { code: "sql_error" } }, { status: 400 }) }, "HTTP 400 sql_error"],
      [{ SELECT: result([["other"]]) }, "Write not readable"],
      [{ COUNT: result([[0]]) }, "Query missed the row"],
      [{ DELETE: Response.json({ error: { code: "internal_error" } }, { status: 500 }) }, "HTTP 500 internal_error"],
    ] as const) {
      const fetcher = fake(fail);
      expect(await runTerraBase("documents", canary, "round", fetcher)).toEqual({ ok: false, latencyMs: null, error });
      expect(String(fetcher.mock.calls.at(-1)![1]?.body)).toContain("DELETE FROM");
    }
  });

  it("leaves writes unmeasured when the monitor's plan allowance refuses them", async () => {
    const fetcher = fake({
      INSERT: Response.json({ error: { code: "operation_allowance", message: "upgrade" } }, { status: 429 }),
    });
    expect(await runTerraBase("documents", canary, "round", fetcher)).toEqual({
      ok: null,
      latencyMs: null,
      error: "Canary quota exhausted",
    });
    expect(String(fetcher.mock.calls.at(-1)![1]?.body)).toContain("DELETE FROM");
    const spent = fake({ INSERT: Response.json({ error: { code: "quota_exceeded" } }, { status: 429 }) });
    expect(await runTerraBase("documents", canary, "round", spent)).toMatchObject({ ok: null, error: "Canary quota exhausted" });
    const busy = fake({ INSERT: Response.json({ error: { code: "tenant_busy" } }, { status: 429 }) });
    expect(await runTerraBase("documents", canary, "round", busy)).toMatchObject({
      ok: false,
      error: "HTTP 429 tenant_busy",
    });
  });

  it("leaves documents unmeasured on grant 401, but judges API and anonymous readiness 401", async () => {
    const rejected = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(Response.json({ error: "invalid_client" }, { status: 401 }));
    expect(await runTerraBase("documents", canary, "round", rejected)).toEqual({
      ok: null,
      latencyMs: null,
      error: "Canary credential invalid",
    });
    expect(rejected).toHaveBeenCalledTimes(1);
    const api = fake({ INSERT: Response.json({ error: { code: "invalid_token" } }, { status: 401 }) });
    expect(await runTerraBase("documents", canary, "round", api)).toMatchObject({
      ok: false,
      error: "HTTP 401 invalid_token",
    });
    const ready = vi.fn<typeof fetch>().mockResolvedValueOnce(new Response(null, { status: 401 }));
    expect(await runTerraBase("health", canary, "round", ready)).toMatchObject({ ok: false, error: "HTTP 401" });
  });

  it("reuses the token across rounds, exchanges again after a failure, and times only TerraBase", async () => {
    vi.useFakeTimers();
    try {
      const store = fake();
      const fetcher = vi.fn<typeof fetch>(async (input, init) => {
        // ts-auth answers slowly; TerraBase in 100 ms per request.
        await vi.advanceTimersByTimeAsync(String(input).endsWith("/oauth/token") ? 2_000 : 100);
        return store(input, init);
      });
      const tokens = () => fetcher.mock.calls.filter(([url]) => String(url).endsWith("/oauth/token")).length;
      expect(await runTerraBase("documents", canary, "round", fetcher)).toEqual({
        ok: true,
        latencyMs: 100,
        error: null,
      });
      await runTerraBase("documents", canary, "round", fetcher);
      expect(tokens()).toBe(1);
      store.mockResolvedValueOnce(Response.json({ error: { code: "invalid_token" } }, { status: 401 }));
      expect(await runTerraBase("documents", canary, "round", fetcher)).toMatchObject({ ok: false });
      await runTerraBase("documents", canary, "round", fetcher);
      expect(tokens()).toBe(2);
      // Within a round's use (20 s) of the 900 s token's expiry, the next round exchanges again.
      await vi.advanceTimersByTimeAsync(880_000);
      await runTerraBase("documents", canary, "round", fetcher);
      expect(tokens()).toBe(3);
    } finally {
      vi.useRealTimers();
    }
  });

  it("keeps readiness anonymous and rejects a wrong region, unavailable or invalid response", async () => {
    for (const [body, status, ok] of [
      [{ status: "ready", version: "1.0", region: "gru" }, 200, true],
      [{ status: "ready", version: "1.0", region: "sjc" }, 200, false],
      [{ status: "unavailable", version: "1.0", region: "gru" }, 200, false],
      [{ status: "ready", version: "1.0", region: "gru" }, 503, false],
      [{ status: "ready", region: "gru" }, 200, false],
    ] as const) {
      const fetcher = vi.fn<typeof fetch>().mockResolvedValue(Response.json(body, { status }));
      expect(await runTerraBase("health", canary, "round", fetcher)).toMatchObject({ ok });
      expect(fetcher).toHaveBeenCalledTimes(1);
      const [url, init] = fetcher.mock.calls[0]!;
      expect(url).toBe(`${origin}/readyz`);
      expect(new Headers(init?.headers).has("authorization")).toBe(false);
      expect(new Headers(init?.headers).get("X-TerraScale-Region-Pin")).toBe("gru");
      expect([...new Headers(init?.headers).keys()].sort()).toEqual(["user-agent", "x-terrascale-region-pin"]);
    }
  });
});
