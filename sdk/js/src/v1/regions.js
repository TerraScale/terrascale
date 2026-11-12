import { requireId } from "../ids.js";
import { requireDatabaseSelector } from "./ids.js";
import { region } from "./catalog.js";
import { list, nullable, numeric, optional, record, text } from "./management-decode.js";
/** @import { V1Transport } from "./transport.js" */
/** @import { V1RequestOptions } from "./terrabase-v1.js" */

const replicaStatus = record({ region: region, role: optional(nullable(text)), lagMs: optional(nullable(numeric)), lastPulledAt: optional(nullable(text)),
  lastVerified: optional(nullable(text)), revision: optional(nullable(text)) });
const databaseRegions = record({ databaseId: requireId, observedAt: text, regions: list(replicaStatus) });
/** @typedef {ReturnType<typeof databaseRegions>} V1DatabaseRegions */

/** Reads every selected region and writer-observed pull progress. */
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
