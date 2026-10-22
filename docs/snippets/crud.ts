// Runnable snippet: document CRUD with @terrascale/sdk 0.3.0.
// `sandbox.client` is the runner's credential-bound createTerraScaleClient
// instance; applications can create the same client with their API base URL
// and access token.
import { strict as assert } from "node:assert";
import type { TerraScaleClient } from "@terrascale/sdk";

type Sandbox = {
  readonly databaseId: string;
  readonly client: Pick<TerraScaleClient, "documents">;
};

export const title = "Document CRUD";

// Document ids are canonical ULIDs; any other id answers 400 invalid_document_id.
const todoId = "01JB7Q3V0Z8K4M2N6P9R5T1W3X";

export async function run(sandbox: Sandbox): Promise<void> {
  const { databaseId } = sandbox;
  const created = await sandbox.client.documents.put(databaseId, "todos", todoId, {
    text: "ship docs",
    done: false,
  });
  assert.equal(created.ok, true);
  assert.ok(created.value.revision, "create returns a revision");

  const read = await sandbox.client.documents.get(databaseId, "todos", todoId);
  assert.equal(read.ok, true);
  assert.equal(read.value?.text, "ship docs");

  const updated = await sandbox.client.documents.patch(databaseId, "todos", todoId, { done: true });
  assert.equal(updated.ok, true);
  const reread = await sandbox.client.documents.get(databaseId, "todos", todoId);
  assert.equal(reread.ok, true);
  assert.equal(reread.value?.done, true);

  const deleted = await sandbox.client.documents.delete(databaseId, "todos", todoId);
  assert.equal(deleted.ok, true);
  const gone = await sandbox.client.documents.get(databaseId, "todos", todoId);
  assert.equal(gone.ok, false);
  assert.equal(gone.ok ? undefined : gone.error.kind, "not-found");
}
