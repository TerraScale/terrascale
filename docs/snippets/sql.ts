// Runnable snippet: read-only parameterized SQL with @terrascale/sdk 0.3.0.
// SQL is executed by `client.sql.execute`; writes remain document operations.
import { strict as assert } from "node:assert";
import type { TerraScaleClient } from "@terrascale/sdk";

type Sandbox = {
  readonly databaseId: string;
  readonly client: Pick<TerraScaleClient, "documents" | "sql">;
};

export const title = "SQL query";

// Document ids are canonical ULIDs. Keep them in ascending order: the query result is sorted.
const [orderA, orderB, orderC] = [
  "01JB7Q3V0Z8K4M2N6P9R5T1W5A",
  "01JB7Q3V0Z8K4M2N6P9R5T1W5B",
  "01JB7Q3V0Z8K4M2N6P9R5T1W5C",
];

export async function run(sandbox: Sandbox): Promise<void> {
  const { databaseId } = sandbox;
  for (const [id, status, total] of [[orderA, "paid", 42], [orderB, "pending", 7], [orderC, "paid", 99]] as const) {
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
  assert.deepEqual(paid.value.rows.map((row) => row.id).sort(), [orderA, orderC]);
}
