import schema from "../../schema.sql?raw";
import type { Env } from "../env";

const stores = new WeakMap<D1Database, Promise<void>>();
const SCHEMA_VERSION = "region-discovery-v1";

async function migrate(db: D1Database): Promise<void> {
  const ledger = await db
    .prepare("SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'schema_migrations'")
    .first();
  if (ledger && (await db.prepare("SELECT 1 FROM schema_migrations WHERE id = ?").bind(SCHEMA_VERSION).first())) {
    return;
  }
  await db.batch(
    schema
      .trim()
      .split("\n")
      .map((sql) => db.prepare(sql)),
  );
}

export async function bootstrap(env: Env): Promise<void> {
  let ready = stores.get(env.STATUS_DB);
  if (!ready) {
    ready = migrate(env.STATUS_DB).catch((error: unknown) => {
      stores.delete(env.STATUS_DB);
      throw error;
    });
    stores.set(env.STATUS_DB, ready);
  }
  await ready;
}
