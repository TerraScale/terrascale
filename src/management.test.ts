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
  it("fails closed for project-scoped routes absent from the producer contract", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleManagementClient({
      apiBaseUrl: "https://api.example.test",
      projectId: "project/one",
      fetch: fixtureFetch(requests),
    });

    for (const operation of [
      () => client.databases.create({ name: "primary" }),
      () => client.databases.get("database/one"),
      () => client.databases.delete("database/one", { confirm: true }),
      () => client.branches.getCapability(),
      () => client.usage.getProject(),
    ]) {
      expect(operation).toThrow(ManagementContractGapError);
    }
    expect(requests).toHaveLength(0);
  });

  it("fails closed before validating or sending unsupported project routes", async () => {
    const requests: Request[] = [];
    const client = createTerraScaleManagementClient({
      apiBaseUrl: "https://api.example.test",
      fetch: fixtureFetch(requests),
    });

    expect(() => client.databases.list()).toThrow(ManagementContractGapError);
    expect(() => client.databases.create({ name: "" }, "project")).toThrow(ManagementContractGapError);
    expect(() => client.databases.list("project", { take: 201 })).toThrow(ManagementContractGapError);
    expect(() => client.databases.delete("db", { confirm: false as never })).toThrow(ManagementContractGapError);
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
