import { createServer } from "node:http";
import { afterEach, describe, expect, test } from "vitest";
import { createTerraBaseV1Client } from "../src/index.js";

// These loopback fixtures verify SDK wire/decoder behavior, not producer qualification.
const project = "01ARZ3NDEKTSV4RRFFQ69G5FA1";
const token = "fixture.payload.signature";
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

describe("native fetch failures and typed diagnostics", () => {
  test("redirects never forward credentials or retry the request", async () => {
    const { db, requests } = await fixture((_request, response) => response.writeHead(307, { location: "/redirect-target" }).end());
    await expect(db.execute("INSERT INTO users (id) VALUES (1)")).rejects.toMatchObject({ status: 307, code: "http_307" });
    expect(requests).toHaveLength(1);
  });
});

