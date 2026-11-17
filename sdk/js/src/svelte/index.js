import { V1TableShape } from "../v1/table-shape.js";
import { ShapeQueryObserver } from "../v1/shape-query.js";
/** @typedef {import("../v1/shape-query.js").ShapeQueryState} ShapeQueryState */
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
 * @typedef {SvelteReadable<ShapeQueryState> & { readonly setShape: (shape: V1TableShape) => void }} SvelteShapeQuery
 */

/**
 * @overload
 * @param {V1TableShape} source
 * @returns {SvelteShapeQuery}
 */
/**
 * @template TValue
 * @overload
 * @param {ReactiveQuerySource<TValue>} source
 * @param {ReactiveQueryOptions<TValue>} [options]
 * @returns {SvelteQuery<TValue>}
 */
/**
 * A live shape or typed reactive query as a readable Svelte store.
 * @template TValue
 * @param {V1TableShape | ReactiveQuerySource<TValue>} source
 * @param {ReactiveQueryOptions<TValue>} [options]
 * @returns {SvelteShapeQuery | SvelteQuery<TValue>}
 */
export function createQuery(source, options = {}) {
  if (source instanceof V1TableShape) {
    const observer = new ShapeQueryObserver(source);
    return Object.freeze({
      /** @param {SvelteSubscriber<ShapeQueryState>} run */
      subscribe: run => {
        const stop = observer.subscribe(() => run(observer.getSnapshot()));
        run(observer.getSnapshot());
        return stop;
      },
      /** @param {V1TableShape} shape */
      setShape: shape => observer.setShape(shape),
    });
  }
  const observer = new ReactiveQueryObserver(source, options);
  /** @param {SvelteSubscriber<ReactiveQueryState<TValue>>} run */
  const subscribe = run => {
    const stop = observer.subscribe(() => run(observer.getSnapshot()));
    run(observer.getSnapshot());
    return stop;
  };
  const refetch = () => observer.refetch();
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
