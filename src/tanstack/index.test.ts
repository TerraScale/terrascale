import assert from "node:assert/strict";
import { describe, test } from "vitest";

import {
  encodeTerrascaleSyncV2MsgpackEnvelope,
  type TerrascaleSyncV2MsgpackMessage,
} from "../msgpack/generated/terrascale-sync-v2-msgpack.js";
import { TerraScaleSyncV2Client } from "../sync/v2/client.js";
import { syncV2ResultDigest, type SyncV2ResultItem } from "../sync/v2/result-view.js";
import { SyncV2ViewCheckpoint } from "../sync/v2/result-view.js";
import { terrascaleCollectionOptions, type TerraScaleTanStackMutationResponse } from "./index.js";

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

function deferred<T>(): { readonly promise: Promise<T>; readonly resolve: (value: T) => void; readonly reject: (error: unknown) => void } {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

async function flush(): Promise<void> {
  await new Promise<void>(resolve => setTimeout(resolve, 0));
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

  test("does not publish or advance the checkpoint until an async commit succeeds", async () => {
    const client = new TerraScaleSyncV2Client();
    const commit = deferred<void>();
    const errors: unknown[] = [];
    let ready = 0;
    const adapter = terrascaleCollectionOptions<{ id: string; value: string }>({
      client,
      subscription: "todos-async-commit",
      query: "from todos",
      getKey: row => row.id,
      transport: { send: () => undefined },
      mutate: async () => ({ status: "rejected" as const }),
    });
    adapter.adapter.options.sync.sync({
      collection: {}, begin: () => undefined, write: () => undefined,
      commit: () => commit.promise, markReady: () => { ready += 1; },
      markError: error => { errors.push(error); }, truncate: () => undefined,
    });

    adapter.adapter.receive(frame({ kind: "snapshot", subscription: "todos-async-commit", snapshot: snapshot([item("a", "open")]) }));
    assert.equal(adapter.adapter.getCheckpoint(), undefined);
    assert.equal(ready, 0);

    const failure = new Error("collection commit failed");
    commit.reject(failure);
    await flush();
    assert.equal(adapter.adapter.getCheckpoint(), undefined);
    assert.equal(ready, 0);
    assert.deepEqual(errors, [failure]);
  });

  test("serializes async publications and applies a reset after an earlier failure", async () => {
    const client = new TerraScaleSyncV2Client();
    const firstCommit = deferred<void>();
    const resetCommit = deferred<void>();
    const commits: Array<Promise<void>> = [firstCommit.promise, resetCommit.promise];
    const writes: Array<{ type: string; value?: Record<string, unknown> }> = [];
    const adapter = terrascaleCollectionOptions<{ id: string; value: string }>({
      client,
      subscription: "todos-reset-commit",
      query: "from todos",
      getKey: row => row.id,
      transport: { send: () => undefined },
      mutate: async () => ({ status: "rejected" as const }),
    });
    adapter.adapter.options.sync.sync({
      collection: {}, begin: () => undefined,
      write: message => writes.push(message as typeof writes[number]),
      commit: () => commits.shift() ?? true, markReady: () => undefined,
      markError: () => undefined, truncate: () => undefined,
    });
    adapter.adapter.receive(frame({ kind: "snapshot", subscription: "todos-reset-commit", snapshot: snapshot([item("a", "old")]) }));
    adapter.adapter.receive(frame({
      kind: "reset", subscription: "todos-reset-commit", reason: "checkpoint_expired",
      snapshot: snapshot([item("b", "fresh")], 3n, 2n),
    }));
    assert.equal(adapter.adapter.getCheckpoint(), undefined);

    firstCommit.reject(new Error("stale publication"));
    await flush();
    assert.equal(adapter.adapter.getCheckpoint(), undefined);
    resetCommit.resolve();
    await flush();
    assert.equal(new TextDecoder().decode(adapter.adapter.getCheckpoint() ?? new Uint8Array()), "checkpoint-2-3");
    assert.equal(writes.at(-1)?.value?.value, "fresh");
  });

  test("keeps concurrent mutations in invocation order with one idempotent request", async () => {
    const client = new TerraScaleSyncV2Client();
    const calls: string[] = [];
    const responses = [deferred<TerraScaleTanStackMutationResponse>(), deferred<TerraScaleTanStackMutationResponse>()];
    const adapter = terrascaleCollectionOptions<{ id: string; value: string }>({
      client,
      subscription: "todos-order",
      query: "from todos",
      getKey: row => row.id,
      transport: { send: () => undefined },
      mutate: async request => {
        calls.push(request.transactionId);
        return responses[calls.length - 1]!.promise;
      },
    });
    adapter.adapter.options.sync.sync({
      collection: {}, begin: () => undefined, write: () => undefined,
      commit: () => true, markReady: () => undefined, markError: error => { throw error; }, truncate: () => undefined,
    });
    adapter.adapter.receive(frame({ kind: "snapshot", subscription: "todos-order", snapshot: snapshot([item("a", "open")]) }));

    const first = adapter.adapter.options.onUpdate({ transaction: {
      id: "tx-a", mutations: [{ type: "update", key: "a", modified: { id: "a", value: "one" } }],
    } });
    const retry = adapter.adapter.options.onUpdate({ transaction: {
      id: "tx-a", mutations: [{ type: "update", key: "a", modified: { id: "a", value: "one" } }],
    } });
    const second = adapter.adapter.options.onInsert({ transaction: {
      id: "tx-b", mutations: [{ type: "insert", key: "b", modified: { id: "b", value: "two" } }],
    } });
    await Promise.resolve();
    assert.deepEqual(calls, ["tx-a"]);

    const firstRows = [item("a", "one")];
    responses[0]!.resolve({
      view_generation: 1n, from_revision: 1n, to_revision: 2n,
      operations: [{ kind: "Update", identity: "a", value: firstRows[0]!.value }],
      digest: syncV2ResultDigest(firstRows), checkpoint: new SyncV2ViewCheckpoint("checkpoint-1-2"),
    });
    await flush();
    assert.deepEqual(calls, ["tx-a", "tx-b"]);

    const secondRows = [item("a", "one"), item("b", "two")];
    responses[1]!.resolve({
      view_generation: 1n, from_revision: 2n, to_revision: 3n,
      operations: [{ kind: "Insert", identity: "b", position: 1, value: secondRows[1]!.value }],
      digest: syncV2ResultDigest(secondRows), checkpoint: new SyncV2ViewCheckpoint("checkpoint-1-3"),
    });
    await Promise.all([first, retry, second]);
    assert.deepEqual(calls, ["tx-a", "tx-b"]);
  });

  test("sends unsubscribe before aborting the adapter signal", () => {
    const client = new TerraScaleSyncV2Client();
    const sent: Array<{ readonly frame: Uint8Array; readonly aborted: boolean }> = [];
    const adapter = terrascaleCollectionOptions<{ id: string; value: string }>({
      client,
      subscription: "todos-dispose-signal",
      query: "from todos",
      getKey: row => row.id,
      transport: { send: (frameValue, signal) => { sent.push({ frame: frameValue, aborted: signal.aborted }); } },
      mutate: async () => ({ status: "rejected" as const }),
    });
    adapter.adapter.options.sync.sync({
      collection: {}, begin: () => undefined, write: () => undefined,
      commit: () => true, markReady: () => undefined, markError: () => undefined, truncate: () => undefined,
    });
    assert.equal(sent.length, 1);
    adapter.adapter.dispose();
    assert.equal(sent.length, 2);
    assert.equal(sent[1]?.aborted, false);
    assert.notDeepEqual(sent[0]?.frame, sent[1]?.frame);
  });

  test("keeps another adapter online while one adapter has an async send", async () => {
    const client = new TerraScaleSyncV2Client();
    const pendingSend = deferred<void>();
    const stable = terrascaleCollectionOptions<{ id: string; value: string }>({
      client,
      subscription: "todos-stable-online",
      query: "from todos",
      getKey: row => row.id,
      transport: { send: () => undefined },
      mutate: async () => ({ status: "rejected" as const }),
    });
    stable.adapter.options.sync.sync({
      collection: {}, begin: () => undefined, write: () => undefined,
      commit: () => true, markReady: () => undefined, markError: () => undefined, truncate: () => undefined,
    });
    stable.adapter.receive(frame({ kind: "snapshot", subscription: "todos-stable-online", snapshot: snapshot([item("a", "open")]) }));

    const changing = terrascaleCollectionOptions<{ id: string; value: string }>({
      client,
      subscription: "todos-changing-online",
      query: "from todos",
      getKey: row => row.id,
      transport: { send: () => pendingSend.promise },
      mutate: async () => ({ status: "rejected" as const }),
    });
    changing.adapter.options.sync.sync({
      collection: {}, begin: () => undefined, write: () => undefined,
      commit: () => true, markReady: () => undefined, markError: () => undefined, truncate: () => undefined,
    });
    assert.equal(client.getSnapshot().online, true);
    await assert.rejects(() => stable.adapter.options.onUpdate({ transaction: {
      id: "stable-offline-check",
      mutations: [{ type: "update", key: "a", modified: { id: "a", value: "done" } }],
    } }), /the TerraScale mutation was rejected/);
    assert.equal(client.getSnapshot().online, true);
    changing.adapter.dispose();
    stable.adapter.dispose();
    pendingSend.resolve();
  });
});
