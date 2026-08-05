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
      return Response.json({ items: [] });
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
  assert.equal(Object.values(client).length, 21);
  const savedQueries: sdk.SavedQueriesClient = client.savedQueries;
  const savedQuery: sdk.ProjectSavedQueryResponse | undefined = undefined;
  assert.equal(savedQueries, client.savedQueries);
  assert.equal(savedQuery, undefined);

  for (const subclient of Object.values(client)) {
    assertRuntimePrivateTransport(subclient);
  }
  assertRuntimePrivateTransport(admin);

  const result = await client.regions.list();
  assert.equal(result.ok, true);
  assert.equal(requests[0]?.pathname, "/api/v1/regions");

  // @ts-expect-error Generic arbitrary requests are intentionally not public SDK API.
  void client.request;
  // @ts-expect-error The raw HTTP helper is intentionally not exported by the root package.
  void sdk.TerraScaleHttp;

  const internalHttpEntrypoint = "@terrascale/sdk/http";
  await assert.rejects(import(internalHttpEntrypoint), { code: "ERR_PACKAGE_PATH_NOT_EXPORTED" });
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
