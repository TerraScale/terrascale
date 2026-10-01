import { describe, expect, test } from "vitest";
import { journeyConfiguration } from "./journey-configuration.js";
import { createTerraBaseClient } from "../../src/index.js";
import { createTsAuthClient } from "../../src/ts-auth.js";

/**
 * @typedef {Record<
 *   | "TERRASCALE_SDK_TARBALL"
 *   | "TERRASCALE_SDK_TARBALL_SHA256"
 *   | "TERRABASE_TENANT"
 *   | "TERRABASE_DATABASE"
 *   | "TERRABASE_COLLECTION"
 *   | "TERRABASE_SHARD"
 *   | "TERRABASE_SCHEMA_GENERATION"
 *   | "TERRABASE_SCHEMA_DIGEST"
 *   | "TERRABASE_API_KEY"
 *   | "TERRABASE_DENY_API_KEY"
 *   | "TERRABASE_TEST_FIELD_ID"
 *   | "TERRABASE_TEST_FIELD_TYPE"
 *   | "TERRABASE_BROWSER_DOCUMENT"
 *   | "TERRABASE_EXPECTED_REGION"
 *   | "TERRASCALE_QUALIFICATION_ALLOCATION_ID",
 *   string
 * >} Allocation
 */

/** @returns {Allocation} */
function allocation() {
  return {
    TERRASCALE_SDK_TARBALL: "/tmp/qualified-sdk.tgz",
    TERRASCALE_SDK_TARBALL_SHA256: "aa".repeat(32),
    TERRABASE_TENANT: "10000000-0000-0000-0000-000000000001",
    TERRABASE_DATABASE: "20000000-0000-0000-0000-000000000001",
    TERRABASE_COLLECTION: "30000000-0000-0000-0000-000000000001",
    TERRABASE_SHARD: "18446744073709551615",
    TERRABASE_SCHEMA_GENERATION: "1",
    TERRABASE_SCHEMA_DIGEST: "cc".repeat(32),
    TERRABASE_API_KEY: "allowed-scoped-credential",
    TERRABASE_DENY_API_KEY: "denied-scoped-credential",
    TERRABASE_TEST_FIELD_ID: "1",
    TERRABASE_TEST_FIELD_TYPE: "utf8",
    TERRABASE_BROWSER_DOCUMENT: "40000000-0000-0000-0000-000000000001",
    TERRABASE_EXPECTED_REGION: "GRU",
    TERRASCALE_QUALIFICATION_ALLOCATION_ID: "isolated-real-allocation",
  };
}

describe("pure Node/browser journey configuration admission", () => {
  test.each(["native", "browser"])("%s defaults to Atlas and enables a complete allocation without an endpoint", target => {
    const configuration = journeyConfiguration(allocation(), target);
    expect(configuration).toMatchObject({ routing: "atlas", nativeOrigin: "https://gru.api.terrascale.tech" });
    expect(configuration).not.toHaveProperty("endpoint");
  });

  test.each(["native", "browser"])("%s skips only completely absent configuration", target => {
    expect(journeyConfiguration({}, target)).toBeUndefined();
    expect(() => journeyConfiguration({ TERRABASE_TENANT: allocation().TERRABASE_TENANT }, target)).toThrow(/incomplete/u);
  });

  test.each(["native", "browser"])("%s requires an explicit direct mode for a provisioned endpoint", target => {
    const environment = { ...allocation(), TERRABASE_ENDPOINT: "http://127.0.0.1:18447" };
    expect(() => journeyConfiguration(environment, target)).toThrow(/omit/u);
    expect(journeyConfiguration({ ...environment, TERRABASE_ROUTING_MODE: "direct" }, target))
      .toMatchObject({ routing: "direct", endpoint: environment.TERRABASE_ENDPOINT, nativeOrigin: environment.TERRABASE_ENDPOINT });
  });

  test.each(["native", "browser"])("%s cannot bypass absent Atlas authority inputs", target => {
    for (const name of ["TERRABASE_EXPECTED_REGION", "TERRASCALE_QUALIFICATION_ALLOCATION_ID"]) {
      expect(() => journeyConfiguration({ ...allocation(), [name]: "" }, target)).toThrow(/incomplete/u);
    }
    expect(() => journeyConfiguration({ ...allocation(), TERRABASE_ROUTING_MODE: "fallback" }, target)).toThrow(/mode/u);
  });

  test.each([
    "API", "ATLAS", "AUTH", "SYNC", "STATUS", "WWW", "APP", "DASH", "DOCS", "GIT", "CDN", "ADMIN", "MCP", "MAIL",
    "GRU.API", "gru", "GRU:443", "-GRU", "ABCDEFGHIJKLMNOPQ",
  ])("rejects nonregional Atlas expectation %s", region => {
    expect(() => journeyConfiguration({ ...allocation(), TERRABASE_EXPECTED_REGION: region }, "browser")).toThrow(/canonical/u);
  });

  test.each([
    ["GRU", "https://gru.api.terrascale.tech"],
    ["AMS", "https://ams.api.terrascale.tech"],
    ["ABCDEFGHIJKLMNOP", "https://abcdefghijklmnop.api.terrascale.tech"],
    ["9-A", "https://9-a.api.terrascale.tech"],
  ])("preserves canonical Atlas expectation %s", (region, origin) => {
    for (const target of ["native", "browser"]) {
      expect(journeyConfiguration({ ...allocation(), TERRABASE_EXPECTED_REGION: region }, target))
        .toMatchObject({ routing: "atlas", nativeOrigin: origin });
    }
  });

  test.each([
    "https://api.terrascale.tech",
    "https://gru.terrascale.tech",
    "http://remote.example.test",
    "https://gru.api.terrascale.tech/path",
    "https://credential@gru.api.terrascale.tech",
    "https://auth.terrascale.tech", "https://atlas.terrascale.tech", "https://sync.terrascale.tech",
    "https://status.terrascale.tech", "https://www.terrascale.tech", "https://app.terrascale.tech",
    "https://dash.terrascale.tech", "https://docs.terrascale.tech", "https://git.terrascale.tech",
    "https://cdn.terrascale.tech", "https://admin.terrascale.tech", "https://mcp.terrascale.tech", "https://mail.terrascale.tech",
    "https://terrascale.tech", "https://gru.api.terrascale.tech.", "https://GRU.api.terrascale.tech",
    "https://nested.gru.terrascale.tech", "https://gru.api.terrascale.tech:8443", "https://gru.api.terrascale.tech:443",
    "https://-gru.api.terrascale.tech", "https://abcdefghijklmnopq.api.terrascale.tech", "https://%67ru.api.terrascale.tech",
    "https://K.terrascale.tech", "http://gru.api.terrascale.tech", "https://GRU.API.TerraScale.Tech",
    "https://%6eative.example.test", "https://K.example.test", "https://xn--foo.api.terrascale.tech", "https://ams-.api.terrascale.tech",
    "https://native.example.test.", "https://native.example.test..:8443", "http://localhost.:9911",
  ])("rejects unadmitted direct origin %s before qualification or SDK credential acquisition", endpoint => {
    for (const target of ["native", "browser"]) {
      expect(() => journeyConfiguration({ ...allocation(), TERRABASE_ROUTING_MODE: "direct", TERRABASE_ENDPOINT: endpoint }, target)).toThrow(/Direct qualification/u);
    }
    let credentials = 0, requests = 0;
    expect(() => createTerraBaseClient({
      tenant: allocation().TERRABASE_TENANT, database: allocation().TERRABASE_DATABASE, shard: 18446744073709551615n,
      endpoint, apiKey: () => { credentials++; return "key"; }, fetch: async () => { requests++; throw new Error("Unexpected native request."); },
    })).toThrow();
    expect(credentials).toBe(0);
    expect(requests).toBe(0);
  });

  test.each([
    ["https://gru.api.terrascale.tech", "https://gru.api.terrascale.tech"],
    ["https://fra.api.terrascale.tech/", "https://fra.api.terrascale.tech"],
    ["https://ams.api.terrascale.tech", "https://ams.api.terrascale.tech"],
    ["https://abcdefghijklmnop.api.terrascale.tech", "https://abcdefghijklmnop.api.terrascale.tech"],
    ["https://9-a.api.terrascale.tech", "https://9-a.api.terrascale.tech"],
    ["https://native.example.test:8443", "https://native.example.test:8443"],
    ["https://CUSTOM.example.test", "https://custom.example.test"],
    ["http://localhost:9911", "http://localhost:9911"],
    ["http://127.0.0.1:9911", "http://127.0.0.1:9911"],
    ["http://[::1]:9911", "http://[::1]:9911"],
    ["http://native.localhost:9911", "http://native.localhost:9911"],
  ])("admits direct origin %s consistently for both journey targets and the public SDK", async (endpoint, origin) => {
    for (const target of ["native", "browser"]) {
      expect(journeyConfiguration({ ...allocation(), TERRABASE_ROUTING_MODE: "direct", TERRABASE_ENDPOINT: endpoint }, target))
        .toMatchObject({ routing: "direct", endpoint: origin, nativeOrigin: origin });
    }
    /** @type {Request[]} */
    const requests = [];
    const client = createTerraBaseClient({
      tenant: allocation().TERRABASE_TENANT, database: allocation().TERRABASE_DATABASE, shard: 18446744073709551615n,
      endpoint, apiKey: "key", fetch: async input => {
        requests.push(/** @type {Request} */ (input));
        return new Response(JSON.stringify({ version: 1, state: "absent", state_version: null, context: [], siblings: [] }), { headers: { "content-type": "application/json" } });
      },
    });
    expect((await client.read(allocation().TERRABASE_COLLECTION, allocation().TERRABASE_BROWSER_DOCUMENT)).ok).toBe(true);
    expect(requests.map(request => new URL(request.url).origin)).toEqual([origin]);
  });

  test("browser document and independent denial credential remain mandatory", () => {
    expect(() => journeyConfiguration({ ...allocation(), TERRABASE_BROWSER_DOCUMENT: "" }, "browser")).toThrow(/incomplete/u);
    expect(() => journeyConfiguration({ ...allocation(), TERRABASE_DENY_API_KEY: allocation().TERRABASE_API_KEY }, "browser")).toThrow(/differ/u);
  });
});

describe("native and hosted origin admission remain distinct", () => {
  test.each([
    "https://api.terrascale.tech", "https://gru.api.terrascale.tech",
    "https://GRU.API.TerraScale.Tech", "https://gru.api.terrascale.tech.",
  ])("hosted identity rejects the database API origin %s before dispatch", apiBaseUrl => {
    let requests = 0;
    expect(() => createTsAuthClient({
      apiBaseUrl, instanceSlug: "test", accessToken: "hosted-key",
      fetch: async () => { requests++; throw new Error("Unexpected hosted request."); },
    })).toThrow(/Database API host/u);
    expect(requests).toBe(0);
  });

  test("a native-reserved Auth origin remains eligible only for the hosted client", async () => {
    /** @type {Request[]} */
    const requests = [];
    const client = createTsAuthClient({
      apiBaseUrl: "https://auth.terrascale.tech", instanceSlug: "test", accessToken: "hosted-key",
      fetch: async input => {
        const request = /** @type {Request} */ (input); requests.push(request);
        const body = request.url === "https://atlas.terrascale.tech/api/v1/auth/discovery"
          ? { v: 1, refreshSeconds: 60, endpoints: [{ region: "GRU", url: "https://gru.auth.terrascale.tech" }] }
          : request.url === "https://gru.auth.terrascale.tech/v1/test/users" ? { data: [] } : undefined;
        if (body === undefined) throw new Error("Unexpected hosted request.");
        return new Response(JSON.stringify(body), { headers: { "content-type": "application/json" } });
      },
    });
    expect(await client.users.list()).toMatchObject({ ok: true, value: { data: [] } });
    expect(requests.map(request => request.url)).toEqual([
      "https://atlas.terrascale.tech/api/v1/auth/discovery", "https://gru.auth.terrascale.tech/v1/test/users",
    ]);
    expect(requests[0]?.headers.get("authorization")).toBeNull();
    expect(requests[1]?.headers.get("authorization")).toBe("Bearer hosted-key");
    expect(() => createTerraBaseClient({
      tenant: allocation().TERRABASE_TENANT, database: allocation().TERRABASE_DATABASE, shard: 18446744073709551615n,
      endpoint: "https://auth.terrascale.tech", apiKey: "native-key",
    })).toThrow(/canonical regional hostname/u);
  });
});
