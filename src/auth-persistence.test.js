import { expect, test } from "vitest";
import { terraScaleReleaseBlockers } from "./management-contracts.js";
test("planned production auth persistence retains its security and transaction obligations", () => {
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
