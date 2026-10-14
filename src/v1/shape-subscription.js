// Client state for one collection shape: bootstrap, snapshot pages, log chunks, then the live tail.
import { TerraBaseV1Error } from "./errors.js";
import { requireFilter } from "./filter.js";
import { parseExactJson, stringifyExactJson } from "./json.js";
import { readShapePage } from "./shape-reader.js";
/**
 * @import { JsonObject } from "./json.js"
 * @import { V1Filter } from "./filter.js"
 * @import { V1Transport } from "./transport.js"
 */
/**
 * @typedef {object} V1ShapeSubscribeOptions
 * @property {AbortSignal | undefined} [signal] Closes the subscription when aborted; cancellation settles `done`.
 * @property {((subscription: V1ShapeSubscription) => void) | undefined} [onUpdate] Runs after each validated response, resets included.
 */
/** @typedef {{ readonly version: string; readonly document: JsonObject }} V1ShapeDocument */
const retryBaseMilliseconds = 250;
const retryCapMilliseconds = 30_000;
// Bounds consecutive transient failures; a successful response resets the count.
const maximumConsecutiveRetries = 8;
/** @param {import("./errors.js").TerraBaseV1Error} error @returns {boolean} */
function isRetryable(error) {
  return (error.status === 429 && error.code === "shape_busy") ||
    (error.status === 503 && (error.code === "unavailable" || error.code === "shape_too_large"));
}
/** @param {number} attempt @param {number | undefined} retryAfter @returns {number} */
function retryDelay(attempt, retryAfter) {
  return Math.max(Math.min(retryCapMilliseconds, retryBaseMilliseconds * 2 ** (attempt - 1)), (retryAfter ?? 0) * 1000);
}
/** @param {unknown} value @param {readonly string[]} allowed @param {string} what */
function requireMembers(value, allowed, what) {
  if (typeof value !== "object" || value === null) throw new TypeError(`${what} is an object.`);
  for (const key of Object.keys(value)) if (!allowed.includes(key)) throw new TypeError(`Unknown ${what} member ${key}.`);
}
/** @template T @param {T} value @returns {T} */
function deepFreeze(value) {
  if (typeof value === "object" && value !== null && !Object.isFrozen(value)) {
    Object.freeze(value);
    for (const item of Object.values(value)) deepFreeze(item);
  }
  return value;
}
// setTimeout overflows above 2^31-1 ms and fires at once, so a long Retry-After is waited in chunks.
const maximumTimerMilliseconds = 2 ** 31 - 1;
/** @param {number} milliseconds @param {AbortSignal} signal @returns {Promise<void>} */
function sleep(milliseconds, signal) {
  return new Promise((resolve, reject) => {
    signal.throwIfAborted();
    let remaining = milliseconds;
    /** @type {ReturnType<typeof setTimeout> | undefined} */
    let timer;
    const onAbort = () => { clearTimeout(timer); reject(signal.reason); };
    const wait = () => {
      const chunk = Math.min(remaining, maximumTimerMilliseconds);
      timer = setTimeout(() => {
        remaining -= chunk;
        if (remaining > 0) wait();
        else { signal.removeEventListener("abort", onAbort); resolve(); }
      }, chunk);
    };
    signal.addEventListener("abort", onAbort, { once: true });
    wait();
  });
}
export class V1ShapeSubscription {
  /** @type {Map<string, V1ShapeDocument>} */
  #documents = new Map();
  /** @type {string | undefined} */
  #handle;
  #offset = "-1";
  /** @type {string | undefined} */
  #cursor;
  /** @type {string | undefined} Usable only from a snapshot-complete or log position. */
  #resume;
  #upToDate = false;
  // Set after a changed-handle 409 while the saved resume is still usable; cleared by any commit or reset.
  #resumeAttempt = false;
  #controller = new AbortController();
  #transport;
  #params;
  /** @type {V1Filter | undefined} */
  #where;
  #onUpdate;
  #external;
  #onExternalAbort = () => this.close();
  #done;
  /**
   * @param {V1Transport} transport
   * @param {Readonly<Record<string, string>>} params
   * @param {{ where?: V1Filter | undefined }} [shape]
   * @param {V1ShapeSubscribeOptions} [options]
   */
  constructor(transport, params, shape = {}, options = {}) {
    requireMembers(shape, ["where"], "shape");
    requireMembers(options, ["signal", "onUpdate"], "shape subscribe option");
    const { where } = shape;
    const { signal, onUpdate } = options;
    if (signal !== undefined && !(signal instanceof AbortSignal)) throw new TypeError("signal is an AbortSignal.");
    if (onUpdate !== undefined && typeof onUpdate !== "function") throw new TypeError("onUpdate is a function.");
    this.#transport = transport;
    this.#params = Object.freeze({ ...params });
    // Exact copy: a structuredClone would lose the source text of large or fractional JSON numbers.
    this.#where = where === undefined ? undefined : requireFilter(parseExactJson(stringifyExactJson(requireFilter(where), "shape filter")));
    this.#onUpdate = onUpdate;
    this.#external = signal;
    if (signal?.aborted === true) this.close();
    else signal?.addEventListener("abort", this.#onExternalAbort);
    this.#done = this.#run();
  }
  /** A copy per read: callers cannot change the documents this subscription holds. */
  get state() { return new Map(this.#documents); }
  get upToDate() { return this.#upToDate; }
  /** Settles when the run ends: resolves on close, rejects on a terminal failure. */
  get done() { return this.#done; }
  close() { this.#controller.abort(); }
  /** @returns {Promise<void>} */
  async #run() {
    const signal = this.#controller.signal;
    let retries = 0;
    try {
      for (;;) {
        signal.throwIfAborted();
        const request = this.#request();
        let page;
        try {
          page = await readShapePage(this.#transport, this.#params, request, signal);
        } catch (error) {
          if (!(error instanceof TerraBaseV1Error)) throw error;
          if (isRetryable(error) && retries < maximumConsecutiveRetries) {
            retries += 1;
            await sleep(retryDelay(retries, error.retryAfter), signal);
            continue;
          }
          if (error.status !== 409 || error.code !== "must_refetch") throw error;
          this.#recover(request, error);
          continue;
        }
        retries = 0;
        this.#commit(page);
        this.#onUpdate?.(this);
      }
    } catch (error) {
      if (signal.aborted) return;
      throw error;
    } finally {
      // A finished run must not keep the caller's signal (and this subscription) alive.
      this.#external?.removeEventListener("abort", this.#onExternalAbort);
    }
  }
  /** @returns {import("./shapes.js").V1ShapeRequest} */
  #request() {
    if (this.#resumeAttempt) return { where: this.#where, offset: "-1", cursor: this.#resume };
    if (this.#handle === undefined) return { where: this.#where, offset: "-1" };
    // Only a fresh head is a live tail; snapshot positions are never live.
    if (this.#upToDate && !this.#offset.startsWith("snap.")) return { where: this.#where, offset: this.#offset, handle: this.#handle, live: true, cursor: this.#cursor };
    return { where: this.#where, offset: this.#offset, handle: this.#handle };
  }
  /**
   * @param {import("./shapes.js").V1ShapeRequest} request
   * @param {TerraBaseV1Error} error
   */
  #recover(request, error) {
    if (this.#resumeAttempt) return this.#reset();
    if (request.handle === undefined) throw error;
    // §7.6 reads the lineage from the response header. A body that names another handle is ambiguous: fail closed.
    const handle = error.headers.get("tb-shape-handle");
    const changed = handle !== null && handle !== this.#handle && (error.handle === undefined || error.handle === handle);
    if (changed && this.#resume !== undefined) {
      // One attempt on the new lineage; its own 409 falls through to #reset above.
      this.#resumeAttempt = true;
      return;
    }
    this.#reset();
  }
  #reset() {
    this.#documents = new Map();
    this.#handle = undefined;
    this.#offset = "-1";
    this.#cursor = undefined;
    this.#resume = undefined;
    this.#upToDate = false;
    this.#resumeAttempt = false;
    this.#onUpdate?.(this);
  }
  /** @param {import("./shapes.js").V1ShapePage} page */
  #commit(page) {
    // Repeats are applied, never skipped: a repeated event can carry a newer materialized value.
    const documents = new Map(this.#documents);
    for (const change of page.changes)
      for (const op of change.ops) {
        if (op.op === "delete") documents.delete(op.id);
        else documents.set(op.id, deepFreeze({ version: op.version, document: op.document }));
      }
    this.#documents = documents;
    this.#handle = page.handle;
    this.#offset = page.nextOffset;
    this.#cursor = page.cursor;
    this.#resume = page.nextOffset.startsWith("snap.") ? undefined : page.cursorResume;
    this.#upToDate = page.upToDate;
    this.#resumeAttempt = false;
  }
}
