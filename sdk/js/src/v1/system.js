import { decodeDocument } from "./decode.js";
import { requireId } from "../ids.js";
import { requireDocumentId, requireName } from "./ids.js";
import { flag, list, nullable, numeric, optional, record, text } from "./management-decode.js";
import { members } from "./management-validation.js";
import { decodeCollection, decodeQueryPage } from "./terrabase-v1.js";
import { V1Transport } from "./transport.js";
/** @import { V1RequestOptions } from "./terrabase-v1.js" */
// Public probes omit build metadata; only the internal listener emits it.
const status = record({ status: text, version: text, region: text,
  storageFormat: optional(nullable(text)), revision: optional(nullable(text)), machine: optional(nullable(text)) });
const replication = record({ region: text, status: text, maxLagSeconds: nullable(numeric),
  peers: list(record({ region: text, status: text, lagSeconds: nullable(numeric) })) });
const databases = record({ items: list(record({ name: text, id: requireId, homeRegion: text, replicaRegions: list(text), allRegions: flag })) });
/** @typedef {ReturnType<typeof status> & {readonly httpStatus: number}} V1ProbeStatus */
/** @typedef {ReturnType<typeof replication>} V1ReplicationHealth */
/** @typedef {ReturnType<typeof databases>} V1PlatformDatabases */
/** @typedef {{ readonly baseUrl: string; readonly token?: string; readonly fetch?: import("../config.js").FetchLike; readonly maxResponseBytes?: number }} TerraBaseSystemClientConfig */

/** Public anonymous probes; 503 readiness/backup bodies remain typed probe outcomes. */
export class V1Health {
  /** @type {V1Transport} */
  #transport;
  /** @param {V1Transport} transport */
  constructor(transport) { this.#transport = transport; Object.freeze(this); }
  /** @param {V1RequestOptions} [options] @returns {Promise<V1ProbeStatus>} */
  async liveness(options = {}) {
    const response = await this.#transport.call("healthz", {}, { signal: options.signal });
    return Object.freeze({ ...status(response.value), httpStatus: response.status });
  }
  /** @param {V1RequestOptions} [options] @returns {Promise<V1ProbeStatus>} */
  async readiness(options = {}) {
    const response = await this.#transport.call("readyz", {}, { signal: options.signal });
    return Object.freeze({ ...status(response.value), httpStatus: response.status });
  }
  /** @param {V1RequestOptions} [options] @returns {Promise<V1ProbeStatus>} */
  async backup(options = {}) {
    const response = await this.#transport.call("backupz", {}, { signal: options.signal });
    return Object.freeze({ ...status(response.value), httpStatus: response.status });
  }
  /** @param {V1RequestOptions} [options] @returns {Promise<V1ReplicationHealth>} */
  async replication(options = {}) { return replication((await this.#transport.call("replicationHealth", {}, { signal: options.signal })).value); }
}
/** The public read-only explore API requires a user JWT carrying platform_role=admin. */
export class V1Platform {
  /** @type {V1Transport} */
  #transport;
  /** @param {V1Transport} transport */
  constructor(transport) { this.#transport = transport; Object.freeze(this); }
  /** @param {V1RequestOptions} [options] @returns {Promise<V1PlatformDatabases>} */
  async databases(options = {}) { return databases((await this.#transport.call("listPlatformDatabases", {}, { signal: options.signal })).value); }
  /** @param {"ts-auth" | "ts-dashboard"} database @param {V1RequestOptions} [options] */
  async collections(database, options = {}) {
    const response = await this.#transport.call("listPlatformCollections", { database: requireName("database", database) }, { signal: options.signal });
    return record({ items: list(decodeCollection), next_cursor: nullable(text) })(response.value);
  }
  /** @param {"ts-auth" | "ts-dashboard"} database @param {string} collection @param {{ readonly limit?: number; readonly cursor?: string }} [request] @param {V1RequestOptions} [options] */
  async documents(database, collection, request = {}, options = {}) {
    members(request, ["limit", "cursor"]);
    const query = new URLSearchParams();
    if (request.limit !== undefined) {
      if (!Number.isInteger(request.limit) || request.limit < 1 || request.limit > 1000) throw new TypeError("limit must be 1-1000.");
      query.set("limit", String(request.limit));
    }
    if (request.cursor !== undefined) {
      if (typeof request.cursor !== "string" || request.cursor === "") throw new TypeError("cursor must be non-empty.");
      query.set("cursor", request.cursor);
    }
    return decodeQueryPage((await this.#transport.call("listPlatformDocuments", { database: requireName("database", database), collection: requireName("collection", collection) }, { query, signal: options.signal })).value);
  }
  /** @param {"ts-auth" | "ts-dashboard"} database @param {string} collection @param {string} id @param {V1RequestOptions} [options] */
  async document(database, collection, id, options = {}) {
    return decodeDocument((await this.#transport.call("getPlatformDocument", { database: requireName("database", database), collection: requireName("collection", collection) }, {
      query: new URLSearchParams({ id: requireDocumentId(id) }), signal: options.signal,
    })).value);
  }
}
/** @param {TerraBaseSystemClientConfig} config */
export function createTerraBaseSystemClient(config) {
  const transport = new V1Transport(config);
  return Object.freeze({ health: new V1Health(transport), platform: new V1Platform(transport) });
}
