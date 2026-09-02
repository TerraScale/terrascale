import assert from "node:assert/strict";
import { describe, test } from "vitest";

import { isUlid, tryUlid } from "./ids.js";

describe("ULID helpers", () => {
  test("brands only canonical values while the converting helper normalizes input", () => {
    const canonical = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

    assert.equal(isUlid(canonical), true);
    assert.equal(isUlid(canonical.toLowerCase()), false);
    assert.equal(isUlid(` ${canonical} `), false);
    assert.equal(tryUlid(` ${canonical.toLowerCase()} `), canonical);
  });
});
