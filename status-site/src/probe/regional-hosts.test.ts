import { describe, expect, it } from "vitest";
import { testD1 } from "../testing/d1";
import { parseSpec } from "../domain/assertions";
import { evaluate, targets, type RoundEndpoint } from "./round";
import { check } from "./check";
import { discoverRegionalHosts, REGIONAL_ORIGINS } from "./regional-hosts";

describe("regional host probes", () => {
  it("discovers advertised origins and fails a host answered by another region", async () => {
    const db = testD1();
    const advertised = { regions: [{ id: "new-region", apiUrl: "https://edge-new.terrascale.tech" }] };
    const fetcher = (async (url: string) => {
      expect(url).toBe(REGIONAL_ORIGINS);
      return Response.json(advertised);
    }) as typeof fetch;
    await discoverRegionalHosts(db, fetcher);
    await discoverRegionalHosts(db, fetcher);
    const rows = db.sqlite
      .prepare("SELECT * FROM endpoints WHERE url = 'https://edge-new.terrascale.tech/healthz'")
      .all();
    expect(rows).toHaveLength(1);
    const row = rows[0]!;
    const endpoint: RoundEndpoint = {
      id: String(row.id),
      url: String(row.url),
      headers: String(row.headers),
      spec: parseSpec({
        expectStatus: String(row.expect_status),
        keyword: null,
        assertions: String(row.assertions),
        extract: "{}",
        indirect: 0,
      }),
    };
    let served = "new-region";
    const observe = async () => {
      const result = await check(targets([endpoint])[0]!, (async (url: string) => {
        expect(url).toBe("https://edge-new.terrascale.tech/healthz");
        return Response.json({ status: "ok", region: served });
      }) as typeof fetch);
      return evaluate([endpoint], [{ vantage: "gru", colo: "GRU", results: [result] }], {}).samples[0];
    };
    expect(await observe()).toMatchObject({ ok: 1, total: 1 });
    served = "sjc";
    expect(await observe()).toMatchObject({ ok: 0, total: 1 });
  });

  it("keeps monitoring known hosts when discovery fails", async () => {
    const db = testD1();
    const fetcher = (async () =>
      Response.json({ regions: [{ id: "gru", apiUrl: "https://gru.api.terrascale.tech" }] })) as typeof fetch;
    await discoverRegionalHosts(db, fetcher);
    const before = db.sqlite.prepare("SELECT id, url FROM endpoints ORDER BY id").all();
    await discoverRegionalHosts(db, (async () => new Response(null, { status: 503 })) as typeof fetch);
    expect(db.sqlite.prepare("SELECT id, url FROM endpoints ORDER BY id").all()).toEqual(before);
  });
});
