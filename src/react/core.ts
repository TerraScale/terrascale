import type { TerraScaleError, TerraScaleResult } from "../result.js";
import type { DatabaseResult } from "../database-types.js";

export {
  createDatabaseQuery,
  DatabaseSubscriptionError,
  DatabaseSubscriptionView,
  type DatabaseQueryCallbacks,
  type DatabaseQueryOptions,
  type DatabaseQuerySource,
  type DatabaseViewBinding,
  type DatabaseViewPosition,
  type DatabaseViewSnapshot,
} from "../database-view.js";

type DatabaseError = Extract<DatabaseResult<never>, { readonly ok: false }>["error"];

/** A subscription cleanup returned by a typed reactive transport. */
export type ReactiveDisposer = (() => void) | { readonly dispose: () => void } | { readonly unsubscribe: () => void };

export type ReactiveSubscriptionContext = {
  readonly signal: AbortSignal;
};

export type ReactiveQueryCallbacks<TValue> = {
  readonly next: (value: TValue) => void;
  readonly error: (error: unknown) => void;
  readonly reset: (reset: ReactiveReset<TValue>) => void;
};

/**
 * The operation owned by the public reactive contract. It may use HTTP
 * long-polling or WebSocket delivery; the hook does not know or care which.
 */
export type ReactiveQuerySource<TValue> = {
  readonly key?: string | readonly unknown[];
  readonly subscribe: (
    observer: ReactiveQueryCallbacks<TValue>,
    context: ReactiveSubscriptionContext,
  ) => ReactiveDisposer | void | Promise<ReactiveDisposer | void>;
};

export type ReactiveResetReason =
  | "checkpoint-expired"
  | "retention-advanced"
  | "generation-changed"
  | "checkpoint-not-current"
  | "view-diverged"
  | "oversized"
  | "invalid-operations"
  | "invalid-checkpoint"
  | "backpressure"
  | (string & {});

/** A server-authorized reset. The checkpoint remains opaque to the hook. */
export type ReactiveReset<TValue> = {
  readonly reason: ReactiveResetReason;
  readonly data?: TValue;
  readonly checkpoint?: string | Uint8Array;
  readonly code?: string;
  readonly message?: string;
};

export type ReactiveErrorKind = "subscription" | "transport" | "remote" | "cancelled" | "mutation";

export type ReactiveError = {
  readonly kind: ReactiveErrorKind;
  readonly code: string;
  readonly message: string;
  readonly cause?: unknown;
  readonly terraScale?: TerraScaleError;
  readonly database?: DatabaseError;
};

export type ReactiveQueryStatus = "pending" | "success" | "error" | "reset";

export type ReactiveQueryState<TValue> = {
  readonly status: ReactiveQueryStatus;
  readonly data: TValue | undefined;
  readonly error: ReactiveError | undefined;
  readonly reset: ReactiveReset<TValue> | undefined;
  readonly isLoading: boolean;
  readonly isFetching: boolean;
  readonly isStale: boolean;
};

export type ReactiveQueryOptions<TValue> = {
  readonly enabled?: boolean;
  readonly initialData?: TValue;
};

type Listener = () => void;

/** Framework-neutral query observer used by useQuery and SSR-safe tests. */
export class ReactiveQueryObserver<TValue> {
  readonly #source: ReactiveQuerySource<TValue>;
  #enabled: boolean;
  readonly #listeners = new Set<Listener>();
  readonly #serverSnapshot: ReactiveQueryState<TValue>;
  #state: ReactiveQueryState<TValue>;
  #abort: AbortController | undefined;
  #cleanup: (() => void) | undefined;
  #generation = 0;
  #started = false;

  public constructor(source: ReactiveQuerySource<TValue>, options: ReactiveQueryOptions<TValue> = {}) {
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

  public getSnapshot = (): ReactiveQueryState<TValue> => this.#state;
  public getServerSnapshot = (): ReactiveQueryState<TValue> => this.#serverSnapshot;

  /**
   * Updates the small, hook-facing option set without replacing the external
   * store. React may render a hook with a new options object while preserving
   * the observer; an `enabled` transition must therefore start/stop delivery
   * at the subscription boundary instead of leaving a stale subscription.
   */
  public updateOptions(options: ReactiveQueryOptions<TValue> = {}): void {
    const enabled = options.enabled ?? true;
    const wasEnabled = this.#enabled;
    this.#enabled = enabled;
    if (!enabled && wasEnabled) {
      this.dispose();
    } else if (enabled && !wasEnabled && this.#listeners.size > 0) {
      this.start();
    }
  }

  public subscribe = (listener: Listener): (() => void) => {
    this.#listeners.add(listener);
    if (this.#listeners.size === 1 && this.#enabled) this.start();
    return () => {
      this.#listeners.delete(listener);
      if (this.#listeners.size === 0) this.dispose();
    };
  };

  public start(): void {
    if (this.#started || !this.#enabled) return;
    this.#started = true;
    const abort = new AbortController();
    this.#abort = abort;
    const generation = ++this.#generation;
    this.#setState({
      ...this.#state,
      isFetching: true,
      isLoading: this.#state.data === undefined,
      ...(this.#state.data === undefined ? { status: "pending" as const } : {}),
    });
    // State listeners may synchronously refetch or disable this generation.
    if (generation !== this.#generation || abort.signal.aborted) return;
    let result: ReactiveDisposer | void | Promise<ReactiveDisposer | void>;
    try {
      result = this.#source.subscribe(this.#observer(generation), { signal: abort.signal });
    } catch (error) {
      this.#onError(generation, error);
      return;
    }
    const retainCleanup = (cleanup: ReactiveDisposer | void): void => {
      if (generation !== this.#generation || abort.signal.aborted) disposeSubscription(cleanup);
      else this.#cleanup = toCleanup(cleanup);
    };
    if (result instanceof Promise) {
      void result.then(retainCleanup, error => this.#onError(generation, error));
    } else {
      retainCleanup(result);
    }
  }

  /** Aborts HTTP polling and disposes the current transport subscription. */
  public dispose(): void {
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

  /** Restart only while an enabled query has live observers. */
  public refetch(): void {
    if (!this.#enabled || this.#listeners.size === 0) return;
    this.dispose();
    this.start();
  }

  #observer(generation: number): ReactiveQueryCallbacks<TValue> {
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

  #onError(generation: number, cause: unknown): void {
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

  #setState(state: ReactiveQueryState<TValue>): void {
    this.#state = freezeState(state);
    for (const listener of this.#listeners) listener();
  }
}

export type ReactiveHttpEvent<TValue> =
  | { readonly kind: "snapshot" | "data"; readonly value: TValue }
  | { readonly kind: "reset"; readonly reset: ReactiveReset<TValue> }
  | { readonly kind: "error"; readonly error: unknown };

/**
 * Typed boundary for the owning HTTP contract. Implementations perform the
 * documented request and polling protocol; this adapter only maps events to
 * the hook's observer and propagates AbortSignal.
 */
export type ReactiveHttpTransport<TRequest, TValue> = {
  readonly subscribe: (
    request: TRequest,
    observer: { readonly event: (event: ReactiveHttpEvent<TValue>) => void },
    context: ReactiveSubscriptionContext,
  ) => ReactiveDisposer | void | Promise<ReactiveDisposer | void>;
};

export function createReactiveHttpQuery<TRequest, TValue>(
  transport: ReactiveHttpTransport<TRequest, TValue>,
  request: TRequest,
  key?: string | readonly unknown[],
): ReactiveQuerySource<TValue> {
  return Object.freeze({
    ...(key === undefined ? {} : { key }),
    subscribe: (observer: ReactiveQueryCallbacks<TValue>, context: ReactiveSubscriptionContext) =>
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

export type ReactiveMutationContext = {
  readonly signal: AbortSignal;
  readonly idempotencyKey: string;
};

export type OptimisticRollback = void | (() => void) | { readonly rollback: () => void };

export type ReactiveMutationOptions<TValue, TVariables> = {
  readonly mutationFn: (
    variables: TVariables,
    context: ReactiveMutationContext,
  ) => Promise<TValue | TerraScaleResult<TValue> | DatabaseResult<TValue>>;
  readonly idempotencyKey?: string | ((variables: TVariables) => string);
  readonly onMutate?: (variables: TVariables, context: ReactiveMutationContext) => OptimisticRollback | Promise<OptimisticRollback>;
};

export type ReactiveMutationStatus = "idle" | "pending" | "success" | "error" | "cancelled";

export type ReactiveMutationState<TValue> = {
  readonly status: ReactiveMutationStatus;
  readonly data: TValue | undefined;
  readonly error: ReactiveError | undefined;
  readonly idempotencyKey: string | undefined;
  readonly isPending: boolean;
};

export class ReactiveMutationObserver<TValue, TVariables> {
  #options: ReactiveMutationOptions<TValue, TVariables>;
  readonly #listeners = new Set<Listener>();
  #state: ReactiveMutationState<TValue> = freezeMutationState({ status: "idle", data: undefined as TValue | undefined, error: undefined, idempotencyKey: undefined, isPending: false });
  #abort: AbortController | undefined;
  #rollback: (() => void) | undefined;
  #activeKey: string | undefined;
  #run = 0;
  #activeRun = 0;
  #cancelledRun: number | undefined;
  #disposed = false;

  public constructor(options: ReactiveMutationOptions<TValue, TVariables>) {
    this.#options = options;
  }

  /** Keep callbacks current when a component re-renders without replacing the
   * observer (the normal `useMutation` lifecycle). A mutation already running
   * captures the callbacks it started with, so a render cannot redirect an
   * in-flight durable request. */
  public updateOptions(options: ReactiveMutationOptions<TValue, TVariables>): void {
    this.#options = options;
  }

  public getSnapshot = (): ReactiveMutationState<TValue> => this.#state;
  public subscribe = (listener: Listener): (() => void) => {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  };

  public async mutateAsync(variables: TVariables): Promise<TValue> {
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
    this.#cancelledRun = undefined;
    this.#activeKey = idempotencyKey;
    let rollback: (() => void) | undefined;
    const rollbackNow = (): void => {
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
      else if (run === this.#cancelledRun && this.#state.status === "cancelled" && error.database !== undefined) {
        // Cancellation stops presentation immediately. A later native result
        // may still establish uncertainty for this same server operation.
        this.#setState({ ...this.#state, error });
      }
      throw cause instanceof Error ? cause : new ReactiveMutationError(error.code, error.message, cause);
    } finally {
      if (run === this.#activeRun) {
        this.#rollback = undefined;
        this.#abort = undefined;
        this.#activeKey = undefined;
      }
    }
  }

  public mutate(variables: TVariables): void {
    void this.mutateAsync(variables).catch(() => undefined);
  }

  /** Cancels the durable request and immediately rolls back its presentation. */
  public cancel(): void {
    if (!this.#state.isPending) return;
    const abort = this.#abort;
    const rollback = this.#rollback;
    const key = this.#activeKey;
    this.#abort = undefined;
    this.#rollback = undefined;
    this.#activeKey = undefined;
    this.#cancelledRun = this.#activeRun;
    const cancelledGeneration = this.#activeRun = ++this.#run;
    const error = normalizeReactiveError(cancelledError(), "cancelled");
    // Publish the detached ownership before invoking abort/rollback callbacks,
    // then notify only if those callbacks did not start a replacement run.
    this.#state = freezeMutationState({ status: "cancelled", data: this.#state.data, error, idempotencyKey: key, isPending: false });
    abort?.abort();
    rollback?.();
    if (cancelledGeneration === this.#activeRun) this.#notify();
  }

  public reset(): void {
    const resetGeneration = this.#run + (this.#state.isPending ? 1 : 0);
    this.cancel();
    if (resetGeneration !== this.#run) return;
    this.#setState({ status: "idle", data: undefined, error: undefined, idempotencyKey: undefined, isPending: false });
  }

  public dispose(): void {
    this.#disposed = true;
    this.cancel();
    this.#listeners.clear();
  }

  #setState(state: ReactiveMutationState<TValue>): void {
    this.#state = freezeMutationState(state);
    this.#notify();
  }

  #notify(): void {
    for (const listener of this.#listeners) listener();
  }
}

export class ReactiveMutationError extends Error {
  public override readonly name = "ReactiveMutationError";
  public constructor(public readonly code: string, message: string, public override readonly cause?: unknown) {
    super(`terrascale reactive mutation: ${code}: ${message}`, { cause });
  }
}

function unwrapMutationResult<TValue>(result: TValue | TerraScaleResult<TValue> | DatabaseResult<TValue>): TValue {
  if (isTerraScaleResult(result)) {
    if (!result.ok) throw new ReactiveMutationError(result.error.code, "message" in result.error ? result.error.message : `TerraBase request failed: ${result.error.code}`, result.error);
    return result.value;
  }
  return result;
}

function isTerraScaleResult<TValue>(value: TValue | TerraScaleResult<TValue> | DatabaseResult<TValue>): value is TerraScaleResult<TValue> | DatabaseResult<TValue> {
  return typeof value === "object" && value !== null && "ok" in value && typeof (value as { readonly ok?: unknown }).ok === "boolean";
}

function normalizeReactiveError(cause: unknown, fallback: ReactiveErrorKind): ReactiveError {
  if (cause instanceof ReactiveMutationError) return Object.freeze({ kind: fallback, code: cause.code, message: cause.message, cause: cause.cause, ...(isDatabaseError(cause.cause) ? { database: cause.cause } : {}) });
  if (isTerraScaleError(cause)) {
    return Object.freeze({ kind: fallback, code: cause.code, message: cause.message, cause, terraScale: cause, ...(cause instanceof Error && isDatabaseError(cause.cause) ? { database: cause.cause } : {}) });
  }
  if (isDatabaseError(cause)) return Object.freeze({ kind: fallback, code: cause.code, message: `TerraBase request failed: ${cause.code}`, cause, database: cause });
  if (cause instanceof Error) return Object.freeze({ kind: fallback, code: fallback, message: cause.message, cause });
  return Object.freeze({ kind: fallback, code: fallback, message: String(cause), cause });
}

function isDatabaseError(value: unknown): value is DatabaseError {
  return typeof value === "object" && value !== null && "code" in value && typeof value.code === "string" && !("message" in value);
}

function isTerraScaleError(value: unknown): value is TerraScaleError {
  return typeof value === "object" && value !== null && "code" in value && "message" in value && typeof (value as { readonly code?: unknown }).code === "string";
}

function freezeState<TValue>(state: ReactiveQueryState<TValue>): ReactiveQueryState<TValue> {
  return Object.freeze(state);
}

function freezeMutationState<TValue>(state: ReactiveMutationState<TValue>): ReactiveMutationState<TValue> {
  return Object.freeze(state);
}

function toCleanup(disposer: ReactiveDisposer | void): (() => void) | undefined {
  if (disposer === undefined) return undefined;
  if (typeof disposer === "function") return disposer;
  if ("dispose" in disposer) return () => disposer.dispose();
  return () => disposer.unsubscribe();
}

function disposeSubscription(disposer: ReactiveDisposer | void): void {
  toCleanup(disposer)?.();
}

function toRollback(rollback: OptimisticRollback): (() => void) | undefined {
  if (rollback === undefined) return undefined;
  if (typeof rollback === "function") return rollback;
  return rollback.rollback;
}

function normalizeIdempotencyKey(value: string): string {
  if (typeof value !== "string" || value.trim().length === 0 || value.trim().length > 128) {
    throw new TypeError("Reactive mutation idempotency keys must be nonblank and contain at most 128 characters.");
  }
  return value.trim();
}

function createIdempotencyKey(): string {
  const randomUuid = globalThis.crypto?.randomUUID;
  return randomUuid === undefined ? `tsm-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}` : randomUuid.call(globalThis.crypto);
}

function cancelledError(): ReactiveMutationError {
  return new ReactiveMutationError("cancelled", "the mutation was cancelled");
}
