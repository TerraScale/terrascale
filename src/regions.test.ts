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

  test("normalizes canonical request region codes only", () => {
    assert.deepEqual(
      normalizeRegionCodes(["GRU", "fra", " sfo ", "gru", "sfo"]),
      ["gru", "fra", "sfo"],
    );
  });

  test("rejects retired region aliases", () => {
    assert.equal(canonicalRegionCode("ashburn"), "ashburn");
    assert.equal(findRegion("ashburn"), undefined);
    assert.equal(findRegion("ord"), undefined);
  });

  test("treats only exact public codes as TerraScaleRegionCode values", () => {
    assert.equal(isRegionCode("sfo"), true);
    assert.equal(isRegionCode("SFO"), false);
    assert.equal(isRegionCode("ashburn"), false);
  });

  test("keeps future and malformed codes for API validation", () => {
    assert.deepEqual(normalizeRegionCodes(["iad"]), ["iad"]);
    assert.deepEqual(normalizeRegionCodes(["", "not a region"]), ["not a region"]);
    assert.deepEqual(normalizeRegionCodes(["", "  "]), ["gru"]);
  });
});
