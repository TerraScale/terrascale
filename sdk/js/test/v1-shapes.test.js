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
});
