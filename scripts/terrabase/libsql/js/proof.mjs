// Proof: @libsql/client against TerraBase Hrana over HTTP. See ../README.md.
import { readFileSync } from "node:fs";
import { randomBytes } from "node:crypto";
import { createClient } from "@libsql/client";

const url = process.env.LIBSQL_URL;
const tokenFile = process.env.LIBSQL_TOKEN_FILE;
if (!url || !tokenFile) {
  console.error("set LIBSQL_URL and LIBSQL_TOKEN_FILE");
  process.exit(2);
}
const client = createClient({ url, authToken: readFileSync(tokenFile, "utf8").trim() });

const t = `proof_js_${randomBytes(4).toString("hex")}`;
const owners = `${t}_owners`;
const items = `${t}_items`;

function check(ok, what) {
  if (!ok) throw new Error(`check failed: ${what}`);
}
async function count() {
  const rs = await client.execute(`SELECT count(*) AS n FROM ${items}`);
  return Number(rs.rows[0].n);
}

let failed = false;
try {
  console.log(`create tables ${owners}, ${items}`);
  await client.execute(`CREATE TABLE ${owners} (id INTEGER PRIMARY KEY, name TEXT NOT NULL)`);
  await client.execute(`CREATE TABLE ${items} (sku TEXT PRIMARY KEY, owner_id INTEGER NOT NULL, qty INTEGER)`);

  const ins = await client.execute({ sql: `INSERT INTO ${owners} (id, name) VALUES (?, ?)`, args: [1, "ann"] });
  await client.execute({ sql: `INSERT INTO ${owners} (id, name) VALUES (:id, :name)`, args: { id: 2, name: "bob" } });
  console.log(`insert with parameters: rowsAffected=${ins.rowsAffected}`);
  check(ins.rowsAffected === 1, "insert affected 1 row");

  const batch = await client.batch(
    [
      { sql: `INSERT INTO ${items} (sku, owner_id, qty) VALUES (?, ?, ?)`, args: ["a-1", 1, 3] },
      { sql: `INSERT INTO ${items} (sku, owner_id, qty) VALUES (?, ?, ?)`, args: ["a-2", 1, 5] },
      { sql: `INSERT INTO ${items} (sku, owner_id, qty) VALUES (?, ?, ?)`, args: ["b-1", 2, 7] },
    ],
    "write",
  );
  console.log(`batch (write): ${batch.length} statements`);
  check(batch.length === 3, "batch returned 3 results");

  const join = await client.execute(
    `SELECT o.name, i.sku, i.qty FROM ${items} i JOIN ${owners} o ON o.id = i.owner_id ORDER BY i.sku`,
  );
  console.log("select with join:", join.rows.map((r) => `${r.name}/${r.sku}/${r.qty}`).join(", "));
  check(join.rows.length === 3 && join.rows[2].name === "bob", "join returned 3 rows, last owned by bob");

  const tx = await client.transaction("write");
  await tx.execute({ sql: `INSERT INTO ${items} (sku, owner_id, qty) VALUES (?, ?, ?)`, args: ["tx-1", 2, 1] });
  const seen = await tx.execute({ sql: `SELECT qty FROM ${items} WHERE sku = ?`, args: ["tx-1"] });
  check(seen.rows.length === 1, "transaction sees its own insert");
  await tx.commit();
  console.log(`transaction commit: count=${await count()}`);
  check((await count()) === 4, "committed row visible");

  const tx2 = await client.transaction("write");
  await tx2.execute({ sql: `INSERT INTO ${items} (sku, owner_id, qty) VALUES (?, ?, ?)`, args: ["tx-2", 2, 1] });
  await tx2.rollback();
  console.log(`transaction rollback: count=${await count()}`);
  check((await count()) === 4, "rolled back row not visible");
} catch (e) {
  failed = true;
  console.error(`error: ${e.message}`);
} finally {
  try {
    await client.batch([`DROP TABLE IF EXISTS ${items}`, `DROP TABLE IF EXISTS ${owners}`], "write");
    console.log("drop tables");
  } catch (e) {
    failed = true;
    console.error(`drop failed: ${e.message}`);
  }
  client.close();
}
console.log(failed ? "FAIL" : "PASS");
process.exit(failed ? 1 : 0);
