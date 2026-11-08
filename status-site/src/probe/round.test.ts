import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { CheckSpec } from "../domain/assertions";
import type { Env } from "../env";
import { loadSnapshot } from "../db/reads";
import { overview, snapshotWindow } from "../overview";
import { testD1 } from "../testing/d1";
import type { ProbeRequest, ProbeResponse } from "./protocol";
import { evaluate, observe, roundOf, runRound, targets, type RoundEndpoint, type RoundState } from "./round";
import { rollupStatements } from "./rollup";
import { vantageBindings } from "../testing/vantages";

// Rounds never reach the network: the email probe's inbox is unreachable
// here unless a test supplies its own fetch.
beforeEach(() => {
  vi.stubGlobal("fetch", async () => {
    throw new TypeError("offline");
  });
});
afterEach(() => {
  vi.unstubAllGlobals();
});

const plain: CheckSpec = { expectStatus: "2xx", keyword: null, assertions: [], extract: {}, indirect: false };
const atlas = (code: string): CheckSpec => ({
  ...plain,
  assertions: [{ path: `regions[code=${code}].available`, op: "eq", value: true }],
  extract: { lag_ms: `regions[code=${code}].lag_ms` },
  indirect: true,
});

const endpoints: RoundEndpoint[] = [
  { id: "gru", url: "https://atlas.test/regions", headers: "{}", spec: atlas("GRU") },
  { id: "sfo", url: "https://atlas.test/regions", headers: "{}", spec: atlas("SFO") },
  { id: "web", url: "https://web.test/", headers: "{}", spec: plain },
];

const catalog = JSON.stringify({
  regions: [
    { code: "GRU", available: true, lag_ms: 9 },
    { code: "SFO", available: false },
  ],
});

/** A vantage's answer: atlas answering with `atlasStatus`, the website with `webStatus`. */
const answer = (vantage: string, atlasStatus: number, webStatus = 200): ProbeResponse => ({
  vantage,
  colo: vantage.toUpperCase(),
  results: targets(endpoints).map((target) =>
    target.url.includes("atlas")
      ? { key: target.key, status: atlasStatus, latencyMs: 50, error: null, body: catalog }
      : { key: target.key, status: webStatus, latencyMs: 30, error: null },
  ),
});

describe("targets", () => {
  it("probes a shared URL once, with its body when any endpoint needs it", () => {
    expect(targets(endpoints).map((target) => [target.url, target.body])).toEqual([
      ["https://atlas.test/regions", true],
      ["https://web.test/", false],
    ]);
  });
});

describe("roundOf", () => {
  it("starts rounds on the half minute", () => {
    expect(roundOf(Date.parse("2026-09-26T10:00:44.123Z"))).toBe(Date.parse("2026-09-26T10:00:30Z"));
  });
});

describe("evaluate", () => {
  it("accepts a single reported region and starts checking new peers without catalog edits", () => {
    const url = "https://api.terrascale.tech/health/replication";
    const reportEndpoints: RoundEndpoint[] = [
      { id: "report", url, headers: "{}", spec: plain, regionCode: null },
      { id: "sjc", url, headers: "{}", spec: { ...plain, indirect: true }, regionCode: "SJC" },
    ];
    expect(targets(reportEndpoints)).toHaveLength(1);
    expect(targets(reportEndpoints)[0]?.body).toBe(true);
    const response = (peers: unknown[], lag: number | null): ProbeResponse[] =>
      ["sfo", "gru"].map((vantage) => ({
        vantage,
        colo: vantage.toUpperCase(),
        results: [
          {
            key: `${url}\n{}`,
            status: 200,
            latencyMs: 10,
            error: null,
            body: JSON.stringify({ region: "gru", status: "ok", maxLagSeconds: lag, peers }),
          },
        ],
      }));
    expect(evaluate(reportEndpoints, response([], null), {}).samples.map((s) => [s.state, s.total])).toEqual([
      ["operational", 2],
      ["none", 0],
    ]);
    const added = response([{ region: "new-region", status: "ok", lagSeconds: 31 }], 31);
    const first = evaluate(reportEndpoints, added, {});
    expect(evaluate(reportEndpoints, added, first.streaks).samples[0]?.state).toBe("major_outage");
    expect(first.samples[0]?.results[0]?.e).toContain("new-region");
    expect(first.samples[1]?.total).toBe(0);
  });

  it("judges each endpoint on the shared observation", () => {
    const { samples, streaks } = evaluate(endpoints, [answer("sfo", 200), answer("fra", 200), null], {});
    expect(samples.map((sample) => [sample.endpointId, sample.state, sample.ok, sample.total])).toEqual([
      ["gru", "operational", 2, 2],
      ["sfo", "degraded", 0, 2],
      ["web", "operational", 2, 2],
    ]);
    expect(samples[0]?.values).toEqual({ lag_ms: 9 });
    expect(samples[1]?.results[0]).toEqual({
      v: "sfo",
      ok: 0,
      ms: null,
      e: "Expected regions[code=SFO].available eq true",
    });
    // The first down round is a streak of one; the second confirms it.
    expect(streaks).toEqual({ sfo: 1 });
    expect(evaluate(endpoints, [answer("sfo", 200), answer("fra", 200)], streaks).samples[1]?.state).toBe(
      "major_outage",
    );
  });

  it("leaves unknown results out: a failing indirect source never marks its cells down", () => {
    const { samples } = evaluate(endpoints, [answer("sfo", 503, 503), answer("fra", 503, 503)], {});
    expect(samples.map((sample) => sample.state)).toEqual(["none", "none", "degraded"]);
    expect(samples[0]?.results).toEqual([
      { v: "sfo", ok: null, ms: null, e: "HTTP 503" },
      { v: "fra", ok: null, ms: null, e: "HTTP 503" },
    ]);
  });

  it("never counts a known blocked probe as down, and keeps the streak", () => {
    const blocked = ["sfo", "gru", "fra", "nrt"].map((code) => answer(code, 200, 403));
    const known = endpoints.map((endpoint) =>
      endpoint.id === "web" ? { ...endpoint, spec: { ...plain, blockedStatus: 403 } } : endpoint,
    );
    const { samples, streaks } = evaluate(known, blocked, { web: 1 });
    expect(samples[2]?.state).toBe("none");
    expect(samples[2]?.results[0]).toEqual({ v: "sfo", ok: null, ms: 30, e: "Probe blocked (HTTP 403)" });
    expect(streaks.web).toBe(1);
  });

  it("cannot judge when only one vantage answered", () => {
    const { samples } = evaluate(endpoints, [answer("sfo", 200, 503), null, null, null], {});
    expect(samples.every((sample) => sample.state === "none")).toBe(true);
  });
});

/** A vantage answering every catalog target as its service would when up,
    except those `failing` names, which answer 503. */
function vantage(code: string, failing: (url: string) => boolean = () => false, version = () => "1.0.0") {
  return {
    fetch: async (_url: string, init: RequestInit) => {
      const body = JSON.parse(String(init.body)) as ProbeRequest;
      return Response.json({
        vantage: code,
        colo: code.toUpperCase(),
        results: body.targets.map((target) => {
          const region = "gru";
          const answer = (status: number, json?: unknown) => ({
            key: target.key,
            status,
            latencyMs: 40,
            error: null,
            body: target.body ? (json === undefined ? "TerraScale" : JSON.stringify(json)) : undefined,
          });
          if (failing(target.url)) return answer(503, {});
          if (target.url.endsWith("/health/ready")) return answer(200, { status: "ok", version: version() });
          if (target.url.endsWith("/readyz")) return answer(200, { status: "ready", version: version(), region });
          if (target.url.includes("/v1/")) return answer(401, { error: { code: "invalid_key" } });
          if (target.url.includes("openid-configuration"))
            return answer(200, {
              issuer: "https://auth.terrascale.tech/terrascale",
              jwks_uri: "https://auth.terrascale.tech/terrascale/jwks",
            });
          if (target.url.endsWith("/health/replication"))
            return answer(200, {
              region,
              status: "ok",
              maxLagSeconds: 2,
              peers: [{ region: "sjc", status: "ok", lagSeconds: 2 }],
            });
          if (target.url.includes("cloudflarestatus")) return answer(200, { component: { status: "operational" } });
          return answer(200);
        }),
      } satisfies ProbeResponse);
    },
  };
}

const READY = ["01M4E7G1W82VKFPF5FJ9AVK1D6", "01M4E7G1W82VKFPF5FJ9AVK1DQ", "01M5K15M315JB1H3FYW3VS1S55"];

describe("runRound", () => {
  it("stores one sample per endpoint, launches passing ones, and records new versions as deploys", async () => {
    const db = testD1();
    let version = "1.0.0";
    const asked: string[] = [];
    const counted = (code: string) => {
      const inner = vantage(
        code,
        (url) => url === "https://terrascale.tech/",
        () => version,
      );
      return { fetch: (url: string, init: RequestInit) => (asked.push(code), inner.fetch(url, init)) };
    };
    const env = {
      STATUS_DB: db,
      ...vantageBindings({
        sfo: counted("sfo"),
        gru: counted("gru"),
        fra: counted("fra"),
        nrt: { fetch: async () => new Response("boom", { status: 500 }) },
      }),
    } as unknown as Env;
    const at = Date.parse("2026-09-26T10:00:30Z");
    const first = await runRound(env, at, { streaks: {}, colos: {}, versions: {}, trackers: {} });
    // Retrying the same round stores nothing new.
    const again = await runRound(env, at, first);
    version = "1.1.0";
    await runRound(env, at + 30_000, again);

    expect(asked).toHaveLength(9);
    expect(first.colos).toEqual({ sfo: "SFO", gru: "GRU", fra: "FRA" });
    expect(first.versions).toEqual(Object.fromEntries(READY.map((id) => [id, "1.0.0"])));
    const samples = db.sqlite
      .prepare(
        `SELECT endpoint_id, state, ok, total FROM probe_samples WHERE at = '2026-09-26 10:00:30' ORDER BY endpoint_id`,
      )
      .all();
    expect(samples).toEqual([
      { endpoint_id: "01M4E7G1W82VKFPF5FJ9AVK1D6", state: "operational", ok: 3, total: 3 },
      { endpoint_id: "01M4E7G1W82VKFPF5FJ9AVK1D7", state: "operational", ok: 3, total: 3 },
      // Without DATA_API_CANARY or TERRABASE_PROBES the canaries are not configured, never down.
      { endpoint_id: "01M4E7G1W82VKFPF5FJ9AVK1D9", state: "none", ok: 0, total: 0 },
      { endpoint_id: "01M4E7G1W82VKFPF5FJ9AVK1DB", state: "operational", ok: 3, total: 3 },
      { endpoint_id: "01M4E7G1W82VKFPF5FJ9AVK1DC", state: "operational", ok: 3, total: 3 },
      { endpoint_id: "01M4E7G1W82VKFPF5FJ9AVK1DD", state: "operational", ok: 3, total: 3 },
      { endpoint_id: "01M4E7G1W82VKFPF5FJ9AVK1DF", state: "none", ok: 0, total: 0 },
      { endpoint_id: "01M4E7G1W82VKFPF5FJ9AVK1DG", state: "none", ok: 0, total: 0 },
      { endpoint_id: "01M4E7G1W82VKFPF5FJ9AVK1DH", state: "operational", ok: 3, total: 3 },
      { endpoint_id: "01M4E7G1W82VKFPF5FJ9AVK1DQ", state: "operational", ok: 3, total: 3 },
      { endpoint_id: "01M4E7G1W82VKFPF5FJ9AVK1DR", state: "degraded", ok: 0, total: 3 },
      { endpoint_id: "01M4E7G1W82VKFPF5FJ9AVK1DS", state: "degraded", ok: 0, total: 3 },
      { endpoint_id: "01M4E7G1W82VKFPF5FJ9AVK1DY", state: "degraded", ok: 0, total: 3 },
      { endpoint_id: "01M4E7G1W82VKFPF5FJ9AVK1E0", state: "operational", ok: 3, total: 3 },
      { endpoint_id: "01M4E7G1W82VKFPF5FJ9AVK1E1", state: "none", ok: 0, total: 0 },
      { endpoint_id: "01M4PVGGBHD58R8HYNDBTGFRGJ", state: "operational", ok: 3, total: 3 },
      { endpoint_id: "01M4PVGGBJ24A140MWGJV7MVKV", state: "operational", ok: 3, total: 3 },
      { endpoint_id: "01M4PVGGBJA6S4F64HDQVD2MXM", state: "none", ok: 0, total: 0 },
      { endpoint_id: "01M5K15M315JB1H3FYW3VS1S55", state: "operational", ok: 3, total: 3 },
      { endpoint_id: "01M5ZSKXT0H98ZM78BWKCMY8Y2", state: "none", ok: 0, total: 0 },
      { endpoint_id: "01M5ZT3RJ5W2CY6B2PXNQ8E4KD", state: "none", ok: 0, total: 0 },
    ]);
    // Every endpoint that passed launched with its first round; the website, canaries and unmatched regions wait.
    expect(
      db.sqlite.prepare("SELECT id, live_since FROM endpoints WHERE live_since IS NULL ORDER BY id").all(),
    ).toEqual([
      { id: "01M4E7G1W82VKFPF5FJ9AVK1D9", live_since: null },
      { id: "01M4E7G1W82VKFPF5FJ9AVK1DF", live_since: null },
      { id: "01M4E7G1W82VKFPF5FJ9AVK1DG", live_since: null },
      { id: "01M4E7G1W82VKFPF5FJ9AVK1DR", live_since: null },
      { id: "01M4E7G1W82VKFPF5FJ9AVK1DS", live_since: null },
      { id: "01M4E7G1W82VKFPF5FJ9AVK1DY", live_since: null },
      { id: "01M4E7G1W82VKFPF5FJ9AVK1E1", live_since: null },
      { id: "01M4PVGGBJA6S4F64HDQVD2MXM", live_since: null },
      { id: "01M5ZSKXT0H98ZM78BWKCMY8Y2", live_since: null },
      { id: "01M5ZT3RJ5W2CY6B2PXNQ8E4KD", live_since: null },
    ]);
    expect(db.sqlite.prepare("SELECT DISTINCT live_since FROM endpoints WHERE live_since IS NOT NULL").all()).toEqual([
      { live_since: "2026-09-26 10:00:30" },
    ]);
    expect(db.sqlite.prepare("SELECT endpoint_id, version FROM deploys ORDER BY endpoint_id").all()).toEqual(
      READY.map((id) => ({ endpoint_id: id, version: "1.1.0" })),
    );
    // Every answer is recorded with its round, so coverage can tell who answered last.
    expect(db.sqlite.prepare("SELECT code, colo, seen_at FROM vantages ORDER BY code").all()).toEqual([
      { code: "fra", colo: "FRA", seen_at: "2026-09-26 10:01:00" },
      { code: "gru", colo: "GRU", seen_at: "2026-09-26 10:01:00" },
      { code: "sfo", colo: "SFO", seen_at: "2026-09-26 10:01:00" },
    ]);
  });

  it("probes only active components", async () => {
    const db = testD1();
    db.sqlite.exec("UPDATE components SET lifecycle = 'planned' WHERE id != '01M4E7G1W82VKFPF5FJ9AVK1D3'");
    const env = {
      STATUS_DB: db,
      ...vantageBindings({ sfo: vantage("sfo"), gru: vantage("gru"), fra: vantage("fra"), nrt: vantage("nrt") }),
    } as unknown as Env;
    await runRound(env, Date.parse("2026-09-26T10:00:30Z"), { streaks: {}, colos: {}, versions: {}, trackers: {} });
    expect(db.sqlite.prepare("SELECT endpoint_id FROM probe_samples").all()).toEqual([
      { endpoint_id: "01M4E7G1W82VKFPF5FJ9AVK1DS" },
    ]);
  });

  it("runs with state keyed by retired endpoint IDs and stores none of it", async () => {
    const db = testD1();
    const env = {
      STATUS_DB: db,
      ...vantageBindings({
        sfo: vantage("sfo"),
        gru: vantage("gru"),
        fra: vantage("fra"),
        nrt: { fetch: async () => new Response("boom", { status: 500 }) },
      }),
    } as unknown as Env;
    const at = Date.parse("2026-09-26T10:01:00Z");
    const state = await runRound(env, at, {
      streaks: { legacy: 3 },
      colos: {},
      versions: { legacy: "0.9.0" },
      trackers: {},
    });
    const measured = db.sqlite
      .prepare(
        "SELECT COUNT(*) AS n FROM endpoints e JOIN components c ON c.id = e.component_id WHERE c.lifecycle = 'active'",
      )
      .get() as { n: number };
    expect(
      db.sqlite.prepare("SELECT endpoint_id FROM probe_samples WHERE at = '2026-09-26 10:01:00'").all(),
    ).toHaveLength(measured.n);
    expect(db.sqlite.prepare("SELECT endpoint_id FROM probe_samples WHERE endpoint_id = 'legacy'").all()).toEqual([]);
    expect(db.sqlite.prepare("SELECT endpoint_id FROM deploys WHERE endpoint_id = 'legacy'").all()).toEqual([]);
    expect(state.streaks).not.toHaveProperty("legacy");
  });
});

describe("automatic incidents", () => {
  it.each([null, "2026-09-26 09:00:00"])(
    "never opens or recovers an outage on grant 401 with healthy HTTP siblings, and closes it unconfirmed (live since %s)",
    async (liveSince) => {
      const db = testD1();
      const componentId = "01M4E7G1W82VKFPF5FJ9AVK1CW";
      const endpointId = "01M4E7G1W82VKFPF5FJ9AVK1D9";
      const incidentId = "00000000000000000000000019";
      db.sqlite.prepare("UPDATE components SET lifecycle = 'planned' WHERE id != ?").run(componentId);
      db.sqlite.exec("UPDATE endpoints SET live_since = '2026-09-26 09:00:00'");
      db.sqlite.prepare("UPDATE endpoints SET live_since = ? WHERE id = ?").run(liveSince, endpointId);
      let httpFailure = false;
      const env = {
        STATUS_DB: db,
        DATA_API_CANARY: JSON.stringify({
          [endpointId]: {
            origin: "https://api.example.test",
            issuer: "https://auth.example.test/terrascale",
            region: "gru",
            project: componentId,
            database: "canary",
            collection: "canary",
            document: endpointId,
            key: `${componentId}_${"a".repeat(52)}`,
          },
        }),
        ...vantageBindings(Object.fromEntries(["sfo", "gru"].map((code) => [code, vantage(code, () => httpFailure)]))),
      } as unknown as Env;
      const fetcher = vi
        .fn<typeof fetch>()
        .mockImplementation(async () => Response.json({ error: "invalid_client" }, { status: 401 }));
      vi.stubGlobal("fetch", fetcher);
      try {
        let state: RoundState = { streaks: {}, colos: {}, versions: {}, trackers: {} };
        const start = Date.parse("2026-09-26T10:00:00Z");
        for (let round = 0; round < 3; round++) state = await runRound(env, start + round * 30_000, state);
        expect(db.sqlite.prepare("SELECT count(*) AS n FROM incidents").get()).toEqual({ n: 0 });
        const page = overview(await loadSnapshot(env, snapshotWindow(start + 60_000, "24h")), start + 60_000, "24h");
        expect(
          page.groups.flatMap((group) => group.components).find((entry) => entry.id === componentId),
        ).toMatchObject({
          status: "none",
          note: "Canary credential invalid",
        });
        expect(page.monitoring.issues).toEqual([{ endpointId, componentId, note: "Canary credential invalid" }]);
        db.sqlite
          .prepare(`INSERT INTO incidents (id, title, status, impact, source, auto_key)
        VALUES (?, 'Data API is down', 'monitoring', 'critical', 'auto', ?)`)
          .run(incidentId, componentId);
        state.trackers[componentId] = {
          state: "major_outage",
          since: start,
          incidentId,
          impact: "critical",
          healthySince: start,
        };
        for (let round = 3; round < 25; round++) state = await runRound(env, start + round * 30_000, state);
        // Five unmeasured minutes close it as unconfirmed, never as recovered.
        expect(db.sqlite.prepare("SELECT status, resolved_at FROM incidents").all()).toEqual([
          { status: "resolved", resolved_at: "2026-09-26 10:06:30" },
        ]);
        expect(db.sqlite.prepare("SELECT body FROM incident_updates").all()).toEqual([
          { body: expect.stringContaining("(Canary credential invalid), so this outage is not confirmed") },
        ]);
        expect(
          db.sqlite
            .prepare("SELECT DISTINCT state, ok, total, latency_ms FROM probe_samples WHERE endpoint_id = ?")
            .all(endpointId),
        ).toEqual([{ state: "none", ok: 0, total: 0, latency_ms: null }]);
        expect(db.sqlite.prepare("SELECT live_since FROM endpoints WHERE id = ?").get(endpointId)?.live_since).toBe(
          liveSince,
        );
        await db.batch(rollupStatements(db, [endpointId], start + 12 * 60_000));
        expect(
          db.sqlite.prepare("SELECT count(*) AS n FROM probe_hours WHERE endpoint_id = ?").get(endpointId),
        ).toEqual({ n: 0 });
        expect(fetcher).toHaveBeenCalledTimes(25);
        httpFailure = true;
        state = await runRound(env, start + 25 * 30_000, state);
        state = await runRound(env, start + 26 * 30_000, state);
        state = await runRound(env, start + 27 * 30_000, state);
        expect(db.sqlite.prepare("SELECT status, impact FROM incidents WHERE id != ?").all(incidentId)).toEqual([
          { status: "investigating", impact: "critical" },
        ]);
        expect(state.trackers[componentId]?.healthySince).toBeNull();
        const failed = overview(
          await loadSnapshot(env, snapshotWindow(start + 27 * 30_000, "24h")),
          start + 27 * 30_000,
          "24h",
        );
        expect(
          failed.groups.flatMap((group) => group.components).find((entry) => entry.id === componentId),
        ).toMatchObject({ status: "major_outage", note: null });
        expect(failed.monitoring.issues).toHaveLength(1);
      } finally {
        vi.unstubAllGlobals();
      }
    },
  );

  const run = async (failing: (url: string, round: number) => boolean, rounds = 20) => {
    const db = testD1();
    let round = 0;
    const env = {
      STATUS_DB: db,
      ...vantageBindings(
        Object.fromEntries(
          ["sfo", "gru", "fra", "nrt"].map((code) => [code, vantage(code, (url) => failing(url, round))]),
        ),
      ),
    } as unknown as Env;
    let state = { streaks: {}, colos: {}, versions: {}, trackers: {} };
    const start = Date.parse("2026-09-26T10:00:00Z");
    for (; round < rounds; round++) state = await runRound(env, start + round * 30_000, state);
    return db;
  };

  it("opens an incident for a component down for a minute and resolves it after five healthy minutes", async () => {
    // Up at first, so it launches; down from 10:00:30 to 10:03:00.
    const db = await run((url, round) => url.startsWith("https://docs.") && round >= 1 && round <= 6);
    const incidents = db.sqlite
      .prepare("SELECT title, status, impact, source, auto_key, created_at, resolved_at FROM incidents")
      .all();
    expect(incidents).toEqual([
      {
        title: "Documentation is down",
        status: "resolved",
        impact: "critical",
        source: "auto",
        auto_key: null,
        // Opened a minute after the first failing round.
        created_at: "2026-09-26 10:01:30",
        resolved_at: "2026-09-26 10:08:30",
      },
    ]);
    const updates = db.sqlite.prepare("SELECT status FROM incident_updates ORDER BY created_at").all();
    expect(updates.map((row) => row.status)).toEqual(["investigating", "monitoring", "resolved"]);
    expect(db.sqlite.prepare("SELECT component_id FROM incident_components").all()).toEqual([
      { component_id: "01M4E7G1W82VKFPF5FJ9AVK1CX" },
    ]);
  });

  it("keeps a missing or lagging reported peer down publicly and refreshes each cause once", async () => {
    const db = testD1();
    let round = 0;
    const env = {
      STATUS_DB: db,
      ...vantageBindings(
        Object.fromEntries(
          ["sfo", "gru", "fra", "nrt"].map((code) => {
            const inner = vantage(code, (url) => url.endsWith("/health/replication") && round >= 1 && round <= 3);
            return [
              code,
              {
                fetch: async (url: string, init: RequestInit) => {
                  const response = await inner.fetch(url, init);
                  const body = (await response.json()) as ProbeResponse;
                  if (round >= 4) {
                    for (const result of body.results) {
                      if (!result.key.includes("/health/replication")) continue;
                      result.body = JSON.stringify({
                        status: "ok",
                        region: "gru",
                        maxLagSeconds: round >= 6 ? 31 : null,
                        peers: [{ region: "future", status: "ok", lagSeconds: round >= 6 ? 31 : null }],
                      });
                    }
                  }
                  return Response.json(body);
                },
              },
            ];
          }),
        ),
      ),
    } as unknown as Env;
    let state = { streaks: {}, colos: {}, versions: {}, trackers: {} };
    const start = Date.parse("2026-09-26T10:00:00Z");
    for (; round < 8; round++) state = await runRound(env, start + round * 30_000, state);
    const now = start + 7 * 30_000;
    const page = overview(await loadSnapshot(env, snapshotWindow(now, "24h")), now, "24h");
    const replication = page.groups
      .flatMap((group) => group.components)
      .find((component) => component.name === "Replication")!;
    expect(replication.status).toBe("major_outage");
    expect(replication.clusters.find((cluster) => cluster.regionCode === null)).toMatchObject({
      status: "major_outage",
      note: null,
    });
    expect(
      db.sqlite.prepare("SELECT live_since FROM endpoints WHERE id = '01M4PVGGBHD58R8HYNDBTGFRGJ'").get()?.live_since,
    ).toBe("2026-09-26 10:00:00");
    const updates = db.sqlite.prepare("SELECT status, body FROM incident_updates ORDER BY created_at").all();
    expect(updates).toHaveLength(3);
    expect(updates.map((update) => update.status)).toEqual(["investigating", "investigating", "investigating"]);
    expect(updates[0]?.body).toContain("HTTP 503");
    expect(updates[1]?.body).toContain("future");
    expect(updates[1]?.body).toContain("lag");
    expect(updates[2]?.body).toContain("30");
  });

  it("opens nothing for a service that has not launched yet, and launches it on its first passing round", async () => {
    const db = await run((url, round) => url.startsWith("https://auth.") && round < 8, 10);
    expect(db.sqlite.prepare("SELECT count(*) AS n FROM incidents").get()).toEqual({ n: 0 });
    expect(
      db.sqlite
        .prepare("SELECT DISTINCT live_since FROM endpoints WHERE url LIKE 'https://auth.%' AND kind = 'http'")
        .all(),
    ).toEqual([{ live_since: "2026-09-26 10:04:00" }]);
  });
});

describe("observe", () => {
  it("names the monitor fault of a component nothing could measure", () => {
    const docs = [{ ...endpoints[2]!, componentId: "docs" }];
    const quota = { v: "canary", ok: null, ms: null, e: "Canary quota exhausted" } as const;
    const sample = {
      endpointId: "web",
      state: "none",
      ok: 0,
      total: 0,
      latencyMs: null,
      results: [quota],
      values: null,
    };
    expect(observe(docs, [sample], [{ id: "docs", name: "Document API" }], [])).toEqual([
      expect.objectContaining({ state: "none", monitorFault: "Canary quota exhausted" }),
    ]);
  });
});
