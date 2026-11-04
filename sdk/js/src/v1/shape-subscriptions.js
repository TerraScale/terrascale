/** @import { V1TableShapeState } from "./table-shape.js" */
/** @typedef {{ listener: (state: V1TableShapeState) => void; resolve: () => void; reject: (error: unknown) => void }} Observer */
/** @typedef {{ controller: AbortController; observers: Set<Observer>; latest?: V1TableShapeState; done: Promise<void> }} Group */
/** @type {WeakMap<object, Map<string, Group>>} */
const subscriptions = new WeakMap();

/**
 * @param {object} transport
 * @param {string} key
 * @param {(publish: (state: V1TableShapeState) => void, signal: AbortSignal) => Promise<void>} run
 * @param {(state: V1TableShapeState) => void} listener
 * @param {AbortSignal | undefined} external
 */
export function subscribeShape(transport, key, run, listener, external) {
  let groups = subscriptions.get(transport);
  if (groups === undefined) { groups = new Map(); subscriptions.set(transport, groups); }
  let group = groups.get(key);
  const start = group === undefined;
  if (group === undefined) {
    if (groups.size >= 256) throw new RangeError("A client supports at most 256 distinct active shapes.");
    group = { controller: new AbortController(), observers: new Set(), done: Promise.resolve() };
    groups.set(key, group);
  }
  const shared = group;
  if (shared.observers.size >= 4096) throw new RangeError("A shape supports at most 4096 listeners per client.");
  /** @type {Observer} */
  let observer;
  const done = new Promise((resolve, reject) => { observer = { listener, resolve, reject }; });
  let closed = false;
  const close = () => {
    if (closed) return;
    closed = true;
    external?.removeEventListener("abort", close);
    shared.observers.delete(observer);
    if (shared.observers.size === 0) {
      if (groups.get(key) === shared) groups.delete(key);
      shared.controller.abort(new DOMException("The shape subscription was closed.", "AbortError"));
      shared.done.then(observer.resolve, observer.resolve);
    } else observer.resolve();
  };
  shared.observers.add(observer);
  external?.addEventListener("abort", close, { once: true });
  if (external?.aborted) close();
  if (start) {
    shared.done = Promise.resolve().then(async () => {
      try {
        if (!shared.controller.signal.aborted) await run(state => {
          shared.latest = state;
          for (const entry of [...shared.observers]) {
            try { entry.listener(state); }
            catch (error) { shared.observers.delete(entry); entry.reject(error); }
          }
          if (shared.observers.size === 0) shared.controller.abort();
        }, shared.controller.signal);
        for (const entry of shared.observers) entry.resolve();
      } catch (error) {
        if (!shared.controller.signal.aborted) for (const entry of shared.observers) entry.reject(error);
      } finally {
        if (groups.get(key) === shared) groups.delete(key);
        shared.observers.clear();
      }
    });
  } else if (shared.latest !== undefined) {
    queueMicrotask(() => {
      if (!closed && shared.latest !== undefined) {
        try { listener(shared.latest); }
        catch (error) { observer.reject(error); close(); }
      }
    });
  }
  return { close, done };
}
