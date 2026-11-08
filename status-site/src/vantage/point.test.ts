import { afterEach, describe, expect, it, vi } from "vitest";
import type { Env } from "../env";
import type { ProbeResponse } from "../probe/protocol";
import { VantagePoint } from "./point";

const target = { key: "dash", url: "https://dash.test/health/ready", headers: "{}", body: false };

function vantagePoint() {
  const point = new VantagePoint({} as DurableObjectState, { VERSION: "a".repeat(40) } as Env);
  return (init: RequestInit = {}) => point.fetch(new Request("https://vantage/gru", { method: "POST", ...init }));
}

/** Answers the data center trace with `colo`, and every target with 200. */
const network = (colo: string | null) =>
  vi.fn(async (url: string) =>
    url.endsWith("/cdn-cgi/trace") && colo ? new Response(`h=www.cloudflare.com\ncolo=${colo}\n`) : new Response("ok"),
  );

describe("vantage point", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("only accepts POST", async () => {
    expect((await vantagePoint()({ method: "GET" })).status).toBe(405);
  });

  it("rejects a missing or malformed target list", async () => {
    const probe = vantagePoint();
    expect((await probe({ body: "not json" })).status).toBe(400);
    expect((await probe({ body: JSON.stringify({}) })).status).toBe(400);
    expect((await probe({ body: JSON.stringify({ targets: [{ key: "dash" }] }) })).status).toBe(400);
  });

  it("probes every target and reports its vantage, revision and data center once", async () => {
    const fetch = network("GRU");
    vi.stubGlobal("fetch", fetch);
    const probe = vantagePoint();
    await probe({ body: JSON.stringify({ targets: [target] }) });
    const body = (await (await probe({ body: JSON.stringify({ targets: [target] }) })).json()) as ProbeResponse;
    expect(body).toMatchObject({
      vantage: "gru",
      version: "a".repeat(40),
      colo: "GRU",
      results: [{ key: "dash", status: 200, error: null }],
    });
    expect(fetch.mock.calls.filter(([url]) => url.endsWith("/cdn-cgi/trace"))).toHaveLength(1);
  });

  it("looks the data center up again after a failed lookup", async () => {
    vi.stubGlobal("fetch", network(null));
    const probe = vantagePoint();
    expect(((await (await probe({ body: JSON.stringify({ targets: [target] }) })).json()) as ProbeResponse).colo).toBe(
      null,
    );
    vi.stubGlobal("fetch", network("GRU"));
    expect(((await (await probe({ body: JSON.stringify({ targets: [target] }) })).json()) as ProbeResponse).colo).toBe(
      "GRU",
    );
  });
});
