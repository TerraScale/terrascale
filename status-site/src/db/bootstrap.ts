import schema from "../../schema.sql?raw";
import type { Env } from "../env";

const stores = new WeakMap<D1Database, Promise<unknown>>();

/** D1 exec requires one complete SQL statement per line in schema.sql. */
export async function bootstrap(env: Env): Promise<void> {
  let ready = stores.get(env.STATUS_DB);
  if (!ready) {
    ready = env.STATUS_DB.exec(schema).catch((error: unknown) => {
      stores.delete(env.STATUS_DB);
      throw error;
    });
    stores.set(env.STATUS_DB, ready);
  }
  await ready;
}
