// Runnable snippet: read-only SQL over extracted columns (D-54/D-78).
// Real endpoint: POST /api/v1/databases/{id}/sql once the contract is pinned (P-1).
import { strict as assert } from "node:assert";

type Sandbox = {
  put(collection: string, doc: Record<string, unknown>): Promise<Record<string, unknown>>;
  query(collection: string, predicate: (doc: Record<string, unknown>) => boolean): Promise<Record<string, unknown>[]>;
};

export const title = "SQL query";

export async function run(sandbox: Sandbox): Promise<void> {
  await sandbox.put("orders", { id: "o-1", status: "paid", total: 42 });
  await sandbox.put("orders", { id: "o-2", status: "pending", total: 7 });
  await sandbox.put("orders", { id: "o-3", status: "paid", total: 99 });

  // SELECT * FROM orders WHERE status = 'paid'
  const paid = await sandbox.query("orders", (d) => d.status === "paid");
  assert.equal(paid.length, 2);
  assert.deepEqual(
    paid.map((d) => d.id).sort(),
    ["o-1", "o-3"],
  );
}
