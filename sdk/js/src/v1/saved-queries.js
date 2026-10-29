import { requireId } from "../ids.js";
import { array, malformed, object, string } from "./decode.js";
import { stringifyExactJson } from "./json.js";
import { members, mutationOptions, pageQuery } from "./management-validation.js";
/** @import { V1Transport } from "./transport.js" */
/** @import { V1RequestOptions } from "./terrabase-v1.js" */
/** @typedef {{ readonly id: string; readonly name: string; readonly sql: string; readonly createdAt: string; readonly updatedAt: string | null }} V1SavedQuery */
/** @typedef {{ readonly name: string; readonly sql: string }} V1SavedQueryRequest */
/** @typedef {{ readonly items: readonly V1SavedQuery[]; readonly next: string | null }} V1SavedQueryPage */

/** @param {import("./json.js").JsonValue | undefined} value @returns {V1SavedQuery} */
function savedQuery(value) {
  const raw = object(value, "saved query");
  return Object.freeze({ id: requireId(raw.id), name: string(raw.name, "saved query name"), sql: string(raw.sql, "saved SQL"),
    createdAt: string(raw.createdAt, "saved query createdAt"), updatedAt: raw.updatedAt === null ? null : string(raw.updatedAt, "saved query updatedAt") });
}
/** @param {V1SavedQueryRequest} request */
function body(request) {
  members(request, ["name", "sql"]);
  if (typeof request.name !== "string" || request.name.trim().length < 1 || request.name.trim().length > 128 ||
      /[\p{Cc}]/u.test(request.name) || !/[\p{L}\p{N}]/u.test(request.name)) throw new TypeError("name must contain a letter or digit and be 1-128 characters without controls.");
  if (typeof request.sql !== "string" || request.sql.trim() === "" || request.sql.length > 16_384)
    throw new TypeError("sql must be a non-empty statement of at most 16384 UTF-16 code units.");
  return stringifyExactJson({ name: request.name, sql: request.sql }, "saved query");
}

/** Saved queries belong to the authorized project, independently of database handles. */
export class V1SavedQueries {
  /** @type {V1Transport} */
  #transport;
  /** @param {V1Transport} transport */
  constructor(transport) { this.#transport = transport; Object.freeze(this); }
  /** @param {V1SavedQueryRequest} request @param {V1RequestOptions} [options] @returns {Promise<V1SavedQuery>} */
  async create(request, options = {}) {
    mutationOptions(options);
    return savedQuery((await this.#transport.call("createSavedQuery", {}, { body: body(request), signal: options.signal })).value);
  }
  /** @param {import("./catalog.js").V1DatabaseListRequest} [request] @param {V1RequestOptions} [options] @returns {Promise<V1SavedQueryPage>} */
  async list(request = {}, options = {}) {
    const raw = object((await this.#transport.call("listSavedQueries", {}, { query: pageQuery(request), signal: options.signal })).value, "saved query page");
    if (raw.next !== null && (typeof raw.next !== "string" || raw.next === "")) malformed("saved query next");
    return Object.freeze({ items: Object.freeze(array(raw.items, "saved queries").map(savedQuery)), next: raw.next });
  }
  /** @param {string} id @param {V1RequestOptions} [options] @returns {Promise<V1SavedQuery>} */
  async get(id, options = {}) {
    return savedQuery((await this.#transport.call("getSavedQuery", { query: requireId(id) }, { signal: options.signal })).value);
  }
  /** @param {string} id @param {V1SavedQueryRequest} request @param {V1RequestOptions} [options] @returns {Promise<V1SavedQuery>} */
  async update(id, request, options = {}) {
    mutationOptions(options);
    return savedQuery((await this.#transport.call("updateSavedQuery", { query: requireId(id) }, { body: body(request), signal: options.signal })).value);
  }
  /** @param {string} id @param {V1RequestOptions} [options] @returns {Promise<void>} */
  async delete(id, options = {}) {
    mutationOptions(options);
    await this.#transport.call("deleteSavedQuery", { query: requireId(id) }, { signal: options.signal });
  }
}
