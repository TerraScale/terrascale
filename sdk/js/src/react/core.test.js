import assert from "node:assert/strict";
import { describe, test } from "vitest";

import { ReactiveMutationObserver, ReactiveQueryObserver } from "./core.js";
/** @import { ReactiveQuerySource } from "./core.js" */

/** @returns {Promise<void>} */
const flush = async () => {
  await new Promise(resolve => setTimeout(resolve, 0));
};

describe("reactive hook core", () => {
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
});
