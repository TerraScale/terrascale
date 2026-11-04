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

test.each(["disconnect", "overload"])("%s publishes stale state before resuming the identical read", async failure => {
  let step = 0;
  const service = await fixture((req, res) => {
    if (step < initial.length) reply(req, res, initial[step++]);
    else if (step++ === initial.length) {
      if (failure === "disconnect") res.destroy();
      else json(res, { error: { code: "unavailable" } }, 503);
    } else reply(req, res, { ...fresh, headers: { ...fresh.headers, "tb-shape-cursor": "51" } });
  });
  /** @type {{ fresh: boolean; size: number }[]} */
  const updates = [];
  const subscription = notes(service.client).subscribeShape({}, { onUpdate: s => {
    updates.push({ fresh: s.upToDate, size: s.state.size });
    if (updates.filter(x => x.fresh).length === 2) s.close();
  } });
  await subscription.done;
  expect(updates.slice(-3).map(x => x.fresh)).toEqual([true, false, true]);
  expect(updates.slice(-3).every(x => x.size > 0)).toBe(true);
  expect(service.requests[4]?.url.search).toBe(service.requests[5]?.url.search);
});

test.each(["load", "save"])("close settles done while replica %s never settles", async operation => {
  let step = 0;
  const service = await fixture((req, res) => reply(req, res, initial[step++]));
  /** @type {() => void} */
  let started = () => {};
  const waiting = new Promise(resolve => { started = () => resolve(undefined); });
  const stalled = () => { started(); return new Promise(() => {}); };
  const replica = { load: operation === "load" ? stalled : () => undefined, save: operation === "save" ? stalled : () => {} };
  const subscription = notes(service.client).subscribeShape({}, { replica });
  await waiting;
  subscription.close();
  let timer;
  try {
    expect(await Promise.race([subscription.done.then(() => "closed"), new Promise(resolve => { timer = setTimeout(() => resolve("stuck"), 100); })])).toBe("closed");
  } finally { clearTimeout(timer); subscription.close(); }
});

test.each(["throw", "reject", "hang"])("replica clear %s cannot mask or postpone terminal denial", async failure => {
  let step = 0;
  const service = await fixture((req, res) => {
    if (step < initial.length) reply(req, res, initial[step++]);
    else json(res, { error: { code: "forbidden_scope" } }, 403);
  });
  const replica = { load: () => undefined, save: (/** @type {string | undefined} */ text) => {
    if (text !== undefined) return;
    if (failure === "throw") throw new Error("store clear failed");
    return failure === "reject" ? Promise.reject(new Error("store clear failed")) : new Promise(() => {});
  } };
  const subscription = notes(service.client).subscribeShape({}, { replica });
  let timer;
  try {
    const error = await Promise.race([subscription.done.catch(error => error), new Promise(resolve => { timer = setTimeout(() => resolve(undefined), 1000); })]);
    expect(error).toMatchObject({ status: 403, code: "forbidden_scope" });
    expect(subscription.state.size).toBe(0);
    expect(subscription.upToDate).toBe(false);
  } finally { clearTimeout(timer); subscription.close(); }
});
