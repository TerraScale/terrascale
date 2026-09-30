import { describe, expect, test } from "vitest";
import { journeyConfiguration } from "./journey-configuration.mjs";

function allocation(): Record<string, string> {
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
    expect(configuration).toMatchObject({ routing: "atlas", nativeOrigin: "https://gru.terrascale.tech" });
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

  test.each(["API", "ATLAS", "GRU.API", "gru", "GRU:443"])("rejects nonregional Atlas expectation %s", region => {
    expect(() => journeyConfiguration({ ...allocation(), TERRABASE_EXPECTED_REGION: region }, "browser")).toThrow(/canonical/u);
  });

  test.each([
    "https://api.terrascale.tech",
    "https://gru.api.terrascale.tech.",
    "http://remote.example.test",
    "https://gru.terrascale.tech/path",
    "https://credential@gru.terrascale.tech",
  ])("rejects unadmitted direct origin %s", endpoint => {
    expect(() => journeyConfiguration({ ...allocation(), TERRABASE_ROUTING_MODE: "direct", TERRABASE_ENDPOINT: endpoint }, "browser")).toThrow();
  });

  test("browser document and independent denial credential remain mandatory", () => {
    expect(() => journeyConfiguration({ ...allocation(), TERRABASE_BROWSER_DOCUMENT: "" }, "browser")).toThrow(/incomplete/u);
    expect(() => journeyConfiguration({ ...allocation(), TERRABASE_DENY_API_KEY: allocation().TERRABASE_API_KEY }, "browser")).toThrow(/differ/u);
  });
});
