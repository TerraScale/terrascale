import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "vitest";

import { createTerraScaleSqliteRuntime } from "./local/index.js";
import { installLocalSqliteTestEnvironment } from "./local/test-environment.js";
import type { TerraScaleResult } from "./result.js";

installLocalSqliteTestEnvironment();

function expectOk<T>(result: TerraScaleResult<T>): T {
  if (!result.ok) throw new Error(`${result.error.code}: ${result.error.message}`);
  return result.value;
}

function expectFailure<T>(result: TerraScaleResult<T>, code: string): void {
  assert.equal(result.ok, false);
  if (result.ok) return;
  assert.equal(result.error.code, code);
}

test("SQLite local data APIs preserve semantic state across CRUD, commands, and reopen", async () => {
  const directory = await mkdtemp(join(tmpdir(), "terrascale-local-data-semantic-"));
  const databasePath = join(directory, "semantic.sqlite");
  let runtime = createTerraScaleSqliteRuntime({
    sqlite: { enabled: true, databasePath },
  });

  try {
    const seed = await runtime.seed();
    const client = runtime.createClient({
      projectId: seed.projectId,
      accessToken: "local-test-token",
    });

    // Databases: exercise every operation, including the
    // confirmation-protected database deletion.
    const initialDatabases = expectOk(await client.databases.list());
    assert.equal(initialDatabases.some((item) => item.databaseId === seed.databaseId), true);
    const database = expectOk(await client.databases.create({ name: "semantic database" }));
    assert.equal(database.name, "semantic database");
    assert.equal(expectOk(await client.databases.get(database.databaseId)).databaseId, database.databaseId);
    assert.equal(
      expectOk(await client.databases.list()).some((item) => item.databaseId === database.databaseId),
      true,
    );
    const deletedDatabase = expectOk(
      await client.databases.delete(database.databaseId, { confirm: true }),
    );
    assert.equal(deletedDatabase.deleted, true);
    expectFailure(await client.databases.get(database.databaseId), "database_not_found");
    assert.equal(
      expectOk(await client.databases.list()).some((item) => item.databaseId === database.databaseId),
      false,
    );

    const capability = expectOk(await client.databaseBranches.getCapability());
    assert.equal(capability.physicalCloneAvailable, false);
    assert.equal(capability.promotionAvailable, false);
    assert.equal(capability.mergeAvailable, false);
    expectFailure(
      await client.databaseBranches.requestClone({ sourceDatabaseId: seed.databaseId }),
      "database_branch_unavailable",
    );

    // Backup policy/confirmation/execute/list/get, restore points, and the
    // restore operation all use the same durable database state.
    const policy = expectOk(
      await client.backups.configurePolicy(seed.databaseId, {
        fullBackupCron: "0 0 * * *",
        retentionDays: 7,
      }),
    );
    assert.equal(policy.retentionDays, 7);
    const policyAgain = expectOk(
      await client.backups.configurePolicy(seed.databaseId, { fullBackupCron: "0 1 * * *" }),
    );
    assert.equal(policyAgain.backupPolicyId, policy.backupPolicyId);
    const plan = expectOk(
      await client.backups.planBackup(seed.databaseId, { operationSessionId: "semantic-backup-session" }),
    );
    const backup = expectOk(
      await client.backups.executeBackup(
        seed.databaseId,
        { operationSessionId: "semantic-backup-session", confirmationToken: plan.confirmationToken },
        "semantic-backup-once",
      ),
    );
    const backupReplay = expectOk(
      await client.backups.executeBackup(
        seed.databaseId,
        { operationSessionId: "semantic-backup-session", confirmationToken: plan.confirmationToken },
        "semantic-backup-once",
      ),
    );
    assert.equal(backupReplay.backupId, backup.backupId);
    expectFailure(
      await client.backups.executeBackup(
        seed.databaseId,
        { operationSessionId: "different-session", confirmationToken: plan.confirmationToken },
        "semantic-backup-once",
      ),
      "idempotency_key_reused",
    );
    assert.equal(expectOk(await client.backups.listBackups(seed.databaseId)).length, 1);
    assert.equal(expectOk(await client.backups.getBackup(seed.databaseId, backup.backupId)).backupId, backup.backupId);
    const points = expectOk(await client.backups.listRestorePoints(seed.databaseId));
    assert.equal(points.some((point) => point.backupId === backup.backupId), true);
    const restore = expectOk(
      await client.backups.startRestore(seed.databaseId, {
        backupId: backup.backupId,
        idempotencyKey: "semantic-restore-once",
      }),
    );
    const restoreReplay = expectOk(
      await client.backups.startRestore(seed.databaseId, {
        backupId: backup.backupId,
        idempotencyKey: "semantic-restore-once",
      }),
    );
    assert.equal(restoreReplay.restoreId, restore.restoreId);
    assert.equal(expectOk(await client.backups.getRestore(seed.databaseId, restore.restoreId)).restoreId, restore.restoreId);
    assert.equal(
      expectOk(await client.backups.listRestores(seed.databaseId)).some((item) => item.restoreId === restore.restoreId),
      true,
    );

    await runtime.close();
    runtime = createTerraScaleSqliteRuntime({ sqlite: { enabled: true, databasePath } });
    const reopened = runtime.createClient({
      projectId: seed.projectId,
      accessToken: "local-test-token",
    });
    assert.equal(expectOk(await reopened.databases.get(seed.databaseId)).databaseId, seed.databaseId);
    assert.equal(expectOk(await reopened.backups.listBackups(seed.databaseId)).length, 1);
    assert.equal(expectOk(await reopened.backups.listRestores(seed.databaseId)).length, 1);
  } finally {
    await runtime.close();
    await rm(directory, { recursive: true, force: true });
  }
});
