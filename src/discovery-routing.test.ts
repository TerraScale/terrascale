import { afterEach, describe, expect, test, vi } from "vitest";
import { createTerraBaseClient } from "./database.js";
import { TerraBaseDiscoveryRouter } from "./discovery.js";
import type { DatabaseCommit } from "./database-types.js";

// Exact actual receiver bytes; mutations below are deterministic negative/ranking conformance only.
const successBytes = "{\"schema_version\":1,\"scope\":{\"tenant_id\":\"10000000-0000-0000-0000-000000000001\",\"database_id\":\"20000000-0000-0000-0000-000000000001\",\"shard_id\":\"18446744073709551615\"},\"authority\":{\"deployment_id\":\"fixture-deployment\",\"cell\":\"gru\",\"publisher_id\":\"publisher-a\",\"publisher_generation\":\"18446744073709551615\"},\"revision\":\"41\",\"published_at\":1790769600,\"expires_at\":1790769609,\"refreshSeconds\":8,\"residency\":null,\"endpoints\":[{\"region\":\"GRU\",\"url\":\"https://gru.terrascale.tech\",\"catalog\":{\"digest\":\"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\"},\"capabilities\":{\"atomic_groups\":false,\"client_v1\":true,\"conditional_mutation\":false,\"query\":true,\"read\":true,\"security_state\":false,\"subscriptions\":true,\"write\":true},\"security_writer\":null,\"priority\":1}]}";
const forbiddenBytes = "{\"error\":{\"code\":\"discovery_forbidden\",\"message\":\"native discovery admission is unavailable\",\"fix\":\"retry with current authorized native scope metadata\"}}";
const now = 1790769600000;
const scope = { tenant: "10000000-0000-0000-0000-000000000001", database: "20000000-0000-0000-0000-000000000001", shard: 18446744073709551615n };
const collection = "30000000-0000-0000-0000-000000000001";
const document = "40000000-0000-0000-0000-000000000001";
const replica = "50000000-0000-0000-0000-000000000001";
const key = "tbk1." + Buffer.alloc(16, 5).toString("base64url") + "." + Buffer.alloc(32, 6).toString("base64url");
const rotatedKey = "tbk1." + Buffer.alloc(16, 7).toString("base64url") + "." + Buffer.alloc(32, 8).toString("base64url");
const headers = { "content-type": "application/json", "cache-control": "private, no-store" };
const response = (value: unknown, status = 200): Response => new Response(typeof value === "string" ? value : JSON.stringify(value), { status, headers });
const fixture = (): Record<string, any> => JSON.parse(successBytes);
const point = { version: 1, state: "absent", state_version: null, context: [], siblings: [] };
const commit = (): DatabaseCommit => ({
  transaction_id: { expires_at_unix_ms: 1790770200000n, nonce: "01".repeat(16) }, authority: { mode: "leaderless" },
  writes: [{ collection, document, schema_generation: 1n, schema_digest: "cc".repeat(32), condition: { kind: "none" }, observed: [], value: { kind: "live", fields: [] } }],
});
const advanceLease = (value: Record<string, any>, time: number): void => {
  value.published_at = time / 1000; value.expires_at = time / 1000 + 9; value.refreshSeconds = 8;
};

afterEach(() => vi.useRealTimers());

describe("native Atlas routing freshness and observation", () => {
  test("uses receiver priority among operation-eligible regions, independently of array or static region order", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    const value = fixture();
    value.endpoints.push({ ...structuredClone(value.endpoints[0]), region: "FRA", url: "https://fra.terrascale.tech", priority: 1 });
    value.endpoints[0].priority = 2; value.endpoints[1].capabilities.read = false;
    const router = new TerraBaseDiscoveryRouter(scope, async () => response(value));
    expect((await router.resolve(key, ["client_v1", "write"])).region).toBe("FRA");
    expect((await router.resolve(key, ["client_v1", "read"])).region).toBe("GRU");
    expect((await router.resolve(key, ["client_v1", "read", "query"])).region).toBe("GRU");
  });

  test("never rounds a short lease up; expiry requests all four exact observed cursor fields", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    const requests: Request[] = [];
    const value = fixture(); value.refreshSeconds = 1;
    const router = new TerraBaseDiscoveryRouter(scope, async input => { requests.push(input as Request); return response(value); });
    await router.resolve(key, ["read"]);
    vi.setSystemTime(now + 999); await router.resolve(key, ["read"]); expect(requests).toHaveLength(1);
    vi.setSystemTime(now + 1000); await router.resolve(key, ["read"]); expect(requests).toHaveLength(2);
    expect(Object.fromEntries(new URL(requests[1]!.url).searchParams)).toEqual({
      tenant_id: scope.tenant, database_id: scope.database, shard_id: scope.shard.toString(),
      observed_deployment_id: "fixture-deployment", observed_cell: "gru", observed_publisher_generation: "18446744073709551615", rev: "41",
    });
  });

  test("subtracts network delay and never serves beyond absolute expiry", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    let calls = 0;
    const router = new TerraBaseDiscoveryRouter(scope, async () => { calls++; vi.setSystemTime(calls === 1 ? now + 7500 : now + 9000); return response(successBytes); });
    await router.resolve(key, ["read"]); expect(calls).toBe(1);
    vi.setSystemTime(now + 8000);
    await expect(router.resolve(key, ["read"])).rejects.toMatchObject({ code: "invalid_response" });
    expect(calls).toBe(2);
  });

  test("network delay consuming the complete TTL fails without caching", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    const router = new TerraBaseDiscoveryRouter(scope, async () => { vi.setSystemTime(now + 8000); return response(successBytes); });
    await expect(router.resolve(key, ["read"])).rejects.toMatchObject({ code: "routing_expired" });
  });

  test("a backward wall-clock step cannot extend the monotonic deadline", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    const monotonic = vi.spyOn(performance, "now").mockReturnValue(1000);
    let calls = 0;
    try {
      const router = new TerraBaseDiscoveryRouter(scope, async () => { calls++; return response(successBytes); });
      await router.resolve(key, ["read"]);
      vi.setSystemTime(now - 1000); monotonic.mockReturnValue(9000);
      await expect(router.resolve(key, ["read"])).rejects.toMatchObject({ code: "invalid_response" });
      expect(calls).toBe(2);
    } finally { monotonic.mockRestore(); }
  });

  test("a backward wall step within the monotonic lease rejects a publication that is now future", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    const monotonic = vi.spyOn(performance, "now").mockReturnValue(1000);
    let calls = 0;
    try {
      const router = new TerraBaseDiscoveryRouter(scope, async () => { calls++; return response(successBytes); });
      const selected = await router.resolve(key, ["read"]);
      vi.setSystemTime(now - 1); monotonic.mockReturnValue(1500);
      expect(() => router.assertCurrent(selected)).toThrow("routing_expired");
      await expect(router.resolve(key, ["read"])).rejects.toMatchObject({ code: "invalid_response" });
      expect(calls).toBe(2);
      vi.setSystemTime(now);
      expect(() => router.assertCurrent(selected)).not.toThrow();
    } finally { monotonic.mockRestore(); }
  });

  test("credential rotation invalidates the route lease and retains observation floors", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    const requests: Request[] = [];
    const router = new TerraBaseDiscoveryRouter(scope, async input => { requests.push(input as Request); return requests.length === 1 ? response(successBytes) : response(forbiddenBytes, 403); });
    await router.resolve(key, ["read"]);
    await expect(router.resolve(rotatedKey, ["read"])).rejects.toMatchObject({ code: "discovery_forbidden", status: 403 });
    expect(requests[1]!.headers.get("authorization")).toBe("Bearer " + rotatedKey);
    expect(new URL(requests[1]!.url).searchParams.get("rev")).toBe("41");
  });

  test.each(["deployment", "cell", "epoch", "revision"] as const)("rejects changed/regressed %s and retains the previous cursor after failure", async field => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    const initial = fixture(); initial.authority.publisher_generation = "2";
    const changed = structuredClone(initial); advanceLease(changed, now + 8000);
    if (field === "deployment") changed.authority.deployment_id = "another-deployment";
    if (field === "cell") changed.authority.cell = "fra";
    if (field === "epoch") { changed.authority.publisher_generation = "1"; changed.revision = "42"; }
    if (field === "revision") { changed.authority.publisher_generation = "3"; changed.revision = "40"; }
    const requests: Request[] = [];
    const router = new TerraBaseDiscoveryRouter(scope, async input => { requests.push(input as Request); return response(requests.length === 1 ? initial : changed); });
    await router.resolve(key, ["read"]); vi.setSystemTime(now + 8000);
    await expect(router.resolve(key, ["read"])).rejects.toMatchObject({ code: "routing_observation_regressed" });
    await expect(router.resolve(key, ["read"])).rejects.toMatchObject({ code: "routing_observation_regressed" });
    expect(new URL(requests[2]!.url).searchParams.get("observed_publisher_generation")).toBe("2");
    expect(new URL(requests[2]!.url).searchParams.get("rev")).toBe("41");
  });

  test("concurrent late responses cannot overwrite a newer accepted observation", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    let release!: (response: Response) => void;
    let started!: () => void;
    const firstStarted = new Promise<void>(resolve => { started = resolve; });
    let calls = 0;
    const router = new TerraBaseDiscoveryRouter(scope, async () => {
      calls++;
      if (calls === 1) { started(); return new Promise<Response>(resolve => { release = resolve; }); }
      const value = fixture(); value.revision = "42"; return response(value);
    });
    const old = router.resolve(key, ["read"]); await firstStarted;
    await router.resolve(rotatedKey, ["read"]); release(response(successBytes));
    await expect(old).rejects.toMatchObject({ code: "routing_observation_regressed" });
  });

  test("a selected lease is rechecked against a concurrently advanced credential observation before dispatch", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    let calls = 0;
    const router = new TerraBaseDiscoveryRouter(scope, async () => { const value = fixture(); value.revision = String(41 + calls++); return response(value); });
    const old = await router.resolve(key, ["read"]);
    const current = await router.resolve(rotatedKey, ["read"]);
    expect(() => router.assertCurrent(old)).toThrow("routing_observation_regressed");
    expect(() => router.assertCurrent(current)).not.toThrow();
    vi.setSystemTime(now + 8000);
    expect(() => router.assertCurrent(current)).toThrow("routing_expired");
  });

  test("rejects publisher substitution at an unchanged epoch and publication revision", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    const requests: Request[] = [];
    const router = new TerraBaseDiscoveryRouter(scope, async input => {
      requests.push(input as Request);
      const value = fixture();
      if (requests.length > 1) value.authority.publisher_id = "publisher-b";
      return response(value);
    });
    const original = await router.resolve(key, ["read"]);
    await expect(router.resolve(rotatedKey, ["read"])).rejects.toMatchObject({ code: "routing_observation_regressed" });
    await expect(router.resolve(rotatedKey, ["read"])).rejects.toMatchObject({ code: "routing_observation_regressed" });
    expect(() => router.assertCurrent(original)).not.toThrow();
    expect(new URL(requests[2]!.url).searchParams.get("observed_publisher_generation")).toBe("18446744073709551615");
    expect(new URL(requests[2]!.url).searchParams.get("rev")).toBe("41");
    vi.setSystemTime(now + 8000);
    expect(() => router.assertCurrent(original)).toThrow("routing_expired");
  });

  test("accepts a legitimate same-epoch publisher replacement only at a newer publication revision", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    let calls = 0;
    const router = new TerraBaseDiscoveryRouter(scope, async () => {
      const value = fixture();
      if (calls++ > 0) { value.authority.publisher_id = "publisher-b"; value.revision = "42"; }
      return response(value);
    });
    const old = await router.resolve(key, ["read"]);
    const current = await router.resolve(rotatedKey, ["read"]);
    expect(() => router.assertCurrent(old)).toThrow("routing_observation_regressed");
    expect(() => router.assertCurrent(current)).not.toThrow();
  });

  test.each([
    [400, "invalid_cursor"], [503, "discovery_unconfigured"], [503, "no_eligible_region"], [503, "routing_observation_regressed"],
  ])("Atlas failure %s/%s never creates regional/catalog/default eligibility", async (status, code) => {
    let calls = 0;
    const router = new TerraBaseDiscoveryRouter(scope, async () => { calls++; return response({ error: { code, message: "unavailable", fix: "retry current admission" } }, status); });
    await expect(router.resolve(key, ["read"])).rejects.toMatchObject({ code, status });
    expect(calls).toBe(1);
  });

  test("pre-cancellation and a bounded pending Atlas request do no data dispatch", async () => {
    const controller = new AbortController(); controller.abort();
    let calls = 0, release!: (value: Response) => void;
    const eventual = new Promise<Response>(resolve => { release = resolve; });
    const router = new TerraBaseDiscoveryRouter(scope, async () => { calls++; return eventual; }, 10);
    await expect(router.resolve(key, ["read"], controller.signal)).rejects.toBe(controller.signal.reason);
    expect(calls).toBe(0);
    const keepAlive = setTimeout(() => undefined, 100);
    try { await expect(router.resolve(key, ["read"])).rejects.toMatchObject({ code: "discovery_unavailable" }); }
    finally {
      clearTimeout(keepAlive); release(new Response(null));
      await new Promise<void>(resolve => setImmediate(resolve));
    }
    expect(calls).toBe(1);
  });
});

describe("native Atlas response cancellation", () => {
  function deferred<T>(): { promise: Promise<T>; resolve: (value: T) => void; reject: (reason: unknown) => void } {
    let resolve!: (value: T) => void, reject!: (reason: unknown) => void;
    const promise = new Promise<T>((accept, deny) => { resolve = accept; reject = deny; });
    return { promise, resolve, reject };
  }

  const flush = async (): Promise<void> => { await new Promise<void>(resolve => setImmediate(resolve)); };

  test("discards a late body from an abort-ignoring fetch without waiting, retrying, or accepting its observation", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    const started = deferred<void>(), late = deferred<Response>(), controller = new AbortController();
    const requests: Request[] = [];
    const router = new TerraBaseDiscoveryRouter(scope, async input => {
      requests.push(input as Request);
      if (requests.length === 1) { started.resolve(undefined); return late.promise; }
      return response(successBytes);
    });
    const reason = new Error("caller canceled native discovery");
    const pending = router.resolve(key, ["read"], controller.signal);
    await started.promise; controller.abort(reason);
    await expect(pending).rejects.toBe(reason); expect(requests).toHaveLength(1);
    let canceled = 0;
    const value = fixture(); value.revision = "42";
    const unread = new Response(new ReadableStream<Uint8Array>({
      start(stream) { stream.enqueue(new TextEncoder().encode(JSON.stringify(value))); },
      cancel() { canceled++; },
    }), { headers });
    try {
      late.resolve(unread); await flush();
      expect(canceled).toBe(1); expect(unread.bodyUsed).toBe(true);
      expect(requests).toHaveLength(1);
      expect((await router.resolve(key, ["read"])).region).toBe("GRU");
      expect(requests).toHaveLength(2);
      expect(new URL(requests[1]!.url).searchParams.has("rev")).toBe(false);
    } finally {
      late.resolve(unread); await flush();
      if (!unread.bodyUsed) await unread.body?.cancel();
    }
  });

  test("discards an eventual body after the bounded discovery deadline", async () => {
    const started = deferred<void>(), late = deferred<Response>();
    let calls = 0, canceled = 0;
    const router = new TerraBaseDiscoveryRouter(scope, async () => { calls++; started.resolve(undefined); return late.promise; }, 10);
    const pending = router.resolve(key, ["read"]);
    const keepAlive = setTimeout(() => undefined, 100);
    const unread = new Response(new ReadableStream({ cancel() { canceled++; } }), { headers });
    try {
      await started.promise;
      await expect(pending).rejects.toMatchObject({ code: "discovery_unavailable" });
      expect(calls).toBe(1);
      late.resolve(unread); await flush(); expect(canceled).toBe(1);
    } finally {
      clearTimeout(keepAlive); late.resolve(unread); await flush();
      if (!unread.bodyUsed) await unread.body?.cancel();
    }
  });

  test("cleans up when the fetch aborts its caller synchronously before the cancellation race attaches", async () => {
    const controller = new AbortController(), reason = new Error("abort inside custom fetch");
    let calls = 0, canceled = 0;
    const unread = new Response(new ReadableStream({ cancel() { canceled++; } }), { headers });
    const router = new TerraBaseDiscoveryRouter(scope, async () => { calls++; controller.abort(reason); return unread; });
    try {
      await expect(router.resolve(key, ["read"], controller.signal)).rejects.toBe(reason);
      await flush(); expect(calls).toBe(1); expect(canceled).toBe(1);
    } finally { if (!unread.bodyUsed) await unread.body?.cancel(); }
  });

  test("late cancellation failure is handled and does not change the terminal caller result", async () => {
    const started = deferred<void>(), late = deferred<Response>(), controller = new AbortController();
    const reason = new Error("caller cancellation"), cleanupFailure = new Error("custom body cancellation failed");
    let calls = 0, canceled = 0;
    const router = new TerraBaseDiscoveryRouter(scope, async () => { calls++; started.resolve(undefined); return late.promise; });
    const pending = router.resolve(key, ["read"], controller.signal);
    const unread = new Response(new ReadableStream({ cancel() { canceled++; return Promise.reject(cleanupFailure); } }), { headers });
    try {
      await started.promise; controller.abort(reason); await expect(pending).rejects.toBe(reason);
      late.resolve(unread); await flush();
      expect(calls).toBe(1); expect(canceled).toBe(1); await expect(pending).rejects.toBe(reason);
    } finally { late.resolve(unread); await flush(); }
  });

  test.each(["cancel", "body", "bodyUsed"] as const)("synchronous %s cleanup failure cannot escape or change the terminal caller result", async property => {
    const started = deferred<void>(), late = deferred<Response>(), controller = new AbortController();
    const reason = new Error("caller cancellation"), cleanupFailure = new Error("synchronous cleanup failure");
    let calls = 0, attempts = 0;
    const stream = new ReadableStream();
    const unread = new Response(stream, { headers });
    if (property === "cancel") {
      Object.defineProperty(stream, "cancel", { configurable: true, value() { attempts++; throw cleanupFailure; } });
    } else {
      Object.defineProperty(unread, property, { configurable: true, get() { attempts++; throw cleanupFailure; } });
    }
    const router = new TerraBaseDiscoveryRouter(scope, async () => { calls++; started.resolve(undefined); return late.promise; });
    const pending = router.resolve(key, ["read"], controller.signal);
    try {
      await started.promise; controller.abort(reason); await expect(pending).rejects.toBe(reason);
      late.resolve(unread); await flush();
      expect(calls).toBe(1); expect(attempts).toBe(1); await expect(pending).rejects.toBe(reason);
    } finally {
      late.resolve(unread); await flush();
      if (property === "cancel") Reflect.deleteProperty(stream, property);
      else Reflect.deleteProperty(unread, property);
      await ReadableStream.prototype.cancel.call(stream).catch(() => undefined);
    }
  });

  test("late body cleanup is detached from a custom cancel operation that has not completed", async () => {
    const started = deferred<void>(), late = deferred<Response>(), cancelFinished = deferred<void>(), controller = new AbortController();
    const reason = new Error("caller cancellation");
    let canceled = 0;
    const router = new TerraBaseDiscoveryRouter(scope, async () => { started.resolve(undefined); return late.promise; });
    const pending = router.resolve(key, ["read"], controller.signal);
    const unread = new Response(new ReadableStream({ cancel() { canceled++; return cancelFinished.promise; } }), { headers });
    try {
      await started.promise; controller.abort(reason); await expect(pending).rejects.toBe(reason);
      late.resolve(unread); await flush(); expect(canceled).toBe(1);
      await expect(pending).rejects.toBe(reason);
    } finally { late.resolve(unread); cancelFinished.resolve(undefined); await flush(); }
  });

  test("a signal-respecting fetch rejects once without an extra dispatch or unhandled late rejection", async () => {
    const started = deferred<void>(), controller = new AbortController(), reason = new Error("caller cancellation");
    let calls = 0;
    const router = new TerraBaseDiscoveryRouter(scope, async input => {
      calls++; const request = input as Request;
      return new Promise<Response>((_resolve, reject) => {
        request.signal.addEventListener("abort", () => reject(request.signal.reason), { once: true });
        started.resolve(undefined);
      });
    });
    const pending = router.resolve(key, ["read"], controller.signal);
    await started.promise; controller.abort(reason); await expect(pending).rejects.toBe(reason);
    await flush(); expect(calls).toBe(1);
  });

  test("resolved successful bodies are consumed and their accepted lease survives a later caller abort", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    const controller = new AbortController();
    let calls = 0, canceled = 0;
    const consumed = new Response(new ReadableStream<Uint8Array>({
      start(stream) { stream.enqueue(new TextEncoder().encode(successBytes)); stream.close(); },
      cancel() { canceled++; },
    }), { headers });
    const router = new TerraBaseDiscoveryRouter(scope, async () => { calls++; return consumed; });
    const endpoint = await router.resolve(key, ["read"], controller.signal);
    controller.abort(); await flush();
    expect(canceled).toBe(0); expect(consumed.bodyUsed).toBe(true);
    expect(() => router.assertCurrent(endpoint)).not.toThrow();
    expect(await router.resolve(key, ["read"])).toBe(endpoint); expect(calls).toBe(1);
  });

  test("late aborted observations preserve the original accepted lease and durable observation floor", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    const started = deferred<void>(), late = deferred<Response>(), controller = new AbortController();
    const requests: Request[] = [];
    const router = new TerraBaseDiscoveryRouter(scope, async input => {
      requests.push(input as Request);
      if (requests.length === 2) { started.resolve(undefined); return late.promise; }
      return response(successBytes);
    });
    const original = await router.resolve(key, ["read"]);
    const pending = router.resolve(rotatedKey, ["read"], controller.signal);
    const reason = new Error("caller cancellation");
    const unread = response(JSON.stringify({ ...fixture(), revision: "42" }));
    try {
      await started.promise; controller.abort(reason); await expect(pending).rejects.toBe(reason);
      late.resolve(unread); await flush();
      expect(unread.bodyUsed).toBe(true); expect(requests).toHaveLength(2);
      expect(() => router.assertCurrent(original)).not.toThrow();
      await router.resolve(key, ["read"]);
      expect(requests).toHaveLength(3); expect(new URL(requests[2]!.url).searchParams.get("rev")).toBe("41");
    } finally {
      late.resolve(unread); await flush();
      if (!unread.bodyUsed) await unread.body?.cancel();
    }
  });
});

describe("native client consumes actual Atlas admission", () => {
  test.each(["read", "query", "subscription", "commit", "status"] as const)("public %s preserves the actual Atlas 403 without data dispatch", async operation => {
    const requests: Request[] = [];
    const client = createTerraBaseClient({ ...scope, apiKey: key, fetch: async input => {
      requests.push(input as Request);
      return response(forbiddenBytes, 403);
    } });
    const prepared = await client.prepareCommit(commit());
    const result = operation === "read" ? await client.read(collection, document)
      : operation === "query" ? await client.query(collection, { predicate: { op: "is_missing", path: [1] }, limit: 1 })
      : operation === "subscription" ? await client.subscription(collection, { phase: "snapshot", predicate: { op: "is_missing", path: [1] }, limit: 1 })
      : operation === "commit" ? await client.commit(prepared)
      : await client.transactionStatus(prepared);
    expect(result).toMatchObject({ ok: false, error: { code: "discovery_forbidden", status: 403 } });
    if (operation === "commit") expect(result).toMatchObject({ error: { transaction: prepared, mutation_outcome: "unknown" } });
    expect(requests.map(request => new URL(request.url).hostname)).toEqual(["atlas.terrascale.tech"]);
  });

  test.each(["network", "not-found", "unconfigured", "legacy"] as const)("public read fails closed on discovery %s", async fault => {
    const requests: Request[] = [];
    const client = createTerraBaseClient({ ...scope, apiKey: key, fetch: async input => {
      requests.push(input as Request);
      if (fault === "network") throw new Error("Atlas unavailable");
      if (fault === "not-found") return new Response(null, { status: 404 });
      if (fault === "unconfigured") return response({ error: { code: "discovery_unconfigured", message: "native discovery admission is unavailable", fix: "retry after native discovery is configured" } }, 503);
      return response({ version: 1, database: "old", endpoints: [] });
    } });
    const code = fault === "network" ? "discovery_unavailable" : fault === "unconfigured" ? "discovery_unconfigured" : "invalid_response";
    expect(await client.read(collection, document)).toMatchObject({ ok: false, error: { code } });
    expect(requests.map(request => new URL(request.url).hostname)).toEqual(["atlas.terrascale.tech"]);
  });

  test.each(["read", "commit"] as const)("public %s discovery timeout settles without data dispatch", async operation => {
    const requests: Request[] = [];
    const client = createTerraBaseClient({ ...scope, apiKey: key, discoveryTimeoutMs: 10, requestTimeoutMs: 500, fetch: async input => {
      const request = input as Request;
      requests.push(request);
      return new Promise<Response>((_resolve, reject) => {
        const abort = (): void => reject(request.signal.reason);
        request.signal.addEventListener("abort", abort, { once: true });
        if (request.signal.aborted) abort();
      });
    } });
    const prepared = await client.prepareCommit(commit());
    const keepAlive = setTimeout(() => undefined, 1000);
    try {
      const result = operation === "read" ? await client.read(collection, document) : await client.commit(prepared);
      expect(result).toMatchObject({ ok: false, error: { code: "discovery_unavailable" } });
      if (operation === "commit") expect(result).toMatchObject({ error: { transaction: prepared, mutation_outcome: "unknown" } });
      expect(requests.map(request => new URL(request.url).hostname)).toEqual(["atlas.terrascale.tech"]);
      expect(requests[0]!.signal.aborted).toBe(true);
    } finally { clearTimeout(keepAlive); }
  });

  test("public reads and writes choose independently eligible endpoints from one admitted lease", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    const value = fixture();
    value.endpoints[0].priority = 2;
    value.endpoints.push({ ...structuredClone(value.endpoints[0]), region: "FRA", url: "https://fra.terrascale.tech", priority: 1 });
    value.endpoints[1].capabilities.read = false;
    const requests: Request[] = [];
    const client = createTerraBaseClient({ ...scope, apiKey: key, fetch: async input => {
      const request = input as Request;
      requests.push(request);
      if (new URL(request.url).hostname === "atlas.terrascale.tech") return response(value);
      if (request.url.endsWith("/transactions/status")) return response({ ...JSON.parse(await request.text()), state: "unknown", outcome: null });
      if (request.url.endsWith("/transactions")) return response({ error: { code: "commit_uncertain" } }, 503);
      return response(point);
    } });
    const prepared = await client.prepareCommit(commit());
    expect((await client.read(collection, document)).ok).toBe(true);
    expect(await client.transactionStatus(prepared)).toMatchObject({ ok: true, value: { state: "unknown", outcome: null } });
    expect(await client.commit(prepared)).toMatchObject({ ok: false, error: { code: "commit_uncertain", transaction: prepared, mutation_outcome: "uncertain" } });
    expect(requests.map(request => new URL(request.url).hostname)).toEqual(["atlas.terrascale.tech", "gru.terrascale.tech", "fra.terrascale.tech", "fra.terrascale.tech"]);
  });

  test("first discovers the exact private tuple then connects directly to the admitted native path", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    const requests: Request[] = [];
    const client = createTerraBaseClient({ ...scope, apiKey: key, fetch: async input => {
      const request = input as Request; requests.push(request);
      return response(new URL(request.url).hostname === "atlas.terrascale.tech" ? successBytes : point);
    } });
    expect((await client.read(collection, document)).ok).toBe(true);
    expect(requests).toHaveLength(2);
    expect(requests[1]!.url).toBe("https://gru.terrascale.tech/v1/tenants/" + scope.tenant + "/databases/" + scope.database + "/shards/18446744073709551615/collections/" + collection + "/documents/" + document);
    expect(requests[1]!.credentials).toBe("omit"); expect(requests[1]!.redirect).toBe("manual");
    expect([...requests[1]!.headers]).toEqual([["accept", "application/json"], ["authorization", "Bearer " + key]]);
  });

  test("captures one native credential across discovery and data, independently of hosted identity credentials", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    let providers = 0;
    const requests: Request[] = [];
    const config = { ...scope, accessToken: "hosted-identity-session", apiKey: () => { providers++; return providers === 1 ? key : rotatedKey; }, fetch: async (input: RequestInfo | URL) => {
      const request = input as Request; requests.push(request);
      return response(new URL(request.url).hostname === "atlas.terrascale.tech" ? successBytes : point);
    } };
    expect((await createTerraBaseClient(config).read(collection, document)).ok).toBe(true);
    expect(providers).toBe(1);
    for (const request of requests) expect(request.headers.get("authorization")).toBe("Bearer " + key);
  });

  test.each(["absent", "version", "multiple", "single_writer"] as const)("blocks %s mutation before native dispatch and retains an unresolved immutable identity", async kind => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    const requests: Request[] = [];
    const client = createTerraBaseClient({ ...scope, apiKey: key, fetch: async input => { requests.push(input as Request); return response(successBytes); } });
    const value = commit();
    const selected: DatabaseCommit = kind === "multiple" ? { ...value, writes: [value.writes[0]!, { ...value.writes[0]!, document: replica }] }
      : kind === "single_writer" ? { ...value, authority: { mode: "single_writer", generation: 1n, writer: { replica, incarnation: "03".repeat(16) } }, writes: [{ ...value.writes[0]!, condition: { kind: "absent" } }] }
      : { ...value, writes: [{ ...value.writes[0]!, condition: kind === "absent" ? { kind: "absent" } : { kind: "version", version: "04".repeat(32) } }] };
    const prepared = await client.prepareCommit(selected);
    const result = await client.commit(prepared);
    expect(result).toEqual({ ok: false, error: { code: "capability_unavailable", transaction: prepared, mutation_outcome: "unknown" } });
    expect(requests).toHaveLength(1); expect(new URL(requests[0]!.url).hostname).toBe("atlas.terrascale.tech");
  });

  test("retained advanced leaderless status requires write and does not require downshifted conditional/group caps", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    const value = commit();
    const client = createTerraBaseClient({ ...scope, apiKey: key, fetch: async input => {
      const request = input as Request;
      if (new URL(request.url).hostname === "atlas.terrascale.tech") return response(successBytes);
      return response({ ...JSON.parse(await request.text()), state: "unknown", outcome: null });
    } });
    const prepared = await client.prepareCommit({ ...value, writes: [{ ...value.writes[0]!, condition: { kind: "absent" } }, { ...value.writes[0]!, document: replica, condition: { kind: "version", version: "04".repeat(32) } }] });
    const result = await client.transactionStatus(prepared);
    expect(result.ok && result.value.state).toBe("unknown");
    expect(result.ok && result.value.outcome).toBeNull();
  });

  test("read-only native admission cannot resolve write receipts or dispatch an unconditional write", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    const value = fixture(); value.endpoints[0].capabilities.write = false;
    let calls = 0;
    const client = createTerraBaseClient({ ...scope, apiKey: key, fetch: async () => { calls++; return response(value); } });
    const prepared = await client.prepareCommit(commit());
    expect(await client.commit(prepared)).toMatchObject({ ok: false, error: { code: "capability_unavailable", mutation_outcome: "unknown" } });
    expect(await client.transactionStatus(prepared)).toMatchObject({ ok: false, error: { code: "capability_unavailable" } });
    expect(calls).toBe(1);
  });

  test("single-writer status cannot select a nearest substitute from null writer admission", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    let calls = 0;
    const client = createTerraBaseClient({ ...scope, apiKey: key, fetch: async () => { calls++; return response(successBytes); } });
    const identity = { transaction_id: commit().transaction_id, effect_digest: "04".repeat(32), authority: { mode: "single_writer" as const, generation: 1n, writer: { replica, incarnation: "03".repeat(16) } } };
    expect(await client.transactionStatus(identity)).toMatchObject({ ok: false, error: { code: "capability_unavailable" } });
    expect(calls).toBe(1);
  });

  test("read subscriptions can project committed groups while atomic_groups remains false", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    const client = createTerraBaseClient({ ...scope, apiKey: key, fetch: async input => {
      const request = input as Request;
      return response(new URL(request.url).hostname === "atlas.terrascale.tech" ? successBytes : { version: 1, phase: "poll", groups: [{ sequence: "1", effects: [{ document, point: null }, { document: replica, point: null }] }], poll_cursor: "b3BhcXVl" });
    } });
    const result = await client.subscription(collection, { phase: "poll", predicate: { op: "is_missing", path: [1] }, poll_cursor: "b3BhcXVl" });
    expect(result.ok && result.value.phase === "poll" && result.value.groups[0]?.effects).toHaveLength(2);
  });

  test("query and subscription capability denial blocks each named data request", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    const value = fixture(); value.endpoints[0].capabilities.query = false; value.endpoints[0].capabilities.subscriptions = false;
    let calls = 0;
    const client = createTerraBaseClient({ ...scope, apiKey: key, fetch: async () => { calls++; return response(value); } });
    expect(await client.query(collection, { predicate: { op: "is_missing", path: [1] }, limit: 1 })).toMatchObject({ ok: false, error: { code: "capability_unavailable" } });
    expect(await client.subscription(collection, { phase: "snapshot", predicate: { op: "is_missing", path: [1] }, limit: 1 })).toMatchObject({ ok: false, error: { code: "capability_unavailable" } });
    expect(calls).toBe(1);
  });

  test("unconditional immutable retries use identical bytes and preserve lost-response uncertainty", async () => {
    vi.useFakeTimers({ toFake: ["Date"] }); vi.setSystemTime(now);
    const bodies: string[] = [];
    const client = createTerraBaseClient({ ...scope, apiKey: key, fetch: async input => {
      const request = input as Request;
      if (new URL(request.url).hostname === "atlas.terrascale.tech") return response(successBytes);
      bodies.push(await request.text()); throw new Error("native response lost");
    } });
    const prepared = await client.prepareCommit(commit());
    for (let i = 0; i < 2; i++) expect(await client.commit(prepared)).toEqual({ ok: false, error: { code: "transport_unavailable", transaction: prepared, mutation_outcome: "uncertain" } });
    expect(bodies).toHaveLength(2); expect(bodies[0]).toBe(bodies[1]);
  });
});
