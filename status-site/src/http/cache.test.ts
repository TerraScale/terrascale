import { describe, expect, it } from "vitest";
import { cacheKey, oneOf, readQuery, text } from "./cache";

describe("readQuery", () => {
  const spec = { range: oneOf("24h", "7d"), q: text(8) };

  it("keeps only declared, valid parameters", () => {
    const url = new URL("https://s.test/?range=7d&q=%20db%20&bust=1");
    expect(readQuery(url, spec)).toEqual({ range: "7d", q: "db" });
  });

  it("drops invalid values", () => {
    expect(readQuery(new URL("https://s.test/?range=1y&q=far-too-long"), spec)).toEqual({});
  });
});

describe("cacheKey", () => {
  it("sorts parameters and marks fragments", () => {
    const url = new URL("https://s.test/history?b=1");
    expect(cacheKey(url, { range: "7d", kind: "warning" }, true)).toBe(
      "https://s.test/history?kind=warning&range=7d&fragment=main",
    );
    expect(cacheKey(url, {}, false)).toBe("https://s.test/history");
  });
});
