import { describe, expect, test, vi } from "vitest";
import { createTerraBaseClient } from "../src/index.js";
import { fixture, json, project } from "./helpers/v1-http.js";

const first = "first.payload.signature";
const second = "second.payload.signature";
const database = { id: "01ARZ3NDEKTSV4RRFFQ69G5FA2", projectId: project, name: "app", branchedFrom: null,
  createdAt: "2026-10-13T12:00:00+00:00", region: "gru", homeRegion: "gru" };
const inserted = { columns: [], rows: [], rows_affected: 1, last_insert_rowid: 1 };

describe("per-request bearer token providers over HTTP", () => {
  test.each([false, true])("replaces tokens for reads and SQL (async: %s)", async asynchronous => {
    const { baseUrl, requests } = await fixture((request, response) => {
      if (request.url.pathname.endsWith("/healthz"))
        json(response, { status: "ok", version: "0.0.1", region: "fixture", storageFormat: "1", revision: null, machine: "fixture" });
      else if (request.url.pathname.endsWith("/sql/execute")) json(response, { results: [inserted] });
      else json(response, database);
    });
    let current = first;
    const provider = vi.fn(() => asynchronous ? Promise.resolve(current) : current);
    const client = createTerraBaseClient({ project, baseUrl, token: provider });
    expect(provider).not.toHaveBeenCalled();
    await client.databases.get("app");
    current = second;
    await client.database("app").execute("INSERT INTO users (id) VALUES (1)");
    await client.database("app").execute("INSERT INTO users (id) VALUES (2)");
    await client.health.liveness();
    expect(requests.map(request => request.headers.authorization)).toEqual([
      `Bearer ${first}`, `Bearer ${second}`, `Bearer ${second}`, undefined,
    ]);
    expect(provider).toHaveBeenCalledTimes(3);
    expect(JSON.stringify(client)).not.toContain("payload");
  });
});
