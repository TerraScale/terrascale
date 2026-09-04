// Runnable snippet: live query subscribe (D-14/D-38).
// Real transport: CDN-backed HTTP long-polling on sync.api. by default,
// WebSocket opt-in. The explicit local stub mirrors this delta contract.
import { strict as assert } from "node:assert";

type Sandbox = {
  put(collection: string, doc: Record<string, unknown>): Promise<Record<string, unknown>>;
  subscribe(collection: string, cb: (delta: { doc?: Record<string, unknown> }) => void): () => void;
};

export const title = "Live subscription";

export async function run(sandbox: Sandbox): Promise<void> {
  const seen: string[] = [];
  const unsub = sandbox.subscribe("events", (delta) => {
    if (delta.doc?.id) seen.push(delta.doc.id as string);
  });

  await sandbox.put("events", { id: "e-1", kind: "click" });
  await sandbox.put("events", { id: "e-2", kind: "purchase" });
  unsub();

  await sandbox.put("events", { id: "e-3", kind: "click" });
  assert.deepEqual(seen, ["e-1", "e-2"]);
}
