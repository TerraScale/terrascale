import { array, boolean, malformed, object, string } from "./decode.js";
import { isTerraBaseV1Error } from "./errors.js";
import { decodeSqlValue } from "./sql-execute.js";
import { ShapeCdn } from "./shape-cdn.js";
import { subscribeShape } from "./shape-subscriptions.js";
import { OpaqueRedirectError } from "./transport.js";
/**
 * @import { JsonObject } from "./json.js"
 * @import { V1SqlValue } from "./sql-execute.js"
 * @import { V1Transport } from "./transport.js"
 */

/** @typedef {Readonly<Record<string, V1SqlValue>>} V1TableRow */
/**
 * @template {V1TableRow} [T=V1TableRow]
 * @typedef {object} V1TableShapeState
 * @property {ReadonlyMap<string, T>} rows Every row of the table, keyed by row id as a decimal string; a live view that later changes update.
 * @property {boolean} upToDate True once the shape reached the table's head.
 * @property {string} offset The commit boundary the rows are at.
 * @property {ReadonlySet<string>} provisional Keys whose row shows a write made in this region that the database's home has not confirmed yet; a rejected write disappears.
 */
/**
 * @typedef {object} V1TableShapeRequest
 * @property {string | undefined} [where] A SQL condition over the table's columns; only matching rows are in the shape.
 * @property {readonly string[] | undefined} [columns] The columns each row carries; default every stored column.
 */
/**
 * @typedef {object} V1TableShapeSubscribeOptions
 * @property {AbortSignal | undefined} [signal] Closes the subscription when aborted.
 */

const tablePattern = /^(?!_tb_|turso_|sqlite_)[A-Za-z_][A-Za-z0-9_]{0,62}$/u;
const retryable = new Set([408, 429, 500, 502, 503, 504]);
const terminal = new Set(["invalid_request", "unsupported_table", "unknown_database", "unknown_table", "invalid_key", "key_revoked", "key_expired", "forbidden_scope", "not_granted", "column_not_granted", "grant_invalid"]);

/** @param {number} attempt @param {number | undefined} retryAfter */
function delay(attempt, retryAfter) {
  return retryAfter !== undefined ? retryAfter * 1000 : Math.min(30_000, 250 * 2 ** attempt) * (0.5 + Math.random() / 2);
}

/** @param {number} ms @param {AbortSignal} signal */
function sleep(ms, signal) {
  return new Promise((resolve, reject) => {
    const done = () => { clearTimeout(timer); reject(signal.reason); };
    const timer = setTimeout(() => { signal.removeEventListener("abort", done); resolve(undefined); }, ms);
    signal.addEventListener("abort", done, { once: true });
  });
}

/** One SQL table's free-read shape: a snapshot, then live changes. */
export class V1TableShape {
  /** @type {V1Transport} */
  #transport;
  /** @type {Readonly<Record<string, string>>} */
  #params;
  /** @type {Readonly<Record<string, string>>} */
  #filter;

  /** @param {V1Transport} transport @param {string} database @param {string} table @param {V1TableShapeRequest} [request] */
  constructor(transport, database, table, request = {}) {
    if (typeof table !== "string" || !tablePattern.test(table)) throw new TypeError("A table name is 1 to 63 letters, digits and underscores, not starting with a digit or a reserved engine prefix.");
    if (request === null || typeof request !== "object" || Array.isArray(request)) throw new TypeError("A shape request is { where?, columns? }.");
    const { where, columns, ...extra } = request;
    for (const member of Object.keys(extra)) throw new TypeError(`Unknown shape member ${member}.`);
    /** @type {Record<string, string>} */
    const filter = {};
    if (where !== undefined) {
      if (typeof where !== "string" || where.trim() === "") throw new TypeError("where is a non-empty SQL condition.");
      filter.where = where;
    }
    if (columns !== undefined) {
      if (!Array.isArray(columns) || columns.length === 0 || columns.some(column => typeof column !== "string" || !/^[A-Za-z_][A-Za-z0-9_]*$/u.test(column)) || new Set(columns).size !== columns.length)
        throw new TypeError("columns is a non-empty list of distinct column names.");
      filter.columns = columns.join(",");
    }
    this.#transport = transport;
    this.#params = Object.freeze({ database, table });
    this.#filter = Object.freeze(filter);
  }

  /**
   * Calls `listener` with the whole table after the snapshot and after every change, until closed.
   * @template {V1TableRow} [T=V1TableRow]
   * @param {(state: V1TableShapeState<T>) => void} listener
   * @param {V1TableShapeSubscribeOptions} [options]
   * @returns {{ close: () => void; done: Promise<void> }}
   */
  subscribe(listener, options = {}) {
    if (typeof listener !== "function") throw new TypeError("subscribe takes a listener function.");
    return subscribeShape(this.#transport, JSON.stringify([this.#params, this.#filter]),
      (publish, signal) => this.#run(publish, signal),
      /** @type {(state: V1TableShapeState) => void} */ (listener), options.signal);
  }

  /** @param {(state: V1TableShapeState) => void} listener @param {AbortSignal} signal */
  async #run(listener, signal) {
    /** @type {Map<string, V1TableRow>} */
    let rows = new Map();
    /** @type {{ id: string; ops: import("./json.js").JsonValue[] }[]} */
    let pending = [];
    let local = "0";
    let skipHint = false;
    let handle = "";
    let offset = "-1";
    let upToDate = false;
    let cdn = new ShapeCdn(this.#transport, this.#params, this.#filter);
    /** @type {string | undefined} */
    let cursor;
    let hintProtocol;
    let hintRetryAt = 0;
    let hintedOffset = "";
    /** @type {import("./shape-hints.js").ShapeHints | undefined} */
    let hints;
    try {
    for (let attempt = 0; !signal.aborted;) {
      if (upToDate && !skipHint && hintProtocol === "terrascale.shape.v1" && Date.now() >= hintRetryAt) {
        try {
          hints ??= await this.#transport.openShapeHints(this.#params, new URLSearchParams({ ...this.#filter, handle, offset }), signal);
          if (hints !== undefined) {
            const hint = await hints.wait(handle, offset, local, signal);
            if (hint.local !== undefined) skipHint = true;
            else {
              cdn.offer(handle, hint.manifest);
              hintedOffset = hint.next_offset;
              upToDate = false;
            }
          }
        } catch (error) {
          hints?.close();
          hints = undefined;
          if (signal.aborted) return;
          if (isTerraBaseV1Error(error) && error.code === "must_refetch") {
            rows = new Map(); pending = []; local = "0"; handle = ""; offset = "-1"; upToDate = false; cursor = undefined;
            cdn = new ShapeCdn(this.#transport, this.#params, this.#filter);
            continue;
          }
          if (isTerraBaseV1Error(error) && terminal.has(error.code)) throw error;
          const pause = delay(attempt++, isTerraBaseV1Error(error) ? error.retryAfter : undefined);
          hintRetryAt = Date.now() + Math.max(1000, pause);
          await sleep(pause, signal);
        }
      }
      const query = new URLSearchParams({ ...this.#filter, ...(handle === "" ? { offset } : { handle, offset }) });
      if (upToDate && !offset.startsWith("snap.")) {
        query.set("live", "true");
        if (cursor !== undefined) query.set("cursor", cursor);
        query.set("local", local);
      }
      skipHint = false;
      /** @type {import("./transport.js").V1CallResult} */
      let response;
      try {
        const cached = handle !== "" && !upToDate ? await cdn.read(handle, offset, signal) : undefined;
        try {
          response = cached ?? await this.#transport.call("readTableShape", this.#params, { query, signal });
        } catch (error) {
          if (!(error instanceof OpaqueRedirectError)) throw error;
          response = await this.#transport.call("readTableShape", this.#params, { query, signal, redirect: "follow" });
        }
        if (response.status === 307) {
          const base = await this.#transport.url("readTableShape", this.#params, signal);
          const location = new URL(response.headers.get("location") ?? "", base);
          const original = new URL(base);
          const nextHandle = location.searchParams.get("handle");
          const nextOffset = location.searchParams.get("offset");
          if (handle !== "" || location.origin !== original.origin || location.pathname !== original.pathname
            || nextHandle === null || !/^h1-[a-f0-9]{16}$/u.test(nextHandle) || nextOffset === null || !/^snap\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$/u.test(nextOffset))
            malformed("shape bootstrap redirect");
          handle = nextHandle;
          offset = nextOffset;
          continue;
        }
      } catch (error) {
        if (signal.aborted) return;
        if (isTerraBaseV1Error(error, "must_refetch")) {
          rows = new Map();
          pending = [];
          local = "0";
          handle = "";
          offset = "-1";
          upToDate = false;
          cursor = undefined;
          cdn = new ShapeCdn(this.#transport, this.#params, this.#filter);
          continue;
        }
        if (isTerraBaseV1Error(error) && (terminal.has(error.code) || !retryable.has(error.status))) throw error;
        await sleep(delay(attempt++, isTerraBaseV1Error(error) ? error.retryAfter : undefined), signal);
        continue;
      }
      attempt = 0;
      hintProtocol = response.headers.get("tb-shape-hints") ?? hintProtocol;
      const page = object(response.value, "shape page");
      const pageHandle = string(page.handle, "shape handle");
      if (handle !== "" && pageHandle !== handle) malformed("shape handle");
      handle = pageHandle;
      for (const change of array(page.changes, "shape changes")) {
        const commit = object(change, "shape change");
        applyOps(rows, array(commit.ops, "shape ops"));
        for (const name of ["confirmed", "revert"]) {
          if (commit[name] === undefined) continue;
          const settled = new Set(array(commit[name], `shape ${name}`).map(id => string(id, `shape ${name} id`)));
          pending = pending.filter(transaction => !settled.has(transaction.id));
        }
      }
      const previousLocal = local;
      if (page.provisional !== undefined) {
        pending = array(page.provisional, "shape provisional").map(entry => {
          const transaction = object(entry, "shape provisional transaction");
          return { id: string(transaction.id, "shape provisional id"), ops: array(transaction.ops, "shape provisional ops") };
        });
        local = string(page.local, "shape local");
        if (!/^(?:0|[a-f0-9]{16})$/u.test(local)) malformed("shape local");
      }
      const previousOffset = offset;
      offset = string(page.next_offset, "shape next_offset");
      upToDate = boolean(page.up_to_date, "shape up_to_date") || offset === hintedOffset;
      cursor = response.headers.get("tb-shape-cursor") ?? cursor;
      if (!offset.startsWith("snap.")) listener(Object.freeze({ ...view(rows, pending), upToDate, offset }));
      if (query.has("live") && offset === previousOffset && local === previousLocal) await sleep(1000 + Math.random() * 250, signal);
    }
    } finally { hints?.close(); }
  }
}

/** @param {Map<string, V1TableRow>} rows @param {import("./json.js").JsonValue[]} ops @param {Set<string>} [touched] */
function applyOps(rows, ops, touched) {
  for (const entry of ops) {
    const op = object(entry, "shape op");
    const key = string(op.key, "shape key");
    if (op.move !== undefined && op.move !== "in" && op.move !== "out") malformed("shape move");
    if (op.move === "out" || (op.move === undefined && op.op === "delete")) { rows.delete(key); touched?.delete(key); }
    else if (op.move === "in" || op.op === "insert" || op.op === "update") { rows.set(key, decodeRow(object(op.row, "shape row"))); touched?.add(key); }
    else malformed("shape op");
  }
}

/**
 * The confirmed rows with the region's pending local writes applied on top, in commit order.
 * @param {Map<string, V1TableRow>} rows @param {{ id: string; ops: import("./json.js").JsonValue[] }[]} pending
 */
function view(rows, pending) {
  if (pending.length === 0) return { rows, provisional: Object.freeze(new Set()) };
  const merged = new Map(rows);
  /** @type {Set<string>} */
  const touched = new Set();
  for (const transaction of pending) applyOps(merged, transaction.ops, touched);
  return { rows: merged, provisional: Object.freeze(touched) };
}

/** @param {JsonObject} row @returns {V1TableRow} */
function decodeRow(row) {
  return Object.freeze(Object.fromEntries(Object.entries(row).map(([name, value]) => [name, decodeSqlValue(value)])));
}
