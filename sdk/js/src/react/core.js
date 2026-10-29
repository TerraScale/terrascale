import { generateId } from "../ids.js";
/** @import { TerraScaleError, TerraScaleResult } from "../result.js" */
/**
 * A subscription cleanup returned by a typed reactive transport.
 *
 * @typedef {(() => void) | { readonly dispose: () => void } | { readonly unsubscribe: () => void }} ReactiveDisposer
 */

/**
 * @typedef {{
 *   readonly signal: AbortSignal;
 * }} ReactiveSubscriptionContext
 */

/**
 * @template TValue
 * @typedef {{
 *   readonly next: (value: TValue) => void;
 *   readonly error: (error: unknown) => void;
 *   readonly reset: (reset: ReactiveReset<TValue>) => void;
 * }} ReactiveQueryCallbacks
 */

/**
 * The operation owned by the public reactive contract. It may use HTTP
 * long-polling or WebSocket delivery; the hook does not know or care which.
 *
 * @template TValue
 * @typedef {{
 *   readonly key?: string | readonly unknown[];
 *   readonly subscribe: (
 *     observer: ReactiveQueryCallbacks<TValue>,
 *     context: ReactiveSubscriptionContext,
 *   ) => ReactiveDisposer | void | Promise<ReactiveDisposer | void>;
 * }} ReactiveQuerySource
 */

/**
 * @typedef {"checkpoint-expired"
 *   | "retention-advanced"
 *   | "generation-changed"
 *   | "checkpoint-not-current"
 *   | "view-diverged"
 *   | "oversized"
 *   | "invalid-operations"
 *   | "invalid-checkpoint"
 *   | "backpressure"
 *   | (string & {})} ReactiveResetReason
 */

/**
 * A server-authorized reset. The checkpoint remains opaque to the hook.
 *
 * @template TValue
 * @typedef {{
 *   readonly reason: ReactiveResetReason;
 *   readonly data?: TValue;
 *   readonly checkpoint?: string | Uint8Array;
 *   readonly code?: string;
 *   readonly message?: string;
 * }} ReactiveReset
 */

/** @typedef {"subscription" | "transport" | "remote" | "cancelled" | "mutation"} ReactiveErrorKind */

/**
 * @typedef {{
 *   readonly kind: ReactiveErrorKind;
 *   readonly code: string;
 *   readonly message: string;
 *   readonly cause?: unknown;
 *   readonly terraScale?: TerraScaleError;
 * }} ReactiveError
 */

/** @typedef {"pending" | "success" | "error" | "reset"} ReactiveQueryStatus */

/**
 * @template TValue
 * @typedef {{
 *   readonly status: ReactiveQueryStatus;
 *   readonly data: TValue | undefined;
 *   readonly error: ReactiveError | undefined;
 *   readonly reset: ReactiveReset<TValue> | undefined;
 *   readonly isLoading: boolean;
 *   readonly isFetching: boolean;
 *   readonly isStale: boolean;
 * }} ReactiveQueryState
 */

/**
 * @template TValue
 * @typedef {{
 *   readonly enabled?: boolean;
 *   readonly initialData?: TValue;
 * }} ReactiveQueryOptions
 */

/**
 * Framework-neutral query observer used by useQuery and SSR-safe tests.
 *
 * @template TValue
 */
export class ReactiveQueryObserver {
  /** @readonly @type {ReactiveQuerySource<TValue>} */
  #source;
  /** @type {boolean} */
  #enabled;
  /** @readonly @type {Set<() => void>} */
  #listeners = new Set();
  /** @readonly @type {ReactiveQueryState<TValue>} */
  #serverSnapshot;
  /** @type {ReactiveQueryState<TValue>} */
  #state;
  /** @type {AbortController | undefined} */
  #abort;
  /** @type {(() => void) | undefined} */
  #cleanup;
  #generation = 0;
  #started = false;

  /**
   * @param {ReactiveQuerySource<TValue>} source
   * @param {ReactiveQueryOptions<TValue>} [options]
   */
  constructor(source, options = {}) {
    this.#source = source;
    this.#enabled = options.enabled ?? true;
    const hasInitial = options.initialData !== undefined;
    this.#state = freezeState({
      status: hasInitial ? "success" : "pending",
      data: options.initialData,
      error: undefined,
      reset: undefined,
      isLoading: !hasInitial,
      isFetching: false,
      isStale: false,
    });
    this.#serverSnapshot = this.#state;
  }

  /** @returns {ReactiveQueryState<TValue>} */
  getSnapshot = () => this.#state;
  /** @returns {ReactiveQueryState<TValue>} */
  getServerSnapshot = () => this.#serverSnapshot;

  /**
   * Updates the small, hook-facing option set without replacing the external
   * store. React may render a hook with a new options object while preserving
   * the observer; an `enabled` transition must therefore start/stop delivery
   * at the subscription boundary instead of leaving a stale subscription.
   *
   * @param {ReactiveQueryOptions<TValue>} [options]
   * @returns {void}
   */
  updateOptions(options = {}) {
    const enabled = options.enabled ?? true;
    const wasEnabled = this.#enabled;
    this.#enabled = enabled;
    if (!enabled && wasEnabled) {
      this.dispose();
    } else if (enabled && !wasEnabled && this.#listeners.size > 0) {
      this.start();
    }
  }

  /**
   * @param {() => void} listener
   * @returns {() => void}
   */
  subscribe = listener => {
    this.#listeners.add(listener);
    if (this.#listeners.size === 1 && this.#enabled) this.start();
    return () => {
      this.#listeners.delete(listener);
      if (this.#listeners.size === 0) this.dispose();
    };
  };

  /** @returns {void} */
  start() {
    if (this.#started || !this.#enabled) return;
    this.#started = true;
    const abort = new AbortController();
    this.#abort = abort;
    const generation = ++this.#generation;
    this.#setState({
      ...this.#state,
      isFetching: true,
      isLoading: this.#state.data === undefined,
      ...(this.#state.data === undefined ? { status: /** @type {const} */ ("pending") } : {}),
    });
    // State listeners may synchronously refetch or disable this generation.
    if (generation !== this.#generation || abort.signal.aborted) return;
    /** @type {ReactiveDisposer | void | Promise<ReactiveDisposer | void>} */
    let result;
    try {
      result = this.#source.subscribe(this.#observer(generation), { signal: abort.signal });
    } catch (error) {
      this.#onError(generation, error);
      return;
    }
    /**
     * @param {ReactiveDisposer | void} cleanup
     * @returns {void}
     */
    const retainCleanup = cleanup => {
      if (generation !== this.#generation || abort.signal.aborted) disposeSubscription(cleanup);
      else this.#cleanup = toCleanup(cleanup);
    };
    if (result instanceof Promise) {
      void result.then(retainCleanup, error => this.#onError(generation, error));
    } else {
      retainCleanup(result);
    }
  }

  /**
   * Aborts HTTP polling and disposes the current transport subscription.
   *
   * @returns {void}
   */
  dispose() {
    if (!this.#started) return;
    ++this.#generation;
    const abort = this.#abort;
    this.#abort = undefined;
    const cleanup = this.#cleanup;
    this.#cleanup = undefined;
    this.#started = false;
    abort?.abort();
    cleanup?.();
  }

  /**
   * Restart only while an enabled query has live observers.
   *
   * @returns {void}
   */
  refetch() {
    if (!this.#enabled || this.#listeners.size === 0) return;
    this.dispose();
    this.start();
  }

  /**
   * @param {number} generation
   * @returns {ReactiveQueryCallbacks<TValue>}
   */
  #observer(generation) {
    return {
      next: value => {
        if (generation !== this.#generation) return;
        if (this.#state.status === "success" && this.#state.data === value && this.#state.isFetching && !this.#state.isStale) return;
        this.#setState({
          status: "success",
          data: value,
          error: undefined,
          reset: undefined,
          isLoading: false,
          isFetching: true,
          isStale: false,
        });
      },
      error: error => this.#onError(generation, error),
      reset: reset => {
        if (generation !== this.#generation) return;
        this.#setState({
          status: "reset",
          data: reset.data,
          error: undefined,
          reset,
          isLoading: reset.data === undefined,
          isFetching: true,
          isStale: true,
        });
      },
    };
  }

  /**
   * @param {number} generation
   * @param {unknown} cause
   * @returns {void}
   */
  #onError(generation, cause) {
    if (generation !== this.#generation || this.#abort?.signal.aborted) return;
    const error = normalizeReactiveError(cause, "subscription");
    if (generation !== this.#generation || this.#abort?.signal.aborted) return;
    // A subscription error is terminal for this delivery generation. Fence
    // late stream events before aborting and disposing the transport so an
    // already reported error cannot be replaced by a subsequent value.
    const terminalGeneration = ++this.#generation;
    const abort = this.#abort;
    this.#abort = undefined;
    const cleanup = this.#cleanup;
    this.#cleanup = undefined;
    this.#started = false;
    // Abort listeners and transport cleanup may synchronously refetch. Detach
    // the failed generation first, then leave any replacement ownership intact.
    abort?.abort();
    cleanup?.();
    if (terminalGeneration !== this.#generation) return;
    this.#setState({
      ...this.#state,
      status: "error",
      error,
      isLoading: this.#state.data === undefined,
      isFetching: false,
      isStale: true,
    });
  }

  /**
   * @param {ReactiveQueryState<TValue>} state
   * @returns {void}
   */
  #setState(state) {
    this.#state = freezeState(state);
    for (const listener of this.#listeners) listener();
  }
}

/**
 * @template TValue
 * @typedef {{ readonly kind: "snapshot" | "data"; readonly value: TValue }
 *   | { readonly kind: "reset"; readonly reset: ReactiveReset<TValue> }
 *   | { readonly kind: "error"; readonly error: unknown }} ReactiveHttpEvent
 */

/**
 * Typed boundary for the owning HTTP contract. Implementations perform the
 * documented request and polling protocol; this adapter only maps events to
 * the hook's observer and propagates AbortSignal.
 *
 * @template TRequest, TValue
 * @typedef {{
 *   readonly subscribe: (
 *     request: TRequest,
 *     observer: { readonly event: (event: ReactiveHttpEvent<TValue>) => void },
 *     context: ReactiveSubscriptionContext,
 *   ) => ReactiveDisposer | void | Promise<ReactiveDisposer | void>;
 * }} ReactiveHttpTransport
 */

/**
 * @template TRequest, TValue
 * @param {ReactiveHttpTransport<TRequest, TValue>} transport
 * @param {TRequest} request
 * @param {string | readonly unknown[]} [key]
 * @returns {ReactiveQuerySource<TValue>}
 */
export function createReactiveHttpQuery(transport, request, key) {
  return Object.freeze({
    ...(key === undefined ? {} : { key }),
    /**
     * @param {ReactiveQueryCallbacks<TValue>} observer
     * @param {ReactiveSubscriptionContext} context
     */
    subscribe: (observer, context) =>
      transport.subscribe(request, {
        event: event => {
          switch (event.kind) {
            case "snapshot":
            case "data":
              observer.next(event.value);
              break;
            case "reset":
              observer.reset(event.reset);
              break;
            case "error":
              observer.error(event.error);
              break;
          }
        },
      }, context),
  });
}

/**
 * @typedef {{
 *   readonly signal: AbortSignal;
 *   readonly idempotencyKey: string;
 * }} ReactiveMutationContext
 */

/** @typedef {void | (() => void) | { readonly rollback: () => void }} OptimisticRollback */

/**
 * @template TValue, TVariables
 * @typedef {{
 *   readonly mutationFn: (
 *     variables: TVariables,
 *     context: ReactiveMutationContext,
 *   ) => Promise<TValue | TerraScaleResult<TValue>>;
 *   readonly idempotencyKey?: string | ((variables: TVariables) => string);
 *   readonly onMutate?: (variables: TVariables, context: ReactiveMutationContext) => OptimisticRollback | Promise<OptimisticRollback>;
 * }} ReactiveMutationOptions
 */

/** @typedef {"idle" | "pending" | "success" | "error" | "cancelled"} ReactiveMutationStatus */

/**
 * @template TValue
 * @typedef {{
 *   readonly status: ReactiveMutationStatus;
 *   readonly data: TValue | undefined;
 *   readonly error: ReactiveError | undefined;
 *   readonly idempotencyKey: string | undefined;
 *   readonly isPending: boolean;
 * }} ReactiveMutationState
 */

/**
 * @template TValue, TVariables
 */
export class ReactiveMutationObserver {
  /** @type {ReactiveMutationOptions<TValue, TVariables>} */
  #options;
  /** @readonly @type {Set<() => void>} */
  #listeners = new Set();
  /** @type {ReactiveMutationState<TValue>} */
  #state = freezeMutationState({ status: "idle", data: /** @type {TValue | undefined} */ (undefined), error: undefined, idempotencyKey: undefined, isPending: false });
  /** @type {AbortController | undefined} */
  #abort;
  /** @type {(() => void) | undefined} */
  #rollback;
  /** @type {string | undefined} */
  #activeKey;
  #run = 0;
  #activeRun = 0;
  #disposed = false;

  /**
   * @param {ReactiveMutationOptions<TValue, TVariables>} options
   */
  constructor(options) {
    this.#options = options;
  }

  /**
   * Keep callbacks current when a component re-renders without replacing the
   * observer (the normal `useMutation` lifecycle). A mutation already running
   * captures the callbacks it started with, so a render cannot redirect an
   * in-flight durable request.
   *
   * @param {ReactiveMutationOptions<TValue, TVariables>} options
   * @returns {void}
   */
  updateOptions(options) {
    this.#options = options;
  }

  /** @returns {ReactiveMutationState<TValue>} */
  getSnapshot = () => this.#state;
  /**
   * @param {() => void} listener
   * @returns {() => void}
   */
  subscribe = listener => {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  };

  /**
   * @param {TVariables} variables
   * @returns {Promise<TValue>}
   */
  async mutateAsync(variables) {
    if (this.#disposed) throw new ReactiveMutationError("cancelled", "the mutation observer has been disposed");
    if (this.#state.isPending) throw new ReactiveMutationError("mutation_in_flight", "only one mutation may be in flight per hook");
    const options = this.#options;
    const idempotencyKey = normalizeIdempotencyKey(
      typeof options.idempotencyKey === "function"
        ? options.idempotencyKey(variables)
        : options.idempotencyKey ?? createIdempotencyKey(),
    );
    const abort = new AbortController();
    const run = ++this.#run;
    const context = Object.freeze({ signal: abort.signal, idempotencyKey });
    this.#abort = abort;
    this.#activeRun = run;
    this.#activeKey = idempotencyKey;
    /** @type {(() => void) | undefined} */
    let rollback;
    /** @returns {void} */
    const rollbackNow = () => {
      const pending = rollback;
      rollback = undefined;
      pending?.();
    };
    this.#rollback = rollbackNow;
    try {
      this.#setState({ status: "pending", data: this.#state.data, error: undefined, idempotencyKey, isPending: true });
      if (abort.signal.aborted || run !== this.#activeRun) throw cancelledError();
      rollback = toRollback(await options.onMutate?.(variables, context));
      if (abort.signal.aborted || run !== this.#activeRun) throw cancelledError();
      const result = await options.mutationFn(variables, context);
      const value = unwrapMutationResult(result);
      if (abort.signal.aborted) throw cancelledError();
      rollback = undefined;
      if (run === this.#activeRun) this.#setState({ status: "success", data: value, error: undefined, idempotencyKey, isPending: false });
      return value;
    } catch (cause) {
      rollbackNow();
      const error = normalizeReactiveError(cause, abort.signal.aborted ? "cancelled" : "mutation");
      if (run === this.#activeRun) this.#setState({ status: error.kind === "cancelled" ? "cancelled" : "error", data: this.#state.data, error, idempotencyKey, isPending: false });
      throw cause instanceof Error ? cause : new ReactiveMutationError(error.code, error.message, cause);
    } finally {
      if (run === this.#activeRun) {
        this.#rollback = undefined;
        this.#abort = undefined;
        this.#activeKey = undefined;
      }
    }
  }

  /**
   * @param {TVariables} variables
   * @returns {void}
   */
  mutate(variables) {
    void this.mutateAsync(variables).catch(() => undefined);
  }

  /**
   * Cancels the durable request and immediately rolls back its presentation.
   *
   * @returns {void}
   */
  cancel() {
    if (!this.#state.isPending) return;
    const abort = this.#abort;
    const rollback = this.#rollback;
    const key = this.#activeKey;
    this.#abort = undefined;
    this.#rollback = undefined;
    this.#activeKey = undefined;
    const cancelledGeneration = this.#activeRun = ++this.#run;
    const error = normalizeReactiveError(cancelledError(), "cancelled");
    // Publish the detached ownership before invoking abort/rollback callbacks,
    // then notify only if those callbacks did not start a replacement run.
    this.#state = freezeMutationState({ status: "cancelled", data: this.#state.data, error, idempotencyKey: key, isPending: false });
    abort?.abort();
    rollback?.();
    if (cancelledGeneration === this.#activeRun) this.#notify();
  }

  /** @returns {void} */
  reset() {
    const resetGeneration = this.#run + (this.#state.isPending ? 1 : 0);
    this.cancel();
    if (resetGeneration !== this.#run) return;
    this.#setState({ status: "idle", data: undefined, error: undefined, idempotencyKey: undefined, isPending: false });
  }

  /** @returns {void} */
  dispose() {
    this.#disposed = true;
    this.cancel();
    this.#listeners.clear();
  }

  /**
   * @param {ReactiveMutationState<TValue>} state
   * @returns {void}
   */
  #setState(state) {
    this.#state = freezeMutationState(state);
    this.#notify();
  }

  /** @returns {void} */
  #notify() {
    for (const listener of this.#listeners) listener();
  }
}

export class ReactiveMutationError extends Error {
  /** @override @readonly */
  name = "ReactiveMutationError";
  /** @readonly @type {string} */
  code;
  /** @override @readonly @type {unknown} */
  cause;

  /**
   * @param {string} code
   * @param {string} message
   * @param {unknown} [cause]
   */
  constructor(code, message, cause) {
    super(`terrascale reactive mutation: ${code}: ${message}`, { cause });
    this.code = code;
    this.cause = cause;
  }
}

/**
 * @template TValue
 * @param {TValue | TerraScaleResult<TValue>} result
 * @returns {TValue}
 */
function unwrapMutationResult(result) {
  if (isTerraScaleResult(result)) {
    if (!result.ok) throw new ReactiveMutationError(result.error.code, result.error.message, result.error);
    return result.value;
  }
  return result;
}

/**
 * @template TValue
 * @param {TValue | TerraScaleResult<TValue>} value
 * @returns {value is TerraScaleResult<TValue>}
 */
function isTerraScaleResult(value) {
  return typeof value === "object" && value !== null && "ok" in value && typeof (/** @type {{ readonly ok?: unknown }} */ (value)).ok === "boolean";
}

/**
 * @param {unknown} cause
 * @param {ReactiveErrorKind} fallback
 * @returns {ReactiveError}
 */
function normalizeReactiveError(cause, fallback) {
  if (cause instanceof ReactiveMutationError) return Object.freeze({ kind: fallback, code: cause.code, message: cause.message, cause: cause.cause });
  if (isTerraScaleError(cause)) {
    return Object.freeze({ kind: fallback, code: cause.code, message: cause.message, cause, terraScale: cause });
  }
  if (cause instanceof Error) return Object.freeze({ kind: fallback, code: fallback, message: cause.message, cause });
  return Object.freeze({ kind: fallback, code: fallback, message: String(cause), cause });
}

/**
 * @param {unknown} value
 * @returns {value is TerraScaleError}
 */
function isTerraScaleError(value) {
  return typeof value === "object" && value !== null && "code" in value && "message" in value && typeof (/** @type {{ readonly code?: unknown }} */ (value)).code === "string";
}

/**
 * @template TValue
 * @param {ReactiveQueryState<TValue>} state
 * @returns {ReactiveQueryState<TValue>}
 */
function freezeState(state) {
  return Object.freeze(state);
}

/**
 * @template TValue
 * @param {ReactiveMutationState<TValue>} state
 * @returns {ReactiveMutationState<TValue>}
 */
function freezeMutationState(state) {
  return Object.freeze(state);
}

/**
 * @param {ReactiveDisposer | void} disposer
 * @returns {(() => void) | undefined}
 */
function toCleanup(disposer) {
  if (disposer === undefined) return undefined;
  if (typeof disposer === "function") return disposer;
  if ("dispose" in disposer) return () => disposer.dispose();
  return () => disposer.unsubscribe();
}

/**
 * @param {ReactiveDisposer | void} disposer
 * @returns {void}
 */
function disposeSubscription(disposer) {
  toCleanup(disposer)?.();
}

/**
 * @param {OptimisticRollback} rollback
 * @returns {(() => void) | undefined}
 */
function toRollback(rollback) {
  if (rollback === undefined) return undefined;
  if (typeof rollback === "function") return rollback;
  return rollback.rollback;
}

/**
 * @param {string} value
 * @returns {string}
 */
function normalizeIdempotencyKey(value) {
  if (typeof value !== "string" || value.trim().length === 0 || value.trim().length > 128) {
    throw new TypeError("Reactive mutation idempotency keys must be nonblank and contain at most 128 characters.");
  }
  return value.trim();
}

/** @returns {string} */
function createIdempotencyKey() {
  return generateId();
}

/** @returns {ReactiveMutationError} */
function cancelledError() {
  return new ReactiveMutationError("cancelled", "the mutation was cancelled");
}
