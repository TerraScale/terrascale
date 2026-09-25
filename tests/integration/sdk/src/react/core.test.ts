import assert from "node:assert/strict";
import { describe, test } from "vitest";

import {
  ReactiveMutationObserver,
  ReactiveQueryObserver,
  createReactiveHttpQuery,
  type ReactiveQueryCallbacks,
  type ReactiveHttpEvent,
  type ReactiveQuerySource,
} from "./core.js";

const flush = async (): Promise<void> => {
  await new Promise<void>(resolve => setTimeout(resolve, 0));
};

describe("reactive hook core", () => {
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
