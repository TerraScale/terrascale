import { expect, test } from "vitest";
import { collection } from "./schema.js";
import { raw, select, SqlValidationError } from "./sql.js";

test("persistent schema SQL builder keeps values separate and uses positional bindings", () => {
  const schema = collection("items")
    .field(1, "key", "i64")
    .field(2, "name", "utf8")
    .build();
  const [key, name] = schema.fields;
  if (key === undefined || name === undefined)
    throw new Error("schema fields are missing");
  const base = select(schema).columns([name]);
  const query = base
    .whereEq(key, 9_223_372_036_854_775_807n)
    .limit(10)
    .build();
  expect(query.statement).toBe(
    "SELECT name FROM items WHERE key = $1 LIMIT $2",
  );
  expect(query.parameters).toEqual([9_223_372_036_854_775_807n, 10]);
  expect(base.build().parameters).toEqual([]);
  expect(query.toDescriptor()).toEqual({
    statement: query.statement,
    parameters: query.parameters,
  });
  expect("subscribe" in query).toBe(false);
});

test("exact integer/byte parameters are isolated and unsupported coercions fail before execution", () => {
  const bytes = Uint8Array.of(1, 2);
  const query = raw("SELECT name FROM items WHERE key = $1", [bytes]);
  bytes[0] = 5;
  const copy = /** @type {Uint8Array} */ (query.parameters[0]);
  copy[0] = 9;
  expect(query.parameters).toEqual([Uint8Array.of(1, 2)]);
  for (const value of [
    Number.MAX_SAFE_INTEGER + 1,
    1.5,
    1n << 63n,
    new Date(),
    {},
  ]) {
    expect(() =>
      raw("SELECT name FROM items WHERE key = $1", [/** @type {bigint} */ (value)]),
    ).toThrow(SqlValidationError);
  }
  expect(() => raw("SELECT name FROM items; DELETE FROM items")).toThrow();
  expect(() => raw("SELECT name FROM items WHERE key = $2", [1n])).toThrow();
  expect(() =>
    raw("SELECT name FROM items WHERE key = $1", [1n, 2n]),
  ).toThrow();
});
