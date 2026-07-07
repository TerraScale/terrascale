import assert from "node:assert/strict";
import { describe, test } from "node:test";

import {
  canonicalRegionCode,
  findRegion,
  isRegionCode,
  normalizeRegionCodes,
  terraScaleRegions,
} from "./regions.js";

describe("region catalog", () => {
  test("exposes the public three-region contract", () => {
    assert.deepEqual(
      terraScaleRegions.map((region) => region.code),
      ["gru", "sfo", "fra"],
    );
  });

  test("normalizes canonical and legacy request region codes", () => {
    assert.deepEqual(
      normalizeRegionCodes(["GRU", "fra", " ORD ", "gru", "ASHBURN", "sfo"]),
      ["gru", "fra", "sfo"],
    );
  });

  test("keeps legacy codes addressable as aliases", () => {
    assert.equal(canonicalRegionCode("ashburn"), "sfo");
    assert.equal(findRegion("ord")?.code, "fra");
  });

  test("treats only exact public codes as TerraScaleRegionCode values", () => {
    assert.equal(isRegionCode("sfo"), true);
    assert.equal(isRegionCode("SFO"), false);
    assert.equal(isRegionCode("ashburn"), false);
  });

  test("defaults empty or invalid region selections to GRU", () => {
    assert.deepEqual(normalizeRegionCodes(["", "iad"]), ["gru"]);
  });
});
