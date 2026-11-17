import { requireId } from "../ids.js";
import { requireDatabaseSelector } from "./ids.js";
import { dictionary, flag, list, nullable, optional, record, text } from "./management-decode.js";
import { members, mutationOptions, pageQuery } from "./management-validation.js";
import { assertJsonObject, stringifyExactJson } from "./json.js";
import { object } from "./decode.js";
/** @import { V1Transport } from "./transport.js" */
/** @import { V1RequestOptions } from "./terrabase-v1.js" */
/** @import { JsonObject, JsonValue } from "./json.js" */

/**
 * @typedef {{ readonly create?: Readonly<Record<string, string>> | undefined; readonly update?: Readonly<Record<string, string>> | undefined }} V1RulesValues
 * @typedef {{ readonly read?: string | undefined; readonly create?: string | undefined; readonly update?: string | undefined;
 *   readonly delete?: string | undefined; readonly columns?: readonly string[] | undefined; readonly writable?: readonly string[] | undefined;
 *   readonly validate?: readonly string[] | undefined; readonly set?: V1RulesValues | undefined }} V1RulesTable
 * @typedef {{ readonly tables: Readonly<Record<string, V1RulesTable>> }} V1RulesSet
 * @typedef {{ readonly uid: string; readonly claims?: JsonObject | undefined; readonly org_id?: string | null | undefined;
 *   readonly org_role?: string | null | undefined; readonly is_anonymous?: boolean | undefined }} V1RulesTestAuth
 * @typedef {{ readonly operation: "read" | "create" | "update" | "delete"; readonly table: string; readonly auth: V1RulesTestAuth;
 *   readonly row?: JsonObject | undefined; readonly new?: JsonObject | undefined; readonly time?: number | undefined;
 *   readonly rules?: V1RulesSet | undefined }} V1RulesTestRequest
 */

const problem = record({ table: text, field: text, code: text, message: text });
const names = list(text);
const assignments = dictionary(text);

/** @param {JsonValue | undefined} value @returns {V1RulesTable} */
function rulesTable(value) {
  const raw = object(value, "rules table");
  /** @type {Record<string, unknown>} */
  const table = {};
  for (const key of ["read", "create", "update", "delete"]) if (raw[key] !== undefined) table[key] = text(raw[key]);
  for (const key of ["columns", "writable", "validate"]) if (raw[key] !== undefined) table[key] = names(raw[key]);
  if (raw.set !== undefined) {
    const set = object(raw.set, "rules set values");
    table.set = Object.freeze({ ...(set.create === undefined ? {} : { create: assignments(set.create) }),
      ...(set.update === undefined ? {} : { update: assignments(set.update) }) });
  }
  return /** @type {V1RulesTable} */ (Object.freeze(table));
}

const rulesBody = record({ version: requireId, tables: dictionary(rulesTable), problems: optional(list(problem)),
  warnings: optional(list(problem)), updatedAt: text, updatedBy: text });
const versionItem = record({ version: requireId, updatedAt: text, updatedBy: text, digest: text });
const versionPage = record({ items: list(versionItem), next: nullable(text) });
const testBody = record({ allowed: flag, decidedBy: text, clause: nullable(text),
  results: list(record({ rule: text, value: flag })), columns: names, problems: list(problem) });
/** @typedef {ReturnType<typeof problem>} V1RulesProblem */
/** @typedef {ReturnType<typeof rulesBody>} V1Rules */
/** @typedef {ReturnType<typeof versionItem>} V1RulesVersion */
/** @typedef {ReturnType<typeof versionPage>} V1RulesVersionPage */
/** @typedef {ReturnType<typeof testBody>} V1RulesTestResult */

/** @param {string | null} current @returns {Record<string, string>} */
function precondition(current) {
  if (current === null) return { "If-None-Match": "*" };
  return { "If-Match": `"${requireId(current, "current rules version")}"` };
}

/** @param {V1RulesSet} rules */
function rulesJson(rules) {
  members(rules, ["tables"]);
  assertJsonObject(rules.tables, "rules tables");
  return stringifyExactJson(rules, "rules");
}

/**
 * A database's Rules: the deployed set, its kept versions, restores and the simulator.
 * Deploy and restore take the current version (`null` when the database has no rules) so a newer deployment is never overwritten.
 */
export class V1DatabaseRules {
  /** @type {V1Transport} */
  #transport;
  /** @type {{database: string}} */
  #params;
  /** @param {V1Transport} transport @param {string} database */
  constructor(transport, database) { this.#transport = transport; this.#params = { database: requireDatabaseSelector(database) }; Object.freeze(this); }
  /** Reads the deployed rules; `404 no_rules` when none were deployed. @param {V1RequestOptions} [options] @returns {Promise<V1Rules>} */
  async get(options = {}) {
    return rulesBody((await this.#transport.call("getDatabaseRules", this.#params, { signal: options.signal })).value);
  }
  /** Deploys a rules set over `currentVersion`. @param {V1RulesSet} rules @param {string | null} currentVersion @param {V1RequestOptions} [options] @returns {Promise<V1Rules>} */
  async deploy(rules, currentVersion, options = {}) {
    mutationOptions(options);
    return rulesBody((await this.#transport.call("putDatabaseRules", this.#params,
      { body: rulesJson(rules), headers: precondition(currentVersion), signal: options.signal })).value);
  }
  /** Lists kept versions, newest first. @param {{ readonly after?: string | undefined; readonly limit?: number | undefined }} [request] @param {V1RequestOptions} [options] @returns {Promise<V1RulesVersionPage>} */
  async versions(request = {}, options = {}) {
    return versionPage((await this.#transport.call("listDatabaseRulesVersions", this.#params,
      { query: pageQuery(request), signal: options.signal })).value);
  }
  /** Reads one kept version. @param {string} version @param {V1RequestOptions} [options] @returns {Promise<V1Rules>} */
  async version(version, options = {}) {
    return rulesBody((await this.#transport.call("getDatabaseRulesVersion", { ...this.#params, version: requireId(version, "rules version") },
      { signal: options.signal })).value);
  }
  /** Deploys a kept version's body as a new version over `currentVersion`. @param {string} version @param {string | null} currentVersion @param {V1RequestOptions} [options] @returns {Promise<V1Rules>} */
  async restore(version, currentVersion, options = {}) {
    mutationOptions(options);
    return rulesBody((await this.#transport.call("restoreDatabaseRulesVersion", { ...this.#params, version: requireId(version, "rules version") },
      { headers: precondition(currentVersion), signal: options.signal })).value);
  }
  /** Evaluates one request against the deployed rules, or the draft in `request.rules`. @param {V1RulesTestRequest} request @param {V1RequestOptions} [options] @returns {Promise<V1RulesTestResult>} */
  async test(request, options = {}) {
    members(request, ["operation", "table", "auth", "row", "new", "time", "rules"]);
    if (!["read", "create", "update", "delete"].includes(request.operation)) throw new TypeError("operation must be read, create, update or delete.");
    members(request.auth, ["uid", "claims", "org_id", "org_role", "is_anonymous"]);
    if (request.rules !== undefined) rulesJson(request.rules);
    return testBody((await this.#transport.call("testDatabaseRules", this.#params,
      { body: stringifyExactJson(request, "rules test"), signal: options.signal })).value);
  }
}
