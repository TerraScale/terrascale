import { expect, test } from "vitest";
import { fixture, json, prefix } from "./helpers/v1-http.js";
import { ExactDecimal } from "../src/index.js";
const base = `${prefix}/databases/app`;
const field = { path: "email", present: 2, types: { string: 2 } };
const inferred = { collection: "users", documents: 2, sampledDocuments: 2, fields: [field], uniqueConstraints: null };
/** @type {import("../src/v1/database-schema.js").V1UniqueSchemaRequest} */
const declaration = { fields: [{ name: "email", unique: true, normalization: "trim_lowercase" }] };

test("inferred schema, unique declarations and query statistics use distinct public paths", async () => {
  const values = [{ items: [{ name: "users", documents: 2, bytes: 100 }] }, inferred,
    { constraints: [{ name: "email", fields: ["email"], normalization: "trim_lowercase" }] },
    { items: [{ id: "a".repeat(64), collection: "users", shape: "SELECT * FROM users", queries: 3,
      totalLatencyMs: 1.5, maxLatencyMs: 0.8, rowsScanned: 4, lastSeenAt: "2026-10-22T10:00:00Z" }] }];
  const { client, requests } = await fixture((_r, response) => json(response, values[requests.length - 1]));
  const db = client.database("app");
  expect(await db.schema.list()).toEqual(values[0]);
  expect(await db.schema.infer("users")).toEqual(values[1]);
  expect(await db.schema.declare("users", declaration)).toEqual(values[2]);
  expect(await db.schema.queryShapes()).toEqual(values[3]);
  expect(requests.map(r => [r.method, r.url.pathname])).toEqual([
    ["GET", `${base}/schema`], ["GET", `${base}/schema/users`], ["PUT", `${base}/schema/users`], ["GET", `${base}/query-shapes`],
  ]);
  expect(JSON.parse(requests[2]?.body ?? "")).toEqual(declaration);
});
test("large management counts retain exact numeric values", async () => {
  const { client } = await fixture((_r, response) => response.writeHead(200, { "content-type": "application/json" })
    .end('{"items":[{"name":"users","documents":9007199254740993,"bytes":0}]}'));
  const result = await client.database("app").schema.list();
  expect(result.items[0]?.documents).toBeInstanceOf(ExactDecimal);
  expect(String(result.items[0]?.documents)).toBe("9007199254740993");
});
test.each([400, 401, 403, 404, 409, 503])("schema declaration keeps producer error %i without replay", async status => {
  const { client, requests } = await fixture((_r, response) => json(response, { error: { code: "invalid_schema", message: "Refused" } }, status));
  await expect(client.database("app").schema.declare("users", declaration)).rejects.toMatchObject({ status, code: "invalid_schema" });
  expect(requests).toHaveLength(1);
});
test.each([{ fields: {} }, { documents: "2" }, { uniqueConstraints: [{}] }])("inferred schema rejects invalid response %j", async patch => {
  const { client } = await fixture((_r, response) => json(response, { ...inferred, ...patch }));
  await expect(client.database("app").schema.infer("users")).rejects.toThrow(TypeError);
});
test("unique declarations forward omitted flags for the producer's false default", async () => {
  const { client, requests } = await fixture((_r, response) => json(response, { constraints: [] }));
  const request = { fields: [{ name: "email" }], indexes: [{ name: "email_index", fields: ["email"] }] };
  expect(await client.database("app").schema.declare("users", request)).toEqual({ constraints: [] });
  expect(JSON.parse(requests[0]?.body ?? "")).toEqual(request);
});
