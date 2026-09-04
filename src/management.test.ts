import { describe, expect, it } from "vitest";
import {
  createTerraScaleManagementClient,
  ManagementContractGapError,
  validatePlacementLock,
} from "./management.js";

function fixtureFetch(requests: Request[]): typeof fetch {
  return async (input) => {
    const request = new Request(input);
    requests.push(request);
    return Response.json({}, { status: request.method === "POST" ? 201 : 200 });
  };
}

describe("management entrypoint", () => {
  it("uses only published project-scoped routes and preserves encoded identifiers", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleManagementClient({
      apiBaseUrl: "https://api.example.test",
      projectId: "project/one",
      fetch: fixtureFetch(requests),
    });

    await client.databases.create({ name: "primary" });
    await client.databases.get("database/one");
    await client.databases.delete("database/one", { confirm: true });
    await client.branches.getCapability();
    await client.usage.getProject();

    expect(new URL(requests[0]!.url).pathname).toBe("/api/v1/projects/project%2Fone/databases");
    expect(await requests[0]!.json()).toEqual({ name: "primary" });
    expect(new URL(requests[1]!.url).pathname).toBe("/api/v1/projects/project%2Fone/databases/database%2Fone");
    expect(new URL(requests[2]!.url).pathname).toBe("/api/v1/projects/project%2Fone/databases/database%2Fone");
    expect(requests[2]!.headers.get("x-terrascale-delete-confirmation")).toBe("database/one");
    expect(new URL(requests[3]!.url).pathname).toBe("/api/v1/projects/project%2Fone/database-branches/capability");
    expect(new URL(requests[4]!.url).pathname).toBe("/api/v1/projects/project%2Fone/usage");
  });

  it("fails closed for missing scope, invalid input, and unconfirmed deletion", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleManagementClient({
      apiBaseUrl: "https://api.example.test",
      fetch: fixtureFetch(requests),
    });

    expect(() => client.databases.list()).toThrow("projectId");
    expect(() => client.databases.create({ name: "" }, "project")).toThrow("name");
    expect(() => client.databases.create({ name: "db", placement: ["GRU"] }, "project")).toThrow(ManagementContractGapError);
    expect(() => client.databases.create({ name: "db", placement: ["GRU"], residency: "eu-only" }, "project")).toThrow("cannot be widened");
    expect(() => client.databases.list("project", { take: 201 })).toThrow("take");
    expect(() => client.databases.delete("db", { confirm: false as never })).toThrow("confirm");
    expect(requests).toHaveLength(0);
  });

  it("normalizes and bounds residency locks", () => {
    expect(validatePlacementLock(["GRU", "FRA"])).toEqual(["GRU", "FRA"]);
    expect(validatePlacementLock("eu-only")).toBe("eu-only");
    expect(() => validatePlacementLock([])).toThrow("regions must contain between 1 and 20");
    expect(() => validatePlacementLock("unknown" as never)).toThrow("placement");
  });

  it("does not issue requests for operations awaiting a published contract", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleManagementClient({
      apiBaseUrl: "https://api.example.test",
      projectId: "project",
      fetch: fixtureFetch(requests),
    });

    expect(() => client.schemas.get("db", "users")).toThrow(ManagementContractGapError);
    expect(() => client.collections.list("db")).toThrow(ManagementContractGapError);
    expect(() => client.exports.create("db")).toThrow(ManagementContractGapError);
    expect(requests).toHaveLength(0);
  });
});
