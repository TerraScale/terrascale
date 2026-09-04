import { describe, expect, it } from "vitest";
import { createTerraScaleManagementClient } from "./management.js";

function fixtureFetch(requests: Request[]): typeof fetch {
  return async (input) => {
    const request = new Request(input);
    requests.push(request);
    const url = new URL(request.url);
    const body = request.method === "GET" || request.method === "DELETE" ? {} : { ok: true };
    if (url.pathname.endsWith("/collections")) {
      return Response.json({ collections: ["users"] });
    }
    return Response.json(body, { status: request.method === "POST" ? 201 : 200 });
  };
}

describe("management entrypoint", () => {
  it("uses named direct TerraBase routes and preserves encoded identifiers", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleManagementClient({
      apiBaseUrl: "https://api.example.test",
      fetch: fixtureFetch(requests),
      idempotencyKeyFactory: () => "stable-management-operation",
    });

    await client.databases.create({ name: "primary", placement: ["GRU", "SFO"] });
    await client.collections.list("database/one");
    await client.schemas.put("database/one", "user records", { fields: {} });
    await client.backups.restore("database/one", { name: "restore-target", at: 1_735_689_600_000 });

    expect(new URL(requests[0]!.url).pathname).toBe("/api/v1/databases");
    expect(await requests[0]!.json()).toEqual({ name: "primary", placement: ["GRU", "SFO"] });
    expect(requests[0]!.headers.get("idempotency-key")).toBe("stable-management-operation");
    expect(new URL(requests[1]!.url).pathname).toBe("/api/v1/databases/database%2Fone/collections");
    expect(new URL(requests[2]!.url).pathname).toBe(
      "/api/v1/databases/database%2Fone/collections/user%20records/schema",
    );
    expect(new URL(requests[3]!.url).pathname).toBe("/api/v1/databases/database%2Fone/backups/restore");
  });

  it("validates placement locks before a request can widen residency", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleManagementClient({
      apiBaseUrl: "https://api.example.test",
      fetch: fixtureFetch(requests),
    });

    expect(() => client.databases.create({ name: "bad", placement: [] })).toThrow(
      "regions must contain between 1 and 20",
    );
    expect(() =>
      client.databases.create({
        name: "bad",
        placement: ["GRU"],
        residency: "not-a-lock" as "eu-only",
      }),
    ).toThrow("residency");
    expect(() =>
      client.databases.create({ name: "bad", placement: ["GRU"], residency: "eu-only" }),
    ).toThrow("cannot be widened");
    expect(requests).toHaveLength(0);
  });

  it("requires project scope for API keys and sends stable mutation identities", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleManagementClient({
      apiBaseUrl: "https://api.example.test",
      fetch: fixtureFetch(requests),
      idempotencyKeyFactory: () => "api-key-operation",
    });

    expect(() => client.apiKeys.list()).toThrow("projectId must be nonblank");
    await client.apiKeys.create("project/one", { name: "server" });
    await client.apiKeys.rotate("project/one", "key/one");
    await client.apiKeys.revoke("project/one", "key/one");

    expect(new URL(requests[0]!.url).pathname).toBe("/api/v1/projects/project%2Fone/auth/api-keys");
    expect(new URL(requests[1]!.url).pathname).toBe(
      "/api/v1/projects/project%2Fone/auth/api-keys/key%2Fone/rotate",
    );
    expect(new URL(requests[2]!.url).pathname).toBe("/api/v1/projects/project%2Fone/auth/api-keys/key%2Fone");
    expect(requests[1]!.headers.get("idempotency-key")).toBe("api-key-operation");
    expect(requests[2]!.headers.get("idempotency-key")).toBe("api-key-operation");
  });
});
