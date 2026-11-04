import assert from "node:assert/strict";
import { describe, test } from "vitest";

import { createQuery } from "./index.js";
/** @import { SvelteQuery } from "./index.js" */
/** @import { ReactiveQuerySource } from "../react/core.js" */

/**
 * @template TValue
 * @param {TValue} value
 * @returns {ReactiveQuerySource<TValue>}
 */
function immediateSource(value) {
  return {
    subscribe: observer => {
      observer.next(value);
      return () => undefined;
    },
  };
}

/**
 * Drives a store exactly like Svelte's `$store` auto-subscription.
 *
 * @template TValue
 * @param {{ readonly subscribe: (run: (value: TValue) => void) => () => void }} store
 * @param {(value: TValue) => void} [onValue]
 * @returns {{
 *   readonly values: TValue[];
 *   readonly current: () => TValue | undefined;
 *   readonly stop: () => void;
 * }}
 */
function bind(store, onValue) {
  /** @type {TValue[]} */
  const values = [];
  const stop = store.subscribe(next => { values.push(next); onValue?.(next); });
  return { values, current: () => values.at(-1), stop };
}

describe("svelte reactive bindings", () => {
  test("createQuery publishes observer snapshots through the store contract", () => {
    /** @type {SvelteQuery<readonly string[]>} */
    const query = createQuery(immediateSource(["one"]));
    const bound = bind(query);
    try {
      assert.equal(bound.values.length >= 1, true);
      assert.equal(bound.current()?.status, "success");
      assert.deepEqual(bound.current()?.data, ["one"]);
    } finally {
      bound.stop();
    }
  });
});
