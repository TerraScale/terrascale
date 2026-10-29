import { generateId } from "../src/ids.js";
import { describe, expect, test } from "vitest";
import { v1Filter } from "../src/index.js";
import { liveConfigured, liveDatabase } from "./support/terrabase-v1-live.js";

describe.skipIf(!liveConfigured)("real C# response bounds", () => {
  test("accepts a full 8 MiB query budget plus document framing", async () => {
    const database = liveDatabase();
    const name = `response_${generateId().toLowerCase()}`;
    await database.collections.create(name);
    const documents = database.collection(name);
    const run = generateId();
    const document = { run, padding: "" };
    document.padding = "x".repeat(1024 * 1024 - JSON.stringify(document).length);
    const ids = Array.from({ length: 8 }, () => generateId());
    try {
      for (const id of ids) await documents.put(id, document);
      const page = await documents.query({ filter: v1Filter.eq("/run", run), limit: 8 });
      expect(page.items).toHaveLength(8);
      expect(page.items.every(item => item.document.padding === document.padding)).toBe(true);
    } finally {
      await database.collections.drop(name);
    }
  });
});
