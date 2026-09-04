// Runnable snippet: database-per-tenant (D-51).
// ts.tenants.create(key) -> a database inheriting the tenant template.
// Runs in the stub harness; remote uses POST /api/v1/tenants and fails
// closed when the contract is unavailable.
import { strict as assert } from "node:assert";

type Sandbox = {
  createTenant(key: string): Promise<{ databaseId: string; key: string }>;
};

export const title = "Database per tenant";

export async function run(sandbox: Sandbox): Promise<void> {
  const tenant = await sandbox.createTenant("acme");
  assert.ok(tenant.databaseId.length > 0, "tenant gets its own database");
  assert.equal(tenant.key, "acme");
}
