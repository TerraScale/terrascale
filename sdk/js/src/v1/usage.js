import { requireId } from "../ids.js";
import { requireDatabaseSelector } from "./ids.js";
import { dictionary, flag, list, nullable, numeric, record, text } from "./management-decode.js";
import { members } from "./management-validation.js";
/** @import { V1Transport } from "./transport.js" */
/** @import { V1RequestOptions } from "./terrabase-v1.js" */
const period = record({ from: text, to: text, granularity: text, reads: numeric, writes: numeric });
const series = list(record({ start: text, reads: numeric, writes: numeric }));
const databaseUsage = record({ databaseId: requireId, storageBytes: numeric, logicalBytes: numeric, collections: numeric,
  documents: numeric, reads: numeric, writes: numeric, period, series });
const projectUsage = record({ projectId: requireId, databases: numeric, storageBytes: numeric, logicalBytes: numeric, fileBytes: numeric,
  documents: numeric, reads: numeric, writes: numeric, period, series, items: list(databaseUsage) });
const bandwidth = record({ bytesSent: numeric, bytesReceived: numeric });
const directedBandwidth = list(record({ sourceRegion: text, destinationRegion: text, kind: text, bytesSent: numeric, bytesReceived: numeric }));
const latency = record({ p50Ms: nullable(numeric), p95Ms: nullable(numeric), p99Ms: nullable(numeric) });
const analytics = record({ projectId: requireId, range: text, from: text, to: text, bucketSeconds: numeric,
  items: list(record({ databaseId: requireId, databaseName: text, region: text,
    series: list(record({ start: text, requests: dictionary(numeric), operations: dictionary(numeric), errors: numeric,
      latency, storageBytes: nullable(numeric), clientBandwidth: bandwidth, replicationBandwidth: bandwidth })) })) });
const replication = record({ projectId: requireId, range: text, from: text, to: text, bucketSeconds: numeric,
  items: list(record({ databaseId: requireId, databaseName: text, region: text, status: text, replicatedOps: numeric,
    lagMs: nullable(numeric), divergence: nullable(flag), convergenceMs: nullable(numeric), lastApplied: nullable(text),
    bandwidth: directedBandwidth, series: list(record({ start: text, replicatedOps: numeric, bandwidth: directedBandwidth })) })) });
/** @typedef {ReturnType<typeof databaseUsage>} V1DatabaseUsage */
/** @typedef {ReturnType<typeof projectUsage>} V1ProjectUsage */
/** @typedef {ReturnType<typeof analytics>} V1Analytics */
/** @typedef {ReturnType<typeof replication>} V1Replication */
/** @typedef {{ readonly granularity?: "hour" | "day"; readonly from?: string; readonly to?: string }} V1UsageRequest */
/** @typedef {{ readonly range?: "1h" | "24h" | "7d" | "30d"; readonly database?: string; readonly region?: string }} V1AnalyticsRequest */
/** @param {V1UsageRequest} request */
function usageQuery(request) {
  members(request, ["granularity", "from", "to"]);
  const query = new URLSearchParams();
  if (request.granularity !== undefined) {
    if (!["hour", "day"].includes(request.granularity)) throw new TypeError("granularity must be hour or day.");
    query.set("granularity", request.granularity);
  }
  for (const name of /** @type {const} */ (["from", "to"])) {
    const value = request[name];
    if (value === undefined) continue;
    if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$/u.test(value) || !Number.isFinite(Date.parse(value)))
      throw new TypeError("from and to must be RFC 3339 instants.");
    query.set(name, value);
  }
  return query;
}
/** @param {V1AnalyticsRequest} request @param {boolean} project */
function analyticsQuery(request, project) {
  members(request, project ? ["range", "database", "region"] : ["range", "region"]);
  const query = new URLSearchParams();
  if (request.range !== undefined) {
    if (!["1h", "24h", "7d", "30d"].includes(request.range)) throw new TypeError("Invalid analytics range.");
    query.set("range", request.range);
  }
  if (request.database !== undefined) query.set("database", requireId(request.database));
  if (request.region !== undefined) {
    if (typeof request.region !== "string" || !/^[a-z][a-z0-9-]{0,62}$/u.test(request.region)) throw new TypeError("Invalid region.");
    query.set("region", request.region);
  }
  return query;
}

/** Project and database usage retain the producer's separate response envelopes. */
export class V1ProjectUsageClient {
  /** @type {V1Transport} */
  #transport;
  /** @param {V1Transport} transport */
  constructor(transport) { this.#transport = transport; Object.freeze(this); }
  /** @param {V1UsageRequest} [request] @param {V1RequestOptions} [options] @returns {Promise<V1ProjectUsage>} */
  async usage(request = {}, options = {}) {
    return projectUsage((await this.#transport.call("getProjectUsage", {}, { query: usageQuery(request), signal: options.signal })).value);
  }
  /** @param {V1AnalyticsRequest} [request] @param {V1RequestOptions} [options] @returns {Promise<V1Analytics>} */
  async analytics(request = {}, options = {}) {
    return analytics((await this.#transport.call("getProjectAnalytics", {}, { query: analyticsQuery(request, true), signal: options.signal })).value);
  }
  /** @param {V1AnalyticsRequest} [request] @param {V1RequestOptions} [options] @returns {Promise<V1Replication>} */
  async replication(request = {}, options = {}) {
    return replication((await this.#transport.call("getProjectReplication", {}, { query: analyticsQuery(request, true), signal: options.signal })).value);
  }
}
export class V1DatabaseUsageClient {
  /** @type {V1Transport} */
  #transport;
  /** @type {{database: string}} */
  #params;
  /** @param {V1Transport} transport @param {string} database */
  constructor(transport, database) { this.#transport = transport; this.#params = { database: requireDatabaseSelector(database) }; Object.freeze(this); }
  /** @param {V1UsageRequest} [request] @param {V1RequestOptions} [options] @returns {Promise<V1DatabaseUsage>} */
  async usage(request = {}, options = {}) {
    return databaseUsage((await this.#transport.call("getDatabaseUsage", this.#params, { query: usageQuery(request), signal: options.signal })).value);
  }
  /** @param {Omit<V1AnalyticsRequest, "database">} [request] @param {V1RequestOptions} [options] @returns {Promise<V1Analytics>} */
  async analytics(request = {}, options = {}) {
    return analytics((await this.#transport.call("getDatabaseAnalytics", this.#params, { query: analyticsQuery(request, false), signal: options.signal })).value);
  }
  /** @param {Omit<V1AnalyticsRequest, "database">} [request] @param {V1RequestOptions} [options] @returns {Promise<V1Replication>} */
  async replication(request = {}, options = {}) {
    return replication((await this.#transport.call("getDatabaseReplication", this.#params, { query: analyticsQuery(request, false), signal: options.signal })).value);
  }
}
