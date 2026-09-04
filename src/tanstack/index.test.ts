import assert from "node:assert/strict";
import { describe, test } from "vitest";

import {
  encodeTerrascaleSyncV2MsgpackEnvelope,
  type TerrascaleSyncV2MsgpackMessage,
} from "../msgpack/generated/terrascale-sync-v2-msgpack.js";
import { TerraScaleSyncV2Client } from "../sync/v2/client.js";
import { syncV2ResultDigest, type SyncV2ResultItem } from "../sync/v2/result-view.js";
import { SyncV2ViewCheckpoint } from "../sync/v2/result-view.js";
import { terrascaleCollectionOptions } from "./index.js";

const encoder = new TextEncoder();
const item = (identity: string, value: string): SyncV2ResultItem => ({
  identity,
  value: { id: encoder.encode(JSON.stringify(identity)), value: encoder.encode(JSON.stringify(value)) },
});

function frame(message: TerrascaleSyncV2MsgpackMessage): Uint8Array {
  return encodeTerrascaleSyncV2MsgpackEnvelope({ features: 0, correlation: 1n, causation: 0n, message });
}

function snapshot(items: readonly SyncV2ResultItem[], revision = 1n, generation = 1n) {
  return {
    view_generation: generation,
    authorization_generation: 1n,
    revision,
    items: items.map(value => ({
      identity: value.identity,
      fields: Object.entries(value.value).map(([path, field]) => ({ path, value: field })),
    })),
    digest: syncV2ResultDigest(items),
    checkpoint: encoder.encode(`checkpoint-${generation}-${revision}`),
  };
}

describe("TerraScale TanStack collection sync", () => {
  test("publishes a snapshot and delta through the official sync callbacks", () => {
    const client = new TerraScaleSyncV2Client();
    const sent: Uint8Array[] = [];
    const writes: Array<{ type: string; key?: string; value?: Record<string, unknown> }> = [];
    let ready = 0;
    const adapter = terrascaleCollectionOptions<{ id: string; value: string }>({
      client,
      subscription: "todos",
      query: "from todos",
      getKey: row => String(row.id),
      transport: { send: frameValue => { sent.push(frameValue); } },
      mutate: async () => ({ status: "rejected" as const }),
    });
    const cleanup = adapter.adapter.options.sync.sync({
      collection: {},
      begin: () => undefined,
      write: message => writes.push(message as typeof writes[number]),
      commit: () => true,
      markReady: () => { ready += 1; },
      markError: error => { throw error; },
      truncate: () => { writes.push({ type: "truncate", key: "" }); },
    });
    assert.equal(sent.length, 1);
    const first = [item("a", "open")];
    adapter.adapter.receive(frame({ kind: "snapshot", subscription: "todos", snapshot: snapshot(first) }));
    assert.deepEqual(writes.map(entry => entry.type), ["truncate", "insert"]);
    assert.equal(writes[1]?.value?.value, "open");
    assert.equal(ready, 1);

    const second = [item("a", "done")];
    adapter.adapter.receive(frame({
      kind: "delta",
      subscription: "todos",
      view_generation: 1n,
      from_revision: 1n,
      to_revision: 2n,
      operations: [{ kind: "update", identity: "a", fields: Object.entries(second[0]!.value).map(([path, value]) => ({ path, value })) }],
      digest: syncV2ResultDigest(second),
      checkpoint: encoder.encode("checkpoint-1-2"),
    }));
    assert.equal(writes.at(-1)?.value?.value, "done");
    adapter.adapter.receive(frame({
      kind: "reset",
      subscription: "todos",
      reason: "checkpoint_expired",
      snapshot: snapshot([item("b", "fresh")], 3n, 2n),
    }));
    assert.equal(writes.at(-2)?.type, "truncate");
    assert.equal(writes.at(-1)?.value?.value, "fresh");
    cleanup?.();
  });

  test("keeps optimistic presentation reversible and requires online authoritative acknowledgement", async () => {
    const client = new TerraScaleSyncV2Client();
    const writes: Array<{ type: string; key: string; value?: Record<string, unknown> }> = [];
    const first = [item("a", "open")];
    const next = [item("a", "done")];
    const nextValue = next[0]!.value.value!;
    const adapter = terrascaleCollectionOptions<{ id: string; value: string }>({
      client,
      subscription: "todos-mutate",
      query: "from todos",
      getKey: row => String(row.id),
      transport: { send: () => undefined },
      mutate: async request => {
        assert.equal(request.idempotencyKey, "transaction-1");
        return {
          view_generation: 1n,
          from_revision: 1n,
          to_revision: 2n,
          operations: [{ kind: "Patch", identity: "a", changes: [{ path: "value", value: nextValue }] }],
          digest: syncV2ResultDigest(next),
          checkpoint: new SyncV2ViewCheckpoint("checkpoint-1-2"),
        };
      },
    });
    adapter.adapter.options.sync.sync({
      collection: {}, begin: () => undefined, write: message => writes.push(message as typeof writes[number]),
      commit: () => true, markReady: () => undefined, markError: error => { throw error; }, truncate: () => undefined,
    });
    adapter.adapter.receive(frame({ kind: "snapshot", subscription: "todos-mutate", snapshot: snapshot(first) }));
    const mutation = adapter.adapter.options.onUpdate({ transaction: {
      id: "transaction-1",
      mutations: [{ type: "update", key: "a", modified: { id: "a", value: "done" }, original: { id: "a", value: "open" } }],
    } });
    await mutation;
    assert.equal(writes.at(-1)?.value?.value, "done");
    adapter.adapter.dispose();
    await assert.rejects(() => adapter.adapter.options.onUpdate({ transaction: { id: "after-dispose", mutations: [] } }), /disposed/);
  });

  test("rolls back a rejected optimistic mutation and aborts transport ownership", async () => {
    const client = new TerraScaleSyncV2Client();
    const writes: Array<{ type: string; value?: Record<string, unknown> }> = [];
    const controller = new AbortController();
    const adapter = terrascaleCollectionOptions<{ id: string; value: string }>({
      client,
      subscription: "todos-reject",
      query: "from todos",
      getKey: row => row.id,
      signal: controller.signal,
      transport: { send: () => undefined },
      mutate: async () => ({ status: "rejected" as const, error: new Error("conflict") }),
    });
    adapter.adapter.options.sync.sync({
      collection: {}, begin: () => undefined, write: message => writes.push(message as typeof writes[number]),
      commit: () => true, markReady: () => undefined, markError: error => { throw error; }, truncate: () => undefined,
    });
    adapter.adapter.receive(frame({ kind: "snapshot", subscription: "todos-reject", snapshot: snapshot([item("a", "open")]) }));
    await assert.rejects(() => adapter.adapter.options.onUpdate({ transaction: {
      id: "reject-1", mutations: [{ type: "update", key: "a", modified: { id: "a", value: "done" } }],
    } }), /conflict/);
    assert.equal(writes.at(-1)?.value?.value, "open");
    controller.abort();
    assert.throws(() => adapter.adapter.receive(new Uint8Array()), /disposed/);
  });
});
