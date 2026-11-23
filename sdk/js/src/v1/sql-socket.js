import { array, boolean, malformed, object, string } from "./decode.js";
import { listenForSqlRefusal } from "./sql-connect-retry.js";
import { SqlError } from "./errors.js";
import { parseExactJson, stringifyExactJson } from "./json.js";
import { decodeSqlFrameResult, encodeStatement, sqlRowObjects, sqlStatement } from "./sql-execute.js";
import { requireBookmark, requireConsistency } from "./bookmark.js";
/**
 * @import { JsonObject, JsonValue } from "./json.js"
 * @import { V1SqlArguments, V1SqlExecuteResult, V1SqlStatement, V1SqlValue } from "./sql-execute.js"
 * @import { V1BookmarkTracker, V1Consistency } from "./bookmark.js"
 */
/** @typedef {{ readonly bookmark?: string | undefined; readonly consistency?: V1Consistency | undefined }} V1SqlFrameOptions */
/** @typedef {{ readonly bookmarks: V1BookmarkTracker; readonly consistency: V1Consistency | undefined }} V1SqlSession */

/**
 * The socket closed before an answer arrived. `closeCode` is the WebSocket close code, such as 1008 after a
 * revoked key or 1001 when the server restarts. A statement in flight may or may not have committed.
 */
export class SqlSocketClosedError extends Error {
  /** @type {number} */
  closeCode;
  /** @type {string} */
  reason;

  /** @param {number} closeCode @param {string} reason */
  constructor(closeCode, reason) {
    super(`SQL WebSocket closed (${closeCode}${reason === "" ? "" : `: ${reason}`}).`);
    this.name = "SqlSocketClosedError";
    this.closeCode = closeCode;
    this.reason = reason;
  }
}

export const sqlSocketProtocol = "terrascale.sql.v2";
const maximumStatements = 256;
const maximumFrameBytes = 1024 * 1024;
const statusByCode = new Map([
  ["forbidden_scope", 403], ["sql_sandbox", 403], ["rule_denied", 403], ["serialization_conflict", 409], ["row_conflict", 409],
  ["schema_mismatch", 412], ["too_large", 413], ["quota_exceeded", 429], ["tenant_busy", 429], ["unauthorized", 401],
  ["write_outcome_unknown", 503], ["unavailable", 503], ["storage_busy", 503], ["request_timeout", 504], ["internal_error", 500],
]);

/**
 * @typedef {object} SqlSocketLike
 * @property {string} protocol
 * @property {number} readyState
 * @property {(data: string) => void} send
 * @property {(code?: number, reason?: string) => void} close
 * @property {(type: string, listener: (event: any) => void, options?: { once?: boolean }) => void} addEventListener
 */

/**
 * One SQL session over the TerraScale SQL WebSocket. Requests are answered in order. Outside a transaction every
 * `execute` and `batch` commits on its own, all or nothing; `transaction()` holds one transaction of at most 5 seconds.
 * Every frame carries the newest TerraScale-Bookmark the client holds for the database, and every answer advances it.
 */
export class V1SqlSocket {
  /** @type {SqlSocketLike} */
  #socket;
  /** @type {V1SqlSession} */
  #session;
  /** @type {Map<number, { resolve: (value: JsonObject) => void; reject: (error: unknown) => void; committing: boolean; control: boolean }>} */
  #pending = new Map();
  #nextId = 1;
  #transaction = false;
  /** @type {SqlSocketClosedError | SqlError | undefined} */
  #closed;

  /** @param {SqlSocketLike} socket @param {V1SqlSession} session */
  constructor(socket, session) {
    this.#socket = socket;
    this.#session = session;
    socket.addEventListener("message", event => this.#receive(event.data));
    socket.addEventListener("close", event => this.#fail(new SqlSocketClosedError(event.code ?? 1006, event.reason ?? "")));
    socket.addEventListener("error", () => this.#fail(new SqlSocketClosedError(1006, "connection failed")));
  }

  /**
   * @param {SqlSocketLike} socket
   * @param {V1SqlSession} session
   * @param {AbortSignal | undefined} signal
   * @param {string} [token] Sent as the first frame when the upgrade carried no Authorization header.
   * @returns {Promise<V1SqlSocket>}
   */
  static async open(socket, session, signal, token) {
    const connected = await V1SqlSocket.#connect(socket, session, signal);
    if (token === undefined) return connected;
    try {
      await connected.#request({ type: "auth", token });
    } catch (error) {
      connected.close();
      throw error;
    }
    return connected;
  }

  /**
   * @param {SqlSocketLike} socket
   * @param {V1SqlSession} session
   * @param {AbortSignal | undefined} signal
   * @returns {Promise<V1SqlSocket>}
   */
  static #connect(socket, session, signal) {
    return new Promise((resolve, reject) => {
      const abort = () => { socket.close(1000, ""); reject(signal?.reason); };
      if (signal?.aborted) return abort();
      signal?.addEventListener("abort", abort, { once: true });
      listenForSqlRefusal(socket, error => {
        signal?.removeEventListener("abort", abort);
        reject(error);
      });
      socket.addEventListener("error", () => {
        signal?.removeEventListener("abort", abort);
        reject(new SqlSocketClosedError(1006, "connection failed"));
      }, { once: true });
      socket.addEventListener("open", () => {
        signal?.removeEventListener("abort", abort);
        if (socket.protocol !== sqlSocketProtocol) {
          socket.close(1002, "");
          reject(new TypeError(`The server did not select the ${sqlSocketProtocol} subprotocol.`));
          return;
        }
        resolve(new V1SqlSocket(socket, session));
      }, { once: true });
      socket.addEventListener("close", event => {
        signal?.removeEventListener("abort", abort);
        reject(new SqlSocketClosedError(event.code ?? 1006, event.reason ?? ""));
      }, { once: true });
    });
  }

  /** Whether a transaction is open on this socket. */
  get inTransaction() { return this.#transaction; }

  /** Whether the socket is closed. */
  get closed() { return this.#closed !== undefined; }

  /** The newest TerraScale-Bookmark the client holds for this database; empty before the first answer. */
  get bookmark() { return this.#session.bookmarks.current(); }

  /**
   * @param {V1SqlArguments} [args]
   * @param {V1SqlFrameOptions} [options]
   * @returns {Promise<V1SqlExecuteResult>}
   */
  async execute(/** @type {string | V1SqlStatement} */ sql, args, options = {}) {
    const [result] = await this.batch([sqlStatement(sql, args)], options);
    return /** @type {V1SqlExecuteResult} */ (result);
  }

  /**
   * @template [T=Record<string, V1SqlValue>]
   * @param {string} sql
   * @param {V1SqlArguments} [args]
   * @param {V1SqlFrameOptions} [options]
   * @returns {Promise<T[]>}
   */
  async query(sql, args, options = {}) {
    return sqlRowObjects(await this.execute(sql, args, options));
  }

  /**
   * Runs 1 to 256 statements; outside a transaction they commit together or not at all.
   * @param {readonly (string | V1SqlStatement)[]} statements
   * @param {V1SqlFrameOptions} [options]
   * @returns {Promise<readonly V1SqlExecuteResult[]>}
   */
  async batch(statements, options = {}) {
    if (!Array.isArray(statements) || statements.length === 0 || statements.length > maximumStatements)
      throw new TypeError(`SQL batches hold 1 to ${maximumStatements} statements.`);
    const answer = await this.#request({ type: "execute", statements: statements.map(encodeStatement), ...this.#causality(options) });
    const results = array(answer.results, "SQL results").map(decodeSqlFrameResult);
    if (results.length !== statements.length) malformed("SQL result count");
    return Object.freeze(results);
  }

  /**
   * Opens a transaction; `consistency: "strong"` makes it a 2-of-3 quorum transaction that commits or fails with serialization_conflict.
   * @param {V1SqlFrameOptions} [options]
   * @returns {Promise<void>}
   */
  async begin(options = {}) { await this.#request({ type: "begin", ...this.#causality(options) }); }

  /** @returns {Promise<void>} */
  async commit() { await this.#request({ type: "commit" }); }

  /** @returns {Promise<void>} */
  async rollback() { await this.#request({ type: "rollback" }); }

  /**
   * Runs `work` in one transaction: commits when it resolves and rolls back when it throws.
   * @template T
   * @param {(socket: V1SqlSocket) => Promise<T>} work
   * @param {V1SqlFrameOptions} [options]
   * @returns {Promise<T>}
   */
  async transaction(work, options = {}) {
    if (typeof work !== "function") throw new TypeError("transaction takes an async function.");
    await this.begin(options);
    /** @type {T} */
    let value;
    try {
      value = await work(this);
    } catch (error) {
      if (this.#transaction && this.#closed === undefined) await this.rollback().catch(() => undefined);
      throw error;
    }
    await this.commit();
    return value;
  }

  /** Closes the socket; an open transaction rolls back on the server. */
  close() {
    if (this.#closed === undefined) this.#socket.close(1000, "");
    this.#fail(new SqlSocketClosedError(1000, ""));
  }

  [Symbol.asyncDispose]() {
    this.close();
    return Promise.resolve();
  }

  /**
   * @param {V1SqlFrameOptions} options
   * @returns {{ bookmark?: string; consistency?: V1Consistency }}
   */
  #causality(options) {
    if (options === null || typeof options !== "object" || Array.isArray(options)) throw new TypeError("SQL frame options must be an object.");
    for (const member of Object.keys(options))
      if (member !== "bookmark" && member !== "consistency") throw new TypeError(`Unknown SQL frame option ${member}.`);
    const bookmark = requireBookmark(options.bookmark) ?? this.#session.bookmarks.current();
    const consistency = requireConsistency(options.consistency) ?? this.#session.consistency;
    return { ...(bookmark === "" ? {} : { bookmark }), ...(consistency === undefined ? {} : { consistency }) };
  }

  /**
   * @param {{ type: "auth" | "execute" | "begin" | "commit" | "rollback"; statements?: unknown[]; token?: string; bookmark?: string; consistency?: V1Consistency }} request
   * @returns {Promise<JsonObject>}
   */
  #request(request) {
    if (this.#closed !== undefined) return Promise.reject(this.#closed);
    const id = this.#nextId++;
    const type = request.type;
    const frame = stringifyExactJson(/** @type {any} */ ({ id, ...request }), "SQL frame");
    if (new TextEncoder().encode(frame).byteLength > maximumFrameBytes)
      return Promise.reject(new TypeError(`A SQL WebSocket frame holds at most ${maximumFrameBytes} bytes.`));
    return new Promise((resolve, reject) => {
      const committing = type === "commit" || type === "execute" && (!this.#transaction || [...this.#pending.values()].some(pending => pending.control));
      this.#pending.set(id, { resolve, reject, committing, control: ["begin", "commit", "rollback"].includes(type) });
      try { this.#socket.send(frame); }
      catch (error) {
        this.#fail(new SqlSocketClosedError(1006, error instanceof Error ? error.message : "send failed"));
        this.#socket.close();
      }
    });
  }

  /** @param {unknown} data */
  #receive(data) {
    /** @type {JsonObject} */
    let frame;
    try {
      if (typeof data !== "string") throw new TypeError("SQL WebSocket frames are JSON text.");
      frame = object(/** @type {JsonValue} */ (parseExactJson(data)), "SQL frame");
    } catch (error) {
      this.#socket.close(1002, "");
      this.#fail(new SqlSocketClosedError(1002, error instanceof Error ? error.message : "unreadable frame"));
      return;
    }
    const id = frame.id;
    const waiter = typeof id === "number" ? this.#pending.get(id) : undefined;
    if (waiter === undefined) {
      if (frame.type === "error") {
        try { this.#fail(socketError(object(frame.error, "SQL error"))); }
        catch (error) { this.#fail(new SqlSocketClosedError(1002, error instanceof Error ? error.message : "unreadable answer")); }
        this.#socket.close();
      }
      return;
    }
    try {
      this.#transaction = boolean(frame.transaction, "SQL transaction flag");
      if (frame.bookmark != null) this.#session.bookmarks.observe(string(frame.bookmark, "SQL bookmark"));
      if (frame.type === "result") waiter.resolve(frame);
      else if (frame.type === "error") {
        const error = socketError(object(frame.error, "SQL error"));
        if (error.code === "write_outcome_unknown") {
          this.#closed = error;
          this.#fail(error);
          this.#socket.close();
        }
        waiter.reject(error);
      }
      else malformed("SQL frame type");
      this.#pending.delete(/** @type {number} */ (id));
    } catch (error) {
      this.#fail(new SqlSocketClosedError(1002, error instanceof Error ? error.message : "unreadable answer"));
      this.#socket.close(1002);
    }
  }

  /** @param {SqlSocketClosedError | SqlError} error */
  #fail(error) {
    if (this.#closed === undefined) {
      const committing = [...this.#pending.values()].some(pending => pending.committing);
      this.#closed = committing
        ? new SqlError({ status: 503, code: "write_outcome_unknown", sqlstate: "08007", statement: 0, message: "A committing request lost its answer; reconcile before repeating." })
        : this.#transaction
          ? new SqlError({ status: 400, code: "transaction_rolled_back", sqlstate: "40000", statement: 0, message: "The connection lost an open transaction before a committing request was sent." })
          : error;
    }
    this.#transaction = false;
    const waiters = [...this.#pending.values()];
    this.#pending.clear();
    for (const waiter of waiters) waiter.reject(this.#closed);
  }
}

/** @param {JsonObject} error @returns {SqlError} */
function socketError(error) {
  const code = string(error.code, "SQL error code");
  const statement = error.statement ?? 0;
  if (typeof statement !== "number" || !Number.isSafeInteger(statement) || statement < 0) malformed("SQL error statement");
  const sqlstate = string(error.sqlState, "SQL error sqlState");
  if (!/^[0-9A-Z]{5}$/u.test(sqlstate)) malformed("SQL error sqlstate");
  return new SqlError({ status: statusByCode.get(code) ?? 400, code, message: string(error.message, "SQL error message"),
    statement, sqlstate, details: error, body: error });
}
