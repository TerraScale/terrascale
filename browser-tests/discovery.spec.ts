import { expect, test } from "@playwright/test";

test("packed browser direct endpoint validation and pre-abort dispatch boundary", async ({
  page,
}) => {
  await page.goto("/browser-tests/fixture.html");
  const observation = await page.evaluate(async () => {
    const sdk = await import("/candidate/dist/index.js");
    const scope = {
      tenant: "00000000-0000-0000-0000-000000000001",
      database: "00000000-0000-0000-0000-000000000002",
      shard: 1n,
    };
    const rejected = [];
    for (const endpoint of [
      "https://api.terrascale.tech",
      "https://api.terrascale.tech.",
      "http://remote.example.test",
      "https://gru.terrascale.tech",
      "https://gru.api.terrascale.tech/path",
    ]) {
      try {
        sdk.createTerraBaseClient({ ...scope, endpoint, apiKey: "unused" });
      } catch {
        rejected.push(endpoint);
      }
    }
    let credentials = 0;
    let dispatches = 0;
    const client = sdk.createTerraBaseClient({
      ...scope,
      endpoint: "https://gru.api.terrascale.tech",
      apiKey: () => {
        credentials += 1;
        return "unused";
      },
      fetch: async () => {
        dispatches += 1;
        throw new Error("Unexpected dispatch.");
      },
    });
    const controller = new AbortController();
    controller.abort();
    const result = await client.read(
      "00000000-0000-0000-0000-000000000003",
      "00000000-0000-0000-0000-000000000004",
      { signal: controller.signal },
    );
    return {
      rejected: rejected.length,
      credentials,
      dispatches,
      ok: result.ok,
      code: result.ok ? undefined : result.error.code,
    };
  });
  expect(observation).toEqual({
    rejected: 5,
    credentials: 0,
    dispatches: 0,
    ok: false,
    code: "cancelled",
  });
});

test("packed browser default Atlas routing uses the authenticated receiver and reports transport failure", async ({
  page,
}) => {
  await page.goto("/browser-tests/fixture.html");
  const observation = await page.evaluate(async () => {
    const sdk = await import("/candidate/dist/index.js");
    let requests = 0;
    let origin;
    let authenticated = false;
    const apiKey = `tbk1.${"A".repeat(22)}.${"A".repeat(43)}`;
    const client = sdk.createTerraBaseClient({
      tenant: "00000000-0000-0000-0000-000000000001",
      database: "00000000-0000-0000-0000-000000000002",
      shard: 1n,
      apiKey,
      fetch: async (input) => {
        requests += 1;
        if (!(input instanceof Request))
          throw new Error("Expected an SDK-owned Request.");
        origin = new URL(input.url).origin;
        authenticated =
          input.headers.get("authorization") === `Bearer ${apiKey}` &&
          input.credentials === "omit" &&
          input.redirect === "manual";
        throw new Error(
          "No actual Atlas allocation is configured for this fault-injection check.",
        );
      },
    });
    const result = await client.read(
      "00000000-0000-0000-0000-000000000003",
      "00000000-0000-0000-0000-000000000004",
    );
    return {
      requests,
      origin,
      authenticated,
      receiver: sdk.terraBaseDiscoveryOrigin,
      ok: result.ok,
      code: result.ok ? undefined : result.error.code,
    };
  });
  expect(observation).toEqual({
    requests: 1,
    origin: "https://atlas.terrascale.tech",
    authenticated: true,
    receiver: "https://atlas.terrascale.tech",
    ok: false,
    code: "discovery_unavailable",
  });
});
