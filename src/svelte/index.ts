import {
  ReactiveMutationObserver,
  ReactiveQueryObserver,
  type ReactiveMutationOptions,
  type ReactiveMutationState,
  type ReactiveQueryOptions,
  type ReactiveQuerySource,
  type ReactiveQueryState,
} from "../react/core.js";

export * from "../react/core.js";

/** The Svelte store contract; `$store` auto-subscription binds to it directly. */
export type SvelteUnsubscriber = () => void;

export type SvelteSubscriber<TValue> = (value: TValue) => void;

export type SvelteReadable<TValue> = {
  readonly subscribe: (run: SvelteSubscriber<TValue>) => SvelteUnsubscriber;
};

export type SvelteQuery<TValue> = SvelteReadable<ReactiveQueryState<TValue>> & {
  /** Restarts delivery from a fresh generation, like the React `refetch`. */
  readonly refetch: () => void;
};

/**
 * One reactive query as a Svelte store. The observer lifecycle follows the
 * store subscription count, so `$query` in a component starts delivery on
 * mount and disposes the transport on unmount. Keep the source object stable
 * between renders.
 */
export function createQuery<TValue>(
  source: ReactiveQuerySource<TValue>,
  options: ReactiveQueryOptions<TValue> = {},
): SvelteQuery<TValue> {
  const observer = new ReactiveQueryObserver<TValue>(source, options);
  const subscribe = (run: SvelteSubscriber<ReactiveQueryState<TValue>>): SvelteUnsubscriber => {
    const stop = observer.subscribe(() => run(observer.getSnapshot()));
    run(observer.getSnapshot());
    return stop;
  };
  const refetch = (): void => {
    observer.refetch();
  };
  return Object.freeze({ subscribe, refetch });
}

export type SvelteMutation<TValue, TVariables> = SvelteReadable<ReactiveMutationState<TValue>> & {
  readonly mutate: (variables: TVariables) => void;
  readonly mutateAsync: (variables: TVariables) => Promise<TValue>;
  readonly cancel: () => void;
  readonly reset: () => void;
};

/** A durable mutation as a Svelte store; `$mutation.isPending` drives the UI. */
export function createMutation<TValue, TVariables = void>(
  options: ReactiveMutationOptions<TValue, TVariables>,
): SvelteMutation<TValue, TVariables> {
  const observer = new ReactiveMutationObserver<TValue, TVariables>(options);
  let subscribers = 0;
  const subscribe = (run: SvelteSubscriber<ReactiveMutationState<TValue>>): SvelteUnsubscriber => {
    subscribers += 1;
    const stop = observer.subscribe(() => run(observer.getSnapshot()));
    run(observer.getSnapshot());
    let active = true;
    return () => {
      if (!active) return;
      active = false;
      stop();
      subscribers -= 1;
      if (subscribers === 0) observer.cancel();
    };
  };
  const mutate = (variables: TVariables): void => observer.mutate(variables);
  const mutateAsync = (variables: TVariables): Promise<TValue> => observer.mutateAsync(variables);
  const cancel = (): void => observer.cancel();
  const reset = (): void => observer.reset();
  return Object.freeze({ subscribe, mutate, mutateAsync, cancel, reset });
}
