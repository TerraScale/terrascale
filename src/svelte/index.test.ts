import assert from "node:assert/strict";
import { describe, test } from "vitest";
import { get } from "svelte/store";

import { createDatabaseQuery, createMutation, createQuery, type SvelteQuery } from "./index.js";
import type { ReactiveQuerySource } from "../react/core.js";
import { createTerraBaseClient } from "../database.js";

const nativeTestKey = `tbk1.${"A".repeat(22)}.${"A".repeat(43)}`;
const unavailableDiscovery = async (): Promise<Response> => { throw new Error("Discovery transport is disabled in this lifecycle test."); };
function immediateSource<TValue>(value: TValue): ReactiveQuerySource<TValue> {
  return {
    subscribe: observer => {
      observer.next(value);
      return () => undefined;
    },
  };
}

/** Drives a store exactly like Svelte's `$store` auto-subscription. */
function bind<TValue>(store: { readonly subscribe: (run: (value: TValue) => void) => () => void }, onValue?: (value: TValue) => void): {
  readonly values: TValue[];
  readonly current: () => TValue | undefined;
  readonly stop: () => void;
} {
  const values: TValue[] = [];
  const stop = store.subscribe(next => { values.push(next); onValue?.(next); });
  return { values, current: () => values.at(-1), stop };
}

describe("svelte reactive bindings", () => {
  test("actual Svelte get releases the disposer after a synchronous query error", () => {
    let stops = 0;
    const query = createQuery<string>({ subscribe: callbacks => {
      callbacks.error(new Error("synchronous terminal failure"));
      return () => { stops += 1; };
    } });
    assert.equal(get(query).status, "error");
    assert.equal(stops, 1);
    assert.equal(get(query).status, "error");
    assert.equal(stops, 2, "each transient Svelte subscriber owns one cleanup");
  });

  test("unobserved and disabled stores do not start a query during refetch", () => {
    let starts = 0;
    const source: ReactiveQuerySource<string> = { subscribe: observer => { starts += 1; observer.next("value"); return () => undefined; } };
    const inactive = createQuery(source);
    inactive.refetch();
    const disabled = createQuery(source, { enabled: false });
    const stop = disabled.subscribe(() => undefined);
    disabled.refetch();
    assert.equal(starts, 0);
    stop();
  });

  test("native store reports unavailable hosted transport without publishing rows", async () => {
    const uuid = (tail: string): string => `00000000-0000-4000-8000-${tail.padStart(12, "0")}`;
    const client = createTerraBaseClient({ tenant: uuid("1"), database: uuid("2"), shard: 1n, apiKey: nativeTestKey, fetch: unavailableDiscovery });
    const query = createQuery(createDatabaseQuery(client, { collection: uuid("3"), predicate: { op: "is_missing", path: [1] } }));
    let finish!: () => void;
    const terminal = new Promise<void>(resolve => { finish = resolve; });
    const bound = bind(query, state => { if (state.status === "error") finish(); });
    await terminal;
    assert.equal(bound.current()?.status, "error");
    assert.equal(bound.current()?.error?.code, "transport_unavailable");
    assert.equal(bound.current()?.data, undefined);
    bound.stop();
  });

  test("last component unmount aborts a mutation and rolls back only once", async () => {
    let signal: AbortSignal | undefined;
    let rollback = 0;
    const mutation = createMutation<string, void>({
      onMutate: () => () => { rollback += 1; },
      mutationFn: (_variables, context) => {
        signal = context.signal;
        return new Promise<string>((_resolve, reject) => context.signal.addEventListener("abort", () => reject(new Error("cancelled")), { once: true }));
      },
    });
    const first = bind(mutation);
    const second = bind(mutation);
    const pending = mutation.mutateAsync(undefined);
    const rejected = assert.rejects(pending, /cancelled/u);
    await Promise.resolve();
    first.stop();
    assert.equal(signal?.aborted, false);
    second.stop();
    second.stop();
    assert.equal(signal?.aborted, true);
    await rejected;
    assert.equal(rollback, 1);
    const remount = bind(mutation);
    assert.equal(remount.current()?.status, "cancelled");
    remount.stop();
  });

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
