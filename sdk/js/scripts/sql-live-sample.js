#!/usr/bin/env node
import { ulid } from "ulid";
import { createTerraBaseClient, SqlError } from "../src/index.js";

const [baseUrl, project, token, database] = ["TB_V1_URL", "TB_V1_PROJECT", "TB_V1_DATA_KEY", "TB_V1_DATABASE"].map(name => process.env[name]);
if (!baseUrl || !project || !token || !database) throw new Error("Set TB_V1_URL, TB_V1_PROJECT, TB_V1_DATA_KEY and TB_V1_DATABASE.");

const db = createTerraBaseClient({ baseUrl, project, token }).database(database);
const table = `sdk_js_sample_${ulid().toLowerCase()}`;
/** @param {boolean} condition @param {string} what */
const check = (condition, what) => { if (!condition) throw new Error(`FAIL ${what}`); console.log(`ok ${what}`); };

await db.batch([
  `CREATE TABLE ${table} (id INTEGER PRIMARY KEY, name TEXT NOT NULL, avatar BLOB)`,
  { sql: `INSERT INTO ${table} (id, name, avatar) VALUES (?, ?, ?)`, args: [1, "Ada", new Uint8Array([0, 1, 255])] },
]);
const rows = await db.query(`SELECT id, name, avatar FROM ${table}`);
check(rows.length === 1 && rows[0]?.name === "Ada" && rows[0]?.avatar instanceof Uint8Array, "REST batch and typed rows");

const duplicate = await db.execute(`INSERT INTO ${table} (id, name) VALUES (1, 'again')`).catch(error => error);
check(duplicate instanceof SqlError && duplicate.sqlstate === "23505", `REST error ${duplicate?.code} ${duplicate?.sqlstate}`);

{
  await using socket = await db.connect();
  await socket.transaction(async tx => {
    await tx.execute(`INSERT INTO ${table} (id, name) VALUES (?, ?)`, [2, "Grace"]);
    check((await tx.query(`SELECT count(*) AS n FROM ${table}`))[0]?.n === 2, "WebSocket read inside its transaction");
  });
  await socket.transaction(async tx => {
    await tx.execute(`INSERT INTO ${table} (id, name) VALUES (?, ?)`, [3, "Linus"]);
    throw new Error("undo");
  }).catch(() => undefined);
  check((await socket.query(`SELECT id FROM ${table} ORDER BY id`)).map(row => row.id).join() === "1,2", "WebSocket commit and rollback");
  const refused = await socket.execute(`INSERT INTO ${table} (id, name) VALUES (2, 'again')`).catch(error => error);
  check(refused instanceof SqlError && refused.code === "primary_key_violation", `WebSocket error ${refused?.code} ${refused?.sqlstate}`);
}

await db.execute(`DROP TABLE ${table}`);
console.log("PASS");
