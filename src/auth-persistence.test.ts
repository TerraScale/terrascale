import { expect, it } from "vitest";
import { createAuthPersistenceClient } from "./auth-persistence.js";
import { AuthTransactionOperationLimitError, AuthTransactionOutcomeUnknownError, AuthTransactionPoisonedError, runAuthTransaction } from "./auth-transaction.js";

it("stages named auth operations with stable keys and commits without the staging header", async () => {
  const requests: Request[] = [];
  const client = createAuthPersistenceClient({ fetch: async input => {
    requests.push(new Request(input));
    return Response.json({ record: null, matched: true });
  } });
  await client.insertUnique("auth db", { model: "user", record: { id: "one" } }, { idempotencyKey: "insert-one", transactionId: "tx-one" });
  await client.commitTransaction("auth db", { transactionId: "tx-one" }, { idempotencyKey: "commit-one", transactionId: "ignored" } as never);
  expect(new URL(requests[0]!.url).pathname).toBe("/api/v1/databases/auth%20db/auth/insertUnique");
  expect(requests[0]?.headers.get("x-terrascale-auth-transaction")).toBe("tx-one");
  expect(requests[0]?.headers.get("idempotency-key")).toBe("insert-one");
  expect(requests[1]?.headers.has("x-terrascale-auth-transaction")).toBe(false);
  expect(await requests[1]?.json()).toEqual({ transactionId: "tx-one" });
  expect(() => client.findPage("db", { model: "user", predicates: [], offset: 1001, limit: 1 }, { idempotencyKey: "page" })).toThrow(TypeError);
});

it("returns deterministic transaction conflicts without automatic replay", async () => {
  let calls = 0;
  const client = createAuthPersistenceClient({ maxRetryAttempts: 2, fetch: async () => {
    calls++;
    return Response.json({ error: { code: "transaction_conflict", message: "conflict", fix: "resolve identity" } }, { status: 409 });
  } });
  const result = await client.commitTransaction("db", { transactionId: "tx" }, { idempotencyKey: "commit" });
  expect(calls).toBe(1);
  expect(result.ok).toBe(false);
  if (!result.ok) expect(result.error.code).toBe("transaction_conflict");
});

it("stages findOne only with an explicit transaction identity and stable key", async () => {
  const requests: Request[] = [];
  const client = createAuthPersistenceClient({ fetch: async input => {
    requests.push(new Request(input));
    return Response.json({ record: { id: "one" }, matched: true });
  } });
  await client.findOne("db", "user", "one", { transactionId: "tx-one", idempotencyKey: "find-one" });
  expect(requests[0]?.headers.get("x-terrascale-auth-transaction")).toBe("tx-one");
  expect(requests[0]?.headers.get("idempotency-key")).toBe("find-one");
  await client.findOne("db", "user", "one");
  expect(requests[1]?.headers.has("x-terrascale-auth-transaction")).toBe(false);
  expect(requests[1]?.headers.has("idempotency-key")).toBe(false);
  expect(() => client.findOne("db", "user", "one", { transactionId: "tx-one" })).toThrow(TypeError);
  expect(() => client.findOne("db", "user", "one", { idempotencyKey: "find-one" })).toThrow(TypeError);
});

it("bounds begin labels in UTF-8 bytes and preserves the returned transaction identity", async () => {
  let calls = 0;
  const client = createAuthPersistenceClient({ fetch: async () => {
    calls++;
    return Response.json({ record: null, matched: true, transactionId: "é".repeat(50) + "#42", expiresAtMs: 30_000 });
  } });
  const result = await client.beginTransaction("db", { transactionId: "é".repeat(50) }, { idempotencyKey: "begin" });
  expect(result.ok).toBe(true);
  if (result.ok) expect(result.value.transactionId).toBe("é".repeat(50) + "#42");
  expect(() => client.beginTransaction("db", { transactionId: "é".repeat(50) + "x" }, { idempotencyKey: "too-long" })).toThrow(TypeError);
  expect(calls).toBe(1);
});

it("maps predicate auth routes and keeps ordinary queries read-scoped", async () => {
  const requests: Request[] = [];
  const client = createAuthPersistenceClient({ fetch: async input => {
    requests.push(new Request(input));
    return Response.json({ record: null, matched: true, count: 2 });
  } });
  const predicates = [{ field: "active", value: true }] as const;
  await client.count("db", { model: "user", predicates });
  await client.findPage("db", { model: "user", predicates, offset: 0, limit: 10 });
  await client.updateOne("db", { model: "user", predicates, update: { name: "Ada" } }, { idempotencyKey: "update-one", transactionId: "tx" });
  await client.updateMany("db", { model: "user", predicates, update: { active: false } }, { idempotencyKey: "update-many", transactionId: "tx" });
  await client.deleteOne("db", { model: "user", predicates }, { idempotencyKey: "delete-one", transactionId: "tx" });
  await client.deleteMany("db", { model: "user", predicates }, { idempotencyKey: "delete-many", transactionId: "tx" });
  await client.incrementOne("db", { model: "user", predicates, increment: { attempts: 1 }, set: { active: true } }, { idempotencyKey: "increment-one", transactionId: "tx" });
  await client.consumeMatching("db", { model: "verification", predicates }, { idempotencyKey: "consume-matching", transactionId: "tx" });

  expect(requests.map(request => new URL(request.url).pathname)).toEqual([
    "/api/v1/databases/db/auth/count",
    "/api/v1/databases/db/auth/findPage",
    "/api/v1/databases/db/auth/updateOne",
    "/api/v1/databases/db/auth/updateMany",
    "/api/v1/databases/db/auth/deleteOne",
    "/api/v1/databases/db/auth/deleteMany",
    "/api/v1/databases/db/auth/incrementOne",
    "/api/v1/databases/db/auth/consumeMatching",
  ]);
  expect(requests[0]?.headers.has("idempotency-key")).toBe(false);
  expect(requests[1]?.headers.has("idempotency-key")).toBe(false);
  expect(requests[2]?.headers.get("x-terrascale-auth-transaction")).toBe("tx");
  expect(await requests[6]?.json()).toEqual({ model: "user", predicates, increment: { attempts: 1 }, set: { active: true } });
});

it("retries ordinary auth query reads after transient failures without adding mutation identity", async () => {
  const requests: Request[] = [];
  const attempts = new Map<string, number>();
  let factoryCalls = 0;
  const client = createAuthPersistenceClient({ maxRetryAttempts: 1, retryDelayMs: 0, idempotencyKeyFactory: () => {
    factoryCalls++;
    return "unexpected-global-key";
  }, fetch: async input => {
    const request = new Request(input);
    requests.push(request);
    const path = new URL(request.url).pathname;
    const attempt = (attempts.get(path) ?? 0) + 1;
    attempts.set(path, attempt);
    if (attempt === 1) return Response.json({ error: { code: "auth_authority_unavailable", message: "retry" } }, { status: 503 });
    return Response.json({ record: null, matched: true, count: 2 });
  } });

  await expect(client.count("db", { model: "user", predicates: [] })).resolves.toMatchObject({ ok: true });
  await expect(client.findPage("db", { model: "user", predicates: [], offset: 0, limit: 10 })).resolves.toMatchObject({ ok: true });

  expect(requests).toHaveLength(4);
  expect(factoryCalls).toBe(0);
  for (const request of requests) {
    expect(request.headers.has("idempotency-key")).toBe(false);
    expect(request.headers.has("x-terrascale-auth-transaction")).toBe(false);
  }
});

it("does not retry ordinary auth queries on denial statuses", async () => {
  let calls = 0;
  const client = createAuthPersistenceClient({ maxRetryAttempts: 2, retryDelayMs: 0, fetch: async () => {
    calls++;
    return Response.json({ error: { code: "forbidden", message: "denied" } }, { status: 403 });
  } });

  const result = await client.count("db", { model: "user", predicates: [] });
  expect(calls).toBe(1);
  expect(result.ok).toBe(false);
  if (!result.ok) expect(result.error.status).toBe(403);
});

it("preserves full-width auth result counts as bigint", async () => {
  const client = createAuthPersistenceClient({ fetch: async () => new Response(
    '{"record":null,"matched":true,"count":18446744073709551615}',
    { status: 200, headers: { "content-type": "application/json" } },
  ) });
  const result = await client.count("db", { model: "user", predicates: [] });
  expect(result.ok).toBe(true);
  if (result.ok) expect(result.value.count).toBe(18446744073709551615n);
});

it("serializes transaction operations, preserves keys, and commits once", async () => {
  const paths: string[] = [];
  const keys: string[] = [];
  const client = createAuthPersistenceClient({ fetch: async input => {
    const request = new Request(input);
    const path = new URL(request.url).pathname;
    paths.push(path);
    const key = request.headers.get("idempotency-key");
    if (key !== null) keys.push(key);
    if (path.endsWith("/begin")) return Response.json({ record: null, matched: true, transactionId: "canonical-tx#1", expiresAtMs: 30_000 });
    return Response.json({ record: null, matched: true });
  } });
  const value = await runAuthTransaction(client, "db", async operations => {
    await Promise.all([
      operations.count({ model: "user", predicates: [] }, { idempotencyKey: "read-key" }),
      operations.incrementOne({ model: "user", predicates: [], increment: { attempts: 1 } }, { idempotencyKey: "increment-key" }),
    ]);
    return "done";
  }, {
    transactionIdFactory: () => "client-label",
    beginIdempotencyKeyFactory: () => "begin-key",
    commitIdempotencyKeyFactory: () => "commit-key",
    rollbackIdempotencyKeyFactory: () => "rollback-key",
  });
  expect(value).toBe("done");
  expect(paths).toEqual([
    "/api/v1/databases/db/auth/transactions/begin",
    "/api/v1/databases/db/auth/count",
    "/api/v1/databases/db/auth/incrementOne",
    "/api/v1/databases/db/auth/transactions/commit",
  ]);
  expect(keys).toEqual(["begin-key", "read-key", "increment-key", "commit-key"]);
});

it("rolls back a poisoned transaction even when callback catches the failed result", async () => {
  const paths: string[] = [];
  const client = createAuthPersistenceClient({ fetch: async input => {
    const path = new URL(new Request(input).url).pathname;
    paths.push(path);
    if (path.endsWith("/begin")) return Response.json({ record: null, matched: true, transactionId: "tx#1", expiresAtMs: 30_000 });
    if (path.endsWith("/count")) return Response.json({ error: { code: "transaction_conflict", message: "poisoned" } }, { status: 409 });
    return Response.json({ record: null, matched: true });
  } });
  await expect(runAuthTransaction(client, "db", async operations => {
    const result = await operations.count({ model: "user", predicates: [] }, { idempotencyKey: "count-key" });
    expect(result.ok).toBe(false);
    return undefined;
  }, { transactionIdFactory: () => "label", beginIdempotencyKeyFactory: () => "begin", rollbackIdempotencyKeyFactory: () => "rollback" })).rejects.toBeInstanceOf(AuthTransactionPoisonedError);
  expect(paths.at(-1)).toBe("/api/v1/databases/db/auth/transactions/rollback");
});

it("exposes uncertain commit identity without replaying the callback", async () => {
  let callbackCalls = 0;
  const client = createAuthPersistenceClient({ fetch: async input => {
    const path = new URL(new Request(input).url).pathname;
    if (path.endsWith("/begin")) return Response.json({ record: null, matched: true, transactionId: "canonical-tx#1", expiresAtMs: 30_000 });
    if (path.endsWith("/commit")) return Response.json({ error: { code: "auth_outcome_unknown", message: "resolve" } }, { status: 503 });
    return Response.json({ record: null, matched: true });
  } });
  await expect(runAuthTransaction(client, "db", async operations => {
    callbackCalls++;
    await operations.count({ model: "user", predicates: [] }, { idempotencyKey: "count" });
  }, { transactionIdFactory: () => "label", beginIdempotencyKeyFactory: () => "begin", commitIdempotencyKeyFactory: () => "commit" })).rejects.toMatchObject({
    transactionId: "canonical-tx#1",
    commitIdempotencyKey: "commit",
  } satisfies Partial<AuthTransactionOutcomeUnknownError>);
  expect(callbackCalls).toBe(1);
});

it("classifies transport and malformed commit responses as uncertain without replay", async () => {
  for (const mode of ["transport", "malformed", "empty"] as const) {
    let callbackCalls = 0;
    const client = createAuthPersistenceClient({ fetch: async input => {
      const path = new URL(new Request(input).url).pathname;
      if (path.endsWith("/begin")) return Response.json({ record: null, matched: true, transactionId: "canonical-tx#1", expiresAtMs: 30_000 });
      if (path.endsWith("/commit")) {
        if (mode === "transport") throw new TypeError("connection closed after write");
        return mode === "empty"
          ? Response.json({})
          : new Response("not-json", { status: 200, headers: { "content-type": "application/json" } });
      }
      return Response.json({ record: null, matched: true });
    } });
    await expect(runAuthTransaction(client, "db", async operations => {
      callbackCalls++;
      await operations.count({ model: "user", predicates: [] }, { idempotencyKey: `count-${mode}` });
    }, { transactionIdFactory: () => `label-${mode}`, beginIdempotencyKeyFactory: () => `begin-${mode}`, commitIdempotencyKeyFactory: () => `commit-${mode}` })).rejects.toMatchObject({
      transactionId: "canonical-tx#1",
      commitIdempotencyKey: `commit-${mode}`,
    });
    expect(callbackCalls).toBe(1);
  }
});

it("preserves callback and rollback identities when a frozen callback error meets failed cleanup", async () => {
  const client = createAuthPersistenceClient({ fetch: async input => {
    const path = new URL(new Request(input).url).pathname;
    if (path.endsWith("/begin")) return Response.json({ record: null, matched: true, transactionId: "canonical#4", expiresAtMs: 30_000 });
    if (path.endsWith("/rollback")) return Response.json({ error: { code: "auth_outcome_unknown", message: "cleanup uncertain" } }, { status: 503 });
    return Response.json({ record: null, matched: true });
  } });
  const original = Object.freeze(new Error("callback failed"));
  let thrown: unknown;
  try {
    await runAuthTransaction(client, "db", async () => {
      throw original;
    }, {
      transactionIdFactory: () => "label",
      beginIdempotencyKeyFactory: () => "begin",
      rollbackIdempotencyKeyFactory: () => "rollback-key",
    });
  } catch (error) {
    thrown = error;
  }
  expect(thrown).toBeInstanceOf(AggregateError);
  if (thrown instanceof AggregateError) {
    const rollback = thrown.errors.find(value => value instanceof Error && value.name === "AuthTransactionRollbackError") as { transactionId?: string; rollbackIdempotencyKey?: string } | undefined;
    expect(thrown.errors).toContain(original);
    expect(rollback?.transactionId).toBe("canonical#4");
    expect(rollback?.rollbackIdempotencyKey).toBe("rollback-key");
  }
});

it("propagates the outer abort signal to queued operations and still attempts cleanup", async () => {
  const controller = new AbortController();
  const paths: string[] = [];
  const client = createAuthPersistenceClient({ fetch: async input => {
    const request = new Request(input);
    const path = new URL(request.url).pathname;
    paths.push(path);
    if (path.endsWith("/begin")) return Response.json({ record: null, matched: true, transactionId: "tx#1", expiresAtMs: 30_000 });
    if (path.endsWith("/count")) {
      controller.abort();
      throw new DOMException("aborted", "AbortError");
    }
    return Response.json({ record: null, matched: true });
  } });
  await expect(runAuthTransaction(client, "db", async operations => {
    try {
      await operations.count({ model: "user", predicates: [] }, { idempotencyKey: "count" });
    } catch {
      // The callback deliberately catches the transport abort; the helper must retain poison state.
    }
  }, { signal: controller.signal, transactionIdFactory: () => "label", beginIdempotencyKeyFactory: () => "begin", rollbackIdempotencyKeyFactory: () => "rollback" })).rejects.toBeInstanceOf(AuthTransactionPoisonedError);
  expect(paths.at(-1)).toBe("/api/v1/databases/db/auth/transactions/rollback");
});

it("poisons at the local transaction operation bound while draining accepted work", async () => {
  let countCalls = 0;
  const paths: string[] = [];
  const client = createAuthPersistenceClient({ fetch: async input => {
    const path = new URL(new Request(input).url).pathname;
    paths.push(path);
    if (path.endsWith("/begin")) return Response.json({ record: null, matched: true, transactionId: "canonical#5", expiresAtMs: 30_000 });
    if (path.endsWith("/count")) countCalls++;
    return Response.json({ record: null, matched: true });
  } });
  let limitError: unknown;
  await expect(runAuthTransaction(client, "db", async operations => {
    for (let index = 0; index < 101; index += 1) {
      try {
        await operations.count({ model: "user", predicates: [] }, { idempotencyKey: `count-${index}` });
      } catch (error) {
        limitError = error;
      }
    }
  }, { transactionIdFactory: () => "label", beginIdempotencyKeyFactory: () => "begin", rollbackIdempotencyKeyFactory: () => "rollback" })).rejects.toBeInstanceOf(AuthTransactionPoisonedError);
  expect(limitError).toBeInstanceOf(AuthTransactionOperationLimitError);
  expect(countCalls).toBe(100);
  expect(paths.at(-1)).toBe("/api/v1/databases/db/auth/transactions/rollback");
});
