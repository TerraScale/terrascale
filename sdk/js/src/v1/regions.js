import { requireId } from "../ids.js";
import { requireDatabaseSelector } from "./ids.js";
import { region } from "./catalog.js";
import { flag, list, numeric, record, text } from "./management-decode.js";
/** @import { V1Transport } from "./transport.js" */
/** @import { V1RequestOptions } from "./terrabase-v1.js" */

const regionReplication = record({ region: region, appliedBookmark: text, lagMs: numeric, healthy: flag });
const databaseRegions = record({ databaseId: requireId, observedAtUtc: text, regions: list(regionReplication) });
/** @typedef {ReturnType<typeof databaseRegions>} V1DatabaseRegions */

/** Reads every peer region of a database: each accepts reads and writes and reports the bookmark it has applied. */
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
