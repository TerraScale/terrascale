// Runnable snippet: backup policy, an on-demand backup and a restore into a
// new database with @terrascale/sdk 0.0.4. The token needs management access
// to the sandbox project. Backups are unbilled; logical documents and
// retained history are billed storage.
import { strict as assert } from "node:assert";
import type { TerraBaseV1Client } from "@terrascale/sdk";

type Sandbox = {
  readonly databaseName: string;
  readonly client: Pick<TerraBaseV1Client, "database" | "databases">;
};

export const runnable = true;
export const title = "Backup and restore";

export async function run(sandbox: Sandbox): Promise<void> {
  const db = sandbox.client.database(sandbox.databaseName);

  const policy = await db.backups.setPolicy({ fullBackupCron: "0 2 * * *", retentionDays: 7 });
  assert.equal(policy.retentionDays, 7);

  const backup = await db.backups.create();
  assert.equal(backup.trigger, "manual");
  const listed = await db.backups.list();
  assert.ok(listed.items.some((item) => item.id === backup.id), "the new backup is listed");

  // Restore never overwrites: it creates a new database from the backup.
  const restored = await db.backups.restore(backup.id, { name: `${sandbox.databaseName}-restored` });
  assert.equal(restored.name, `${sandbox.databaseName}-restored`);
  await sandbox.client.databases.delete(restored.name);
}
