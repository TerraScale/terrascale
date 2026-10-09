import { collections } from "./support/terrabase-v1-fixture.js";
import { generateId } from "../src/ids.js";
import { describe, expect, test } from "vitest";
import { v1Filter } from "../src/index.js";
import { liveConfigured, liveDatabase } from "./support/terrabase-v1-live.js";

describe.skipIf(!liveConfigured)("real C# collection shapes", () => {
  test("immutable snapshot bootstrap, log continuation and filtered deletes", async () => {
    const documents = liveDatabase().collection(collections.shapes);
    const id = generateId();
    const run = generateId();
    const where = v1Filter.and(v1Filter.eq("/run", run), v1Filter.eq("/active", true));
    try {
      const written = await documents.put(id, { run, active: true, title: "before" });
      const snapshot = await documents.shape({ where });
      expect(snapshot.offset).toMatch(/^snap\.\d+\.0$/u);
      expect(snapshot.upToDate).toBe(false);
      expect(snapshot.changes.flatMap(change => change.ops)).toEqual([{ op: "upsert", id, version: written.version, document: { run, active: true, title: "before" } }]);
      const updated = await documents.patch(id, { merge: { title: "after" } });
      const replay = await documents.shape({ where, handle: snapshot.handle, offset: snapshot.offset });
      expect(replay).toEqual(snapshot);
      let tail = await documents.shape({ where, handle: snapshot.handle, offset: snapshot.nextOffset });
      expect(tail.upToDate).toBe(true);
      expect(tail.changes.flatMap(change => change.ops)).toEqual([{ op: "upsert", id, version: updated.version, document: { run, active: true, title: "after" } }]);
      await documents.patch(id, { merge: { active: false } });
      tail = await documents.shape({ where, handle: tail.handle, offset: tail.nextOffset });
      expect(tail.changes.flatMap(change => change.ops)).toEqual([{ op: "delete", id }]);
      expect(tail.upToDate).toBe(true);
      const obsoleteHandle = tail.handle === "h1-0000000000000000" ? "h1-1111111111111111" : "h1-0000000000000000";
      await expect(documents.shape({ where, handle: obsoleteHandle, offset: tail.nextOffset })).rejects.toMatchObject({ status: 409, code: "must_refetch", handle: tail.handle });
      const abort = new AbortController();
      const pending = documents.shape({ where, handle: tail.handle, offset: tail.nextOffset, live: true }, { signal: abort.signal });
      abort.abort(new Error("test cancellation"));
      await expect(pending).rejects.toThrow(/test cancellation/u);
    } finally { await documents.delete(id).catch(() => undefined); }
  });

  test("shape long-poll wakes on real commits and advances the public cursor", async () => {
    const documents = liveDatabase().collection(collections.shapes);
    const id = generateId();
    const where = v1Filter.eq("/run", generateId());
    const snapshot = await documents.shape({ where });
    const head = await documents.shape({ where, handle: snapshot.handle, offset: snapshot.nextOffset });
    const signal = AbortSignal.timeout(10_000);
    const pending = documents.shape({ where, handle: head.handle, offset: head.nextOffset, live: true }, { signal });
    try {
      // A real durable commit wakes the collection's poll, even when its document does not match.
      const written = await documents.put(id, { unrelated: true });
      let live = await pending;
      // Other suites share this database and may wake the first poll before this write.
      while (BigInt(live.nextOffset) < BigInt(written.offset))
        live = await documents.shape({ where, handle: live.handle, offset: live.nextOffset, live: true, cursor: live.cursor }, { signal });
      expect(BigInt(live.nextOffset)).toBeGreaterThanOrEqual(BigInt(written.offset));
      expect(live.upToDate).toBe(true);
      expect(live.cursor).toMatch(/^\d+$/u);
      const next = await documents.shape({ where, handle: live.handle, offset: head.nextOffset, live: true, cursor: live.cursor });
      expect(BigInt(/** @type {string} */ (next.cursor))).toBeGreaterThan(BigInt(/** @type {string} */ (live.cursor)));
    } finally { await documents.delete(id).catch(() => undefined); }
  });

  test.skipIf(!process.env.TB_V1_READ_KEY)("documents:read does not grant shapes:read", async () => {
    await expect(liveDatabase(process.env.TB_V1_READ_KEY).collection(collections.shapes).shape())
      .rejects.toMatchObject({ status: 403, code: "forbidden_scope" });
  });
});
