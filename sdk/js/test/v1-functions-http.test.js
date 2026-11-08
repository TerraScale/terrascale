import { expect, test } from "vitest";
import { entity, fixture, json, prefix } from "./helpers/v1-http.js";
const base = `${prefix}/databases/app`;
const at = "2026-10-22T10:00:00Z";
const source = "export default (event) => event.body";
const deployed = { name: "ingest", source, cron: null, createdAt: at, updatedAt: at };
const summary = { name: "ingest", source: null, cron: null, createdAt: at, updatedAt: at };
const log = { id: entity, at, trigger: "http", status: "ok", durationMs: 3, lines: ["done"], error: null };

test("the six function operations follow the producer routes and contracts", async () => {
  const { client, requests } = await fixture((request, response) => {
    const path = request.url?.pathname ?? "";
    if (request.method === "DELETE") { response.writeHead(204).end(); return; }
    if (path.endsWith("/invoke")) { response.writeHead(201, { "content-type": "text/plain; charset=utf-8" }).end("42"); return; }
    if (path.endsWith("/logs")) { json(response, [log]); return; }
    if (request.method === "GET" && path.endsWith("/functions")) { json(response, [summary]); return; }
    json(response, deployed);
  });
  const functions = client.database("app").functions;
  expect(await functions.deploy("ingest", { source, cron: "* * * * *" })).toEqual(deployed);
  expect(await functions.list()).toEqual([summary]);
  expect(await functions.get("ingest")).toEqual(deployed);
  const invocation = await functions.invoke("ingest", { body: JSON.stringify({ n: 1 }), query: { verbose: "true" } });
  expect(invocation.status).toBe(201);
  expect(invocation.contentType).toBe("text/plain; charset=utf-8");
  expect(new TextDecoder().decode(invocation.body)).toBe("42");
  expect(await functions.logs("ingest", 2)).toEqual([log]);
  expect(await functions.delete("ingest")).toBeUndefined();
  expect(requests.map(r => [r.method, r.url.pathname])).toEqual([
    ["PUT", `${base}/functions/ingest`],
    ["GET", `${base}/functions`],
    ["GET", `${base}/functions/ingest`],
    ["POST", `${base}/functions/ingest/invoke`],
    ["GET", `${base}/functions/ingest/logs`],
    ["DELETE", `${base}/functions/ingest`],
  ]);
  expect(requests[0]?.body).toBe(JSON.stringify({ source, cron: "* * * * *" }));
  expect(requests[3]?.body).toBe(JSON.stringify({ n: 1 }));
  expect(requests[3]?.headers["content-type"]).toBe("application/json");
  expect(requests[4]?.url.searchParams.get("limit")).toBe("2");
  expect(JSON.stringify(client)).toBe("{}");
});
