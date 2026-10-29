import { describe, expect, test } from "vitest";
import { ulid } from "ulid";
import { SqlError, TerraBaseV1Error, V1ForbiddenScopeError } from "../src/index.js";
import { ExactDecimal } from "../src/v1/json.js";
import { fixture, json, prefix } from "./helpers/v1-http.js";
import { liveConfigured, liveDatabase } from "./support/terrabase-v1-live.js";

// Native loopback HTTP verifies the SDK encoder and decoder, not a C# producer qualification.
const people = {
  columns: [{ name: "id", decltype: "INTEGER" }, { name: "name", decltype: "TEXT" }, { name: "n", decltype: null }],
  rows: [[1, "Ada", null], [2, "Grace", 2.5]], rows_affected: 0, last_insert_rowid: null,
};
const inserted = { columns: [], rows: [], rows_affected: 1, last_insert_rowid: 9007199254740993 };

test("execute sends one positional statement and decodes typed columns and rows", async () => {
  const { client, requests } = await fixture((_request, response) => json(response, { results: [people] }));
  const result = await client.database("app").execute("SELECT id, name, n FROM people WHERE id > ?", [0, true, 12345678901234567890n % (1n << 63n), "x"]);
  expect(requests[0]?.method).toBe("POST");
  expect(requests[0]?.url.pathname).toBe(`${prefix}/databases/app/sql/execute`);
  expect(requests[0]?.headers["content-type"]).toBe("application/json");
  expect(requests[0]?.headers.authorization).toMatch(/^Bearer /u);
  expect(requests[0]?.body).toBe(`{"statements":[{"sql":"SELECT id, name, n FROM people WHERE id > ?","args":[0,true,${12345678901234567890n % (1n << 63n)},"x"]}]}`);
  expect(result).toEqual({ columns: people.columns, rows: people.rows, rowsAffected: 0, lastInsertRowid: null });
});

test("query returns rows keyed by column name; named arguments drop no prefix", async () => {
  const { client, requests } = await fixture((_request, response) => json(response, { results: [people] }));
  /** @type {{ id: number; name: string; n: number | null }[]} */
  const rows = await client.database("app").query("SELECT id, name, n FROM people WHERE name = :name", { name: "Ada" });
  expect(rows).toEqual([{ id: 1, name: "Ada", n: null }, { id: 2, name: "Grace", n: 2.5 }]);
  expect(JSON.parse(requests[0]?.body ?? "")).toEqual({ statements: [{ sql: "SELECT id, name, n FROM people WHERE name = :name", named: { name: "Ada" } }] });
  await expect(client.database("app").query("SELECT 1", { ":name": "Ada" })).rejects.toThrow(/without its prefix/u);
});

test("batch sends every statement in one request and keeps integers beyond 2^53 exact", async () => {
  const { client, requests } = await fixture((_request, response) => {
    response.writeHead(200, { "content-type": "application/json" }).end(`{"results":[{"columns":[],"rows":[],"rows_affected":1,"last_insert_rowid":9007199254740993},${JSON.stringify(people)}]}`);
  });
  const results = await client.database("app").batch([{ sql: "INSERT INTO people (name) VALUES (?)", args: ["Ada"] }, "SELECT id, name, n FROM people"]);
  expect(requests).toHaveLength(1);
  expect(JSON.parse(requests[0]?.body ?? "").statements).toHaveLength(2);
  expect(results).toHaveLength(2);
  expect(results[0]?.rowsAffected).toBe(inserted.rows_affected);
  expect(results[0]?.lastInsertRowid).toBeInstanceOf(ExactDecimal);
  expect(BigInt(String(results[0]?.lastInsertRowid))).toBe(9007199254740993n);
  expect(results[1]?.rows).toEqual(people.rows);
});

test("rejects invalid batches before sending", async () => {
  const { client, requests } = await fixture((_request, response) => json(response, { results: [] }));
  const db = client.database("app");
  await expect(db.batch([])).rejects.toThrow(/1 to 256/u);
  await expect(db.batch(Array.from({ length: 257 }, () => "SELECT 1"))).rejects.toThrow(/1 to 256/u);
  await expect(db.execute("x".repeat(16_385))).rejects.toThrow(/16384/u);
  await expect(db.execute("SELECT ?", [Number.NaN])).rejects.toThrow(TypeError);
  await expect(db.execute("SELECT ?", [1n << 63n])).rejects.toThrow(TypeError);
  await expect(db.execute("SELECT ?", [/** @type {any} */ ({})])).rejects.toThrow(TypeError);
  await expect(db.batch([/** @type {any} */ ({ sql: "SELECT 1", params: [] })])).rejects.toThrow(/params/u);
  expect(requests).toHaveLength(0);
});

test("rejects a result count or row width that does not match", async () => {
  const { client } = await fixture((_request, response) => json(response, { results: [{ ...people, rows: [[1]] }] }));
  await expect(client.database("app").execute("SELECT 1")).rejects.toThrow(/row width/u);
  const other = await fixture((_request, response) => json(response, { results: [] }));
  await expect(other.client.database("app").execute("SELECT 1")).rejects.toThrow(/result count/u);
});

test.each([
  [400, "sql_error", "SQLITE_CONSTRAINT_PRIMARYKEY"],
  [403, "forbidden_scope", "SQLITE_AUTH"],
  [409, "sql_conflict", "SQLITE_BUSY"],
  [413, "sql_too_large", "SQLITE_TOOBIG"],
])("maps %i %s to SqlError with the statement index", async (status, code, sqliteCode) => {
  const { client } = await fixture((_request, response) => json(response, { error: { code, message: "refused", statement: 1, sqlite_code: sqliteCode } }, status));
  const error = await client.database("app").batch(["SELECT 1", "INSERT INTO t VALUES (1)"]).catch(error => error);
  expect(error).toBeInstanceOf(SqlError);
  expect(error).toBeInstanceOf(TerraBaseV1Error);
  expect(error).toMatchObject({ name: "SqlError", status, code, message: expect.stringContaining("refused"), statement: 1, sqliteCode });
});

test("keeps generic errors outside SqlError", async () => {
  const { client } = await fixture((_request, response) => json(response, { error: { code: "forbidden_scope", message: "missing scope" } }, 403));
  const error = await client.database("app").execute("SELECT 1").catch(error => error);
  expect(error).toBeInstanceOf(V1ForbiddenScopeError);
  expect(error).not.toBeInstanceOf(SqlError);
});

describe.skipIf(!liveConfigured)("real C# relational SQL", () => {
  test("creates a table, writes in one transaction and reads typed rows", async () => {
    const db = liveDatabase();
    const table = `sdk_sql_${ulid().toLowerCase()}`;
    await db.execute(`CREATE TABLE ${table} (id INTEGER PRIMARY KEY, name TEXT NOT NULL, score REAL)`);
    const [first] = await db.batch([
      { sql: `INSERT INTO ${table} (id, name, score) VALUES (?, ?, ?)`, args: [1, "Ada", 1.5] },
      { sql: `INSERT INTO ${table} (id, name, score) VALUES (:id, :name, :score)`, named: { id: 2, name: "Grace", score: null } },
    ]);
    expect(first?.rowsAffected).toBe(1);
    expect(await db.query(`SELECT id, name, score FROM ${table} ORDER BY id`)).toEqual([{ id: 1, name: "Ada", score: 1.5 }, { id: 2, name: "Grace", score: null }]);
    const error = await db.batch([`INSERT INTO ${table} (id, name) VALUES (3, 'Linus')`, `INSERT INTO ${table} (id, name) VALUES (1, 'again')`]).catch(error => error);
    expect(error).toBeInstanceOf(SqlError);
    expect(error).toMatchObject({ status: 400, code: "sql_error", statement: 1 });
    expect(await db.query(`SELECT id FROM ${table} WHERE id = 3`)).toEqual([]);
    await db.execute(`DROP TABLE ${table}`);
  });
});
