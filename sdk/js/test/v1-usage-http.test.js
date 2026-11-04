import { expect, test } from "vitest";
import { entity, fixture, json, prefix, project } from "./helpers/v1-http.js";
const period = { from: "2026-10-21T00:00:00Z", to: "2026-10-22T00:00:00Z", granularity: "hour", reads: 2, writes: 1 };
const usage = { databaseId: entity, storageBytes: 10, logicalBytes: 4, collections: 1, documents: 1, reads: 2, writes: 1, period, series: [] };
const common = { projectId: project, range: "24h", from: period.from, to: period.to, bucketSeconds: 3600 };
const analytics = { ...common, items: [{ databaseId: entity, databaseName: "app", region: "gru", series: [{ start: period.from,
  requests: { get: 2 }, operations: { read: 2 }, errors: 0, latency: { p50Ms: 0.1, p95Ms: null, p99Ms: null },
  storageBytes: null, clientBandwidth: { bytesSent: 12, bytesReceived: 2 }, replicationBandwidth: { bytesSent: 1, bytesReceived: 3 } }] }] };
const replication = { ...common, items: [{ databaseId: entity, databaseName: "app", region: "gru", status: "stopped",
  replicatedOps: 0, lagMs: null, divergence: null, convergenceMs: null, lastApplied: null, bandwidth: [], series: [] }] };
test("all six usage routes preserve envelopes, buckets, nullable observations and filters", async () => {
  const replies = [{ projectId: project, databases: 1, storageBytes: 15, logicalBytes: 9, fileBytes: 5, documents: 1, reads: 2, writes: 1, period, series: [], items: [usage] },
    usage, analytics, analytics, replication, replication];
  const { client, requests } = await fixture((_r, response) => json(response, replies[requests.length - 1]));
  const db = client.database("app");
  expect(await client.metrics.usage({ granularity: "hour", from: period.from, to: period.to })).toEqual(replies[0]);
  expect(await db.metrics.usage()).toEqual(usage);
  expect(await client.metrics.analytics({ database: entity, range: "24h", region: "gru" })).toEqual(analytics);
  expect(await db.metrics.analytics({ range: "1h" })).toEqual(analytics);
  expect(await client.metrics.replication({ range: "7d" })).toEqual(replication);
  expect(await db.metrics.replication({ range: "30d" })).toEqual(replication);
  expect(requests.map(r => [r.method, r.url.pathname])).toEqual([
    ["GET", `${prefix}/usage`], ["GET", `${prefix}/databases/app/usage`], ["GET", `${prefix}/analytics`],
    ["GET", `${prefix}/databases/app/analytics`], ["GET", `${prefix}/replication`], ["GET", `${prefix}/databases/app/replication`],
  ]);
  expect(Object.fromEntries(requests[0]?.url.searchParams ?? [])).toEqual({ granularity: "hour", from: period.from, to: period.to });
  expect(requests[2]?.url.searchParams.get("database")).toBe(entity);
});
test.each([400, 401, 403, 404, 503])("usage retains HTTP error %i", async status => {
  const { client, requests } = await fixture((_r, response) => json(response, { error: { code: "invalid_query", message: "Refused" } }, status));
  await expect(client.metrics.analytics()).rejects.toMatchObject({ status, code: "invalid_query" });
  expect(requests).toHaveLength(1);
});
test("filters reject malformed IDs, ranges and timestamps before dispatch", async () => {
  const { client, requests } = await fixture((_r, response) => json(response, {}));
  await expect(client.metrics.analytics({ database: "db-prefixed" })).rejects.toThrow(TypeError);
  await expect(client.metrics.usage({ from: "yesterday" })).rejects.toThrow(TypeError);
  await expect(client.metrics.replication(/** @type {any} */ ({ range: "all" }))).rejects.toThrow(TypeError);
  expect(requests).toHaveLength(0);
});
