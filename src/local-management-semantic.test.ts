import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

import { toUlid, type Ulid } from "./ids.js";
import type { TerraScaleResult } from "./result.js";
import {
  createTerraScaleSqliteRuntime,
  type TerraScaleSqliteRuntime,
} from "./local/index.js";
import { TerraScaleSqliteStore } from "./local/store.js";
import { installLocalSqliteTestEnvironment } from "./local/test-environment.test.js";

installLocalSqliteTestEnvironment();

const userId = toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAV");
const secondUserId = toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAW");

function value<T>(result: TerraScaleResult<T>): T {
  if (!result.ok) throw new Error(`${result.routeName}: ${result.error.code}: ${result.error.message}`);
  return result.value;
}

function failure<T>(result: TerraScaleResult<T>, code: string): void {
  assert.equal(result.ok, false);
  if (!result.ok) assert.equal(result.error.code, code);
}

async function withRuntime<T>(work: (runtime: TerraScaleSqliteRuntime, path: string) => Promise<T>): Promise<T> {
  const directory = await mkdtemp(join(tmpdir(), "terrascale-management-semantic-"));
  const path = join(directory, "management.sqlite");
  const runtime = createTerraScaleSqliteRuntime({ sqlite: { enabled: true, databasePath: path } });
  try {
    return await work(runtime, path);
  } finally {
    await runtime.close();
    await rm(directory, { recursive: true, force: true });
  }
}

function client(runtime: TerraScaleSqliteRuntime, projectId?: Ulid) {
  return runtime.createClient({
    accessToken: "local-test-token",
    ...(projectId === undefined ? {} : { projectId }),
  });
}

test("organizations, regions, and saved-query versions have real CRUD, isolation, and conflict semantics", async () => {
  await withRuntime(async (runtime) => {
    const root = client(runtime);
    const regions = value(await root.regions.list());
    assert.deepEqual(regions.regions.map((region) => region.code), ["GRU", "SFO", "FRA"]);
    assert.equal(value(await root.regions.get("GRU")).code, "GRU");

    const organization = value(await root.organizations.create({ name: "Semantic organization" }));
    const otherOrganization = value(await root.organizations.create({ name: "Isolated organization" }));
    assert.equal(value(await root.organizations.get(organization.organizationId)).name, "Semantic organization");
    assert.equal(value(await root.organizations.list()).length, 2);
    assert.equal(value(await root.organizations.update(organization.organizationId, { name: "Renamed organization" })).name, "Renamed organization");

    const project = value(await root.organizations.createProject(organization.organizationId, {
      name: "Semantic project", slug: "semantic", regions: ["GRU", "SFO"], backupRegion: "GRU",
    }));
    const otherProject = value(await root.organizations.createProject(otherOrganization.organizationId, {
      name: "Other project", regions: ["FRA"], backupRegion: "FRA",
    }));
    assert.deepEqual(value(await root.organizations.listProjects(organization.organizationId)).map((item) => item.projectId), [project.projectId]);
    assert.equal(value(await root.organizations.getProject(project.projectId)).name, "Semantic project");
    assert.deepEqual(value(await root.organizations.listProjectRegionPlacements(project.projectId)).map((item) => item.region), ["GRU", "SFO"]);
    assert.equal(value(await root.organizations.updateProject(project.projectId, { name: "Updated project", regions: ["FRA"], backupRegion: "FRA" })).placementGeneration, 2);
    assert.deepEqual(value(await root.organizations.listProjectRegionPlacements(project.projectId)).map((item) => item.region), ["FRA"]);

    assert.equal(value(await root.organizations.listMembers(organization.organizationId)).length, 1);
    assert.equal(value(await root.organizations.updateMemberRole(organization.organizationId, userId, { role: "Member" })).role, "Member");

    const accepted = value(await root.organizations.createInvitation(organization.organizationId, {
      email: "accepted@example.test", projectId: project.projectId, responseUrlTemplate: "https://example.test/accept",
    }));
    assert.equal(value(await root.organizations.listInvitations(organization.organizationId)).length, 1);
    assert.equal(value(await root.organizations.resendInvitation(organization.organizationId, accepted.invitationId)).sentCount, 2);
    assert.equal(value(await root.organizations.acceptInvitation(organization.organizationId, accepted.invitationId)).status, "Accepted");
    const declined = value(await root.organizations.createInvitation(organization.organizationId, { email: "declined@example.test", responseUrlTemplate: "https://example.test/decline" }));
    assert.equal(value(await root.organizations.declineInvitation(organization.organizationId, declined.invitationId)).status, "Declined");
    const canceled = value(await root.organizations.createInvitation(organization.organizationId, { email: "canceled@example.test", responseUrlTemplate: "https://example.test/cancel" }));
    assert.equal(value(await root.organizations.cancelInvitation(organization.organizationId, canceled.invitationId)).status, "Canceled");

    assert.equal(value(await root.organizations.removeMember(organization.organizationId, userId)), undefined);
    const rejoin = value(await root.organizations.createInvitation(organization.organizationId, { email: "rejoin@example.test", responseUrlTemplate: "https://example.test/rejoin" }));
    value(await root.organizations.acceptInvitation(organization.organizationId, rejoin.invitationId));
    assert.equal(value(await root.organizations.leave(organization.organizationId)), undefined);
    assert.equal(value(await root.organizations.listMembers(organization.organizationId)).length, 0);

    const scoped = client(runtime, project.projectId);
    const saved = value(await scoped.savedQueries.create({ name: "Initial query", query: "from docs" }));
    assert.equal(value(await scoped.savedQueries.list()).items.length, 1);
    const updated = value(await scoped.savedQueries.update(saved.savedQueryId, { name: "Updated query", query: "from docs where active = true", revision: saved.revision }));
    assert.equal(updated.name, "Updated query");
    assert.equal(updated.revision, "2");
    const fetched = value(await scoped.savedQueries.get(saved.savedQueryId));
    assert.equal(fetched.revision, "2");
    const revisionUpdated = value(await scoped.savedQueries.update(saved.savedQueryId, { name: "Revision query", query: "from docs where revision = true", revision: fetched.revision }));
    assert.equal(revisionUpdated.revision, "3");
    failure(await scoped.savedQueries.update(saved.savedQueryId, { name: "Stale", query: "from docs", revision: fetched.revision }), "conflict");
    const page = value(await scoped.savedQueries.list(undefined, { take: 10 }));
    assert.equal(page.items[0]?.revision, "3");
    assert.equal(page.totalCount, 1);
    assert.equal(page.nextCursor, null);
    failure(await scoped.savedQueries.list(undefined, { cursor: "10junk" }), "invalid_request");
    assert.equal(value(await scoped.savedQueries.delete(saved.savedQueryId)).deleted, true);
    assert.equal(value(await scoped.savedQueries.list()).items.length, 0);
    assert.equal(value(await client(runtime, otherProject.projectId).savedQueries.list()).items.length, 0);

    assert.equal(value(await root.organizations.requestDeletion(organization.organizationId, { confirm: true, organizationName: "Renamed organization" })).status, "DeletionPending");
    assert.equal(value(await root.organizations.deleteProject(project.projectId, { confirm: true, projectName: "Updated project" })).deleted, true);
    assert.equal(value(await root.organizations.listProjects(organization.organizationId)).length, 0);
    assert.equal(value(await root.organizations.getProject(otherProject.projectId)).name, "Other project");
  });
});

test("access and usage persist through SQLite reopen", async () => {
  await withRuntime(async (first, path) => {
    const seed = await first.seed();
    const root = client(first);
    const scoped = client(first, seed.projectId);
    const otherProject = value(await root.organizations.createProject(seed.organizationId, {
      name: "Second project", regions: ["FRA"], backupRegion: "FRA",
    }));
    const access = scoped.access;
    assert.equal(value(await access.listTeamMembers()).totalCount, 0);
    assert.equal(value(await access.addTeamMember({ userId: secondUserId, role: "viewer" })).role, "viewer");
    failure(await access.addTeamMember({ userId: secondUserId, role: "viewer" }), "conflict");
    assert.equal(value(await access.updateTeamMember(secondUserId, { role: "member" })).role, "member");
    assert.equal(value(await access.removeTeamMember(secondUserId)), undefined);
    assert.equal(value(await access.listTeamMembers()).totalCount, 0);
    assert.equal(value(await client(first, otherProject.projectId).access.listTeamMembers()).totalCount, 0);

    assert.equal(value(await scoped.usage.getProjectUsage()).projectId, seed.projectId);
    assert.equal(value(await scoped.usage.getProjectUsageWorkload("7d")).range, "7d");
    assert.equal(value(await scoped.usage.getProjectLatency(undefined, { latitude: -23.5, longitude: -46.6 })).projectId, seed.projectId);
    assert.equal(value(await scoped.usage.getProjectUsageHistory("30d")).range, "30d");
    assert.equal(value(await root.usage.getOrganizationUsage(seed.organizationId)).organizationId, seed.organizationId);

    await first.close();
    const reopened = createTerraScaleSqliteRuntime({ sqlite: { enabled: true, databasePath: path } });
    try {
      const reopenedScoped = client(reopened, seed.projectId);
      assert.equal(value(await reopenedScoped.access.listTeamMembers()).totalCount, 0);
      assert.equal(value(await client(reopened).organizations.get(seed.organizationId)).organizationId, seed.organizationId);
    } finally {
      await reopened.close();
    }
  });
});

test("audit and notifications read, isolate, and persist valid SQLite records", async () => {
  await withRuntime(async (initial, path) => {
    const seed = await initial.seed();
    await initial.close();

    const store = new TerraScaleSqliteStore(path);
    const auditId = toUlid("01J00000000000000000000010");
    const firstNotification = toUlid("01J00000000000000000000011");
    const secondNotification = toUlid("01J00000000000000000000012");
    const otherNotification = toUlid("01J00000000000000000000013");
    const now = "2026-08-28T12:00:00.000Z";
    store.put("audit", seed.projectId, auditId, { auditEventId: auditId, projectId: seed.projectId, actorUserId: userId, action: "document.created", targetType: "document", occurredAt: now, metadata: { source: "semantic-test" } });
    for (const [id, value] of [
      [firstNotification, { notificationId: firstNotification, userId, organizationId: seed.organizationId, projectId: seed.projectId, title: "Unread project", body: "first", severity: "info", createdAt: now, category: "project" }],
      [secondNotification, { notificationId: secondNotification, userId, organizationId: seed.organizationId, projectId: seed.projectId, title: "Unread warning", severity: "warning", createdAt: now, category: "operations" }],
      [otherNotification, { notificationId: otherNotification, userId: secondUserId, projectId: seed.projectId, title: "Other user", severity: "info", createdAt: now, category: "project" }],
    ] as const) store.put("notifications", "local", id, value);
    store.close();

    const reopened = createTerraScaleSqliteRuntime({ sqlite: { enabled: true, databasePath: path } });
    try {
      const scoped = client(reopened, seed.projectId);
      const audit = value(await scoped.audit.list());
      assert.equal(audit.totalCount, 1);
      assert.equal(value(await scoped.audit.get(auditId)).action, "document.created");
      assert.equal(value(await client(reopened, toUlid("01J00000000000000000000000")).audit.list()).totalCount, 0);

      const notifications = value(await scoped.notifications.list({ projectId: seed.projectId, unreadOnly: true }));
      assert.equal(notifications.length, 2);
      assert.equal(value(await scoped.notifications.markRead(firstNotification)).readAt !== undefined, true);
      assert.equal(value(await scoped.notifications.list({ projectId: seed.projectId, unreadOnly: true })).length, 1);
      assert.equal(value(await scoped.notifications.markAllRead({ organizationId: seed.organizationId })).markedRead, 1);
      assert.equal(value(await scoped.notifications.list({ projectId: seed.projectId, unreadOnly: true })).length, 0);
      assert.equal(value(await scoped.notifications.list({ projectId: seed.projectId })).length, 2);
    } finally {
      await reopened.close();
    }
  });
});
