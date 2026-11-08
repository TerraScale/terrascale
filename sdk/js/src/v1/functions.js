import { requireId } from "../ids.js";
import { requireDatabaseSelector, requireName } from "./ids.js";
import { list, nullable, numeric, optional, record, text } from "./management-decode.js";
import { members, mutationOptions } from "./management-validation.js";
import { stringifyExactJson } from "./json.js";
/** @import { V1Transport } from "./transport.js" */
/** @import { V1RequestOptions } from "./terrabase-v1.js" */

const functionBody = record({ name: text, source: optional(nullable(text)), cron: nullable(text), createdAt: text, updatedAt: text });
const logBody = record({ id: requireId, at: text, trigger: text, status: text, durationMs: numeric, lines: list(text), error: nullable(text) });
/** @typedef {ReturnType<typeof functionBody>} V1Function */
/** @typedef {ReturnType<typeof logBody>} V1FunctionLog */
/** @typedef {{ readonly source: string; readonly cron?: string | undefined }} V1FunctionDeployRequest */
/** @typedef {{ readonly body?: string | Uint8Array | undefined; readonly query?: Readonly<Record<string, string>> | undefined }} V1FunctionInvokeRequest */
/** @typedef {{ readonly status: number; readonly contentType: string; readonly body: Uint8Array }} V1FunctionResponse */

const maximumInvokeBytes = 1_048_576;

/** A deployed function's lifecycle: deploy, list, get, invoke, logs and delete for one database. */
export class V1Functions {
  /** @type {V1Transport} */
  #transport;
  /** @type {{database: string}} */
  #params;
  /** @param {V1Transport} transport @param {string} database */
  constructor(transport, database) { this.#transport = transport; this.#params = { database: requireDatabaseSelector(database) }; Object.freeze(this); }
  /** @param {string} name @param {V1FunctionDeployRequest} request @param {V1RequestOptions} [options] @returns {Promise<V1Function>} */
  async deploy(name, request, options = {}) {
    members(request, ["source", "cron"]);
    mutationOptions(options);
    if (typeof request.source !== "string") throw new TypeError("source must be a string.");
    if (request.cron !== undefined && typeof request.cron !== "string") throw new TypeError("cron must be a five-field UTC expression.");
    return functionBody((await this.#transport.call("deployFunction", { ...this.#params, function: requireName("function", name) }, {
      body: stringifyExactJson({ source: request.source, ...(request.cron === undefined ? {} : { cron: request.cron }) }, "function deploy"),
      signal: options.signal,
    })).value);
  }
  /** @param {V1RequestOptions} [options] @returns {Promise<readonly V1Function[]>} */
  async list(options = {}) {
    return list(functionBody)((await this.#transport.call("listFunctions", this.#params, { signal: options.signal })).value);
  }
  /** @param {string} name @param {V1RequestOptions} [options] @returns {Promise<V1Function>} */
  async get(name, options = {}) {
    return functionBody((await this.#transport.call("getFunction", { ...this.#params, function: requireName("function", name) },
      { signal: options.signal })).value);
  }
  /** @param {string} name @param {V1FunctionInvokeRequest} [request] @param {V1RequestOptions} [options] @returns {Promise<V1FunctionResponse>} */
  async invoke(name, request = {}, options = {}) {
    members(request, ["body", "query"]);
    mutationOptions(options);
    const bytes = typeof request.body === "string" ? new TextEncoder().encode(request.body) : request.body;
    if (bytes !== undefined && bytes.byteLength > maximumInvokeBytes)
      throw new TypeError(`Function input is limited to ${maximumInvokeBytes} bytes.`);
    const query = new URLSearchParams();
    if (request.query !== undefined) {
      for (const [key, value] of Object.entries(request.query)) {
        if (typeof value !== "string") throw new TypeError("query values must be strings.");
        query.append(key, value);
      }
    }
    return this.#transport.raw("invokeFunction", { ...this.#params, function: requireName("function", name) },
      { ...(bytes === undefined ? {} : { body: bytes }), query, signal: options.signal });
  }
  /** @param {string} name @param {number} [limit] @param {V1RequestOptions} [options] @returns {Promise<readonly V1FunctionLog[]>} */
  async logs(name, limit, options = {}) {
    if (limit !== undefined && (!Number.isInteger(limit) || limit < 1 || limit > 100))
      throw new TypeError("limit must be 1-100.");
    const query = new URLSearchParams();
    if (limit !== undefined) query.set("limit", String(limit));
    return list(logBody)((await this.#transport.call("listFunctionLogs", { ...this.#params, function: requireName("function", name) },
      { query, signal: options.signal })).value);
  }
  /** @param {string} name @param {V1RequestOptions} [options] @returns {Promise<void>} */
  async delete(name, options = {}) {
    mutationOptions(options);
    await this.#transport.call("deleteFunction", { ...this.#params, function: requireName("function", name) }, { signal: options.signal });
  }
}
