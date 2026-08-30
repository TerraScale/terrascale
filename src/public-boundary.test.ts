import assert from "node:assert/strict";
import { test } from "node:test";

import { createTerraScaleAdminClient } from "./admin.js";
import * as sdk from "./index.js";

test("built package keeps all public clients on typed operations without a runtime HTTP escape hatch", async () => {
  const requests: URL[] = [];
  const client = sdk.createTerraScaleClient({
    apiBaseUrl: "https://api.terrascale.test",
    fetch: async (input) => {
      requests.push(new URL(input instanceof Request ? input.url : input.toString()));
      return Response.json({ regions: [] });
    },
  });
  const admin = createTerraScaleAdminClient({
    apiBaseUrl: "https://api.terrascale.test",
    fetch: async () => Response.json({}),
  });

  assert.equal("TerraScaleHttp" in sdk, false);
  assert.equal("TerraScaleRequestOptions" in sdk, false);
  assert.equal("request" in client, false);
  assert.equal(typeof client.regions.list, "function");
  assert.equal(typeof client.savedQueries.list, "function");
  assert.equal(typeof client.savedQueries.update, "function");
  assert.equal(typeof client.databaseBranches.getCapability, "function");
  assert.equal(typeof client.databaseBranches.requestClone, "function");
  assert.equal(typeof client.queues.list, "function");
  assert.equal(typeof client.queues.create, "function");
  assert.equal(Object.values(client).length, 16);
  for (const removedClient of ["auth", "apiKeys", "m2m", "billing", "pricing", "security"]) {
    assert.equal(removedClient in client, false);
  }
  for (const removedOnboardingMethod of [
    "updateAccountInformation",
    "createPaymentSession",
    "validatePaymentMethod",
    "registerOnboardingPaymentMethod",
    "updateMfaDecision",
  ]) {
    assert.equal(removedOnboardingMethod in client.onboarding, false);
  }
  const savedQueries: sdk.SavedQueriesClient = client.savedQueries;
  const databaseBranches: sdk.DatabaseBranchesClient = client.databaseBranches;
  const queues: sdk.QueuesClient = client.queues;
  const savedQuery: sdk.ProjectSavedQueryResponse | undefined = undefined;
  assert.equal(savedQueries, client.savedQueries);
  assert.equal(databaseBranches, client.databaseBranches);
  assert.equal(queues, client.queues);
  assert.equal(savedQuery, undefined);

  for (const subclient of Object.values(client)) {
    assertRuntimePrivateTransport(subclient);
  }
  assertRuntimePrivateTransport(admin);

  const result = await client.regions.list();
  assert.equal(result.ok, true);
  assert.equal(requests[0]?.pathname, "/api/v1/regions");

  const internalHttpEntrypoint = "@terrascale/sdk/http";
  await assert.rejects(import(internalHttpEntrypoint), { code: "ERR_PACKAGE_PATH_NOT_EXPORTED" });
});

test("root clients reject SQLite, private service origins, and arbitrary default headers", () => {
  const fetch = async (): Promise<Response> => Response.json({});

  assert.throws(
    () => sdk.createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      fetch,
      sqlite: { enabled: true },
    } as never),
    /only available through the @terrascale\/sdk\/local entrypoint/u,
  );
  assert.throws(
    () => sdk.createTerraScaleClient({
      apiBaseUrl: "http://terrascale.local",
      fetch,
      sqlite: { enabled: true, databasePath: "/tmp/forged-local.sqlite" },
    } as never),
    /only available through the @terrascale\/sdk\/local entrypoint/u,
  );
  assert.throws(
    () => sdk.createTerraScaleClient({ apiBaseUrl: "https://terrabase.internal", fetch }),
    /only the public API, not TBEP or TerraBase/u,
  );
  assert.throws(
    () => sdk.createTerraScaleClient({ apiBaseUrl: "https://api.example.test/tbep", fetch }),
    /only the public API, not TBEP or TerraBase/u,
  );
  assert.throws(
    () => sdk.createTerraScaleClient({
      apiBaseUrl: "https://api.terrascale.test",
      fetch,
      defaultHeaders: { authorization: "Bearer leaked" },
    } as never),
    /defaultHeaders is not supported/u,
  );
});

test("named credentials are sent only to named public operations", async () => {
  const requests: Request[] = [];
  const client = sdk.createTerraScaleClient({
    apiBaseUrl: "https://api.terrascale.test",
    accessToken: "project-token",
    fetch: async input => {
      requests.push(input instanceof Request ? input : new Request(input));
      return Response.json({ regions: [] });
    },
  });

  const result = await client.regions.list();
  assert.equal(result.ok, true);
  assert.equal(requests[0]?.headers.get("authorization"), "Bearer project-token");
  assert.equal(requests[0]?.url.includes("project-token"), false);
  const exposedConfig = client.config as unknown as Record<string, unknown>;
  for (const secretOrTransport of ["accessToken", "fetch", "defaultHeaders"]) {
    assert.equal(secretOrTransport in exposedConfig, false);
  }
});

function assertRuntimePrivateTransport(client: object): void {
  const candidate = client as { readonly http?: { readonly request?: unknown } };

  assert.equal("http" in client, false);
  assert.equal(Object.hasOwn(client, "http"), false);
  assert.equal(Object.getOwnPropertyNames(client).includes("http"), false);
  assert.equal(Object.getOwnPropertyNames(Object.getPrototypeOf(client)).includes("http"), false);
  assert.equal(candidate.http?.request, undefined);
  assert.equal(candidate.http, undefined);
}
