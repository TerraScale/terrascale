import { collections } from "./support/terrabase-v1-fixture.js";
import { generateId } from "../src/ids.js";
import { inspect } from "node:util";
import { describe, expect, test } from "vitest";
import { ExactDecimal } from "../src/index.js";
import { liveConfigured, liveClient, liveDatabase } from "./support/terrabase-v1-live.js";

describe.skipIf(!liveConfigured)("real C# documents", () => {
  test("create, conditional replacement, both patch media types, 304 and tombstone recreation", async () => {
    const documents = liveDatabase().collection(collections.documents);
    const created = await documents.create({ count: 1, remove: true, tags: [] });
    try {
      expect(created.created).toBe(true);
      expect(created.location).toContain(`/projects/${process.env.TB_V1_PROJECT}/`);
      const read = await documents.get(created.id);
      expect(read).toEqual({ id: created.id, version: created.version, state: "value", document: { count: 1, remove: true, tags: [] } });
      await expect(documents.get(created.id, { ifNoneMatch: read.version })).rejects.toMatchObject({ status: 304, code: "not_modified" });
      const updated = await documents.put(created.id, { count: 2, remove: true, tags: [] }, { ifMatch: read.version });
      expect(updated.created).toBe(false);
      expect(BigInt(updated.offset)).toBeGreaterThan(BigInt(created.offset));
      await expect(documents.put(created.id, { count: 99 }, { ifMatch: read.version })).rejects.toMatchObject({ status: 412, code: "precondition_failed" });
      await documents.patch(created.id, { merge: { remove: null, nested: { flag: true } } });
      await documents.patch(created.id, { ops: [
        { op: "inc", path: "/count", by: new ExactDecimal("0.1") },
        { op: "append", path: "/tags", value: "one" },
        { op: "set", path: "/nested/name", value: "Ana" },
        { op: "unset", path: "/nested/flag" },
      ] });
      expect((await documents.get(created.id)).document).toEqual({ count: 2.1, tags: ["one"], nested: { name: "Ana" } });
      const deleted = await documents.delete(created.id);
      await expect(documents.get(created.id)).rejects.toMatchObject({ status: 404, code: "not_found", etag: deleted.version });
      const recreated = await documents.put(created.id, { restored: true }, { ifMatch: deleted.version });
      expect(recreated.created).toBe(true);
    } finally { await documents.delete(created.id).catch(() => undefined); }
  });

  test("credentials stay private and real keys enforce read/write scopes", async () => {
    const client = liveClient();
    expect(JSON.stringify(client)).not.toContain(process.env.TB_V1_DATA_KEY);
    expect(inspect(client, { showHidden: true, depth: 8 })).not.toContain(process.env.TB_V1_DATA_KEY);
    const id = generateId();
    await expect(liveDatabase(`tb_${"x".repeat(43)}`).collection(collections.documents).get(id)).rejects.toMatchObject({ status: 401, code: "invalid_key" });
    if (process.env.TB_V1_READ_KEY)
      await expect(liveDatabase(process.env.TB_V1_READ_KEY).collection(collections.documents).put(id, {})).rejects.toMatchObject({ status: 403, code: "forbidden_scope" });
    if (process.env.TB_V1_WRITE_KEY)
      await expect(liveDatabase(process.env.TB_V1_WRITE_KEY).collection(collections.documents).get(id)).rejects.toMatchObject({ status: 403, code: "forbidden_scope" });
  });
});

