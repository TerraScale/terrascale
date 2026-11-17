/** @import { V1TableRow, V1TableShape, V1TableShapeState } from "./table-shape.js" */

/**
 * @typedef {{
 *   readonly data: readonly V1TableRow[];
 *   readonly status: "loading" | "live" | "reconnecting" | "error";
 *   readonly error: unknown;
 * }} ShapeQueryState
 */

export class ShapeQueryObserver {
  /** @type {V1TableShape} */
  #shape;
  /** @type {Set<() => void>} */
  #listeners = new Set();
  /** @type {ShapeQueryState} */
  #state;
  /** @type {ShapeQueryState} */
  #serverState;
  /** @type {{ close: () => void; done: Promise<void> } | undefined} */
  #subscription;
  #generation = 0;

  /** @param {V1TableShape} shape @param {readonly V1TableRow[]} [data] */
  constructor(shape, data = Object.freeze([])) {
    this.#shape = shape;
    this.#state = Object.freeze({ data, status: "loading", error: undefined });
    this.#serverState = this.#state;
  }

  /** @returns {ShapeQueryState} */
  getSnapshot = () => this.#state;
  /** @returns {ShapeQueryState} */
  getServerSnapshot = () => this.#serverState;

  /** @param {() => void} listener @returns {() => void} */
  subscribe = listener => {
    this.#listeners.add(listener);
    if (this.#listeners.size === 1 && this.#state.status !== "error") this.#start();
    let active = true;
    return () => {
      if (!active) return;
      active = false;
      this.#listeners.delete(listener);
      if (this.#listeners.size === 0) this.#stop();
    };
  };

  /** Switches a Svelte store to a new shape while retaining its previous rows. @param {V1TableShape} shape */
  setShape(shape) {
    if (shape.key === this.#shape.key) return;
    this.#stop();
    this.#shape = shape;
    this.#publish({ ...this.#state, status: "loading", error: undefined });
    if (this.#listeners.size > 0) this.#start();
  }

  #start() {
    if (this.#subscription !== undefined) return;
    const generation = ++this.#generation;
    /** @param {unknown} error */
    const fail = error => {
      if (generation !== this.#generation) return;
      this.#stop();
      this.#publish({ ...this.#state, status: "error", error });
    };
    try {
      const subscription = this.#shape.subscribe(state => {
        if (generation !== this.#generation) return;
        const data = state.status === "loading" || state.offset === "-1"
          ? this.#state.data : rowsArray(state);
        this.#publish({ data, status: state.status, error: undefined });
      });
      this.#subscription = subscription;
      void subscription.done.catch(fail);
    } catch (error) { fail(error); }
  }

  #stop() {
    ++this.#generation;
    this.#subscription?.close();
    this.#subscription = undefined;
  }

  /** @param {ShapeQueryState} state */
  #publish(state) {
    this.#state = Object.freeze(state);
    for (const listener of this.#listeners) listener();
  }
}

/** @param {V1TableShapeState} state @returns {readonly V1TableRow[]} */
function rowsArray(state) {
  return Object.freeze([...state.rows].sort(([a], [b]) => {
    if (/^-?\d+$/u.test(a) && /^-?\d+$/u.test(b)) {
      const left = BigInt(a), right = BigInt(b);
      return left < right ? -1 : left > right ? 1 : 0;
    }
    return a < b ? -1 : a > b ? 1 : 0;
  }).map(([, row]) => row));
}
