import { describe, expect, test } from "vitest";
import { createTerraBaseClient } from "./database.js";
import type { DatabaseCommit } from "./database-types.js";

// SDK-owned cancellation and transport-fault controls only; no native service
// success or provider acceptance is simulated by these responses.
const config = {
  tenant: "00000000-0000-0000-0000-000000000001",
  database: "00000000-0000-0000-0000-000000000002",
  shard: 1n,
  endpoint: "https://gru.api.terrascale.tech",
  apiKey: "fault-control-only",
};
const collection = "00000000-0000-0000-0000-000000000003";
const document = "00000000-0000-0000-0000-000000000004";
const commit: DatabaseCommit = {
  authority: { mode: "leaderless" },
  transaction_id: {
    expires_at_unix_ms: 123456n,
    nonce: "01".repeat(16),
  },
  writes: [{
    collection,
    document,
    schema_generation: 1n,
    schema_digest: "02".repeat(32),
    condition: { kind: "absent" },
    observed: [],
    value: {
      kind: "live",
      fields: [{ id: 1, value: { type: "bytes", value: new Uint8Array([1, 2]) } }],
    },
  }],
};

function deferred<T>(): { readonly promise: Promise<T>; readonly resolve: (value: T) => void } {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(complete => { resolve = complete; });
  return { promise, resolve };
}

function nextTurn(): Promise<void> {
  return new Promise(resolve => { setImmediate(resolve); });
}

async function withoutUnhandledRejections(operation: () => Promise<void>): Promise<void> {
  const reasons: unknown[] = [];
  const onUnhandled = (reason: unknown): void => { reasons.push(reason); };
  process.on("unhandledRejection", onUnhandled);
  try {
    await operation();
    await nextTurn();
    expect(reasons).toEqual([]);
  } finally {
    process.removeListener("unhandledRejection", onUnhandled);
  }
}

describe("native transport cancellation lifecycle", () => {
  test("an aborted mutation disposes a late fetch response exactly once without retry", async () => {
    const controller = new AbortController();
    const started = deferred<void>();
    const delivered = deferred<Response>();
    let calls = 0;
    let cancellations = 0;
    let request: Request | undefined;
    const response = new Response(new ReadableStream<Uint8Array>({
      cancel: () => { cancellations++; },
    }), { status: 503, headers: { "content-type": "application/json" } });
    const client = createTerraBaseClient({
      ...config,
      fetch: input => {
        calls++;
        request = input as Request;
        started.resolve(undefined);
        return delivered.promise;
      },
    });
    const prepared = await client.prepareCommit(commit);
    const pending = client.commit(prepared, { signal: controller.signal });
    await started.promise;
    controller.abort(new Error("Terminal mutation cancellation."));
    expect(await pending).toEqual({
      ok: false,
      error: { code: "cancelled", mutation_outcome: "uncertain", transaction: prepared },
    });
    delivered.resolve(response);
    await nextTurn();
    expect(request?.signal.aborted).toBe(true);
    expect(calls).toBe(1);
    expect(cancellations).toBe(1);
    expect(response.body?.locked).toBe(false);
  });

  test("same-turn fetch abort observes its rejected promise and preserves cancellation", async () => {
    await withoutUnhandledRejections(async () => {
      const controller = new AbortController();
      let calls = 0;
      const client = createTerraBaseClient({
        ...config,
        fetch: () => {
          calls++;
          controller.abort(new Error("Fetch cancelled synchronously."));
          return Promise.reject(new Error("Late fetch rejection."));
        },
      });
      expect(await client.read(collection, document, { signal: controller.signal }))
        .toEqual({ ok: false, error: { code: "cancelled" } });
      expect(calls).toBe(1);
    });
  });

  test("same-turn fetch abort disposes its resolved response even when cleanup rejects", async () => {
    await withoutUnhandledRejections(async () => {
      const controller = new AbortController();
      let cancellations = 0;
      const response = new Response(new ReadableStream<Uint8Array>({
        cancel: () => {
          cancellations++;
          throw new Error("Cleanup rejected.");
        },
      }), { status: 503, headers: { "content-type": "application/json" } });
      const client = createTerraBaseClient({
        ...config,
        fetch: () => {
          controller.abort(new Error("Fetch cancelled synchronously."));
          return Promise.resolve(response);
        },
      });
      expect(await client.read(collection, document, { signal: controller.signal }))
        .toEqual({ ok: false, error: { code: "cancelled" } });
      expect(cancellations).toBe(1);
      expect(response.body?.locked).toBe(false);
    });
  });

  test("abort between fetch fulfillment and request continuation still cancels the body", async () => {
    const controller = new AbortController();
    let cancellations = 0;
    const response = new Response(new ReadableStream<Uint8Array>({
      cancel: () => { cancellations++; },
    }), { status: 503, headers: { "content-type": "application/json" } });
    const client = createTerraBaseClient({
      ...config,
      fetch: () => {
        const fulfilled = Promise.resolve(response);
        void fulfilled.then(() => {
          queueMicrotask(() => { controller.abort(new Error("Continuation cancelled.")); });
        });
        return fulfilled;
      },
    });
    expect(await client.read(collection, document, { signal: controller.signal }))
      .toEqual({ ok: false, error: { code: "cancelled", status: 503 } });
    expect(cancellations).toBe(1);
    expect(response.body?.locked).toBe(false);
  });

  test("same-turn credential abort observes rejection without dispatching", async () => {
    await withoutUnhandledRejections(async () => {
      const controller = new AbortController();
      let calls = 0;
      const client = createTerraBaseClient({
        ...config,
        apiKey: () => {
          controller.abort(new Error("Credential resolution cancelled."));
          return Promise.reject(new Error("Late credential rejection."));
        },
        fetch: () => {
          calls++;
          return Promise.reject(new Error("Dispatch was not permitted."));
        },
      });
      expect(await client.read(collection, document, { signal: controller.signal }))
        .toEqual({ ok: false, error: { code: "cancelled" } });
      expect(calls).toBe(0);
    });
  });

  test("same-turn body abort observes read rejection and releases its reader", async () => {
    await withoutUnhandledRejections(async () => {
      const controller = new AbortController();
      const response = new Response(new ReadableStream<Uint8Array>({
        pull: stream => {
          controller.abort(new Error("Body read cancelled."));
          stream.error(new Error("Late body rejection."));
        },
      }, { highWaterMark: 0 }), { status: 503, headers: { "content-type": "application/json" } });
      const client = createTerraBaseClient({ ...config, fetch: () => Promise.resolve(response) });
      expect(await client.read(collection, document, { signal: controller.signal }))
        .toEqual({ ok: false, error: { code: "cancelled", status: 503 } });
      expect(response.body?.locked).toBe(false);
    });
  });

  test.each(["read", "commit"] as const)("%s cancellation after the final body read wins over a parsed error", async operation => {
    const controller = new AbortController();
    const lifecycle: string[] = [];
    const reason = new DOMException("Cancelled after final body read.", "AbortError");
    let pulls = 0;
    let cancellations = 0;
    let dispatches = 0;
    const response = new Response(new ReadableStream<Uint8Array>({
      pull: stream => {
        pulls++;
        if (pulls === 1) {
          stream.enqueue(new TextEncoder().encode('{"error":{"code":"precondition_failed"}}'));
          lifecycle.push("payload");
          return;
        }
        // With zero buffering this closes the genuine pending final read.
        // The first microtask yields to its cancellation wrapper; the second
        // aborts before the parsed body reaches the request continuation.
        stream.close();
        lifecycle.push("final-read-settled");
        queueMicrotask(() => {
          queueMicrotask(() => {
            lifecycle.push("abort");
            controller.abort(reason);
          });
        });
      },
      cancel: () => { cancellations++; },
    }, { highWaterMark: 0 }), { status: 412, headers: { "content-type": "application/json" } });
    const client = createTerraBaseClient({
      ...config,
      fetch: () => {
        dispatches++;
        return Promise.resolve(response);
      },
    });
    const prepared = operation === "commit" ? await client.prepareCommit(commit) : undefined;
    const result = prepared === undefined
      ? await client.read(collection, document, { signal: controller.signal })
      : await client.commit(prepared, { signal: controller.signal });
    lifecycle.push("result");
    expect(result).toEqual({
      ok: false,
      error: {
        code: "cancelled",
        status: 412,
        ...(prepared === undefined ? {} : { mutation_outcome: "uncertain", transaction: prepared }),
      },
    });
    expect(lifecycle).toEqual(["payload", "final-read-settled", "abort", "result"]);
    expect(controller.signal.reason).toBe(reason);
    expect(reason.name).toBe("AbortError");
    expect(pulls).toBe(2);
    expect(dispatches).toBe(1);
    expect(cancellations).toBe(0);
    expect(response.body?.locked).toBe(false);
  });

  test.each([
    { error: new Error("Transport unavailable."), code: "transport_unavailable" },
    { error: new TypeError("Network connection failed."), code: "transport_unavailable" },
  ])("uncancelled fetch rejection preserves $code without retry", async ({ error, code }) => {
    let calls = 0;
    const client = createTerraBaseClient({
      ...config,
      fetch: () => {
        calls++;
        return Promise.reject(error);
      },
    });
    expect(await client.read(collection, document))
      .toEqual({ ok: false, error: { code } });
    expect(calls).toBe(1);
  });

  test("a rejected non-JSON body is cancelled and cleanup cannot replace its error", async () => {
    await withoutUnhandledRejections(async () => {
      let cancellations = 0;
      const response = new Response(new ReadableStream<Uint8Array>({
        cancel: () => {
          cancellations++;
          throw new Error("Cleanup rejected.");
        },
      }), { status: 503, headers: { "content-type": "text/html" } });
      const client = createTerraBaseClient({ ...config, fetch: () => Promise.resolve(response) });
      expect(await client.read(collection, document))
        .toEqual({ ok: false, error: { code: "invalid_response", status: 503 } });
      expect(cancellations).toBe(1);
      expect(response.body?.locked).toBe(false);
    });
  });

  test.each([false, true])("a streamed error at the 4 MiB boundary (over limit: %s) releases its reader", async overLimit => {
    const maximumBytes = 4 * 1024 * 1024;
    const prefix = new TextEncoder().encode('{"error":{"code":"forbidden_scope"}}');
    let cancellations = 0;
    const response = new Response(new ReadableStream<Uint8Array>({
      start: stream => {
        stream.enqueue(prefix);
        stream.enqueue(new Uint8Array(maximumBytes - prefix.length).fill(0x20));
        if (overLimit) stream.enqueue(new Uint8Array([0x20]));
        else stream.close();
      },
      cancel: () => { cancellations++; },
    }), { status: 403, headers: { "content-type": "application/json" } });
    const client = createTerraBaseClient({ ...config, fetch: () => Promise.resolve(response) });
    expect(await client.read(collection, document)).toEqual({
      ok: false,
      error: { code: overLimit ? "invalid_response" : "forbidden_scope", status: 403 },
    });
    expect(cancellations).toBe(overLimit ? 1 : 0);
    expect(response.body?.locked).toBe(false);
  });
});
