import { expect, it } from "vitest";
import { createAuthPersistenceClient } from "./auth-persistence.js";

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
