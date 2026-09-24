import assert from "node:assert/strict";
import { describe, test } from "vitest";

import { createMutation, createQuery, type SvelteQuery } from "./index.js";
import type { ReactiveQuerySource } from "../react/core.js";

function immediateSource<TValue>(value: TValue): ReactiveQuerySource<TValue> {
  return {
    subscribe: observer => {
      observer.next(value);
      return () => undefined;
    },
  };
}

/** Drives a store exactly like Svelte's `$store` auto-subscription. */
function bind<TValue>(store: { readonly subscribe: (run: (value: TValue) => void) => () => void }): {
  readonly values: TValue[];
  readonly current: () => TValue | undefined;
  readonly stop: () => void;
} {
  const values: TValue[] = [];
  const stop = store.subscribe(next => values.push(next));
  return { values, current: () => values.at(-1), stop };
}

describe("svelte reactive bindings", () => {
  test("createQuery publishes observer snapshots through the store contract", () => {
    const query: SvelteQuery<readonly string[]> = createQuery(immediateSource(["one"]));
    const bound = bind(query);
    try {
      assert.equal(bound.values.length >= 1, true);
      assert.equal(bound.current()?.status, "success");
      assert.deepEqual(bound.current()?.data, ["one"]);
    } finally {
      bound.stop();
    }
  });

  test("createQuery follows the subscription lifecycle like a component mount", async () => {
    let subscriptions = 0;
    let deliveries = 0;
    const source: ReactiveQuerySource<number> = {
      subscribe: observer => {
        subscriptions += 1;
        deliveries += 1;
        observer.next(deliveries);
        return () => { subscriptions -= 1; };
      },
    };
    const query = createQuery(source);
    assert.equal(subscriptions, 0, "the transport must not start before a subscriber binds");

    const first = bind(query);
    await Promise.resolve();
    assert.equal(subscriptions, 1);
    first.stop();
    assert.equal(subscriptions, 0, "unmount must dispose the transport");

    const second = bind(query);
    await Promise.resolve();
    assert.equal(second.current()?.data, 2, "a remount restarts delivery on a fresh generation");
    second.stop();
  });

  test("refetch restarts the delivery generation for live subscribers", async () => {
    let generation = 0;
    const source: ReactiveQuerySource<number> = {
      subscribe: observer => {
        generation += 1;
        observer.next(generation);
        return () => undefined;
      },
    };
    const query = createQuery(source);
    const bound = bind(query);
    try {
      await Promise.resolve();
      assert.equal(bound.current()?.data, 1);
      query.refetch();
      await Promise.resolve();
      assert.equal(bound.current()?.data, 2);
    } finally {
      bound.stop();
    }
  });

  test("createMutation exposes the durable mutation state through the store contract", async () => {
    const mutation = createMutation<string, string>({
      mutationFn: async variables => `acked:${variables}`,
    });
    const bound = bind(mutation);
    try {
      assert.equal(bound.current()?.status, "idle");
      const value = await mutation.mutateAsync("write");
      assert.equal(value, "acked:write");
      assert.equal(bound.current()?.status, "success");
      assert.equal(bound.current()?.data, "acked:write");
      assert.equal(typeof bound.current()?.idempotencyKey, "string");
    } finally {
      bound.stop();
    }
  });
});
