// Smoke test: node-postgres (pg) against TerraBase over the Postgres wire. See ../README.md.
import { randomBytes } from "node:crypto";
import pg from "pg";

const url = process.env.TERRABASE_PG_URL;
if (!url) {
  console.error("set TERRABASE_PG_URL");
  process.exit(2);
}
const { hostname, port, pathname } = new URL(url);
console.log(`target ${hostname}:${port || 5432} db=${decodeURIComponent(pathname.slice(1))}`);

const t = `smoke_js_${randomBytes(4).toString("hex")}`;
const owners = `${t}_owners`;
const items = `${t}_items`;
const client = new pg.Client({ connectionString: url });

function check(ok, what) {
  if (!ok) throw new Error(`check failed: ${what}`);
}
async function count() {
  const rs = await client.query(`SELECT count(*) AS n FROM ${items}`);
  return Number(rs.rows[0].n);
}
const insert = `INSERT INTO ${items} (sku, owner_id, qty) VALUES ($1, $2, $3)`;

const steps = [
  ["create tables (integer and text primary keys)", async () => {
    await client.query(`CREATE TABLE ${owners} (id integer PRIMARY KEY, name text NOT NULL)`);
    await client.query(`CREATE TABLE ${items} (sku text PRIMARY KEY, owner_id integer NOT NULL, qty integer)`);
  }],
  ["insert (simple protocol, literals)", async () => {
    // No values: pg sends a simple Query message.
    const rs = await client.query(`INSERT INTO ${owners} (id, name) VALUES (1, 'ann'), (2, 'bob')`);
    check(rs.rowCount === 2, `rowCount=${rs.rowCount}, want 2`);
    await client.query(`INSERT INTO ${items} (sku, owner_id, qty) VALUES ('a-1', 1, 3)`);
  }],
  ["insert with parameters (extended protocol, unnamed statement)", async () => {
    const rs = await client.query(insert, ["a-2", 1, 5]);
    check(rs.rowCount === 1, `rowCount=${rs.rowCount}, want 1`);
  }],
  ["insert with a named prepared statement, reused", async () => {
    for (const values of [["b-1", 2, 7], ["b-2", 2, 9]]) {
      const rs = await client.query({ name: `${t}_ins`, text: insert, values });
      check(rs.rowCount === 1, `rowCount=${rs.rowCount}, want 1`);
    }
    check((await count()) === 4, "4 items");
  }],
  ["update with affected-row count", async () => {
    const rs = await client.query(`UPDATE ${items} SET qty = qty + 1 WHERE owner_id = $1`, [2]);
    check(rs.rowCount === 2, `rowCount=${rs.rowCount}, want 2`);
  }],
  ["delete with affected-row count", async () => {
    const rs = await client.query(`DELETE FROM ${items} WHERE sku = $1`, ["b-2"]);
    check(rs.rowCount === 1, `rowCount=${rs.rowCount}, want 1`);
    check((await count()) === 3, "3 items");
  }],
  ["select with a join and a parameter", async () => {
    const rs = await client.query(
      `SELECT o.name, i.sku, i.qty FROM ${items} i JOIN ${owners} o ON o.id = i.owner_id WHERE o.name = $1 ORDER BY i.sku`,
      ["ann"],
    );
    const got = rs.rows.map((r) => `${r.name}/${r.sku}/${r.qty}`).join(",");
    check(got === "ann/a-1/3,ann/a-2/5", `rows=${got}`);
  }],
  ["transaction commit persists", async () => {
    await client.query("BEGIN");
    await client.query(insert, ["tx-1", 2, 1]);
    await client.query("COMMIT");
    check((await count()) === 4, "committed row visible");
  }],
  ["transaction rollback discards", async () => {
    await client.query("BEGIN");
    await client.query(insert, ["tx-2", 2, 1]);
    await client.query("ROLLBACK");
    check((await count()) === 4, "rolled back row not visible");
  }],
  ["unique violation is SQLSTATE 23505", async () => {
    try {
      await client.query(insert, ["a-1", 1, 1]);
    } catch (e) {
      check(e.code === "23505", `sqlstate=${e.code}`);
      return;
    }
    throw new Error("duplicate insert succeeded");
  }],
  ["prepared select reused", async () => {
    const q = (sku) => client.query({ name: `${t}_qty`, text: `SELECT qty FROM ${items} WHERE sku = $1`, values: [sku] });
    check((await q("a-1")).rows[0].qty === 3, "a-1 qty 3");
    check((await q("b-1")).rows[0].qty === 8, "b-1 qty 8");
  }],
];

let failed = false;
try {
  await client.connect();
} catch (e) {
  console.log(`FAIL connect: ${e.message}`);
  console.log("FAIL");
  process.exit(1);
}
try {
  for (const [name, fn] of steps) {
    try {
      await fn();
      console.log(`PASS ${name}`);
    } catch (e) {
      failed = true;
      console.log(`FAIL ${name}: ${e.message}`);
      break;
    }
  }
} finally {
  if (failed) await client.query("ROLLBACK").catch(() => {}); // leave a transaction a failed step opened
  try {
    await client.query(`DROP TABLE IF EXISTS ${items}`);
    await client.query(`DROP TABLE IF EXISTS ${owners}`);
    console.log("PASS drop tables");
  } catch (e) {
    failed = true;
    console.log(`FAIL drop tables: ${e.message}`);
  }
  await client.end().catch(() => {});
}
console.log(failed ? "FAIL" : "PASS");
process.exit(failed ? 1 : 0);
