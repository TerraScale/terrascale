// Runnable snippet: document CRUD.
// Runs against a sandbox branch pre-filled with the reader's key (D-56).
// In CI it runs against the stub sandbox (same interface, no credentials).
import { strict as assert } from "node:assert";

export const title = "Document CRUD";

type Sandbox = {
  put(collection: string, doc: Record<string, unknown>): Promise<Record<string, unknown>>;
  get(collection: string, id: string): Promise<Record<string, unknown> | null>;
  remove(collection: string, id: string): Promise<boolean>;
};

export async function run(sandbox: Sandbox): Promise<void> {
  const created = await sandbox.put("todos", { id: "todo-1", text: "ship docs", done: false });
  assert.ok(created._rev, "create returns a revision");

  const read = await sandbox.get("todos", "todo-1");
  assert.equal(read?.text, "ship docs");

  await sandbox.put("todos", { id: "todo-1", text: "ship docs", done: true });
  const updated = await sandbox.get("todos", "todo-1");
  assert.equal(updated?.done, true);

  assert.equal(await sandbox.remove("todos", "todo-1"), true);
  assert.equal(await sandbox.get("todos", "todo-1"), null);
}
