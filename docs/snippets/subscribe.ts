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

  assert.equal((await sandbox.client.documents.put(sandbox.databaseId, "events", "e-1", { kind: "click" })).ok, true);
  assert.equal((await sandbox.client.documents.put(sandbox.databaseId, "events", "e-2", { kind: "purchase" })).ok, true);
  unsub();
  live.close();

  await sandbox.client.documents.put(sandbox.databaseId, "events", "e-3", { kind: "click" });
  assert.deepEqual(seen, ["e-1", "e-2"]);
}
