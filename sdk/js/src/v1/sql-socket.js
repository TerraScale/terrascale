import { array, boolean, malformed, object, string } from "./decode.js";
import { listenForSqlRefusal } from "./sql-connect-retry.js";
import { SqlError, SqlErrorCode } from "./errors.js";
import { parseExactJson, stringifyExactJson } from "./json.js";
import { decodeSqlFrameResult, encodeStatement, sqlRowObjects, sqlStatement } from "./sql-execute.js";
import { requestBookmark, requireConsistency } from "./bookmark.js";
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
const maximumOutboundFrameBytes = 1024 * 1024;
const maximumInboundFrameBytes = 64 * 1024 * 1024;
const frameDeadlineMs = 60_000;
const heartbeatIntervalMs = 20_000;
const statusByCode = new Map([
  [SqlErrorCode.invalidRequest, 400], [SqlErrorCode.invalidBookmark, 400], [SqlErrorCode.invalidConsistency, 400],
  ["syntax_error", 400], ["undefined_table", 400], ["undefined_column", 400], ["duplicate_object", 400],
  ["sql_error", 400], ["transaction_state", 400], ["transaction_rolled_back", 400], ["query_canceled", 400],
  ["check_violation", 400], ["not_null_violation", 400], ["foreign_key_violation", 400], ["unique_violation", 400],
  ["primary_key_violation", 400], ["constraint_violation", 400],
  ["unauthorized", 401],
  ["forbidden_scope", 403], ["sql_sandbox", 403], ["rule_denied", 403], ["column_not_readable", 403], ["rules_invalid", 403],
  ["serialization_conflict", 409], ["row_conflict", 409],
  ["too_large", 413], ["quota_exceeded", 429], ["tenant_busy", 429],
  [SqlErrorCode.writeOutcomeUnknown, 503], [SqlErrorCode.commitUncertain, 503], [SqlErrorCode.inRegionQuorumUnconfirmed, 503],
  ["unavailable", 503], ["storage_busy", 503], ["storage_corrupted", 503], ["io_error", 503],
  ["strong_quorum_unreachable", 503], ["system_catalog_timeout", 503],
  ["request_timeout", 504], ["internal_error", 500],
]);

/**
 * @typedef {object} SqlSocketLike
 * @property {string} protocol
 * @property {number} readyState
 * @property {(data: string) => void} send
 * @property {(code?: number, reason?: string) => void} close
 * @property {(type: string, listener: (event: any) => void, options?: { once?: boolean }) => void} addEventListener
 * @property {(data: string) => void} [ping]
 * @property {(type: "pong", listener: (data: Uint8Array) => void) => void} [on]
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
  /** @type {ReturnType<typeof setTimeout> | undefined} */
  #responseTimer;
  /** @type {ReturnType<typeof setInterval> | undefined} */
  #heartbeat;
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
    socket.addEventListener("error", () => this.#terminate(new SqlSocketClosedError(1006, "connection failed")));
    const ping = typeof socket.ping === "function" ? socket.ping.bind(socket) : undefined;
    if (ping !== undefined) {
      let awaitingPong = false;
      const decoder = new TextDecoder();
      /** @param {unknown} payload */
      const onPong = payload => {
        const text = typeof payload === "string"
          ? payload
          : payload instanceof Uint8Array || payload instanceof ArrayBuffer
            ? decoder.decode(payload)
            : "";
        if (text === sqlSocketProtocol || text === "") awaitingPong = false;
      };
      if (typeof socket.on === "function") socket.on("pong", onPong);
      else socket.addEventListener("pong", event => onPong(event?.data ?? event));
      this.#heartbeat = setInterval(() => {
        if (awaitingPong) {
          this.#terminate(new SqlSocketClosedError(1006, "heartbeat deadline expired"));
          return;
        }
        awaitingPong = true;
        try { ping(sqlSocketProtocol); }
        catch { this.#terminate(new SqlSocketClosedError(1006, "heartbeat failed")); }
      }, heartbeatIntervalMs);
    }
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
    const abort = () => connected.close();
    signal?.addEventListener("abort", abort, { once: true });
    try {
      signal?.throwIfAborted();
      await connected.#request({ type: "auth", token });
      signal?.throwIfAborted();
    } catch (error) {
      connected.close();
      signal?.throwIfAborted();
      throw error;
    } finally {
      signal?.removeEventListener("abort", abort);
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
          socket.close();
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
    this.#terminate(new SqlSocketClosedError(1000, ""));
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
    const bookmark = requestBookmark(options.bookmark, this.#session.bookmarks);
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
    if (new TextEncoder().encode(frame).byteLength > maximumOutboundFrameBytes)
      return Promise.reject(new TypeError(`A SQL WebSocket request holds at most ${maximumOutboundFrameBytes} bytes.`));
    return new Promise((resolve, reject) => {
      const committing = type === "commit" || type === "execute" && (!this.#transaction || [...this.#pending.values()].some(pending => pending.control));
      const wasEmpty = this.#pending.size === 0;
      this.#pending.set(id, { resolve, reject, committing, control: ["begin", "commit", "rollback"].includes(type) });
      if (wasEmpty) this.#armResponseTimer();
      try { this.#socket.send(frame); }
      catch (error) {
        this.#terminate(new SqlSocketClosedError(1006, error instanceof Error ? error.message : "send failed"));
      }
    });
  }

  #armResponseTimer() {
    clearTimeout(this.#responseTimer);
    this.#responseTimer = setTimeout(() => {
      this.#terminate(new SqlSocketClosedError(1006, "SQL frame response deadline expired"));
    }, frameDeadlineMs);
  }

  /** @param {unknown} data */
  #receive(data) {
    if (this.#closed !== undefined) return;
    if (typeof data === "string" && exceedsInboundLimit(data)) {
      this.#terminate(new SqlSocketClosedError(1009, `SQL responses are at most ${maximumInboundFrameBytes} bytes`));
      return;
    }
    try {
      if (typeof data !== "string") throw new TypeError("SQL WebSocket frames are JSON text.");
      const frame = object(/** @type {JsonValue} */ (parseExactJson(data)), "SQL frame");
      const id = frame.id;
      const error = frame.type === "error" ? socketError(object(frame.error, "SQL error")) : undefined;
      if (error?.outcomeUncertain) {
        this.#terminate(error);
        if (typeof frame.bookmark === "string") this.#session.bookmarks.observe(frame.bookmark);
        return;
      }
      const waiter = typeof id === "number" ? this.#pending.get(id) : undefined;
      if (waiter === undefined) {
        if (error !== undefined) this.#terminate(error);
        return;
      }
      this.#transaction = boolean(frame.transaction, "SQL transaction flag");
      if (frame.bookmark != null) this.#session.bookmarks.observe(string(frame.bookmark, "SQL bookmark"));
      if (frame.type === "result") waiter.resolve(frame);
      else if (error !== undefined) waiter.reject(error);
      else malformed("SQL frame type");
      this.#pending.delete(/** @type {number} */ (id));
      if (this.#pending.size === 0) {
        clearTimeout(this.#responseTimer);
        this.#responseTimer = undefined;
      } else {
        this.#armResponseTimer();
      }
    } catch (error) {
      this.#terminate(new SqlSocketClosedError(1002, error instanceof Error ? error.message : "unreadable answer"));
    }
  }

  /** @param {SqlSocketClosedError | SqlError} error */
  #terminate(error) {
    this.#fail(error);
    if (this.#socket.readyState < 2) {
      const normal = error instanceof SqlSocketClosedError && error.closeCode === 1000;
      this.#socket.close(normal ? 1000 : undefined);
    }
  }

  /** @param {SqlSocketClosedError | SqlError} error */
  #fail(error) {
    if (this.#closed !== undefined) return;
    const committing = [...this.#pending.values()].some(pending => pending.committing);
    this.#closed = error instanceof SqlError && error.outcomeUncertain
      ? error
      : committing
        ? new SqlError({ status: 503, code: SqlErrorCode.writeOutcomeUnknown, sqlstate: "08007", statement: 0, message: "A committing request lost its answer; reconcile before repeating." })
        : this.#transaction
          ? new SqlError({ status: 400, code: "transaction_rolled_back", sqlstate: "40000", statement: 0, message: "The connection lost an open transaction before a committing request was sent." })
          : error;
    this.#transaction = false;
    clearInterval(this.#heartbeat);
    clearTimeout(this.#responseTimer);
    this.#responseTimer = undefined;
    for (const waiter of this.#pending.values()) {
      waiter.reject(this.#closed);
    }
    this.#pending.clear();
  }
}

/** @param {string} data */
function exceedsInboundLimit(data) {
  if (data.length > maximumInboundFrameBytes) return true;
  if (data.length * 3 <= maximumInboundFrameBytes) return false;
  let remaining = maximumInboundFrameBytes - data.length;
  for (const character of data) {
    const code = /** @type {number} */ (character.codePointAt(0));
    remaining -= code <= 0x7f ? 0 : code <= 0x7ff ? 1 : 2;
    if (remaining < 0) return true;
  }
  return false;
}

/** @param {JsonObject} error @returns {SqlError} */
function socketError(error) {
  const code = string(error.code, "SQL error code");
  const statement = error.statement ?? 0;
  if (typeof statement !== "number" || !Number.isSafeInteger(statement) || statement < 0) malformed("SQL error statement");
  const sqlstate = string(error.sqlState, "SQL error sqlState");
  if (!/^[0-9A-Z]{5}$/u.test(sqlstate)) malformed("SQL error sqlstate");
  return new SqlError({ status: statusByCode.get(code) ?? null, code, message: string(error.message, "SQL error message"),
    statement, sqlstate, details: error, body: error });
}
