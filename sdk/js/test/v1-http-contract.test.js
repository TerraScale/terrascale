import { createServer } from "node:http";
import { afterEach, describe, expect, test } from "vitest";
import { createTerraBaseV1Client } from "../src/index.js";

// These loopback fixtures verify SDK wire/decoder behavior, not producer qualification.
const project = "01ARZ3NDEKTSV4RRFFQ69G5FA1";
const entityId = "01ARZ3NDEKTSV4RRFFQ69G5FA2";
const token = "fixture.payload.signature";
const version = "opaque-etag";
const prefix = `/v1/projects/${project}/databases/app`;
/** @typedef {{ method: string | undefined; url: URL; headers: import("node:http").IncomingHttpHeaders; body: string }} WireRequest */
/** @type {import("node:http").Server[]} */
const servers = [];

afterEach(async () => {
  for (const server of servers.splice(0)) {
    server.closeAllConnections();
    await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve(undefined)));
  }
});

/**
 * @param {(request: WireRequest, response: import("node:http").ServerResponse) => void} handle
 */
async function fixture(handle) {
  /** @type {WireRequest[]} */
  const requests = [];
  const server = createServer((request, response) => {
    void (async () => {
      let body = "";
      for await (const chunk of request) body += chunk.toString();
      const record = { method: request.method, url: new URL(request.url ?? "/", "http://localhost"), headers: request.headers, body };
      requests.push(record);
      handle(record, response);
    })().catch(error => response.destroy(error));
  });
  servers.push(server);
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => resolve(undefined));
  });
  const address = server.address();
  if (address === null || typeof address === "string") throw new Error("Expected loopback address.");
  const baseUrl = `http://127.0.0.1:${address.port}`;
  const client = createTerraBaseV1Client({ project, token, baseUrl });
  return { requests, baseUrl, client, db: client.database("app") };
}

/** @param {import("node:http").ServerResponse} response @param {unknown} value @param {number} [status] */
function json(response, value, status = 200) {
  response.writeHead(status, { "content-type": "application/json" }).end(JSON.stringify(value));
}
/** @param {string} id @param {object} [document] */
const document = (id, document = { value: 1 }) => ({ id, version, state: "value", document });
/** @param {string} id */
const write = id => ({ id, version, offset: "1", durability: "local_durable" });

describe("current document wire contracts", () => {
  test("round-trips a document ULID exactly once through every point method", async () => {
      const key = entityId;
      const { db, requests } = await fixture((request, response) => {
        const id = request.url.searchParams.get("id") ?? "";
        json(response, request.method === "GET" ? document(id, { id: "unrelated-data" }) : write(id));
      });
      const docs = db.collection("users");
      expect((await docs.get(key)).id).toBe(key);
      expect((await docs.put(key, { id: "unrelated-data" })).id).toBe(key);
      await docs.patch(key, { merge: { id: "unrelated-data" } });
      await docs.delete(key);
      expect(requests.map(request => request.method)).toEqual(["GET", "PUT", "PATCH", "DELETE"]);
      for (const request of requests) {
        expect(request.url.pathname).toBe(`${prefix}/collections/users/documents/by-key`);
        expect([...request.url.searchParams]).toEqual([["id", key]]);
        expect(request.headers.authorization).toBe(`Bearer ${token}`);
        expect(request.headers.accept).toBe("application/json");
        expect(request.headers["idempotency-key"]).toBeUndefined();
      }
      expect(JSON.parse(requests[1]?.body ?? "")).toEqual({ id: "unrelated-data" });
      expect(requests[2]?.headers["content-type"]).toBe("application/merge-patch+json");
  });
});

describe("pagination termination and cancellation", () => {
  test("follows opaque cursors, sends count once, and stops at null", async () => {
    const { db, requests } = await fixture((_request, response) => json(response,
      requests.length === 1 ? { items: [document(entityId)], next_cursor: "next+cursor", count: { value: "1", exact: true } }
        : { items: [], next_cursor: null }));
    const pages = [];
    for await (const page of db.collection("users").pages({ count: true, limit: 1 })) pages.push(page);
    expect(pages).toHaveLength(2);
    expect(requests.map(request => JSON.parse(request.body))).toEqual([{ count: true, limit: 1 }, { limit: 1, cursor: "next+cursor" }]);
  });
});

describe("native fetch failures and typed diagnostics", () => {
  test("redirects never forward credentials or retry the request", async () => {
    const { db, requests } = await fixture((_request, response) => response.writeHead(307, { location: "/redirect-target" }).end());
    await expect(db.collection("users").put(entityId, {})).rejects.toMatchObject({ status: 307, code: "http_307" });
    expect(requests).toHaveLength(1);
  });
});

