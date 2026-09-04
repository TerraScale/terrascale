import assert from "node:assert/strict";
import { describe, test } from "vitest";

import {
  ReactiveMutationObserver,
  ReactiveQueryObserver,
  createReactiveHttpQuery,
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
});
