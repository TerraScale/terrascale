import { collections } from "./support/terrabase-v1-fixture.js";
import { generateId } from "../src/ids.js";
import { describe, expect, test } from "vitest";
import { v1Filter } from "../src/index.js";
import { liveConfigured, liveDatabase } from "./support/terrabase-v1-live.js";

describe.skipIf(!liveConfigured)("real C# transaction and collection conformance", () => {
  test("pending commits validate only the operations sent to the producer", async () => {
    const database = liveDatabase();
    const documents = database.collection(collections.transactions);
    const first = generateId();
    const second = generateId();
    try {
      const transaction = database.transaction().put(collections.transactions, first, { value: 1 });
      const pending = transaction.commit();
      transaction.put(collections.transactions, second, { value: 2 });
      expect((await pending).results).toHaveLength(1);
      expect((await documents.get(first)).document.value).toBe(1);
      await expect(documents.get(second)).rejects.toMatchObject({ status: 404, code: "not_found" });
    } finally { await documents.delete(first).catch(() => undefined); }
  });

  test("cross-collection commits and assertion rollback preserve atomicity", async () => {
    const database = liveDatabase();
    const documents = database.collection(collections.transactions);
    const other = database.collection(collections.conformance);
    const existing = generateId();
    const first = generateId();
    const second = generateId();
    const marker = generateId();
    try {
      await documents.put(existing, { marker, active: true });
      const result = await database.transaction()
        .put(collections.transactions, first, { marker, value: 1 }, { ifNoneMatchAny: true })
        .create(collections.conformance, { marker, value: 2 }, second)
        .assert(collections.transactions, existing, v1Filter.eq("/active", true))
        .assertCount(collections.transactions, v1Filter.eq("/marker", marker), { gte: 1, lte: 1 })
        .commit();
      expect(result.results).toHaveLength(4);
      expect(result.results[3]).toEqual({ asserted: true, count: "1" });
      expect((await other.get(second)).document.value).toBe(2);
      await expect(database.transaction()
        .patch(collections.transactions, first, { merge: { value: 99 } })
        .assert(collections.transactions, existing, v1Filter.eq("/active", false))
        .commit()).rejects.toMatchObject({ status: 412, code: "precondition_failed", op: 1 });
      expect((await documents.get(first)).document.value).toBe(1);
      expect(() => database.transaction().put(collections.transactions, first, {}).delete(collections.transactions, first))
        .toThrow(/same document/u);
    } finally {
      for (const id of [existing, first]) await documents.delete(id).catch(() => undefined);
      await other.delete(second).catch(() => undefined);
    }
  });

  test("named collection create/get/list/drop and name conflict use real producer routes", async () => {
    const collections = liveDatabase().collections;
    const name = `c_${generateId().toLowerCase()}`;
    const created = await collections.create(name);
    try {
      expect(created.name).toBe(name);
      expect(await collections.get(name)).toEqual(created);
      await expect(collections.create(name)).rejects.toMatchObject({ status: 409, code: "name_taken" });
      const page = await collections.list({ limit: 200 });
      expect(page.items.some(item => item.name === name)).toBe(true);
    } finally { await collections.drop(name); }
    await expect(collections.get(name)).rejects.toMatchObject({ status: 404, code: "unknown_collection" });
  });
});
