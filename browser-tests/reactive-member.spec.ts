import { expect, test } from "@playwright/test";
import { journeyConfiguration } from "../scripts/qualification/journey-configuration.mjs";

const configuration = journeyConfiguration(process.env, "browser");

test("installed browser Atlas/direct read/query/snapshot/poll and scope denial against an actual allocation", async ({
  page,
}) => {
  if (configuration === undefined) {
    test.skip(
      true,
      "requires a real isolated native allocation with browser CORS and credentials; this skip does not qualify release E2E",
    );
    return;
  }
  await page.goto("/browser-tests/fixture.html");
  const observation = await page.evaluate(async (configuration) => {
    const sdk = await import("/candidate/dist/index.js");
    const inputs = configuration.inputs;
    const scope = {
      tenant: inputs.TERRABASE_TENANT,
      database: inputs.TERRABASE_DATABASE,
      shard: BigInt(inputs.TERRABASE_SHARD),
    };
    const config = {
      ...scope,
      ...(configuration.routing === "direct"
        ? { endpoint: configuration.endpoint }
        : {}),
    };
    let atlasCalls = 0;
    let nativeCalls = 0;
    let deniedAtlasCalls = 0;
    let deniedNativeCalls = 0;
    let requiredCapabilities = ["client_v1", "read"];
    const tracedFetch = (apiKey: string, denied = false) => async (
      input: RequestInfo | URL,
      init?: RequestInit,
    ) => {
      if (!(input instanceof Request)) {
        throw new Error("Expected an SDK-owned request.");
      }
      if (
        input.headers.get("authorization") !== `Bearer ${apiKey}` ||
        input.credentials !== "omit" || input.redirect !== "manual"
      ) {
        throw new Error("Native browser request lost its scoped credential or transport policy.");
      }
      const url = new URL(input.url);
      if (
        url.origin === sdk.terraBaseDiscoveryOrigin &&
        configuration.routing === "atlas"
      ) {
        if (
          url.pathname !== "/api/v1/discovery" ||
          url.searchParams.get("tenant_id") !== scope.tenant ||
          url.searchParams.get("database_id") !== scope.database ||
          url.searchParams.get("shard_id") !== scope.shard.toString()
        ) {
          throw new Error("Atlas browser request belongs to another native scope.");
        }
        if (denied) deniedAtlasCalls += 1;
        else atlasCalls += 1;
      } else if (url.origin === configuration.nativeOrigin) {
        const documentPath =
          `/v1/tenants/${scope.tenant}/databases/${scope.database}` +
          `/shards/${scope.shard}/collections/${inputs.TERRABASE_COLLECTION}` +
          `/documents/${inputs.TERRABASE_BROWSER_DOCUMENT}`;
        if (
          !denied && nativeCalls === 0 &&
          (input.method !== "GET" || url.pathname !== documentPath)
        ) {
          throw new Error("The first regional operation must be the named document read.");
        }
        if (denied) deniedNativeCalls += 1;
        else nativeCalls += 1;
      } else {
        throw new Error("Browser journey dispatched outside the admitted Atlas/regional origins.");
      }
      const response = await fetch(input, init);
      if (url.origin === sdk.terraBaseDiscoveryOrigin && response.status === 200) {
        const routing = sdk.parseTerraBaseRoutingDocument(
          await response.clone().json(),
          scope,
        );
        const selected = [...routing.endpoints]
          .sort((a, b) => a.priority - b.priority)
          .find(endpoint => requiredCapabilities.every(name => endpoint.capabilities[name]));
        if (selected?.url !== configuration.nativeOrigin) {
          throw new Error("Atlas did not select the allocation's closest eligible native origin.");
        }
      }
      return response;
    };
    const client = sdk.createTerraBaseClient({
      ...config,
      apiKey: inputs.TERRABASE_API_KEY,
      fetch: tracedFetch(inputs.TERRABASE_API_KEY),
    });
    const deniedClient = sdk.createTerraBaseClient({
      ...config,
      apiKey: inputs.TERRABASE_DENY_API_KEY,
      fetch: tracedFetch(inputs.TERRABASE_DENY_API_KEY, true),
    });
    const constructionRequests =
      atlasCalls + nativeCalls + deniedAtlasCalls + deniedNativeCalls;
    if (constructionRequests !== 0) {
      throw new Error("Construction must not prove native readiness.");
    }
    const collection = inputs.TERRABASE_COLLECTION;
    const predicate = {
      op: "is_missing",
      path: [Number(inputs.TERRABASE_TEST_FIELD_ID)],
    };
    const read = await client.read(collection, inputs.TERRABASE_BROWSER_DOCUMENT);
    if (!read.ok) throw new Error(`Native browser read failed: ${read.error.code}`);
    requiredCapabilities = ["client_v1", "read", "query"];
    const query = await client.query(collection, { predicate, limit: 1 });
    if (!query.ok) throw new Error(`Native browser query failed: ${query.error.code}`);
    requiredCapabilities = ["client_v1", "read", "subscriptions"];
    let cursor;
    let rows = 0;
    for (let page = 0; page < 1024; page += 1) {
      const snapshot = await client.subscription(collection, {
        phase: "snapshot",
        predicate,
        limit: 128,
        ...(cursor === undefined ? {} : { snapshot_cursor: cursor }),
      });
      if (!snapshot.ok || snapshot.value.phase !== "snapshot") {
        throw new Error("Native browser snapshot failed.");
      }
      rows += snapshot.value.rows.length;
      if ("poll_cursor" in snapshot.value) {
        const poll = await client.subscription(collection, {
          phase: "poll",
          predicate,
          poll_cursor: snapshot.value.poll_cursor,
        });
        if (!poll.ok || poll.value.phase !== "poll") throw new Error("Native browser poll failed.");
        requiredCapabilities = ["client_v1", "read"];
        const denied = await deniedClient.read(collection, inputs.TERRABASE_BROWSER_DOCUMENT);
        return {
          state: read.value.state,
          rows,
          poll: true,
          denied: denied.ok ? 200 : denied.error.status,
          constructionRequests,
          atlasCalls,
          nativeCalls,
          deniedAtlasCalls,
          deniedNativeCalls,
        };
      }
      cursor = snapshot.value.next_snapshot_cursor;
    }
    throw new Error("Native browser snapshot exceeded bounded page count.");
  }, configuration);
  expect(["absent", "deleted", "value", "conflict"]).toContain(observation.state);
  expect(observation.poll).toBe(true);
  expect([401, 403]).toContain(observation.denied);
  expect(observation.constructionRequests).toBe(0);
  expect(observation.nativeCalls).toBeGreaterThanOrEqual(4);
  if (configuration.routing === "atlas") {
    expect(observation.atlasCalls).toBeGreaterThanOrEqual(1);
    expect(observation.deniedAtlasCalls).toBeGreaterThanOrEqual(1);
  } else {
    expect(observation.atlasCalls + observation.deniedAtlasCalls).toBe(0);
    expect(observation.deniedNativeCalls).toBe(1);
  }
});
