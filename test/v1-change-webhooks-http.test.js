import { inspect } from "node:util";
import { expect, test } from "vitest";
import { V1ChangeWebhooks } from "../src/index.js";
import { managementV1Routes } from "../src/v1/management-routes.js";
import { entity, fixture, json, prefix, token } from "./helpers/v1-http.js";
const base = `${prefix}/databases/app/collections/items/webhooks`;
const at = "2026-10-23T00:00:00Z";
const hook = { id: entity, url: "https://receiver.example/events", createdAt: at, status: "active" };
const secret = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
const delivery = { id: entity, createdAt: at, status: "pending", attempts: 1, lastAttemptAt: null,
  nextAttemptAt: at, httpStatus: 503, errorCode: "http_error" };

test("collection exposes create/list/delete/delivery APIs with exact paths and direct arrays", async () => {
  const replies = [{ ...hook, secret }, [{ ...hook, status: "feed_gap" }], [delivery,
    { ...delivery, status: "delivered", lastAttemptAt: at, nextAttemptAt: null, httpStatus: null, errorCode: null }]];
  const { client, requests } = await fixture((request, response) => {
    if (request.method === "DELETE") { response.writeHead(204).end(); return; }
    json(response, replies[requests.length - 1] ?? [], request.method === "POST" ? 201 : 200);
  });
  const hooks = client.database("app").collection("items").webhooks;
  expect(hooks).toBeInstanceOf(V1ChangeWebhooks);
  const created = await hooks.create({ url: hook.url });
  expect(created.secret).toBe(secret);
  expect(JSON.stringify(created)).toBe(JSON.stringify(hook));
  expect(inspect(created)).not.toContain(secret);
  expect(Object.isFrozen(created)).toBe(true);
  expect(await hooks.list({ after: entity, limit: 2 })).toEqual(replies[1]);
  expect(await hooks.deliveries(entity, { after: entity, limit: 100 })).toEqual(replies[2]);
  expect(await hooks.delete(entity)).toBeUndefined();
  expect(await hooks.list()).toEqual([]);
  expect(requests.map(r => [r.method, r.url.pathname, r.url.search])).toEqual([
    ["POST", base, ""], ["GET", base, `?after=${entity}&limit=2`],
    ["GET", `${base}/${entity}/deliveries`, `?after=${entity}&limit=100`],
    ["DELETE", `${base}/${entity}`, ""], ["GET", base, ""],
  ]);
  expect(requests[0]?.body).toBe(JSON.stringify({ url: hook.url }));
  for (const request of requests) {
    expect(request.headers.authorization).toBe(`Bearer ${token}`);
    expect(request.headers["idempotency-key"]).toBeUndefined();
  }
});

test("route manifest requires read authority for creation and deletion as well as write", () => {
  const routes = managementV1Routes.filter(r => r.pathTemplate.endsWith("/webhooks") || r.pathTemplate.includes("/webhooks/"));
  expect(routes).toHaveLength(4);
  for (const route of routes) {
    expect([route.permission, ...(route.additionalPermissions ?? [])].sort()).toEqual(route.method === "GET"
      ? ["documents:read"] : ["documents:read", "documents:write"]);
    expect(route.contract).toBe("docs/terrabase/change-webhooks.md");
  }
});

test.each([`wh_${entity}`, entity.toLowerCase(), `8${entity.slice(1)}`, `${entity.slice(0, -1)}I`])("noncanonical IDs fail before transport: %s", async id => {
  const { client, requests } = await fixture((_request, response) => json(response, []));
  const hooks = client.database("app").collection("items").webhooks;
  await expect(hooks.delete(id)).rejects.toThrow(/ULID/u);
  await expect(hooks.deliveries(id)).rejects.toThrow(/ULID/u);
  await expect(hooks.list({ after: id })).rejects.toThrow(/ULID/u);
  await expect(hooks.deliveries(entity, { after: id })).rejects.toThrow(/ULID/u);
  expect(requests).toHaveLength(0);
});

test("invalid URLs, unknown fields and out-of-range limits fail before transport", async () => {
  const { client, requests } = await fixture((_request, response) => json(response, []));
  const hooks = client.database("app").collection("items").webhooks;
  for (const url of ["http://receiver.example", "https://user:pass@receiver.example", "https://receiver.example/#fragment", "https://receiver.example:444", "a".repeat(2049)])
    await expect(hooks.create({ url })).rejects.toThrow(TypeError);
  for (const limit of [0, 101, 1.5]) {
    await expect(hooks.list({ limit })).rejects.toThrow(/limit/u);
    await expect(hooks.deliveries(entity, { limit })).rejects.toThrow(/limit/u);
  }
  await expect(hooks.create(/** @type {any} */ ({ url: hook.url, secret }))).rejects.toThrow(/Unknown/u);
  await expect(hooks.create({ url: hook.url }, /** @type {any} */ ({ idempotencyKey: entity }))).rejects.toThrow(/Idempotency-Key/u);
  expect(requests).toHaveLength(0);
});

test.each([400, 401, 403, 404, 409, 503])("HTTP %i errors preserve existing typed errors without replay", async status => {
  const { client, requests } = await fixture((_request, response) => json(response, { error: { code: "webhook_refused", message: "Refused" } }, status));
  await expect(client.database("app").collection("items").webhooks.create({ url: hook.url })).rejects.toMatchObject({ status, code: "webhook_refused" });
  expect(requests).toHaveLength(1);
});

test("malformed response IDs are rejected and extra fields cannot expose secrets in listings", async () => {
  const { client } = await fixture((_request, response) => json(response, [{ ...hook, secret, receiverBody: "private" }]));
  const listed = await client.database("app").collection("items").webhooks.list();
  expect(listed).toEqual([hook]);
  expect(JSON.stringify(listed)).not.toContain(secret);
  const invalid = await fixture((_request, response) => json(response, [{ ...hook, id: `wh_${entity}` }]));
  await expect(invalid.client.database("app").collection("items").webhooks.list()).rejects.toThrow(/ULID/u);
});

test("cancellation reaches the shared transport", async () => {
  const { client, requests } = await fixture((_request, response) => json(response, []));
  const cancellation = new AbortController();
  cancellation.abort();
  await expect(client.database("app").collection("items").webhooks.list({}, { signal: cancellation.signal })).rejects.toMatchObject({ name: "AbortError" });
  expect(requests).toHaveLength(0);
});
