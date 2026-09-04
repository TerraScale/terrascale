import assert from "node:assert/strict";
import { describe, test } from "vitest";

import { TerraScaleHttp } from "./http.js";
import {
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
  test("uses the named tenant CRUD, key, and support-branch routes", async () => {
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

    const created = await client.create({
      key: "acme",
      template: "default",
      placementLock: ["FRA"],
    });
    const fetched = await client.get("acme");
    const listed = await client.list({ limit: 10 });
    const deleted = await client.delete("acme", { confirm: true });
    const key = await client.createKey("acme");
    const branch = await client.createBranch("acme", { reason: "reproduce incident" });

    assert.equal(created.ok, true);
    assert.equal(fetched.ok, true);
    assert.equal(listed.ok, true);
    if (listed.ok) assert.deepEqual(listed.value, [tenant]);
    assert.equal(deleted.ok, true);
    assert.equal(key.ok, true);
    assert.equal(branch.ok, true);

    assert.deepEqual(
      requests.map((request) => [request.method, new URL(request.url).pathname]),
      [
        ["POST", "/api/v1/tenants"],
        ["GET", "/api/v1/tenants/acme"],
        ["GET", "/api/v1/tenants"],
        ["DELETE", "/api/v1/tenants/acme"],
        ["POST", "/api/v1/tenants/acme/keys"],
        ["POST", "/api/v1/tenants/acme/branches"],
      ],
    );
    assert.equal(requests[0]?.headers.get("idempotency-key"), "tenant-operation-1");
    assert.equal(requests[3]?.headers.get("x-terrascale-delete-confirmation"), "acme");
    assert.equal(requests[5]?.headers.get("idempotency-key"), "tenant-operation-1");
    assert.deepEqual(JSON.parse(await requests[0]!.clone().text()), {
      key: "acme",
      template: "default",
      placementLock: ["FRA"],
    });
  });

  test("supports the documented key shorthand and validates placement locks", async () => {
    let body: unknown;
    const client = new TenantsClient(new TerraScaleHttp({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async input => {
        body = JSON.parse(await (input instanceof Request ? input : new Request(input)).text());
        return Response.json(tenant);
      },
    }));

    await client.create("acme", { template: "default", placementLock: "br-only" });
    assert.deepEqual(body, { key: "acme", template: "default", placementLock: "br-only" });
    assert.throws(
      () => client.create({ key: "acme", placementLock: [] }),
      /regions must contain/u,
    );
    assert.throws(() => client.get("  "), /tenant key cannot be blank/u);
    assert.throws(() => client.delete("acme", {} as never), /confirm/u);
  });

  test("returns a typed cancellation result without sending an aborted request", async () => {
    let fetches = 0;
    const controller = new AbortController();
    controller.abort();
    const client = new TenantsClient(new TerraScaleHttp({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async () => {
        fetches++;
        return Response.json(tenant);
      },
    }));

    const result = await client.get("acme", { signal: controller.signal });
    assert.equal(result.ok, false);
    if (!result.ok) assert.equal(result.error.code, "request_cancelled");
    assert.equal(fetches, 0);
  });

  test("cancels an in-flight request without waiting for its response", async () => {
    let fetchStarted = false;
    const controller = new AbortController();
    const client = new TenantsClient(new TerraScaleHttp({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async () => {
        fetchStarted = true;
        await new Promise<never>(() => undefined);
        throw new Error("unreachable");
      },
    }));

    const pending = client.get("acme", { signal: controller.signal });
    await new Promise<void>(resolve => queueMicrotask(resolve));
    assert.equal(fetchStarted, true);
    controller.abort();
    const result = await pending;

    assert.equal(result.ok, false);
    if (!result.ok) assert.equal(result.error.code, "request_cancelled");
  });

  test("preserves a transport failure when no caller abort occurred", async () => {
    const client = new TenantsClient(new TerraScaleHttp({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async () => { throw new TypeError("socket closed"); },
    }));

    const result = await client.get("acme", { signal: new AbortController().signal });
    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.equal(result.error.kind, "transport");
      assert.equal(result.error.code, "transport_error");
      assert.match(result.error.message, /socket closed/u);
    }
  });

  test("does not turn a transport AbortError into cancellation unless the caller aborted", async () => {
    const client = new TenantsClient(new TerraScaleHttp({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async () => { throw new DOMException("origin timeout", "AbortError"); },
    }));

    const result = await client.get("acme", { signal: new AbortController().signal });
    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.equal(result.error.kind, "transport");
      assert.equal(result.error.code, "transport_error");
      assert.notEqual(result.error.code, "request_cancelled");
    }
  });

  test("keeps nested typed service errors intact", async () => {
    const client = new TenantsClient(new TerraScaleHttp({
      apiBaseUrl: "https://api.terrascale.test",
      fetch: async () => Response.json({
        error: { code: "tenant_denied", message: "tenant is not accessible", details: { region: "FRA" } },
      }, { status: 403 }),
    }));

    const result = await client.get("acme", { signal: new AbortController().signal });
    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.equal(result.error.kind, "authorization");
      assert.equal(result.error.code, "tenant_denied");
      assert.deepEqual(result.error.details, {
        error: { code: "tenant_denied", message: "tenant is not accessible", details: { region: "FRA" } },
      });
    }
  });
});
