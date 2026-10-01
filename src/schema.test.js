import { expect, test } from "vitest";
import {
  collection,
  defineField,
  defineSchema,
  encodeSchemaFields,
  normalizeSchema,
  SchemaValidationError,
} from "./schema.js";
/** @import { DatabaseValue } from "./database-types.js" */

test("pure schema builders retain persistent branches and deterministic numeric field order", () => {
  const base = collection("items").field(2, "payload", "bytes", {
    required: false,
  });
  const left = base.field(1, "count", "i64");
  const right = base.field(3, "active", "bool");
  expect(base.build().fields).toHaveLength(1);
  expect(left.build().fields.map((field) => field.id)).toEqual([1, 2]);
  expect(right.build().fields.map((field) => field.id)).toEqual([2, 3]);
  expect(() => defineSchema([right, left]).build()).toThrow();
  expect(() =>
    normalizeSchema({
      collections: [
        {
          name: "items",
          fields: [defineField(1, "a", "bool"), defineField(1, "b", "utf8")],
        },
      ],
    }),
  ).toThrow(SchemaValidationError);
  for (const type of [
    "float",
    "timestamp",
    "counter-pn",
    "set-add-wins",
    "register-mv",
  ])
    expect(() => defineField(1, "field", /** @type {"bool"} */ (type))).toThrow();
});

test("local declarations preserve exact native values, missing fields and explicit null", () => {
  const schema = collection("items")
    .field(1, "count", "i64")
    .field(2, "optional", "bytes", { required: false })
    .build();
  expect(
    encodeSchemaFields(schema, {
      count: { type: "i64", value: (1n << 63n) - 1n },
    }),
  ).toEqual([{ id: 1, value: { type: "i64", value: (1n << 63n) - 1n } }]);
  expect(encodeSchemaFields(schema, { count: { type: "null" } })).toEqual([
    { id: 1, value: { type: "null" } },
  ]);
  expect(() => encodeSchemaFields(schema, {})).toThrow(/Missing/u);
  expect(() =>
    encodeSchemaFields(schema, { count: { type: "i64", value: 1n << 63n } }),
  ).toThrow();
  expect(() =>
    encodeSchemaFields(schema, { count: { type: "utf8", value: "1" } }),
  ).toThrow();
});

test("schema field codec enforces shared document bounds and array descendant prohibition", () => {
  const schema = collection("items")
    .field(1, "a", "bytes")
    .field(2, "b", "bytes")
    .build();
  expect(() =>
    encodeSchemaFields(schema, {
      a: { type: "bytes", value: new Uint8Array(700_000) },
      b: { type: "bytes", value: new Uint8Array(700_000) },
    }),
  ).toThrow(/1 MiB/u);
  const arrays = collection("items").field(1, "a", "array").build();
  expect(() =>
    encodeSchemaFields(arrays, {
      a: {
        type: "array",
        items: [
          {
            type: "map",
            fields: [{ id: 1, value: { type: "array", items: [] } }],
          },
        ],
      },
    }),
  ).toThrow(/descendant arrays/u);
});

test("schema document bounds accept legal depth16 and share the value-node budget across fields", () => {
  const maps = collection("items").field(1, "nested", "map").build();
  /** @type {DatabaseValue} */
  let value = {
    type: "bool",
    value: true,
  };
  for (let depth = 0; depth < 15; depth += 1)
    value = { type: "map", fields: [{ id: 1, value }] };
  expect(encodeSchemaFields(maps, { nested: value })).toHaveLength(1);
  expect(() =>
    encodeSchemaFields(maps, {
      nested: { type: "map", fields: [{ id: 1, value }] },
    }),
  ).toThrow();
  const arrays = collection("items")
    .field(1, "a", "array")
    .field(2, "b", "array")
    .field(3, "c", "array")
    .field(4, "d", "array")
    .build();
  const items = Array.from({ length: 1023 }, () => ({ type: /** @type {const} */ ("null") }));
  expect(
    encodeSchemaFields(arrays, {
      a: { type: "array", items },
      b: { type: "array", items },
      c: { type: "array", items },
      d: { type: "array", items },
    }),
  ).toHaveLength(4);
  expect(() =>
    encodeSchemaFields(arrays, {
      a: { type: "array", items: [...items, { type: "null" }] },
      b: { type: "array", items },
      c: { type: "array", items },
      d: { type: "array", items },
    }),
  ).toThrow(/node/u);
});
