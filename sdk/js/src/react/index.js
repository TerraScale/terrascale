import { V1TableShape } from "../v1/table-shape.js";
import { ShapeQueryObserver } from "../v1/shape-query.js";
/** @typedef {import("../v1/shape-query.js").ShapeQueryState} ShapeQueryState */
import { useCallback, useEffect, useMemo, useRef, useSyncExternalStore } from "react";
import {
  ReactiveMutationObserver,
  ReactiveQueryObserver,
} from "./core.js";
/**
 * @import {
 *   ReactiveMutationOptions,
 *   ReactiveMutationState,
 *   ReactiveQueryOptions,
 *   ReactiveQuerySource,
 *   ReactiveQueryState,
 * } from "./core.js"
 */

export * from "./core.js";

/**
 * @overload
 * @param {V1TableShape} source
 * @returns {ShapeQueryState}
 */
/**
 * @template TValue
 * @overload
 * @param {ReactiveQuerySource<TValue>} source
 * @param {ReactiveQueryOptions<TValue>} [options]
 * @returns {ReactiveQueryState<TValue> & { readonly refetch: () => void }}
 */
/**
 * Subscribes directly to a live table shape or a typed reactive source.
 * @template TValue
 * @param {V1TableShape | ReactiveQuerySource<TValue>} source
 * @param {ReactiveQueryOptions<TValue>} [options]
 * @returns {ShapeQueryState | (ReactiveQueryState<TValue> & { readonly refetch: () => void })}
 */
export function useQuery(source, options = {}) {
  const previous = useRef(/** @type {ShapeQueryObserver | undefined} */ (undefined));
  const identity = source instanceof V1TableShape ? source.key : source;
  const observer = useMemo(() => {
    if (source instanceof V1TableShape) {
      return new ShapeQueryObserver(source, previous.current?.getSnapshot().data);
    }
    return new ReactiveQueryObserver(source, options);
  }, [identity]);
  if (observer instanceof ReactiveQueryObserver) observer.updateOptions(options);
  const state = useSyncExternalStore(
    observer.subscribe,
    /** @type {() => ShapeQueryState | ReactiveQueryState<TValue>} */ (observer.getSnapshot),
    observer.getServerSnapshot,
  );
  useEffect(() => {
    previous.current = observer instanceof ShapeQueryObserver ? observer : undefined;
  }, [observer]);
  const refetch = useCallback(() => {
    if (observer instanceof ReactiveQueryObserver) observer.refetch();
  }, [observer]);
  return useMemo(() => observer instanceof ShapeQueryObserver
    ? /** @type {ShapeQueryState} */ (state)
    : Object.freeze({ .../** @type {ReactiveQueryState<TValue>} */ (state), refetch }), [observer, state, refetch]);
}

/**
 * @template TValue, TVariables
 * @typedef {ReactiveMutationState<TValue> & {
 *   readonly mutate: (variables: TVariables) => void;
 *   readonly mutateAsync: (variables: TVariables) => Promise<TValue>;
 *   readonly cancel: () => void;
 *   readonly reset: () => void;
 * }} UseMutationResult
 */

/**
 * A durable mutation always receives one idempotency identity and AbortSignal.
 * The optional onMutate result is a reversible presentation rollback; it is
 * never an offline queue and is removed on either acceptance or rejection.
 *
 * @template TValue
 * @template [TVariables=void]
 * @param {ReactiveMutationOptions<TValue, TVariables>} options
 * @returns {UseMutationResult<TValue, TVariables>}
 */
export function useMutation(options) {
  const observerRef = useRef(/** @type {ReactiveMutationObserver<TValue, TVariables> | undefined} */ (undefined));
  if (observerRef.current === undefined) observerRef.current = new ReactiveMutationObserver(options);
  const observer = observerRef.current;
  observer.updateOptions(options);
  const state = useSyncExternalStore(observer.subscribe, observer.getSnapshot, observer.getSnapshot);
  const effectGeneration = useRef(0);
  useEffect(() => {
    const generation = ++effectGeneration.current;
    return () => {
      observer.cancel();
      queueMicrotask(() => {
        if (effectGeneration.current === generation) observer.dispose();
      });
    };
  }, [observer]);
  const mutate = useCallback(/** @param {TVariables} variables */ variables => observer.mutate(variables), [observer]);
  const mutateAsync = useCallback(/** @param {TVariables} variables */ variables => observer.mutateAsync(variables), [observer]);
  const cancel = useCallback(() => observer.cancel(), [observer]);
  const reset = useCallback(() => observer.reset(), [observer]);
  return useMemo(() => Object.freeze({ ...state, mutate, mutateAsync, cancel, reset }), [state, mutate, mutateAsync, cancel, reset]);
}

/**
 * Explicitly disposes a mutation observer created outside a React tree.
 *
 * @template TValue, TVariables
 * @param {ReactiveMutationObserver<TValue, TVariables>} observer
 * @returns {void}
 */
export function disposeMutation(observer) {
  observer.dispose();
}
