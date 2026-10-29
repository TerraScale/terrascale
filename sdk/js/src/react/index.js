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
 * React's external-store primitive gives useQuery one SSR-safe snapshot and
 * makes every server update flow through the typed reactive subscription.
 * Keep the source object stable between renders.
 *
 * @template TValue
 * @param {ReactiveQuerySource<TValue>} source
 * @param {ReactiveQueryOptions<TValue>} [options]
 * @returns {ReactiveQueryState<TValue> & { readonly refetch: () => void }}
 */
export function useQuery(source, options = {}) {
  const observer = useMemo(
    () => new ReactiveQueryObserver(source, options),
    [source],
  );
  observer.updateOptions(options);
  const state = useSyncExternalStore(
    observer.subscribe,
    observer.getSnapshot,
    observer.getServerSnapshot,
  );
  const refetch = useCallback(() => {
    observer.refetch();
  }, [observer]);
  return useMemo(() => Object.freeze({ ...state, refetch }), [state, refetch]);
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
      // React may immediately mount this effect again in Strict Mode. Cancel
      // owned work now, then dispose only if the effect stays unmounted.
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
