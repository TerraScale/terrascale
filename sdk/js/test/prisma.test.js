import { expect, test } from "vitest";
import { PrismaTerraScale } from "../src/prisma.js";
import { fixture, json } from "./helpers/v1-http.js";
import { sqlSocketFixture } from "./helpers/sql-socket-server.js";

const created = {
  columns: [{ name: "id", decltype: "INTEGER" }, { name: "name", decltype: "TEXT" }, { name: "score", decltype: "REAL" }, { name: "avatar", decltype: "BLOB" }],
  rows: [[1, "Ada", 1.5, { base64: "AQI=" }], [2, "Grace", null, null]],
  rows_affected: 0, last_insert_rowid: null,
};

test("adapter maps typed columns, rows, affected rows and the last insert id", async () => {
  let calls = 0;
  const { client } = await fixture((_request, response) => {
    calls += 1;
    if (calls === 1) json(response, { results: [created] });
    else response.writeHead(200, { "content-type": "application/json" }).end('{"results":[{"columns":[],"rows":[],"rows_affected":1,"last_insert_rowid":9007199254740993}]}');
  });
  const adapter = await new PrismaTerraScale(client.database("app")).connect();
  expect(adapter.provider).toBe("sqlite");
  expect(adapter.adapterName).toBe("@terrascale/sdk/prisma");
  const result = await adapter.queryRaw({ sql: "SELECT * FROM people", args: [], argTypes: [] });
  expect(result.columnNames).toEqual(["id", "name", "score", "avatar"]);
  expect(result.columnTypes).toEqual([0, 7, 3, 13]);
  expect(result.rows).toEqual([[1, "Ada", 1.5, Uint8Array.from([1, 2])], [2, "Grace", null, null]]);
  expect(await adapter.executeRaw({ sql: "INSERT INTO people (name) VALUES (?)", args: ["Linus"], argTypes: [{ scalarType: "string", arity: "scalar" }] })).toBe(1);
  expect(result.lastInsertId).toBeUndefined();
  const insert = await adapter.queryRaw({ sql: "INSERT INTO people (name) VALUES (?)", args: ["Ada"], argTypes: [] });
  expect(insert.lastInsertId).toBe("9007199254740993");
  await adapter.dispose();
});

test("interactive transactions run begin, statements and rollback or commit over one socket each", async () => {
  const { client, frames, upgrades } = await sqlSocketFixture((frame, peer) => {
    const results = frame.type === "execute"
      ? [{ columns: [], rows: [], rows_affected: 1, last_insert_rowid: null }]
      : [];
    peer.send({ id: frame.id, type: "result", results, transaction: frame.type === "begin" || frame.type === "execute" });
  });
  const adapter = await new PrismaTerraScale(client.database("app")).connect();
  const rolledBack = await adapter.startTransaction();
  expect(rolledBack.options).toEqual({ usePhantomQuery: false });
  await rolledBack.executeRaw({ sql: "INSERT INTO people (name) VALUES (?)", args: ["Ada"], argTypes: [] });
  await rolledBack.rollback();
  const committed = await adapter.startTransaction("SERIALIZABLE");
  expect(await committed.queryRaw({ sql: "SELECT * FROM people", args: [], argTypes: [] })).toMatchObject({ columnNames: [], rows: [] });
  await committed.commit();
  expect(frames.map(frame => frame.type)).toEqual(["begin", "execute", "rollback", "begin", "execute", "commit"]);
  expect(upgrades).toHaveLength(2);
  await expect(adapter.startTransaction("READ COMMITTED")).rejects.toThrow("Unsupported isolation level: READ COMMITTED");
  await adapter.dispose();
});

test("a unique violation surfaces as Prisma's UniqueConstraintViolation kind", async () => {
  const { client } = await fixture((_request, response) =>
    json(response, { error: { code: "unique_violation", message: "UNIQUE constraint failed: people.id", statement: 0, sqlstate: "23505" } }, 400));
  const adapter = await new PrismaTerraScale(client.database("app")).connect();
  const error = await adapter.executeRaw({ sql: "INSERT INTO people (id) VALUES (1)", args: [], argTypes: [] }).catch(error => error);
  expect(error).toBeInstanceOf(Error);
  expect(error.name).toBe("DriverAdapterError");
  expect(error.cause).toEqual({
    kind: "UniqueConstraintViolation",
    originalCode: "unique_violation",
    originalMessage: "TerraBase 400 unique_violation: UNIQUE constraint failed: people.id",
  });
  expect(error.message).toContain("UNIQUE constraint failed: people.id");
  await adapter.dispose();
});

test("executeScript splits on top-level semicolons only, keeping trigger bodies and string literals whole", async () => {
  /** @type {string[]} */
  const received = [];
  const { client } = await fixture((request, response) => {
    const statements = JSON.parse(request.body).statements;
    received.push(...statements.map(/** @param {{ sql: string }} statement */ statement => statement.sql));
    json(response, { results: statements.map(() => ({ columns: [], rows: [], rows_affected: 0, last_insert_rowid: null })) });
  });
  const adapter = await new PrismaTerraScale(client.database("app")).connect();
  await adapter.executeScript([
    "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL);",
    "INSERT INTO people (id, name) VALUES (1, 'Ada; Grace');",
    "-- a ; comment\n/* a ; block */ UPDATE people SET name = 'Linus; ' || \"x;\" WHERE id = 1;",
    "CREATE TRIGGER touch AFTER INSERT ON people BEGIN UPDATE people SET name = 'kept; whole' WHERE id = NEW.id; UPDATE people SET score = 1; END;",
    "SELECT ';'",
  ].join("\n"));
  expect(received).toEqual([
    "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL);",
    "INSERT INTO people (id, name) VALUES (1, 'Ada; Grace');",
    "-- a ; comment\n/* a ; block */ UPDATE people SET name = 'Linus; ' || \"x;\" WHERE id = 1;",
    "CREATE TRIGGER touch AFTER INSERT ON people BEGIN UPDATE people SET name = 'kept; whole' WHERE id = NEW.id; UPDATE people SET score = 1; END;",
    "SELECT ';'",
  ]);
  await adapter.dispose();
});
