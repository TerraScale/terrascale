import { requireDatabaseSelector, requireName } from "./ids.js";
import { list, numeric, optional, nullable, record, text, dictionary } from "./management-decode.js";
import { members, mutationOptions } from "./management-validation.js";
import { stringifyExactJson } from "./json.js";
/** @import { V1Transport } from "./transport.js" */
/** @import { V1RequestOptions } from "./terrabase-v1.js" */
const constraint = record({ name: text, fields: list(text), normalization: text });
const schema = record({ collection: text, documents: numeric, sampledDocuments: numeric,
  fields: list(record({ path: text, present: numeric, types: dictionary(numeric) })), uniqueConstraints: optional(nullable(list(constraint))) });
const collections = record({ items: list(record({ name: text, documents: numeric, bytes: numeric })) });
const unique = record({ constraints: list(constraint) });
const shapes = record({ items: list(record({ id: text, collection: text, shape: text, queries: numeric,
  totalLatencyMs: numeric, maxLatencyMs: numeric, rowsScanned: numeric, lastSeenAt: text })) });
/** @typedef {ReturnType<typeof schema>} V1InferredSchema */
/** @typedef {ReturnType<typeof collections>} V1DatabaseSchema */
/** @typedef {ReturnType<typeof unique>} V1UniqueSchema */
/** @typedef {ReturnType<typeof shapes>} V1QueryShapes */
/** @typedef {"none" | "lowercase" | "trim_lowercase"} V1Normalization */
/** @typedef {{ readonly fields?: readonly { readonly name: string; readonly unique?: boolean; readonly normalization?: V1Normalization | null }[] | null; readonly indexes?: readonly { readonly name: string; readonly fields: readonly string[]; readonly unique?: boolean; readonly normalization?: V1Normalization | null }[] | null }} V1UniqueSchemaRequest */

/** Inferred collection fields, declared uniqueness, and observed query statistics. */
export class V1DatabaseSchemaClient {
  /** @type {V1Transport} */
  #transport;
  /** @type {{database: string}} */
  #params;
  /** @param {V1Transport} transport @param {string} database */
  constructor(transport, database) { this.#transport = transport; this.#params = { database: requireDatabaseSelector(database) }; Object.freeze(this); }
  /** @param {V1RequestOptions} [options] @returns {Promise<V1DatabaseSchema>} */
  async list(options = {}) { return collections((await this.#transport.call("getDatabaseSchema", this.#params, { signal: options.signal })).value); }
  /** @param {string} collection @param {V1RequestOptions} [options] @returns {Promise<V1InferredSchema>} */
  async infer(collection, options = {}) {
    return schema((await this.#transport.call("inferCollectionSchema", { ...this.#params, collection: requireName("collection", collection) }, { signal: options.signal })).value);
  }
  /** @param {string} collection @param {V1UniqueSchemaRequest} request @param {V1RequestOptions} [options] @returns {Promise<V1UniqueSchema>} */
  async declare(collection, request, options = {}) {
    members(request, ["fields", "indexes"]);
    mutationOptions(options);
    return unique((await this.#transport.call("declareCollectionSchema", { ...this.#params, collection: requireName("collection", collection) }, {
      body: stringifyExactJson(request, "unique schema"), signal: options.signal,
    })).value);
  }
  /** @param {V1RequestOptions} [options] @returns {Promise<V1QueryShapes>} */
  async queryShapes(options = {}) { return shapes((await this.#transport.call("listQueryShapes", this.#params, { signal: options.signal })).value); }
}
