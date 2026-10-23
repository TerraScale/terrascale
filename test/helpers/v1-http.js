import { createServer } from "node:http";
import { afterEach } from "vitest";
import { createTerraBaseClient } from "../../src/index.js";
export const project = "01ARZ3NDEKTSV4RRFFQ69G5FA1";
export const entity = "01ARZ3NDEKTSV4RRFFQ69G5FA2";
export const token = "fixture.payload.signature";
export const prefix = `/v1/projects/${project}`;
/** @typedef {{ method: string | undefined; url: URL; headers: import("node:http").IncomingHttpHeaders; body: string; bytes: Uint8Array }} WireRequest */
/** @type {import("node:http").Server[]} */
const servers = [];
afterEach(async () => {
  for (const server of servers.splice(0)) {
    server.closeAllConnections();
    await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve(undefined)));
  }
});
/** @param {(request: WireRequest, response: import("node:http").ServerResponse) => void} handle */
export async function fixture(handle) {
  /** @type {WireRequest[]} */
  const requests = [];
  const server = createServer((request, response) => {
    void (async () => {
      /** @type {Buffer[]} */
      const chunks = [];
      for await (const chunk of request) chunks.push(Buffer.from(chunk));
      const bytes = Buffer.concat(chunks);
      const body = bytes.toString("utf8");
      const record = { method: request.method, url: new URL(request.url ?? "/", "http://localhost"), headers: request.headers, body, bytes };
      requests.push(record);
      handle(record, response);
    })().catch(error => response.destroy(error));
  });
  servers.push(server);
  await new Promise((resolve, reject) => { server.once("error", reject); server.listen(0, "127.0.0.1", () => resolve(undefined)); });
  const address = server.address();
  if (address === null || typeof address === "string") throw new Error("Expected loopback address.");
  const baseUrl = `http://127.0.0.1:${address.port}`;
  return { client: createTerraBaseClient({ project, token, baseUrl }), requests, baseUrl };
}
/** @param {import("node:http").ServerResponse} response @param {unknown} value @param {number} [status] */
export function json(response, value, status = 200) {
  response.writeHead(status, { "content-type": "application/json" }).end(JSON.stringify(value));
}
