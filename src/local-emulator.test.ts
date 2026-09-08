import assert from "node:assert/strict";
import { mkdtemp, rm, stat } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "vitest";

import { exercisePublicClientRoutes } from "./client-route-capture.js";
import type { FetchLike } from "./config.js";
import { createTerraScaleClient } from "./factory.js";
import { toUlid } from "./ids.js";
import {
  createTerraScaleSqliteRuntime,
  type LocalServerAddress,
  type TerraScaleLocalSeed,
  type TerraScaleSqliteRuntimeOptions,
} from "./local/index.js";
import { TerraScaleSqliteStore } from "./local/store.js";
import { installLocalSqliteTestEnvironment } from "./local/test-environment.js";
import type { TerraScaleResult } from "./result.js";

installLocalSqliteTestEnvironment();

const knownUserId = toUlid("01ARZ3NDEKTSV4RRFFQ69G5FAV");
const localBearerToken = "local-test-token-0123456789-abcdefghijk";

function expectOk<T>(result: TerraScaleResult<T>): T {
  if (!result.ok) throw new Error(result.error.message);
  return result.value;
}

async function withTempRuntime<T>(
  work: (runtime: ReturnType<typeof createTerraScaleSqliteRuntime>, path: string) => Promise<T>,
  options: Omit<TerraScaleSqliteRuntimeOptions, "sqlite"> = {},
): Promise<T> {
  const directory = await mkdtemp(join(tmpdir(), "terrascale-local-emulator-"));
  const databasePath = join(directory, "local.sqlite");
  const runtime = createTerraScaleSqliteRuntime({
    ...options,
    sqlite: { enabled: true, databasePath },
  });
  try {
    return await work(runtime, databasePath);
  } finally {
    await runtime.close();
    await rm(directory, { recursive: true, force: true });
    await assert.rejects(stat(directory));
  }
}

async function started(
  runtime: ReturnType<typeof createTerraScaleSqliteRuntime>,
): Promise<LocalServerAddress> {
  return runtime.start({ host: "127.0.0.1", port: 0 });
}

function localHttpFetch(origin: string): FetchLike {
  return async (input, init) => {
    const request = input instanceof Request ? input : new Request(input, init);
    const source = new URL(request.url);
    return fetch(new Request(new URL(`${source.pathname}${source.search}`, origin), request));
  };
}

test("the runtime requires explicit SQLite enablement and offers typed clients", async () => {
  assert.throws(() => createTerraScaleSqliteRuntime(undefined as never), /options are required/u);
  await withTempRuntime(async (runtime, databasePath) => {
    assert.equal(runtime.databasePath, databasePath);
    assert.equal(runtime.createClient().config.sqlite?.enabled, true);
  });
});

test("SQLite-supported captured document and SQL routes execute through the real loopback server", async () => {
  await withTempRuntime(async (runtime) => {
    const address = await started(runtime);
    try {
      // Direct member management and health have separate producer contracts;
      // this explicitly enabled development backend supports the data slice.
      const supportedRoutes = [
        "databases.listDocuments", "databases.getDocument", "databases.patchDocument",
        "databases.putDocument", "databases.deleteDocument", "databases.executeSql",
      ] as const;
      const routes = await exercisePublicClientRoutes(localHttpFetch(address.origin), undefined, supportedRoutes);
      assert.deepEqual(routes.map(route => route.routeName).sort(), [...supportedRoutes].sort());
      assert.equal(new Set(routes.map((route) => route.routeName)).size, routes.length);
    } finally {
      await runtime.stop();
    }
  });
});

test("configured root and AdminClient instances share the SQLite runtime", async () => {
  await withTempRuntime(async (runtime) => {
    const admin = runtime.createAdminClient({ accessToken: "local-test-token" });
    const client = runtime.createClient({
      accessToken: "local-test-token",
      projectId: knownUserId,
    });
    const organization = expectOk(
      await client.organizations.create({ name: "Admin organization" }),
    );
    const project = expectOk(
      await client.organizations.createProject(organization.organizationId, {
        name: "Admin project",
        regions: ["GRU"],
        backupRegion: "GRU",
      }),
    );
    expectOk(await client.databases.create({ name: "Admin database" }));
    const checks: readonly Promise<unknown>[] = [
      admin.overview(),
      admin.createSupportNote({
        note: "local admin test",
        organizationId: organization.organizationId,
        projectId: project.projectId,
      }),
      admin.listOrganizations(),
      admin.listProjects(),
      admin.listDatabases(),
      admin.getUsage(),
    ];
    for (const check of checks) expectOk((await check) as TerraScaleResult<unknown>);
    expectOk(await admin.listAudit());
    expectOk(await admin.listNotifications());
  });
});

test("typed data survives SQLite close/reopen over the real HTTP server", async () => {
  await withTempRuntime(async (first, databasePath) => {
    const firstAddress = await started(first);
    const rootClient = createTerraScaleClient({
      apiBaseUrl: firstAddress.apiBaseUrl,
      accessToken: "local-test-token",
    });
    let seed: TerraScaleLocalSeed;
    try {
      const organization = expectOk(
        await rootClient.organizations.create({ name: "HTTP seeded organization" }),
      );
      const project = expectOk(
        await rootClient.organizations.createProject(organization.organizationId, {
          name: "HTTP seeded project",
          regions: ["GRU"],
          backupRegion: "GRU",
        }),
      );
      const scopedRoot = createTerraScaleClient({
        apiBaseUrl: firstAddress.apiBaseUrl,
        projectId: project.projectId,
        accessToken: "local-test-token",
      });
      const database = expectOk(
        await scopedRoot.databases.create({ name: "HTTP seeded database" }),
      );
      seed = {
        organizationId: organization.organizationId,
        projectId: project.projectId,
        databaseId: database.databaseId,
      };
      expectOk(
        await scopedRoot.databases.get(database.databaseId),
      );
    } finally {
      await first.stop();
    }
    const reopened = createTerraScaleSqliteRuntime({ sqlite: { enabled: true, databasePath } });
    const reopenedAddress = await started(reopened);
    try {
      const client = createTerraScaleClient({
        apiBaseUrl: reopenedAddress.apiBaseUrl,
        projectId: seed!.projectId,
        accessToken: "local-test-token",
      });
      assert.equal(
        expectOk(await client.organizations.get(seed!.organizationId)).organizationId,
        seed!.organizationId,
      );
      assert.equal(
        expectOk(await client.databases.get(seed!.databaseId)).databaseId,
        seed!.databaseId,
      );
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
      const client = createTerraScaleClient({
        apiBaseUrl: address.apiBaseUrl,
        projectId: seed.projectId,
        accessToken: "local-test-token",
      });
      expectOk(
        await client.backups.configurePolicy(seed.databaseId, { fullBackupCron: "0 0 * * *" }),
      );
      const operationSessionId = "backup-session";
      const plan = expectOk(
        await client.backups.planBackup(seed.databaseId, { operationSessionId }),
      );
      const executed = expectOk(
        await client.backups.executeBackup(
          seed.databaseId,
          { operationSessionId, confirmationToken: plan.confirmationToken },
          "backup-once",
        ),
      );
      const executedReplay = expectOk(
        await client.backups.executeBackup(
          seed.databaseId,
          { operationSessionId, confirmationToken: plan.confirmationToken },
          "backup-once",
        ),
      );
      assert.equal(executedReplay.backupId, executed.backupId);
      assert.equal(expectOk(await client.backups.listBackups(seed.databaseId)).length, 1);
    } finally {
      await runtime.stop();
    }
  });
});

test("required bearer gate, health, unknown routes, reset/seed, binding, and remote rejection are real boundary behavior", async () => {
  await withTempRuntime(async (seedRuntime) => {
    const seeded = await seedRuntime.reset({ seed: true });
    assert.notEqual(seeded, undefined);
    assert.equal((await seedRuntime.createClient().organizations.list()).ok, true);
  });
  await withTempRuntime(
    async (runtime) => {
      const address = await started(runtime);
      const health = await fetch(`${address.origin}/health`);
      assert.equal(health.status, 200);
      assert.deepEqual(await health.json(), { status: "ready", storage: "sqlite" });
      const unauthorized = await fetch(`${address.origin}/api/v1/organizations`);
      assert.equal(unauthorized.status, 401);
      const authorized = await fetch(`${address.origin}/api/v1/organizations`, {
        headers: { authorization: `Bearer ${localBearerToken}` },
      });
      assert.equal(authorized.status, 200);
      const authorizedWithTrailingSlash = await fetch(`${address.origin}/api/v1/organizations/`, {
        headers: { authorization: `Bearer ${localBearerToken}` },
      });
      assert.equal(authorizedWithTrailingSlash.status, 200);
      assert.deepEqual(await authorizedWithTrailingSlash.json(), []);
      const unknown = await fetch(`${address.origin}/api/v1/not-a-route`, {
        headers: { authorization: `Bearer ${localBearerToken}` },
      });
      assert.equal(unknown.status, 404);
    },
    { authentication: "required", bearerToken: localBearerToken },
  );
  await withTempRuntime(
    async (permissive) => {
      await assert.rejects(
        () => permissive.start({ host: "192.0.2.1", port: 0 }),
        /non-loopback host/,
      );
    },
    { allowRemote: false },
  );
});

test("project deletion cascades every project-scoped core resource", async () => {
  await withTempRuntime(async (runtime, databasePath) => {
    const root = runtime.createClient();
    const organization = expectOk(await root.organizations.create({ name: "Cascade org" }));
    const project = expectOk(
      await root.organizations.createProject(organization.organizationId, {
        name: "Cascade project",
        regions: ["GRU"],
        backupRegion: "GRU",
      }),
    );
    const scoped = runtime.createClient({ projectId: project.projectId });
    const database = expectOk(await scoped.databases.create({ name: "Cascade database" }));
    expectOk(
      await scoped.backups.planBackup(database.databaseId, { operationSessionId: "cascade-plan" }),
    );
    const deleted = expectOk(
      await root.organizations.deleteProject(project.projectId, {
        confirm: true,
        projectName: project.name,
      }, { idempotencyKey: "cascade-project-delete" }),
    );
    assert.equal(deleted.databasesDeleted, 1);
    assert.equal(deleted.physicalStorageDeleted, true);
    for (const result of [
      await root.organizations.getProject(project.projectId),
      await scoped.databases.get(database.databaseId),
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
    const projectA = expectOk(
      await root.organizations.createProject(organization.organizationId, {
        name: "Project A",
        regions: ["GRU"],
        backupRegion: "GRU",
      }),
    );
    const projectB = expectOk(
      await root.organizations.createProject(organization.organizationId, {
        name: "Project B",
        regions: ["GRU"],
        backupRegion: "GRU",
      }),
    );
    const clientA = runtime.createClient({ projectId: projectA.projectId });
    const clientB = runtime.createClient({ projectId: projectB.projectId });
    const databaseB = expectOk(await clientB.databases.create({ name: "B database" }));
    const crossProject = await clientA.databases.get(databaseB.databaseId);
    assert.equal(crossProject.ok, false);
    if (!crossProject.ok) assert.equal(crossProject.error.status, 404);

    const idempotentA = runtime.createClient({
      projectId: projectA.projectId,
      idempotencyKeyFactory: () => "database-once",
    });
    const first = expectOk(await idempotentA.databases.create({ name: "shared request" }));
    const replay = expectOk(await idempotentA.databases.create({ name: "shared request" }));
    assert.equal(replay.databaseId, first.databaseId);
    const idempotentB = runtime.createClient({
      projectId: projectB.projectId,
      idempotencyKeyFactory: () => "database-once",
    });
    const isolated = expectOk(await idempotentB.databases.create({ name: "shared request" }));
    assert.notEqual(isolated.databaseId, first.databaseId);
    const reused = await idempotentA.databases.create({ name: "different" });
    assert.equal(reused.ok, false);
    if (!reused.ok) assert.equal(reused.error.code, "idempotency_key_reused");
  });
});
