import { describe, test } from "node:test";
import assert from "node:assert/strict";

import { createTerraScaleClient } from "../factory.js";

const apiBaseUrl = process.env.TERRASCALE_API_BASE_URL;
const authBaseUrl = process.env.TERRASCALE_AUTH_API_BASE_URL;

describe("DevHost smoke", { skip: apiBaseUrl === undefined || authBaseUrl === undefined }, () => {
  test("reaches the real auth HTTP boundary", async () => {
    const client = createTerraScaleClient({
      apiBaseUrl: apiBaseUrl!,
      authBaseUrl: authBaseUrl!,
      dashboardClientId: "terrascale-sdk-integration",
    });

    const result = await client.auth.getSession();

    assert.equal(result.ok, false);
    assert.equal(result.routeName, "auth.session.get");
    assert.equal(typeof result.status, "number");
  });
});
