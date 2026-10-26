// Client state for one collection shape: bootstrap, snapshot pages, log chunks, then the live tail.
import { TerraBaseV1Error } from "./errors.js";
import { requireFilter } from "./filter.js";
import { parseExactJson, stringifyExactJson } from "./json.js";
import { readShapePage } from "./shape-reader.js";
import { BodyTransportError, RequestTransportError } from "./transport.js";
/**
 * @import { JsonObject } from "./json.js"
 * @import { V1Filter } from "./filter.js"
 * @import { V1Transport } from "./transport.js"
 */
/**
 * @typedef {object} V1ShapeSubscribeOptions
 * @property {AbortSignal | undefined} [signal] Closes the subscription when aborted; cancellation settles `done`.
 * @property {((subscription: V1ShapeSubscription) => void) | undefined} [onUpdate] Runs after validated responses and freshness changes; throwing rejects `done` unless already closed or aborted.
 * @property {V1ShapeReplicaStore | undefined} [replica] Keeps the last resumable state for reads across restarts and while offline; writes still go to the server.
 */
/**
 * Holds one subscription's state as exact JSON text, e.g. in IndexedDB or React Native storage. Scope one store
 * to one signed-in subject: the replica holds that subject's authorized documents. A failing store rejects `done`.
 * @typedef {object} V1ShapeReplicaStore
 * @property {() => string | undefined | Promise<string | undefined>} load
 * @property {(text: string | undefined) => void | Promise<void>} save Receives undefined after must_refetch discards state.
 */
/** @typedef {{ readonly version: string; readonly document: JsonObject }} V1ShapeDocument */
const retryBaseMilliseconds = 250;
const retryCapMilliseconds = 30_000;
// The eighth attempt reaches the delay ceiling; retries continue until success or cancellation.
const maximumBackoffAttempt = 8;
/** @param {import("./errors.js").TerraBaseV1Error} error @returns {boolean} */
function isRetryable(error) {
  return (error.status === 429 && error.code === "shape_busy") ||
    (error.status === 503 && (error.code === "unavailable" || error.code === "auth_unavailable" || error.code === "shape_too_large"));
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
/** @template T @param {T | PromiseLike<T>} operation @param {AbortSignal} signal @returns {Promise<T>} */
function waitForStore(operation, signal) {
  signal.throwIfAborted();
  return new Promise((resolve, reject) => {
    const abort = () => { signal.removeEventListener("abort", abort); reject(signal.reason); };
    signal.addEventListener("abort", abort, { once: true });
    void Promise.resolve(operation).then(resolve, reject).finally(() => signal.removeEventListener("abort", abort));
  });
}
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
  #replica;
  /** @type {string} */
  #scope = "";
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
    requireMembers(options, ["signal", "onUpdate", "replica"], "shape subscribe option");
    const { where } = shape;
    const { signal, onUpdate, replica } = options;
    if (replica !== undefined && (typeof replica?.load !== "function" || typeof replica.save !== "function"))
      throw new TypeError("replica has load and save functions.");
    if (signal !== undefined && !(signal instanceof AbortSignal)) throw new TypeError("signal is an AbortSignal.");
    if (onUpdate !== undefined && typeof onUpdate !== "function") throw new TypeError("onUpdate is a function.");
    this.#transport = transport;
    this.#params = Object.freeze({ ...params });
    // Exact copy: a structuredClone would lose the source text of large or fractional JSON numbers.
    this.#where = where === undefined ? undefined : requireFilter(parseExactJson(stringifyExactJson(requireFilter(where), "shape filter")));
    this.#onUpdate = onUpdate;
    this.#replica = replica;
    this.#external = signal;
    if (signal?.aborted === true) this.close();
    else signal?.addEventListener("abort", this.#onExternalAbort);
    this.#done = this.#run();
    // Callback-only consumers must not trigger a process-level unhandled rejection.
    void this.#done.catch(() => {});
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
    /** @type {import("./shapes.js").V1ShapeRequest | undefined} */
    let retryRequest;
    try {
      // Bind persisted authorization views to the service and project as well as the shape.
      this.#scope = stringifyExactJson([await this.#transport.url("readFreeShape", this.#params, signal), this.#where ?? null], "shape scope");
      if (this.#replica !== undefined) await this.#restore(signal);
      for (;;) {
        signal.throwIfAborted();
        const request = retryRequest ?? this.#request();
        let page;
        try {
          page = await readShapePage(this.#transport, this.#params, request, signal);
        } catch (error) {
          // Shape reads are replay-safe GETs: a lost connection keeps state and resumes at the same position.
          const lost = error instanceof RequestTransportError || error instanceof BodyTransportError;
          if (lost || (error instanceof TerraBaseV1Error && isRetryable(error))) {
            retryRequest = request;
            if (this.#upToDate) { this.#upToDate = false; this.#onUpdate?.(this); }
            retries = Math.min(retries + 1, maximumBackoffAttempt);
            await sleep(retryDelay(retries, lost ? undefined : error.retryAfter), signal);
            continue;
          }
          retryRequest = undefined;
          if (!(error instanceof TerraBaseV1Error)) throw error;
          if (error.status === 401 || error.status === 403) {
            // Denial takes precedence over callback/storage failures. A stuck store
            // cannot postpone the terminal auth error after local state is cleared.
            try { this.#reset(); } catch { /* preserve the authorization failure */ }
            try { void Promise.resolve(this.#replica?.save(undefined)).catch(() => {}); }
            catch { /* a synchronous store failure must not mask denial either */ }
            throw error;
          }
          if (error.status !== 409 || error.code !== "must_refetch") throw error;
          this.#recover(request, error);
          if (this.#handle === undefined) await waitForStore(this.#replica?.save(undefined), signal);
          continue;
        }
        retryRequest = undefined;
        retries = 0;
        this.#commit(page);
        await this.#persist(signal);
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
      if (this.#upToDate) {
        this.#upToDate = false;
        this.#onUpdate?.(this);
      }
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
  /** @param {AbortSignal} signal */
  async #restore(signal) {
    signal.throwIfAborted();
    const text = await waitForStore(this.#replica?.load(), signal);
    signal.throwIfAborted();
    if (text === undefined) return;
    /** @type {any} */
    let saved;
    try { saved = parseExactJson(text); } catch { saved = undefined; }
    // Unknown or other-shape replicas are discarded, never merged.
    if (saved?.format !== 1 || saved.scope !== this.#scope || typeof saved.handle !== "string" || typeof saved.offset !== "string" ||
        !/^(?:0|[1-9][0-9]*)$/u.test(saved.offset) || !Array.isArray(saved.documents) ||
        !saved.documents.every((/** @type {any} */ item) => Array.isArray(item) && typeof item[0] === "string" &&
          typeof item[1]?.version === "string" && typeof item[1].document === "object" && item[1].document !== null)) return;
    this.#documents = new Map(saved.documents.map((/** @type {[string, V1ShapeDocument]} */ [id, entry]) => [id, deepFreeze({ version: entry.version, document: entry.document })]));
    this.#handle = saved.handle;
    this.#offset = saved.offset;
    this.#cursor = typeof saved.cursor === "string" ? saved.cursor : undefined;
    this.#resume = typeof saved.resume === "string" ? saved.resume : undefined;
    // Restored state is stale until the server confirms a fresh head.
    this.#upToDate = false;
    this.#onUpdate?.(this);
  }
  /** @param {AbortSignal} signal */
  async #persist(signal) {
    // Snapshot pages are not resumable positions; keep the previous replica until the snapshot completes.
    if (this.#replica === undefined || this.#handle === undefined || this.#offset.startsWith("snap.")) return;
    signal.throwIfAborted();
    await waitForStore(this.#replica.save(stringifyExactJson({ format: 1, scope: this.#scope, handle: this.#handle, offset: this.#offset,
      cursor: this.#cursor ?? null, resume: this.#resume ?? null, documents: [...this.#documents] }, "shape replica")), signal);
    signal.throwIfAborted();
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
