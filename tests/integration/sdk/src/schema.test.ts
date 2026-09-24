import assert from "node:assert/strict";
import { describe, test } from "vitest";

import {
  collection,
  defineSchema,
  serializeSchema,
  validateSchema,
} from "./schema.js";
import type { SchemaJson } from "./schema.js";

describe("typed schema builder", () => {
  test("emits the complete D-78 schema contract", () => {
    const schema = defineSchema({
      events: {
        fields: {
          expiresAt: { type: "timestamp", required: true },
          body: { type: "text", required: true },
          attempts: { type: "counter-pn", required: true, mergePolicy: "counter-pn" },
          labels: { type: "set-add-wins", required: false, mergePolicy: "set-add-wins" },
        },
        indexes: [{ name: "events_by_expiry", fields: ["expiresAt"] }],
        fts: ["body"],
        ttl: "expiresAt",
        historyRetentionDays: 30,
        mergePolicy: "hlc-tiebreak",
      },
    });

    assert.deepEqual(JSON.parse(schema.toJSON()), {
      collections: [{
        name: "events",
        fields: [
          { name: "expiresAt", type: "timestamp", required: true, indexed: false },
          { name: "body", type: "text", required: true, indexed: false },
          { name: "attempts", type: "counter-pn", required: true, indexed: false, mergePolicy: "counter-pn" },
          { name: "labels", type: "set-add-wins", required: false, indexed: false, mergePolicy: "set-add-wins" },
        ],
        indexes: [
          { name: "events_by_expiry", fields: ["expiresAt"], kind: "btree" },
          { name: "body_fts", fields: ["body"], kind: "fts" },
        ],
        ttl: { field: "expiresAt" },
        historyRetentionDays: 30,
        mergePolicy: "hlc-tiebreak",
      }],
    });
  });

  test("sorts all declaration order and keeps output byte stable", () => {
    const first = defineSchema({
      zeta: { fields: { z: "string", a: "int" }, indexes: [{ name: "z", fields: ["z"] }, { name: "a", fields: ["a"] }] },
      alpha: { fields: { z: "string", a: "int" } },
    });
    const second = defineSchema({
      alpha: { fields: { z: "string", a: "int" } },
      zeta: { fields: { z: "string", a: "int" }, indexes: [{ name: "z", fields: ["z"] }, { name: "a", fields: ["a"] }] },
    });
    assert.equal(first.toJSON(), second.toJSON());
    assert.equal(first.toJSON(), serializeSchema(first.toObject()));
  });

  test("supports the fluent collection API and defaults history to seven days", () => {
    const built = collection("users")
      .field("id", "string", { indexed: true, unique: true })
      .field("expiresAt", "timestamp")
      .fullText("id")
      .ttl("expiresAt")
      .build();
    assert.equal(built.historyRetentionDays, 7);
    assert.equal(built.fields.find((field) => field.name === "id")?.unique, true);
    assert.deepEqual(built.indexes, [{ name: "id_fts", fields: ["id"], kind: "fts" }]);
  });

  test("rejects invalid history, names, references, and policies", () => {
    const invalid = [
      { collections: [{ name: "events", fields: [], historyRetentionDays: 0, mergePolicy: "remove-wins" }] },
      { collections: [{ name: "bad-name", fields: [], historyRetentionDays: 7, mergePolicy: "remove-wins" }] },
      { collections: [{ name: "events", fields: [], indexes: [{ name: "missing", fields: ["value"] }], historyRetentionDays: 7, mergePolicy: "remove-wins" }] },
      { collections: [{ name: "events", fields: [{ name: "expiresAt", type: "string", required: true, indexed: false }], ttl: { field: "expiresAt" }, historyRetentionDays: 7, mergePolicy: "remove-wins" }] },
      { collections: [{ name: "events", fields: [{ name: "value", type: "string", required: true, indexed: false, mergePolicy: "remove-wins" }], historyRetentionDays: 7, mergePolicy: "remove-wins" }] },
    ] as const;
    for (const schema of invalid) assert.throws(() => validateSchema(schema as unknown as SchemaJson), /history|name|unknown|TTL|merge policy/u);
  });
});
