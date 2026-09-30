import assert from "node:assert/strict";
import { describe, test } from "vitest";
import * as React from "react";
import { act, create, type ReactTestRenderer } from "react-test-renderer";
import { useQuery } from "./index.js";

import {
  ReactiveMutationObserver,
  ReactiveQueryObserver,
  createDatabaseQuery,
  createReactiveHttpQuery,
  type ReactiveQueryCallbacks,
  type ReactiveHttpEvent,
  type ReactiveQuerySource,
} from "./core.js";
import { createTerraBaseClient } from "../database.js";
import type { DatabaseCommitReceipt, DatabaseResult } from "../database-types.js";

const nativeTestKey = `tbk1.${"A".repeat(22)}.${"A".repeat(43)}`;
const unavailableDiscovery = async (): Promise<Response> => { throw new Error("Discovery transport is disabled in this lifecycle test."); };
const flush = async (): Promise<void> => {
  await new Promise<void>(resolve => setTimeout(resolve, 0));
};

describe("reactive hook core", () => {
  test("reentrant refetch starts only the replacement generation", () => {
    let starts = 0;
    let stops = 0;
    let reentered = false;
    const observer = new ReactiveQueryObserver<string>({ subscribe: (_callbacks, context) => {
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
    const observer = new ReactiveQueryObserver<string>({ subscribe: callbacks => {
      callbacks.error(new Error("synchronous terminal failure"));
      return () => { stops += 1; };
    } });
    const stop = observer.subscribe(() => undefined);
    assert.equal(observer.getSnapshot().status, "error");
    assert.equal(stops, 1);
    stop();
    assert.equal(stops, 1);
  });

  for (const boundary of ["abort", "cleanup", "notification"] as const) {
    test(`terminal ${boundary} refetch retains replacement ownership and fences late events`, () => {
      const streams: Array<{ readonly callbacks: ReactiveQueryCallbacks<string>; readonly signal: AbortSignal }> = [];
      let refetched = false;
      let stops = 0;
      let observer: ReactiveQueryObserver<string>;
      const refetchOnce = (): void => {
        if (refetched) return;
        refetched = true;
        observer.refetch();
      };
      observer = new ReactiveQueryObserver<string>({ subscribe: (callbacks, context) => {
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
      streams[0]!.callbacks.error(new Error("terminal source failure"));
      assert.equal(streams.length, 2);
      assert.equal(streams[0]!.signal.aborted, true);
      assert.equal(streams[1]!.signal.aborted, false);
      assert.equal(observer.getSnapshot().status, "pending", "obsolete error cannot overwrite replacement state");
      assert.equal(observer.getSnapshot().isFetching, true);
      streams[1]!.callbacks.next("replacement");
      assert.equal(observer.getSnapshot().data, "replacement");
      stop();
      assert.equal(streams[1]!.signal.aborted, true);
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
    const source: ReactiveQuerySource<string> = { subscribe: callbacks => {
      starts += 1;
      callbacks.error(new Error("synchronous terminal failure"));
      return () => { stops += 1; };
    } };
    const View = (): React.ReactElement => React.createElement("span", null, useQuery(source).status);
    let renderer: ReactTestRenderer | undefined;
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
    const observer = new ReactiveQueryObserver<string>({ subscribe: () => { subscriptions += 1; return () => undefined; } }, { enabled: false });
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

  test("native query remains a typed generic observer source and fails closed on discovery transport failure", async () => {
    const uuid = (tail: string): string => `00000000-0000-4000-8000-${tail.padStart(12, "0")}`;
    const client = createTerraBaseClient({ tenant: uuid("1"), database: uuid("2"), shard: 1n, apiKey: nativeTestKey, fetch: unavailableDiscovery });
    const source = createDatabaseQuery(client, { collection: uuid("3"), predicate: { op: "is_missing", path: [1] } });
    const observer = new ReactiveQueryObserver(source);
    let finish!: () => void;
    const terminal = new Promise<void>(resolve => { finish = resolve; });
    const stop = observer.subscribe(() => { if (observer.getSnapshot().status === "error") finish(); });
    await terminal;
    assert.equal(observer.getSnapshot().status, "error");
    assert.equal(observer.getSnapshot().error?.code, "discovery_unavailable");
    assert.equal(observer.getSnapshot().data, undefined);
    stop();
  });

  test("native uncertain mutation failures retain operation identity and never become success", async () => {
    const failure: DatabaseResult<DatabaseCommitReceipt> = { ok: false, error: { code: "transport_unavailable", mutation_outcome: "uncertain", transaction: { transaction_id: { expires_at_unix_ms: 100n, nonce: "11".repeat(16) }, authority: { mode: "leaderless" }, effect_digest: "22".repeat(32) } } };
    const observer = new ReactiveMutationObserver<DatabaseCommitReceipt, void>({ mutationFn: async () => failure });
    await assert.rejects(observer.mutateAsync(undefined), /transport_unavailable/u);
    assert.equal(observer.getSnapshot().status, "error");
    assert.equal(observer.getSnapshot().error?.code, "transport_unavailable");
    assert.equal(observer.getSnapshot().error?.database?.mutation_outcome, "uncertain");
    assert.equal(observer.getSnapshot().error?.database?.transaction, failure.error.transaction);
    observer.dispose();
  });

  test("a native result after cancellation preserves uncertainty without reviving the mutation", async () => {
    let finish!: () => void;
    const ready = new Promise<void>(resolve => { finish = resolve; });
    const failure: DatabaseResult<string> = { ok: false, error: { code: "cancelled", mutation_outcome: "uncertain", transaction: { transaction_id: { expires_at_unix_ms: 100n, nonce: "11".repeat(16) }, authority: { mode: "leaderless" }, effect_digest: "22".repeat(32) } } };
    const observer = new ReactiveMutationObserver<string, void>({ mutationFn: async () => { await ready; return failure; } });
    const pending = observer.mutateAsync(undefined);
    const rejected = assert.rejects(pending, /cancelled/u);
    await Promise.resolve();
    observer.cancel();
    finish();
    await rejected;
    assert.equal(observer.getSnapshot().status, "cancelled");
    assert.equal(observer.getSnapshot().error?.database?.mutation_outcome, "uncertain");
    assert.equal(observer.getSnapshot().error?.database?.transaction, failure.error.transaction);
    observer.dispose();
  });

  test("subscribes only while observed and aborts/disposes on the last observer", () => {
    let abort: AbortSignal | undefined;
    let disposed = 0;
    const source: ReactiveQuerySource<string> = {
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
    let callbacks: { reset: (value: { readonly reason: "generation-changed"; readonly data: string }) => void; error: (value: unknown) => void } | undefined;
    const observer = new ReactiveQueryObserver<string>({
      subscribe: (events) => {
        callbacks = events;
      },
    });
    const unsubscribe = observer.subscribe(() => undefined);
    callbacks!.reset({ reason: "generation-changed", data: "replacement" });
    assert.equal(observer.getSnapshot().status, "reset");
    assert.equal(observer.getSnapshot().reset?.reason, "generation-changed");
    assert.equal(observer.getSnapshot().data, "replacement");
    callbacks!.error(new Error("expired"));
    assert.equal(observer.getSnapshot().status, "error");
    assert.equal(observer.getSnapshot().error?.kind, "subscription");
    assert.equal(observer.getSnapshot().error?.message, "expired");
    unsubscribe();
  });

  test("terminates a failed stream and preserves its final error", () => {
    let emit: ReactiveQueryCallbacks<string> | undefined;
    let signal: AbortSignal | undefined;
    let disposed = 0;
    const observer = new ReactiveQueryObserver<string>({
      subscribe: (callbacks, context) => {
        emit = callbacks;
        signal = context.signal;
        return () => { disposed++; };
      },
    });
    const unsubscribe = observer.subscribe(() => undefined);
    emit!.error(new Error("stream failed"));
    assert.equal(observer.getSnapshot().status, "error");
    assert.equal(observer.getSnapshot().error?.message, "stream failed");
    assert.equal(disposed, 1);
    emit!.next("late");
    emit!.reset({ reason: "generation-changed" });
    assert.equal(observer.getSnapshot().status, "error");
    assert.equal(observer.getSnapshot().data, undefined);
    assert.equal(signal?.aborted, true);
    unsubscribe();
  });

  test("disposes an asynchronously acquired stream after cancellation", async () => {
    let resolveCleanup!: () => void;
    let disposed = 0;
    const observer = new ReactiveQueryObserver<string>({
      subscribe: () => new Promise<() => void>(resolve => {
        resolveCleanup = () => resolve(() => { disposed++; });
      }),
    });
    const unsubscribe = observer.subscribe(() => undefined);
    unsubscribe();
    resolveCleanup();
    await flush();
    assert.equal(disposed, 1);
  });

  test("maps typed HTTP events without owning a route or fetch implementation", () => {
    let emit: ((event: ReactiveHttpEvent<string>) => void) | undefined;
    const query = createReactiveHttpQuery({
      subscribe: (_request, observer, _context) => {
        emit = observer.event;
      },
    }, { query: "from docs" });
    const observer = new ReactiveQueryObserver(query);
    const unsubscribe = observer.subscribe(() => undefined);
    emit!({ kind: "snapshot", value: "first" });
    assert.equal(observer.getSnapshot().data, "first");
    emit!({ kind: "reset", reset: { reason: "retention-advanced" } });
    assert.equal(observer.getSnapshot().reset?.reason, "retention-advanced");
    unsubscribe();
  });

  test("uses one stable idempotency key and rolls back optimistic presentation on rejection", async () => {
    let rejectMutation: (() => void) | undefined;
    let rollback = 0;
    const keys: string[] = [];
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
    rejectMutation!();
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
    const observer = new ReactiveMutationObserver<string, void>({
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

  for (const boundary of ["abort", "rollback", "notification"] as const) {
    test(`mutation ${boundary} restart preserves replacement cancellation ownership`, async () => {
      const signals = new Map<string, AbortSignal>();
      const rollbacks: string[] = [];
      let replacement: Promise<string> | undefined;
      let observer: ReactiveMutationObserver<string, string>;
      const restart = (): void => { replacement ??= observer.mutateAsync("replacement"); };
      observer = new ReactiveMutationObserver<string, string>({
        idempotencyKey: value => value,
        onMutate: value => () => {
          rollbacks.push(value);
          if (boundary === "rollback" && value === "first") restart();
        },
        mutationFn: (value, context) => new Promise<string>((_resolve, reject) => {
          signals.set(value, context.signal);
          context.signal.addEventListener("abort", () => {
            if (boundary === "abort" && value === "first") restart();
            reject(new Error("cancelled"));
          }, { once: true });
        }),
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
      const replacementRejected = assert.rejects(replacement!, /cancelled/u);
      observer.cancel();
      await replacementRejected;
      assert.equal(signals.get("replacement")?.aborted, true);
      assert.deepEqual(rollbacks, ["first", "replacement"]);
      assert.equal(observer.getSnapshot().idempotencyKey, "replacement");
      stop();
    });
  }

  for (const phase of ["presentation", "request"] as const) {
    test(`late cancelled ${phase} completion cannot roll back a replacement mutation`, async () => {
      const rollbacks: string[] = [];
      const writes: string[] = [];
      let finishPresentation!: () => void;
      let finishRequest!: () => void;
      const presentation = new Promise<void>(resolve => { finishPresentation = resolve; });
      const request = new Promise<void>(resolve => { finishRequest = resolve; });
      const observer = new ReactiveMutationObserver<void, string>({
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
