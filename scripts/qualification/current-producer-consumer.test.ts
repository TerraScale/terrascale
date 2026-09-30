import { describe, expect, test, vi } from "vitest";
import { qualificationConfiguration } from "./current-producer-consumer.mjs";

describe("real packaged consumer admission", () => {
  test("missing real allocation and artifact cannot become a green skipped journey", () => {
    vi.stubEnv("TERRASCALE_SDK_TARBALL", "");
    expect(() => qualificationConfiguration()).toThrow(/incomplete/u);
    vi.unstubAllEnvs();
  });
});
