/** @import { V1TableShapeState } from "./table-shape.js" */
/** @typedef {{ listener: (state: V1TableShapeState) => void; resolve: () => void; reject: (error: unknown) => void }} Observer */
/** @typedef {{ controller: AbortController; observers: Set<Observer>; latest?: V1TableShapeState; done: Promise<void>; release?: ReturnType<typeof setTimeout> }} Group */
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
  if (external?.aborted) return { close: () => {}, done: Promise.resolve() };
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
  clearTimeout(shared.release);
  delete shared.release;
  const release = () => {
    if (shared.observers.size !== 0 || shared.release !== undefined) return;
    shared.release = setTimeout(() => {
      if (groups.get(key) === shared) groups.delete(key);
      shared.controller.abort(new DOMException("The shape subscription was closed.", "AbortError"));
    }, 2000);
  };
  if (shared.observers.size >= 4096) throw new RangeError("A shape supports at most 4096 listeners per client.");
  let resolveDone = () => {};
  /** @type {(error: unknown) => void} */
  let rejectDone = () => {};
  const done = new Promise((resolve, reject) => { resolveDone = () => resolve(undefined); rejectDone = reject; });
  const observer = { listener, resolve: resolveDone, reject: rejectDone };
  let closed = false;
  const close = () => {
    if (closed) return;
    closed = true;
    external?.removeEventListener("abort", close);
    shared.observers.delete(observer);
    observer.resolve();
    release();
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
          release();
        }, shared.controller.signal);
        for (const entry of shared.observers) entry.resolve();
      } catch (error) {
        if (!shared.controller.signal.aborted) for (const entry of shared.observers) entry.reject(error);
      } finally {
        clearTimeout(shared.release);
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
