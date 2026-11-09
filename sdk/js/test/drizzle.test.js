import { expect, test } from "vitest";
import { drizzleProxy } from "../src/drizzle.js";
import { fixture, json, prefix } from "./helpers/v1-http.js";

const people = {
  columns: [{ name: "id", decltype: "INTEGER" }, { name: "name", decltype: "TEXT" }],
  rows: [[1, "Ada"], [2, "Grace"]], rows_affected: 0, last_insert_rowid: null,
};
const empty = { columns: [], rows: [], rows_affected: 1, last_insert_rowid: 7 };

test("drizzleProxy maps execute and batch results to sqlite-proxy callback shapes", async () => {
  const { client, requests } = await fixture((request, response) => {
    const body = JSON.parse(request.body);
    json(response, { results: body.statements.map((/** @type {{ sql: string }} */ statement) => (statement.sql.includes("people") ? people : empty)) });
  });
  const { query, batch } = drizzleProxy(client.database("app"));

  expect(await query("SELECT id, name FROM people", [], "all")).toEqual({ rows: [[1, "Ada"], [2, "Grace"]] });
  expect(await query("SELECT id, name FROM people", [], "get")).toEqual({ rows: [1, "Ada"] });
  expect(await query("SELECT * FROM missing", [], "get")).toEqual({ rows: undefined });
  expect(await query("INSERT INTO t VALUES (?)", ["x"], "run")).toEqual({ rows: [] });
  expect(await query("SELECT id, name FROM people", [], "values")).toEqual({ rows: [[1, "Ada"], [2, "Grace"]] });

  const results = await batch([
    { sql: "INSERT INTO t VALUES (?)", params: ["x"], method: "run" },
    { sql: "SELECT id, name FROM people", params: [], method: "get" },
  ]);
  expect(requests).toHaveLength(6);
  expect(results).toEqual([{ rows: [] }, { rows: [1, "Ada"] }]);
  const batchRequest = requests.at(-1);
  expect(batchRequest?.url.pathname).toBe(`${prefix}/databases/app/sql/execute`);
  expect(JSON.parse(batchRequest?.body ?? "").statements).toEqual([
    { sql: "INSERT INTO t VALUES (?)", args: ["x"] },
    { sql: "SELECT id, name FROM people", args: [] },
  ]);

  expect(() => drizzleProxy(/** @type {any} */ ({}))).toThrow(TypeError);
});
