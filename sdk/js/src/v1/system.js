import { requireId } from "../ids.js";
import { region } from "./catalog.js";
import { list, nullable, optional, record, text } from "./management-decode.js";
import { V1Transport } from "./transport.js";
/** @import { V1RequestOptions } from "./terrabase-v1.js" */
// Public probes omit build metadata; only the internal listener emits it.
const status = record({ status: text, version: text, region: text,
  storageFormat: optional(nullable(text)), revision: optional(nullable(text)), machine: optional(nullable(text)) });
const databases = record({ items: list(record({ name: text, id: requireId, homeRegion: text })) });
/** @typedef {ReturnType<typeof status> & {readonly httpStatus: number}} V1ProbeStatus */
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
}
/** The public read-only explore API requires a user JWT carrying platform_role=admin. */
export class V1Platform {
  /** @type {V1Transport} */
  #transport;
  /** @param {V1Transport} transport */
  constructor(transport) { this.#transport = transport; Object.freeze(this); }
  /** @param {V1RequestOptions} [options] @returns {Promise<V1PlatformDatabases>} */
  async databases(options = {}) { return databases((await this.#transport.call("listPlatformDatabases", {}, { signal: options.signal })).value); }
}
/** The anonymous serving-region catalog; every region answers with the same ordered ids. */
export class V1Regions {
  /** @type {V1Transport} */
  #transport;
  /** @param {V1Transport} transport */
  constructor(transport) { this.#transport = transport; Object.freeze(this); }
  /** @param {V1RequestOptions} [options] @returns {Promise<{ readonly regions: readonly string[] }>} */
  async list(options = {}) {
    const response = await this.#transport.call("listRegions", {}, { signal: options.signal });
    const regions = record({ regions: list(text) })(response.value).regions.map(region);
    if (regions.length === 0 || regions.slice(1).some((id, index) => (regions[index] ?? "") >= id))
      throw new TypeError("The region catalog must be a nonempty ordered list of region ids.");
    return Object.freeze({ regions: Object.freeze(regions) });
  }
}
/** @param {TerraBaseSystemClientConfig} config */
export function createTerraBaseSystemClient(config) {
  const transport = new V1Transport(config);
  return Object.freeze({ health: new V1Health(transport), platform: new V1Platform(transport), regions: new V1Regions(transport) });
}
