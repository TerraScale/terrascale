import { describe, expect, it } from "vitest";
import type { Env } from "../env";
import { deployments } from "./deployments";
import { vantageBindings } from "../testing/vantages";

describe("regional deployment observations", () => {
  it("checks every fixed service through both vantages and includes their own revisions", async () => {
    const version = "a".repeat(40);
    const fetched: string[] = [];
    const vantage = (colo: string) => ({
      async fetch(_url: string, init: RequestInit) {
        const { targets } = JSON.parse(init.body as string) as {
          targets: { key: string; url: string; headers: string }[];
        };
        for (const target of targets) {
          expect(JSON.parse(target.headers)).toEqual({
            "Cache-Control": "no-cache",
            ...(target.key === "ts-terrabase" ? { "X-TerraScale-Region-Pin": "gru" } : {}),
          });
        }
        fetched.push(...targets.map((target) => target.url));
        return Response.json({
          colo,
          version,
          results: targets.map(({ key }) => ({
            key,
            status: 200,
            body: JSON.stringify({ version }),
          })),
        });
      },
    });
    const rows = await deployments(
      { ...vantageBindings({ gru: vantage("GRU"), sfo: vantage("SFO") }) } as unknown as Env,
      0,
    );
    expect(rows).toHaveLength(10);
    expect(rows.every((row) => row.version === version && row.observedAt === "1970-01-01T00:00:00.000Z")).toBe(true);
    expect(new Set(fetched)).toEqual(
      new Set([
        "https://api.terrascale.tech/healthz",
        "https://auth.terrascale.tech/healthz",
        "https://status.terrascale.tech/health/version",
        "https://dash.terrascale.tech/health/ready",
      ]),
    );
  });

  it("reads the commit from build metadata, as ts-auth reports it", async () => {
    const commit = "b".repeat(40);
    const rows = await deployments(
      {
        ...vantageBindings({
          gru: {
            fetch: async () =>
              Response.json({
                results: [{ key: "ts-auth", status: 200, body: JSON.stringify({ version: `0.1.0-alpha+${commit}` }) }],
              }),
          },
          sfo: {
            fetch: async () => {
              throw new Error("unreachable");
            },
          },
        }),
      } as unknown as Env,
      0,
    );
    expect(rows.find((row) => row.region === "GRU" && row.service === "ts-auth")?.version).toBe(commit);
  });

  it("keeps failed vantages, old version formats and unhealthy responses unknown", async () => {
    const rows = await deployments(
      {
        ...vantageBindings({
          gru: {
            fetch: async () => {
              throw new Error("unreachable");
            },
          },
          sfo: {
            fetch: async () =>
              Response.json({
                version: "old",
                results: [
                  { key: "ts-terrabase", status: 200, body: "not json" },
                  { key: "ts-status", status: 503, body: JSON.stringify({ version: "a".repeat(40) }) },
                  { key: "ts-dashboard", status: 200, body: JSON.stringify({ version: "short" }) },
                ],
              }),
          },
        }),
      } as unknown as Env,
      0,
    );
    expect(rows).toHaveLength(10);
    expect(rows.every((row) => row.version === null)).toBe(true);
    expect(rows.filter((row) => row.region === "GRU").every((row) => row.status === null)).toBe(true);
  });

  it.each([
    null,
    [],
    {},
    { results: {} },
    { results: [null] },
    { results: [[]] },
    { results: [{ key: "ts-terrabase", status: "200" }] },
    { results: [{ key: "ts-terrabase", status: 200.5 }] },
    { results: [{ key: "ts-terrabase", status: 600 }] },
    { results: [{ key: 1, status: 200 }] },
    { results: [{ key: "ts-terrabase", status: 200, body: {} }] },
    { version: 1, results: [] },
    { colo: {}, results: [] },
  ])("keeps malformed GRU payload %j unknown without losing SFO observations", async (body) => {
    const version = "a".repeat(40);
    const rows = await deployments(
      {
        ...vantageBindings({
          gru: { fetch: async () => Response.json(body) },
          sfo: {
            fetch: async () =>
              Response.json({
                version,
                colo: "SFO",
                results: ["ts-terrabase", "ts-auth", "ts-status", "ts-dashboard"].map((key) => ({
                  key,
                  status: 200,
                  body: JSON.stringify({ version }),
                })),
              }),
          },
        }),
      } as unknown as Env,
      0,
    );
    expect(rows).toHaveLength(10);
    const failed = rows.filter((row) => row.region === "GRU");
    expect(failed).toHaveLength(5);
    expect(failed.every((row) => row.status === null && row.version === null && row.colo === null)).toBe(true);
    const healthy = rows.filter((row) => row.region === "SFO");
    expect(healthy).toHaveLength(5);
    expect(healthy.every((row) => row.status === 200 && row.version === version && row.colo === "SFO")).toBe(true);
  });

  it("keeps non-JSON vantage responses unknown", async () => {
    const rows = await deployments(
      {
        ...vantageBindings({
          gru: { fetch: async () => new Response("not JSON") },
          sfo: { fetch: async () => Response.json({ version: "a".repeat(40), results: [] }) },
        }),
      } as unknown as Env,
      0,
    );
    expect(rows).toHaveLength(10);
    expect(rows.filter((row) => row.region === "GRU").every((row) => row.status === null)).toBe(true);
    expect(rows.find((row) => row.region === "SFO" && row.target === "vantage")?.version).toBe("a".repeat(40));
  });
});
