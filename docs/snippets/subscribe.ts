// Runnable snippet: live query subscribe (D-14/D-38).
// Real transport: CDN-backed HTTP long-polling on sync.api. by default,
// WebSocket opt-in. The explicit local stub mirrors this result-view contract.
// Exact SDK call: `new TerraScaleSyncV2Client().subscribe({ databaseId, ... })`.
import { strict as assert } from "node:assert";

type Sandbox = {
  readonly databaseId: string;
  put(input: { databaseId: string; collection: string; doc: Record<string, unknown> }): Promise<Record<string, unknown>>;
  subscribe(input: {
    databaseId: string;
    subscription: string;
    query: string;
    parameters: Uint8Array;
  }): Promise<{
    view: { subscribe(listener: () => void): () => void; getVisibleItems(): readonly { identity: string }[] };
    close(): void;
  }>;
};

export const title = "Live subscription";

export async function run(sandbox: Sandbox): Promise<void> {
  const seen: string[] = [];
  const live = await sandbox.subscribe({
    databaseId: sandbox.databaseId,
    subscription: "events",
    query: "SELECT * FROM events ORDER BY _updated DESC LIMIT 20",
    parameters: new Uint8Array(),
  });
  const unsub = live.view.subscribe(() => {
    for (const item of live.view.getVisibleItems()) {
      if (!seen.includes(item.identity)) seen.push(item.identity);
    }
  });

  await sandbox.put({ databaseId: sandbox.databaseId, collection: "events", doc: { id: "e-1", kind: "click" } });
  await sandbox.put({ databaseId: sandbox.databaseId, collection: "events", doc: { id: "e-2", kind: "purchase" } });
  unsub();
  live.close();

  await sandbox.put({ databaseId: sandbox.databaseId, collection: "events", doc: { id: "e-3", kind: "click" } });
  assert.deepEqual(seen, ["e-1", "e-2"]);
}
