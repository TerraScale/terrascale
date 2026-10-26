import { expect, test } from "vitest";
import { createTerraBaseSystemClient } from "../src/index.js";
import { entity, fixture, json, token } from "./helpers/v1-http.js";
const probe = { status: "unavailable", version: "0.0.1", region: "gru", storageFormat: "1", revision: null, machine: "local" };
test.each([200, 503])("public probes decode redacted status at HTTP %i", async httpStatus => {
  const body = { status: httpStatus === 200 ? "ready" : "unavailable", version: "0.0.1", region: "gru" };
  const { client, requests } = await fixture((_r, response) => json(response, body, httpStatus));
  for (const method of /** @type {const} */ (["readiness", "backup"])) {
    const result = await client.health[method]();
    expect(result).toMatchObject({ ...body, httpStatus });
    expect(result.storageFormat).toBeUndefined();
    expect(result.revision).toBeUndefined();
    expect(result.machine).toBeUndefined();
  }
  expect(requests.every(r => r.headers.authorization === undefined)).toBe(true);
});
test("public liveness still rejects malformed required fields", async () => {
  const { client } = await fixture((_r, response) => json(response, { status: "ok", version: "0.0.1", region: 42 }));
  await expect(client.health.liveness()).rejects.toThrow(TypeError);
});
test("public probes omit authorization and retain readiness and backup 503 bodies", async () => {
  const { client, requests } = await fixture((r, response) => json(response, r.url.pathname === "/health/replication"
    ? { region: "gru", status: "degraded", maxLagSeconds: null, peers: [{ region: "fra", status: "degraded", lagSeconds: null }] }
    : probe, r.url.pathname === "/readyz" || r.url.pathname === "/backupz" ? 503 : 200));
  // Project clients expose the same probes without forwarding their credential.
  expect(await client.health.liveness()).toEqual({ ...probe, httpStatus: 200 });
  expect(await client.health.readiness()).toEqual({ ...probe, httpStatus: 503 });
  expect(await client.health.backup()).toEqual({ ...probe, httpStatus: 503 });
  expect((await client.health.replication()).peers[0]?.lagSeconds).toBeNull();
  expect(requests.map(r => r.url.pathname)).toEqual(["/healthz", "/readyz", "/backupz", "/health/replication"]);
  for (const r of requests) expect(r.headers.authorization).toBeUndefined();
});
test("unscoped system client supports anonymous probes without a project or credential", async () => {
  /** @type {unknown[]} */
  const requests = [];
  const client = createTerraBaseSystemClient({ baseUrl: "https://fixture.invalid", fetch: async (url, init) => {
    requests.push([url, new Headers(init?.headers).get("authorization")]);
    return Response.json(probe);
  } });
  expect((await client.health.liveness()).httpStatus).toBe(200);
  expect(requests).toEqual([["https://fixture.invalid/healthz", null]]);
});
test("platform explore preserves read-only routes, redacted documents and canonical document IDs", async () => {
  const doc = { id: "01ARZ3NDEKTSV4RRFFQ69G5FA3", version: "tbv1-1", state: "value", document: { password: "[redacted]" } };
  const replies = [{ items: [{ name: "ts-auth", id: entity, homeRegion: "gru", replicaRegions: ["fra"], allRegions: false }] },
    { items: [{ id: entity, name: "users", created_at: "1" }], next_cursor: null }, { items: [doc], next_cursor: "opaque+cursor" }, doc];
  const { client, requests } = await fixture((_r, response) => json(response, replies[requests.length - 1]));
  expect(await client.platform.databases()).toEqual(replies[0]);
  expect((await client.platform.collections("ts-auth")).items[0]?.name).toBe("users");
  expect((await client.platform.documents("ts-auth", "users", { limit: 2, cursor: "opaque+cursor" })).nextCursor).toBe("opaque+cursor");
  expect(await client.platform.document("ts-auth", "users", "01ARZ3NDEKTSV4RRFFQ69G5FA3")).toEqual(doc);
  expect(requests.map(r => [r.method, r.url.pathname])).toEqual([
    ["GET", "/v1/platform/databases"], ["GET", "/v1/platform/databases/ts-auth/collections"],
    ["GET", "/v1/platform/databases/ts-auth/collections/users/documents"], ["GET", "/v1/platform/databases/ts-auth/collections/users/documents/by-key"],
  ]);
  expect(requests[3]?.url.searchParams.get("id")).toBe("01ARZ3NDEKTSV4RRFFQ69G5FA3");
  for (const r of requests) expect(r.headers.authorization).toBe(`Bearer ${token}`);
});
test.each([401, 403, 404])("platform refuses status %i without alternate routes or retries", async code => {
  const { client, requests } = await fixture((_r, response) => json(response, { error: { code: "forbidden", message: "Refused" } }, code));
  await expect(client.platform.databases()).rejects.toMatchObject({ status: code });
  expect(requests).toHaveLength(1);
});
test.each(["database_legacy", entity.toLowerCase(), "8".repeat(26)])("platform rejects noncanonical database ID %s", async id => {
  const { client, requests } = await fixture((_r, response) => json(response, {
    items: [{ name: "ts-auth", id, homeRegion: "gru", replicaRegions: [] }],
  }));
  await expect(client.platform.databases()).rejects.toThrow(TypeError);
  expect(requests).toHaveLength(1);
});
