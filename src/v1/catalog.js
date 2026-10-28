import { requireId } from "../ids.js";
import { array, boolean, malformed, object, string } from "./decode.js";
import { requireDatabaseSelector, requireName } from "./ids.js";
import { isJsonPointer } from "./filter.js";
import { isJsonObject, stringifyExactJson } from "./json.js";
/**
 * @import { JsonInputObject, JsonObject, JsonValue } from "./json.js"
 * @import { V1Transport } from "./transport.js"
 * @import { V1RequestOptions } from "./terrabase-v1.js"
 */

/**
 * @typedef {object} V1ManagedDatabase
 * @property {string} id Canonical database ULID.
 * @property {string} projectId Canonical owning-project ULID.
 * @property {string} name Database name used in data routes.
 * @property {string | null | undefined} [previousName] Name lost in a cross-region conflict.
 * @property {string | null | undefined} [conflictOf] ULID of the database that kept that name.
 * @property {string | null} branchedFrom Source database ULID, or null.
 * @property {string} createdAt ISO date-time returned by the producer.
 * @property {string} region Serving region.
 * @property {string} homeRegion Management region.
 * @property {readonly string[]} replicaRegions Other serving regions; empty when allRegions is true.
 * @property {boolean} allRegions True when a full copy is served in every serving region, including later ones.
 */
/** @typedef {{ readonly name: string; readonly homeRegion?: string | null | undefined; readonly replicaRegions?: readonly string[] | null | undefined; readonly allRegions?: boolean | undefined }} V1DatabaseCreateRequest */
/** @typedef {{ readonly replicaRegions?: readonly string[] | undefined; readonly allRegions?: true | undefined; readonly homeRegion?: string | null | undefined }} V1DatabaseUpdateRequest */
/** @typedef {{ readonly name: string }} V1DatabaseBranchRequest */
/** @typedef {{ readonly after?: string | undefined; readonly limit?: number | undefined }} V1DatabaseListRequest */
/** @typedef {{ readonly items: readonly V1ManagedDatabase[]; readonly next: string | null }} V1DatabasePage */
/** @typedef {{ readonly schema: JsonObject | boolean | null; readonly dialect?: string | null | undefined }} V1CollectionSchema */
/** @typedef {{ readonly schemaHash: string; readonly frontier: string; readonly examined: number; readonly valid: number; readonly invalid: number; readonly complete: boolean; readonly nextCursor: string | null; readonly diagnostics: readonly {readonly idHash: string; readonly pathHash: string; readonly keyword: string}[] }} V1SchemaPreview */
/** The expiry member, or null when documents never expire. @typedef {{ readonly path: string | null }} V1CollectionTtl */

/** @param {unknown} value @param {readonly string[]} allowed @returns {void} */
function members(value, allowed) {
  if (typeof value !== "object" || value === null || Array.isArray(value) ||
      ![Object.prototype, null].includes(Object.getPrototypeOf(value)))
    throw new TypeError("A catalog request must be a plain object.");
  for (const key of Object.keys(value))
    if (!allowed.includes(key)) throw new TypeError(`Unknown catalog request member ${key}.`);
}

/** @param {object} options @returns {void} */
function mutationOptions(options) {
  if ("idempotencyKey" in options)
    throw new TypeError("The current producer does not support Idempotency-Key; mutations are never automatically retried.");
}

/** @param {unknown} value @returns {string} */
function region(value) {
  if (typeof value !== "string" || !/^[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?$/u.test(value))
    throw new TypeError("A region is 1-63 lowercase letters, digits or hyphens, starts with a letter and cannot end with a hyphen.");
  return value;
}

/** @param {unknown} value @param {string | null | undefined} home @returns {readonly string[]} */
function replicas(value, home) {
  if (!Array.isArray(value) || value.length > 8) throw new TypeError("replicaRegions is an array of at most 8 regions.");
  const result = value.map(region);
  if (new Set(result).size !== result.length) throw new TypeError("replicaRegions must not repeat a region.");
  if (home !== undefined && home !== null && result.includes(home))
    throw new TypeError("replicaRegions must not include homeRegion.");
  return result;
}

/** @param {JsonValue | undefined} value @returns {V1ManagedDatabase} */
function database(value) {
  const raw = object(value, "managed database");
  const createdAt = string(raw.createdAt, "database createdAt");
  if (!/^\d{4}-\d{2}-\d{2}T/u.test(createdAt) || !Number.isFinite(Date.parse(createdAt))) malformed("database createdAt");
  if (typeof raw.allRegions !== "boolean") malformed("database allRegions");
  return Object.freeze({
    id: requireId(raw.id, "Database ID"), projectId: requireId(raw.projectId, "Project ID"),
    name: requireName("database", raw.name),
    branchedFrom: raw.branchedFrom === null ? null : requireId(raw.branchedFrom, "Source database ID"),
    createdAt, region: region(raw.region), homeRegion: region(raw.homeRegion),
    replicaRegions: Object.freeze(array(raw.replicaRegions, "database replicaRegions").map(region)),
    allRegions: raw.allRegions,
    ...(raw.previousName === undefined ? {} : { previousName: raw.previousName === null ? null : requireName("database", raw.previousName) }),
    ...(raw.conflictOf === undefined ? {} : { conflictOf: raw.conflictOf === null ? null : requireId(raw.conflictOf, "Conflict database ID") }),
  });
}

/** Public database catalog operations, bound to the transport's authorized project. */
export class V1Databases {
  /** @type {V1Transport} */
  #transport;
  /** @param {V1Transport} transport */
  constructor(transport) { this.#transport = transport; Object.freeze(this); }

  /**
   * Creates a database managed in homeRegion, or the called region when omitted.
   * A writable database requires at least two regions. Omitting replicaRegions
   * uses every other current serving region; an explicit list must include at
   * least one region other than the home. An empty list on create returns
   * 400 placement_needs_two_regions.
   * allRegions: true also follows regions that join later and cannot be combined
   * with replicaRegions. The home selects management routing, not single-region placement.
   * @param {V1DatabaseCreateRequest} request @param {V1RequestOptions} [options] @returns {Promise<V1ManagedDatabase>}
   */
  async create(request, options = {}) {
    members(request, ["name", "homeRegion", "replicaRegions", "allRegions"]);
    mutationOptions(options);
    const name = requireName("database", request.name);
    if (request.homeRegion !== undefined && request.homeRegion !== null) region(request.homeRegion);
    if (request.replicaRegions !== undefined && request.replicaRegions !== null) replicas(request.replicaRegions, request.homeRegion);
    if (request.allRegions !== undefined && typeof request.allRegions !== "boolean") throw new TypeError("allRegions must be a boolean.");
    if (request.allRegions === true && request.replicaRegions !== undefined && request.replicaRegions !== null)
      throw new TypeError("allRegions cannot be combined with replicaRegions.");
    const response = await this.#transport.call("createDatabase", { database: name }, {
      body: stringifyExactJson({ name,
        ...(request.homeRegion === undefined ? {} : { homeRegion: request.homeRegion }),
        ...(request.replicaRegions === undefined ? {} : { replicaRegions: request.replicaRegions }),
        ...(request.allRegions === undefined ? {} : { allRegions: request.allRegions }),
      }, "database create"), signal: options.signal,
    });
    return database(response.value);
  }

  /** @param {V1DatabaseListRequest} [request] @param {V1RequestOptions} [options] @returns {Promise<V1DatabasePage>} */
  async list(request = {}, options = {}) {
    members(request, ["after", "limit"]);
    const query = new URLSearchParams();
    if (request.after !== undefined) {
      if (typeof request.after !== "string") throw new TypeError("Database after must be a string.");
      query.set("after", request.after);
    }
    if (request.limit !== undefined) {
      if (!Number.isInteger(request.limit) || request.limit < 1 || request.limit > 100) throw new TypeError("Database list limit is 1-100.");
      query.set("limit", String(request.limit));
    }
    const response = await this.#transport.call("listDatabases", {}, { query, signal: options.signal });
    const raw = object(response.value, "database page");
    if (raw.next !== null && (typeof raw.next !== "string" || raw.next === "")) malformed("database next");
    return Object.freeze({ items: Object.freeze(array(raw.items, "databases").map(database)), next: raw.next });
  }

  /** @param {string} name @param {V1RequestOptions} [options] @returns {Promise<V1ManagedDatabase>} */
  async get(name, options = {}) {
    const response = await this.#transport.call("getDatabase", { database: requireDatabaseSelector(name) }, { signal: options.signal });
    return database(response.value);
  }

  /**
   * Rename by immutable ULID or current name with databases:write. See docs/database-rename.md.
   * Returns the database metadata committed by its home region.
   * ID, data, keys and placement stay unchanged. Invalid names return 400 (or a
   * local TypeError); known same-region duplicates return 409. After convergence,
   * the old name has no alias and may be claimed by another database. Update
   * name-based connection strings and handles; ID-based strings stay valid. Reconcile uncertain failures before explicitly retrying.
   * @param {string} databaseId
   * @param {string} name
   * @param {V1RequestOptions} [options]
   * @returns {Promise<V1ManagedDatabase>}
   */
  async rename(databaseId, name, options = {}) {
    mutationOptions(options);
    const response = await this.#transport.call("updateDatabase", { database: requireDatabaseSelector(databaseId) }, {
      body: stringifyExactJson({ name: requireName("database", name) }, "database rename"), signal: options.signal,
    });
    return database(response.value);
  }

  /**
   * Replaces the replica list from the database's home region, which cannot change.
   * An explicit list turns allRegions off. A writable database needs at least
   * one replica besides the home. An empty list can temporarily make document
   * writes return 409 placement_needs_two_regions; it is not required for
   * deletion and does not provide supported single-region placement.
   * allRegions: true instead includes every serving region and regions that join later.
   * @param {string} name @param {V1DatabaseUpdateRequest} request @param {V1RequestOptions} [options] @returns {Promise<V1ManagedDatabase>}
   */
  async update(name, request, options = {}) {
    members(request, ["homeRegion", "replicaRegions", "allRegions"]);
    mutationOptions(options);
    if (request.homeRegion !== undefined && request.homeRegion !== null) region(request.homeRegion);
    if (request.allRegions === true) {
      if (request.replicaRegions !== undefined) throw new TypeError("allRegions cannot be combined with replicaRegions.");
    } else {
      if (request.allRegions !== undefined) throw new TypeError("allRegions must be true; send replicaRegions to turn it off.");
      replicas(request.replicaRegions, request.homeRegion);
    }
    const response = await this.#transport.call("updateDatabase", { database: requireDatabaseSelector(name) }, {
      body: stringifyExactJson({
        ...(request.allRegions === true ? { allRegions: true } : { replicaRegions: request.replicaRegions }),
        ...(request.homeRegion === undefined ? {} : { homeRegion: request.homeRegion }),
      }, "database update"), signal: options.signal,
    });
    return database(response.value);
  }

  /**
   * Deletes directly in the home region without a preceding placement update.
   * Permanent purge authority removes every replica, including all-regions placement;
   * branches remain independent. A 204 response records logical deletion and local
   * cleanup; remote erasure is retried durably, not guaranteed synchronous.
   * @param {string} name @param {V1RequestOptions} [options] @returns {Promise<void>}
   */
  async delete(name, options = {}) {
    mutationOptions(options);
    await this.#transport.call("deleteDatabase", { database: requireDatabaseSelector(name) }, { signal: options.signal });
  }

  /** @param {string} name @param {V1DatabaseBranchRequest} request @param {V1RequestOptions} [options] @returns {Promise<V1ManagedDatabase>} */
  async branch(name, request, options = {}) {
    members(request, ["name"]);
    mutationOptions(options);
    const response = await this.#transport.call("branchDatabase", { database: requireDatabaseSelector(name) }, {
      body: stringifyExactJson({ name: requireName("database", request.name) }, "database branch"), signal: options.signal,
    });
    return database(response.value);
  }
}

/** @param {JsonValue | undefined} value @returns {V1CollectionSchema} */
function collectionSchema(value) {
  const raw = object(value, "collection schema");
  const schema = raw.schema;
  if (schema !== null && typeof schema !== "boolean" && !isJsonObject(schema)) malformed("declared schema");
  const dialect = raw.dialect;
  if (dialect !== undefined && dialect !== null && typeof dialect !== "string") malformed("schema dialect");
  return Object.freeze({ schema, ...(dialect === undefined ? {} : { dialect }) });
}

/** @param {boolean | JsonInputObject} schema @returns {string} */
function schemaText(schema) {
  if (typeof schema !== "boolean" && !isJsonObject(schema)) throw new TypeError("A declared schema must be a JSON object or boolean.");
  const body = stringifyExactJson(schema, "declared schema");
  if (new TextEncoder().encode(body).byteLength > 64 * 1024) throw new TypeError("A declared schema is at most 64 KiB.");
  let nodes = 0;
  /** @param {unknown} value @param {number} depth */
  const visit = (value, depth) => {
    if (++nodes > 4093) throw new TypeError("A declared schema holds at most 4093 values.");
    if (!Array.isArray(value) && !isJsonObject(value)) return;
    if (depth >= 15) throw new TypeError("A declared schema nests at most 15 levels.");
    for (const child of Object.values(value)) visit(child, depth + 1);
  };
  visit(schema, 0);
  return body;
}

/** @param {V1Transport} transport @param {Readonly<Record<string, string>>} params @param {V1RequestOptions} [options] @returns {Promise<V1CollectionSchema>} */
export async function getCollectionSchema(transport, params, options = {}) {
  return collectionSchema((await transport.call("getCollectionSchema", params, { signal: options.signal })).value);
}

/** @param {V1Transport} transport @param {Readonly<Record<string, string>>} params @param {boolean | JsonInputObject} schema @param {V1RequestOptions} [options] @returns {Promise<V1CollectionSchema>} */
export async function putCollectionSchema(transport, params, schema, options = {}) {
  mutationOptions(options);
  return collectionSchema((await transport.call("putCollectionSchema", params, {
    body: schemaText(schema), signal: options.signal,
  })).value);
}

/**
 * Observes one bounded page; continuation needs identical schema bytes and the same process/frontier.
 * A preview does not reserve a schema change. Applying a schema remains prospective.
 * @param {V1Transport} transport @param {Readonly<Record<string, string>>} params
 * @param {boolean | JsonInputObject} schema @param {{readonly cursor?: string}} [request]
 * @param {V1RequestOptions} [options] @returns {Promise<V1SchemaPreview>}
 */
export async function previewCollectionSchema(transport, params, schema, request = {}, options = {}) {
  members(request, ["cursor"]);
  const query = new URLSearchParams();
  if (request.cursor !== undefined) {
    if (typeof request.cursor !== "string" || request.cursor.length === 0 || request.cursor.length > 2048)
      throw new TypeError("Schema preview cursor must be 1-2048 characters.");
    query.set("cursor", request.cursor);
  }
  const raw = object((await transport.call("previewCollectionSchema", params, {
    body: schemaText(schema), query, signal: options.signal,
  })).value, "schema preview");
  /** @param {JsonValue | undefined} value */
  const hash = value => {
    if (typeof value !== "string" || !/^[a-f0-9]{64}$/u.test(value)) return malformed("preview SHA-256");
    return value;
  };
  /** @param {JsonValue | undefined} value */
  const count = value => {
    if (typeof value === "string" && /^(?:0|[1-9]\d*)$/u.test(value)) value = Number(value);
    if (typeof value !== "number" || !Number.isInteger(value) || value < 0 || value > 100) return malformed("preview page count");
    return value;
  };
  const examined = count(raw.examined), valid = count(raw.valid), invalid = count(raw.invalid);
  const complete = boolean(raw.complete, "preview complete");
  const nextCursor = raw.next_cursor === null ? null : string(raw.next_cursor, "preview cursor");
  const diagnostics = array(raw.diagnostics, "preview diagnostics").map(value => {
    const diagnostic = object(value, "preview diagnostic");
    return Object.freeze({ idHash: hash(diagnostic.id_hash), pathHash: hash(diagnostic.path_hash), keyword: string(diagnostic.keyword, "schema keyword") });
  });
  if (valid + invalid !== examined || diagnostics.length > 16 || complete !== (nextCursor === null)) malformed("preview page");
  return Object.freeze({ schemaHash: hash(raw.schema_hash), frontier: hash(raw.frontier), examined, valid, invalid,
    complete, nextCursor, diagnostics: Object.freeze(diagnostics) });
}

/** @param {V1Transport} transport @param {Readonly<Record<string, string>>} params @param {V1RequestOptions} [options] @returns {Promise<V1CollectionSchema>} */
export async function deleteCollectionSchema(transport, params, options = {}) {
  mutationOptions(options);
  return collectionSchema((await transport.call("deleteCollectionSchema", params, { signal: options.signal })).value);
}

/** @param {JsonValue | undefined} value @returns {V1CollectionTtl} */
function collectionTtl(value) {
  const path = object(value, "collection TTL").path;
  if (path !== null && typeof path !== "string") malformed("TTL path");
  return Object.freeze({ path });
}

/** @param {V1Transport} transport @param {Readonly<Record<string, string>>} params @param {V1RequestOptions} [options] @returns {Promise<V1CollectionTtl>} */
export async function getCollectionTtl(transport, params, options = {}) {
  return collectionTtl((await transport.call("getCollectionTtl", params, { signal: options.signal })).value);
}

/** @param {V1Transport} transport @param {Readonly<Record<string, string>>} params @param {string} path @param {V1RequestOptions} [options] @returns {Promise<V1CollectionTtl>} */
export async function putCollectionTtl(transport, params, path, options = {}) {
  mutationOptions(options);
  if (!isJsonPointer(path))
    throw new TypeError("A TTL path is a JSON Pointer of 1 to 16 object members.");
  return collectionTtl((await transport.call("putCollectionTtl", params, {
    body: stringifyExactJson({ path }, "collection TTL"), signal: options.signal,
  })).value);
}

/** @param {V1Transport} transport @param {Readonly<Record<string, string>>} params @param {V1RequestOptions} [options] @returns {Promise<V1CollectionTtl>} */
export async function deleteCollectionTtl(transport, params, options = {}) {
  mutationOptions(options);
  return collectionTtl((await transport.call("deleteCollectionTtl", params, { signal: options.signal })).value);
}
