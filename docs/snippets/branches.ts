// Runnable snippet: branch a database with @terrascale/sdk 0.0.9. A branch is
// a new database that starts as a copy of its source. The token needs
// management access to the sandbox project.
import { strict as assert } from "node:assert";
import type { TerraBaseV1Client } from "@terrascale/sdk";

type Sandbox = {
  readonly databaseName: string;
  readonly client: Pick<TerraBaseV1Client, "databases">;
};

export const runnable = true;
export const title = "Database branch";

export async function run(sandbox: Sandbox): Promise<void> {
  const source = await sandbox.client.databases.get(sandbox.databaseName);
  const branch = await sandbox.client.databases.branch(source.name, { name: `${source.name}-preview` });
  assert.equal(branch.branchedFrom, source.id);
  await sandbox.client.databases.delete(branch.name);
}
