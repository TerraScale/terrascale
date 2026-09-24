import assert from "node:assert/strict";
import { describe, test } from "vitest";

import {
  Direction,
  Query,
  SqlValidationError,
  select,
  validateSql,
  metadataFields,
} from "./sql.js";
import { defineField, FIELD_TYPES } from "./schema.js";

describe("schema-bound SQL builder", () => {
  const name = defineField("name", FIELD_TYPES.text, { indexed: true });
  const age = defineField("age", FIELD_TYPES.int, { indexed: true });
  const schema = { name: "users", fields: [name, age] as const, historyRetentionDays: 7, mergePolicy: "remove-wins" as const };

  test("binds comparisons, FTS MATCH, ordering, and pagination automatically", () => {
    const query = select(schema)
      .column(name)
      .whereEq(age, 18)
      .whereMatch(name, "alice OR bob")
      .orderByField(age, Direction.Desc)
      .limit(10)
      .offset(20)
      .build();

    assert.equal(
      query.sql,
      "SELECT name FROM users WHERE age = $1 AND name MATCH $2 ORDER BY age DESC LIMIT $3 OFFSET $4",
    );
    assert.deepEqual(query.parameters, [18, "alice OR bob", 10, 20]);
    assert.deepEqual(query.execute(), {
      kind: "execute",
      statement: query.sql,
      sql: query.sql,
      parameters: query.parameters,
    });
    assert.equal(query.subscribe({ transport: "websocket" }).kind, "subscribe");
  });

  test("supports safe JSON paths and rejects undeclared fields", () => {
    const query = select(schema).whereJsonEq("$.profile.city", "GRU").build();
    assert.equal(query.sql, "SELECT * FROM users WHERE body ->> '$.profile.city' = $1");
    assert.deepEqual(query.parameters, ["GRU"]);

    assert.throws(
      () => select(schema).whereEq({ name: "secret", type: FIELD_TYPES.string, required: true, indexed: false }, "x"),
      SqlValidationError,
    );
    assert.throws(() => select(schema).whereJsonEq("$.profile.city' OR 1=1 --", "x"), SqlValidationError);
    assert.equal(select(schema).column(metadataFields._id).build().sql, "SELECT _id FROM users");
  });
});

describe("raw parameterized SQL validation", () => {
  test("accepts read-only statements and checks positional bindings", () => {
    for (const statement of [
      "SELECT * FROM users WHERE id = $1",
      "WITH recent AS (SELECT * FROM users) SELECT * FROM recent",
      "EXPLAIN SELECT * FROM users",
      "EXPLAIN QUERY PLAN SELECT * FROM users",
    ]) {
      Query.tryRaw(statement, statement.includes("$1") ? ["doc-1"] : []);
    }
    validateSql("SELECT * FROM users WHERE id = $1", ["doc-1"]);
    assert.throws(() => validateSql("SELECT * FROM users WHERE id = $1", []), SqlValidationError);
    assert.throws(() => validateSql("SELECT * FROM users", ["unused"]), SqlValidationError);
  });

  test("rejects writes, multiple statements, comments, unsafe functions, and gaps", () => {
    for (const statement of [
      "INSERT INTO users VALUES ($1)",
      "UPDATE users SET name = $1",
      "DELETE FROM users",
      "CREATE TABLE users (id INTEGER)",
      "SELECT * FROM users; SELECT * FROM users",
      "SELECT * FROM users -- DROP TABLE users",
      "SELECT random() FROM users",
      "SELECT CURRENT_TIMESTAMP FROM users",
      "WITH RECURSIVE recent AS (SELECT * FROM users) SELECT * FROM recent",
      "SELECT sqlite_master FROM users",
      "SELECT * FROM users WHERE id = $1 OR id = $3",
    ]) {
      assert.throws(() => Query.raw(statement, ["x", "y", "z"]), SqlValidationError, statement);
    }
  });
});
