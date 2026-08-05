import assert from "node:assert/strict";
import { test } from "node:test";

import * as sdk from "./index.js";

test("package consumers get typed clients but no generic HTTP escape hatch", async () => {
  const client = sdk.createTerraScaleClient({
    apiBaseUrl: "https://api.terrascale.test",
    fetch: async () => Response.json({}),
  });

  assert.equal("TerraScaleHttp" in sdk, false);
  assert.equal("TerraScaleRequestOptions" in sdk, false);
  assert.equal("request" in client, false);
  assert.equal(typeof client.regions.list, "function");

  // @ts-expect-error Generic arbitrary requests are intentionally not public SDK API.
  void client.request;
  // @ts-expect-error The raw HTTP helper is intentionally not exported by the root package.
  void sdk.TerraScaleHttp;

  const internalHttpEntrypoint = "@terrascale/sdk/http";
  await assert.rejects(import(internalHttpEntrypoint), { code: "ERR_PACKAGE_PATH_NOT_EXPORTED" });
});
