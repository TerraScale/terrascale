import { afterEach, describe, expect, test, vi } from "vitest";
import { qualificationConfiguration } from "./current-producer-consumer.js";

/**
 * @param {"direct" | "atlas"} [mode]
 * @returns {void}
 */
function allocation(mode = "atlas") {
  /** @type {Record<string, string>} */
  const values = {
    TERRASCALE_SDK_TARBALL: "/tmp/qualified-sdk.tgz", TERRASCALE_SDK_TARBALL_SHA256: "aa".repeat(32),
    TERRABASE_ROUTING_MODE: mode, TERRABASE_ENDPOINT: mode === "direct" ? "https://gru.api.terrascale.tech" : "",
    TERRABASE_TENANT: "0G000000000000000000000001", TERRABASE_DATABASE: "10000000000000000000000001",
    TERRABASE_COLLECTION: "1G000000000000000000000001", TERRABASE_SHARD: "18446744073709551615",
    TERRABASE_SCHEMA_GENERATION: "1", TERRABASE_SCHEMA_DIGEST: "cc".repeat(32), TERRABASE_API_KEY: "allowed-scoped-credential",
    TERRABASE_DENY_API_KEY: "denied-scoped-credential", TERRABASE_TEST_FIELD_ID: "1", TERRABASE_TEST_FIELD_TYPE: "utf8",
    TERRABASE_EXPECTED_REGION: "GRU", TERRASCALE_QUALIFICATION_ALLOCATION_ID: "isolated-real-allocation",
  };
  for (const [name, value] of Object.entries(values)) vi.stubEnv(name, value);
}

afterEach(() => vi.unstubAllEnvs());

describe("real packaged consumer admission", () => {
  test("missing real allocation and artifact cannot become a green skipped journey", () => {
    vi.stubEnv("TERRASCALE_SDK_TARBALL", "");
    expect(() => qualificationConfiguration()).toThrow(/incomplete/u);
  });

  test("Atlas mode requires an actual allocation and expected native region without an endpoint bypass", () => {
    allocation();
    expect(qualificationConfiguration()).toMatchObject({ routing: "atlas" });
    vi.stubEnv("TERRABASE_ENDPOINT", "https://gru.api.terrascale.tech");
    expect(() => qualificationConfiguration()).toThrow(/omit/u);
    vi.stubEnv("TERRABASE_ENDPOINT", "");
    vi.stubEnv("TERRASCALE_QUALIFICATION_ALLOCATION_ID", "");
    expect(() => qualificationConfiguration()).toThrow(/incomplete/u);
  });

  test.each(["API", "ATLAS", "GRU.API", "gru", "GRU:443"])("rejects an unadmitted expected origin label %s", code => {
    allocation(); vi.stubEnv("TERRABASE_EXPECTED_REGION", code);
    expect(() => qualificationConfiguration()).toThrow(/canonical/u);
  });

  test("the real installed journey defaults to Atlas without an endpoint", () => {
    allocation();
    vi.stubEnv("TERRABASE_ROUTING_MODE", undefined);
    vi.stubEnv("TERRABASE_ENDPOINT", undefined);
    expect(qualificationConfiguration()).toMatchObject({ routing: "atlas" });
  });

  test("a provisioned endpoint cannot silently select direct mode", () => {
    allocation("direct");
    vi.stubEnv("TERRABASE_ROUTING_MODE", undefined);
    expect(() => qualificationConfiguration()).toThrow(/omit/u);
  });

  test("direct producer qualification remains explicit and rejects noncanonical regional hosts", () => {
    allocation("direct");
    expect(qualificationConfiguration()).toMatchObject({ routing: "direct" });
    vi.stubEnv("TERRABASE_ENDPOINT", "https://gru.api.terrascale.tech.");
    expect(() => qualificationConfiguration()).toThrow(/forbidden/u);
    vi.stubEnv("TERRABASE_ENDPOINT", "");
    expect(() => qualificationConfiguration()).toThrow(/incomplete/u);
  });

  test("neither an unknown mode nor a reused denial credential is admitted", () => {
    allocation(); vi.stubEnv("TERRABASE_ROUTING_MODE", "fallback");
    expect(() => qualificationConfiguration()).toThrow(/mode/u);
    vi.stubEnv("TERRABASE_ROUTING_MODE", "atlas"); vi.stubEnv("TERRABASE_DENY_API_KEY", "allowed-scoped-credential");
    expect(() => qualificationConfiguration()).toThrow(/differ/u);
  });
});
