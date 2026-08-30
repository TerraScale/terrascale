import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

import { createTerraScaleSqliteRuntime } from "./local/index.js";
import { installLocalSqliteTestEnvironment } from "./local/test-environment.test.js";
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

    // Databases and environments: exercise every operation, including the
    // confirmation-protected database deletion and environment cascade.
    const initialDatabases = expectOk(await client.databases.list());
    assert.equal(initialDatabases.some((item) => item.databaseId === seed.databaseId), true);
    const database = expectOk(await client.databases.create({ name: "semantic database" }));
    assert.equal(database.name, "semantic database");
    assert.equal(expectOk(await client.databases.get(database.databaseId)).databaseId, database.databaseId);
    assert.equal(
      expectOk(await client.databases.list()).some((item) => item.databaseId === database.databaseId),
      true,
    );
    const environment = expectOk(
      await client.databases.createEnvironment(database.databaseId, {
        name: "semantic preview",
        kind: "Preview",
      }),
    );
    assert.equal(environment.kind, "Preview");
    const environments = expectOk(await client.databases.listEnvironments(database.databaseId));
    assert.equal(environments.some((item) => item.environmentId === environment.environmentId), true);
    assert.equal(
      expectOk(await client.databases.getEnvironment(database.databaseId, environment.environmentId)).environmentId,
      environment.environmentId,
    );
    const deletedEnvironment = expectOk(
      await client.databases.deleteEnvironment(database.databaseId, environment.environmentId),
    );
    assert.equal(deletedEnvironment.deleted, true);
    assert.equal(expectOk(await client.databases.listEnvironments(database.databaseId)).length, 0);
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

    // Repositories and documents use a fresh repository so its deletion can
    // prove that all indexed documents disappear with it.
    const repository = expectOk(
      await client.repositories.create({
        name: "semantic repository",
        databaseId: seed.databaseId,
        collection: "Semantic",
      }),
    );
    assert.equal(
      expectOk(await client.repositories.list()).some((item) => item.repositoryId === repository.repositoryId),
      true,
    );
    assert.equal(
      expectOk(await client.repositories.get({ repositoryId: repository.repositoryId })).name,
      repository.name,
    );

    const scope = { repositoryId: repository.repositoryId };
    const created = expectOk(
      await client.documents.create(scope, { title: "first", count: 1, state: "new" }),
    );
    assert.deepEqual(created.data, { title: "first", count: 1, state: "new" });
    assert.deepEqual(expectOk(await client.documents.get(scope, created.id)).data, created.data);
    const put = expectOk(
      await client.documents.put(
        scope,
        created.id,
        { title: "replaced", count: 2, state: "new" },
        { expectedChangeToken: created.changeToken! },
      ),
    );
    assert.deepEqual(put.data, { title: "replaced", count: 2, state: "new" });
    const patched = expectOk(
      await client.documents.patch(
        scope,
        created.id,
        { state: "ready", extra: true },
        { expectedChangeToken: put.changeToken! },
      ),
    );
    assert.deepEqual(patched.data, { title: "replaced", count: 2, state: "ready", extra: true });
    expectFailure(
      await client.documents.patch(
        scope,
        created.id,
        { state: "conflict" },
        { expectedChangeToken: put.changeToken! },
      ),
      "change_token_mismatch",
    );

    const query = expectOk(
      await client.query.execute(scope, {
        query: "from documents where state = 'ready' order by count desc",
      }),
    );
    assert.equal(query.total, 1);
    assert.equal(query.items[0]?.id, created.id);

    const batch = expectOk(
      await client.documents.batch(scope, {
        operations: [
          { operation: "upsert", documentId: created.id, data: { title: "batched", count: 3 } },
          { operation: "delete", documentId: created.id },
        ],
      }),
    );
    assert.equal(batch.results.length, 2);
    assert.equal(batch.results[0]?.operation, "upsert");
    assert.equal(batch.results[1]?.operation, "delete");
    expectFailure(await client.documents.get(scope, created.id), "document_not_found");
    const deletedDocument = expectOk(
      await client.documents.create(scope, { title: "to delete", state: "temporary" }),
    );
    const deleted = expectOk(await client.documents.delete(scope, deletedDocument.id));
    assert.equal(deleted.deleted, true);
    expectFailure(await client.documents.get(scope, deletedDocument.id), "document_not_found");
    assert.equal(expectOk(await client.documents.list(scope)).total, 0);

    const schema = expectOk(
      await client.schema.configure(scope, {
        enabled: true,
        schema: JSON.stringify({ type: "object", required: ["title"] }),
      }),
    );
    assert.equal(schema.enabled, true);
    assert.equal(expectOk(await client.schema.get(scope)).schema, schema.schema);
    const validSchemaResult = expectOk(await client.schema.validate(scope));
    assert.equal(validSchemaResult.repositoryId, repository.repositoryId);
    const invalid = expectOk(await client.documents.create(scope, { state: "missing title" }));
    const invalidSchemaResult = expectOk(await client.schema.validate(scope));
    const errors = (invalidSchemaResult as unknown as { readonly errors?: readonly unknown[] }).errors;
    assert.equal(Array.isArray(errors), true);
    assert.equal((errors?.length ?? 0) > 0, true);
    expectOk(await client.documents.delete(scope, invalid.id));

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
    const persistedDocuments = expectOk(await reopened.documents.list({ repositoryId: seed.repositoryId }));
    assert.equal(persistedDocuments.total, 1);
    assert.equal(persistedDocuments.items[0]?.id, seed.documentId);
    assert.equal(expectOk(await reopened.backups.listBackups(seed.databaseId)).length, 1);
    assert.equal(expectOk(await reopened.backups.listRestores(seed.databaseId)).length, 1);
    // The local handler intentionally uses its generic missing-resource shape
    // for repository GETs after deletion. Keep this cleanup after persistence
    // assertions because repository index cleanup is currently project-wide.
    const deletedRepository = expectOk(await reopened.repositories.delete(scope));
    assert.equal(deletedRepository.deleted, true);
    expectFailure(await reopened.repositories.get(scope), "resource_not_found");
    assert.equal(
      expectOk(await reopened.repositories.list()).some((item) => item.repositoryId === repository.repositoryId),
      false,
    );
  } finally {
    await runtime.close();
    await rm(directory, { recursive: true, force: true });
  }
});
