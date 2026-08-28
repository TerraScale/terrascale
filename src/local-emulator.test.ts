import assert from "node:assert/strict";
import { mkdtemp, rm, stat } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

import { exercisePublicClientRoutes } from "./client-route-capture.js";
import type { FetchLike } from "./config.js";
import { createTerraScaleClient } from "./factory.js";
import { toUlid } from "./ids.js";
import type { TerraScaleRegionCode } from "./regions.js";
import {
  createTerraScaleSqliteRuntime,
  type LocalServerAddress,
  type TerraScaleLocalSeed,
} from "./local/index.js";
import { localManifestRoutes } from "./local/route-manifest.generated.js";
import { TerraScaleSqliteStore } from "./local/store.js";
import type { TerraScaleResult } from "./result.js";

const knownUserId = toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAV");

function expectOk<T>(result: TerraScaleResult<T>): T {
  if (!result.ok) throw new Error(result.error.message);
  return result.value;
}

async function withTempRuntime<T>(work: (runtime: ReturnType<typeof createTerraScaleSqliteRuntime>, path: string) => Promise<T>): Promise<T> {
  const directory = await mkdtemp(join(tmpdir(), "terrascale-local-emulator-"));
  const databasePath = join(directory, "local.sqlite");
  const runtime = createTerraScaleSqliteRuntime({ sqlite: { enabled: true, databasePath } });
  try {
    return await work(runtime, databasePath);
  } finally {
    await runtime.close();
    await rm(directory, { recursive: true, force: true });
    await assert.rejects(stat(directory));
  }
}

async function started(runtime: ReturnType<typeof createTerraScaleSqliteRuntime>): Promise<LocalServerAddress> {
  return runtime.start({ host: "127.0.0.1", port: 0 });
}

function localHttpFetch(origin: string): FetchLike {
  return async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    const source = new URL(request.url);
    return fetch(new Request(new URL(`${source.pathname}${source.search}`, origin), request));
  };
}

test("the runtime requires explicit SQLite enablement and offers typed clients", () => {
  assert.throws(() => createTerraScaleSqliteRuntime(undefined as never), /options are required/u);
  const runtime = createTerraScaleSqliteRuntime({ sqlite: { enabled: true } });
  assert.equal(runtime.databasePath, ":memory:");
  assert.equal(runtime.createClient().config.sqlite?.enabled, true);
  return runtime.close();
});

test("all captured public client routes execute through the real loopback server", async () => {
  await withTempRuntime(async (runtime) => {
    const address = await started(runtime);
    try {
      const routes = await exercisePublicClientRoutes(localHttpFetch(address.origin));
      assert.equal(routes.length, localManifestRoutes.length);
    } finally {
      await runtime.stop();
    }
  });
});

test("configured root and AdminClient instances share the SQLite runtime", async () => {
  await withTempRuntime(async (runtime) => {
    const admin = runtime.createAdminClient({ accessToken: "local-test-token" });
    const client = runtime.createClient({ accessToken: "local-test-token", projectId: knownUserId });
    const organization = expectOk(await client.organizations.create({ name: "Admin organization" }));
    const project = expectOk(await client.organizations.createProject(organization.organizationId, { name: "Admin project", regions: ["local" as TerraScaleRegionCode], backupRegion: "local" as TerraScaleRegionCode }));
    expectOk(await client.databases.create({ name: "Admin database" }));
    const checks: readonly Promise<unknown>[] = [
      admin.overview(),
      admin.createSupportNote({ note: "local admin test", organizationId: organization.organizationId, projectId: project.projectId }),
      admin.listOrganizations(), admin.listProjects(), admin.listDatabases(), admin.getUsage(),
    ];
    for (const check of checks) expectOk(await check as TerraScaleResult<unknown>);
    expectOk(await admin.listAudit());
    expectOk(await admin.listNotifications());
  });
});

test("typed data survives SQLite close/reopen and enforces document concurrency, schema, and TQL", async () => {
  await withTempRuntime(async (first, databasePath) => {
    const firstAddress = await started(first);
    const rootClient = createTerraScaleClient({ apiBaseUrl: firstAddress.apiBaseUrl, accessToken: "local-test-token" });
    let seed: TerraScaleLocalSeed;
    try {
      const organization = expectOk(await rootClient.organizations.create({ name: "HTTP seeded organization" }));
      const project = expectOk(await rootClient.organizations.createProject(organization.organizationId, { name: "HTTP seeded project", regions: ["local" as TerraScaleRegionCode], backupRegion: "local" as TerraScaleRegionCode }));
      const scopedRoot = createTerraScaleClient({ apiBaseUrl: firstAddress.apiBaseUrl, projectId: project.projectId, accessToken: "local-test-token" });
      const database = expectOk(await scopedRoot.databases.create({ name: "HTTP seeded database" }));
      const repository = expectOk(await scopedRoot.repositories.create({ name: "HTTP seeded repository", databaseId: database.databaseId, collection: "documents" }));
      const seededDocument = expectOk(await scopedRoot.documents.create({ repositoryId: repository.repositoryId }, { title: "SQLite development runtime", status: "ready" }));
      seed = { organizationId: organization.organizationId, projectId: project.projectId, databaseId: database.databaseId, repositoryId: repository.repositoryId, documentId: seededDocument.id };
      const client = createTerraScaleClient({ apiBaseUrl: firstAddress.apiBaseUrl, projectId: seed.projectId, accessToken: "local-test-token" });
      const scope = { repositoryId: seed.repositoryId };
      const created = expectOk(await client.documents.create(scope, { title: "persisted", count: 1 }));
      const read = expectOk(await client.documents.get(scope, created.id));
      assert.deepEqual(read.data, { title: "persisted", count: 1 });
      assert.equal(typeof created.changeToken, "string");
      const patched = expectOk(await client.documents.patch(scope, created.id, { title: "patched", count: 2 }, { expectedChangeToken: created.changeToken! }));
      const conflict = await client.documents.patch(scope, created.id, { title: "conflict" }, { expectedChangeToken: created.changeToken! });
      assert.equal(conflict.ok, false);
      if (!conflict.ok) assert.equal(conflict.error.code, "change_token_mismatch");
      const schema = expectOk(await client.schema.configure(scope, { enabled: true, schema: '{"type":"object"}' }));
      assert.equal(schema.enabled, true);
      expectOk(await client.schema.get(scope));
      expectOk(await client.schema.validate(scope));
      const query = expectOk(await client.query.execute(scope, { query: "from documents where title = 'patched'" }));
      assert.equal(query.items.some((item) => item.id === created.id), true);
      assert.equal(typeof patched.changeToken, "string");
      expectOk(await client.documents.delete(scope, created.id, { expectedChangeToken: patched.changeToken! }));
    } finally {
      await first.stop();
    }
    const reopened = createTerraScaleSqliteRuntime({ sqlite: { enabled: true, databasePath } });
    const reopenedAddress = await started(reopened);
    try {
      const client = createTerraScaleClient({ apiBaseUrl: reopenedAddress.apiBaseUrl, projectId: seed!.projectId, accessToken: "local-test-token" });
      assert.equal(expectOk(await client.organizations.get(seed!.organizationId)).organizationId, seed!.organizationId);
      assert.equal(expectOk(await client.databases.get(seed!.databaseId)).databaseId, seed!.databaseId);
      assert.equal(expectOk(await client.repositories.get({ repositoryId: seed!.repositoryId })).repositoryId, seed!.repositoryId);
      const listed = expectOk(await client.documents.list({ repositoryId: seed!.repositoryId }));
      assert.equal(listed.total, 1);
      assert.equal(listed.items.some((item) => item.id === seed!.documentId), true);
    } finally {
      await reopened.close();
    }
  });
});

test("backup plan/execute/replay uses typed clients over the real server", async () => {
  await withTempRuntime(async (runtime) => {
    const address = await started(runtime);
    try {
      const seed = await runtime.seed();
      const client = createTerraScaleClient({ apiBaseUrl: address.apiBaseUrl, projectId: seed.projectId, accessToken: "local-test-token" });
      expectOk(await client.backups.configurePolicy(seed.databaseId, { fullBackupCron: "0 0 * * *" }));
      const operationSessionId = "backup-session";
      const plan = expectOk(await client.backups.planBackup(seed.databaseId, { operationSessionId }));
      const executed = expectOk(await client.backups.executeBackup(seed.databaseId, { operationSessionId, confirmationToken: plan.confirmationToken }, "backup-once"));
      const executedReplay = expectOk(await client.backups.executeBackup(seed.databaseId, { operationSessionId, confirmationToken: plan.confirmationToken }, "backup-once"));
      assert.equal(executedReplay.backupId, executed.backupId);
      assert.equal(expectOk(await client.backups.listBackups(seed.databaseId)).length, 1);
    } finally {
      await runtime.stop();
    }
  });
});

test("required bearer gate, health, unknown routes, reset/seed, binding, and remote rejection are real boundary behavior", async () => {
  const seedRuntime = createTerraScaleSqliteRuntime({ sqlite: { enabled: true } });
  try {
    const seeded = await seedRuntime.reset({ seed: true });
    assert.notEqual(seeded, undefined);
    assert.equal((await seedRuntime.createClient().organizations.list()).ok, true);
  } finally {
    await seedRuntime.close();
  }
  const runtime = createTerraScaleSqliteRuntime({ sqlite: { enabled: true }, authentication: "required" });
  const address = await started(runtime);
  try {
    const health = await fetch(`${address.origin}/health`);
    assert.equal(health.status, 200);
    assert.deepEqual(await health.json(), { status: "ready", storage: "sqlite" });
    const unauthorized = await fetch(`${address.origin}/api/v1/organizations`);
    assert.equal(unauthorized.status, 401);
    const authorized = await fetch(`${address.origin}/api/v1/organizations`, { headers: { authorization: "Bearer local-test-token" } });
    assert.equal(authorized.status, 200);
    const unknown = await fetch(`${address.origin}/api/v1/not-a-route`, { headers: { authorization: "Bearer local-test-token" } });
    assert.equal(unknown.status, 404);
  } finally {
    await runtime.close();
  }
  const permissive = createTerraScaleSqliteRuntime({ sqlite: { enabled: true }, allowRemote: false });
  await assert.rejects(() => permissive.start({ host: "192.0.2.1", port: 0 }), /non-loopback host/);
  await permissive.close();
});

test("the registered local Sync v1 and v2 clients use the same runtime store", async () => {
  const runtime = createTerraScaleSqliteRuntime({ sqlite: { enabled: true } });
  try {
    const v1 = runtime.createSyncV1Session({
      DatabaseId: knownUserId,
      EnvironmentId: knownUserId,
      Tenant: "tenant-a",
      AuthorizationScope: "local",
      BearerTokenProvider: async () => "local-token",
    });
    await v1.connect();
    assert.equal(v1.getConnectionSnapshot().Status, "connected");
    await v1.disconnect();

    const v2 = runtime.createSyncV2Session({ Collection: "tasks", Principal: "user-a", Tenant: "tenant-a" });
    await v2.connect();
    const applied = await v2.executeMutation({ Document: "task-1", Put: { state: "open" } }, "mutation-once");
    const replay = await v2.executeMutation({ Document: "task-1", Put: { state: "open" } }, "mutation-once");
    assert.equal(applied.Outcome, "applied");
    assert.deepEqual(replay, applied);
    await assert.rejects(
      v2.executeMutation({ Document: "task-1", Put: { state: "closed" } }, "mutation-once"),
      (error: unknown) => error instanceof Error && error.message.includes("idempotency_key_reused"),
    );
    await v2.disconnect();
  } finally {
    await runtime.close();
  }
});

test("Sync v2 journals and idempotency replays stay isolated by principal and tenant", async () => {
  await withTempRuntime(async (runtime) => {
    const address = await started(runtime);
    const post = async (
      path: string,
      principal: string,
      tenant: string,
      body: unknown,
      idempotencyKey?: string,
    ): Promise<Response> => fetch(`${address.origin}${path}`, {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-tb-principal": principal,
        "x-tb-tenant": tenant,
        ...(idempotencyKey === undefined ? {} : { "idempotency-key": idempotencyKey }),
      },
      body: JSON.stringify(body),
    });
    const open = async (principal: string, tenant: string): Promise<{ readonly checkpoint: string; readonly sessionId: number }> => {
      const response = await post("/v1/sync/sessions", principal, tenant, { collection: "shared" });
      assert.equal(response.status, 200);
      return await response.json() as { readonly checkpoint: string; readonly sessionId: number };
    };
    const sessionA = await open("principal-a", "tenant-a");
    const sessionB = await open("principal-b", "tenant-b");
    const mutationA = { document: "document-a", change: { put: [{ name: "owner", value: "a" }] } };
    const mutationB = { document: "document-b", change: { put: [{ name: "owner", value: "b" }] } };
    const appliedA = await post(`/v1/sync/sessions/${sessionA.sessionId}/mutations`, "principal-a", "tenant-a", mutationA, "same-key");
    const replayA = await post(`/v1/sync/sessions/${sessionA.sessionId}/mutations`, "principal-a", "tenant-a", mutationA, "same-key");
    const appliedB = await post(`/v1/sync/sessions/${sessionB.sessionId}/mutations`, "principal-b", "tenant-b", mutationB, "same-key");
    assert.equal(appliedA.status, 200);
    assert.deepEqual(await replayA.json(), await appliedA.clone().json());
    assert.equal(appliedB.status, 200);

    const deltaA = await post(`/v1/sync/sessions/${sessionA.sessionId}/delta`, "principal-a", "tenant-a", { from: sessionA.checkpoint });
    const deltaB = await post(`/v1/sync/sessions/${sessionB.sessionId}/delta`, "principal-b", "tenant-b", { from: sessionB.checkpoint });
    const bodyA = await deltaA.json() as { readonly commits: number; readonly payload: string };
    const bodyB = await deltaB.json() as { readonly commits: number; readonly payload: string };
    assert.equal(bodyA.commits, 1);
    assert.equal(bodyB.commits, 1);
    assert.equal(Buffer.from(bodyA.payload, "base64").toString("utf8").includes("document-a"), true);
    assert.equal(Buffer.from(bodyA.payload, "base64").toString("utf8").includes("document-b"), false);
    assert.equal(Buffer.from(bodyB.payload, "base64").toString("utf8").includes("document-b"), true);
    assert.equal(Buffer.from(bodyB.payload, "base64").toString("utf8").includes("document-a"), false);
  });
});

test("reset and close disconnect every registered Sync session", async () => {
  const runtime = createTerraScaleSqliteRuntime({ sqlite: { enabled: true } });
  const createV1 = () => runtime.createSyncV1Session({
    DatabaseId: knownUserId,
    EnvironmentId: knownUserId,
    Tenant: "tenant-lifecycle",
    AuthorizationScope: "local",
    BearerTokenProvider: async () => "local-token",
  });
  try {
    const resetV1 = createV1();
    const resetV2 = runtime.createSyncV2Session({ Collection: "tasks", Principal: "user-reset", Tenant: "tenant-lifecycle" });
    await resetV1.connect();
    await resetV2.connect();
    await runtime.reset();
    assert.equal(resetV1.getConnectionSnapshot().Status, "disconnected");
    assert.equal(resetV2.getStatus(), "closed");

    const closeV1 = createV1();
    const closeV2 = runtime.createSyncV2Session({ Collection: "tasks", Principal: "user-close", Tenant: "tenant-lifecycle" });
    await closeV1.connect();
    await closeV2.connect();
    await runtime.close();
    assert.equal(closeV1.getConnectionSnapshot().Status, "disconnected");
    assert.equal(closeV2.getStatus(), "closed");
    assert.throws(() => runtime.createClient(), /runtime is closed/u);
  } finally {
    await runtime.close();
  }
});

test("project deletion cascades every project-scoped core resource", async () => {
  await withTempRuntime(async (runtime, databasePath) => {
    const root = runtime.createClient();
    const organization = expectOk(await root.organizations.create({ name: "Cascade org" }));
    const project = expectOk(await root.organizations.createProject(organization.organizationId, {
      name: "Cascade project", regions: ["local" as TerraScaleRegionCode], backupRegion: "local" as TerraScaleRegionCode,
    }));
    const scoped = runtime.createClient({ projectId: project.projectId });
    const database = expectOk(await scoped.databases.create({ name: "Cascade database" }));
    const repository = expectOk(await scoped.repositories.create({ name: "Cascade repository", databaseId: database.databaseId }));
    expectOk(await scoped.documents.create({ repositoryId: repository.repositoryId }, { retained: false }));
    expectOk(await scoped.backups.planBackup(database.databaseId, { operationSessionId: "cascade-plan" }));
    const deleted = expectOk(await root.organizations.deleteProject(project.projectId, { confirm: true, projectName: project.name }));
    assert.equal(deleted.databasesDeleted, 1);
    assert.equal(deleted.physicalStorageDeleted, true);
    for (const result of [
      await root.organizations.getProject(project.projectId),
      await scoped.databases.get(database.databaseId),
      await scoped.repositories.get({ repositoryId: repository.repositoryId }),
    ]) {
      assert.equal(result.ok, false);
      if (!result.ok) assert.equal(result.error.status, 404);
    }
    await runtime.close();
    const store = new TerraScaleSqliteStore(databasePath);
    assert.equal(store.list("backup-confirmations", "local").length, 0);
    store.close();
  });
});

test("the monotonic ULID generator resumes after reopening SQLite", async () => {
  const directory = await mkdtemp(join(tmpdir(), "terrascale-local-ulid-"));
  const databasePath = join(directory, "local.sqlite");
  try {
    const first = new TerraScaleSqliteStore(databasePath);
    first.nextUlid();
    const beforeClose = first.nextUlid();
    first.close();
    const reopened = new TerraScaleSqliteStore(databasePath);
    const afterReopen = reopened.nextUlid();
    reopened.close();
    assert.equal(afterReopen > beforeClose, true);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("runtime clients enforce project isolation and idempotency fingerprints", async () => {
  await withTempRuntime(async (runtime) => {
    const root = runtime.createClient();
    const organization = expectOk(await root.organizations.create({ name: "Isolation org" }));
    const projectA = expectOk(await root.organizations.createProject(organization.organizationId, {
      name: "Project A", regions: ["local" as TerraScaleRegionCode], backupRegion: "local" as TerraScaleRegionCode,
    }));
    const projectB = expectOk(await root.organizations.createProject(organization.organizationId, {
      name: "Project B", regions: ["local" as TerraScaleRegionCode], backupRegion: "local" as TerraScaleRegionCode,
    }));
    const clientA = runtime.createClient({ projectId: projectA.projectId });
    const clientB = runtime.createClient({ projectId: projectB.projectId });
    const databaseB = expectOk(await clientB.databases.create({ name: "B database" }));
    const repositoryB = expectOk(await clientB.repositories.create({ name: "B repository", databaseId: databaseB.databaseId }));
    expectOk(await clientB.documents.create({ repositoryId: repositoryB.repositoryId }, { project: "B" }));
    const crossProject = await clientA.documents.list({ repositoryId: repositoryB.repositoryId });
    assert.equal(crossProject.ok, false);
    if (!crossProject.ok) assert.equal(crossProject.error.status, 404);

    const idempotentA = runtime.createClient({ projectId: projectA.projectId, idempotencyKeyFactory: () => "database-once" });
    const first = expectOk(await idempotentA.databases.create({ name: "shared request" }));
    const replay = expectOk(await idempotentA.databases.create({ name: "shared request" }));
    assert.equal(replay.databaseId, first.databaseId);
    const idempotentB = runtime.createClient({ projectId: projectB.projectId, idempotencyKeyFactory: () => "database-once" });
    const isolated = expectOk(await idempotentB.databases.create({ name: "shared request" }));
    assert.notEqual(isolated.databaseId, first.databaseId);
    const reused = await idempotentA.databases.create({ name: "different" });
    assert.equal(reused.ok, false);
    if (!reused.ok) assert.equal(reused.error.code, "idempotency_key_reused");
  });
});
