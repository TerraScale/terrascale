import { expect, test } from "vitest";
import {
  requireTerraScaleBetterAuth,
  terraScaleReleaseBlockers,
} from "./better-auth.js";
import { TerraScaleCapabilityUnavailableError } from "./management-contracts.js";
test("production auth persistence fails closed and retains every security/transaction obligation", () => {
  expect(requireTerraScaleBetterAuth).toThrow(
    TerraScaleCapabilityUnavailableError,
  );
  expect(terraScaleReleaseBlockers.betterAuth.required).toEqual(
    expect.arrayContaining([
      "unique insert",
      "compare-and-swap",
      "one-time consume",
      "bounded transactions",
      "indexes/migrations",
      "revocation/denial",
      "region concurrency",
    ]),
  );
});
