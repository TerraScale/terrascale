import {
  ReactiveMutationObserver,
  ReactiveQueryObserver,
} from "../react/core.js";
/**
 * @import {
 *   ReactiveMutationOptions,
 *   ReactiveMutationState,
 *   ReactiveQueryOptions,
 *   ReactiveQuerySource,
 *   ReactiveQueryState,
 * } from "../react/core.js"
 */

export * from "../react/core.js";

/**
 * The Svelte store contract; `$store` auto-subscription binds to it directly.
 *
 * @typedef {() => void} SvelteUnsubscriber
 */

/**
 * @template TValue
 * @typedef {(value: TValue) => void} SvelteSubscriber
 */

/**
 * @template TValue
 * @typedef {{
 *   readonly subscribe: (run: SvelteSubscriber<TValue>) => SvelteUnsubscriber;
 * }} SvelteReadable
 */

/**
 * A query store; `refetch` restarts delivery from a fresh generation, like
 * the React `refetch`.
 *
 * @template TValue
 * @typedef {SvelteReadable<ReactiveQueryState<TValue>> & {
 *   readonly refetch: () => void;
 * }} SvelteQuery
 */

/**
 * One reactive query as a Svelte store. The observer lifecycle follows the
 * store subscription count, so `$query` in a component starts delivery on
 * mount and disposes the transport on unmount. Keep the source object stable
 * between renders.
 *
 * @template TValue
 * @param {ReactiveQuerySource<TValue>} source
 * @param {ReactiveQueryOptions<TValue>} [options]
 * @returns {SvelteQuery<TValue>}
 */
export function createQuery(source, options = {}) {
  /** @type {ReactiveQueryObserver<TValue>} */
  const observer = new ReactiveQueryObserver(source, options);
  /**
   * @param {SvelteSubscriber<ReactiveQueryState<TValue>>} run
   * @returns {SvelteUnsubscriber}
   */
  const subscribe = run => {
    const stop = observer.subscribe(() => run(observer.getSnapshot()));
    run(observer.getSnapshot());
    return stop;
  };
  /** @returns {void} */
  const refetch = () => {
    observer.refetch();
  };
  return Object.freeze({ subscribe, refetch });
}

/**
 * @template TValue, TVariables
 * @typedef {SvelteReadable<ReactiveMutationState<TValue>> & {
 *   readonly mutate: (variables: TVariables) => void;
 *   readonly mutateAsync: (variables: TVariables) => Promise<TValue>;
 *   readonly cancel: () => void;
 *   readonly reset: () => void;
 * }} SvelteMutation
 */

/**
 * A durable mutation as a Svelte store; `$mutation.isPending` drives the UI.
 *
 * @template TValue
 * @template [TVariables=void]
 * @param {ReactiveMutationOptions<TValue, TVariables>} options
 * @returns {SvelteMutation<TValue, TVariables>}
 */
export function createMutation(options) {
  /** @type {ReactiveMutationObserver<TValue, TVariables>} */
  const observer = new ReactiveMutationObserver(options);
  let subscribers = 0;
  /**
   * @param {SvelteSubscriber<ReactiveMutationState<TValue>>} run
   * @returns {SvelteUnsubscriber}
   */
  const subscribe = run => {
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
  /**
   * @param {TVariables} variables
   * @returns {void}
   */
  const mutate = variables => observer.mutate(variables);
  /**
   * @param {TVariables} variables
   * @returns {Promise<TValue>}
   */
  const mutateAsync = variables => observer.mutateAsync(variables);
  /** @returns {void} */
  const cancel = () => observer.cancel();
  /** @returns {void} */
  const reset = () => observer.reset();
  return Object.freeze({ subscribe, mutate, mutateAsync, cancel, reset });
}
