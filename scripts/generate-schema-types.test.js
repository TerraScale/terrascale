import { expect, test } from "vitest";
import { generateSchemaTypes } from "./generate-schema-types.js";
import { collection, defineSchema } from "../src/schema.js";
test("schema codegen emits native discriminants and ULID identities deterministically", () => {
  const schema = defineSchema([
    collection("items")
      .field(2, "bytes", "bytes", { required: false })
      .field(1, "count", "i64"),
  ]).build();
  const output = generateSchemaTypes(schema);
  expect(output).toContain("EntityId");
  expect(output).toContain(
    'readonly count: Extract<DatabaseValue, { readonly type: "i64" }>',
  );
  expect(output).toContain(
    'readonly bytes?: Extract<DatabaseValue, { readonly type: "bytes" }>',
  );
  expect(output).not.toMatch(/ULID|number \| bigint|counter-pn|timestamp/u);
  expect(generateSchemaTypes(JSON.stringify(schema))).toBe(output);
});
