// Runnable snippet: one database per tenant with @terrascale/sdk 0.0.4.
// The token needs management access to the sandbox project.
import { strict as assert } from "node:assert";
import type { TerraBaseV1Client } from "@terrascale/sdk";

type Sandbox = {
  readonly databaseName: string;
  readonly client: Pick<TerraBaseV1Client, "database" | "databases">;
};

export const runnable = true;
export const title = "Database per tenant";

export async function run(sandbox: Sandbox): Promise<void> {
  // Derive the name from your own tenant key; names are unique per project.
  const tenant = await sandbox.client.databases.create({ name: `${sandbox.databaseName}-acme` });
  try {
    const db = sandbox.client.database(tenant.name);
    await db.collections.create("invoices");
    const written = await db.collection("invoices").put("01JB7Q3V0Z8K4M2N6P9R5T1W7A", { total: 42 });
    assert.ok(written.version);
  } finally {
    await sandbox.client.databases.delete(tenant.name);
  }
}
