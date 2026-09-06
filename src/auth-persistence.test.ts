import { expect, it } from "vitest";
import { createAuthPersistenceClient } from "./auth-persistence.js";
import { AuthTransactionOutcomeUnknownError, AuthTransactionPoisonedError, runAuthTransaction } from "./auth-transaction.js";

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
    if (path.endsWith("/begin")) return Response.json({ record: null, matched: true, transactionId: "canonical-tx" });
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
    if (path.endsWith("/begin")) return Response.json({ record: null, matched: true, transactionId: "tx" });
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
    if (path.endsWith("/begin")) return Response.json({ record: null, matched: true, transactionId: "canonical-tx" });
    if (path.endsWith("/commit")) return Response.json({ error: { code: "auth_outcome_unknown", message: "resolve" } }, { status: 503 });
    return Response.json({ record: null, matched: true });
  } });
  await expect(runAuthTransaction(client, "db", async operations => {
    callbackCalls++;
    await operations.count({ model: "user", predicates: [] }, { idempotencyKey: "count" });
  }, { transactionIdFactory: () => "label", beginIdempotencyKeyFactory: () => "begin", commitIdempotencyKeyFactory: () => "commit" })).rejects.toMatchObject({
    transactionId: "canonical-tx",
    commitIdempotencyKey: "commit",
  } satisfies Partial<AuthTransactionOutcomeUnknownError>);
  expect(callbackCalls).toBe(1);
});
