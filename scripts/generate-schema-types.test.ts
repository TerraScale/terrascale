import assert from "node:assert/strict";
import { describe, test } from "vitest";

import { defineSchema } from "../src/schema.js";
import { generateSchemaTypes } from "./generate-schema-types.js";

describe("schema type generator", () => {
  test("generates branded IDs, metadata, and field document types", () => {
    const source = defineSchema({
      posts: {
        fields: {
          title: { type: "text", required: true },
          publishedAt: { type: "timestamp", required: false },
          views: { type: "counter-pn", required: true },
          tags: { type: "set-remove-wins", required: true },
          payload: { type: "json", required: true },
        },
      },
    }).toJSON();
    const generated = generateSchemaTypes(source);
    assert.match(generated, /export type PostsId = Brand<string, "PostsId">;/u);
    assert.match(generated, /readonly _id: PostsId;/u);
    assert.match(generated, /readonly publishedAt\?: Timestamp \| null;/u);
    assert.match(generated, /readonly views: number;/u);
    assert.match(generated, /readonly tags: ReadonlySet<JsonValue>;/u);
    assert.match(generated, /readonly payload: JsonValue;/u);
    assert.match(generated, /readonly posts: PostsDocument;/u);
  });

  test("is deterministic for declaration order and import path is explicit", () => {
    const first = defineSchema({ z: { fields: { value: "string" } }, a: { fields: { count: "int" } } }).toJSON();
    const second = defineSchema({ a: { fields: { count: "int" } }, z: { fields: { value: "string" } } }).toJSON();
    assert.equal(generateSchemaTypes(first), generateSchemaTypes(second));
    assert.match(generateSchemaTypes(first, { importPath: "./schema-runtime.js" }), /from "\.\/schema-runtime\.js";/u);
  });
});

