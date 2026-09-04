// Runnable snippet: database-per-tenant with @terrascale/sdk 0.3.0.
// A tenant is a database, so tenant data uses the ordinary database client
// after provisioning and carries the tenant's placement lock.
import { strict as assert } from "node:assert";
import type { TerraScaleClient } from "@terrascale/sdk";

type Sandbox = {
  readonly client: Pick<TerraScaleClient, "tenants">;
};

export const title = "Database per tenant";
// The tenant provisioning producer route is not published yet. Keep this
// fixture beside the docs, but never call an invented endpoint in qualification.
export const runnable = false;

export async function run(sandbox: Sandbox): Promise<void> {
  const created = await sandbox.client.tenants.create("docs-acme", {
    idempotencyKey: "docs-snippet-tenant-acme",
  });
  assert.equal(created.ok, true);
  if (!created.ok) throw new Error("tenant creation failed");
  assert.equal(created.value.key, "docs-acme");
  assert.ok(created.value.databaseId, "tenant gets its own database");

  const fetched = await sandbox.client.tenants.get("docs-acme");
  assert.equal(fetched.ok, true);
  const listed = await sandbox.client.tenants.list();
  assert.equal(listed.ok, true);
  if (listed.ok) assert.ok(listed.value.some((tenant) => tenant.key === "docs-acme"));
}
