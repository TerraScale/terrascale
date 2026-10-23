// Runnable snippet: read a database's changes feed with @terrascale/sdk
// 0.0.4. See /guides/realtime for cursors, retention and reconnects.
import { strict as assert } from "node:assert";
import type { TerraBaseV1Client } from "@terrascale/sdk";

type Sandbox = {
  readonly databaseName: string;
  readonly client: Pick<TerraBaseV1Client, "database">;
};

export const runnable = true;
export const title = "Changes feed";

const [first, second] = ["01JB7Q3V0Z8K4M2N6P9R5T1W6A", "01JB7Q3V0Z8K4M2N6P9R5T1W6B"];

export async function run(sandbox: Sandbox): Promise<void> {
  const db = sandbox.client.database(sandbox.databaseName);
  await db.collections.create("events");
  const events = db.collection("events");
  await events.put(first, { kind: "click" });
  await events.put(second, { kind: "purchase" });

  // Without a cursor the feed starts at retention, then stays open for new writes.
  // Leaving the loop closes the stream.
  const seen: string[] = [];
  let resumeAt: string | undefined;
  for await (const item of db.changes({ collections: ["events"] })) {
    // Store the latest cursor to resume after a disconnect.
    if (item.cursor) resumeAt = item.cursor;
    // Delivery is at least once: deduplicate by id and version.
    if (item.type === "change" && !seen.includes(item.id)) seen.push(item.id);
    if (seen.length === 2) break;
  }
  assert.deepEqual(seen, [first, second]);
  assert.ok(resumeAt, "the feed issued a cursor");
}
