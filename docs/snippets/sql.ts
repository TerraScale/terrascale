// Runnable snippet: read-only parameterized SQL with @terrascale/sdk 0.3.0.
// SQL is executed by `client.sql.execute`; writes remain document operations.
import { strict as assert } from "node:assert";
import type { TerraScaleClient } from "@terrascale/sdk";

type Sandbox = {
  readonly databaseId: string;
  readonly client: Pick<TerraScaleClient, "documents" | "sql">;
};

export const title = "SQL query";

export async function run(sandbox: Sandbox): Promise<void> {
  const { databaseId } = sandbox;
  for (const [id, status, total] of [["o-1", "paid", 42], ["o-2", "pending", 7], ["o-3", "paid", 99]] as const) {
    const write = await sandbox.client.documents.put(databaseId, "orders", id, { status, total });
    assert.equal(write.ok, true);
  }

  const paid = await sandbox.client.sql.execute(databaseId, {
    sql: "SELECT * FROM orders WHERE status = :status",
    parameters: { status: "paid" },
  });
  assert.equal(paid.ok, true);
  if (!paid.ok || !("rows" in paid.value)) throw new Error("SQL did not return rows");
  assert.equal(paid.value.matched, 2);
  assert.deepEqual(paid.value.rows.map((row) => row.id).sort(), ["o-1", "o-3"]);
}
