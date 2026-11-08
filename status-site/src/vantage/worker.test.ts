import { afterEach, describe, expect, it, vi } from "vitest";
import type { ProbeResponse } from "../probe/protocol";
import worker from "./worker";

const target = { key: "dash", url: "https://dash.test/health/ready", headers: "{}", body: false };
const request = (body: unknown = { targets: [target] }, init: RequestInit = {}) =>
  new Request("https://vantage/gru", { method: "POST", body: JSON.stringify(body), ...init });
const probe = (req = request()) => worker.fetch(req, { VERSION: "a".repeat(40) });

describe("placed vantage Worker", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("rejects invalid requests before contacting targets", async () => {
    const fetch = vi.fn();
    vi.stubGlobal("fetch", fetch);
    expect((await probe(new Request("https://vantage/gru"))).status).toBe(405);
    expect((await probe(request({}))).status).toBe(400);
    expect((await probe(request({ targets: [{ key: "dash" }] }))).status).toBe(400);
    expect((await probe(request(null, { body: "not json" }))).status).toBe(400);
    expect(fetch).not.toHaveBeenCalled();
  });

  it("reports execution colo on every invocation, never the requested or ingress location", async () => {
    let where = "GRU";
    const fetch = vi.fn(async (url: string) => new Response(url.endsWith("/cdn-cgi/trace") ? `colo=${where}\n` : "ok"));
    vi.stubGlobal("fetch", fetch);
    const observed = async () =>
      (await (await probe(request(undefined, { headers: { "cf-placement": "remote-GRU" } }))).json()) as ProbeResponse;
    expect(await observed()).toMatchObject({
      vantage: "gru",
      version: "a".repeat(40),
      colo: "GRU",
      results: [{ key: "dash", status: 200, error: null }],
    });
    where = "EWR";
    expect((await observed()).colo).toBe("EWR");
    expect(fetch.mock.calls.filter(([url]) => url.endsWith("/cdn-cgi/trace"))).toHaveLength(2);
  });

  it.each(["missing", "unreachable"])("keeps location unknown when trace is %s", async (failure) => {
    vi.stubGlobal("fetch", async (url: string) => {
      if (url.endsWith("/cdn-cgi/trace") && failure === "unreachable") throw new Error("network failed");
      return new Response("ok");
    });
    expect(((await (await probe()).json()) as ProbeResponse).colo).toBeNull();
  });
});
