import { expect, test } from "vitest";
import { v1Filter } from "../src/index.js";
import { fixture, json } from "./helpers/v1-http.js";

test("search builders and raw filters preserve paths, score order and result scores", async () => {
  const { client, requests } = await fixture((_request, response) => json(response, {
    items: [{ id: "article", version: "tbv1-a", state: "value", document: { title: "TerraBase" }, score: 1.25 }], next_cursor: "opaque-score-cursor",
  }));
  const articles = client.database("app").collection("articles");
  const filter = v1Filter.search("/metadata/a~1b~0c", "database storage");
  expect(filter).toEqual({ search: ["/metadata/a~1b~0c", "database storage"] });
  const query = { filter: v1Filter.and(v1Filter.eq("/published", true), filter), sort: [/** @type {const} */ ({ path: "$score", order: "desc" })] };
  const page = await articles.query(query);
  expect(page.items[0]?.score).toBe(1.25);
  expect(JSON.parse(requests[0]?.body ?? "")).toEqual({
    filter: { and: [{ eq: ["/published", true] }, filter] }, sort: [{ path: "$score", order: "desc" }],
  });
  await articles.query({ ...query, cursor: page.nextCursor });
  expect(JSON.parse(requests[1]?.body ?? "")).toEqual({ ...query, cursor: "opaque-score-cursor" });
  await articles.query({ filter: { search: ["/title", "database"] }, sort: [{ path: "$score", order: "desc" }] });
  expect(JSON.parse(requests[2]?.body ?? "").filter).toEqual(v1Filter.search("/title", "database"));
  const geoFilter = v1Filter.and(
    filter,
    v1Filter.withinRadius("/loc", { lat: 0, lon: 0, meters: 1000 }),
    v1Filter.withinBox("/loc", { south: -1, west: -1, north: 1, east: 1 }),
  );
  await articles.query({ filter: geoFilter, sort: [{ path: "$score", order: "desc" }] });
  expect(JSON.parse(requests[3]?.body ?? "")).toEqual({
    filter: { and: [filter, { within: ["/loc", { lat: 0, lon: 0, meters: 1000 }] }, { within: ["/loc", { south: -1, west: -1, north: 1, east: 1 }] }] },
    sort: [{ path: "$score", order: "desc" }],
  });
});

test("invalid search filters and score sorts fail before HTTP", async () => {
  const { client, requests } = await fixture((_request, response) => json(response, { items: [], next_cursor: null }));
  const articles = client.database("app").collection("articles");
  for (const path of ["$id", "$score", "title", "/bad~2", "/\ud800", [], ["title"]])
    expect(() => v1Filter.search(/** @type {any} */ (path), "database")).toThrow(TypeError);
  for (const terms of ["", "\0", "\ud800", "é".repeat(2049)])
    expect(() => v1Filter.search("/title", terms)).toThrow(TypeError);
  expect(() => v1Filter.search("/title", "é".repeat(2048))).not.toThrow();
  for (const query of [
    { filter: { search: ["$id", "database"] } }, { filter: { search: ["/title", 1] } },
    { filter: { search: ["/title"] } }, { filter: { search: [["title"], "database"] } },
    { filter: { search: ["/title", ""] } }, { filter: { search: ["/title", "\0"] } },
    { filter: { search: ["/title", "é".repeat(2049)] } },
    { filter: v1Filter.search("/title", "database"), sort: [{ path: "$score", order: "desc" }, { path: "$id" }] },
    { filter: v1Filter.and(v1Filter.search("/title", "a"), v1Filter.search("/body", "b")), sort: [{ path: "$score", order: "desc" }] },
    { filter: v1Filter.search("/title", "database"), sort: [{ near: { path: "/embedding", vector: [1] } }] }, { sort: [{ path: "$score", order: "desc" }] },
    { filter: v1Filter.search("/title", "database"), sort: [{ near: { path: "/loc", lat: 0, lon: 0 } }] },
    { filter: v1Filter.search("/title", "database"), sort: [{ path: "$score", order: "asc" }] },
    { filter: v1Filter.search("/title", "database"), sort: [{ path: "$score" }] },
  ]) await expect(articles.query(/** @type {any} */ (query))).rejects.toThrow(TypeError);
  expect(requests).toHaveLength(0);
});

test("text indexes send kind and preserve it on create and list", async () => {
  const index = { name: "articles_text", collection: "articles", fields: ["title", "metadata.summary"], state: "active", kind: "text" };
  const { client, requests } = await fixture((request, response) => json(response,
    request.method === "GET" ? { items: [index] } : index, request.method === "POST" ? 201 : 200));
  const indexes = client.database("app").indexes;
  expect(await indexes.create({ collection: "articles", fields: index.fields, kind: "text" })).toEqual(index);
  expect((await indexes.list()).items[0]?.kind).toBe("text");
  expect(JSON.parse(requests[0]?.body ?? "")).toEqual({ collection: "articles", fields: index.fields, kind: "text" });
  await expect(indexes.create(/** @type {any} */ ({ collection: "articles", fields: ["title"], kind: "unknown" }))).rejects.toThrow(TypeError);
  expect(requests).toHaveLength(2);
});

test.each(["invalid", null])("malformed score %j is rejected", async score => {
  const { client } = await fixture((_request, response) => json(response, {
    items: [{ id: "article", version: "tbv1-a", state: "value", document: {}, score }], next_cursor: null,
  }));
  await expect(client.database("app").collection("articles").query({ filter: v1Filter.search("/title", "database") })).rejects.toThrow(/score/u);
});

test("missing text index preserves invalid_query and sends once", async () => {
  const { client, requests } = await fixture((_request, response) => json(response, { error: { code: "invalid_query", message: "Text index required" } }, 400));
  await expect(client.database("app").collection("articles").query({ filter: v1Filter.search("/title", "database") })).rejects.toMatchObject({ status: 400, code: "invalid_query" });
  expect(requests).toHaveLength(1);
});
