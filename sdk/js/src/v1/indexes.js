import { requireDatabaseSelector, requireName } from "./ids.js";
import { list, numeric, optional, record, text } from "./management-decode.js";
import { members, mutationOptions } from "./management-validation.js";
import { stringifyExactJson } from "./json.js";
/** @import { V1Transport } from "./transport.js" */
/** @import { V1RequestOptions } from "./terrabase-v1.js" */
const index = record({ name: text, collection: text, fields: list(text), state: text, kind: optional(text) });
const indexes = record({ items: list(index) });
const recommendations = record({ items: list(record({ id: text, collection: text, fields: list(text), reason: text, queries: numeric })) });
const applied = record({ status: text, index });
/** @typedef {ReturnType<typeof index>} V1Index */
/** @typedef {ReturnType<typeof indexes>} V1IndexList */
/** @typedef {ReturnType<typeof recommendations>} V1IndexRecommendations */
/** @typedef {ReturnType<typeof applied>} V1AppliedIndexRecommendation */
/** @typedef {"btree" | "text"} V1IndexKind */
/** @typedef {{ readonly collection: string; readonly fields: readonly string[]; readonly name?: string | null; readonly kind?: V1IndexKind }} V1CreateIndexRequest */
/** @param {unknown} value @returns {string} */
function indexName(value) {
  if (typeof value !== "string" || !/^[a-z][a-z0-9_]{0,62}$/u.test(value)) throw new TypeError("Invalid index name.");
  return value;
}

/** Secondary indexes and workload recommendations for one database. */
export class V1Indexes {
  /** @type {V1Transport} */
  #transport;
  /** @type {{database: string}} */
  #params;
  /** @param {V1Transport} transport @param {string} database */
  constructor(transport, database) { this.#transport = transport; this.#params = { database: requireDatabaseSelector(database) }; Object.freeze(this); }
  /** @param {V1CreateIndexRequest} request @param {V1RequestOptions} [options] @returns {Promise<V1Index>} */
  async create(request, options = {}) {
    members(request, ["collection", "fields", "name", "kind"]);
    if (request.kind !== undefined && request.kind !== "btree" && request.kind !== "text") throw new TypeError("kind must be btree or text.");
    requireName("collection", request.collection);
    if (!Array.isArray(request.fields) || request.fields.length < 1 || request.fields.length > 4 ||
        new Set(request.fields).size !== request.fields.length || request.fields.some(f => typeof f !== "string" || f === "_id" || f === "_rev" || !/^[A-Za-z_][A-Za-z0-9_]{0,63}(?:\.[A-Za-z_][A-Za-z0-9_]{0,63}){0,7}$/u.test(f)))
      throw new TypeError("fields must be 1-4 distinct dotted paths, excluding _id and _rev.");
    if (request.name !== undefined && request.name !== null) indexName(request.name);
    mutationOptions(options);
    return index((await this.#transport.call("createIndex", this.#params, { body: stringifyExactJson(request, "index"), signal: options.signal })).value);
  }
  /** @param {{ readonly collection?: string }} [request] @param {V1RequestOptions} [options] @returns {Promise<V1IndexList>} */
  async list(request = {}, options = {}) {
    members(request, ["collection"]);
    const query = new URLSearchParams();
    if (request.collection !== undefined) query.set("collection", requireName("collection", request.collection));
    return indexes((await this.#transport.call("listIndexes", this.#params, { query, signal: options.signal })).value);
  }
  /** @param {string} name @param {V1RequestOptions} [options] @returns {Promise<void>} */
  async drop(name, options = {}) {
    mutationOptions(options);
    await this.#transport.call("dropIndex", { ...this.#params, index: indexName(name) }, { signal: options.signal });
  }
  /** @param {V1RequestOptions} [options] @returns {Promise<V1IndexRecommendations>} */
  async recommendations(options = {}) { return recommendations((await this.#transport.call("listIndexRecommendations", this.#params, { signal: options.signal })).value); }
  /** @param {string} id Opaque producer recommendation identifier. @param {V1RequestOptions} [options] @returns {Promise<V1AppliedIndexRecommendation>} */
  async applyRecommendation(id, options = {}) {
    if (typeof id !== "string" || id === "") throw new TypeError("A recommendation identifier is required.");
    mutationOptions(options);
    return applied((await this.#transport.call("applyIndexRecommendation", { ...this.#params, recommendation: id }, { signal: options.signal })).value);
  }
}
