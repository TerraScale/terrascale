// TerraBase v1 queries through a real HTTP boundary against the in-test
// contract fixture (a stand-in until a packaged tb-server serves v1).
import { afterAll, beforeAll, describe, expect, test } from "vitest";
import { createTerraBaseV1Client, documentIdFor, ExactDecimal, TerraBaseV1Error, v1Filter } from "../src/index.js";
import { startTerraBaseV1Fixture } from "./support/terrabase-v1-fixture.js";

/** @type {Awaited<ReturnType<typeof startTerraBaseV1Fixture>>} */
let fixture;
/** @type {import("../src/index.js").V1Collection} */
let people;
/** @type {Record<string, string>} */
const ids = {};

const seed = /** @type {const} */ ([
  ["ana", { name: "Ana", email: "Ana@Example.com", age: 31, score: 10n ** 30n, tags: ["admin", "beta"], org: "acme", created_at: "1759363200001" }],
  ["bo", { name: "Bo", email: "bo@example.com", age: 25, score: 7, tags: ["beta"], org: "acme", created_at: "1759363200002" }],
  ["cy", { name: "Cy", email: "cy@other.org", age: 40, score: 7.5, tags: [], org: "globex", created_at: "1759363200003", nickname: null }],
  ["dee", { name: "Dee", email: "dee@example.com", age: 25, tags: ["admin"], org: "acme", created_at: "1759363200004" }],
  ["eve", { name: "Eve", email: "eve@other.org", age: 52, score: "n/a", org: "initech", created_at: "1759363200005" }],
]);

beforeAll(async () => {
  fixture = await startTerraBaseV1Fixture();
  const client = createTerraBaseV1Client({ baseUrl: fixture.baseUrl, token: fixture.token });
  people = client.database("crm").collection("people");
  for (const [name, doc] of seed) {
    ids[name] = await documentIdFor("people", name);
    await people.put(/** @type {string} */ (ids[name]), doc);
  }
});
afterAll(async () => {
  await fixture.close();
});

/**
 * @param {import("../src/index.js").V1Filter} filter
 * @returns {Promise<string[]>}
 */
async function names(filter) {
  const page = await people.query({ filter, sort: [{ path: "/name", order: "asc" }], limit: 100 });
  return page.items.map((item) => (item.state === "value" ? String(item.document.name) : "conflict"));
}

describe("TerraBase v1 queries", () => {
  test("every filter operator follows the query grammar", async () => {
    const f = v1Filter;
    expect(await names(f.eq("/org", "acme"))).toEqual(["Ana", "Bo", "Dee"]);
    expect(await names(f.ne("/score", 7))).toEqual(["Ana", "Cy", "Dee", "Eve"]);
    expect(await names(f.lt("/age", 30))).toEqual(["Bo", "Dee"]);
    expect(await names(f.lte("/age", 31))).toEqual(["Ana", "Bo", "Dee"]);
    expect(await names(f.gt("/score", 7))).toEqual(["Ana", "Cy"]);
    expect(await names(f.gte("/score", new ExactDecimal("7.0")))).toEqual(["Ana", "Bo", "Cy"]);
    expect(await names(f.eq("/score", 10n ** 30n))).toEqual(["Ana"]);
    expect(await names(f.in("/org", ["globex", "initech"]))).toEqual(["Cy", "Eve"]);
    expect(await names(f.nin("/org", ["acme"]))).toEqual(["Cy", "Eve"]);
    expect(await names(f.exists("/nickname"))).toEqual(["Cy"]);
    expect(await names(f.prefix(f.lower("/email"), "ana@"))).toEqual(["Ana"]);
    expect(await names(f.eq(f.lower("/email"), "ana@example.com"))).toEqual(["Ana"]);
    expect(await names(f.contains("/tags", "admin"))).toEqual(["Ana", "Dee"]);
    expect(await names(f.eq(f.id, /** @type {string} */ (ids.bo)))).toEqual(["Bo"]);
    expect(await names(f.and(f.eq("/org", "acme"), f.not(f.contains("/tags", "admin"))))).toEqual(["Bo"]);
    expect(await names(f.or(f.eq("/age", 40), f.eq("/age", 52)))).toEqual(["Cy", "Eve"]);
    expect(() => f.and()).toThrow(TypeError);
    expect(() => f.eq("name", "x")).toThrow(TypeError);
    expect(() => f.eq("/x", /** @type {any} */ ({ nested: true }))).toThrow(TypeError);
  });

  test("lower() sends {lower: path} and the server compares case-folded values", async () => {
    const cities = createTerraBaseV1Client({ baseUrl: fixture.baseUrl, token: fixture.token }).database("crm").collection("cities");
    await cities.put(await documentIdFor("cities", "de"), { name: "Straße" });
    await cities.put(await documentIdFor("cities", "gr"), { name: "ΟΔΥΣΣΕΥΣ" });
    /** @param {string} value */
    const folded = async (value) => (await cities.query({ filter: v1Filter.eq(v1Filter.lower("/name"), value) })).items.length;
    expect(await folded("strasse")).toBe(1);
    expect(await folded("οδυσσευσ")).toBe(1);
    const sent = JSON.parse(fixture.requests.filter((request) => request.path.endsWith("/cities/query")).at(-1)?.body ?? "{}");
    // The client never folds locally; the operand is sent as given.
    expect(sent.filter).toEqual({ eq: [{ lower: "/name" }, "οδυσσευσ"] });
  });

  test("sort, projection, count and pagination", async () => {
    const first = await people.query({
      filter: v1Filter.eq("/org", "acme"),
      sort: [{ path: "/age", order: "desc" }],
      project: ["/name", "/age"],
      limit: 2,
      count: true,
    });
    expect(first.count).toEqual({ value: "3", exact: true });
    expect(first.items.map((item) => item.state === "value" && item.document)).toEqual([{ name: "Ana", age: 31 }, { name: "Bo", age: 25 }]);
    expect(first.nextCursor).not.toBeNull();

    const pages = [];
    for await (const page of people.pages({ sort: [{ path: "/age", order: "asc" }], limit: 2, count: true })) pages.push(page);
    expect(pages.map((page) => page.items.length)).toEqual([2, 2, 1]);
    expect(pages[0]?.count).toEqual({ value: "5", exact: true });
    expect(pages[1]?.count).toBeUndefined();
    const ordered = pages.flatMap((page) => page.items.map((item) => item.id));
    // Equal ages (Bo, Dee: 25) are finally ordered by $id ascending.
    const [bo, dee] = [/** @type {string} */ (ids.bo), /** @type {string} */ (ids.dee)].sort();
    expect(ordered.slice(0, 2)).toEqual([bo, dee]);
    expect(new Set(ordered).size).toBe(5);
    const sent = fixture.requests.filter((request) => request.path === "/v1/databases/crm/collections/people/query").slice(-3).map((request) => JSON.parse(request.body));
    expect(sent[0]).toMatchObject({ count: true, limit: 2 });
    expect(sent[1]).not.toHaveProperty("count");
    expect(typeof sent[1].cursor).toBe("string");

    const everyone = [];
    for await (const item of people.items({ limit: 3 })) everyone.push(item.id);
    expect(everyone).toHaveLength(5);
  });

  test("a cursor is bound to its query and explain reports the plan", async () => {
    const page = await people.query({ filter: v1Filter.eq("/org", "acme"), limit: 1 });
    const error = await people.query({ filter: v1Filter.eq("/org", "globex"), cursor: page.nextCursor }).catch((/** @type {unknown} */ reason) => reason);
    expect(error).toBeInstanceOf(TerraBaseV1Error);
    expect(error).toMatchObject({ status: 400, code: "invalid_cursor" });

    const plan = await people.query({ filter: v1Filter.eq("/org", "acme"), explain: "plan" });
    expect(plan.items).toEqual([]);
    expect(plan.explain).toMatchObject({ plan: { kind: "collection_scan" } });
    const analyzed = await people.query({ filter: v1Filter.eq("/org", "acme"), explain: "analyze" });
    expect(analyzed.items).toHaveLength(3);
    expect(analyzed.explain).toMatchObject({ actual: { rows: "3" } });

    await expect(people.query(/** @type {any} */ ({ limit: 0 }))).rejects.toThrow(TypeError);
    await expect(people.query(/** @type {any} */ ({ order: "asc" }))).rejects.toThrow(TypeError);
  });
});
