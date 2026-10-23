// Runnable snippet: export a database as NDJSON with @terrascale/sdk 0.0.5.
// `sandbox.client` is the runner's credential-bound createTerraBaseV1Client
// instance for the sandbox project; its token needs documents:write to seed
// the sandbox database and documents:read to export it.
import { strict as assert } from "node:assert";
import type { TerraBaseV1Client } from "@terrascale/sdk";

type Sandbox = {
  readonly databaseName: string;
  readonly client: Pick<TerraBaseV1Client, "database">;
};

type Line = { readonly collection: string; readonly id: string; readonly document: object };

export const title = "Database export (NDJSON)";
// `export()` ships in @terrascale/sdk 0.0.5; npm serves 0.0.4 as latest.
export const runnable = false;

// Document ids are canonical ULIDs. Export orders lines by collection, then id.
const noteIds = [
  "01JB7Q3V0Z8K4M2N6P9R5T1W6A",
  "01JB7Q3V0Z8K4M2N6P9R5T1W6B",
  "01JB7Q3V0Z8K4M2N6P9R5T1W6C",
];

export async function run(sandbox: Sandbox): Promise<void> {
  const db = sandbox.client.database(sandbox.databaseName);
  await db.collections.create("notes");
  const notes = db.collection("notes");
  for (const [n, id] of noteIds.entries()) await notes.put(id, { text: `note ${n}` });

  // One request reads one snapshot of every live document.
  const full: Line[] = [];
  for await (const line of db.export()) {
    assert.equal(typeof line.collection, "string");
    assert.equal(typeof line.id, "string");
    assert.equal(typeof line.document, "object");
    full.push(line);
  }
  assert.deepEqual(
    full.filter((line) => line.collection === "notes").map((line) => line.id),
    noteIds,
  );

  // Stop after the first line, as a cut connection would, then resume after
  // the last line received. The resumed part reads a new snapshot.
  let last: Line | undefined;
  for await (const line of db.export()) {
    last = line;
    break;
  }
  assert.ok(last, "the export has at least one line");
  const rest: Line[] = [];
  for await (const line of db.export({ after: { collection: last.collection, id: last.id } })) {
    rest.push(line);
  }
  assert.deepEqual([last, ...rest], full);
}
