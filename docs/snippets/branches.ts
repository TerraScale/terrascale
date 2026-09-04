// Runnable snippet: cloud database branch with @terrascale/sdk 0.3.0.
// Branch admission is a management operation and therefore requires a
// project-scoped credential (`TERRASCALE_PROJECT_ID` in the docs runner).
import { strict as assert } from "node:assert";
import type { TerraScaleClient } from "@terrascale/sdk";

type Sandbox = {
  readonly databaseId: string;
  readonly client: Pick<TerraScaleClient, "databaseBranches">;
};

export const title = "Cloud database branch";
// The project-scoped branch producer route is not published yet.
export const runnable = false;

export async function run(sandbox: Sandbox): Promise<void> {
  const result = await sandbox.client.databaseBranches.requestClone({
    sourceDatabaseId: sandbox.databaseId,
  });
  assert.equal(result.ok, true);
}
