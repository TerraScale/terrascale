import { expect, test } from "vitest";
import { createTerraBaseClient } from "../src/index.js";
import { fixture, json, project, token } from "./helpers/v1-http.js";
import { shapeVectors } from "./support/shape-vectors.js";
const vectors = new Map(shapeVectors.http_vectors.map((/** @type {any} */ v) => [v.id, v.response]));
const handle = "h1-f3171a2230109328";
const fresh = { status: 200, headers: { "content-type": "application/json", "tb-shape-handle": handle, "tb-shape-cursor": "50" }, body: {
  ...vectors.get("snapshot-last-page").body, offset: "2", next_offset: "2", covered_offset: "2", values_at: "2", up_to_date: true, changes: [],
} };
const initial = [vectors.get("bootstrap"), vectors.get("snapshot-first-page"), vectors.get("snapshot-last-page"), fresh];
/** @param {import("./helpers/v1-http.js").WireRequest} request @param {import("node:http").ServerResponse} response @param {any} page */
function reply(request, response, page) {
  const headers = new Headers(page.headers);
  const location = headers.get("location");
  if (location !== null) headers.set("location", request.url.pathname + new URL(location, request.url).search);
  response.writeHead(page.status, Object.fromEntries(headers)).end(page.body_utf8 ?? (page.body == null ? "" : JSON.stringify(page.body)));
}
const store = () => ({ text: /** @type {string | undefined} */ (undefined), load() { return this.text; }, save(/** @type {string | undefined} */ text) { this.text = text; } });
/** @param {import("../src/v1/terrabase-v1.js").TerraBaseV1Client} client */
const notes = client => client.database("demo").collection("notes");

test.each(["project", "origin"])("replicas never publish another %s's documents before authorization", async boundary => {
  let step = 0;
  const first = await fixture((req, res) => {
    if (step < initial.length) reply(req, res, initial[step++]);
    else json(res, { error: { code: "forbidden_scope" } }, 403);
  });
  const replica = store();
  await notes(first.client).subscribeShape({}, { replica, onUpdate: s => { if (s.upToDate) s.close(); } }).done;
  expect(replica.text).toBeDefined();
  const denied = boundary === "origin" ? await fixture((_req, res) => json(res, { error: { code: "forbidden_scope" } }, 403)) : first;
  const client = createTerraBaseClient({ baseUrl: denied.baseUrl, project: boundary === "project" ? "01ARZ3NDEKTSV4RRFFQ69G5FA2" : project, token });
  /** @type {number[]} */
  const published = [];
  const subscription = notes(client).subscribeShape({}, { replica, onUpdate: s => published.push(s.state.size) });
  await expect(subscription.done).rejects.toMatchObject({ status: 403 });
  expect(published.every(size => size === 0)).toBe(true);
  expect(subscription.state.size).toBe(0);
  expect(replica.text).toBeUndefined();
});

test.each([401, 403])("a terminal %i clears fresh documents and the persisted authorized view", async status => {
  let step = 0;
  const service = await fixture((req, res) => {
    if (step < initial.length) reply(req, res, initial[step++]);
    else json(res, { error: { code: status === 401 ? "invalid_key" : "forbidden_scope" } }, status);
  });
  const replica = store();
  /** @type {{ fresh: boolean; size: number }[]} */
  const updates = [];
  const subscription = notes(service.client).subscribeShape({}, { replica, onUpdate: s => updates.push({ fresh: s.upToDate, size: s.state.size }) });
  await expect(subscription.done).rejects.toMatchObject({ status });
  expect(updates.some(s => s.fresh && s.size > 0)).toBe(true);
  expect(updates.at(-1)).toEqual({ fresh: false, size: 0 });
  expect(subscription.state.size).toBe(0);
  expect(subscription.upToDate).toBe(false);
  expect(replica.text).toBeUndefined();
  expect(service.requests).toHaveLength(5);
});

