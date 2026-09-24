import assert from "node:assert/strict";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, test } from "vitest";

import {
  encodeTerrascaleSyncV1MsgpackEnvelope,
  type TerrascaleSyncV1MsgpackMessage,
  type TerrascaleSyncV1MsgpackSnapshot,
} from "./msgpack/generated/terrascale-sync-v1-msgpack.js";
import {
  createSyncV1Query,
  type ReactiveQueryCallbacks,
} from "./react/core.js";
import {
  TerraScaleSyncV1Client,
  SyncV1MemoryViewStore,
  createSyncV1BrowserViewStore,
  syncV1ResultDigest,
  type SyncV1ResultItem,
  type SyncV1StoredViewState,
  type SyncV1ViewStore,
} from "./index.js";
import { SyncV1NodeFileViewStore } from "./local/view-store.js";

const encoder = new TextEncoder();
const decoder = new TextDecoder();

const item = (identity: string, value: string): SyncV1ResultItem => ({
  identity,
  value: { value: encoder.encode(value) },
});

function wireSnapshot(
  items: readonly SyncV1ResultItem[],
  revision = 1n,
  viewGeneration = 1n,
  authorizationGeneration = 1n,
): TerrascaleSyncV1MsgpackSnapshot {
  return {
    view_generation: viewGeneration,
    authorization_generation: authorizationGeneration,
    revision,
    items: items.map(entry => ({
      identity: entry.identity,
      fields: Object.entries(entry.value).map(([path, value]) => ({ path, value })),
    })),
    digest: syncV1ResultDigest(items),
    checkpoint: encoder.encode(`checkpoint-${viewGeneration}-${revision}`),
  };
}

function serverFrame(message: TerrascaleSyncV1MsgpackMessage): Uint8Array {
  return encodeTerrascaleSyncV1MsgpackEnvelope({
    features: 0,
    correlation: 1n,
    causation: 0n,
    message,
  });
}

function base64(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString("base64");
}

function attachBody(snapshot: TerrascaleSyncV1MsgpackSnapshot, generation: bigint): Record<string, unknown> {
  return {
    viewId: "orders",
    generation: Number(generation),
    capability: "cap",
    checkpoint: base64(snapshot.checkpoint),
    snapshot: {
      view_generation: Number(snapshot.view_generation),
      authorization_generation: Number(snapshot.authorization_generation),
      revision: Number(snapshot.revision),
      items: snapshot.items.map(entry => ({
        identity: entry.identity,
        fields: entry.fields.map(field => ({ path: field.path, value: base64(field.value) })),
      })),
      digest: snapshot.digest,
      checkpoint: base64(snapshot.checkpoint),
    },
  };
}

/** Delegates to the memory store while recording every durable operation. */
class RecordingViewStore implements SyncV1ViewStore {
  readonly views: string[] = [];
  readonly saves: Array<{ readonly view: string; readonly state: SyncV1StoredViewState }> = [];
  readonly clears: string[] = [];
  readonly #inner = new SyncV1MemoryViewStore();

  public load(view: string): Promise<SyncV1StoredViewState | undefined> {
    this.views.push(view);
    return this.#inner.load(view);
  }

  public save(view: string, state: SyncV1StoredViewState): Promise<void> {
    this.saves.push({ view, state: { ...state, items: [...state.items] } });
    return this.#inner.save(view, state);
  }

  public clear(view: string): Promise<void> {
    this.clears.push(view);
    return this.#inner.clear(view);
  }
}

const subscribeOptions = {
  subscription: "orders",
  databaseId: "db",
  query: "select * from orders",
  parameters: new Uint8Array(),
};

async function waitFor(predicate: () => boolean, description: string): Promise<void> {
  const deadline = Date.now() + 1_000;
  while (!predicate()) {
    if (Date.now() >= deadline) throw new Error(`timed out waiting for ${description}`);
    await new Promise<void>(resolve => setTimeout(resolve, 0));
  }
}

const observerFor = (values: (readonly SyncV1ResultItem[])[]): ReactiveQueryCallbacks<readonly SyncV1ResultItem[]> => ({
  next: value => values.push(value),
  error: () => undefined,
  reset: () => undefined,
});

describe("free local reads", () => {
  test("emits the cached result before the network attach completes", async () => {
    const store = new RecordingViewStore();
    const seed = new TerraScaleSyncV1Client({
      fetch: async () => Response.json(attachBody(wireSnapshot([item("row", "cached")]), 1n)),
    });
    const seeded = await seed.subscribe({ ...subscribeOptions, store, autoStart: false });
    await waitFor(() => store.saves.length === 1, "the seeded durable write");
    seeded.close();

    // The network hangs forever; the cached rows must still be published.
    const offline = new TerraScaleSyncV1Client({
      fetch: () => new Promise<Response>(() => undefined),
    });
    const values: (readonly SyncV1ResultItem[])[] = [];
    void createSyncV1Query(offline, { ...subscribeOptions, store }).subscribe(
      observerFor(values),
      { signal: new AbortController().signal },
    );
    await waitFor(() => values.length > 0, "the cached free read");
    assert.equal(values[0]!.length, 1);
    assert.equal(decoder.decode(values[0]![0]!.value.value), "cached");
  });

  test("resumes attach from the persisted checkpoint", async () => {
    const store = new RecordingViewStore();
    const seed = new TerraScaleSyncV1Client({
      fetch: async () => Response.json(attachBody(wireSnapshot([item("row", "cached")]), 1n)),
    });
    (await seed.subscribe({ ...subscribeOptions, store, autoStart: false })).close();
    await waitFor(() => store.saves.length === 1, "the seeded durable write");

    const attachBodies: Array<Record<string, unknown>> = [];
    const client = new TerraScaleSyncV1Client({
      fetch: async (input, init) => {
        const request = new Request(input, init);
        if (request.method !== "POST") return new Response(null, { status: 204 });
        attachBodies.push(JSON.parse(await request.clone().text()) as Record<string, unknown>);
        // A checkpoint-only attach: the persisted cut is still current.
        return Response.json({
          viewId: "orders",
          generation: 1,
          checkpoint: base64(encoder.encode("checkpoint-1-1")),
          capability: "cap",
          snapshotUrls: [],
        });
      },
    });
    const live = await client.subscribe({ ...subscribeOptions, store, autoStart: false });
    assert.equal(attachBodies.length, 1);
    assert.equal(attachBodies[0]!.checkpoint, base64(encoder.encode("checkpoint-1-1")));
    assert.deepEqual(live.view.getItems().map(entry => entry.identity), ["row"]);
    assert.equal(live.view.getRevision(), 1n);
    live.close();
  });

  test("reset_required clears the store and re-persists the fresh cut", async () => {
    const store = new RecordingViewStore();
    const seed = new TerraScaleSyncV1Client({
      fetch: async () => Response.json(attachBody(wireSnapshot([item("row", "cached")]), 1n)),
    });
    (await seed.subscribe({ ...subscribeOptions, store, autoStart: false })).close();
    await waitFor(() => store.saves.length === 1, "the seeded durable write");

    let attaches = 0;
    const client = new TerraScaleSyncV1Client({
      fetch: async (input, init) => {
        const request = new Request(input, init);
        if (request.method !== "POST") return new Response(null, { status: 204 });
        attaches += 1;
        if (attaches === 1) {
          return Response.json({ error: { reason: "retention_advanced", message: "history has expired" } }, { status: 410 });
        }
        return Response.json(attachBody(wireSnapshot([item("fresh", "authorized")], 5n, 2n), 2n));
      },
    });
    const live = await client.subscribe({ ...subscribeOptions, store, autoStart: false });
    assert.equal(attaches, 2);
    assert.deepEqual(store.clears, ["orders"]);
    assert.deepEqual(live.reset, { reason: "retention-advanced", message: "history has expired" });
    assert.deepEqual(live.view.getItems().map(entry => entry.identity), ["fresh"]);
    await waitFor(() => store.saves.some(save => save.state.revision === "5"), "the re-persisted fresh cut");
    const persisted = await store.load("orders");
    assert.equal(persisted?.revision, "5");
    assert.equal(persisted?.digest, syncV1ResultDigest([item("fresh", "authorized")]));
    live.close();
  });

  test("batches durable writes per frame batch", async () => {
    const store = new RecordingViewStore();
    const client = new TerraScaleSyncV1Client({
      fetch: async (input, init) => {
        const request = new Request(input, init);
        if (request.method !== "POST") return new Response(null, { status: 204 });
        return Response.json(attachBody(wireSnapshot([item("row", "v1")]), 1n));
      },
    });
    const live = await client.subscribe({ ...subscribeOptions, store, autoStart: false });
    await waitFor(() => store.saves.length === 1, "the attach snapshot write");

    client.receive(serverFrame({
      kind: "delta",
      subscription: "orders",
      view_generation: 1n,
      from_revision: 1n,
      to_revision: 2n,
      operations: [{ kind: "update", identity: "row", fields: [{ path: "value", value: encoder.encode("v2") }] }],
      digest: syncV1ResultDigest([item("row", "v2")]),
      checkpoint: encoder.encode("checkpoint-1-2"),
    }));
    client.receive(serverFrame({
      kind: "delta",
      subscription: "orders",
      view_generation: 1n,
      from_revision: 2n,
      to_revision: 3n,
      operations: [{ kind: "insert", identity: "row-2", position: 1, fields: [{ path: "value", value: encoder.encode("r2") }] }],
      digest: syncV1ResultDigest([item("row", "v2"), item("row-2", "r2")]),
      checkpoint: encoder.encode("checkpoint-1-3"),
    }));
    client.receive(serverFrame({
      kind: "delta",
      subscription: "orders",
      view_generation: 1n,
      from_revision: 3n,
      to_revision: 4n,
      operations: [{ kind: "update", identity: "row-2", fields: [{ path: "value", value: encoder.encode("v4") }] }],
      digest: syncV1ResultDigest([item("row", "v2"), item("row-2", "v4")]),
      checkpoint: encoder.encode("checkpoint-1-4"),
    }));
    await waitFor(() => store.saves.length === 2, "the coalesced frame-batch write");
    assert.equal(store.saves.length, 2);
    assert.equal(store.saves[1]!.state.revision, "4");

    // Closing only transitions online state; it must not rewrite the store.
    live.close();
    await new Promise<void>(resolve => setTimeout(resolve, 5));
    assert.equal(store.saves.length, 2);
  });

  test("the Node file view store round-trips and rejects corrupt files", async () => {
    const directory = await mkdtemp(join(tmpdir(), "terrascale-view-store-"));
    try {
      const store = new SyncV1NodeFileViewStore(directory);
      const state: SyncV1StoredViewState = {
        query: "select 1",
        parameters: base64(new Uint8Array([1])),
        view_generation: "1",
        authorization_generation: "1",
        revision: "3",
        items: [{ identity: "row", fields: [{ path: "value", value: base64(encoder.encode("file")) }] }],
        digest: syncV1ResultDigest([item("row", "file")]),
        checkpoint: base64(encoder.encode("checkpoint-1-3")),
      };
      await store.save("orders", state);
      assert.deepEqual(await store.load("orders"), state);
      await store.clear("orders");
      assert.equal(await store.load("orders"), undefined);
      await writeFile(join(directory, `${encodeURIComponent("bad")}.json`), "{not json", "utf8");
      assert.equal(await store.load("bad"), undefined);
    } finally {
      await rm(directory, { recursive: true, force: true });
    }
  });

  test("the browser view store falls back to memory without IndexedDB", async () => {
    const store = await createSyncV1BrowserViewStore();
    assert.ok(store instanceof SyncV1MemoryViewStore);
  });
});
