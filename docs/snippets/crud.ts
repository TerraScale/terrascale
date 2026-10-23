// Runnable snippet: document CRUD with @terrascale/sdk 0.0.4.
// `sandbox.client` is the runner's credential-bound createTerraBaseV1Client
// instance for the sandbox project; its token needs documents:write.
import { strict as assert } from "node:assert";
import { V1NotFoundError, type TerraBaseV1Client } from "@terrascale/sdk";

type Sandbox = {
  readonly databaseName: string;
  readonly client: Pick<TerraBaseV1Client, "database">;
};

export const runnable = true;
export const title = "Document CRUD";

// Document ids are canonical ULIDs; any other id answers 400 invalid_document_id.
const todoId = "01JB7Q3V0Z8K4M2N6P9R5T1W3X";

export async function run(sandbox: Sandbox): Promise<void> {
  const db = sandbox.client.database(sandbox.databaseName);
  await db.collections.create("todos");
  const todos = db.collection("todos");

  const created = await todos.put(todoId, { text: "ship docs", done: false });
  assert.ok(created.version, "create returns a version");

  const read = await todos.get(todoId);
  assert.equal(read.document.text, "ship docs");

  // ifMatch makes the update fail with 412 if someone else wrote in between.
  const updated = await todos.patch(todoId, { merge: { done: true } }, { ifMatch: read.version });
  const reread = await todos.get(todoId);
  assert.equal(reread.document.done, true);

  await todos.delete(todoId, { ifMatch: updated.version });
  await assert.rejects(todos.get(todoId), V1NotFoundError);
}
