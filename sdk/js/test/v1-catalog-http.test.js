import { createServer } from "node:http";
import { afterEach, describe, expect, test } from "vitest";
import { V1Databases } from "../src/v1/catalog.js";
import { V1Transport } from "../src/v1/transport.js";

const project = "01ARZ3NDEKTSV4RRFFQ69G5FA1";
const id = "01ARZ3NDEKTSV4RRFFQ69G5FA2";
const token = "fixture.payload.signature";
const prefix = `/v1/projects/${project}/databases`;
const database = { id, projectId: project, name: "app", branchedFrom: null,
  createdAt: "2026-10-13T12:00:00+00:00", region: "region-123", homeRegion: "region-123", replicaRegions: ["other-region"], allRegions: false };
/** @typedef {{ method: string | undefined; url: URL; headers: import("node:http").IncomingHttpHeaders; body: string }} WireRequest */
/** @type {import("node:http").Server[]} */
const servers = [];

afterEach(async () => {
  for (const server of servers.splice(0)) {
    server.closeAllConnections();
    await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve(undefined)));
  }
});

/** @param {(request: WireRequest, response: import("node:http").ServerResponse) => void} handle */
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
  const transport = new V1Transport({ project, token, baseUrl: `http://127.0.0.1:${address.port}` });
  return { transport, databases: new V1Databases(transport), requests };
}

/** @param {import("node:http").ServerResponse} response @param {unknown} value @param {number} [status] */
function json(response, value, status = 200) {
  response.writeHead(status, { "content-type": "application/json" }).end(JSON.stringify(value));
}

describe("public database catalog HTTP contracts", () => {
  test("uses the six named routes, exact camelCase fields and an empty 204", async () => {
    const { databases, requests } = await fixture((request, response) => {
      if (request.method === "DELETE") { response.writeHead(204).end(); return; }
      json(response, request.method === "GET" && request.url.pathname === prefix ? { items: [database], next: null } : database,
        request.method === "POST" ? 201 : 200);
    });
    expect(await databases.create({ name: "app" })).toEqual(database);
    expect(await databases.list({ after: "opaque+cursor", limit: 100 })).toEqual({ items: [database], next: null });
    expect(await databases.get("app")).toEqual(database);
    expect(await databases.update("app", { replicaRegions: ["other-region"] })).toEqual(database);
    expect(await databases.branch("app", { name: "branch" })).toEqual(database);
    expect(await databases.delete("app")).toBeUndefined();
    expect(requests.map(request => [request.method, request.url.pathname])).toEqual([
      ["POST", prefix], ["GET", prefix], ["GET", `${prefix}/app`], ["PATCH", `${prefix}/app`],
      ["POST", `${prefix}/app/branches`], ["DELETE", `${prefix}/app`],
    ]);
    expect(requests.map(request => request.body)).toEqual(['{"name":"app"}', "", "", '{"replicaRegions":["other-region"]}', '{"name":"branch"}', ""]);
    expect([...(requests[1]?.url.searchParams ?? [])]).toEqual([["after", "opaque+cursor"], ["limit", "100"]]);
    for (const request of requests) {
      expect(request.headers.authorization).toBe(`Bearer ${token}`);
      expect(request.headers.accept).toBe(request.method === "DELETE" ? "*/*" : "application/json");
      expect(request.headers["idempotency-key"]).toBeUndefined();
      if (request.body !== "") expect(request.headers["content-type"]).toBe("application/json");
    }
  });

  // HTTP fixtures verify the SDK contract, not server-side rename invariants.
});

// Conflict metadata must survive catalog list/get and clear on a successful rename.

