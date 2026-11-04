import { describe, expect, it, vi } from "vitest";
import { canarySample, holdsRow, isCanaryConfig, parseCanaries, runCanary, runTokenProbe } from "./canary";
import { CANARY_CREDENTIAL_INVALID } from "../domain/status";

const canary = {
  origin: "https://api.example.test",
  issuer: "https://auth.example.test/terrascale",
  region: "gru",
  project: "01M4E7G1W82VKFPF5FJ9AVK1DT",
  database: "status_canary",
  collection: "canary",
  document: "01M4E7G1W82VKFPF5FJ9AVK1DV",
  key: `01M4E7G1W82VKFPF5FJ9AVK1CQ_${"a".repeat(52)}`,
};
const token = "header.payload.signature";
const rowsOf = (rows: unknown) => ({ results: [{ columns: [{ name: "id", decltype: "TEXT" }], rows, rows_affected: 0, last_insert_rowid: null }] });
const document = rowsOf([[canary.document]]);
const grant = { access_token: token, token_type: "Bearer", expires_in: 900 };
const fake = (body: unknown, status = 200) =>
  vi
    .fn<typeof fetch>()
    .mockResolvedValueOnce(Response.json(grant))
    .mockResolvedValueOnce(Response.json(body, { status }));

describe("read-only Data API probe", () => {
  it("requires current ts-auth credentials and canonical resource identities", () => {
    expect(isCanaryConfig(canary)).toBe(true);
    for (const patch of [
      { origin: canary.origin + "/" },
      { origin: "https://user:password@api.example.test" },
      { issuer: "http://auth.example.test/terrascale" },
      { issuer: `${canary.issuer}/` },
      { issuer: "https://user:password@auth.example.test/terrascale" },
      { issuer: undefined },
      { project: "project" },
      { database: "Canary" },
      { collection: "1canary" },
      { document: "document" },
      { key: "tb_fake_key" },
      { key: canary.key + "\n" },
      { key: `tsk_${"a".repeat(20)}_${"b".repeat(52)}` },
    ])
      expect(isCanaryConfig({ ...canary, ...patch })).toBe(false);
    const error = vi.spyOn(console, "error").mockImplementation(() => {});
    try {
      expect(parseCanaries(undefined)).toEqual({});
      expect(parseCanaries("not json")).toEqual({});
      expect(parseCanaries(JSON.stringify({ [canary.document]: canary, invalid: canary }))).toEqual({
        [canary.document]: canary,
      });
    } finally {
      error.mockRestore();
    }
  });

  it("exchanges the key only at ts-auth and only reads the fixture row with its token", async () => {
    const fetcher = fake(document);
    expect(await runCanary(canary, "round", fetcher)).toMatchObject({ ok: true, error: null });
    expect(fetcher).toHaveBeenCalledTimes(2);
    const [url, init] = fetcher.mock.calls[0]!;
    expect(url).toBe(`${canary.issuer}/oauth/token`);
    expect(init).toMatchObject({ method: "POST", redirect: "manual", cache: "no-store" });
    expect(Object.fromEntries(new URLSearchParams(init?.body as string))).toEqual({
      grant_type: "client_credentials",
      client_id: canary.key.slice(0, 26),
      client_secret: canary.key,
      project: canary.project,
      scope: "sql:read",
    });
    const [readUrl, readInit] = fetcher.mock.calls[1]!;
    expect(readUrl).toBe(`${canary.origin}/v1/projects/${canary.project}/databases/status_canary/sql/execute`);
    expect(readInit).toMatchObject({ method: "POST", cache: "no-store", redirect: "manual" });
    expect(JSON.parse(String(readInit?.body))).toEqual({
      statements: [{ sql: 'SELECT id FROM "canary" WHERE id = ?', args: [canary.document] }],
    });
    expect(new Headers(readInit?.headers).get("authorization")).toBe(`Bearer ${token}`);
    expect(new Headers(readInit?.headers).get("X-TerraScale-Region-Pin")).toBe("gru");
    expect([...new Headers(readInit?.headers).keys()].sort()).toEqual([
      "authorization",
      "content-type",
      "user-agent",
      "x-terrascale-region-pin",
    ]);
    expect(new Headers(init?.headers).has("X-TerraScale-Region-Pin")).toBe(false);
    expect(JSON.stringify(fetcher.mock.calls[1])).not.toContain(canary.key);
  });

  it("shares one exchange between concurrent probes of the same key", async () => {
    let grants = 0;
    const fetcher = vi.fn<typeof fetch>(async (url) => {
      if (!String(url).endsWith("/oauth/token")) return Response.json(document);
      grants++;
      await new Promise((resolve) => setTimeout(resolve, 10));
      return Response.json(grant);
    });
    const results = await Promise.all([1, 2, 3].map(() => runCanary(canary, "round", fetcher)));
    expect(results.every((result) => result.ok)).toBe(true);
    expect(grants).toBe(1);
    expect(fetcher).toHaveBeenCalledTimes(4);
  });

  it("reuses a one-minute ts-auth token for the next round", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    try {
      const minute = { ...grant, expires_in: 59 };
      const fetcher = vi.fn<typeof fetch>(async (url) =>
        String(url).endsWith("/oauth/token") ? Response.json(minute) : Response.json(document),
      );
      const exchanges = () => fetcher.mock.calls.filter(([url]) => String(url).endsWith("/oauth/token")).length;
      vi.setSystemTime(0);
      expect(await runCanary(canary, "round", fetcher)).toMatchObject({ ok: true });
      vi.setSystemTime(30_000);
      expect(await runCanary(canary, "round", fetcher)).toMatchObject({ ok: true });
      expect(exchanges()).toBe(1);
      vi.setSystemTime(60_000);
      expect(await runCanary(canary, "round", fetcher)).toMatchObject({ ok: true });
      expect(exchanges()).toBe(2);
    } finally {
      vi.useRealTimers();
    }
  });

  it("fails closed on invalid rows, JSON, auth failures and redirects without leaking responses", async () => {
    for (const body of [
      rowsOf([]),
      rowsOf([["bad"]]),
      rowsOf([[canary.project]]),
      rowsOf([[canary.document], [canary.document]]),
      rowsOf(null),
      rowsOf([canary.document]),
      { results: [] },
      {},
    ]) {
      const result = await runCanary(canary, "round", fake(body));
      expect(result).toMatchObject({ ok: false, error: "Invalid response" });
    }
    for (const status of [401, 403, 404, 421, 302]) {
      const fetcher = fake({ error: { code: canary.key, message: token } }, status);
      expect(await runCanary(canary, "round", fetcher)).toMatchObject({ ok: false, error: `HTTP ${status}` });
      expect(fetcher).toHaveBeenCalledTimes(2);
    }
    const fetcher = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(Response.json(grant))
      .mockResolvedValueOnce(new Response("bad json"));
    expect(await runCanary(canary, "round", fetcher)).toMatchObject({ ok: false, error: "Invalid JSON" });
    expect(holdsRow(null, canary.document)).toBe(false);
  });

  it("does not contact TerraBase when config or token exchange fails", async () => {
    const invalid = vi.fn<typeof fetch>();
    expect(await runCanary({ ...canary, key: "tb_fake" }, "round", invalid)).toMatchObject({
      ok: false,
      error: "Invalid probe config",
    });
    expect(invalid).not.toHaveBeenCalled();
    for (const response of [
      ...[{ access_token: canary.key }, { token_type: "Basic" }, { expires_in: 0 }, { expires_in: 1.5 }].map((patch) =>
        Response.json({ ...grant, ...patch }),
      ),
    ]) {
      const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(response);
      expect(await runCanary(canary, "round", fetcher)).toMatchObject({ ok: false });
      expect(fetcher).toHaveBeenCalledTimes(1);
    }
    const fetcher = vi.fn<typeof fetch>().mockRejectedValue(new Error(canary.key));
    expect(await runCanary(canary, "round", fetcher)).toEqual({
      ok: false,
      latencyMs: null,
      error: "Token exchange: Unreachable",
    });
  });

  it("records rejected grant credentials as unmeasured, retries and keeps genuine failures judged", async () => {
    const fetcher = vi
      .fn<typeof fetch>()
      .mockResolvedValueOnce(Response.json({ error: "invalid_client", error_description: canary.key }, { status: 401 }))
      .mockResolvedValueOnce(Response.json(grant))
      .mockResolvedValueOnce(Response.json(document));
    const result = await runCanary(canary, "round", fetcher);
    expect(result).toEqual({ ok: null, latencyMs: null, error: CANARY_CREDENTIAL_INVALID });
    expect(fetcher).toHaveBeenCalledTimes(1);
    expect(canarySample("endpoint", result, 2)).toMatchObject({
      streak: 2,
      sample: {
        state: "none",
        ok: 0,
        total: 0,
        latencyMs: null,
        results: [{ v: "canary", ok: null, ms: null, e: CANARY_CREDENTIAL_INVALID }],
      },
    });
    expect(await runCanary(canary, "retry", fetcher)).toMatchObject({ ok: true });
    expect(fetcher).toHaveBeenCalledTimes(3);
    for (const status of [403, 429, 500, 503]) {
      const rejected = vi
        .fn<typeof fetch>()
        .mockResolvedValueOnce(Response.json({ error: "invalid_client" }, { status }));
      const failed = await runCanary(canary, "round", rejected);
      expect(failed).toMatchObject({ ok: false, error: `Token exchange: HTTP ${status} invalid_client` });
      expect(canarySample("endpoint", failed, 1).sample).toMatchObject({ state: "major_outage", total: 1 });
    }
  });
});

describe("Authentication token probe", () => {
  it("exchanges the canary key afresh every round and times the exchange", async () => {
    const fetcher = vi.fn<typeof fetch>().mockImplementation(async () => Response.json(grant));
    expect(await runTokenProbe(canary, fetcher)).toMatchObject({ ok: true, error: null });
    expect(await runTokenProbe(canary, fetcher)).toMatchObject({ ok: true });
    expect(fetcher).toHaveBeenCalledTimes(2);
    expect(fetcher.mock.calls[0]![0]).toBe(`${canary.issuer}/oauth/token`);
  });

  it("fails on refusals and timeouts, and leaves a rejected credential unmeasured", async () => {
    const busy = vi
      .fn<typeof fetch>()
      .mockResolvedValue(Response.json({ error: "temporarily_unavailable" }, { status: 503 }));
    expect(await runTokenProbe(canary, busy)).toEqual({
      ok: false,
      latencyMs: null,
      error: "HTTP 503 temporarily_unavailable",
    });
    const rejected = vi
      .fn<typeof fetch>()
      .mockResolvedValue(Response.json({ error: "invalid_client" }, { status: 401 }));
    expect(await runTokenProbe(canary, rejected)).toEqual({
      ok: null,
      latencyMs: null,
      error: CANARY_CREDENTIAL_INVALID,
    });
    const down = vi.fn<typeof fetch>().mockRejectedValue(new Error("reset"));
    expect(await runTokenProbe(canary, down)).toMatchObject({ ok: false, error: "Unreachable" });
  });
});
