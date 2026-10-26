import assert from "node:assert/strict";
import { describe, test } from "vitest";
import * as React from "react";
import { act, create } from "react-test-renderer";
import { useQuery } from "./index.js";

import {
  ReactiveMutationObserver,
  ReactiveQueryObserver,
  createReactiveHttpQuery,
} from "./core.js";
/** @import { ReactTestRenderer } from "react-test-renderer" */
/** @import { ReactiveQueryCallbacks, ReactiveHttpEvent, ReactiveQuerySource } from "./core.js" */

/** @returns {Promise<void>} */
const flush = async () => {
  await new Promise(resolve => setTimeout(resolve, 0));
};

describe("reactive hook core", () => {
  test("reentrant refetch starts only the replacement generation", () => {
    let starts = 0;
    let stops = 0;
    let reentered = false;
    /** @type {ReactiveQueryObserver<string>} */
    const observer = new ReactiveQueryObserver({ subscribe: (_callbacks, context) => {
      assert.equal(context.signal.aborted, false);
      starts += 1;
      return () => { stops += 1; };
    } });
    const stop = observer.subscribe(() => {
      if (!reentered && observer.getSnapshot().isFetching) {
        reentered = true;
        observer.refetch();
      }
    });
    assert.equal(starts, 1);
    stop();
    assert.equal(stops, starts);
  });

  test("synchronous terminal errors release a subsequently returned disposer", () => {
    let stops = 0;
    /** @type {ReactiveQueryObserver<string>} */
    const observer = new ReactiveQueryObserver({ subscribe: callbacks => {
      callbacks.error(new Error("synchronous terminal failure"));
      return () => { stops += 1; };
    } });
    const stop = observer.subscribe(() => undefined);
    assert.equal(observer.getSnapshot().status, "error");
    assert.equal(stops, 1);
    stop();
    assert.equal(stops, 1);
  });

  for (const boundary of /** @type {const} */ (["abort", "cleanup", "notification"])) {
    test(`terminal ${boundary} refetch retains replacement ownership and fences late events`, () => {
      /** @type {Array<{ readonly callbacks: ReactiveQueryCallbacks<string>; readonly signal: AbortSignal }>} */
      const streams = [];
      let refetched = false;
      let stops = 0;
      /** @type {ReactiveQueryObserver<string>} */
      let observer;
      /** @returns {void} */
      const refetchOnce = () => {
        if (refetched) return;
        refetched = true;
        observer.refetch();
      };
      observer = new ReactiveQueryObserver({ subscribe: (callbacks, context) => {
        streams.push({ callbacks, signal: context.signal });
        if (boundary === "abort") {
          context.signal.addEventListener("abort", refetchOnce, { once: true });
          return; // This source releases its work through AbortSignal only.
        }
        return () => {
          stops += 1;
          if (boundary === "cleanup") refetchOnce();
        };
      } });
      const stop = observer.subscribe(() => {
        if (boundary === "notification" && observer.getSnapshot().status === "error") refetchOnce();
      });
      const original = streams[0];
      assert.ok(original);
      original.callbacks.error(new Error("terminal source failure"));
      assert.equal(streams.length, 2);
      const replacement = streams[1];
      assert.ok(replacement);
      assert.equal(original.signal.aborted, true);
      assert.equal(replacement.signal.aborted, false);
      assert.equal(observer.getSnapshot().status, "pending", "obsolete error cannot overwrite replacement state");
      assert.equal(observer.getSnapshot().isFetching, true);
      replacement.callbacks.next("replacement");
      assert.equal(observer.getSnapshot().data, "replacement");
      stop();
      assert.equal(replacement.signal.aborted, true);
      assert.equal(stops, boundary === "abort" ? 0 : 2);
      const final = observer.getSnapshot();
      for (const stream of streams) {
        stream.callbacks.next("late");
        stream.callbacks.reset({ reason: "generation-changed", data: "late reset" });
        stream.callbacks.error(new Error("late error"));
      }
      assert.equal(observer.getSnapshot(), final);
      assert.equal(streams.length, 2);
    });
  }

  test("actual React Strict Mode releases every synchronously failed query", async () => {
    Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true });
    let starts = 0;
    let stops = 0;
    /** @type {ReactiveQuerySource<string>} */
    const source = { subscribe: callbacks => {
      starts += 1;
      callbacks.error(new Error("synchronous terminal failure"));
      return () => { stops += 1; };
    } };
    /** @returns {React.ReactElement} */
    const View = () => React.createElement("span", null, useQuery(source).status);
    /** @type {ReactTestRenderer | undefined} */
    let renderer;
    try {
      await act(async () => { renderer = create(React.createElement(React.StrictMode, null, React.createElement(View))); });
      assert.deepEqual(renderer?.toJSON(), { type: "span", props: {}, children: ["error"] });
      assert.equal(starts, 2, "Strict Mode must replay the actual subscription");
      assert.equal(stops, starts);
    } finally {
      await act(async () => { renderer?.unmount(); });
    }
    assert.equal(stops, starts);
  });

  test("refetch respects disabled and unobserved query lifecycles", () => {
    let subscriptions = 0;
    /** @type {ReactiveQueryObserver<string>} */
    const observer = new ReactiveQueryObserver({ subscribe: () => { subscriptions += 1; return () => undefined; } }, { enabled: false });
    observer.refetch();
    const stop = observer.subscribe(() => undefined);
    observer.refetch();
    assert.equal(subscriptions, 0);
    observer.updateOptions({ enabled: true });
    assert.equal(subscriptions, 1);
    observer.refetch();
    assert.equal(subscriptions, 2);
    stop();
    observer.refetch();
    assert.equal(subscriptions, 2);
  });

  test("subscribes only while observed and aborts/disposes on the last observer", () => {
    /** @type {AbortSignal | undefined} */
    let abort;
    let disposed = 0;
    /** @type {ReactiveQuerySource<string>} */
    const source = {
      subscribe: (_observer, context) => {
        abort = context.signal;
        return () => { disposed++; };
      },
    };
    const observer = new ReactiveQueryObserver(source);
    const unsubscribe = observer.subscribe(() => undefined);
    assert.equal(abort?.aborted, false);
    unsubscribe();
    assert.equal(abort?.aborted, true);
    assert.equal(disposed, 1);
  });

  test("keeps reset reasons and typed errors in distinct state fields", () => {
    /** @type {{ reset: (value: { readonly reason: "generation-changed"; readonly data: string }) => void; error: (value: unknown) => void } | undefined} */
    let callbacks;
    /** @type {ReactiveQueryObserver<string>} */
    const observer = new ReactiveQueryObserver({
      subscribe: (events) => {
        callbacks = events;
      },
    });
    const unsubscribe = observer.subscribe(() => undefined);
    assert.ok(callbacks);
    callbacks.reset({ reason: "generation-changed", data: "replacement" });
    assert.equal(observer.getSnapshot().status, "reset");
    assert.equal(observer.getSnapshot().reset?.reason, "generation-changed");
    assert.equal(observer.getSnapshot().data, "replacement");
    callbacks.error(new Error("expired"));
    assert.equal(observer.getSnapshot().status, "error");
    assert.equal(observer.getSnapshot().error?.kind, "subscription");
    assert.equal(observer.getSnapshot().error?.message, "expired");
    unsubscribe();
  });

  test("terminates a failed stream and preserves its final error", () => {
    /** @type {ReactiveQueryCallbacks<string> | undefined} */
    let emit;
    /** @type {AbortSignal | undefined} */
    let signal;
    let disposed = 0;
    /** @type {ReactiveQueryObserver<string>} */
    const observer = new ReactiveQueryObserver({
      subscribe: (callbacks, context) => {
        emit = callbacks;
        signal = context.signal;
        return () => { disposed++; };
      },
    });
    const unsubscribe = observer.subscribe(() => undefined);
    assert.ok(emit);
    emit.error(new Error("stream failed"));
    assert.equal(observer.getSnapshot().status, "error");
    assert.equal(observer.getSnapshot().error?.message, "stream failed");
    assert.equal(disposed, 1);
    emit.next("late");
    emit.reset({ reason: "generation-changed" });
    assert.equal(observer.getSnapshot().status, "error");
    assert.equal(observer.getSnapshot().data, undefined);
    assert.equal(signal?.aborted, true);
    unsubscribe();
  });

  test("disposes an asynchronously acquired stream after cancellation", async () => {
    /** @type {() => void} */
    let resolveCleanup = () => undefined;
    let disposed = 0;
    /** @type {ReactiveQueryObserver<string>} */
    const observer = new ReactiveQueryObserver({
      subscribe: () => /** @type {Promise<() => void>} */ (new Promise(resolve => {
        resolveCleanup = () => resolve(() => { disposed++; });
      })),
    });
    const unsubscribe = observer.subscribe(() => undefined);
    unsubscribe();
    resolveCleanup();
    await flush();
    assert.equal(disposed, 1);
  });

  test("maps typed HTTP events without owning a route or fetch implementation", () => {
    /** @type {((event: ReactiveHttpEvent<string>) => void) | undefined} */
    let emit;
    const query = createReactiveHttpQuery({
      subscribe: (_request, observer, _context) => {
        emit = observer.event;
      },
    }, { query: "from docs" });
    const observer = new ReactiveQueryObserver(query);
    const unsubscribe = observer.subscribe(() => undefined);
    assert.ok(emit);
    emit({ kind: "snapshot", value: "first" });
    assert.equal(observer.getSnapshot().data, "first");
    emit({ kind: "reset", reset: { reason: "retention-advanced" } });
    assert.equal(observer.getSnapshot().reset?.reason, "retention-advanced");
    unsubscribe();
  });

  test("uses one stable idempotency key and rolls back optimistic presentation on rejection", async () => {
    /** @type {(() => void) | undefined} */
    let rejectMutation;
    let rollback = 0;
    /** @type {string[]} */
    const keys = [];
    const observer = new ReactiveMutationObserver({
      idempotencyKey: "stable-key",
      onMutate: (_variables, context) => {
        keys.push(context.idempotencyKey);
        return () => { rollback++; };
      },
      mutationFn: (_variables, context) => new Promise((_resolve, reject) => {
        context.signal.addEventListener("abort", () => reject(new Error("aborted")), { once: true });
        rejectMutation = () => reject(new Error("denied"));
      }),
    });
    const pending = observer.mutateAsync({ value: "new" });
    await flush();
    assert.ok(rejectMutation);
    rejectMutation();
    await assert.rejects(pending, /denied/u);
    assert.deepEqual(keys, ["stable-key"]);
    assert.equal(rollback, 1);
    assert.equal(observer.getSnapshot().status, "error");
  });

  test("cancellation aborts durable work and invokes rollback", async () => {
    let rollback = 0;
    const observer = new ReactiveMutationObserver({
      idempotencyKey: "cancel-key",
      onMutate: () => () => { rollback++; },
      mutationFn: (_variables, context) => new Promise((_resolve, reject) => {
        context.signal.addEventListener("abort", () => reject(new Error("aborted")), { once: true });
      }),
    });
    const pending = observer.mutateAsync(undefined);
    await flush();
    observer.cancel();
    await assert.rejects(pending, /aborted|cancelled/u);
    assert.equal(rollback, 1);
    assert.equal(observer.getSnapshot().status, "cancelled");
  });

  test("pending notification cancellation fences presentation and request work", async () => {
    let presentations = 0;
    let requests = 0;
    /** @type {ReactiveMutationObserver<string, void>} */
    const observer = new ReactiveMutationObserver({
      idempotencyKey: "cancel-before-presentation",
      onMutate: () => { presentations += 1; },
      mutationFn: async () => { requests += 1; return "unexpected"; },
    });
    const stop = observer.subscribe(() => { if (observer.getSnapshot().isPending) observer.cancel(); });
    await assert.rejects(observer.mutateAsync(undefined), /cancelled/u);
    assert.equal(presentations, 0);
    assert.equal(requests, 0);
    assert.equal(observer.getSnapshot().idempotencyKey, "cancel-before-presentation");
    stop();
  });

  for (const boundary of /** @type {const} */ (["abort", "rollback", "notification"])) {
    test(`mutation ${boundary} restart preserves replacement cancellation ownership`, async () => {
      /** @type {Map<string, AbortSignal>} */
      const signals = new Map();
      /** @type {string[]} */
      const rollbacks = [];
      /** @type {Promise<string> | undefined} */
      let replacement;
      /** @type {ReactiveMutationObserver<string, string>} */
      let observer;
      /** @returns {void} */
      const restart = () => { replacement ??= observer.mutateAsync("replacement"); };
      observer = new ReactiveMutationObserver({
        idempotencyKey: value => value,
        onMutate: value => () => {
          rollbacks.push(value);
          if (boundary === "rollback" && value === "first") restart();
        },
        mutationFn: (value, context) => /** @type {Promise<string>} */ (new Promise((_resolve, reject) => {
          signals.set(value, context.signal);
          context.signal.addEventListener("abort", () => {
            if (boundary === "abort" && value === "first") restart();
            reject(new Error("cancelled"));
          }, { once: true });
        })),
      });
      const stop = observer.subscribe(() => {
        if (boundary === "notification" && observer.getSnapshot().status === "cancelled" && observer.getSnapshot().idempotencyKey === "first") restart();
      });
      const first = observer.mutateAsync("first");
      const firstRejected = assert.rejects(first, /cancelled/u);
      await flush();
      observer.reset();
      await firstRejected;
      await flush();
      assert.equal(observer.getSnapshot().status, "pending");
      assert.equal(observer.getSnapshot().idempotencyKey, "replacement");
      assert.equal(signals.get("first")?.aborted, true);
      assert.equal(signals.get("replacement")?.aborted, false);
      assert.ok(replacement);
      const replacementRejected = assert.rejects(replacement, /cancelled/u);
      observer.cancel();
      await replacementRejected;
      assert.equal(signals.get("replacement")?.aborted, true);
      assert.deepEqual(rollbacks, ["first", "replacement"]);
      assert.equal(observer.getSnapshot().idempotencyKey, "replacement");
      stop();
    });
  }

  for (const phase of /** @type {const} */ (["presentation", "request"])) {
    test(`late cancelled ${phase} completion cannot roll back a replacement mutation`, async () => {
      /** @type {string[]} */
      const rollbacks = [];
      /** @type {string[]} */
      const writes = [];
      /** @type {() => void} */
      let finishPresentation = () => undefined;
      /** @type {() => void} */
      let finishRequest = () => undefined;
      /** @type {Promise<void>} */
      const presentation = new Promise(resolve => { finishPresentation = resolve; });
      /** @type {Promise<void>} */
      const request = new Promise(resolve => { finishRequest = resolve; });
      /** @type {ReactiveMutationObserver<void, string>} */
      const observer = new ReactiveMutationObserver({
        idempotencyKey: value => value,
        onMutate: async value => {
          if (value === "first" && phase === "presentation") await presentation;
          return () => { rollbacks.push(value); };
        },
        mutationFn: async value => {
          writes.push(value);
          await request;
        },
      });
      const first = observer.mutateAsync("first");
      const firstRejected = assert.rejects(first, /cancelled/u);
      await flush();
      observer.cancel();
      const second = observer.mutateAsync("second");
      await flush();
      finishPresentation();
      if (phase === "request") finishRequest();
      await firstRejected;
      if (phase === "presentation") {
        assert.equal(observer.getSnapshot().status, "pending");
        assert.deepEqual(writes, ["second"]);
        observer.cancel();
        finishRequest();
        await assert.rejects(second, /cancelled/u);
        assert.deepEqual(rollbacks, ["first", "second"]);
      } else {
        await second;
        assert.deepEqual(rollbacks, ["first"]);
        assert.equal(observer.getSnapshot().status, "success");
      }
    });
  }

});
