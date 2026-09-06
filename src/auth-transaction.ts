import {
  AuthPersistenceClient,
  type AuthConditionalUpdate,
  type AuthConsume,
  type AuthIncrement,
  type AuthMutationOptions,
  type AuthPageQuery,
  type AuthPersistenceResult,
  type AuthPredicateOnly,
  type AuthPredicateUpdate,
  type AuthQueryOptions,
  type AuthTransactionOptions as AuthTransportTransactionOptions,
  type AuthUniqueIndex,
  type AuthUniqueInsert,
} from "./auth-persistence.js";
import type { TerraScaleResult } from "./result.js";

export type AuthTransactionOptions = {
  readonly signal?: AbortSignal | undefined;
  readonly transactionIdFactory?: (() => string) | undefined;
  readonly beginIdempotencyKeyFactory?: (() => string) | undefined;
  readonly commitIdempotencyKeyFactory?: (() => string) | undefined;
  readonly rollbackIdempotencyKeyFactory?: (() => string) | undefined;
};

export type AuthTransactionMutationOptions = Omit<AuthMutationOptions, "transactionId">;
export type AuthTransactionQueryOptions = Omit<AuthQueryOptions, "transactionId"> & {
  readonly idempotencyKey: string;
};

export type AuthTransactionOperations = {
  readonly insertUnique: (
    request: AuthUniqueInsert,
    options: AuthTransactionMutationOptions,
  ) => Promise<TerraScaleResult<AuthPersistenceResult>>;
  readonly compareAndSwap: (
    request: AuthConditionalUpdate,
    options: AuthTransactionMutationOptions,
  ) => Promise<TerraScaleResult<AuthPersistenceResult>>;
  readonly consumeOne: (
    request: AuthConsume,
    options: AuthTransactionMutationOptions,
  ) => Promise<TerraScaleResult<AuthPersistenceResult>>;
  readonly consumeMatching: (
    request: AuthPredicateOnly,
    options: AuthTransactionMutationOptions,
  ) => Promise<TerraScaleResult<AuthPersistenceResult>>;
  readonly index: (
    request: AuthUniqueIndex,
    options: AuthTransactionMutationOptions,
  ) => Promise<TerraScaleResult<AuthPersistenceResult>>;
  readonly findOne: (
    model: string,
    id: string,
    options: AuthTransactionQueryOptions,
  ) => Promise<TerraScaleResult<AuthPersistenceResult>>;
  readonly findPage: (
    request: AuthPageQuery,
    options: AuthTransactionQueryOptions,
  ) => Promise<TerraScaleResult<AuthPersistenceResult>>;
  readonly count: (
    request: AuthPredicateOnly,
    options: AuthTransactionQueryOptions,
  ) => Promise<TerraScaleResult<AuthPersistenceResult>>;
  readonly updateOne: (
    request: AuthPredicateUpdate,
    options: AuthTransactionMutationOptions,
  ) => Promise<TerraScaleResult<AuthPersistenceResult>>;
  readonly updateMany: (
    request: AuthPredicateUpdate,
    options: AuthTransactionMutationOptions,
  ) => Promise<TerraScaleResult<AuthPersistenceResult>>;
  readonly deleteOne: (
    request: AuthPredicateOnly,
    options: AuthTransactionMutationOptions,
  ) => Promise<TerraScaleResult<AuthPersistenceResult>>;
  readonly deleteMany: (
    request: AuthPredicateOnly,
    options: AuthTransactionMutationOptions,
  ) => Promise<TerraScaleResult<AuthPersistenceResult>>;
  readonly incrementOne: (
    request: AuthIncrement,
    options: AuthTransactionMutationOptions,
  ) => Promise<TerraScaleResult<AuthPersistenceResult>>;
};

export class AuthTransactionPoisonedError extends Error {
  override readonly cause: unknown;

  constructor(cause: unknown) {
    super("The auth transaction was poisoned by a failed operation.");
    this.name = "AuthTransactionPoisonedError";
    this.cause = cause;
  }
}

export class AuthTransactionOutcomeUnknownError extends Error {
  readonly transactionId: string;
  readonly commitIdempotencyKey: string;

  constructor(transactionId: string, commitIdempotencyKey: string, cause?: unknown) {
    super("The auth transaction commit outcome is unknown; resolve it with the same transaction and commit identity.");
    this.name = "AuthTransactionOutcomeUnknownError";
    this.transactionId = transactionId;
    this.commitIdempotencyKey = commitIdempotencyKey;
    if (cause !== undefined) this.cause = cause;
  }

  override readonly cause?: unknown;
}

export async function runAuthTransaction<T>(
  client: AuthPersistenceClient,
  database: string,
  callback: (operations: AuthTransactionOperations) => Promise<T>,
  options: AuthTransactionOptions = {},
): Promise<T> {
  if (typeof callback !== "function") throw new TypeError("Auth transaction callback must be a function.");
  const transactionLabel = identity(options.transactionIdFactory?.() ?? globalThis.crypto.randomUUID(), "transactionId", 100);
  const beginKey = key(options.beginIdempotencyKeyFactory, "auth-begin");
  const commitKey = key(options.commitIdempotencyKeyFactory, "auth-commit");
  const rollbackKey = key(options.rollbackIdempotencyKeyFactory, "auth-rollback");
  const begun = await client.beginTransaction(database, { transactionId: transactionLabel }, lifecycleOptions(beginKey, options.signal));
  if (!begun.ok) throw new AuthTransactionError("Auth transaction begin failed.", begun);
  const transactionId = validateTransactionResult(begun.value, "begin");

  let poisoned = false;
  let poisonCause: unknown;
  let closed = false;
  let queue = Promise.resolve();
  const enqueue = <R>(work: () => Promise<R>): Promise<R> => {
    if (closed) return Promise.reject(new Error("The auth transaction callback has settled."));
    const result = queue.then(work, work);
    queue = result.then(() => undefined, () => undefined);
    return result;
  };
  const invoke = <R>(work: () => Promise<TerraScaleResult<R>>): Promise<TerraScaleResult<R>> => enqueue(async () => {
    try {
      const result = await work();
      if (!result.ok) {
        poisoned = true;
        poisonCause ??= result;
      }
      return result;
    } catch (error) {
      poisoned = true;
      poisonCause ??= error;
      throw error;
    }
  });
  const mutation = <R>(work: (options: AuthMutationOptions) => Promise<TerraScaleResult<R>>, supplied: AuthTransactionMutationOptions) =>
    invoke(() => work({ ...supplied, transactionId }));
  const query = <R>(work: (options: AuthQueryOptions) => Promise<TerraScaleResult<R>>, supplied: AuthTransactionQueryOptions) =>
    invoke(() => work({ ...supplied, transactionId }));
  const operations: AuthTransactionOperations = {
    insertUnique: (request, supplied) => mutation(options => client.insertUnique(database, request, options), supplied),
    compareAndSwap: (request, supplied) => mutation(options => client.compareAndSwap(database, request, options), supplied),
    consumeOne: (request, supplied) => mutation(options => client.consumeOne(database, request, options), supplied),
    consumeMatching: (request, supplied) => mutation(options => client.consumeMatching(database, request, options), supplied),
    index: (request, supplied) => mutation(options => client.index(database, request, options), supplied),
    findOne: (model, id, supplied) => invoke(() => client.findOne(database, model, id, { ...supplied, transactionId })),
    findPage: (request, supplied) => query(options => client.findPage(database, request, options), supplied),
    count: (request, supplied) => query(options => client.count(database, request, options), supplied),
    updateOne: (request, supplied) => mutation(options => client.updateOne(database, request, options), supplied),
    updateMany: (request, supplied) => mutation(options => client.updateMany(database, request, options), supplied),
    deleteOne: (request, supplied) => mutation(options => client.deleteOne(database, request, options), supplied),
    deleteMany: (request, supplied) => mutation(options => client.deleteMany(database, request, options), supplied),
    incrementOne: (request, supplied) => mutation(options => client.incrementOne(database, request, options), supplied),
  };

  let callbackValue: T | undefined;
  let callbackError: unknown;
  let callbackFailed = false;
  try {
    callbackValue = await callback(operations);
  } catch (error) {
    callbackError = error;
    callbackFailed = true;
  } finally {
    closed = true;
    await queue;
  }
  if (callbackFailed) {
    await rollback(client, database, transactionId, rollbackKey, options.signal);
    throw callbackError;
  }
  if (poisoned) {
    await rollback(client, database, transactionId, rollbackKey, options.signal);
    throw new AuthTransactionPoisonedError(poisonCause);
  }
  let committed: TerraScaleResult<AuthPersistenceResult>;
  try {
    committed = await client.commitTransaction(database, { transactionId }, lifecycleOptions(commitKey, options.signal));
  } catch (error) {
    throw new AuthTransactionOutcomeUnknownError(transactionId, commitKey, error);
  }
  if (!committed.ok) {
    if (committed.error.code === "auth_outcome_unknown" || committed.status === 503) {
      throw new AuthTransactionOutcomeUnknownError(transactionId, commitKey, committed);
    }
    throw new AuthTransactionError("Auth transaction commit failed.", committed);
  }
  return callbackValue as T;
}

class AuthTransactionError extends Error {
  readonly result: unknown;

  constructor(message: string, result: unknown) {
    super(message);
    this.name = "AuthTransactionError";
    this.result = result;
  }
}

async function rollback(client: AuthPersistenceClient, database: string, transactionId: string, keyValue: string, signal: AbortSignal | undefined): Promise<void> {
  try {
    await client.rollbackTransaction(database, { transactionId }, lifecycleOptions(keyValue, signal));
  } catch {
    // Preserve the callback/operation failure. Rollback is idempotent and can be reconciled separately.
  }
}

function key(factory: (() => string) | undefined, prefix: string): string {
  return identity(factory?.() ?? `${prefix}-${globalThis.crypto.randomUUID()}`, "idempotencyKey", 256);
}

function lifecycleOptions(idempotencyKey: string, signal: AbortSignal | undefined): AuthTransportTransactionOptions {
  return signal === undefined ? { idempotencyKey } : { idempotencyKey, signal };
}

function identity(value: unknown, name: string, maximum: number): string {
  if (typeof value !== "string" || value.length === 0 || /\p{Cc}/u.test(value) || new TextEncoder().encode(value).length > maximum) {
    throw new TypeError(`${name} must be nonempty printable text of at most ${maximum} UTF-8 bytes.`);
  }
  return value;
}

function validateTransactionResult(result: AuthPersistenceResult, operation: string): string {
  if (typeof result.transactionId !== "string") throw new AuthTransactionError(`Auth transaction ${operation} returned no canonical identity.`, result);
  return identity(result.transactionId, "transactionId", 128);
}
