import { expect, test } from "vitest";
import { entity, fixture, json, prefix, project } from "./helpers/v1-http.js";
const base = `${prefix}/databases/app`;
const at = "2026-10-22T10:00:00Z";
const backup = { id: entity, databaseId: entity, status: "succeeded", trigger: "manual", sizeBytes: 123, createdAt: at };
const policy = { fullBackupCron: null, retentionDays: 7, disabled: false, updatedAt: null };
const window = { earliest: at, latest: at };
const restored = { id: entity, projectId: project, name: "restored", branchedFrom: entity, createdAt: at, homeRegion: null, replicaRegions: null, placementVersion: 0, replicas: [] };
const destination = { endpoint: "https://bucket.example.invalid", region: "gru", bucket: "fixture", mode: "both", updatedAt: at };
test("all eleven backup routes retain source selectors, pagination and producer DTOs", async () => {
  const replies = [backup, { items: [backup], next: "opaque+after" }, restored, policy, policy, window,
    { window, pointInTime: at, snapshotAt: at, latestSegmentAt: null, segmentCount: 0, historyCheck: "metadata-only" }, restored,
    destination, destination];
  const { client, requests } = await fixture((request, response) => {
    const i = requests.length - 1;
    if (request.method === "DELETE") { response.writeHead(204).end(); return; }
    json(response, replies[i], [0, 2, 7].includes(i) ? 201 : 200);
  });
  const backups = client.database("app").backups;
  expect(await backups.create()).toEqual(backup);
  expect(await backups.list({ after: "opaque+after", limit: 2 })).toEqual(replies[1]);
  expect(await backups.restore(entity, { name: "restored" })).toEqual(restored);
  expect(await backups.getPolicy()).toEqual(policy);
  expect(await backups.setPolicy({ disabled: false, retentionDays: 7, fullBackupCron: null })).toEqual(policy);
  expect(await backups.window({ destination: "customer" })).toEqual(window);
  expect(await backups.plan({ pointInTime: at, destination: "customer" })).toEqual(replies[6]);
  expect(await backups.restorePointInTime({ pointInTime: at, name: "restored", destination: "platform" })).toEqual(restored);
  expect(await client.backupDestination.get()).toEqual(destination);
  const { updatedAt: _updated, ...config } = destination;
  expect(await client.backupDestination.set({ ...config, mode: "both", accessKeyId: "fixture-id", secretKey: "fixture-only" })).toEqual(destination);
  expect(await client.backupDestination.delete()).toBeUndefined();
  expect(requests.map(r => [r.method, r.url.pathname])).toEqual([
    ["POST", `${base}/backups`], ["GET", `${base}/backups`], ["POST", `${base}/backups/${entity}/restore`],
    ["GET", `${base}/backup-policy`], ["PUT", `${base}/backup-policy`], ["GET", `${base}/point-in-time`],
    ["POST", `${base}/point-in-time/plan`], ["POST", `${base}/point-in-time/restore`],
    ["GET", `${prefix}/backup-destination`], ["PUT", `${prefix}/backup-destination`], ["DELETE", `${prefix}/backup-destination`],
  ]);
  expect(requests[5]?.url.searchParams.get("destination")).toBe("customer");
  expect(JSON.stringify(client)).toBe("{}");
});
test.each([400, 401, 403, 404, 409, 421, 429, 503])("restore errors %i are preserved without replay", async status => {
  const { client, requests } = await fixture((_r, response) => json(response, { error: { code: "restore_refused", message: "Refused" } }, status));
  await expect(client.database("app").backups.restore(entity, { name: "restored" })).rejects.toMatchObject({ status, code: "restore_refused" });
  expect(requests).toHaveLength(1);
});
test.each([{}, { name: null }])("restore lets the producer choose the name for %j", async request => {
  const { client, requests } = await fixture((_r, response) => json(response, restored, 201));
  const backups = client.database("app").backups;
  expect(await backups.restore(entity, request)).toEqual(restored);
  expect(await backups.restorePointInTime({ pointInTime: at, ...request })).toEqual(restored);
  expect(JSON.parse(requests[0]?.body ?? "")).toEqual(request);
  expect(JSON.parse(requests[1]?.body ?? "")).toEqual({ pointInTime: at, ...request });
});
test("restore retains assigned replicas and exact placement versions", async () => {
  const body = { ...restored, homeRegion: "gru", replicaRegions: ["fra"], replicas: ["fra"] };
  const { client } = await fixture((_r, response) => {
    response.writeHead(201, { "content-type": "application/json" });
    response.end(JSON.stringify(body).replace('"placementVersion":0', '"placementVersion":9007199254740993'));
  });
  const result = await client.database("app").backups.restore(entity);
  expect(result.replicas).toEqual(["fra"]);
  expect(result.placementVersion?.toString()).toBe("9007199254740993");
});
