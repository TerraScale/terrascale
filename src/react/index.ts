import { useCallback, useEffect, useMemo, useRef, useSyncExternalStore } from "react";
import {
  ReactiveMutationObserver,
  ReactiveQueryObserver,
  type ReactiveMutationOptions,
  type ReactiveMutationState,
  type ReactiveQueryOptions,
  type ReactiveQuerySource,
  type ReactiveQueryState,
} from "./core.js";

export * from "./core.js";

/**
 * React's external-store primitive gives useQuery one SSR-safe snapshot and
 * makes every server update flow through the typed reactive subscription.
 * Keep the source object stable (or provide a stable key) between renders.
 */
export function useQuery<TValue>(
  source: ReactiveQuerySource<TValue>,
  options: ReactiveQueryOptions<TValue> = {},
): ReactiveQueryState<TValue> & { readonly refetch: () => void } {
  const observer = useMemo(
    () => new ReactiveQueryObserver(source, options),
    [source],
  );
  const state = useSyncExternalStore(
    observer.subscribe,
    observer.getSnapshot,
    observer.getServerSnapshot,
  );
  const refetch = useCallback(() => {
    observer.dispose();
    observer.start();
  }, [observer]);
  return useMemo(() => Object.freeze({ ...state, refetch }), [state, refetch]);
}

export type UseMutationResult<TValue, TVariables> = ReactiveMutationState<TValue> & {
  readonly mutate: (variables: TVariables) => void;
  readonly mutateAsync: (variables: TVariables) => Promise<TValue>;
  readonly cancel: () => void;
  readonly reset: () => void;
};

/**
 * A durable mutation always receives one idempotency identity and AbortSignal.
 * The optional onMutate result is a reversible presentation rollback; it is
 * never an offline queue and is removed on either acceptance or rejection.
 */
export function useMutation<TValue, TVariables = void>(
  options: ReactiveMutationOptions<TValue, TVariables>,
): UseMutationResult<TValue, TVariables> {
  const observerRef = useRef<ReactiveMutationObserver<TValue, TVariables> | undefined>(undefined);
  if (observerRef.current === undefined) observerRef.current = new ReactiveMutationObserver(options);
  const observer = observerRef.current;
  const state = useSyncExternalStore(observer.subscribe, observer.getSnapshot, observer.getSnapshot);
  useEffect(() => () => observer.dispose(), [observer]);
  const mutate = useCallback((variables: TVariables) => observer.mutate(variables), [observer]);
  const mutateAsync = useCallback((variables: TVariables) => observer.mutateAsync(variables), [observer]);
  const cancel = useCallback(() => observer.cancel(), [observer]);
  const reset = useCallback(() => observer.reset(), [observer]);
  return useMemo(() => Object.freeze({ ...state, mutate, mutateAsync, cancel, reset }), [state, mutate, mutateAsync, cancel, reset]);
}

/** Explicitly disposes a mutation observer created outside a React tree. */
export function disposeMutation<TValue, TVariables>(
  observer: ReactiveMutationObserver<TValue, TVariables>,
): void {
  observer.dispose();
}
