import { V1Database } from "./v1/terrabase-v1.js";

/** @typedef {"run" | "all" | "values" | "get"} DrizzleProxyMethod */
/** @typedef {{ readonly rows: readonly unknown[] }} DrizzleProxyResult */
/** @typedef {{ readonly sql: string; readonly params: readonly unknown[]; readonly method: DrizzleProxyMethod }} DrizzleProxyQuery */
/** @typedef {(sql: string, params: readonly unknown[], method: DrizzleProxyMethod) => Promise<{ rows: unknown[] }>} DrizzleQueryCallback */
/** @typedef {(queries: readonly DrizzleProxyQuery[]) => Promise<{ rows: unknown[] }[]>} DrizzleBatchCallback */

/** @param {import("./v1/sql-execute.js").V1SqlExecuteResult} result @param {DrizzleProxyMethod} method */
function proxyResult(result, method) {
  if (method === "run") return { rows: [] };
  if (method === "get") return { rows: result.rows.length > 0 ? [result.rows[0]] : [] };
  return { rows: result.rows.map((row) => [...row]) };
}

/** @param {unknown} query */
function proxyQuery(query) {
  const { sql, params, method } = /** @type {DrizzleProxyQuery} */ (query);
  if (typeof sql !== "string" || !Array.isArray(params) || (method !== "run" && method !== "all" && method !== "values" && method !== "get"))
    throw new TypeError("Drizzle proxy queries are { sql, params, method } with method run, all, values or get.");
  return { sql, args: params };
}

/**
 * Adapts a TerraBase database handle to the two callbacks Drizzle's `sqlite-proxy` driver expects.
 * @param {V1Database} database
 * @returns {{ query: DrizzleQueryCallback; batch: DrizzleBatchCallback }}
 */
export function drizzleProxy(database) {
  if (!(database instanceof V1Database)) throw new TypeError("drizzleProxy takes a V1Database from createTerraBaseClient(...).database(name).");
  return {
    async query(sql, params, method) {
      return proxyResult(await database.execute(sql, /** @type {import("./v1/sql-execute.js").V1SqlArguments} */ (params)), method);
    },
    async batch(queries) {
      const results = await database.batch(queries.map(proxyQuery));
      return results.map((result, index) => proxyResult(result, /** @type {DrizzleProxyQuery} */ (queries[index]).method));
    },
  };
}
