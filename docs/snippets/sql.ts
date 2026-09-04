// Runnable snippet: read-only SQL over extracted columns (D-54/D-78).
// Real endpoint: POST /api/v1/databases/{id}/sql with named parameters.
// In the SDK, use `Query.raw(sql, parameters).execute()` (or `.subscribe()`)
// and pass the resulting descriptor to the published database transport.
import { strict as assert } from "node:assert";

type Query = { field: string; op: "="; value: string };
type Sandbox = {
  readonly databaseId: string;
  put(input: { databaseId: string; collection: string; doc: Record<string, unknown> }): Promise<Record<string, unknown>>;
  query(input: { databaseId: string; collection: string; query: Query }): Promise<Record<string, unknown>[]>;
};

export const title = "SQL query";

export async function run(sandbox: Sandbox): Promise<void> {
  const { databaseId } = sandbox;
  await sandbox.put({ databaseId, collection: "orders", doc: { id: "o-1", status: "paid", total: 42 } });
  await sandbox.put({ databaseId, collection: "orders", doc: { id: "o-2", status: "pending", total: 7 } });
  await sandbox.put({ databaseId, collection: "orders", doc: { id: "o-3", status: "paid", total: 99 } });

  // Query.raw("SELECT * FROM orders WHERE status = $1", ["paid"]).execute()
  const paid = await sandbox.query({ databaseId, collection: "orders", query: { field: "status", op: "=", value: "paid" } });
  assert.equal(paid.length, 2);
  assert.deepEqual(
    paid.map((d) => d.id).sort(),
    ["o-1", "o-3"],
  );
}
