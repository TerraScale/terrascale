import { decodeTime } from "ulid";
import { describe, expect, it } from "vitest";
import { isId, newId } from "./ids";

describe("entity IDs", () => {
  it("generates distinct canonical IDs in order within one millisecond and through a clock rollback", () => {
    const at = Date.now();
    const ids = Array.from({ length: 512 }, () => newId(at));
    expect(ids.every(isId)).toBe(true);
    expect(new Set(ids).size).toBe(ids.length);
    expect([...ids].sort()).toEqual(ids);
    expect(decodeTime(ids[0]!)).toBe(at);
    expect(newId(at - 1) > ids.at(-1)!).toBe(true);
  });

  it("rejects noncanonical IDs and values outside the 128-bit range", () => {
    expect(isId("00000000000000000000000000")).toBe(true);
    expect(isId("7ZZZZZZZZZZZZZZZZZZZZZZZZZ")).toBe(true);
    for (const id of [
      "8ZZZZZZZZZZZZZZZZZZZZZZZZZ",
      "01m4e7g1w82vkfpf5fj9avk1cv",
      "acct_01M4E7G1W82VKFPF5FJ9AVK1CV",
      "b450c3c0-4427-41e4-b620-0ba7d93dfdb8",
      ...["I", "L", "O", "U"].map((letter) => `0000000000000000000000000${letter}`),
      "0000000000000000000000000",
      "000000000000000000000000000",
      "00000000000000000000000000\n",
      null,
      123,
    ])
      expect(isId(id)).toBe(false);
  });
});
