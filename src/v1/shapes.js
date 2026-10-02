// Shapes v1 (terrabase docs/shapes-v1.md): the gatekeeper and a subscription
// that follows a shape's log by long-poll (origin or edge) or SSE (origin).
import { array, boolean, decodeDocument, malformed, object, string, u64 } from "./decode.js";
import { TerraBaseV1Error } from "./errors.js";
import { requirePath } from "./filter.js";
import { parseU64, requireName } from "./ids.js";
import { parseExactJson, stringifyExactJson } from "./json.js";
import { v1Route, v1RoutePath } from "./routes.js";
import { backoffMs, requireServiceUrl, retryDelayMs, sleep } from "./transport.js";
/**
 * @import { V1Document } from "./decode.js"
 * @import { V1Filter } from "./filter.js"
 * @import { JsonValue } from "./json.js"
 * @import { V1Transport } from "./transport.js"
 */

/**
 * A shape: a filtered, projected view of one collection. The filter uses the
 * query grammar without sort, limit or cursor.
 * @typedef {object} V1ShapeRequest
 * @property {string} collection
 * @property {V1Filter | undefined} [filter]
 * @property {readonly string[] | undefined} [project]
 */

/**
 * The gatekeeper's answer. `token` is the edge shape token; it expires at
 * `expiresAt` (Unix milliseconds, decimal string).
 * @typedef {object} V1ShapeInfo
 * @property {string} shapeId
 * @property {string} handle
 * @property {string} offset
 * @property {string} url
 * @property {string} token
 * @property {string} expiresAt
 */

/**
 * - `mode`: `"long-poll"` (default) or `"sse"` (origin only).
 * - `edge`: read through the gatekeeper's edge URL with the shape token,
 *   refreshing it before it expires. Long-poll only.
 * - `resume`: continue from a saved `resumePoint`.
 * - `signal`: aborting it closes the subscription; iteration then throws its reason.
 * @typedef {object} V1SubscribeOptions
 * @property {"long-poll" | "sse" | undefined} [mode]
 * @property {boolean | undefined} [edge]
 * @property {{ readonly handle: string; readonly offset: string } | undefined} [resume]
 * @property {AbortSignal | undefined} [signal]
 */

/**
 * An upsert carries the (projected) document like a point read: `state`
 * `"value"` with `document`, or `"conflict"` with `siblings`.
 * @typedef {({ readonly op: "upsert" } & V1Document) | { readonly op: "delete"; readonly id: string }} V1ShapeOp
 */
/** @typedef {{ readonly offset: string; readonly ops: readonly V1ShapeOp[] }} V1ShapeChange */

/**
 * One step of a subscription.
 * - `changes`: apply `changes` in order (snapshot pages carry only upserts).
 * - `up-to-date`: the subscription caught up with the head.
 * - `reset`: the server required a refetch (`must_refetch`); discard all shape
 *   state, a fresh snapshot follows.
 * @typedef {object} V1ShapeEvent
 * @property {"changes" | "up-to-date" | "reset"} type
 * @property {string} handle
 * @property {string} offset Where the response started (`snap.S.P` or a log offset).
 * @property {string} nextOffset Where the next request resumes.
 * @property {boolean} snapshot
 * @property {boolean} upToDate
 * @property {readonly V1ShapeChange[]} changes
 */

const shapeIdPattern = /^[0-9a-f]{32}$/u;

/**
 * @param {V1ShapeRequest} request
 * @returns {string}
 */
function encodeShapeRequest(request) {
  if (typeof request !== "object" || request === null) throw new TypeError("A shape request is an object.");
  for (const key of Object.keys(request))
    if (key !== "collection" && key !== "filter" && key !== "project") throw new TypeError(`Unknown shape member ${key}.`);
  /** @type {Record<string, unknown>} */
  const body = { collection: requireName("collection", request.collection) };
  if (request.filter !== undefined) body.filter = request.filter;
  if (request.project !== undefined) {
    if (!Array.isArray(request.project) || request.project.length > 64) throw new TypeError("project lists at most 64 paths.");
    body.project = request.project.map((path) => {
      if (typeof path !== "string" || requirePath(path) === "$id") throw new TypeError("Projection paths are JSON Pointers.");
      return path;
    });
  }
  return stringifyExactJson(body, "shape request");
}

/**
 * @param {V1Transport} transport
 * @param {string} database
 * @param {V1ShapeRequest} request
 * @param {AbortSignal | undefined} signal
 * @returns {Promise<V1ShapeInfo>}
 */
export async function createShape(transport, database, request, signal) {
  const response = await transport.call("createShape", { db: requireName("database", database) }, {
    body: encodeShapeRequest(request), signal,
  });
  const raw = object(response.value, "shape");
  const shapeId = string(raw.shape_id, "shape_id");
  if (!shapeIdPattern.test(shapeId)) malformed("shape_id");
  return Object.freeze({
    shapeId,
    handle: string(raw.handle, "shape handle"),
    offset: string(raw.offset, "shape offset"),
    url: string(raw.url, "shape url"),
    token: string(raw.token, "shape token"),
    expiresAt: u64(raw.expires_at, "shape expires_at"),
  });
}

/**
 * @param {JsonValue | undefined} value
 * @returns {V1ShapeOp}
 */
function decodeShapeOp(value) {
  const raw = object(value, "shape op");
  const id = string(raw.id, "shape op id");
  if (raw.op === "delete") return Object.freeze({ op: "delete", id });
  if (raw.op !== "upsert") return malformed("shape op kind");
  return Object.freeze({ op: "upsert", ...decodeDocument({ ...raw, state: raw.state ?? "value" }) });
}

/**
 * @param {JsonValue | undefined} value
 * @returns {readonly V1ShapeChange[]}
 */
function decodeChanges(value) {
  return Object.freeze(array(value, "shape changes").map((item) => {
    const change = object(item, "shape change");
    return Object.freeze({
      offset: u64(change.offset, "shape change offset"),
      ops: Object.freeze(array(change.ops, "shape change ops").map(decodeShapeOp)),
    });
  }));
}

/**
 * Shape reads retry transport failures, 429 and the transient 503s, as the
 * contract allows for reads. Everything else, including the edge's
 * `502 upstream_unavailable` and a 504, is returned to the consumer.
 * @param {unknown} error
 * @param {number} attempt
 * @returns {number | undefined}
 */
function shapeRetryDelayMs(error, attempt) {
  if (!(error instanceof TerraBaseV1Error)) return backoffMs(attempt);
  return retryDelayMs(error, attempt, { replaySafe: true, keyed: false });
}

/** Reads `text/event-stream` messages from a fetch body. */
class EventStreamReader {
  /** @type {ReadableStreamDefaultReader<Uint8Array>} */
  #reader;
  /** @type {AbortController} */
  #controller;
  /** @type {number} */
  #maximum;
  #decoder = new TextDecoder("utf-8");
  #buffer = "";

  /**
   * @param {ReadableStream<Uint8Array>} body
   * @param {AbortController} controller
   * @param {number} maximum
   */
  constructor(body, controller, maximum) {
    this.#reader = body.getReader();
    this.#controller = controller;
    this.#maximum = maximum;
  }

  /** @returns {Promise<string | undefined>} */
  async #line() {
    while (true) {
      const match = /\r\n|\r|\n/u.exec(this.#buffer);
      if (match !== null && !(match[0] === "\r" && match.index === this.#buffer.length - 1)) {
        const line = this.#buffer.slice(0, match.index);
        this.#buffer = this.#buffer.slice(match.index + match[0].length);
        return line;
      }
      if (this.#buffer.length > this.#maximum) throw new TypeError("Shape event exceeds its size limit.");
      const part = await this.#reader.read();
      if (part.done) return undefined;
      this.#buffer += this.#decoder.decode(part.value, { stream: true });
    }
  }

  /** @returns {Promise<{ event: string; data: string } | undefined>} */
  async next() {
    let event = "";
    /** @type {string[]} */
    let data = [];
    let size = 0;
    while (true) {
      const line = await this.#line();
      if (line === undefined) return undefined;
      if (line === "") {
        if (data.length > 0) return { event: event === "" ? "message" : event, data: data.join("\n") };
        event = "";
        continue;
      }
      if (line.startsWith(":")) continue;
      const colon = line.indexOf(":");
      const field = colon < 0 ? line : line.slice(0, colon);
      const value = colon < 0 ? "" : line.slice(colon + 1).replace(/^ /u, "");
      if (field === "event") event = value;
      else if (field === "data") {
        size += value.length;
        if (size > this.#maximum) throw new TypeError("Shape event exceeds its size limit.");
        data.push(value);
      }
    }
  }

  /** @returns {void} */
  cancel() {
    this.#controller.abort();
    void this.#reader.cancel().catch(() => undefined);
  }
}

/**
 * Follows a shape: offset -1 redirects to the snapshot, snapshot pages, log
 * chunks, then live long-polls echoing `TB-Shape-Cursor` (or one SSE stream).
 * A `must_refetch` yields a `reset` event and restarts from the snapshot.
 * Iterate it once with `for await`; `waitFor`, `position`, `info` and `close`
 * may be used concurrently.
 */
export class V1ShapeSubscription {
  /** @type {V1Transport} */
  #transport;
  /** @type {string} */
  #database;
  /** @type {V1ShapeRequest} */
  #request;
  /** @type {"long-poll" | "sse"} */
  #mode;
  /** @type {boolean} */
  #edge;
  /** @type {V1ShapeInfo} */
  #info;
  /** @type {number} */
  #obtainedAt;
  /** @type {string} */
  #handle;
  /** @type {string} */
  #offset;
  #cursor = "";
  #live = false;
  #upToDate = false;
  #rejected = false;
  /** @type {EventStreamReader | undefined} */
  #stream;
  #progress = 0n;
  /** @type {Set<{ target: bigint; resolve: () => void; reject: (reason: unknown) => void }>} */
  #waiters = new Set();
  #life = new AbortController();
  #iterating = false;
  /** @type {AbortSignal | undefined} */
  #signal;

  /**
   * @param {V1Transport} transport
   * @param {string} database
   * @param {V1ShapeRequest} request
   * @param {V1ShapeInfo} info
   * @param {V1SubscribeOptions} options
   */
  constructor(transport, database, request, info, options) {
    const mode = options.mode ?? "long-poll";
    if (mode !== "long-poll" && mode !== "sse") throw new TypeError('mode is "long-poll" or "sse".');
    if (mode === "sse" && options.edge === true)
      throw new TypeError("SSE is served by the origin only; do not combine it with edge.");
    this.#transport = transport;
    this.#database = database;
    this.#request = request;
    this.#mode = mode;
    this.#edge = options.edge === true;
    this.#info = info;
    this.#obtainedAt = Date.now();
    this.#handle = info.handle;
    this.#offset = info.offset;
    if (options.resume !== undefined) {
      this.#handle = string(options.resume.handle, "resume handle");
      this.#offset = string(options.resume.offset, "resume offset");
      this.#live = parseU64(this.#offset) !== undefined;
    }
    this.#signal = options.signal;
    if (options.signal !== undefined) {
      if (options.signal.aborted) this.close();
      else options.signal.addEventListener("abort", () => this.close(), { once: true });
    }
  }

  /** @returns {V1ShapeInfo} The current gatekeeper answer (refreshed for the edge). */
  get info() {
    return this.#info;
  }

  /** @returns {string} The highest log offset whose changes the consumer has finished handling. */
  get position() {
    return this.#progress.toString();
  }

  /** @returns {{ readonly handle: string; readonly offset: string }} Where a later subscription can resume. */
  get resumePoint() {
    return Object.freeze({ handle: this.#handle, offset: this.#offset });
  }

  /** @returns {boolean} */
  get closed() {
    return this.#life.signal.aborted;
  }

  /** Stops the subscription and cancels any request in flight. */
  close() {
    if (this.#life.signal.aborted) return;
    this.#life.abort();
    this.#closeStream();
    for (const waiter of this.#waiters) waiter.reject(new Error("TerraBase shape subscription closed."));
    this.#waiters.clear();
  }

  /**
   * Resolves once the consumer has handled the shape up to `offset` (for
   * example a write's `offset`): an event whose `nextOffset` is at least
   * `offset` was yielded and the consumer asked for the next one, or the log
   * advanced past `offset` without changes for this shape. Someone must be
   * iterating the subscription.
   * @param {string | bigint} offset
   * @param {{ readonly signal?: AbortSignal | undefined }} [options]
   * @returns {Promise<void>}
   */
  waitFor(offset, options = {}) {
    const target = typeof offset === "bigint" ? offset : parseU64(offset);
    if (target === undefined || target < 0n) return Promise.reject(new TypeError("waitFor takes a log offset."));
    if (this.#progress >= target) return Promise.resolve();
    if (this.closed) return Promise.reject(new Error("TerraBase shape subscription closed."));
    return new Promise((resolve, reject) => {
      const signal = options.signal;
      /** @returns {void} */
      const abort = () => {
        this.#waiters.delete(waiter);
        reject(signal?.reason);
      };
      const waiter = {
        target,
        resolve: () => { signal?.removeEventListener("abort", abort); resolve(); },
        reject: (/** @type {unknown} */ reason) => { signal?.removeEventListener("abort", abort); reject(reason); },
      };
      if (signal?.aborted) { reject(signal.reason); return; }
      signal?.addEventListener("abort", abort, { once: true });
      this.#waiters.add(waiter);
    });
  }

  /**
   * @param {string} offset
   * @returns {void}
   */
  #advance(offset) {
    const value = parseU64(offset);
    if (value === undefined || value <= this.#progress) return;
    this.#progress = value;
    for (const waiter of this.#waiters) {
      if (waiter.target <= value) {
        this.#waiters.delete(waiter);
        waiter.resolve();
      }
    }
  }

  /** @returns {AsyncGenerator<V1ShapeEvent, void, undefined>} */
  [Symbol.asyncIterator]() {
    return this.events();
  }

  /** @returns {AsyncGenerator<V1ShapeEvent, void, undefined>} */
  async *events() {
    if (this.#iterating) throw new TypeError("A shape subscription has one consumer.");
    this.#iterating = true;
    try {
      let failures = 0;
      while (!this.closed) {
        /** @type {V1ShapeEvent | undefined} */
        let event;
        try {
          event = this.#mode === "sse" ? await this.#stepSse() : await this.#stepPoll();
        } catch (error) {
          if (this.closed) break;
          const delay = shapeRetryDelayMs(error, failures);
          if (delay === undefined || failures >= this.#transport.maxRetries) throw error;
          failures++;
          try {
            await sleep(delay, this.#life.signal);
          } catch {
            break;
          }
          continue;
        }
        failures = 0;
        if (event === undefined) {
          this.#advance(this.#offset);
          continue;
        }
        yield event;
        if (event.type !== "reset") this.#advance(event.nextOffset);
      }
      if (this.#signal?.aborted) throw this.#signal.reason;
    } finally {
      this.#iterating = false;
      this.#closeStream();
    }
  }

  /**
   * @param {string | undefined} handle
   * @returns {V1ShapeEvent}
   */
  #reset(handle) {
    this.#handle = handle ?? "";
    this.#offset = "-1";
    this.#cursor = "";
    this.#live = false;
    this.#upToDate = false;
    this.#closeStream();
    return Object.freeze({
      type: "reset", handle: this.#handle, offset: "-1", nextOffset: "-1",
      snapshot: false, upToDate: false, changes: Object.freeze([]),
    });
  }

  /**
   * @param {JsonValue | undefined} value
   * @param {Headers | undefined} headers
   * @returns {V1ShapeEvent | undefined}
   */
  #apply(value, headers) {
    const raw = object(value, "shape body");
    if (string(raw.shape_id, "shape body shape_id") !== this.#info.shapeId) malformed("shape body shape_id");
    const handle = typeof raw.handle === "string" ? raw.handle : headers?.get("tb-shape-handle") ?? malformed("shape handle");
    const offset = string(raw.offset, "shape body offset");
    const nextOffset = string(raw.next_offset, "shape body next_offset");
    const upToDate = boolean(raw.up_to_date, "shape body up_to_date");
    // An empty snapshot is a single page whose change has ops:[]; a change
    // without ops carries nothing to apply, so events list only changes with ops.
    const changes = Object.freeze(decodeChanges(raw.changes).filter((change) => change.ops.length > 0));
    this.#handle = handle;
    this.#offset = nextOffset;
    this.#live = upToDate && parseU64(nextOffset) !== undefined;
    const wasUpToDate = this.#upToDate;
    this.#upToDate = upToDate;
    if (changes.length === 0 && !(upToDate && !wasUpToDate)) return undefined;
    return Object.freeze({
      type: changes.length > 0 ? "changes" : "up-to-date",
      handle, offset, nextOffset, snapshot: offset.startsWith("snap."), upToDate, changes,
    });
  }

  /**
   * Calls the gatekeeper again when the edge token is close to expiry (or
   * when forced). A changed shape ID is a new lineage, so it resets.
   * @param {boolean} force
   * @returns {Promise<V1ShapeEvent | undefined>}
   */
  async #refresh(force) {
    const previous = this.#info;
    const expiresAt = Number(previous.expiresAt);
    const margin = Math.min(60_000, (expiresAt - this.#obtainedAt) / 2);
    if (!force && Date.now() + margin < expiresAt) return undefined;
    const fresh = await createShape(this.#transport, this.#database, this.#request, this.#life.signal);
    this.#info = fresh;
    this.#obtainedAt = Date.now();
    return fresh.shapeId === previous.shapeId ? undefined : this.#reset(fresh.handle);
  }

  /**
   * @param {"readShape" | "streamShape"} operation
   * @param {boolean} live
   * @returns {{ url: string; token: string | undefined }}
   */
  #url(operation, live) {
    const query = new URLSearchParams();
    if (this.#handle !== "" && this.#offset !== "-1") query.set("handle", this.#handle);
    query.set("offset", this.#offset);
    if (live) {
      query.set("live", "true");
      if (this.#cursor !== "") query.set("cursor", this.#cursor);
    }
    const params = { shape_id: this.#info.shapeId };
    if (!this.#edge) return { url: `${this.#transport.url(operation, params)}?${query}`, token: undefined };
    const edge = requireServiceUrl(this.#info.url, "The shape edge URL");
    return { url: `${edge.origin}${v1RoutePath(v1Route(operation), params)}?${query}`, token: this.#info.token };
  }

  /** @returns {Promise<V1ShapeEvent | undefined>} */
  async #stepPoll() {
    if (this.#edge) {
      const event = await this.#refresh(false);
      if (event !== undefined) return event;
    }
    const { url, token } = this.#url("readShape", this.#live);
    const init = { method: "GET", accept: "application/json", token, signal: this.#life.signal };
    let response = await this.#transport.exchange(url, { ...init, redirect: "manual" });
    // Browsers hide manual redirects; follow it there (fetch drops
    // Authorization across origins) and use the snapshot page's own position.
    if (response.type === "opaqueredirect") response = await this.#transport.exchange(url, { ...init, redirect: "follow" });
    if (response.status === 200) {
      const event = this.#apply(await this.#transport.readJson(response), response.headers);
      const cursor = response.headers.get("tb-shape-cursor");
      if (this.#live && cursor !== null && cursor !== "") this.#cursor = cursor;
      this.#rejected = false;
      return event;
    }
    if (response.status === 307) {
      void response.body?.cancel().catch(() => undefined);
      const location = response.headers.get("location");
      const target = location === null ? undefined : new URL(location, url);
      const handle = target?.searchParams.get("handle");
      const offset = target?.searchParams.get("offset");
      if (handle === null || handle === undefined || offset === null || offset === undefined) malformed("shape redirect");
      this.#handle = handle;
      this.#offset = offset;
      this.#live = false;
      return undefined;
    }
    const error = await this.#transport.error(response);
    if (error.code === "must_refetch") return this.#reset(error.handle);
    if (this.#edge && error.status === 401 && !this.#rejected) {
      this.#rejected = true;
      return this.#refresh(true);
    }
    throw error;
  }

  /** @returns {void} */
  #closeStream() {
    this.#stream?.cancel();
    this.#stream = undefined;
  }

  /** @returns {Promise<V1ShapeEvent | undefined>} */
  async #stepSse() {
    if (this.#stream === undefined) {
      const { url } = this.#url("streamShape", false);
      const controller = new AbortController();
      const life = this.#life.signal;
      if (life.aborted) controller.abort();
      else life.addEventListener("abort", () => controller.abort(), { once: true });
      /** @type {Record<string, string>} */
      const headers = {};
      if (this.#offset !== "-1") headers["last-event-id"] = this.#offset;
      const response = await this.#transport.exchange(url, {
        method: "GET", accept: "text/event-stream", headers, signal: controller.signal,
      });
      if (response.status !== 200) {
        const error = await this.#transport.error(response);
        if (error.code === "must_refetch") return this.#reset(error.handle);
        throw error;
      }
      const mediaType = response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase();
      if (mediaType !== "text/event-stream" || response.body === null) {
        controller.abort();
        malformed("shape event stream");
      }
      this.#stream = new EventStreamReader(response.body, controller, 16 * 1024 * 1024);
    }
    /** @type {{ event: string; data: string } | undefined} */
    let message;
    try {
      message = await this.#stream.next();
    } catch (error) {
      // The next step reconnects with Last-Event-ID after a backoff.
      this.#closeStream();
      throw error;
    }
    if (message === undefined) {
      this.#closeStream();
      throw new TypeError("TerraBase shape event stream ended.");
    }
    switch (message.event) {
      case "changes":
        return this.#apply(parseExactJson(message.data), undefined);
      case "up-to-date": {
        const nextOffset = u64(object(parseExactJson(message.data), "up-to-date event").next_offset, "up-to-date next_offset");
        this.#offset = nextOffset;
        const wasUpToDate = this.#upToDate;
        this.#upToDate = true;
        if (wasUpToDate) return undefined;
        return Object.freeze({
          type: "up-to-date", handle: this.#handle, offset: nextOffset, nextOffset,
          snapshot: false, upToDate: true, changes: Object.freeze([]),
        });
      }
      case "must-refetch": {
        const handle = object(parseExactJson(message.data), "must-refetch event").handle;
        return this.#reset(typeof handle === "string" ? handle : undefined);
      }
      default:
        return undefined;
    }
  }
}
