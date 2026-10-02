// TerraBase v1 contract fixture: an in-process HTTP loopback server that
// implements the frozen wire format of terrabase docs/api-v1.md,
// query-v1.md and shapes-v1.md closely enough to drive the v1 client through
// a real HTTP boundary.
//
// It is a stand-in until a packaged tb-server serves the v1 routes; it is not
// evidence of producer behavior. Replace these tests' target with the
// packaged binary once it is available. It runs over plain HTTP on loopback
// (the client admits HTTP only for loopback hosts). Data keys carry grants
// (database, collections, permissions) and can be revoked; TLS, row filters,
// TTL and auto-indexing are not modeled.
import { createHash, randomBytes } from "node:crypto";
import { EventEmitter } from "node:events";
import { createServer } from "node:http";

/** A syntactically valid data key accepted by the fixture. */
export const fixtureToken = `tbk1.${"f".repeat(22)}.${"s".repeat(43)}`;

const rawJSON = /** @type {(text: string) => unknown} */ (
  /** @type {any} */ (JSON).rawJSON
);
const MISSING = Symbol("missing");

/** An exact JSON number kept as its source text. */
class Num {
  /** @param {string} text */
  constructor(text) {
    this.text = text;
  }
  toJSON() {
    return rawJSON(this.text);
  }
}

/**
 * @param {string} text
 * @returns {any}
 */
function parse(text) {
  // Objects get null prototypes, so member names such as "__proto__" stay
  // ordinary JSON data in every later read, merge, patch and projection.
  return JSON.parse(text, /** @type {(key: string, value: any) => any} */ (/** @type {unknown} */ (
    (/** @type {string} */ _key, /** @type {any} */ value, /** @type {any} */ context) => {
      if (typeof value === "number") return new Num(context.source);
      if (typeof value === "object" && value !== null && !Array.isArray(value)) return Object.assign(Object.create(null), value);
      return value;
    })));
}

/**
 * @param {unknown} value
 * @returns {string}
 */
const stringify = (value) => JSON.stringify(value);

/**
 * @param {string} text
 * @returns {{ m: bigint; e: number }}
 */
function decimal(text) {
  const match = /^(-?)(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$/u.exec(text);
  if (match === null) throw new Error(`bad number ${text}`);
  const fraction = match[3] ?? "";
  return { m: BigInt(`${match[1]}${match[2]}${fraction}`), e: Number(match[4] ?? "0") - fraction.length };
}

/**
 * @param {{ m: bigint; e: number }} a
 * @param {{ m: bigint; e: number }} b
 * @returns {[bigint, bigint, number]}
 */
function align(a, b) {
  const e = Math.min(a.e, b.e);
  return [a.m * 10n ** BigInt(a.e - e), b.m * 10n ** BigInt(b.e - e), e];
}

/**
 * @param {Num} a
 * @param {Num} b
 * @returns {number}
 */
function compareNum(a, b) {
  const [x, y] = align(decimal(a.text), decimal(b.text));
  return x < y ? -1 : x > y ? 1 : 0;
}

/**
 * @param {Num} a
 * @param {Num} b
 * @returns {Num}
 */
function addNum(a, b) {
  const [x, y, e] = align(decimal(a.text), decimal(b.text));
  const sum = x + y;
  if (e >= 0) return new Num((sum * 10n ** BigInt(e)).toString());
  const negative = sum < 0n;
  const digits = (negative ? -sum : sum).toString().padStart(-e + 1, "0");
  return new Num(`${negative ? "-" : ""}${digits.slice(0, e)}.${digits.slice(e)}`);
}

class FixtureError extends Error {
  /**
   * @param {number} status
   * @param {string} code
   * @param {Record<string, unknown>} [extra]
   */
  constructor(status, code, extra = {}) {
    super(code);
    this.status = status;
    this.code = code;
    this.extra = extra;
  }
}

/**
 * @param {number} status
 * @param {string} code
 * @param {Record<string, unknown>} [extra]
 * @returns {never}
 */
function fail(status, code, extra) {
  throw new FixtureError(status, code, extra);
}

/**
 * @param {string} pointer
 * @returns {string[]}
 */
function segments(pointer) {
  if (typeof pointer !== "string" || !pointer.startsWith("/")) fail(400, "invalid_query");
  return pointer.slice(1).split("/").map((part) => part.replace(/~1/gu, "/").replace(/~0/gu, "~"));
}

/**
 * @param {any} value
 * @returns {boolean}
 */
const isObject = (value) => typeof value === "object" && value !== null && !Array.isArray(value) && !(value instanceof Num);

/**
 * @param {any} doc
 * @param {string} id
 * @param {any} path
 * @returns {any}
 */
function resolve(doc, id, path) {
  if (path === "$id") return id;
  if (isObject(path)) {
    const value = resolve(doc, id, path.lower);
    return typeof value === "string" ? caseFold(value) : value;
  }
  let current = doc;
  for (const segment of segments(path)) {
    if (!isObject(current) || !Object.hasOwn(current, segment)) return MISSING;
    current = current[segment];
  }
  return current;
}

/**
 * Unicode default (full) case folding, approximated without the
 * CaseFolding.txt table: upper-then-lower casing gives the full folds such as
 * ß → ss and ligatures; final sigma folds to σ; Cherokee folds to its
 * uppercase block. Fixture-only; the server owns the real folding.
 * @param {string} value
 * @returns {string}
 */
function caseFold(value) {
  return value.toUpperCase().toLowerCase()
    .replace(/ς/gu, "σ")
    .replace(/[\uAB70-\uABBF]/gu, (char) => String.fromCharCode(char.charCodeAt(0) - 0xab70 + 0x13a0))
    .replace(/[\u13F8-\u13FD]/gu, (char) => String.fromCharCode(char.charCodeAt(0) - 8));
}

/**
 * @param {any} value
 * @returns {number}
 */
function rank(value) {
  if (value === MISSING) return 0;
  if (value === null) return 1;
  if (value === false) return 2;
  if (value === true) return 3;
  if (value instanceof Num) return 4;
  if (typeof value === "string") return 5;
  return 6;
}

/**
 * @param {any} a
 * @param {any} b
 * @returns {number}
 */
function compareValues(a, b) {
  const ra = rank(a);
  const rb = rank(b);
  if (ra < rb) return -1;
  if (ra > rb) return 1;
  switch (ra) {
    case 4: return compareNum(a, b);
    case 5: return Buffer.compare(Buffer.from(a), Buffer.from(b));
    default: return 0;
  }
}

/**
 * @param {any} value
 * @returns {boolean}
 */
const isScalar = (value) => value === null || typeof value === "boolean" || typeof value === "string" || value instanceof Num;

/**
 * @param {any} filter
 * @param {number} depth
 * @returns {void}
 */
function validateFilter(filter, depth = 0) {
  if (!isObject(filter) || Object.keys(filter).length !== 1 || depth > 8) fail(400, "invalid_query");
  const [op, args] = /** @type {[string, any]} */ (Object.entries(filter)[0]);
  /** @param {any} path */
  const path = (path) => {
    if (path === "$id") return;
    if (isObject(path) && Object.keys(path).length === 1 && typeof path.lower === "string") return void segments(path.lower);
    segments(path);
  };
  switch (op) {
    case "and":
    case "or":
      if (!Array.isArray(args) || args.length < 1 || args.length > 64) fail(400, "invalid_query");
      for (const child of args) validateFilter(child, depth + 1);
      return;
    case "not":
      validateFilter(args, depth + 1);
      return;
    case "exists":
      path(args);
      return;
    case "in":
    case "nin":
      if (!Array.isArray(args) || args.length !== 2 || !Array.isArray(args[1]) || args[1].length < 1 || args[1].length > 256 || !args[1].every(isScalar)) fail(400, "invalid_query");
      path(args[0]);
      return;
    case "eq": case "ne": case "lt": case "lte": case "gt": case "gte": case "prefix": case "contains":
      if (!Array.isArray(args) || args.length !== 2 || !isScalar(args[1])) fail(400, "invalid_query");
      if (["lt", "lte", "gt", "gte"].includes(op) && args[1] === null) fail(400, "invalid_query");
      if (op === "prefix" && typeof args[1] !== "string") fail(400, "invalid_query");
      path(args[0]);
      return;
    default:
      fail(400, "invalid_query");
  }
}

/**
 * @param {any} value
 * @param {any} operand
 * @returns {boolean}
 */
const scalarEqual = (value, operand) => isScalar(value) && rank(value) === rank(operand) && compareValues(value, operand) === 0;

/**
 * Equality of any two stored values: scalars by exact value, structured
 * values by canonical JSON text with sorted member names.
 * @param {any} a
 * @param {any} b
 * @returns {boolean}
 */
function sameValue(a, b) {
  if (isScalar(a) || isScalar(b)) return isScalar(a) && isScalar(b) && scalarEqual(a, b);
  /** @param {any} value @returns {string} */
  const canonical = (value) => {
    if (Array.isArray(value)) return `[${value.map(canonical).join(",")}]`;
    if (isObject(value)) return `{${Object.keys(value).sort().map((key) => `${JSON.stringify(key)}:${canonical(value[key])}`).join(",")}}`;
    return value instanceof Num ? `#${decimal(value.text).m}e${decimal(value.text).e}` : JSON.stringify(value);
  };
  return canonical(a) === canonical(b);
}

/**
 * @param {any} filter
 * @param {any} doc
 * @param {string} id
 * @returns {boolean}
 */
function matches(filter, doc, id) {
  if (filter === undefined || filter === null) return true;
  const [op, args] = /** @type {[string, any]} */ (Object.entries(filter)[0]);
  switch (op) {
    case "and": return args.every((/** @type {any} */ child) => matches(child, doc, id));
    case "or": return args.some((/** @type {any} */ child) => matches(child, doc, id));
    case "not": return !matches(args, doc, id);
    case "exists": return resolve(doc, id, args) !== MISSING;
  }
  const value = resolve(doc, id, args[0]);
  const operand = args[1];
  switch (op) {
    case "eq": return scalarEqual(value, operand);
    case "ne": return !scalarEqual(value, operand);
    case "in": return operand.some((/** @type {any} */ item) => scalarEqual(value, item));
    case "nin": return !operand.some((/** @type {any} */ item) => scalarEqual(value, item));
    case "prefix": return typeof value === "string" && value.startsWith(operand);
    case "contains": return Array.isArray(value) && value.some((item) => scalarEqual(item, operand));
  }
  const sameType = isScalar(value) && (rank(value) === rank(operand) || (typeof value === "boolean" && typeof operand === "boolean"));
  if (!sameType) return false;
  const order = compareValues(value, operand);
  switch (op) {
    case "lt": return order < 0;
    case "lte": return order <= 0;
    case "gt": return order > 0;
    default: return order >= 0;
  }
}

/**
 * @param {unknown} project
 * @returns {boolean}
 */
const validProjection = (project) => project === undefined ||
  (Array.isArray(project) && project.length <= 64 && project.every((pointer) => typeof pointer === "string" && /^(?:\/(?:[^~/]|~[01])*)+$/u.test(pointer)));

/**
 * @param {any} doc
 * @param {readonly string[] | undefined} project
 * @returns {any}
 */
function projectDocument(doc, project) {
  if (project === undefined) return structuredCloneDoc(doc);
  // Built detached from the source: selected values are cloned, parents are
  // fresh null-prototype objects, and inherited members are never traversed.
  const out = Object.create(null);
  for (const pointer of project) {
    if (resolve(doc, "", pointer) === MISSING) continue;
    const parts = segments(pointer);
    let source = doc;
    let target = out;
    for (const [index, part] of parts.entries()) {
      if (index === parts.length - 1) {
        target[part] = structuredCloneDoc(source[part]);
        break;
      }
      if (!Object.hasOwn(target, part)) target[part] = Object.create(null);
      else if (!isObject(target[part])) break;
      target = target[part];
      source = source[part];
    }
  }
  return out;
}

/**
 * @param {any} target
 * @param {any} patch
 * @returns {any}
 */
function mergePatch(target, patch) {
  if (!isObject(patch)) return structuredCloneDoc(patch);
  const out = Object.create(null);
  if (isObject(target)) for (const [key, value] of Object.entries(target)) out[key] = value;
  for (const [key, value] of Object.entries(patch)) {
    if (value === null) delete out[key];
    else out[key] = mergePatch(Object.hasOwn(out, key) ? out[key] : undefined, value);
  }
  return out;
}

/**
 * @param {any} doc
 * @param {any[]} ops
 * @returns {any}
 */
function applyOps(doc, ops) {
  if (!Array.isArray(ops) || ops.length < 1 || ops.length > 256) fail(400, "invalid_patch");
  const out = structuredCloneDoc(doc);
  for (const op of ops) {
    if (!isObject(op) || typeof op.path !== "string" || !/^(?:\/(?:[^~/]|~[01])*)+$/u.test(op.path)) fail(400, "invalid_patch");
    const parts = segments(op.path);
    const last = /** @type {string} */ (parts.pop());
    let parent = out;
    for (const part of parts) {
      if (!Object.hasOwn(parent, part)) {
        if (op.op === "unset") { parent = undefined; break; }
        parent[part] = Object.create(null);
      }
      if (!isObject(parent[part])) fail(409, "patch_conflict");
      parent = parent[part];
    }
    switch (op.op) {
      case "set": parent[last] = structuredCloneDoc(op.value); break;
      case "unset": if (parent !== undefined) delete parent[last]; break;
      case "inc": {
        if (!(op.by instanceof Num)) fail(400, "invalid_patch");
        const current = Object.hasOwn(parent, last) ? parent[last] : new Num("0");
        if (!(current instanceof Num)) fail(409, "patch_conflict");
        parent[last] = addNum(current, op.by);
        break;
      }
      case "append": {
        const current = Object.hasOwn(parent, last) ? parent[last] : [];
        if (!Array.isArray(current)) fail(409, "patch_conflict");
        parent[last] = [...current, structuredCloneDoc(op.value)];
        break;
      }
      default: fail(400, "invalid_patch");
    }
  }
  return out;
}

/**
 * @param {any} value
 * @returns {any}
 */
function structuredCloneDoc(value) {
  if (Array.isArray(value)) return value.map(structuredCloneDoc);
  if (isObject(value)) {
    const out = Object.create(null);
    for (const [key, item] of Object.entries(value)) out[key] = structuredCloneDoc(item);
    return out;
  }
  return value;
}

/** @returns {string} */
function uuidv7() {
  const bytes = randomBytes(16);
  bytes.writeUIntBE(Date.now(), 0, 6);
  bytes[6] = ((bytes[6] ?? 0) & 0x0f) | 0x70;
  bytes[8] = ((bytes[8] ?? 0) & 0x3f) | 0x80;
  const hex = bytes.toString("hex");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

const sha256 = (/** @type {string} */ text) => createHash("sha256").update(text).digest("hex");
const idPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/u;
const namePattern = /^[a-z][a-z0-9_-]{0,62}$/u;

/**
 * @typedef {object} FixtureOptions
 * @property {number} [liveTimeoutMs] How long a live long-poll waits (contract: 20 s).
 * @property {number} [snapshotPageSize] Documents per snapshot page (contract: 1000).
 * @property {number} [tokenLifetimeMs] Shape token lifetime (contract: at most 15 min).
 * @property {number} [sseHeartbeatMs] SSE comment interval (contract: 15 s).
 * @property {"\n" | "\r" | "\r\n"} [sseLineEnding] SSE line terminator (all three are valid).
 * @property {Record<string, { name: string; path: string; lower: boolean }[]>} [unique]
 *   Unique constraints by collection name (default: `email` on `/email`, lowercased, for `users` and `todos`).
 */

/**
 * A data key grant (api-v1.md): names or `"*"`.
 * @typedef {object} FixtureGrant
 * @property {string} database
 * @property {readonly string[]} collections
 * @property {readonly ("read" | "write" | "shape")[]} permissions
 */

/**
 * @typedef {object} RecordedRequest
 * @property {"origin" | "edge"} server
 * @property {string} method
 * @property {string} path
 * @property {Record<string, string>} query
 * @property {Record<string, string | undefined>} headers
 * @property {string} body
 */

/**
 * @typedef {object} InjectedFailure
 * @property {(request: RecordedRequest) => boolean} match
 * @property {number} status
 * @property {string} code
 * @property {number} [retryAfter]
 * @property {boolean} [afterCommit] Apply the request, then answer with the failure.
 * @property {boolean} [truncate] Send 200 headers and part of a body, then drop the connection.
 * @property {string} [body] Answer with this raw JSON body and `status` instead of an error.
 */

/**
 * @param {FixtureOptions} [options]
 */
export async function startTerraBaseV1Fixture(options = {}) {
  const settings = {
    liveTimeoutMs: options.liveTimeoutMs ?? 1500,
    snapshotPageSize: options.snapshotPageSize ?? 1000,
    tokenLifetimeMs: options.tokenLifetimeMs ?? 15 * 60_000,
    sseHeartbeatMs: options.sseHeartbeatMs ?? 250,
    sseLineEnding: options.sseLineEnding ?? "\n",
  };
  /** @type {RecordedRequest[]} */
  const requests = [];
  /** @type {InjectedFailure[]} */
  const failures = [];
  /** @type {Map<string, { id: string; version: string; doc: any }>} */
  const store = new Map();
  /** @type {Map<string, { head: number; log: { offset: number; changes: { collection: string; id: string; before: any; after: any; version: string }[] }[] }>} */
  const databases = new Map();
  /** @type {Map<string, { status: number; headers: Record<string, string>; body: string; hash: string }>} */
  const idempotency = new Map();
  /** @type {Map<string, { db: string; collection: string; filter: any; project: any; handle: string }>} */
  const shapes = new Map();
  /** @type {Map<string, any[]>} */
  const snapshots = new Map();
  /** @type {Map<string, { shapeId: string; expiresAt: number; revoked: boolean }>} */
  const tokens = new Map();
  /** @type {Set<import("node:http").ServerResponse>} */
  const sseStreams = new Set();
  const events = new EventEmitter();
  events.setMaxListeners(0);
  const unique = options.unique ?? {
    users: [{ name: "email", path: "/email", lower: true }],
    todos: [{ name: "email", path: "/email", lower: true }],
  };
  /** @type {Map<string, { grant: FixtureGrant; revoked: boolean }>} */
  const keys = new Map([[fixtureToken, { grant: { database: "*", collections: ["*"], permissions: ["read", "write", "shape"] }, revoked: false }]]);
  let keyCounter = 0;

  /**
   * @param {FixtureGrant} grant
   * @param {string} db
   * @param {string} collection
   * @param {"read" | "write" | "shape"} permission
   */
  function authorize(grant, db, collection, permission) {
    if ((grant.database !== "*" && grant.database !== db) ||
      !(grant.collections.includes("*") || grant.collections.includes(collection)) ||
      !grant.permissions.includes(permission)) fail(403, "forbidden_scope");
  }
  let versionCounter = 0;
  let corruptSseChanges = 0;
  let gatekeeperCalls = 0;
  let edgeOrigin = "";

  /**
   * The committed entries of one collection.
   * @param {string} db
   * @param {string} collection
   * @param {boolean} withTombstones
   * @returns {{ id: string; version: string; doc: any }[]}
   */
  const collectionEntries = (db, collection, withTombstones) => {
    const prefix = `${db}/${collection}/`;
    /** @type {{ id: string; version: string; doc: any }[]} */
    const entries = [];
    for (const [storeKey, entry] of store)
      if (storeKey.startsWith(prefix) && (withTombstones || entry.doc !== null)) entries.push(entry);
    return entries;
  };

  /** @param {string} db */
  const database = (db) => {
    let state = databases.get(db);
    if (state === undefined) {
      state = { head: 0, log: [] };
      databases.set(db, state);
    }
    return state;
  };
  const nextVersion = () => `tbv1-${sha256(String(++versionCounter)).slice(0, 32)}`;
  const key = (/** @type {string} */ db, /** @type {string} */ collection, /** @type {string} */ id) => `${db}/${collection}/${id}`;

  /**
   * A working view for one transaction: reads see earlier ops' writes.
   * @param {string} db
   */
  function view(db) {
    /** @type {Map<string, { collection: string; id: string; before: any; after: any; version: string }>} */
    const writes = new Map();
    return {
      db,
      writes,
      /**
       * @param {string} collection
       * @param {string} id
       */
      get(collection, id) {
        const pending = writes.get(key(db, collection, id));
        if (pending !== undefined) return { id, version: pending.version, doc: pending.after };
        return store.get(key(db, collection, id));
      },
      /**
       * @param {string} collection
       * @param {string} id
       * @param {any} after
       */
      set(collection, id, after) {
        const existing = store.get(key(db, collection, id));
        const pending = writes.get(key(db, collection, id));
        const version = nextVersion();
        writes.set(key(db, collection, id), { collection, id, before: pending?.before ?? existing?.doc ?? null, after, version });
        if (after !== null) this.checkUnique(collection, id, after);
        return version;
      },
      /**
       * @param {string} collection
       * @param {string} id
       * @param {any} doc
       */
      checkUnique(collection, id, doc) {
        for (const constraint of unique[collection] ?? []) {
          const value = resolve(doc, id, constraint.lower ? { lower: constraint.path } : constraint.path);
          // Missing and null are not constrained; structured values are compared by canonical text.
          if (value === MISSING || value === null) continue;
          /** @type {Map<string, any>} */
          const docs = new Map();
          for (const entry of collectionEntries(db, collection, true)) docs.set(entry.id, entry.doc);
          for (const pending of writes.values()) if (pending.collection === collection) docs.set(pending.id, pending.after);
          for (const [otherId, other] of docs) {
            if (otherId === id || other === null) continue;
            const otherValue = resolve(other, otherId, constraint.lower ? { lower: constraint.path } : constraint.path);
            if (otherValue !== MISSING && otherValue !== null && sameValue(otherValue, value))
              fail(409, "unique_violation", { constraint: constraint.name });
          }
        }
      },
      commit() {
        const state = database(db);
        if (writes.size === 0) return state.head;
        const offset = ++state.head;
        for (const write of writes.values())
          store.set(key(db, write.collection, write.id), { id: write.id, version: write.version, doc: write.after });
        state.log.push({ offset, changes: [...writes.values()] });
        events.emit("commit", db);
        return offset;
      },
    };
  }

  /**
   * @param {{ version: string; doc: any } | undefined} entry
   * @param {string | undefined} ifMatch
   * @param {boolean} ifNoneMatchAny
   */
  function precondition(entry, ifMatch, ifNoneMatchAny) {
    if (ifMatch !== undefined && entry?.version !== ifMatch) fail(412, "precondition_failed");
    if (ifNoneMatchAny && entry !== undefined && entry.doc !== null) fail(412, "precondition_failed");
  }

  /**
   * @param {string | undefined} header
   * @returns {string | undefined}
   */
  const unquote = (header) => header === undefined ? undefined : header.replace(/^"|"$/gu, "");

  /**
   * @param {string} text
   * @returns {any}
   */
  function bodyObject(text) {
    let value;
    try { value = parse(text); } catch { fail(400, "invalid_request"); }
    if (!isObject(value)) fail(400, "invalid_request");
    return value;
  }

  /**
   * @param {number} offset
   * @param {string} durability
   */
  const writeBody = (/** @type {string} */ id, /** @type {string} */ version, offset, durability = "local_durable") =>
    stringify({ id, version, offset: String(offset), durability });

  /**
   * @typedef {{ status: number; headers: Record<string, string>; body: string }} FixtureResponse
   */

  /**
   * @param {any} op
   * @param {number} index
   * @param {ReturnType<typeof view>} work
   * @returns {any}
   */
  function applyTxOp(op, index, work) {
    try {
      if (!isObject(op)) fail(400, "invalid_request");
      const { collection, id } = op;
      if (typeof collection !== "string" || !namePattern.test(collection)) fail(400, "invalid_request");
      // Every op but create (and assert_count, which has none) names a valid document ID.
      const idOptional = op.op === "create" || op.op === "assert_count";
      if ((id !== undefined || !idOptional) && (typeof id !== "string" || !idPattern.test(id))) fail(400, "invalid_request");
      switch (op.op) {
        case "create": {
          if (op.if_match !== undefined || op.if_none_match !== undefined) fail(400, "invalid_request");
          const docId = id ?? uuidv7();
          if (!isObject(op.document)) fail(400, "invalid_request");
          precondition(work.get(collection, docId), undefined, true);
          return { id: docId, version: work.set(collection, docId, op.document) };
        }
        case "put": {
          if (!isObject(op.document)) fail(400, "invalid_request");
          precondition(work.get(collection, id), op.if_match, op.if_none_match === "*");
          return { id, version: work.set(collection, id, op.document) };
        }
        case "patch": {
          const entry = work.get(collection, id);
          if (("merge" in op) === ("ops" in op)) fail(400, "invalid_request");
          if (entry === undefined || entry.doc === null) fail(404, "not_found");
          precondition(entry, op.if_match, false);
          const next = "merge" in op ? mergePatch(entry.doc, op.merge) : applyOps(entry.doc, op.ops);
          return { id, version: work.set(collection, id, next) };
        }
        case "delete": {
          const entry = work.get(collection, id);
          if (entry === undefined || entry.doc === null) fail(404, "not_found");
          precondition(entry, op.if_match, false);
          return { id, version: work.set(collection, id, null) };
        }
        case "assert_count": {
          validateFilter(op.filter);
          const bounds = ["gte", "lte"].filter((name) => op[name] !== undefined);
          if (bounds.length === 0 || !bounds.every((name) => op[name] instanceof Num)) fail(400, "invalid_request");
          // Counted against committed state, before this transaction's writes.
          const examined = collectionEntries(work.db, collection, true);
          if (examined.length > 10_000) fail(400, "assert_requires_index");
          const count = examined.filter((entry) => entry.doc !== null && matches(op.filter, entry.doc, entry.id)).length;
          if ((op.gte !== undefined && count < Number(op.gte.text)) || (op.lte !== undefined && count > Number(op.lte.text))) fail(412, "precondition_failed");
          return { asserted: true, count: String(count) };
        }
        case "assert": {
          const entry = work.get(collection, id);
          const live = entry !== undefined && entry.doc !== null;
          if (op.absent === true) { if (live) fail(412, "precondition_failed"); }
          else {
            validateFilter(op.filter);
            if (!live || !matches(op.filter, entry.doc, id)) fail(412, "precondition_failed");
          }
          return { asserted: true };
        }
        default:
          return fail(400, "invalid_request");
      }
    } catch (error) {
      if (error instanceof FixtureError) error.extra = { ...error.extra, op: index };
      throw error;
    }
  }

  /**
   * @param {string} db
   * @param {string} collection
   * @param {any} query
   * @returns {string}
   */
  function runQuery(db, collection, query) {
    for (const member of Object.keys(query))
      if (!["filter", "sort", "project", "limit", "cursor", "count", "explain"].includes(member)) fail(400, "invalid_query");
    if (query.filter !== undefined) validateFilter(query.filter);
    const sort = /** @type {{ path: any; order: string }[]} */ (query.sort ?? []);
    if (!Array.isArray(sort) || sort.length > 4) fail(400, "invalid_query");
    if (!validProjection(query.project)) fail(400, "invalid_query");
    let limit = 50;
    if (query.limit !== undefined) limit = query.limit instanceof Num ? Number(query.limit.text) : Number.NaN;
    if (!Number.isInteger(limit) || limit < 1 || limit > 1000) fail(400, "invalid_query");
    const digest = sha256(stringify([query.filter ?? null, query.sort ?? null, query.project ?? null]));
    const all = collectionEntries(db, collection, false);
    const matching = all.filter((entry) => matches(query.filter, entry.doc, entry.id));
    /** @param {{ id: string; doc: any }} entry */
    const sortKey = (entry) => sort.map((item) => resolve(entry.doc, entry.id, item.path));
    /**
     * @param {any[]} ka
     * @param {string} ia
     * @param {any[]} kb
     * @param {string} ib
     */
    const order = (ka, ia, kb, ib) => {
      for (const [index, item] of sort.entries()) {
        const result = compareValues(ka[index], kb[index]);
        if (result !== 0) return item.order === "desc" ? -result : result;
      }
      return ia < ib ? -1 : ia > ib ? 1 : 0;
    };
    matching.sort((a, b) => order(sortKey(a), a.id, sortKey(b), b.id));
    let remaining = matching;
    const cursor = query.cursor ?? null;
    if (cursor !== null) {
      let position;
      try {
        position = parse(Buffer.from(cursor, "base64url").toString("utf8"));
      } catch { fail(400, "invalid_cursor"); }
      if (!isObject(position) || position.digest !== digest || !Array.isArray(position.key) ||
        position.key.length !== sort.length || typeof position.id !== "string") fail(400, "invalid_cursor");
      const after = position.key.map((/** @type {any} */ value) => (isObject(value) && value.$missing === true ? MISSING : value));
      remaining = matching.filter((entry) => order(sortKey(entry), entry.id, after, position.id) > 0);
    }
    /** @type {any} */
    const response = {};
    const plan = cursor === null ? query.explain : undefined;
    const page = plan === "plan" ? [] : remaining.slice(0, limit);
    response.items = page.map((entry) => ({ id: entry.id, version: entry.version, state: "value", document: projectDocument(entry.doc, query.project) }));
    const last = page.at(-1);
    response.next_cursor = plan !== "plan" && remaining.length > page.length && last !== undefined
      ? Buffer.from(stringify({ digest, key: sortKey(last).map((value) => (value === MISSING ? { $missing: true } : value)), id: last.id })).toString("base64url")
      : null;
    if (cursor === null && query.count === true) response.count = { value: String(matching.length), exact: true };
    if (plan !== undefined) {
      response.explain = { plan: { kind: "collection_scan", residual: query.filter !== undefined }, estimated_rows: String(matching.length) };
      if (plan === "analyze") response.explain.actual = { rows: String(page.length), documents_read: String(all.length) };
    }
    return stringify(response);
  }

  /**
   * @param {RecordedRequest} request
   * @param {string[]} match
   * @param {FixtureGrant} grant
   * @returns {FixtureResponse}
   */
  function dataRoute(request, match, grant) {
    const [, db = "", collection = "", rest = ""] = match;
    if (!namePattern.test(db) || (collection !== "" && !namePattern.test(collection))) fail(400, "invalid_request");
    // Shapes and transactions authorize per request member below (shape, or
    // read for asserts and write for writes).
    if (collection !== "" && rest !== "/shapes" && rest !== "/transactions")
      authorize(grant, db, collection, request.method === "GET" || rest === "/query" ? "read" : "write");
    const json = { "content-type": "application/json" };
    const ifMatch = unquote(request.headers["if-match"]);
    const ifNoneMatch = request.headers["if-none-match"];
    if (rest.startsWith("/documents/")) {
      const id = rest.slice("/documents/".length);
      if (!idPattern.test(id)) fail(400, "invalid_request");
      const entry = store.get(key(db, collection, id));
      const work = view(db);
      switch (request.method) {
        case "GET": {
          if (entry === undefined) fail(404, "not_found");
          if (entry.doc === null) throw new FixtureError(404, "not_found", { etag: entry.version });
          if (unquote(ifNoneMatch) === entry.version) return { status: 304, headers: { etag: `"${entry.version}"` }, body: "" };
          return { status: 200, headers: { ...json, etag: `"${entry.version}"` }, body: stringify({ id, version: entry.version, state: "value", document: entry.doc }) };
        }
        case "PUT": {
          const doc = bodyObject(request.body);
          precondition(entry, ifMatch, ifNoneMatch === "*");
          const version = work.set(collection, id, doc);
          const offset = work.commit();
          const created = entry === undefined || entry.doc === null;
          return { status: created ? 201 : 200, headers: { ...json, etag: `"${version}"` }, body: writeBody(id, version, offset) };
        }
        case "PATCH": {
          const type = request.headers["content-type"];
          const patch = bodyObject(request.body);
          if (type !== "application/merge-patch+json" && type !== "application/vnd.terrabase.ops+json") fail(415, "unsupported_media_type");
          if (entry === undefined || entry.doc === null) fail(404, "not_found");
          precondition(entry, ifMatch, false);
          const next = type === "application/merge-patch+json" ? mergePatch(entry.doc, patch) : applyOps(entry.doc, patch.ops);
          const version = work.set(collection, id, next);
          return { status: 200, headers: { ...json, etag: `"${version}"` }, body: writeBody(id, version, work.commit()) };
        }
        case "DELETE": {
          if (entry === undefined || entry.doc === null) fail(404, "not_found");
          precondition(entry, ifMatch, false);
          const version = work.set(collection, id, null);
          return { status: 200, headers: json, body: writeBody(id, version, work.commit()) };
        }
      }
      fail(405, "method_not_allowed");
    }
    if (rest === "/documents" && request.method === "POST") {
      const doc = bodyObject(request.body);
      const id = uuidv7();
      const work = view(db);
      const version = work.set(collection, id, doc);
      const offset = work.commit();
      return { status: 201, headers: { ...json, location: `/v1/databases/${db}/collections/${collection}/documents/${id}` }, body: writeBody(id, version, offset) };
    }
    if (rest === "/query" && request.method === "POST") return { status: 200, headers: json, body: runQuery(db, collection, bodyObject(request.body)) };
    if (rest === "/transactions" && request.method === "POST") {
      const body = bodyObject(request.body);
      if (!Array.isArray(body.ops) || body.ops.length < 1 || body.ops.length > 1024) fail(400, "invalid_request");
      for (const op of body.ops)
        authorize(grant, db, String(op?.collection), op?.op === "assert" || op?.op === "assert_count" ? "read" : "write");
      if (body.ops.filter((/** @type {any} */ op) => op.op === "assert_count").length > 16) fail(400, "invalid_request");
      /** @type {Set<string>} */
      const targets = new Set();
      for (const [index, op] of body.ops.entries()) {
        if (op.id === undefined) continue;
        const target = `${op.collection}/${op.id}`;
        if (targets.has(target)) fail(400, "invalid_request", { op: index });
        targets.add(target);
      }
      const work = view(db);
      const results = body.ops.map((/** @type {any} */ op, /** @type {number} */ index) => applyTxOp(op, index, work));
      const offset = work.commit();
      return { status: 200, headers: json, body: stringify({ offset: String(offset), durability: "local_durable", results }) };
    }
    if (rest === "/shapes" && request.method === "POST") {
      gatekeeperCalls++;
      const body = bodyObject(request.body);
      for (const member of Object.keys(body)) if (!["collection", "filter", "project"].includes(member)) fail(400, "invalid_request");
      if (typeof body.collection !== "string" || !namePattern.test(body.collection)) fail(400, "invalid_request");
      authorize(grant, db, body.collection, "shape");
      if (body.filter !== undefined) validateFilter(body.filter);
      if (!validProjection(body.project)) fail(400, "invalid_request");
      const shapeId = sha256(`TB-SHAPE-V1\0${db}\0${body.collection}\0${stringify(body.filter ?? null)}\0${stringify(body.project ?? null)}`).slice(0, 32);
      const existing = shapes.get(shapeId);
      shapes.set(shapeId, { db, collection: body.collection, filter: body.filter, project: body.project, handle: existing?.handle ?? `h1-${randomBytes(6).toString("hex")}` });
      const token = `fxshape.${shapeId}.${randomBytes(12).toString("base64url")}`;
      const expiresAt = Date.now() + settings.tokenLifetimeMs;
      tokens.set(token, { shapeId, expiresAt, revoked: false });
      return {
        status: 201, headers: json,
        body: stringify({ shape_id: shapeId, handle: shapes.get(shapeId)?.handle, offset: "-1", url: `${edgeOrigin}/v1/shapes/${shapeId}?offset=-1`, token, expires_at: String(expiresAt) }),
      };
    }
    return fail(404, "unknown_route");
  }

  /**
   * @param {{ db: string; collection: string; filter: any; project: any; handle: string }} shape
   * @param {number} from
   * @returns {{ next: number; changes: any[] }}
   */
  function chunk(shape, from) {
    const state = database(shape.db);
    const end = Math.min(state.head, from + 4096);
    const changes = [];
    for (const commit of state.log) {
      if (commit.offset <= from || commit.offset > end) continue;
      const ops = [];
      for (const change of commit.changes) {
        if (change.collection !== shape.collection) continue;
        const before = change.before !== null && matches(shape.filter, change.before, change.id);
        const after = change.after !== null && matches(shape.filter, change.after, change.id);
        if (after) ops.push({ op: "upsert", id: change.id, version: change.version, document: projectDocument(change.after, shape.project) });
        else if (before) ops.push({ op: "delete", id: change.id });
      }
      if (ops.length > 0) changes.push({ offset: String(commit.offset), ops });
    }
    return { next: end, changes };
  }

  /**
   * @param {string} shapeId
   * @param {{ db: string; collection: string; filter: any; project: any; handle: string }} shape
   * @param {number} snapshot
   * @param {number} page
   * @returns {any}
   */
  function snapshotPage(shapeId, shape, snapshot, page) {
    const docs = snapshots.get(`${shape.handle}:${snapshot}`);
    if (docs === undefined) fail(409, "must_refetch", { handle: shape.handle });
    const pages = Math.max(1, Math.ceil(docs.length / settings.snapshotPageSize));
    if (page >= pages) fail(400, "invalid_request");
    const slice = docs.slice(page * settings.snapshotPageSize, (page + 1) * settings.snapshotPageSize);
    const next = page + 1 < pages ? `snap.${snapshot}.${page + 1}` : String(snapshot);
    return {
      shape_id: shapeId, handle: shape.handle, offset: `snap.${snapshot}.${page}`, next_offset: next,
      up_to_date: next === String(database(shape.db).head),
      // An empty snapshot is a single page whose change has ops:[] (shapes-v1.md).
      changes: [{ offset: String(snapshot), ops: slice }],
    };
  }

  /**
   * @param {{ db: string; collection: string; filter: any; project: any; handle: string }} shape
   * @returns {number}
   */
  function materialize(shape) {
    const head = database(shape.db).head;
    const docs = collectionEntries(shape.db, shape.collection, false)
      .filter((entry) => matches(shape.filter, entry.doc, entry.id))
      .map((entry) => ({ op: "upsert", id: entry.id, version: entry.version, document: projectDocument(entry.doc, shape.project) }))
      .sort((a, b) => (a.id < b.id ? -1 : 1));
    snapshots.set(`${shape.handle}:${head}`, docs);
    return head;
  }

  /**
   * @param {string} db
   * @param {number} timeoutMs
   * @param {AbortSignal} signal
   * @returns {Promise<void>}
   */
  function waitForChange(db, timeoutMs, signal) {
    return new Promise((resolveWait) => {
      /** @param {string} [changed] */
      const done = (changed) => {
        if (changed !== undefined && changed !== db) return;
        clearTimeout(timer);
        events.off("commit", done);
        events.off("rotate", done);
        signal.removeEventListener("abort", stop);
        resolveWait();
      };
      const stop = () => done();
      const timer = setTimeout(done, timeoutMs);
      events.on("commit", done);
      events.on("rotate", done);
      signal.addEventListener("abort", stop, { once: true });
    });
  }

  /**
   * @param {string} shapeId
   * @param {Record<string, string>} query
   * @param {AbortSignal} signal
   * @returns {Promise<FixtureResponse>}
   */
  async function readShape(shapeId, query, signal) {
    const shape = shapes.get(shapeId);
    if (shape === undefined) fail(404, "not_found");
    for (const member of Object.keys(query)) if (!["handle", "offset", "live", "cursor"].includes(member)) fail(400, "invalid_request");
    const offset = query.offset;
    if (offset === undefined) fail(400, "invalid_request");
    if (offset === "-1") {
      const head = materialize(shape);
      return { status: 307, headers: { location: `?handle=${encodeURIComponent(shape.handle)}&offset=snap.${head}.0`, "cache-control": "public, max-age=0, s-maxage=5" }, body: "" };
    }
    if (query.handle === undefined) fail(400, "invalid_request");
    if (query.handle !== shape.handle) fail(409, "must_refetch", { handle: shape.handle });
    /** @param {any} body @param {string} cache @param {Record<string, string>} [extra] */
    const respond = (body, cache, extra = {}) => ({
      status: 200,
      headers: {
        "content-type": "application/json", "cache-control": cache, "tb-shape-handle": shape.handle,
        "tb-shape-next-offset": body.next_offset, "tb-shape-up-to-date": String(body.up_to_date),
        etag: `"${shapeId}:${shape.handle}:${body.offset}:${body.next_offset}"`, ...extra,
      },
      body: stringify(body),
    });
    const snap = /^snap\.(\d+)\.(\d+)$/u.exec(offset);
    if (snap !== null) {
      if (query.live !== undefined || query.cursor !== undefined) fail(400, "invalid_request");
      return respond(snapshotPage(shapeId, shape, Number(snap[1]), Number(snap[2])), "public, max-age=60, s-maxage=31536000, immutable");
    }
    if (!/^(?:0|[1-9]\d*)$/u.test(offset)) fail(400, "invalid_request");
    const from = Number(offset);
    const live = query.live === "true";
    if ((query.live !== undefined && !live) || (query.cursor !== undefined && !live)) fail(400, "invalid_request");
    const state = database(shape.db);
    if (from > state.head) fail(400, "invalid_request");
    if (live && state.head <= from) {
      await waitForChange(shape.db, settings.liveTimeoutMs, signal);
      if (query.handle !== shape.handle) fail(409, "must_refetch", { handle: shape.handle });
    }
    const { next, changes } = chunk(shape, from);
    const body = { shape_id: shapeId, handle: shape.handle, offset: String(from), next_offset: String(next), up_to_date: next === state.head, changes };
    if (!live) return respond(body, next === from + 4096 ? "public, max-age=60, s-maxage=31536000, immutable" : "public, max-age=0, s-maxage=1");
    const bucket = Math.floor(Date.now() / 20_000);
    return respond(body, `public, max-age=0, s-maxage=${20 - (Math.floor(Date.now() / 1000) % 20)}`, { "tb-shape-cursor": String(bucket) });
  }

  /**
   * @param {string} shapeId
   * @param {RecordedRequest} request
   * @param {import("node:http").IncomingMessage} req
   * @param {import("node:http").ServerResponse} res
   */
  async function streamShape(shapeId, request, req, res) {
    const shape = shapes.get(shapeId);
    if (shape === undefined) fail(404, "not_found");
    const offset = request.headers["last-event-id"] ?? request.query.offset;
    if (offset === undefined) fail(400, "invalid_request");
    if (offset !== "-1" && request.query.handle === undefined) fail(400, "invalid_request");
    const snap = /^snap\.(\d+)\.(\d+)$/u.exec(offset);
    if (offset !== "-1" && snap === null && !/^(?:0|[1-9]\d*)$/u.test(offset)) fail(400, "invalid_request");
    const handle = offset === "-1" ? shape.handle : /** @type {string} */ (request.query.handle);
    const abort = new AbortController();
    req.on("close", () => abort.abort());
    res.writeHead(200, { "content-type": "text/event-stream", "cache-control": "no-store" });
    sseStreams.add(res);
    res.on("close", () => { sseStreams.delete(res); abort.abort(); });
    const eol = settings.sseLineEnding;
    /** @param {string} event @param {any} data @param {string} [id] */
    const send = (event, data, id) => {
      let payload = stringify(data);
      if (event === "changes" && corruptSseChanges > 0) {
        // The event is consumed by the client but cannot be applied.
        corruptSseChanges--;
        payload = stringify({ shape_id: shapeId, handle, offset: data.offset });
      }
      res.write(`event: ${event}${eol}${id === undefined ? "" : `id: ${id}${eol}`}data: ${payload}${eol}${eol}`);
    };
    const refetch = () => {
      send("must-refetch", { handle: shape.handle });
      res.end();
    };
    if (shape.handle !== handle) return refetch();
    /** @type {number} */
    let from;
    if (offset === "-1" || snap !== null) {
      // offset=-1 streams the snapshot directly; a snap.S.P resume point
      // (Last-Event-ID of a snapshot page) continues from page P of snapshot S.
      const head = snap === null ? materialize(shape) : Number(snap[1]);
      if (!snapshots.has(`${shape.handle}:${head}`)) return refetch();
      for (let page = snap === null ? 0 : Number(snap[2]); ; page++) {
        const body = snapshotPage(shapeId, shape, head, page);
        send("changes", body, body.next_offset);
        if (!body.next_offset.startsWith("snap.")) break;
      }
      from = head;
    } else from = Number(offset);
    /** @type {number | undefined} */
    let announced;
    while (!abort.signal.aborted) {
      if (shape.handle !== handle) return refetch();
      const state = database(shape.db);
      if (state.head > from) {
        const { next, changes } = chunk(shape, from);
        const body = { shape_id: shapeId, handle, offset: String(from), next_offset: String(next), up_to_date: next === state.head, changes };
        if (changes.length > 0) {
          send("changes", body, String(next));
          if (body.up_to_date) announced = next;
        }
        from = next;
        continue;
      }
      // Caught up: announce the head whenever it moved, including past
      // commits with no change for this shape, so the client's offset advances.
      if (announced !== from) {
        send("up-to-date", { next_offset: String(from) });
        announced = from;
      }
      const before = state.head;
      await waitForChange(shape.db, settings.sseHeartbeatMs, abort.signal);
      if (state.head === before && shape.handle === handle && !abort.signal.aborted) res.write(`: keepalive${eol}${eol}`);
    }
    return undefined;
  }


  /**
   * @param {"origin" | "edge"} server
   * @param {import("node:http").IncomingMessage} req
   * @param {import("node:http").ServerResponse} res
   */
  async function handle(server, req, res) {
    const chunks = [];
    let size = 0;
    for await (const part of req) {
      size += part.length;
      chunks.push(part);
    }
    const url = new URL(req.url ?? "/", "http://fixture");
    /** @type {Record<string, string>} */
    const query = {};
    let duplicate = false;
    for (const [name, value] of url.searchParams) {
      if (Object.hasOwn(query, name) || value === "") duplicate = true;
      query[name] = value;
    }
    /** @type {RecordedRequest} */
    const request = {
      server, method: req.method ?? "GET", path: url.pathname, query,
      headers: Object.fromEntries(["authorization", "accept", "content-type", "if-match", "if-none-match", "idempotency-key", "last-event-id"].map((name) => {
        const value = req.headers[name];
        return [name, Array.isArray(value) ? value.join(",") : value];
      })),
      body: Buffer.concat(chunks).toString("utf8"),
    };
    requests.push(request);
    /** @param {FixtureResponse} response */
    const send = (response) => {
      res.writeHead(response.status, response.headers);
      res.end(response.body);
    };
    /** @param {FixtureError} error */
    const sendError = (error) => {
      const { etag, retryAfter, ...extra } = /** @type {Record<string, unknown>} */ (error.extra);
      /** @type {Record<string, string>} */
      const headers = { "content-type": "application/json", "cache-control": "no-store" };
      if (typeof etag === "string") headers.etag = `"${etag}"`;
      if (typeof retryAfter === "number") headers["retry-after"] = String(retryAfter);
      send({ status: error.status, headers, body: stringify({ error: { code: error.code, ...extra } }) });
    };
    try {
      if (size > 4 * 1024 * 1024) fail(413, "payload_too_large");
      if (duplicate) fail(400, "invalid_request");
      const failureIndex = failures.findIndex((failure) => failure.match(request));
      const failure = failureIndex < 0 ? undefined : failures.splice(failureIndex, 1)[0];
      if (failure?.truncate === true) {
        res.writeHead(200, { "content-type": "application/json", "content-length": "4096" });
        res.write('{"id":"');
        setTimeout(() => res.destroy(), 10);
        return;
      }
      if (failure?.body !== undefined) {
        send({ status: failure.status, headers: { "content-type": "application/json" }, body: failure.body });
        return;
      }
      const injected = () => new FixtureError(/** @type {InjectedFailure} */ (failure).status, /** @type {InjectedFailure} */ (failure).code,
        failure?.retryAfter === undefined ? {} : { retryAfter: failure.retryAfter });
      if (failure !== undefined && failure.afterCommit !== true) throw injected();
      const shapeMatch = /^\/v1\/shapes\/([0-9a-f]{32})(\/sse)?$/u.exec(url.pathname);
      if (server === "edge") {
        if (shapeMatch === null || shapeMatch[2] !== undefined || request.method !== "GET") fail(404, "unknown_route");
        const token = tokens.get(request.headers.authorization?.replace(/^Bearer /u, "") ?? "");
        if (token === undefined || token.revoked || token.expiresAt <= Date.now()) fail(401, "invalid_token");
        if (token.shapeId !== shapeMatch[1]) fail(403, "forbidden_scope");
        const abort = new AbortController();
        res.on("close", () => abort.abort());
        send(await readShape(/** @type {string} */ (shapeMatch[1]), query, abort.signal));
        return;
      }
      const dataKey = keys.get(request.headers.authorization?.replace(/^Bearer /u, "") ?? "");
      if (dataKey === undefined || dataKey.revoked) fail(401, "invalid_key");
      const grant = dataKey.grant;
      if (shapeMatch !== null) {
        if (request.method !== "GET") fail(405, "method_not_allowed");
        const shape = shapes.get(/** @type {string} */ (shapeMatch[1]));
        if (shape !== undefined) authorize(grant, shape.db, shape.collection, "shape");
        if (shapeMatch[2] !== undefined) {
          await streamShape(/** @type {string} */ (shapeMatch[1]), request, req, res);
          return;
        }
        const abort = new AbortController();
        res.on("close", () => abort.abort());
        send(await readShape(/** @type {string} */ (shapeMatch[1]), query, abort.signal));
        return;
      }
      const dataMatch = /^\/v1\/databases\/([^/]+)(?:\/collections\/([^/]+))?(\/.*)$/u.exec(url.pathname);
      if (dataMatch === null) fail(404, "unknown_route");
      const idempotencyKey = request.headers["idempotency-key"];
      const mutating = request.method !== "GET" && !(dataMatch[3] === "/query") && !(dataMatch[3] === "/shapes");
      if (idempotencyKey !== undefined && mutating) {
        if (!/^[A-Za-z0-9_.-]{1,64}$/u.test(idempotencyKey)) fail(400, "invalid_request");
        const storeKey = `${request.method} ${url.pathname} ${idempotencyKey}`;
        const hash = sha256(`${request.headers["content-type"] ?? ""}\0${request.headers["if-match"] ?? ""}\0${request.headers["if-none-match"] ?? ""}\0${request.body}`);
        const previous = idempotency.get(storeKey);
        if (previous !== undefined) {
          if (previous.hash !== hash) fail(409, "idempotency_conflict");
          send(previous);
          return;
        }
        let response;
        try {
          response = dataRoute(request, dataMatch, grant);
        } catch (error) {
          if (error instanceof FixtureError) {
            sendError(error);
            return;
          }
          throw error;
        }
        idempotency.set(storeKey, { ...response, hash });
        if (failure !== undefined) throw injected();
        send(response);
        return;
      }
      const response = dataRoute(request, dataMatch, grant);
      if (failure !== undefined) throw injected();
      send(response);
    } catch (error) {
      if (error instanceof FixtureError) {
        if (res.headersSent) res.end();
        else sendError(error);
        return;
      }
      if (!res.headersSent) res.writeHead(500, { "content-type": "application/json" });
      // An unexpected fault: api-v1.md's 500 internal_error.
      res.end(stringify({ error: { code: "internal_error", message: String(error) } }));
    }
  }

  /**
   * @param {"origin" | "edge"} name
   * @returns {Promise<{ server: import("node:http").Server; origin: string }>}
   */
  async function listen(name) {
    // A client may abort while its body is still uploading; never leave the
    // rejection unhandled.
    const server = createServer((req, res) => {
      handle(name, req, res).catch(() => {
        if (!res.headersSent) res.writeHead(400, { "content-type": "application/json" });
        res.end();
      });
    });
    await new Promise((resolveListen) => server.listen(0, "127.0.0.1", () => resolveListen(undefined)));
    const address = /** @type {import("node:net").AddressInfo} */ (server.address());
    return { server, origin: `http://127.0.0.1:${address.port}` };
  }

  const origin = await listen("origin");
  const edge = await listen("edge");
  edgeOrigin = edge.origin;

  return {
    baseUrl: origin.origin,
    edgeUrl: edge.origin,
    token: fixtureToken,
    settings,
    requests,
    /** @returns {number} */
    gatekeeperCalls: () => gatekeeperCalls,
    /** @param {InjectedFailure} failure */
    failNext(failure) {
      failures.push(failure);
    },
    /**
     * Issues another data key with the given grant.
     * @param {FixtureGrant} grant
     * @returns {string}
     */
    issueKey(grant) {
      const id = String(++keyCounter).padStart(22, "k");
      const token = `tbk1.${id}.${randomBytes(32).toString("base64url").slice(0, 43)}`;
      keys.set(token, { grant, revoked: false });
      return token;
    },
    /**
     * Revokes a data key; its next request fails with 401 invalid_key.
     * @param {string} token
     */
    revokeKey(token) {
      const record = keys.get(token);
      if (record === undefined) throw new Error("unknown key");
      record.revoked = true;
    },
    /** @param {string} shapeId */
    rotateShapeHandle(shapeId) {
      const shape = shapes.get(shapeId);
      if (shape === undefined) throw new Error("unknown shape");
      shape.handle = `h1-${randomBytes(6).toString("hex")}`;
      events.emit("rotate");
    },
    revokeShapeTokens() {
      for (const token of tokens.values()) token.revoked = true;
    },
    /** Replaces the next SSE changes event's data with an unappliable body. */
    corruptNextSseChanges() {
      corruptSseChanges++;
    },
    dropSseStreams() {
      for (const stream of sseStreams) stream.destroy();
    },
    async close() {
      for (const stream of sseStreams) stream.destroy();
      await Promise.all([origin.server, edge.server].map((server) => new Promise((resolveClose) => {
        server.closeAllConnections();
        server.close(() => resolveClose(undefined));
      })));
    },
  };
}
