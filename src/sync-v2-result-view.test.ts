import assert from "node:assert/strict";
import { describe, test } from "node:test";

import {
  SyncV2ResultViewStore,
  SyncV2ViewCheckpoint,
  syncV2ResultDigest,
  type SyncV2ResultItem,
} from "./index.js";

const row = (identity: string, value: string): SyncV2ResultItem => ({
  identity,
  value: { value: new TextEncoder().encode(value) },
});

const snapshot = (items: readonly SyncV2ResultItem[], revision = 1n, generation = 1n) => ({
  view_generation: generation,
  authorization_generation: 1n,
  revision,
  items,
  digest: syncV2ResultDigest(items),
  checkpoint: new SyncV2ViewCheckpoint(`checkpoint-${generation}-${revision}`),
});

describe("current v2 authorized result view", () => {
  test("applies atomic insert/update/patch/remove/move revisions and ignores duplicates", () => {
    const store = new SyncV2ResultViewStore();
    store.bootstrap(snapshot([row("a", "a"), row("b", "b")]));
    const items = [row("b", "changed"), row("c", "c")];
    const delta = {
      view_generation: 1n,
      from_revision: 1n,
      to_revision: 2n,
      operations: [
        { kind: "Patch" as const, identity: "a", changes: [{ path: "value", value: new TextEncoder().encode("patched") }] },
        { kind: "Move" as const, identity: "a", position: 1 },
        { kind: "Remove" as const, identity: "b" },
        { kind: "Insert" as const, identity: "c", position: 0, value: row("c", "c").value },
        { kind: "Update" as const, identity: "c", value: row("c", "c").value },
      ],
      digest: syncV2ResultDigest([row("c", "c"), row("a", "patched")]),
      checkpoint: new SyncV2ViewCheckpoint("checkpoint-1-2"),
    };
    assert.equal(store.applyDelta(delta), "applied");
    assert.equal(store.applyDelta(delta), "duplicate");
    assert.deepEqual(store.getItems().map(item => item.identity), ["c", "a"]);
    assert.equal(store.applyDelta({ ...delta, from_revision: 9n, to_revision: 10n }), "gap");
    void items;
  });

  test("refuses disconnected durable mutations and rolls back rejected presentation", () => {
    const store = new SyncV2ResultViewStore();
    store.bootstrap(snapshot([row("a", "old")]));
    store.setOnline(false);
    assert.throws(
      () => store.beginMutation("offline-key", [{ kind: "Update", identity: "a", value: row("a", "new").value }]),
      /offline/u,
    );
    store.setOnline(true);
    store.beginMutation("stable-key", [{ kind: "Update", identity: "a", value: row("a", "new").value }]);
    assert.equal(new TextDecoder().decode(store.getVisibleItems()[0]!.value.value), "new");
    assert.equal(store.rejectMutation("stable-key"), "rejected");
    assert.equal(new TextDecoder().decode(store.getVisibleItems()[0]!.value.value), "old");
    assert.equal(store.rejectMutation("stable-key"), "rejected");
  });

  test("authorization transitions require a newer view and policy generation", () => {
    const store = new SyncV2ResultViewStore();
    store.bootstrap(snapshot([row("a", "visible")], 1n, 1n));
    store.transitionAuthorization(2n, ["a"], {
      ...snapshot([row("b", "expanded")], 2n, 2n),
      authorization_generation: 2n,
    });
    assert.equal(store.getAuthorizationGeneration(), 2n);
    assert.deepEqual(store.getItems().map(item => item.identity), ["b"]);
  });
});
