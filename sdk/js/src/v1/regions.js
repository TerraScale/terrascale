import { requireId } from "../ids.js";
import { requireDatabaseSelector } from "./ids.js";
import { region } from "./catalog.js";
import { list, numeric, optional, record, text } from "./management-decode.js";
/** @import { V1Transport } from "./transport.js" */
/** @import { V1RequestOptions } from "./terrabase-v1.js" */

const replicaStatus = record({ region: region, lagMs: optional(numeric), lastPulledAt: optional(text),
  lastVerified: optional(text), revision: optional(text) });
const databaseRegions = record({ databaseId: requireId, homeRegion: region, observedAt: text, replicas: list(replicaStatus) });
/** @typedef {ReturnType<typeof databaseRegions>} V1DatabaseRegions */

/** Reads a database's home region and the home-observed replica pull progress. */
export class V1DatabaseRegionsClient {
  /** @type {V1Transport} */
  #transport;
  /** @type {{database: string}} */
  #params;
  /** @param {V1Transport} transport @param {string} database */
  constructor(transport, database) { this.#transport = transport; this.#params = { database: requireDatabaseSelector(database) }; Object.freeze(this); }
  /** @param {V1RequestOptions} [options] @returns {Promise<V1DatabaseRegions>} */
  async regions(options = {}) {
    return databaseRegions((await this.#transport.call("getDatabaseRegions", this.#params, { signal: options.signal })).value);
  }
}
