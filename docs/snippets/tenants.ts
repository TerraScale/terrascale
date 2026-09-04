// Runnable snippet: database-per-tenant (D-51).
// Tenant creation will use the named SDK tenant client once published.
// This remains a credential-free stub only. The tenant route is not published,
// so remote mode must never call it.
import { strict as assert } from "node:assert";

export const runnable = false;

type Sandbox = {
  createTenant(key: string): Promise<{ databaseId: string; key: string }>;
};

export const title = "Database per tenant";

export async function run(sandbox: Sandbox): Promise<void> {
  const tenant = await sandbox.createTenant("acme");
  assert.ok(tenant.databaseId.length > 0, "tenant gets its own database");
  assert.equal(tenant.key, "acme");
}
