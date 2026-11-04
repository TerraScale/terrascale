import { collections } from "./support/terrabase-v1-fixture.js";
import { generateId } from "../src/ids.js";
import { describe, expect, test } from "vitest";
import { v1Filter } from "../src/index.js";
import { liveConfigured, liveDatabase } from "./support/terrabase-v1-live.js";

describe.skipIf(!liveConfigured)("real C# transaction and collection conformance", () => {
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
});
