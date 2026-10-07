// The TerraBase v1 client: documents, transactions, queries and shapes over
// the project-scoped C# Documents API (decision 0004 and shape-reads.md).
import {
  array, boolean, decodeDocument, decodeWrite, durability, malformed, object, u64,
} from "./decode.js";
import { isJsonPointer, requireFilter, requirePath } from "./filter.js";
import { requireDocumentId, requireName, requireVersion } from "./ids.js";
import { assertJsonObject, assertJsonValue, ExactDecimal, stringifyExactJson } from "./json.js";
import { readShape } from "./shapes.js";
import { V1Transport } from "./transport.js";
/**
 * @import { V1Document, V1Durability, V1WriteResult } from "./decode.js"
 * @import { V1Filter, V1Path } from "./filter.js"
 * @import { JsonInput, JsonInputObject, JsonValue } from "./json.js"
 * @import { V1ShapePage, V1ShapeRequest } from "./shapes.js"
 * @import { TerraBaseV1ClientConfig } from "./transport.js"
 */

/** @typedef {{ readonly signal?: AbortSignal | undefined }} V1RequestOptions */
/**
 * @typedef {object} V1ReadOptions
 * @property {string | undefined} [ifNoneMatch] A version; an unchanged document fails with `not_modified` (304).
 * @property {AbortSignal | undefined} [signal]
 */
/**
 * @typedef {object} V1WriteOptions
 * @property {string | undefined} [ifMatch] Require the current version, or "*" for a live document.
 * @property {boolean | undefined} [ifNoneMatchAny] Require the document to be absent or deleted (`If-None-Match: *`).
 * @property {AbortSignal | undefined} [signal]
 */
/** @typedef {V1WriteOptions} V1CreateOptions */
/** @typedef {{ readonly ifMatch?: string | undefined; readonly signal?: AbortSignal | undefined }} V1ConditionalWriteOptions */

/**
 * One operation of an `application/vnd.terrabase.ops+json` patch. Paths are
 * RFC 6901 JSON Pointers without array indices.
 * @typedef {{ readonly op: "set"; readonly path: string; readonly value: JsonInput }
 *   | { readonly op: "unset"; readonly path: string }
 *   | { readonly op: "inc"; readonly path: string; readonly by: number | bigint | ExactDecimal }
 *   | { readonly op: "append"; readonly path: string; readonly value: JsonInput }} V1PatchOp
 */
/**
 * A patch: an RFC 7396 merge patch (`null` removes members) or 1-256 ops.
 * @typedef {{ readonly merge: JsonInputObject } | { readonly ops: readonly V1PatchOp[] }} V1Patch
 */

/** @typedef {{ readonly path: V1Path; readonly order?: "asc" | "desc" | undefined }} V1SortKey */
/**
 * A query-v1 request. Every member is optional.
 * @typedef {object} V1Query
 * @property {V1Filter | null | undefined} [filter]
 * @property {readonly V1SortKey[] | undefined} [sort] At most 4 keys; results are finally ordered by `$id`.
 * @property {readonly string[] | undefined} [project] At most 64 paths.
 * @property {number | undefined} [limit] 1-1000 (server default 50).
 * @property {string | null | undefined} [cursor] A previous page's `nextCursor`.
 * @property {boolean | undefined} [count] Count every match (first page only).
 */
/** @typedef {{ readonly value: string; readonly exact: boolean }} V1Count */
/**
 * @typedef {object} V1QueryPage
 * @property {readonly V1Document[]} items Documents in query order.
 * @property {string | null} nextCursor
 * @property {V1Count | undefined} count
 */

/** @typedef {{ readonly ifMatch: string } | { readonly ifNoneMatchAny: true }} V1Precondition */
/**
 * One op's result: `id` and `version` for writes, `asserted` for asserts, and
 * also `count` (decimal u64) for `assertCount`.
 * @typedef {{ readonly id: string; readonly version: string } | { readonly asserted: true; readonly count?: string }} V1TransactionOpResult
 */
/** @typedef {{ readonly gte?: number | undefined; readonly lte?: number | undefined }} V1CountBounds */
/**
 * @typedef {object} V1TransactionResult
 * @property {string} offset
 * @property {V1Durability} durability
 * @property {readonly V1TransactionOpResult[]} results One per op, in order.
 */

const queryMembers = new Set(["filter", "sort", "project", "limit", "cursor", "count"]);

/**
 * @param {string} path
 * @returns {string}
 */
function pointer(path) {
  if (!isJsonPointer(path)) throw new TypeError("Paths are RFC 6901 JSON Pointers (only ~0 and ~1 escapes).");
  return path;
}

/**
 * @param {V1PatchOp} op
 * @returns {Record<string, JsonInput>}
 */
function patchOp(op) {
  if (typeof op !== "object" || op === null || Array.isArray(op)) throw new TypeError("A patch op is an object.");
  let allowed = ["op", "path", "value"];
  switch (op.op) {
    case "unset": allowed = ["op", "path"]; break;
    case "inc": allowed = ["op", "path", "by"]; break;
  }
  for (const key of Object.keys(op)) if (!allowed.includes(key)) throw new TypeError(`Unknown patch op member ${key}.`);
  switch (op.op) {
    case "set":
    case "append":
      assertJsonValue(op.value, "patch value");
      return { op: op.op, path: pointer(op.path), value: op.value };
    case "unset":
      return { op: "unset", path: pointer(op.path) };
    case "inc": {
      const by = op.by;
      if (!(typeof by === "bigint" || by instanceof ExactDecimal || (typeof by === "number" && Number.isFinite(by))))
        throw new TypeError("inc takes a finite number, bigint or ExactDecimal.");
      return { op: "inc", path: pointer(op.path), by };
    }
    default:
      throw new TypeError("Patch ops are set, unset, inc or append.");
  }
}

/**
 * @param {V1Patch} patch
 * @returns {{ merge: JsonInputObject } | { ops: Record<string, JsonInput>[] }}
 */
function normalizePatch(patch) {
  if (typeof patch !== "object" || patch === null || Array.isArray(patch)) throw new TypeError("A patch is { merge } or { ops }.");
  for (const key of Object.keys(patch)) if (key !== "merge" && key !== "ops") throw new TypeError(`Unknown patch member ${key}.`);
  const hasMerge = Object.hasOwn(patch, "merge");
  const hasOps = Object.hasOwn(patch, "ops");
  if (hasMerge === hasOps) throw new TypeError("A patch has exactly one of merge or ops.");
  // Branch on the same own-property test that was validated, never on an
  // inherited member.
  if (hasMerge) {
    const merge = /** @type {{ readonly merge: JsonInputObject }} */ (patch).merge;
    assertJsonObject(merge, "merge patch");
    return { merge };
  }
  const ops = /** @type {{ readonly ops: readonly V1PatchOp[] }} */ (patch).ops;
  if (!Array.isArray(ops) || ops.length < 1 || ops.length > 256)
    throw new TypeError("An ops patch has 1-256 ops.");
  return { ops: ops.map(patchOp) };
}

/**
 * @param {V1Query} query
 * @returns {string}
 */
function encodeQuery(query) {
  if (typeof query !== "object" || query === null || Array.isArray(query)) throw new TypeError("A query is an object.");
  for (const key of Object.keys(query))
    if (!queryMembers.has(key)) throw new TypeError(`Unknown query member ${key}.`);
  /** @type {Record<string, unknown>} */
  const body = {};
  if (query.filter !== undefined) body.filter = query.filter === null ? null : requireFilter(query.filter);
  if (query.sort !== undefined) {
    if (!Array.isArray(query.sort) || query.sort.length > 4) throw new TypeError("sort has at most 4 keys.");
    body.sort = query.sort.map((key) => {
      if (key === null || typeof key !== "object") throw new TypeError("Sort keys are objects.");
      for (const member of Object.keys(key)) if (member !== "path" && member !== "order") throw new TypeError(`Unknown sort member ${member}.`);
      if (key.order !== undefined && key.order !== "asc" && key.order !== "desc") throw new TypeError('Sort order is "asc" or "desc".');
      return { path: requirePath(key.path), ...(key.order === undefined ? {} : { order: key.order }) };
    });
  }
  if (query.project !== undefined) {
    if (!Array.isArray(query.project) || query.project.length > 64) throw new TypeError("project lists at most 64 paths.");
    body.project = query.project.map(requirePath);
  }
  if (query.limit !== undefined) {
    if (!Number.isInteger(query.limit) || query.limit < 1 || query.limit > 1000) throw new TypeError("limit is 1-1000.");
    body.limit = query.limit;
  }
  if (query.cursor !== undefined) {
    if (query.cursor !== null && (typeof query.cursor !== "string" || query.cursor === "")) throw new TypeError("cursor is a string or null.");
    body.cursor = query.cursor;
  }
  if (query.count !== undefined) {
    if (typeof query.count !== "boolean") throw new TypeError("count is a boolean.");
    body.count = query.count;
  }
  return stringifyExactJson(body, "query");
}

/**
 * @param {JsonValue | undefined} value
 * @returns {V1QueryPage}
 */
function decodeQueryPage(value) {
  const raw = object(value, "query response");
  const next = raw.next_cursor;
  if (next !== null && (typeof next !== "string" || next === "")) malformed("next_cursor");
  /** @type {V1Count | undefined} */
  let count;
  if (raw.count !== undefined) {
    const rawCount = object(raw.count, "count");
    count = Object.freeze({ value: u64(rawCount.value, "count value"), exact: boolean(rawCount.exact, "count exact flag") });
  }
  return Object.freeze({
    items: Object.freeze(array(raw.items, "query items").map(decodeDocument)),
    nextCursor: typeof next === "string" && next !== "" ? next : null,
    count,
  });
}

/**
 * @param {{ readonly ifMatch?: string | undefined; readonly ifNoneMatchAny?: boolean | undefined }} options
 * @param {boolean} allowNoneMatch
 * @returns {Record<string, string>}
 */
function preconditionHeaders(options, allowNoneMatch) {
  /** @type {Record<string, string>} */
  const headers = {};
  rejectIdempotency(options);
  if (options.ifNoneMatchAny !== undefined && typeof options.ifNoneMatchAny !== "boolean") throw new TypeError("ifNoneMatchAny is a boolean.");
  if (options.ifMatch !== undefined && options.ifNoneMatchAny === true)
    throw new TypeError("ifMatch and ifNoneMatchAny are mutually exclusive.");
  if (options.ifMatch !== undefined) headers["if-match"] = options.ifMatch === "*" ? "*" : `"${requireVersion(options.ifMatch)}"`;
  if (options.ifNoneMatchAny === true) {
    if (!allowNoneMatch) throw new TypeError("This operation does not take ifNoneMatchAny.");
    headers["if-none-match"] = "*";
  }
  return headers;
}

/** @param {object} options @returns {void} */
function rejectIdempotency(options) {
  if ("idempotencyKey" in options) throw new TypeError("The current producer does not support Idempotency-Key; mutations are never automatically retried.");
}
/** @param {JsonInputObject} document @returns {string} */
function encodeDocument(document) {
  assertJsonObject(document, "document");
  const body = stringifyExactJson(document, "document");
  if (new TextEncoder().encode(body).byteLength > 1024 * 1024) throw new TypeError("A document is at most 1 MiB.");
  return body;
}

/** A collection handle by name. It performs no I/O until an operation runs. */
export class V1Collection {
  /** @type {V1Transport} */
  #transport;
  /** @type {Readonly<Record<string, string>>} */
  #params;
  /** @type {string} */
  name;
  /** @type {string} */
  database;

  /**
   * @param {V1Transport} transport
   * @param {string} database
   * @param {string} name
   */
  constructor(transport, database, name) {
    this.#transport = transport;
    this.database = requireName("database", database);
    this.name = requireName("collection", name);
    this.#params = Object.freeze({ database: this.database, collection: this.name });
    Object.freeze(this);
  }

  /**
   * @param {string} id
   * @returns {Readonly<Record<string, string>>}
   */
  #document(id) {
    return { ...this.#params, id: requireDocumentId(id) };
  }

  /**
   * Reads a document. Absent and deleted documents fail with
   * `not_found`; a deleted document's error carries its tombstone `etag`.
   * @param {string} id
   * @param {V1ReadOptions} [options]
   * @returns {Promise<V1Document>}
   */
  async get(id, options = {}) {
    /** @type {Record<string, string>} */
    const headers = {};
    if (options.ifNoneMatch !== undefined) headers["if-none-match"] = `"${requireVersion(options.ifNoneMatch)}"`;
    const response = await this.#transport.call("getDocument", this.#document(id), { headers, signal: options.signal });
    return decodeDocument(response.value);
  }

  /**
   * Writes the complete document. `created` reports a 201.
   * @param {string} id
   * @param {JsonInputObject} document
   * @param {V1WriteOptions} [options]
   * @returns {Promise<V1WriteResult>}
   */
  async put(id, document, options = {}) {
    const params = this.#document(id);
    const headers = preconditionHeaders(options, true);
    return decodeWrite(await this.#transport.call("putDocument", params, {
      body: encodeDocument(document), headers, signal: options.signal,
    }));
  }

  /**
   * Creates a document under a server-generated UUIDv7.
   * @param {JsonInputObject} document
   * @param {V1CreateOptions} [options]
   * @returns {Promise<V1WriteResult>}
   */
  async create(document, options = {}) {
    return decodeWrite(await this.#transport.call("createDocument", this.#params, {
      body: encodeDocument(document), headers: preconditionHeaders(options, true), signal: options.signal,
    }));
  }

  /**
   * Applies a merge patch or ops patch to an existing document.
   * @param {string} id
   * @param {V1Patch} patch
   * @param {V1ConditionalWriteOptions} [options]
   * @returns {Promise<V1WriteResult>}
   */
  async patch(id, patch, options = {}) {
    const params = this.#document(id);
    const headers = preconditionHeaders(options, false);
    const normalized = normalizePatch(patch);
    const [contentType, body] = "merge" in normalized
      ? ["application/merge-patch+json", stringifyExactJson(normalized.merge, "merge patch")]
      : ["application/vnd.terrabase.ops+json", stringifyExactJson({ ops: normalized.ops }, "patch ops")];
    return decodeWrite(await this.#transport.call("patchDocument", params, {
      body, contentType, headers,
      signal: options.signal,
    }));
  }

  /**
   * Tombstones a document; `version` is the tombstone's.
   * @param {string} id
   * @param {V1ConditionalWriteOptions} [options]
   * @returns {Promise<V1WriteResult>}
   */
  async delete(id, options = {}) {
    const params = this.#document(id);
    const headers = preconditionHeaders(options, false);
    return decodeWrite(await this.#transport.call("deleteDocument", params, {
      headers, signal: options.signal,
    }));
  }

  /**
   * Runs one page of a query.
   * @param {V1Query} [query]
   * @param {V1RequestOptions} [options]
   * @returns {Promise<V1QueryPage>}
   */
  async query(query = {}, options = {}) {
    const response = await this.#transport.call("queryDocuments", this.#params, {
      body: encodeQuery(query), signal: options.signal,
    });
    return decodeQueryPage(response.value);
  }

  /**
   * Iterates over every page, following `nextCursor`. `count`
   * is sent with the first page only.
   * @param {V1Query} [query]
   * @param {V1RequestOptions} [options]
   * @returns {AsyncGenerator<V1QueryPage, void, undefined>}
   */
  async *pages(query = {}, options = {}) {
    /** @type {V1Query} */
    let current = query;
    while (true) {
      const page = await this.query(current, options);
      yield page;
      if (page.nextCursor === null) return;
      const { count: _count, ...rest } = current;
      current = { ...rest, cursor: page.nextCursor };
    }
  }

  /**
   * Iterates over every matching document across pages.
   * @param {V1Query} [query]
   * @param {V1RequestOptions} [options]
   * @returns {AsyncGenerator<V1Document, void, undefined>}
   */
  async *items(query = {}, options = {}) {
    for await (const page of this.pages(query, options)) yield* page.items;
  }
  /**
   * Reads one C# shape snapshot page or change-log chunk. Bootstrap defaults to offset -1.
   * Echo handle, nextOffset and cursor on the following read; discard state on must_refetch.
   * @param {V1ShapeRequest} [request]
   * @param {V1RequestOptions} [options]
   * @returns {Promise<V1ShapePage>}
   */
  shape(request = {}, options = {}) {
    return readShape(this.#transport, this.#params, request, options.signal);
  }

}

/**
 * An atomic transaction of up to 1024 ops across the collections of one
 * database. No two ops may target the same document. Documents are encoded
 * when an op is added, so later mutation of the input does not change it.
 */
export class V1Transaction {
  /** @type {V1Transport} */
  #transport;
  /** @type {string} */
  #database;
  /** @type {string[]} */
  #ops = [];
  /** @type {Set<string>} */
  #targets = new Set();
  #countAsserts = 0;

  /**
   * @param {V1Transport} transport
   * @param {string} database
   */
  constructor(transport, database) {
    this.#transport = transport;
    this.#database = requireName("database", database);
  }

  /** @returns {number} The number of ops added. */
  get size() {
    return this.#ops.length;
  }

  /**
   * @param {Record<string, unknown>} op
   * @returns {this}
   */
  #add(op) {
    if (this.#ops.length >= 1024) throw new TypeError("A transaction has at most 1024 ops.");
    const encoded = stringifyExactJson(op, `transaction op ${this.#ops.length}`);
    const target = op.id === undefined ? undefined : JSON.stringify([op.collection, op.id]);
    if (target !== undefined) {
      if (this.#targets.has(target)) throw new TypeError("Two transaction operations cannot target the same document.");
      this.#targets.add(target);
    }
    this.#ops.push(encoded);
    return this;
  }

  /**
   * @param {V1Precondition | undefined} precondition
   * @param {boolean} allowNoneMatch
   * @returns {Record<string, string>}
   */
  static #precondition(precondition, allowNoneMatch) {
    if (precondition === undefined) return {};
    if ("ifMatch" in precondition && !("ifNoneMatchAny" in precondition))
      return { if_match: precondition.ifMatch === "*" ? "*" : requireVersion(precondition.ifMatch) };
    if ("ifNoneMatchAny" in precondition && precondition.ifNoneMatchAny === true && !("ifMatch" in precondition)) {
      if (!allowNoneMatch) throw new TypeError("This op does not take ifNoneMatchAny.");
      return { if_none_match: "*" };
    }
    throw new TypeError("A precondition is { ifMatch } or { ifNoneMatchAny: true }.");
  }

  /**
   * Adds a create (a put requiring absence). Omit `id` to have the server
   * generate a UUIDv7, returned in the op's result.
   * @param {string} collection
   * @param {JsonInputObject} document
   * @param {string} [id]
   * @returns {this}
   */
  create(collection, document, id) {
    encodeDocument(document);
    return this.#add({
      op: "create", collection: requireName("collection", collection),
      ...(id === undefined ? {} : { id: requireDocumentId(id) }), document,
    });
  }

  /**
   * @param {string} collection
   * @param {string} id
   * @param {JsonInputObject} document
   * @param {V1Precondition} [precondition]
   * @returns {this}
   */
  put(collection, id, document, precondition) {
    encodeDocument(document);
    return this.#add({
      op: "put", collection: requireName("collection", collection), id: requireDocumentId(id), document,
      ...V1Transaction.#precondition(precondition, true),
    });
  }

  /**
   * @param {string} collection
   * @param {string} id
   * @param {V1Patch} patch
   * @param {{ readonly ifMatch: string }} [precondition]
   * @returns {this}
   */
  patch(collection, id, patch, precondition) {
    return this.#add({
      op: "patch", collection: requireName("collection", collection), id: requireDocumentId(id),
      ...normalizePatch(patch), ...V1Transaction.#precondition(precondition, false),
    });
  }

  /**
   * @param {string} collection
   * @param {string} id
   * @param {{ readonly ifMatch: string }} [precondition]
   * @returns {this}
   */
  delete(collection, id, precondition) {
    return this.#add({
      op: "delete", collection: requireName("collection", collection), id: requireDocumentId(id),
      ...V1Transaction.#precondition(precondition, false),
    });
  }

  /**
   * Requires the document to exist and match the filter.
   * @param {string} collection
   * @param {string} id
   * @param {V1Filter} filter
   * @returns {this}
   */
  assert(collection, id, filter) {
    return this.#add({ op: "assert", collection: requireName("collection", collection), id: requireDocumentId(id), filter: requireFilter(filter) });
  }

  /**
   * Requires the document to be absent or deleted.
   * @param {string} collection
   * @param {string} id
   * @returns {this}
   */
  assertAbsent(collection, id) {
    return this.#add({ op: "assert", collection: requireName("collection", collection), id: requireDocumentId(id), absent: true });
  }

  /**
   * Requires the number of the collection's documents matching `filter` to
   * satisfy `gte` and/or `lte`. Omit the filter with null to count all documents. It is evaluated inside the commit, before
   * this transaction's writes, so it is serializable with every other write.
   * At most 16 per transaction; the result carries the `count`.
   * @param {string} collection
   * @param {V1Filter | null} filter
   * @param {V1CountBounds} bounds
   * @returns {this}
   */
  assertCount(collection, filter, bounds) {
    if (filter !== null) requireFilter(filter);
    /** @type {Record<string, number>} */
    const limits = {};
    for (const name of /** @type {const} */ (["gte", "lte"])) {
      const value = bounds?.[name];
      if (value === undefined) continue;
      if (!Number.isSafeInteger(value) || value < 0) throw new TypeError(`assertCount ${name} must be a non-negative integer.`);
      limits[name] = value;
    }
    if (Object.keys(limits).length === 0) throw new TypeError("assertCount needs gte, lte or both.");
    if (limits.gte !== undefined && limits.lte !== undefined && limits.gte > limits.lte) throw new TypeError("assertCount gte must not exceed lte.");
    if (this.#countAsserts >= 16) throw new TypeError("A transaction has at most 16 assertCount ops.");
    this.#add({ op: "assert_count", collection: requireName("collection", collection), ...(filter === null ? {} : { filter }), ...limits });
    this.#countAsserts++;
    return this;
  }

  /**
   * Applies every op atomically. Failures are TerraBaseV1Error values whose
   * `op` names the failing op index.
   * @param {V1RequestOptions} [options]
   * @returns {Promise<V1TransactionResult>}
   */
  async commit(options = {}) {
    if (this.#ops.length === 0) throw new TypeError("A transaction needs at least one op.");
    rejectIdempotency(options);
    const sentCount = this.#ops.length;
    const response = await this.#transport.call("commitTransaction", { database: this.#database }, {
      body: `{"ops":[${this.#ops.join(",")}]}`,
      signal: options.signal,
    });
    const raw = object(response.value, "transaction result");
    if (array(raw.results, "transaction results").length !== sentCount) malformed("transaction result count");
    return Object.freeze({
      offset: u64(raw.offset, "transaction offset"),
      durability: durability(raw.durability),
      results: Object.freeze(array(raw.results, "transaction results").map((value) => {
        const result = object(value, "transaction op result");
        if (result.asserted === true)
          return Object.freeze(result.count === undefined
            ? { asserted: /** @type {const} */ (true) }
            : { asserted: /** @type {const} */ (true), count: u64(result.count, "assert_count count") });
        return Object.freeze({ id: requireDocumentId(result.id), version: requireVersion(result.version) });
      })),
    });
  }
}

/** @typedef {{ readonly name: string; readonly createdAt: string }} V1CollectionInfo */
/** @typedef {{ readonly items: readonly V1CollectionInfo[]; readonly nextCursor: string | null }} V1CollectionPage */
/** @typedef {{ readonly limit?: number | undefined; readonly cursor?: string | undefined }} V1CollectionListOptions */
/** @param {JsonValue | undefined} value @returns {V1CollectionInfo} */
function decodeCollection(value) {
  const raw = object(value, "collection");
  return Object.freeze({ name: requireName("collection", raw.name), createdAt: u64(raw.created_at, "collection created_at") });
}
/** Collection management on the public client listener. */
export class V1Collections {
  /** @type {V1Transport} */
  #transport;
  /** @type {string} */
  #database;
  /** @param {V1Transport} transport @param {string} database */
  constructor(transport, database) {
    this.#transport = transport;
    this.#database = requireName("database", database);
    Object.freeze(this);
  }
  /** @param {string} name @param {V1RequestOptions} [options] @returns {Promise<V1CollectionInfo>} */
  async create(name, options = {}) {
    rejectIdempotency(options);
    const response = await this.#transport.call("createCollection", { database: this.#database }, {
      body: stringifyExactJson({ name: requireName("collection", name) }, "collection"), signal: options.signal,
    });
    return decodeCollection(response.value);
  }
  /** @param {string} name @param {V1RequestOptions} [options] @returns {Promise<V1CollectionInfo>} */
  async get(name, options = {}) {
    const response = await this.#transport.call("getCollection", { database: this.#database, collection: requireName("collection", name) }, { signal: options.signal });
    return decodeCollection(response.value);
  }
  /** @param {V1CollectionListOptions} [request] @param {V1RequestOptions} [options] @returns {Promise<V1CollectionPage>} */
  async list(request = {}, options = {}) {
    const query = new URLSearchParams();
    for (const key of Object.keys(request)) if (key !== "limit" && key !== "cursor") throw new TypeError(`Unknown collection list member ${key}.`);
    if (request.limit !== undefined) {
      if (!Number.isInteger(request.limit) || request.limit < 1 || request.limit > 200) throw new TypeError("Collection list limit is 1-200.");
      query.set("limit", String(request.limit));
    }
    if (request.cursor !== undefined) {
      if (typeof request.cursor !== "string" || request.cursor === "") throw new TypeError("Collection cursor is a non-empty string.");
      query.set("cursor", request.cursor);
    }
    const response = await this.#transport.call("listCollections", { database: this.#database }, { query, signal: options.signal });
    const raw = object(response.value, "collection page");
    if (raw.next_cursor !== null && (typeof raw.next_cursor !== "string" || raw.next_cursor === "")) malformed("collection next_cursor");
    return Object.freeze({ items: Object.freeze(array(raw.items, "collections").map(decodeCollection)), nextCursor: raw.next_cursor });
  }
  /** @param {string} name @param {V1RequestOptions} [options] @returns {Promise<V1CollectionInfo>} */
  async drop(name, options = {}) {
    rejectIdempotency(options);
    const response = await this.#transport.call("dropCollection", { database: this.#database, collection: requireName("collection", name) }, { signal: options.signal });
    return decodeCollection(response.value);
  }
}

/** A database handle by name. It performs no I/O until an operation runs. */
export class V1Database {
  /** @type {V1Collections} */
  collections;
  /** @type {V1Transport} */
  #transport;
  /** @type {string} */
  name;

  /**
   * @param {V1Transport} transport
   * @param {string} name
   */
  constructor(transport, name) {
    this.#transport = transport;
    this.name = requireName("database", name);
    this.collections = new V1Collections(transport, this.name);
    Object.freeze(this);
  }

  /**
   * @param {string} name
   * @returns {V1Collection}
   */
  collection(name) {
    return new V1Collection(this.#transport, this.name, name);
  }

  /** @returns {V1Transaction} */
  transaction() {
    return new V1Transaction(this.#transport, this.name);
  }


}

/**
 * A TerraBase v1 data-plane client for one project and access token. The token is held
 * privately; it is never serialized, logged or forwarded to redirects.
 */
export class TerraBaseV1Client {
  /** @type {V1Transport} */
  #transport;

  /** @param {TerraBaseV1ClientConfig} config */
  constructor(config) {
    this.#transport = new V1Transport(config);
    Object.freeze(this);
  }

  /**
   * @param {string} name
   * @returns {V1Database}
   */
  database(name) {
    return new V1Database(this.#transport, name);
  }
}

/**
 * Creates a TerraBase v1 client for documents, transactions, queries and shapes.
 * @param {TerraBaseV1ClientConfig} config
 * @returns {TerraBaseV1Client}
 */
export function createTerraBaseV1Client(config) {
  return new TerraBaseV1Client(config);
}
