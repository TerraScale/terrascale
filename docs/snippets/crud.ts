// Runnable snippet: document CRUD.
// Runs against a sandbox branch pre-filled with the reader's key (D-56).
// An explicit local test run may use the stub sandbox; the docs runner defaults
// to the real cloud branch and fails closed when credentials are absent.
// The current SDK has no published typed document CRUD family. Its exact
// database-scoped setup is `createTerraScaleClient({ accessToken })` followed
// by `client.databases.get(databaseId)`; this fixture exercises the pending
// document route through the runner adapter without inventing an SDK method.
import { strict as assert } from "node:assert";

export const title = "Document CRUD";

type Sandbox = {
  readonly databaseId: string;
  put(input: { databaseId: string; collection: string; doc: Record<string, unknown> }): Promise<Record<string, unknown>>;
  get(input: { databaseId: string; collection: string; id: string }): Promise<Record<string, unknown> | null>;
  remove(input: { databaseId: string; collection: string; id: string }): Promise<boolean>;
};

export async function run(sandbox: Sandbox): Promise<void> {
  const { databaseId } = sandbox;
  const created = await sandbox.put({ databaseId, collection: "todos", doc: { id: "todo-1", text: "ship docs", done: false } });
  assert.ok(created._rev, "create returns a revision");

  const read = await sandbox.get({ databaseId, collection: "todos", id: "todo-1" });
  assert.equal(read?.text, "ship docs");

  await sandbox.put({ databaseId, collection: "todos", doc: { id: "todo-1", text: "ship docs", done: true } });
  const updated = await sandbox.get({ databaseId, collection: "todos", id: "todo-1" });
  assert.equal(updated?.done, true);

  assert.equal(await sandbox.remove({ databaseId, collection: "todos", id: "todo-1" }), true);
  assert.equal(await sandbox.get({ databaseId, collection: "todos", id: "todo-1" }), null);
}
