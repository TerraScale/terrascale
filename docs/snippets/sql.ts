// Runnable snippet: read-only SQL with @terrascale/sdk 0.0.4.
// `db.sql` runs SELECT statements; writes remain document operations.
import { strict as assert } from "node:assert";
import type { TerraBaseV1Client } from "@terrascale/sdk";

type Sandbox = {
  readonly databaseName: string;
  readonly client: Pick<TerraBaseV1Client, "database">;
};

export const runnable = true;
export const title = "SQL query";

// Document ids are canonical ULIDs.
const [orderA, orderB, orderC] = [
  "01JB7Q3V0Z8K4M2N6P9R5T1W5A",
  "01JB7Q3V0Z8K4M2N6P9R5T1W5B",
  "01JB7Q3V0Z8K4M2N6P9R5T1W5C",
];

export async function run(sandbox: Sandbox): Promise<void> {
  const db = sandbox.client.database(sandbox.databaseName);
  await db.collections.create("orders");
  const orders = db.collection("orders");
  for (const [id, status, total] of [[orderA, "paid", 42], [orderB, "pending", 7], [orderC, "paid", 99]] as const) {
    await orders.put(id, { status, total });
  }

  const paid = await db.sql("SELECT * FROM orders WHERE status = 'paid'");
  assert.equal(paid.matched, 2);
  assert.equal(paid.truncated, false);
  assert.deepEqual(paid.rows.map((row) => row.id).sort(), [orderA, orderC]);
}
