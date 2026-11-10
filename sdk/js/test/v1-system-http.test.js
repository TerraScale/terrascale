import { expect, test } from "vitest";
import { createTerraBaseClient } from "../src/index.js";
import { fixture, json, project, token } from "./helpers/v1-http.js";
const probe = { status: "unavailable", version: "0.0.1", region: "gru", storageFormat: "1", revision: null, machine: "local" };
test("public probes omit authorization and retain readiness and backup 503 bodies", async () => {
  const { client, requests } = await fixture((r, response) => json(response, probe, r.url.pathname === "/readyz" || r.url.pathname === "/backupz" ? 503 : 200));
  // Project clients expose the same probes without forwarding their credential.
  expect(await client.health.liveness()).toEqual({ ...probe, httpStatus: 200 });
  expect(await client.health.readiness()).toEqual({ ...probe, httpStatus: 503 });
  expect(await client.health.backup()).toEqual({ ...probe, httpStatus: 503 });
  expect(requests.map(r => r.url.pathname)).toEqual(["/healthz", "/readyz", "/backupz"]);
  for (const r of requests) expect(r.headers.authorization).toBeUndefined();
});

test("listRegions reads the anonymous ordered region catalog without authorization", async () => {
  const { client, requests } = await fixture((_request, response) => json(response, { regions: ["gru", "sjc"] }));
  const regions = await client.regions.list();
  expect(regions).toEqual({ regions: ["gru", "sjc"] });
  expect(Object.isFrozen(regions.regions)).toBe(true);
  expect(requests.map(r => `${r.method} ${r.url.pathname}`)).toEqual(["GET /v1/regions"]);
  expect(requests[0]?.headers.authorization).toBeUndefined();
});

test("a configured region is sent as X-TerraScale-Region-Pin", async () => {
  const { requests, baseUrl } = await fixture((_request, response) => json(response, probe));
  const config = { project, token, baseUrl, region: "gru" };
  const client = createTerraBaseClient(config);
  await client.health.liveness();
  expect(requests.at(-1)?.headers["x-terrascale-region-pin"]).toBe("gru");
});
