import { afterEach, describe, expect, test, vi } from "vitest";
import { createTerraBaseClient } from "./database.js";
import type { DatabaseCommit } from "./database-types.js";

// Deterministic request/decoder conformance only. Real service acceptance has a separate runner.
const tenant = "00000000-0000-0000-0000-000000000001";
const database = "00000000-0000-0000-0000-000000000002";
const collection = "00000000-0000-0000-0000-000000000003";
const document = "00000000-0000-0000-0000-000000000004";
const replica = "00000000-0000-0000-0000-000000000005";
const scope = { tenant, database, shard: (1n << 64n) - 1n };
const point = { version: 1, state: "absent", state_version: null, context: [], siblings: [] };
const json = (value: unknown, status = 200): Response => new Response(JSON.stringify(value), { status, headers: { "content-type": "application/json" } });
const commit: DatabaseCommit = {
  authority: { mode: "leaderless" }, transaction_id: { expires_at_unix_ms: 123456n, nonce: "01".repeat(16) },
  writes: [{ collection, document, schema_generation: 1n, schema_digest: "02".repeat(32), condition: { kind: "absent" }, observed: [], value: { kind: "live", fields: [{ id: 1, value: { type: "bytes", value: new Uint8Array([1, 2]) } }] } }],
};

afterEach(() => vi.useRealTimers());

describe("current database request conformance", () => {
  test("constructs exact canonical scope and sends no invented selector headers", async () => {
    const requests: Request[] = [];
    const client = createTerraBaseClient({ ...scope, endpoint: "http://127.0.0.1:9911", apiKey: "scoped-test-key", fetch: async input => {
      const request = input as Request; requests.push(request);
      return json(point);
    } });
    expect((await client.read(collection, document)).ok).toBe(true);
    const request = requests[0]!;
    expect(request.url).toBe(`http://127.0.0.1:9911/v1/tenants/${tenant}/databases/${database}/shards/18446744073709551615/collections/${collection}/documents/${document}`);
    expect([...request.headers]).toEqual([["accept", "application/json"], ["authorization", "Bearer scoped-test-key"]]);
    expect(request.redirect).toBe("manual");
    expect(() => client.read(collection, "01ARZ3NDEKTSV4RRFFQ69G5FAV")).toThrow(TypeError);
  });

  test("rejects test API origins and credential-bearing origins at construction", () => {
    for (const endpoint of ["https://api.terrascale.tech", "https://gru.api.terrascale.tech", "https://api.terrascale.tech.", "https://gru.api.terrascale.tech.", "https://secret@gru.terrascale.tech", "https://gru.terrascale.tech/base", "http://public.example"]) {
      expect(() => createTerraBaseClient({ ...scope, endpoint, apiKey: "key" })).toThrow();
    }
  });

  test.each(["auth", "atlas", "www", "status", "sync", "app", "dash", "docs", "git", "cdn", "admin", "mcp", "mail"])("rejects reserved TerraScale %s origins before dispatch", label => {
    expect(() => createTerraBaseClient({ ...scope, endpoint: `https://${label}.terrascale.tech`, apiKey: "key" })).toThrow(/canonical regional hostname/u);
  });

  test.each(["https://terrascale.tech", "https://gru.terrascale.tech.", "https://GRU.terrascale.tech", "https://nested.gru.terrascale.tech", "https://gru.terrascale.tech:8443", "https://-gru.terrascale.tech"])("rejects noncanonical TerraScale origin %s", endpoint => {
    expect(() => createTerraBaseClient({ ...scope, endpoint, apiKey: "key" })).toThrow(/canonical regional hostname/u);
  });

  test.each(["https://gru.terrascale.tech", "https://fra.terrascale.tech/", "https://native.example.test:8443", "https://CUSTOM.example.test"])("preserves direct provisioned origin %s", async endpoint => {
    const requests: Request[] = [];
    const client = createTerraBaseClient({ ...scope, endpoint, apiKey: "key", fetch: async input => { requests.push(input as Request); return json(point); } });
    expect((await client.read(collection, document)).ok).toBe(true);
    expect(requests.map(request => new URL(request.url).origin)).toEqual([new URL(endpoint).origin]);
  });

  test.each(["fetch", "stream"] as const)("native %s TypeError remains a transport fault for reads and dispatched commits", async fault => {
    const requests: Request[] = [];
    const streams: ReadableStream<Uint8Array>[] = [];
    const client = createTerraBaseClient({ ...scope, endpoint: "https://gru.terrascale.tech", apiKey: "key", fetch: async input => {
      requests.push(input as Request);
      if (fault === "fetch") throw new TypeError("connection reset");
      const stream = new ReadableStream<Uint8Array>({ start(controller) { controller.error(new TypeError("connection reset")); } });
      streams.push(stream);
      return new Response(stream, { headers: { "content-type": "application/json" } });
    } });
    const prepared = await client.prepareCommit(commit);
    expect(await client.read(collection, document)).toMatchObject({ ok: false, error: { code: "transport_unavailable" } });
    expect(await client.commit(prepared)).toMatchObject({ ok: false, error: { code: "transport_unavailable", mutation_outcome: "uncertain", transaction: prepared } });
    expect(requests).toHaveLength(2);
    expect(requests.map(request => new URL(request.url).hostname)).toEqual(["gru.terrascale.tech", "gru.terrascale.tech"]);
    expect(streams.every(stream => !stream.locked)).toBe(true);
  });

  test.each(["json", "shape"] as const)("native %s validation remains invalid_response for reads and commits", async fault => {
    let requests = 0;
    const client = createTerraBaseClient({ ...scope, endpoint: "https://gru.terrascale.tech", apiKey: "key", fetch: async () => {
      requests++;
      return fault === "json" ? new Response("{", { headers: { "content-type": "application/json" } }) : json({ version: 1, invented: true });
    } });
    const prepared = await client.prepareCommit(commit);
    expect(await client.read(collection, document)).toMatchObject({ ok: false, error: { code: "invalid_response", status: 200 } });
    expect(await client.commit(prepared)).toMatchObject({ ok: false, error: { code: "invalid_response", status: 200, mutation_outcome: "uncertain", transaction: prepared } });
    expect(requests).toBe(2);
  });

  test.each(["fetch", "stream"] as const)("native %s abort remains cancelled with one dispatch and uncertain commit", async fault => {
    const controller = new AbortController();
    let requests = 0, cancelled = 0;
    const client = createTerraBaseClient({ ...scope, endpoint: "https://gru.terrascale.tech", apiKey: "key", fetch: async input => {
      requests++;
      const request = input as Request;
      if (fault === "fetch") {
        controller.abort(new TypeError("caller cancellation"));
        throw request.signal.reason;
      }
      return new Response(new ReadableStream<Uint8Array>({
        pull() { controller.abort(new TypeError("caller cancellation")); },
        cancel() { cancelled++; },
      }), { headers: { "content-type": "application/json" } });
    } });
    const prepared = await client.prepareCommit(commit);
    expect(await client.commit(prepared, { signal: controller.signal })).toMatchObject({ ok: false, error: { code: "cancelled", mutation_outcome: "uncertain", transaction: prepared } });
    expect(requests).toBe(1);
    if (fault === "stream") expect(cancelled).toBe(1);
  });

  test("caller replacement of public scope cannot change fingerprinting or point scope validation", async () => {
    const otherScope = { ...scope, database: "00000000-0000-0000-0000-000000000008", shard: 9n };
    const requests: Request[] = [];
    const event = { database: otherScope.database, shard: otherScope.shard.toString(), replica, incarnation: "03".repeat(16), sequence: "1" };
    const client = createTerraBaseClient({ ...scope, endpoint: "https://gru.terrascale.tech", apiKey: "key", fetch: async input => {
      requests.push(input as Request);
      return json({ version: 1, state: "value", state_version: "04".repeat(32), context: [{ replica, incarnation: event.incarnation, sequence: "1" }], siblings: [{ event, schema_generation: "1", schema_digest: "02".repeat(32), kind: "live", fields: [] }] });
    } });
    expect(Reflect.set(client, "scope", otherScope)).toBe(false);
    expect(client.scope).toEqual(scope);
    expect(Object.isFrozen(client.scope)).toBe(true);
    // Internal comparisons must use the immutable original even if JS shadows a prototype getter.
    Object.defineProperty(client, "scope", { value: otherScope });
    const result = await client.read(collection, document);
    expect(!result.ok && result.error.code).toBe("invalid_response");
    expect(requests[0]!.url).toContain(`/databases/${scope.database}/shards/${scope.shard}`);
    const originalClient = createTerraBaseClient({ ...scope, endpoint: "https://gru.terrascale.tech", apiKey: "key" });
    expect((await client.prepareCommit(commit)).effect_digest).toBe((await originalClient.prepareCommit(commit)).effect_digest);
  });

  test("snapshots a commit and exact retries retain bytes despite caller mutation", async () => {
    const bodies: string[] = [];
    const inputBytes = new Uint8Array([1, 2]);
    const client = createTerraBaseClient({ ...scope, endpoint: "http://127.0.0.1:9911", apiKey: "key", fetch: async input => {
      const body = await (input as Request).text(); bodies.push(body);
      const wire = JSON.parse(body);
      return json({ version: 1, durability: "local_durable", transaction_id: { expires_at_unix_ms: "123456", nonce: "01".repeat(16) }, event: { database, shard: scope.shard.toString(), replica, incarnation: "03".repeat(16), sequence: "1" }, commit_sequence: "1", effect_digest: wire.effect_digest, authority: wire.authority });
    } });
    const prepared = await client.prepareCommit({ ...commit, writes: [{ ...commit.writes[0]!, value: { kind: "live", fields: [{ id: 1, value: { type: "bytes", value: inputBytes } }] } }] });
    inputBytes[0] = 99;
    expect((await client.commit(prepared)).ok).toBe(true);
    expect((await client.commit(prepared)).ok).toBe(true);
    expect(bodies[0]).toBe(bodies[1]);
    expect(JSON.parse(bodies[0]!).writes[0].value.fields[0].value.value).toBe("AQI");
    expect(JSON.parse(bodies[0]!).effect_digest).toBe(prepared.effect_digest);
  });

  test("a lost response retains uncertainty and stable operation identity", async () => {
    const client = createTerraBaseClient({ ...scope, endpoint: "http://127.0.0.1:9911", apiKey: "key", fetch: async () => { throw new Error("connection lost"); } });
    const prepared = await client.prepareCommit(commit);
    const result = await client.commit(prepared);
    expect(result).toEqual({ ok: false, error: { code: "transport_unavailable", mutation_outcome: "uncertain", transaction: prepared } });
  });

  test("producer uncertainty and deterministic rejection have distinct outcomes", async () => {
    let code = "commit_uncertain";
    let status = 503;
    const client = createTerraBaseClient({ ...scope, endpoint: "http://127.0.0.1:9911", apiKey: "key", fetch: async () => json({ error: { code } }, status) });
    const prepared = await client.prepareCommit(commit);
    let result = await client.commit(prepared);
    expect(!result.ok && result.error.mutation_outcome).toBe("uncertain");
    code = "precondition_failed"; status = 412;
    result = await client.commit(prepared);
    expect(!result.ok && result.error.mutation_outcome).toBe("rejected");
  });

  test.each([400, 409, 500, 503])("precondition_failed at HTTP %s does not prove rejection", async status => {
    const client = createTerraBaseClient({ ...scope, endpoint: "http://127.0.0.1:9911", apiKey: "key", fetch: async () => json({ error: { code: "precondition_failed" } }, status) });
    const result = await client.commit(await client.prepareCommit(commit));
    expect(!result.ok && result.error.mutation_outcome).toBe("unknown");
  });

  test("an unadmitted HTTP 412 envelope cannot prove rejection", async () => {
    const client = createTerraBaseClient({ ...scope, endpoint: "http://127.0.0.1:9911", apiKey: "key", fetch: async () => json({ error: { code: "precondition_failed", invented: true } }, 412) });
    const result = await client.commit(await client.prepareCommit(commit));
    expect(!result.ok && result.error.code).toBe("invalid_response");
    expect(!result.ok && result.error.mutation_outcome).toBe("unknown");
  });

  test("invalid success receipts cannot become successful or definitive aborted writes", async () => {
    const client = createTerraBaseClient({ ...scope, endpoint: "http://127.0.0.1:9911", apiKey: "key", fetch: async () => json({ version: 1, durability: "global" }) });
    const result = await client.commit(await client.prepareCommit(commit));
    expect(!result.ok && result.error.code).toBe("invalid_response");
    expect(!result.ok && result.error.mutation_outcome).toBe("uncertain");
  });

  test("a followed native redirect cannot resolve a sent mutation", async () => {
    const client = createTerraBaseClient({ ...scope, endpoint: "https://gru.terrascale.tech", apiKey: "key", fetch: async () => {
      const response = json(point); Object.defineProperty(response, "redirected", { value: true }); return response;
    } });
    const result = await client.commit(await client.prepareCommit(commit));
    expect(result).toMatchObject({ ok: false, error: { code: "invalid_response", status: 200, mutation_outcome: "uncertain" } });
  });

  test.each([201, 202, 204, 206])("HTTP %s cannot satisfy any native HTTP 200 operation", async status => {
    const client = createTerraBaseClient({ ...scope, endpoint: "https://gru.terrascale.tech", apiKey: "key", fetch: async input => {
      const request = input as Request;
      if (status === 204) return new Response(null, { status, headers: { "content-type": "application/json" } });
      if (request.method === "GET") return json(point, status);
      const wire = JSON.parse(await request.text());
      if (request.url.endsWith("/status")) return json({ ...wire, state: "unknown", outcome: null }, status);
      if (request.url.endsWith("/query")) return json({ version: 1, rows: [] }, status);
      if (request.url.endsWith("/subscriptions")) return json({ version: 1, phase: "snapshot", rows: [], poll_cursor: "b3BhcXVl" }, status);
      return json({ version: 1, transaction_id: wire.transaction_id, authority: wire.authority, effect_digest: wire.effect_digest, durability: "local_durable", event: { database, shard: scope.shard.toString(), replica, incarnation: "03".repeat(16), sequence: "1" }, commit_sequence: "1" }, status);
    } });
    const prepared = await client.prepareCommit(commit);
    const results = await Promise.all([
      client.read(collection, document),
      client.query(collection, { predicate: { op: "is_missing", path: [1] }, limit: 1 }),
      client.subscription(collection, { phase: "snapshot", predicate: { op: "is_missing", path: [1] }, limit: 1 }),
      client.transactionStatus(prepared),
      client.commit(prepared),
    ]);
    for (const result of results) expect(result).toMatchObject({ ok: false, error: { code: "invalid_response", status } });
    expect(results[4]).toMatchObject({ ok: false, error: { mutation_outcome: "uncertain", transaction: prepared } });
  });

  test("pre-cancelled calls do no discovery or credential/network dispatch", async () => {
    let calls = 0;
    const client = createTerraBaseClient({ ...scope, apiKey: "key", fetch: async () => { calls++; return json(point); } });
    const controller = new AbortController(); controller.abort();
    const result = await client.read(collection, document, { signal: controller.signal });
    expect(!result.ok && result.error.code).toBe("cancelled");
    expect(calls).toBe(0);
  });

  test("native requests reject nonnative discovery credentials before metadata or data dispatch", async () => {
    let requests = 0;
    let credentials = 0;
    const client = createTerraBaseClient({ ...scope, apiKey: () => { credentials++; return "key"; }, fetch: async () => { requests++; return json(point); } });
    const prepared = await client.prepareCommit(commit);
    const results = await Promise.all([
      client.read(collection, document),
      client.query(collection, { predicate: { op: "is_missing", path: [1] }, limit: 1 }),
      client.subscription(collection, { phase: "snapshot", predicate: { op: "is_missing", path: [1] }, limit: 1 }),
      client.commit(prepared),
      client.transactionStatus(prepared),
    ]);
    for (const result of results) expect(!result.ok && result.error.code).toBe("invalid_discovery_credential");
    expect(!results[3]!.ok && results[3]!.error.mutation_outcome).toBe("unknown");
    expect(credentials).toBe(5);
    expect(requests).toBe(0);
  });

  test.each(["unknown", "pending", "expired"] as const)("durable status %s is unresolved with no invented aborted outcome", async state => {
    const requests: Request[] = [];
    const client = createTerraBaseClient({ ...scope, endpoint: "http://127.0.0.1:9911", apiKey: "key", fetch: async input => {
      const request = input as Request; requests.push(request);
      const identity = JSON.parse(await request.text());
      return json({ ...identity, state, outcome: null });
    } });
    const prepared = await client.prepareCommit(commit);
    const result = await client.transactionStatus(prepared);
    expect(result.ok && result.value.state).toBe(state);
    expect(result.ok && result.value.outcome).toBeNull();
    expect(new URL(requests[0]!.url).pathname).toBe(`/v1/tenants/${tenant}/databases/${database}/shards/18446744073709551615/transactions/status`);
  });

  test("preparing a single-writer operation is immutable and sends no discovery or data request", async () => {
    const client = createTerraBaseClient({ ...scope, apiKey: "key", fetch: async () => { throw new Error("must not discover"); } });
    const prepared = await client.prepareCommit({ ...commit, authority: { mode: "single_writer", generation: 1n, writer: { replica, incarnation: "03".repeat(16) } } });
    expect(prepared.authority.mode).toBe("single_writer");
    expect(Object.isFrozen(prepared.authority)).toBe(true);
  });

  test("prepared single-writer commits stay bound to their preparing client and authority endpoint", async () => {
    let requests = 0;
    const fetch = async (): Promise<Response> => { requests++; return json({ error: { code: "authority_unavailable" } }, 503); };
    const authorityClient = createTerraBaseClient({ ...scope, endpoint: "https://gru.terrascale.tech", apiKey: "key", fetch });
    const prepared = await authorityClient.prepareCommit({ ...commit, authority: { mode: "single_writer", generation: 1n, writer: { replica, incarnation: "03".repeat(16) } } });
    for (const endpoint of [undefined, "https://fra.terrascale.tech", "https://gru.terrascale.tech"]) {
      const otherClient = createTerraBaseClient({ ...scope, ...(endpoint === undefined ? {} : { endpoint }), apiKey: "key", fetch });
      expect(() => otherClient.commit(prepared)).toThrow(/another client/u);
    }
    expect(requests).toBe(0);
    const result = await authorityClient.commit(prepared);
    expect(!result.ok && result.error.code).toBe("authority_unavailable");
    expect(requests).toBe(1);
  });

  test.each([false, true])("status response matches the sent identity despite caller mutation (changed response: %s)", async changedResponse => {
    let sent!: () => void;
    const started = new Promise<void>(resolve => { sent = resolve; });
    let release!: (response: Response) => void;
    const pending = new Promise<Response>(resolve => { release = resolve; });
    let wire!: Record<string, unknown>;
    const client = createTerraBaseClient({ ...scope, endpoint: "https://gru.terrascale.tech", apiKey: "key", fetch: async input => { wire = JSON.parse(await (input as Request).text()); sent(); return pending; } });
    const identity = { transaction_id: { ...commit.transaction_id }, authority: { mode: "single_writer" as const, generation: 1n, writer: { replica, incarnation: "03".repeat(16) } }, effect_digest: "04".repeat(32) };
    const result = client.transactionStatus(identity);
    await started;
    identity.transaction_id.nonce = "05".repeat(16);
    identity.transaction_id.expires_at_unix_ms = 987654n;
    identity.effect_digest = "06".repeat(32);
    identity.authority.generation = 2n;
    identity.authority.writer.incarnation = "07".repeat(16);
    const responseIdentity = changedResponse ? { version: 1, transaction_id: { ...identity.transaction_id, expires_at_unix_ms: identity.transaction_id.expires_at_unix_ms.toString() }, effect_digest: identity.effect_digest, authority: { ...identity.authority, generation: identity.authority.generation.toString() } } : wire;
    release(json({ ...responseIdentity, state: "rejected", outcome: { error: { code: "precondition_failed" } } }));
    const status = await result;
    expect(status.ok).toBe(!changedResponse);
    if (changedResponse) expect(!status.ok && status.error.code).toBe("invalid_response");
    else expect(status.ok && status.value.transaction_id.nonce).toBe("01".repeat(16));
  });

  test("subscription responses must match the requested phase", async () => {
    let response: unknown = { version: 1, phase: "poll", groups: [], poll_cursor: "b3BhcXVl" };
    const client = createTerraBaseClient({ ...scope, endpoint: "http://127.0.0.1:9911", apiKey: "key", fetch: async () => json(response) });
    const snapshot = await client.subscription(collection, { phase: "snapshot", predicate: { op: "is_missing", path: [1] }, limit: 1 });
    expect(!snapshot.ok && snapshot.error.code).toBe("invalid_response");
    response = { version: 1, phase: "snapshot", rows: [], poll_cursor: "b3BhcXVl" };
    const poll = await client.subscription(collection, { phase: "poll", predicate: { op: "is_missing", path: [1] }, poll_cursor: "b3BhcXVl" });
    expect(!poll.ok && poll.error.code).toBe("invalid_response");
  });

  test("query and subscription snapshot pages cannot exceed the sent limit", async () => {
    const rows = [{ document, point }, { document: replica, point }];
    const client = createTerraBaseClient({ ...scope, endpoint: "http://127.0.0.1:9911", apiKey: "key", fetch: async input => json((input as Request).url.endsWith("/query") ? { version: 1, rows } : { version: 1, phase: "snapshot", rows, poll_cursor: "b3BhcXVl" }) });
    const query = { predicate: { op: "is_missing" as const, path: [1] }, limit: 1 };
    const snapshot = { phase: "snapshot" as const, ...query };
    const queryResult = client.query(collection, query);
    const snapshotResult = client.subscription(collection, snapshot);
    query.limit = 2;
    snapshot.limit = 2;
    expect(!(await queryResult).ok).toBe(true);
    expect(!(await snapshotResult).ok).toBe(true);
  });

  test("authorization failure or pre-dispatch cancellation cannot resolve an earlier uncertain attempt", async () => {
    const client = createTerraBaseClient({ ...scope, endpoint: "http://127.0.0.1:9911", apiKey: "key", fetch: async () => json({ error: { code: "forbidden_scope" } }, 403) });
    const prepared = await client.prepareCommit(commit);
    const forbidden = await client.commit(prepared);
    expect(!forbidden.ok && forbidden.error.mutation_outcome).toBe("unknown");
    const controller = new AbortController(); controller.abort();
    const cancelled = await client.commit(prepared, { signal: controller.signal });
    expect(!cancelled.ok && cancelled.error.mutation_outcome).toBe("unknown");
  });

  test("deadline bounds a pending credential provider without sending a request", async () => {
    let calls = 0;
    const client = createTerraBaseClient({ ...scope, endpoint: "http://127.0.0.1:9911", apiKey: () => new Promise<string>(() => undefined), requestTimeoutMs: 10, fetch: async () => { calls++; return json(point); } });
    const result = await client.commit(await client.prepareCommit(commit));
    expect(!result.ok && result.error.code).toBe("cancelled");
    expect(!result.ok && result.error.mutation_outcome).toBe("unknown");
    expect(calls).toBe(0);
  });

  test("deadline bounds an incomplete JSON body and cancels its reader", async () => {
    let cancelled = false;
    const client = createTerraBaseClient({ ...scope, endpoint: "http://127.0.0.1:9911", apiKey: "key", requestTimeoutMs: 10, fetch: async () => new Response(new ReadableStream({ cancel: () => { cancelled = true; } }), { headers: { "content-type": "application/json" } }) });
    const result = await client.commit(await client.prepareCommit(commit));
    expect(!result.ok && result.error.mutation_outcome).toBe("uncertain");
    expect(cancelled).toBe(true);
  });

});
