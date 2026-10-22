import { expect, test } from "vitest";
import { entity, fixture, json, prefix, token } from "./helpers/v1-http.js";
const input = { name: "Active users", sql: "SELECT * FROM users" };
const saved = { id: entity, ...input, createdAt: "2026-10-22T10:00:00Z", updatedAt: null };

test("saved queries use all five public project routes with opaque management pagination", async () => {
  const { client, requests } = await fixture((request, response) => {
    if (request.method === "DELETE") { response.writeHead(204).end(); return; }
    json(response, request.method === "GET" && request.url.pathname.endsWith("/saved-queries") ? { items: [saved], next: "opaque+/=" } : saved,
      request.method === "POST" ? 201 : 200);
  });
  expect(await client.savedQueries.create(input)).toEqual(saved);
  expect(await client.savedQueries.list({ after: "opaque+/=", limit: 100 })).toEqual({ items: [saved], next: "opaque+/=" });
  expect(await client.savedQueries.get(entity)).toEqual(saved);
  expect(await client.savedQueries.update(entity, input)).toEqual(saved);
  expect(await client.savedQueries.delete(entity)).toBeUndefined();
  expect(requests.map(r => [r.method, r.url.pathname, r.body])).toEqual([
    ["POST", `${prefix}/saved-queries`, JSON.stringify(input)], ["GET", `${prefix}/saved-queries`, ""],
    ["GET", `${prefix}/saved-queries/${entity}`, ""], ["PUT", `${prefix}/saved-queries/${entity}`, JSON.stringify(input)],
    ["DELETE", `${prefix}/saved-queries/${entity}`, ""],
  ]);
  expect(requests[1]?.url.searchParams.get("after")).toBe("opaque+/=");
  for (const request of requests) {
    expect(request.headers.authorization).toBe(`Bearer ${token}`);
    expect(request.headers["idempotency-key"]).toBeUndefined();
    if (request.body) expect(request.headers["content-type"]).toBe("application/json");
  }
  expect(JSON.stringify(client)).toBe("{}");
});
test.each([400, 401, 403, 404, 421, 503])("saved mutation preserves error %i and never retries", async status => {
  const { client, requests } = await fixture((_r, response) => {
    response.setHeader("x-request-id", "saved-request");
    json(response, { error: { code: "refused", message: "Refused" } }, status);
  });
  await expect(client.savedQueries.update(entity, input)).rejects.toMatchObject({ status, code: "refused", requestId: "saved-request" });
  expect(requests).toHaveLength(1);
});
test("validates saved query identifiers, input and page bounds before dispatch", async () => {
  const { client, requests } = await fixture((_r, response) => json(response, saved));
  await expect(client.savedQueries.get("query_prefixed")).rejects.toThrow(TypeError);
  await expect(client.savedQueries.create({ ...input, name: "!" })).rejects.toThrow(TypeError);
  await expect(client.savedQueries.create({ ...input, sql: "a".repeat(16385) })).rejects.toThrow(TypeError);
  await expect(client.savedQueries.list({ limit: 101 })).rejects.toThrow(TypeError);
  await expect(client.savedQueries.delete(entity, { signal: AbortSignal.abort("cancel") })).rejects.toBe("cancel");
  await expect(client.savedQueries.create(input, /** @type {any} */ ({ idempotencyKey: "key" }))).rejects.toThrow(/Idempotency-Key/u);
  expect(requests).toHaveLength(0);
});
test.each([{ id: "invalid" }, { updatedAt: 1 }, { sql: null }])("rejects malformed saved query %j", async patch => {
  const { client } = await fixture((_r, response) => json(response, { ...saved, ...patch }));
  await expect(client.savedQueries.get(entity)).rejects.toThrow(TypeError);
});
test("saved SQL uses the producer's UTF-16 length limit rather than UTF-8 bytes", async () => {
  const sql = `SELECT '${"é😀".repeat(5458)}'`;
  expect(sql.length).toBe(16383);
  const { client, requests } = await fixture((_r, response) => json(response, { ...saved, sql }, 201));
  expect((await client.savedQueries.create({ ...input, sql: `${sql} ` })).sql).toBe(sql);
  expect(JSON.parse(requests[0]?.body ?? "").sql).toBe(`${sql} `);
  await expect(client.savedQueries.update(entity, { ...input, sql: `${sql}  ` })).rejects.toThrow(/UTF-16/u);
  expect(requests).toHaveLength(1);
});
