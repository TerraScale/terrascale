import assert from "node:assert/strict";
import { describe, test } from "vitest";

import { defineSchema } from "../src/schema.js";
import { generateSchemaTypes } from "./generate-schema-types.js";

describe("schema type generator", () => {
  test("generates branded IDs and canonical document read types", () => {
    const source = defineSchema({
      posts: {
        fields: {
          bytes: { type: "bytes", required: true },
          count: { type: "int", required: true },
          ratio: { type: "float", required: true },
          enabled: { type: "bool", required: true },
          register: { type: "register-mv", required: true },
          additions: { type: "set-add-wins", required: true },
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
    assert.doesNotMatch(generated, /readonly _(id|rev|hlc|created|updated):/u);
    assert.match(generated, /readonly bytes: string;/u);
    assert.match(generated, /readonly count: number \| bigint;/u);
    assert.match(generated, /readonly ratio: number;/u);
    assert.match(generated, /readonly enabled: boolean;/u);
    assert.match(generated, /readonly register: DocumentValue;/u);
    assert.match(generated, /readonly additions: readonly DocumentValue\[\];/u);
    assert.match(generated, /readonly publishedAt\?: number \| bigint \| null;/u);
    assert.match(generated, /readonly views: number \| bigint;/u);
    assert.match(generated, /readonly tags: readonly DocumentValue\[\];/u);
    assert.match(generated, /readonly payload: DocumentValue;/u);
    assert.match(generated, /readonly posts: PostsDocument;/u);
  });

  test("is deterministic for declaration order and import path is explicit", () => {
    const first = defineSchema({ z: { fields: { value: "string" } }, a: { fields: { count: "int" } } }).toJSON();
    const second = defineSchema({ a: { fields: { count: "int" } }, z: { fields: { value: "string" } } }).toJSON();
    assert.equal(generateSchemaTypes(first), generateSchemaTypes(second));
    assert.match(generateSchemaTypes(first, { importPath: "./schema-runtime.js" }), /from "\.\/schema-runtime\.js";/u);
  });
});

