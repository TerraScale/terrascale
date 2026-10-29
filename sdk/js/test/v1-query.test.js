import { collections } from "./support/terrabase-v1-fixture.js";
import { generateId } from "../src/ids.js";
import { describe, expect, test } from "vitest";
import { v1Filter } from "../src/index.js";
import { liveConfigured, liveDatabase } from "./support/terrabase-v1-live.js";

describe.skipIf(!liveConfigured)("real C# queries", () => {
  test("filters, sorting, projection, count, pagination and query-bound cursors", async () => {
    const documents = liveDatabase().collection(collections.queries);
    const run = generateId();
    const ids = [generateId(), generateId(), generateId()];
    const rows = [
      { run, name: "Ana", age: 31, tags: ["admin"] },
      { run, name: "Bo", age: 25, tags: ["beta"] },
      { run, name: "Cy", age: 40, tags: [], nickname: null },
    ];
    try {
      for (const [index, id] of ids.entries()) await documents.put(id, /** @type {import("../src/v1/json.js").JsonInputObject} */ (rows[index]));
      const filter = v1Filter.eq("/run", run);
      const first = await documents.query({ filter, sort: [{ path: "/age", order: "asc" }], project: ["/name", "/age"], count: true, limit: 2 });
      expect(first.count).toEqual({ value: "3", exact: true });
      expect(first.items.map(item => item.document)).toEqual([{ name: "Bo", age: 25 }, { name: "Ana", age: 31 }]);
      const pages = [];
      for await (const page of documents.pages({ filter, sort: [{ path: "/age", order: "asc" }], limit: 2, count: true })) pages.push(page);
      expect(pages.map(page => page.items.length)).toEqual([2, 1]);
      expect(pages[1]?.count).toBeUndefined();
      expect(new Set(pages.flatMap(page => page.items.map(item => item.id))).size).toBe(3);
      /** @type {[import("../src/index.js").V1Filter, string[]][]} */
      const cases = [
        [v1Filter.eq("/age", 31), ["Ana"]], [v1Filter.ne("/age", 25), ["Ana", "Cy"]],
        [v1Filter.lt("/age", 30), ["Bo"]], [v1Filter.lte("/age", 31), ["Ana", "Bo"]],
        [v1Filter.gt("/age", 31), ["Cy"]], [v1Filter.gte("/age", 31), ["Ana", "Cy"]],
        [v1Filter.in("/age", [25, 40]), ["Bo", "Cy"]], [v1Filter.nin("/age", [25]), ["Ana", "Cy"]],
        [v1Filter.prefix("/name", "A"), ["Ana"]], [v1Filter.not(v1Filter.eq("/age", 31)), ["Bo", "Cy"]],
        [v1Filter.or(v1Filter.eq("/age", 25), v1Filter.eq("/age", 40)), ["Bo", "Cy"]],
      ];
      for (const [predicate, names] of cases) {
        const page = await documents.query({ filter: v1Filter.and(filter, predicate), sort: [{ path: "/name", order: "asc" }] });
        expect(page.items.map(item => item.document.name)).toEqual(names);
      }
      const matches = await documents.query({ filter: v1Filter.and(filter, v1Filter.contains("/tags", "admin")) });
      expect(matches.items.map(item => item.document.name)).toEqual(["Ana"]);
      const nulls = await documents.query({ filter: v1Filter.and(filter, v1Filter.exists("/nickname")) });
      expect(nulls.items.map(item => item.document.name)).toEqual(["Cy"]);
      await expect(documents.query({ filter: v1Filter.eq("/run", generateId()), cursor: first.nextCursor })).rejects.toMatchObject({ status: 400, code: "invalid_cursor" });
    } finally { for (const id of ids) await documents.delete(id).catch(() => undefined); }
  });
});
