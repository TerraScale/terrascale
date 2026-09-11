import assert from "node:assert/strict";
import { describe, test } from "vitest";

import { TerraScaleHttp } from "./http.js";
import {
  TenantContractGapError,
  TenantsClient,
  type Tenant,
} from "./tenants.js";

const tenant: Tenant = {
  key: "acme",
  databaseId: "db-acme",
  template: "default",
  placementLock: "eu-only",
};

describe("TenantsClient", () => {
  test("fails closed for tenant routes absent from the producer contract", async () => {
    const requests: Request[] = [];
    const http = new TerraScaleHttp({
      apiBaseUrl: "https://api.terrascale.test",
      idempotencyKeyFactory: () => "tenant-operation-1",
      fetch: async (input) => {
        requests.push(input instanceof Request ? input : new Request(input));
        const request = requests.at(-1)!;
        const body = request.url.endsWith("/keys")
          ? { keyId: "key-1", token: "secret", tenantKey: "acme" }
          : request.url.endsWith("/branches")
            ? { branchId: "branch-1", tenantKey: "acme", status: "ready" }
              : request.method === "GET" && new URL(request.url).pathname.endsWith("/tenants")
              ? { tenants: [tenant] }
              : tenant;
        return Response.json(body);
      },
    });
    const client = new TenantsClient(http);
    assert.throws(() => client.create({ key: "acme", template: "default", placementLock: ["FRA"] }), TenantContractGapError);
    assert.throws(() => client.get("acme"), TenantContractGapError);
    await assert.rejects(() => client.list({ limit: 10 }), TenantContractGapError);
    assert.throws(() => client.delete("acme", { confirm: true }), TenantContractGapError);
    assert.throws(
      () => client.createKey("acme"),
      (error: unknown) => error instanceof TenantContractGapError && error.operation === "tenants.keys.create",
    );
    assert.throws(
      () => client.createBranch("acme", { reason: "reproduce incident" }),
      (error: unknown) => error instanceof TenantContractGapError && error.operation === "tenants.branches.create",
    );
    assert.equal(requests.length, 0);
  });

  test("fails closed before local input validation for unsupported routes", () => {
    let fetches = 0;
    const client = new TenantsClient(new TerraScaleHttp({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async () => { fetches++; return Response.json(tenant); },
    }));
    assert.throws(() => client.create(""), TenantContractGapError);
    assert.throws(() => client.get("  "), TenantContractGapError);
    assert.throws(() => client.delete("acme", {} as never), TenantContractGapError);
    assert.equal(fetches, 0);
  });
});
