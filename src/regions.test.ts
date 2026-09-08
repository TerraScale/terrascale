import assert from "node:assert/strict";
import { describe, test } from "vitest";

import {
  canonicalRegionCode,
  findRegion,
  isRegionCode,
  normalizeRegionCodes,
  parseRegionCode,
  terraScaleRegionCodes,
  terraScaleRegions,
} from "./regions.js";

describe("RegionCode contract", () => {
  test("exposes launch codes while accepting dynamic catalog codes", () => {
    assert.deepEqual(terraScaleRegionCodes, ["GRU", "SFO", "FRA"]);
    assert.deepEqual(
      terraScaleRegions.map((region) => region.code),
      ["GRU", "SFO", "FRA"],
    );
    for (const region of terraScaleRegions) {
      assert.equal(Object.hasOwn(region, "ociRegionCode"), false);
      assert.equal(Object.hasOwn(region, "clusterNodeTag"), false);
    }
  });

  test("accepts each registered code without changing it", () => {
    for (const code of terraScaleRegionCodes) {
      assert.equal(canonicalRegionCode(code), code);
      assert.equal(parseRegionCode(code), code);
      assert.equal(isRegionCode(code), true);
      assert.equal(findRegion(code)?.code, code);
    }
    assert.equal(parseRegionCode("IAD"), "IAD");
    assert.deepEqual(normalizeRegionCodes(["IAD", "GRU"]), ["IAD", "GRU"]);
  });

  test("rejects lowercase, aliases, whitespace, wrong lengths, non-ASCII, and unregistered codes", () => {
    const invalidCodes = [
      "gru",
      "sfo",
      "fra",
      "Gru",
      "GRu",
      " GRU",
      "GRU ",
      "GRU\n",
      "",
      "-G",
      "京",
      "GÉ",
      "ABCDEFGHIJKLMNOPQ",
      "AB/CD",
      "ABC_1",
      "ABC.",
    ];

    for (const code of invalidCodes) {
      assert.equal(canonicalRegionCode(code), undefined, code);
      assert.equal(isRegionCode(code), false, code);
      assert.equal(findRegion(code), undefined, code);
      assert.throws(() => parseRegionCode(code), /uppercase Region code/u, code);
    }
    assert.equal(canonicalRegionCode(undefined), undefined);
  });

  test("deduplicates an exact selection in first-seen order", () => {
    assert.deepEqual(
      normalizeRegionCodes(["GRU", "FRA", "SFO", "GRU", "SFO"]),
      ["GRU", "FRA", "SFO"],
    );
  });

  test("rejects empty, oversized, or invalid selections instead of defaulting or normalizing", () => {
    assert.throws(() => normalizeRegionCodes(undefined), /between 1 and 20/u);
    assert.throws(() => normalizeRegionCodes([]), /between 1 and 20/u);
    assert.throws(
      () => normalizeRegionCodes(Array.from({ length: 21 }, () => "GRU")),
      /between 1 and 20/u,
    );
    for (const selection of [["gru"], [" GRU "], ["京"], ["GRU", "ABC_"]]) {
      assert.throws(() => normalizeRegionCodes(selection), /uppercase Region code/u);
    }
  });
});
