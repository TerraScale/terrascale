import assert from "node:assert/strict";
import { mkdtemp, rm, stat } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

import { toUlid, type DatabaseId, type OrganizationId, type ProjectId, type Ulid } from "./ids.js";
import {
  createTerraScaleSqliteRuntime,
  type TerraScaleSqliteRuntime,
} from "./local/index.js";
import { TerraScaleSqliteStore } from "./local/store.js";
import type * as Models from "./models.js";
import type { TerraScaleResult } from "./result.js";

function expectOk<T>(result: TerraScaleResult<T>): T {
  if (!result.ok) throw new Error(`${result.routeName}: ${result.error.message}`);
  return result.value;
}

function expectFailure<T>(result: TerraScaleResult<T>, code: string): void {
  assert.equal(result.ok, false);
  if (result.ok) return;
  assert.equal(result.error.code, code);
}

async function withTempPath<T>(work: (databasePath: string) => Promise<T>): Promise<T> {
  const directory = await mkdtemp(join(tmpdir(), "terrascale-admin-semantic-"));
  const databasePath = join(directory, "admin.sqlite");
  try {
    return await work(databasePath);
  } finally {
    await rm(directory, { recursive: true, force: true });
    await assert.rejects(stat(directory));
  }
}

function closeRuntime(runtime: TerraScaleSqliteRuntime): Promise<void> {
  return runtime.close();
}

test("AdminClient has semantic SQLite coverage across its complete public surface", async () => {
  await withTempPath(async (databasePath) => {
    const email = `admin-semantic-${randomUUID()}@example.test`;
    const userId = toUlid("01J00000000000000000000003");
    let organizationId = "" as OrganizationId;
    let projectId = "" as ProjectId;
    let databaseId = "" as DatabaseId;
    let supportNoteId = "" as Ulid;

    const seed = new TerraScaleSqliteStore(databasePath);
    seed.put("users", "global", userId, {
      userId,
      email,
      name: "Semantic Admin User",
      displayName: "Semantic Admin User",
      enabled: true,
      owner: true,
      organizationIds: [],
      createdAt: "2026-08-28T00:00:00.000Z",
    });
    seed.close();
    const runtime = createTerraScaleSqliteRuntime({ sqlite: { enabled: true, databasePath } });

    try {
      const root = runtime.createClient();
      const admin = runtime.createAdminClient();

      const organization = expectOk(await root.organizations.create({
        name: "Semantic Admin Organization",
      }));
      organizationId = organization.organizationId;
      assert.equal(organization.name, "Semantic Admin Organization");
      assert.equal(organization.status, "Active");

      const project = expectOk(await root.organizations.createProject(organizationId, {
        name: "Semantic Admin Project",
        regions: ["local"],
        backupRegion: "local",
      }));
      projectId = project.projectId;
      assert.equal(project.organizationId, organizationId);
      assert.deepEqual(project.regions, ["local"]);

      const scopedRoot = runtime.createClient({ projectId });
      const database = expectOk(await scopedRoot.databases.create({ name: "Semantic Admin Database" }));
      databaseId = database.databaseId;
      assert.equal(database.name, "Semantic Admin Database");
      assert.equal(database.projectId, projectId);

      // overview (1) and getUsage (10) both describe the root-client-created resources.
      const overview = expectOk(await admin.overview());
      assert.equal(overview.organizations, 1);
      assert.equal(overview.projects, 1);
      assert.equal(overview.databases, 1);
      assert.ok(overview.usage.some((metric) => metric.name === "operations" && metric.quantity === 0));

      // listUsers (2) and getUser (3) return the seeded local admin user with meaningful fields.
      const users = expectOk(await admin.listUsers({ search: email }));
      assert.equal(users.totalCount, 1);
      assert.equal(users.items[0]?.userId, userId);
      assert.equal(users.items[0]?.email, email);
      assert.equal(users.items[0]?.displayName, "Semantic Admin User");
      assert.equal(users.items[0]?.enabled, true);
      const fetchedUser = expectOk(await admin.getUser(userId));
      assert.equal(fetchedUser.userId, userId);
      assert.equal(fetchedUser.email, email);
      assert.equal(fetchedUser.enabled, true);

      // setUserEnabled (4) has observable disable and re-enable transitions.
      const disabled = expectOk(await admin.setUserEnabled(userId, false));
      assert.equal(disabled.enabled, false);
      assert.equal(expectOk(await admin.getUser(userId)).enabled, false);
      const enabled = expectOk(await admin.setUserEnabled(userId, true));
      assert.equal(enabled.enabled, true);

      // createSupportNote (6) preserves all supplied relationships and content.
      const supportNote = expectOk(await admin.createSupportNote({
        userId,
        organizationId,
        projectId,
        note: "Investigated semantic admin fixture",
      }));
      supportNoteId = supportNote.supportNoteId;
      assert.equal(supportNote.userId, userId);
      assert.equal(supportNote.organizationId, organizationId);
      assert.equal(supportNote.projectId, projectId);
      assert.equal(supportNote.note, "Investigated semantic admin fixture");
      assert.ok(Date.parse(supportNote.createdAt) > 0);

      // listOrganizations (7), listProjects (8), listDatabases (9).
      const organizations = expectOk(await admin.listOrganizations({ take: 10 }));
      assert.equal(organizations.totalCount, 1);
      assert.equal(organizations.items[0]?.organizationId, organizationId);
      assert.equal(organizations.items[0]?.name, "Semantic Admin Organization");
      const projects = expectOk(await admin.listProjects({ take: 10 }));
      assert.equal(projects.totalCount, 1);
      assert.equal(projects.items[0]?.projectId, projectId);
      assert.equal(projects.items[0]?.organizationId, organizationId);
      const databases = expectOk(await admin.listDatabases({ take: 10 }));
      assert.equal(databases.totalCount, 1);
      assert.equal(databases.items[0]?.databaseId, databaseId);
      assert.equal(databases.items[0]?.projectId, projectId);

      const usage = expectOk(await admin.getUsage());
      assert.equal(usage.organizations, overview.organizations);
      assert.equal(usage.projects, overview.projects);
      assert.equal(usage.databases, overview.databases);

      // deleteUser (5) removes the seeded local admin user.
      expectOk(await admin.deleteUser(userId));
      expectFailure(await admin.getUser(userId), "not_found");
      assert.equal(expectOk(await admin.listUsers({ search: email })).totalCount, 0);
    } finally {
      await closeRuntime(runtime);
    }

    // Verify the deletion at the durable store boundary, and add read-only admin fixtures.
    const durable = new TerraScaleSqliteStore(databasePath);
    try {
      const userRecords = durable.list<Record<string, unknown>>("users", "global");
      assert.equal(userRecords.some((record) => record.userId === userId), false);
      const persistedSupportNote = durable.get<Models.AdminSupportNote>("support-notes", "local", supportNoteId);
      assert.equal(persistedSupportNote?.note, "Investigated semantic admin fixture");
      assert.equal(persistedSupportNote?.projectId, projectId);

      const auditEventId = toUlid("01J00000000000000000000001");
      durable.put<Models.AuditEvent>("audit", "local", auditEventId, {
        auditEventId,
        projectId,
        action: "admin.semantic.fixture",
        targetType: "database",
        targetId: databaseId,
        occurredAt: "2026-08-28T00:00:00.000Z",
        metadata: { source: "sqlite-semantic-test" },
      });
      const notificationId = toUlid("01J00000000000000000000002");
      durable.put<Models.Notification>("notifications", "local", notificationId, {
        notificationId,
        title: "Semantic operations notice",
        body: "A database operation is queued.",
        severity: "warning",
        createdAt: "2026-08-28T00:00:00.000Z",
        category: "operations",
        organizationId,
        projectId,
      });
    } finally {
      durable.close();
    }

    const reopened = createTerraScaleSqliteRuntime({ sqlite: { enabled: true, databasePath } });
    try {
      const admin = reopened.createAdminClient();
      // Persistence/reopen covers support note, resources, audit, and notifications.
      const organizations = expectOk(await admin.listOrganizations());
      assert.equal(organizations.items.some((item) => item.organizationId === organizationId && item.name === "Semantic Admin Organization"), true);
      const projects = expectOk(await admin.listProjects());
      assert.equal(projects.items.some((item) => item.projectId === projectId && item.organizationId === organizationId), true);
      const databases = expectOk(await admin.listDatabases());
      assert.equal(databases.items.some((item) => item.databaseId === databaseId && item.projectId === projectId), true);
      const notes = expectOk(await admin.createSupportNote({ organizationId, projectId, note: "reopen check" }));
      assert.notEqual(notes.supportNoteId, supportNoteId);
      const audit = expectOk(await admin.listAudit());
      assert.equal(audit.totalCount, 1);
      assert.equal(audit.items[0]?.action, "admin.semantic.fixture");
      assert.equal(audit.items[0]?.targetId, databaseId);
      const notifications = expectOk(await admin.listNotifications());
      assert.equal(notifications.totalCount, 1);
      assert.equal(notifications.items[0]?.title, "Semantic operations notice");
      assert.equal(notifications.items[0]?.organizationId, organizationId);
      assert.equal(expectOk(await admin.listUsers({ search: email })).totalCount, 0);
    } finally {
      await closeRuntime(reopened);
    }
  });
});
