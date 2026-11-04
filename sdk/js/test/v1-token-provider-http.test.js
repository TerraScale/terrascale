import { describe, expect, test, vi } from "vitest";
import { createTerraBaseClient } from "../src/index.js";
import { fixture, json, project } from "./helpers/v1-http.js";

const first = "first.payload.signature";
const second = "second.payload.signature";
const document = { id: "01ARZ3NDEKTSV4RRFFQ69G5FA2", version: "opaque", state: "value", document: { value: 1 } };

describe("per-request bearer token providers over HTTP", () => {
  test.each([false, true])("replaces tokens for reads, writes and queries (async: %s)", async asynchronous => {
    const { baseUrl, requests } = await fixture((request, response) => {
      if (request.url.pathname.endsWith("/healthz"))
        json(response, { status: "ok", version: "0.0.1", region: "fixture", storageFormat: "1", revision: null, machine: "fixture" });
      else if (request.url.pathname.endsWith("/query")) json(response, { items: [document], next_cursor: null });
      else if (request.method === "GET") json(response, document);
      else json(response, { id: "01ARZ3NDEKTSV4RRFFQ69G5FA2", version: "opaque", offset: "1", durability: "local_durable" });
    });
    let current = first;
    const provider = vi.fn(() => asynchronous ? Promise.resolve(current) : current);
    const client = createTerraBaseClient({ project, baseUrl, token: provider });
    expect(provider).not.toHaveBeenCalled();
    const docs = client.database("app").collection("users");
    await docs.get("01ARZ3NDEKTSV4RRFFQ69G5FA2");
    current = second;
    await docs.put("01ARZ3NDEKTSV4RRFFQ69G5FA2", { value: 2 });
    await docs.query();
    await client.health.liveness();
    expect(requests.map(request => request.headers.authorization)).toEqual([
      `Bearer ${first}`, `Bearer ${second}`, `Bearer ${second}`, undefined,
    ]);
    expect(provider).toHaveBeenCalledTimes(3);
    expect(JSON.stringify(client)).not.toContain("payload");
  });
});
