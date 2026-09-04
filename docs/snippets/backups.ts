// Runnable snippet: configure and trigger an on-demand backup with
// @terrascale/sdk 0.3.0. Backups are billed storage/retention, not reads.
import { strict as assert } from "node:assert";
import type { TerraScaleClient } from "@terrascale/sdk";

type Sandbox = {
  readonly databaseId: string;
  readonly client: Pick<TerraScaleClient, "backups">;
};

export const title = "Backup and restore point";
// The project-scoped backup producer routes are not published yet.
export const runnable = false;

export async function run(sandbox: Sandbox): Promise<void> {
  const configured = await sandbox.client.backups.configurePolicy(sandbox.databaseId, {
    fullBackupCron: "0 2 * * *",
    retentionDays: 7,
  });
  assert.equal(configured.ok, true);

  const operationSessionId = "docs-snippet-backup";
  const plan = await sandbox.client.backups.planBackup(sandbox.databaseId, { operationSessionId });
  assert.equal(plan.ok, true);
  if (!plan.ok) throw new Error("backup plan failed");

  const executed = await sandbox.client.backups.executeBackup(
    sandbox.databaseId,
    { operationSessionId, confirmationToken: plan.value.confirmationToken },
    "docs-snippet-backup-execute",
  );
  assert.equal(executed.ok, true);
}
