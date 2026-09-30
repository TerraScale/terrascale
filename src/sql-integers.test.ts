import { expect, test } from "vitest";
import { raw } from "./sql.js";
import { encodeDatabaseValue, decodeDatabaseValue } from "./database-codec.js";
test("native and SQL construction preserve both signed i64 endpoints without number conversion", () => {
  for (const value of [-(1n << 63n), (1n << 63n) - 1n]) {
    expect(
      raw("SELECT amount FROM balances WHERE key = $1", [value]).parameters[0],
    ).toBe(value);
    expect(
      decodeDatabaseValue(encodeDatabaseValue({ type: "i64", value })),
    ).toEqual({ type: "i64", value });
  }
});
