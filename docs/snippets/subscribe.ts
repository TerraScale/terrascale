// Runnable snippet: live SQL result view with the pinned @terrascale/sdk
// 0.3.0 Sync v1 client. HTTP long-polling is the default reactive transport.
import { strict as assert } from "node:assert";
import type { TerraScaleSyncV1Client } from "@terrascale/sdk";

type Sandbox = {
  readonly databaseId: string;
  // In an application this is `new TerraScaleSyncV1Client({ accessToken })`.
  readonly sync: Pick<TerraScaleSyncV1Client, "subscribe">;
  readonly client: { documents: { put(database: string, collection: string, id: string, value: Record<string, unknown>): Promise<{ ok: boolean }> } };
};

export const title = "Live subscription";

const [first, second, third] = [
  "01JB7Q3V0Z8K4M2N6P9R5T1W6A",
  "01JB7Q3V0Z8K4M2N6P9R5T1W6B",
  "01JB7Q3V0Z8K4M2N6P9R5T1W6C",
];

export async function run(sandbox: Sandbox): Promise<void> {
  const live = await sandbox.sync.subscribe({
    databaseId: sandbox.databaseId,
    subscription: "events",
    query: "SELECT * FROM events ORDER BY _updated DESC LIMIT 20",
    parameters: new Uint8Array(),
  });
  const seen: string[] = [];
  const unsub = live.view.subscribe(() => {
    for (const item of live.view.getVisibleItems()) if (!seen.includes(item.identity)) seen.push(item.identity);
  });

  assert.equal((await sandbox.client.documents.put(sandbox.databaseId, "events", first, { kind: "click" })).ok, true);
  assert.equal((await sandbox.client.documents.put(sandbox.databaseId, "events", second, { kind: "purchase" })).ok, true);
  unsub();
  live.close();

  await sandbox.client.documents.put(sandbox.databaseId, "events", third, { kind: "click" });
  assert.deepEqual(seen, [first, second]);
}
