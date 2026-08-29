import assert from "node:assert/strict";
import { test } from "node:test";

import {
  BetterAuthAdapterError,
  createTerraScaleBetterAuthAdapter,
  type BetterAuthDatabaseOperations,
} from "./better-auth.js";
import { ok, type TerraScaleResult } from "./result.js";

const headers = new Headers();

test("Better Auth adapter maps CRUD to named Database API operations", async () => {
  const calls: string[] = [];
  const keys: string[] = [];
  const operations = operationsForTest(calls, keys);
  const adapter = createTerraScaleBetterAuthAdapter(operations, {
    idempotencyKeyFactory: () => `attempt-${keys.length + 1}`,
  });

  const created = await adapter.create<{ id: string; email: string }, { email: string }>({
    model: "user",
    data: { id: "ignored", email: "user@example.test" } as unknown as { email: string },
  });
  assert.deepEqual(created, { email: "user@example.test" });
  assert.deepEqual(calls, ["insertUnique:user"]);
  assert.deepEqual(keys, ["attempt-1"]);

  const found = await adapter.findOne<{ id: string }>({
    model: "user",
    where: [{ field: "email", value: "user@example.test" }],
  });
  assert.deepEqual(found, { id: "user-1" });

  const page = await adapter.findMany<{ id: string }>({ model: "user", limit: 5 });
  assert.deepEqual(page, [{ id: "user-1" }]);

  await adapter.update<{ id: string }>({
    model: "user",
    where: [{ field: "id", value: "user-1" }],
    update: { email: "new@example.test" },
  });
  assert.deepEqual(keys, ["attempt-1", "attempt-2"]);
  assert.ok(calls.includes("compareAndSwap:user"));
});

test("Better Auth adapter preserves one-time consume and transaction boundaries", async () => {
  const calls: string[] = [];
  const operations = operationsForTest(calls, []);
  const adapter = createTerraScaleBetterAuthAdapter(operations);

  const consumed = await adapter.consumeOne<{ id: string }>({
    model: "verification",
    where: [{ field: "identifier", value: "token" }],
  });
  assert.deepEqual(consumed, { id: "verification-1" });

  const transactionValue = await adapter.transaction(async (transaction) => {
    await transaction.delete({ model: "verification", where: [] });
    return "committed";
  });
  assert.equal(transactionValue, "committed");
  assert.ok(calls.includes("transaction"));
  assert.ok(calls.includes("delete:verification"));
});

test("failed Database API results become Better Auth adapter errors", async () => {
  const operations: BetterAuthDatabaseOperations = {
    ...operationsForTest([], []),
    findOne: async () => ({
      ok: false,
      routeName: "database.auth.findOne",
      status: 409,
      headers,
      error: { kind: "conflict", code: "unique_conflict", message: "already exists" },
    }),
  };
  const adapter = createTerraScaleBetterAuthAdapter(operations);

  await assert.rejects(
    adapter.findOne({ model: "user", where: [] }),
    (error: unknown) =>
      error instanceof BetterAuthAdapterError &&
      error.code === "unique_conflict" &&
      error.message === "already exists",
  );
});

function operationsForTest(calls: string[], keys: string[]): BetterAuthDatabaseOperations {
  const result = <T>(value: T): TerraScaleResult<T> => ok("database.auth.test", 200, headers, value);
  return {
    insertUnique: async ({ model, data, idempotencyKey }) => {
      calls.push(`insertUnique:${model}`);
      keys.push(idempotencyKey);
      return result(data);
    },
    findOne: async <T extends Record<string, unknown>>({ model }: { readonly model: string }) => {
      calls.push(`findOne:${model}`);
      return result<T | null>({ id: "user-1" } as unknown as T);
    },
    findPage: async <T extends Record<string, unknown>>({ model }: { readonly model: string }) => {
      calls.push(`findPage:${model}`);
      return result<readonly T[]>([{ id: "user-1" } as unknown as T]);
    },
    count: async ({ model }) => {
      calls.push(`count:${model}`);
      return result(1);
    },
    compareAndSwap: async <T extends Record<string, unknown>>({ model, idempotencyKey }: { readonly model: string; readonly idempotencyKey: string }) => {
      calls.push(`compareAndSwap:${model}`);
      keys.push(idempotencyKey);
      return result<T | null>({ id: "user-1" } as unknown as T);
    },
    updateMany: async ({ model, idempotencyKey }) => {
      calls.push(`updateMany:${model}`);
      keys.push(idempotencyKey);
      return result(1);
    },
    delete: async ({ model, idempotencyKey }) => {
      calls.push(`delete:${model}`);
      keys.push(idempotencyKey);
      return result(undefined);
    },
    deleteMany: async ({ model, idempotencyKey }) => {
      calls.push(`deleteMany:${model}`);
      keys.push(idempotencyKey);
      return result(1);
    },
    consumeOne: async <T extends Record<string, unknown>>({ model, idempotencyKey }: { readonly model: string; readonly idempotencyKey: string }) => {
      calls.push(`consumeOne:${model}`);
      keys.push(idempotencyKey);
      return result<T | null>({ id: "verification-1" } as unknown as T);
    },
    transaction: async (callback) => {
      calls.push("transaction");
      return callback(operationsForTest(calls, keys));
    },
  };
}
