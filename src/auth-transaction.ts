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

export type AuthTransactionCallbackOptions = {
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
  readonly rollbackFailure?: AuthTransactionRollbackError | undefined;

  constructor(cause: unknown, rollbackFailure?: AuthTransactionRollbackError) {
    super("The auth transaction was poisoned by a failed operation.");
    this.name = "AuthTransactionPoisonedError";
    this.cause = cause;
    this.rollbackFailure = rollbackFailure;
  }
}

export class AuthTransactionOperationLimitError extends Error {
  readonly limit: number;

  constructor(limit: number) {
    super(`The auth transaction exceeds its ${limit}-operation bound.`);
    this.name = "AuthTransactionOperationLimitError";
    this.limit = limit;
  }
}

export class AuthTransactionRollbackError extends Error {
  readonly transactionId: string;
  readonly rollbackIdempotencyKey: string;
  override readonly cause: unknown;

  constructor(transactionId: string, rollbackIdempotencyKey: string, cause: unknown) {
    super("The auth transaction rollback outcome could not be confirmed.");
    this.name = "AuthTransactionRollbackError";
    this.transactionId = transactionId;
    this.rollbackIdempotencyKey = rollbackIdempotencyKey;
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
  options: AuthTransactionCallbackOptions = {},
): Promise<T> {
  if (typeof callback !== "function") throw new TypeError("Auth transaction callback must be a function.");
  const transactionLabel = identity(options.transactionIdFactory?.() ?? globalThis.crypto.randomUUID(), "transactionId", 100);
  const beginKey = key(options.beginIdempotencyKeyFactory, "auth-begin");
  const commitKey = key(options.commitIdempotencyKeyFactory, "auth-commit");
  const rollbackKey = key(options.rollbackIdempotencyKeyFactory, "auth-rollback");
  if (new Set([beginKey, commitKey, rollbackKey]).size !== 3) {
    throw new TypeError("Auth transaction lifecycle idempotency keys must be distinct.");
  }
  const begun = await client.beginTransaction(database, { transactionId: transactionLabel }, lifecycleOptions(beginKey, options.signal));
  if (!begun.ok) throw new AuthTransactionError("Auth transaction begin failed.", begun);
  const transactionId = validateTransactionResult(begun.value, "begin");

  let poisoned = false;
  let poisonCause: unknown;
  let closed = false;
  let acceptedOperations = 0;
  let queue = Promise.resolve();
  const enqueue = <R>(work: () => Promise<R>): Promise<R> => {
    if (closed) return Promise.reject(new Error("The auth transaction callback has settled."));
    if (acceptedOperations >= 100) {
      const error = new AuthTransactionOperationLimitError(100);
      poisoned = true;
      poisonCause ??= error;
      return Promise.reject(error);
    }
    acceptedOperations += 1;
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
    invoke(() => work({ ...supplied, transactionId, ...combinedSignal(options.signal, supplied.signal) }));
  const query = <R>(work: (options: AuthQueryOptions) => Promise<TerraScaleResult<R>>, supplied: AuthTransactionQueryOptions) =>
    invoke(() => work({ ...supplied, transactionId, ...combinedSignal(options.signal, supplied.signal) }));
  const operations: AuthTransactionOperations = {
    insertUnique: (request, supplied) => mutation(options => client.insertUnique(database, request, options), supplied),
    compareAndSwap: (request, supplied) => mutation(options => client.compareAndSwap(database, request, options), supplied),
    consumeOne: (request, supplied) => mutation(options => client.consumeOne(database, request, options), supplied),
    consumeMatching: (request, supplied) => mutation(options => client.consumeMatching(database, request, options), supplied),
    index: (request, supplied) => mutation(options => client.index(database, request, options), supplied),
    findOne: (model, id, supplied) => invoke(() => client.findOne(database, model, id, { ...supplied, transactionId, ...combinedSignal(options.signal, supplied.signal) })),
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
    const rollbackFailure = await rollback(client, database, transactionId, rollbackKey);
    throw withRollbackFailure(callbackError, rollbackFailure);
  }
  if (poisoned) {
    const rollbackFailure = await rollback(client, database, transactionId, rollbackKey);
    throw new AuthTransactionPoisonedError(poisonCause, rollbackFailure);
  }
  let committed: TerraScaleResult<AuthPersistenceResult>;
  try {
    committed = await client.commitTransaction(database, { transactionId }, lifecycleOptions(commitKey, options.signal));
  } catch (error) {
    throw new AuthTransactionOutcomeUnknownError(transactionId, commitKey, error);
  }
  if (!committed.ok) {
    if (isUncertainCommit(committed)) {
      throw new AuthTransactionOutcomeUnknownError(transactionId, commitKey, committed);
    }
    throw new AuthTransactionError("Auth transaction commit failed.", committed);
  }
  try {
    validateControlResult(committed.value, "commit");
  } catch (error) {
    throw new AuthTransactionOutcomeUnknownError(transactionId, commitKey, error);
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

async function rollback(client: AuthPersistenceClient, database: string, transactionId: string, keyValue: string): Promise<AuthTransactionRollbackError | undefined> {
  const cleanup = cleanupSignal();
  try {
    const result = await client.rollbackTransaction(database, { transactionId }, lifecycleOptions(keyValue, cleanup.signal));
    if (!result.ok) return new AuthTransactionRollbackError(transactionId, keyValue, result);
    try {
      validateControlResult(result.value, "rollback");
      return undefined;
    } catch (error) {
      return new AuthTransactionRollbackError(transactionId, keyValue, error);
    }
  } catch (error) {
    return new AuthTransactionRollbackError(transactionId, keyValue, error);
  } finally {
    cleanup.dispose();
  }
}

function key(factory: (() => string) | undefined, prefix: string): string {
  return identity(factory?.() ?? `${prefix}-${globalThis.crypto.randomUUID()}`, "idempotencyKey", 256);
}

function lifecycleOptions(idempotencyKey: string, signal: AbortSignal | undefined): AuthTransportTransactionOptions {
  return signal === undefined ? { idempotencyKey } : { idempotencyKey, signal };
}

function combinedSignal(outer: AbortSignal | undefined, supplied: AbortSignal | undefined): { readonly signal?: AbortSignal } {
  if (outer === undefined) return supplied === undefined ? {} : { signal: supplied };
  if (supplied === undefined) return { signal: outer };
  return { signal: AbortSignal.any([outer, supplied]) };
}

function cleanupSignal(): { readonly signal: AbortSignal; readonly dispose: () => void } {
  const controller = new AbortController();
  const timer = globalThis.setTimeout(() => controller.abort(), 5_000);
  return { signal: controller.signal, dispose: () => globalThis.clearTimeout(timer) };
}

function isUncertainCommit(result: Exclude<TerraScaleResult<unknown>, { readonly ok: true }>): boolean {
  return result.error.kind === "transport" || result.error.kind === "invalid-response" || (result.status !== undefined && result.status >= 500);
}

function withRollbackFailure(error: unknown, failure: AuthTransactionRollbackError | undefined): unknown {
  if (failure === undefined) return error;
  if ((typeof error === "object" || typeof error === "function") && error !== null) {
    try {
      Object.defineProperty(error, "authTransactionRollbackFailure", { configurable: true, enumerable: false, value: failure });
      return error;
    } catch {
      // Frozen errors still need to preserve both causes below.
    }
  }
  return new AggregateError([error, failure], "The auth callback failed and rollback could not be confirmed.", { cause: error });
}

function validateControlResult(result: AuthPersistenceResult, operation: string): void {
  if (result.matched !== true || result.record !== null) {
    throw new AuthTransactionError(`Auth transaction ${operation} returned an invalid success result.`, result);
  }
}

function identity(value: unknown, name: string, maximum: number): string {
  if (typeof value !== "string" || value.length === 0 || /\p{Cc}/u.test(value) || new TextEncoder().encode(value).length > maximum) {
    throw new TypeError(`${name} must be nonempty printable text of at most ${maximum} UTF-8 bytes.`);
  }
  return value;
}

function validateTransactionResult(result: AuthPersistenceResult, operation: string): string {
  validateControlResult(result, operation);
  if (typeof result.transactionId !== "string" || !result.transactionId.includes("#")) {
    throw new AuthTransactionError(`Auth transaction ${operation} returned no canonical identity.`, result);
  }
  validateU64(result.expiresAtMs, `${operation}.expiresAtMs`);
  return identity(result.transactionId, "transactionId", 128);
}

function validateU64(value: number | bigint | undefined, name: string): void {
  if (value === undefined) throw new AuthTransactionError(`Auth transaction ${name} is missing.`, value);
  if (typeof value === "bigint") {
    if (value < 0n) throw new AuthTransactionError(`Auth transaction ${name} is invalid.`, value);
    return;
  }
  if (!Number.isSafeInteger(value) || value < 0) throw new AuthTransactionError(`Auth transaction ${name} is invalid.`, value);
}
