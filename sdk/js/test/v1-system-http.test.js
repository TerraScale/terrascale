import { expect, test } from "vitest";
import { fixture, json } from "./helpers/v1-http.js";
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
