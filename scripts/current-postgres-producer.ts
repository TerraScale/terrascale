/** Installed-package journey. The producer owns the TLS listener and fixture. */
import { readFile } from "node:fs/promises";
import { PostgresClient, PostgresError, postgresQuery } from "@terrascale/sdk/postgres";

function required(name: string): string {
  const value = process.env[name];
  if (!value) throw new Error(`missing ${name}`);
  return value;
}
function identifier(name: string): string {
  const value = required(name);
  if (!/^[a-z_][a-z0-9_]*$/.test(value)) throw new Error(`invalid ${name}`);
  return `"${value}"`;
}

const table = identifier("TERRASCALE_PG_TABLE");
const key = identifier("TERRASCALE_PG_KEY_COLUMN");
const payloadColumn = identifier("TERRASCALE_PG_PAYLOAD_COLUMN");
const client = await PostgresClient.connect({
  host: required("TERRASCALE_PG_HOST"), port: Number(required("TERRASCALE_PG_PORT")),
  tlsServerName: required("TERRASCALE_PG_TLS_SERVER_NAME"),
  caCertificatePem: await readFile(required("TERRASCALE_PG_CA_FILE"), "utf8"),
  database: required("TERRASCALE_PG_DATABASE"), keyId: required("TERRASCALE_PG_KEY_ID"),
  password: required("TERRASCALE_PG_PASSWORD"),
});
const minimum = -(1n << 63n);
const maximum = (1n << 63n) - 1n;
const text = "quote ' ; DROP TABLE sentinel; -- \\ Unicode α 🌍";
function check(value: boolean): void { if (!value) throw new Error("PostgreSQL journey assertion failed"); }
try {
  await client.query(postgresQuery("BEGIN"));
  const inserted = await client.query(postgresQuery(`INSERT INTO ${table} (${key}, ${payloadColumn}) VALUES ($1,$2),($3,$4)`,
    [minimum, text, maximum, "maximum"]));
  check(inserted.affectedRows === 2);
  const staged = await client.query(postgresQuery(`SELECT ${payloadColumn}, ${key} FROM ${table} WHERE ${key}=$1`, [minimum]));
  check(staged.rows.length === 1 && staged.rows[0]?.[0] === text && staged.rows[0]?.[1] === minimum);
  check(staged.columns[0]?.oid === 25 && staged.columns[1]?.oid === 20);
  await client.query(postgresQuery("COMMIT"));
  const committed = await client.query(postgresQuery(`SELECT ${key}, ${payloadColumn} FROM ${table} WHERE ${key}=$1`, [maximum]));
  check(committed.rows[0]?.[0] === maximum && committed.rows[0]?.[1] === "maximum");
  try { await client.query(postgresQuery(`SELECT ${key} FROM ${table}`)); throw new Error("scan unexpectedly admitted"); }
  catch (error) { check(error instanceof PostgresError && error.sqlState === "0A000"); }
  for (const value of [minimum, maximum]) {
    const removed = await client.query(postgresQuery(`DELETE FROM ${table} WHERE ${key}=$1`, [value]));
    check(removed.affectedRows === 1);
  }
  process.stdout.write("PostgreSQL installed-package point journey passed\n");
} finally { await client.close(); }
