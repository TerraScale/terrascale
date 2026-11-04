import { expect, test } from "vitest";
import { entity, fixture, json, prefix, project } from "./helpers/v1-http.js";
const period = { from: "2026-10-21T00:00:00Z", to: "2026-10-22T00:00:00Z", granularity: "hour", reads: 2, writes: 1 };
const usage = { databaseId: entity, storageBytes: 10, reads: 2, writes: 1, period, series: [] };
const month = { month: "2026-10", readUnits: 2, writeUnits: 1, units: 1, organizationUnits: 1, organizationReadUnits: 2 };
const common = { projectId: project, range: "24h", from: period.from, to: period.to, bucketSeconds: 3600 };
const analytics = { ...common, items: [{ databaseId: entity, databaseName: "app", region: "gru", series: [{ start: period.from,
  requests: { get: 2 }, operations: { read: 2 }, errors: 0, latency: { p50Ms: 0.1, p95Ms: null, p99Ms: null },
  storageBytes: null, clientBandwidth: { bytesSent: 12, bytesReceived: 2 }, replicationBandwidth: { bytesSent: 1, bytesReceived: 3 } }] }] };
test("all four usage routes preserve envelopes, buckets, nullable observations and filters", async () => {
  const replies = [{ projectId: project, databases: 1, storageBytes: 15, fileBytes: 5, reads: 2, writes: 1, period, series: [], items: [usage], month },
    usage, analytics, analytics];
  const { client, requests } = await fixture((_r, response) => json(response, replies[requests.length - 1]));
  const db = client.database("app");
  expect(await client.metrics.usage({ granularity: "hour", from: period.from, to: period.to })).toEqual(replies[0]);
  expect(await db.metrics.usage()).toEqual(usage);
  expect(await client.metrics.analytics({ database: entity, range: "24h", region: "gru" })).toEqual(analytics);
  expect(await db.metrics.analytics({ range: "1h" })).toEqual(analytics);
  expect(requests.map(r => [r.method, r.url.pathname])).toEqual([
    ["GET", `${prefix}/usage`], ["GET", `${prefix}/databases/app/usage`], ["GET", `${prefix}/analytics`],
    ["GET", `${prefix}/databases/app/analytics`],
  ]);
  expect(Object.fromEntries(requests[0]?.url.searchParams ?? [])).toEqual({ granularity: "hour", from: period.from, to: period.to });
  expect(requests[2]?.url.searchParams.get("database")).toBe(entity);
});
