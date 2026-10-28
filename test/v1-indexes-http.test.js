import { expect, test } from "vitest";
import { fixture, json, prefix, token } from "./helpers/v1-http.js";
const base = `${prefix}/databases/app`;
const index = { name: "users_email", collection: "users", fields: ["email"], state: "active" };

test("all five index routes preserve selector names and opaque recommendation IDs", async () => {
  const replies = [index, { items: [index] }, null, { items: [{ id: "users:email", collection: "users", fields: ["email"], reason: "filter", queries: 10 }] },
    { status: "applied", index }, { status: "already-applied", index }];
  const { client, requests } = await fixture((request, response) => {
    const i = requests.length - 1;
    if (request.method === "DELETE") { response.writeHead(204).end(); return; }
    json(response, replies[i], i === 0 || i === 4 ? 201 : 200);
  });
  const indexes = client.database("app").indexes;
  expect(await indexes.create({ collection: "users", fields: ["email"] })).toEqual(index);
  expect(await indexes.list({ collection: "users" })).toEqual(replies[1]);
  await indexes.drop("users_email");
  expect(await indexes.recommendations()).toEqual(replies[3]);
  expect(await indexes.applyRecommendation("users:email")).toEqual(replies[4]);
  expect(await indexes.applyRecommendation("users:email")).toEqual(replies[5]);
  expect(requests.map(r => [r.method, r.url.pathname])).toEqual([
    ["POST", `${base}/indexes`], ["GET", `${base}/indexes`], ["DELETE", `${base}/indexes/users_email`],
    ["GET", `${base}/index-recommendations`],
    ["POST", `${base}/index-recommendations/users%3Aemail/apply`], ["POST", `${base}/index-recommendations/users%3Aemail/apply`],
  ]);
  expect(requests[1]?.url.search).toBe("?collection=users");
  for (const r of requests) expect(r.headers.authorization).toBe(`Bearer ${token}`);
});
test.each([400, 401, 403, 404, 409, 421, 503])("index writes preserve %i and are never retried", async status => {
  const { client, requests } = await fixture((_r, response) => json(response, { error: { code: "index_refused", message: "Refused" } }, status));
  await expect(client.database("app").indexes.create({ collection: "users", fields: ["email"] })).rejects.toMatchObject({ status, code: "index_refused" });
  expect(requests).toHaveLength(1);
});
test("invalid secondary index requests fail before dispatch", async () => {
  const { client, requests } = await fixture((_r, response) => json(response, index));
  for (const fields of [[], ["_id"], ["email", "email"], ["a", "b", "c", "d", "e"]])
    await expect(client.database("app").indexes.create({ collection: "users", fields })).rejects.toThrow(TypeError);
  await expect(client.database("app").indexes.drop("../bad")).rejects.toThrow(TypeError);
  expect(requests).toHaveLength(0);
});
