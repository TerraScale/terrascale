import { expect, test } from "vitest";
import { entity, fixture, json, prefix, project } from "./helpers/v1-http.js";
const base = `${prefix}/databases/app`;
const at = "2026-10-22T10:00:00Z";
const backup = { id: entity, databaseId: entity, status: "succeeded", trigger: "manual", sizeBytes: 123, createdAt: at };
const policy = { fullBackupCron: null, retentionDays: 7, disabled: false, updatedAt: null };
const window = { earliest: at, latest: at };
const restored = { id: entity, projectId: project, name: "restored", branchedFrom: entity, createdAt: at, homeRegion: null };
const operation = "01ARZ3NDEKTSV4RRFFQ69G5FA7";
const opRunning = { id: operation, operationId: operation, status: "running", kind: "backup", database: "restored", errorCode: null, errorMessage: null, createdAt: at, finishedAt: null };
const opDone = { id: operation, operationId: operation, status: "completed", kind: "backup", database: "restored", errorCode: null, errorMessage: null, createdAt: at, finishedAt: at };
const destination = { endpoint: "https://bucket.example.invalid", region: "gru", bucket: "fixture", mode: "both", updatedAt: at };
test("all backup routes retain source selectors, pagination and producer DTOs", async () => {
  const replies = [backup, { items: [backup], next: "opaque+after" }, opRunning, opDone, restored, opDone, policy, policy, window,
    { window, pointInTime: at, snapshotAt: at, latestSegmentAt: null, segmentCount: 0, historyCheck: "metadata-only" },
    opRunning, opDone, restored, destination, destination, destination];
  const statuses = [201, 200, 202, 200, 200, 200, 200, 200, 200, 200, 202, 200, 200, 200, 200, 200];
  const { client, requests } = await fixture((request, response) => {
    const i = requests.length - 1;
    if (request.method === "DELETE") { response.writeHead(204).end(); return; }
    json(response, replies[i], statuses[i]);
  });
  const backups = client.database("app").backups;
  expect(await backups.create()).toEqual(backup);
  expect(await backups.list({ after: "opaque+after", limit: 2 })).toEqual(replies[1]);
  expect(await backups.restore(entity, { name: "restored" })).toEqual(restored);
  expect(await backups.getRestoreOperation(operation)).toEqual(opDone);
  expect(await backups.getPolicy()).toEqual(policy);
  expect(await backups.setPolicy({ disabled: false, retentionDays: 7, fullBackupCron: null })).toEqual(policy);
  expect(await backups.window({ destination: "customer" })).toEqual(window);
  expect(await backups.plan({ pointInTime: at, destination: "customer" })).toEqual(replies[9]);
  expect(await backups.restorePointInTime({ pointInTime: at, name: "restored", destination: "platform" })).toEqual(restored);
  expect(await client.backupDestination.get()).toEqual(destination);
  const { updatedAt: _updated, ...config } = destination;
  expect(await client.backupDestination.set({ ...config, mode: "both", accessKeyId: "fixture-id", secretKey: "fixture-only" })).toEqual(destination);
  expect(await client.backupDestination.delete()).toBeUndefined();
  expect(requests.map(r => [r.method, r.url.pathname])).toEqual([
    ["POST", `${base}/backups`], ["GET", `${base}/backups`], ["POST", `${base}/backups/${entity}/restore`],
    ["GET", `${base}/restores/${operation}`], ["GET", `${prefix}/databases/restored`],
    ["GET", `${base}/restores/${operation}`],
    ["GET", `${base}/backup-policy`], ["PUT", `${base}/backup-policy`], ["GET", `${base}/point-in-time`],
    ["POST", `${base}/point-in-time/plan`], ["POST", `${base}/point-in-time/restore`],
    ["GET", `${base}/restores/${operation}`], ["GET", `${prefix}/databases/restored`],
    ["GET", `${prefix}/backup-destination`], ["PUT", `${prefix}/backup-destination`], ["DELETE", `${prefix}/backup-destination`],
  ]);
  expect(requests[8]?.url.searchParams.get("destination")).toBe("customer");
  expect(JSON.stringify(client)).toBe("{}");
});
