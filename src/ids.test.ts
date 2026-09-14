import assert from "node:assert/strict";
import { describe, test } from "node:test";

import { isUlid, toUlid, tryUlid } from "./ids.js";

describe("ULID helpers", () => {
  test("brands only canonical values while the converting helper normalizes input", () => {
    const canonical = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

    assert.equal(isUlid(canonical), true);
    assert.equal(isUlid(canonical.toLowerCase()), false);
    assert.equal(isUlid(` ${canonical} `), false);
    assert.equal(tryUlid(` ${canonical.toLowerCase()} `), canonical);
    assert.equal(isUlid("81ARZ3NDEKTSV4RRFFQ69G5FAV"), false);
    assert.equal(tryUlid("81ARZ3NDEKTSV4RRFFQ69G5FAV"), undefined);
    assert.throws(
      () => toUlid("81ARZ3NDEKTSV4RRFFQ69G5FAV"),
      /Expected a 26-character ULID/u,
    );
  });
});
