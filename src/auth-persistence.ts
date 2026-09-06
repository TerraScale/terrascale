import type { TerraScaleRuntimeConfig } from "./config.js";
import { TerraScaleHttp } from "./http.js";
import type { JsonObject, JsonValue } from "./models.js";
import type { TerraScaleResult } from "./result.js";

export type AuthPersistencePredicate = {
  readonly field: string;
  readonly value: JsonValue;
  readonly operator?: "eq" | "ne" | "lt" | "lte" | "gt" | "gte" | "in" | "not_in" | "contains" | "starts_with" | "ends_with";
  readonly connector?: "AND" | "OR";
  readonly mode?: "sensitive" | "insensitive";
};
export type AuthUniqueInsert = { readonly model: string; readonly record: JsonObject };
export type AuthConditionalUpdate = { readonly model: string; readonly id: string; readonly expected: JsonObject; readonly update: JsonObject };
export type AuthConsume = { readonly model: string; readonly id: string; readonly expected: JsonObject };
export type AuthUniqueIndex = { readonly model: string; readonly fields: readonly string[] };
export type AuthPageQuery = { readonly model: string; readonly predicates: readonly AuthPersistencePredicate[]; readonly offset: number; readonly limit: number };
export type AuthPredicateOnly = { readonly model: string; readonly predicates: readonly AuthPersistencePredicate[] };
export type AuthPredicateUpdate = AuthPredicateOnly & { readonly update: JsonObject };
export type AuthIncrement = AuthPredicateOnly & { readonly increment: Record<string, number>; readonly set?: JsonObject | undefined };
export type AuthTransactionIdentity = { readonly transactionId: string };
export type AuthPersistenceResult = {
  readonly record: JsonObject | null;
  readonly matched: boolean;
  readonly records?: readonly JsonObject[];
  readonly count?: number | bigint;
  readonly transactionId?: string;
  readonly expiresAtMs?: number | bigint;
};
export type AuthRequestOptions = { readonly signal?: AbortSignal };
export type AuthTransactionOptions = AuthRequestOptions & { readonly idempotencyKey: string };
export type AuthMutationOptions = AuthTransactionOptions & { readonly transactionId?: string };
export type AuthFindOneOptions = AuthRequestOptions & { readonly transactionId?: string; readonly idempotencyKey?: string };
export type AuthQueryOptions = AuthRequestOptions & { readonly transactionId?: string; readonly idempotencyKey?: string };

/** Named server-side persistence operations backed by the admitted auth authority. */
export class AuthPersistenceClient {
  readonly #http: TerraScaleHttp;
  constructor(config: TerraScaleRuntimeConfig) {
    if (typeof window !== "undefined") throw new Error("Auth persistence is server-only.");
    this.#http = new TerraScaleHttp(config);
  }

  insertUnique(database: string, request: AuthUniqueInsert, options: AuthMutationOptions): Promise<TerraScaleResult<AuthPersistenceResult>> {
    return this.#http.request({ method: "POST", path: `${authPath(database)}/insertUnique`, routeName: "databases.insertUnique", body: request, ...mutationOptions(options) });
  }

  compareAndSwap(database: string, request: AuthConditionalUpdate, options: AuthMutationOptions): Promise<TerraScaleResult<AuthPersistenceResult>> {
    return this.#http.request({ method: "POST", path: `${authPath(database)}/compareAndSwap`, routeName: "databases.compareAndSwap", body: request, ...mutationOptions(options) });
  }

  consumeOne(database: string, request: AuthConsume, options: AuthMutationOptions): Promise<TerraScaleResult<AuthPersistenceResult>> {
    return this.#http.request({ method: "POST", path: `${authPath(database)}/consumeOne`, routeName: "databases.consumeOne", body: request, ...mutationOptions(options) });
  }

  index(database: string, request: AuthUniqueIndex, options: AuthMutationOptions): Promise<TerraScaleResult<AuthPersistenceResult>> {
    return this.#http.request({ method: "POST", path: `${authPath(database)}/index`, routeName: "databases.index", body: request, ...mutationOptions(options) });
  }

  findOne(database: string, model: string, id: string, options: AuthFindOneOptions = {}): Promise<TerraScaleResult<AuthPersistenceResult>> {
    if (options.transactionId === undefined) {
      if (options.idempotencyKey !== undefined) throw new TypeError("findOne idempotencyKey requires transactionId.");
      return this.#http.request({ method: "GET", path: `${authPath(database)}/records/${segment(model)}/${segment(id)}`, routeName: "databases.findOne", signal: options.signal });
    }
    if (options.idempotencyKey === undefined) throw new TypeError("findOne transactionId requires idempotencyKey.");
    identity(options.transactionId, "transactionId", 128);
    identity(options.idempotencyKey, "idempotencyKey", 256);
    return this.#http.request({ method: "GET", path: `${authPath(database)}/records/${segment(model)}/${segment(id)}`, routeName: "databases.findOne", signal: options.signal, idempotencyKey: options.idempotencyKey, headers: { "x-terrascale-auth-transaction": options.transactionId } });
  }

  findPage(database: string, request: AuthPageQuery, options: AuthQueryOptions = {}): Promise<TerraScaleResult<AuthPersistenceResult>> {
    if (!Number.isInteger(request.offset) || request.offset < 0 || request.offset > 1000 || !Number.isInteger(request.limit) || request.limit < 0 || request.limit > 1000) throw new TypeError("Auth pagination values must be integers between 0 and 1000.");
    return this.#http.request({ method: "POST", path: `${authPath(database)}/findPage`, routeName: "databases.findPage", body: request, ...queryOptions(options) });
  }

  updateOne(database: string, request: AuthPredicateUpdate, options: AuthMutationOptions): Promise<TerraScaleResult<AuthPersistenceResult>> {
    return this.#http.request({ method: "POST", path: `${authPath(database)}/updateOne`, routeName: "databases.updateOne", body: request, ...mutationOptions(options) });
  }

  updateMany(database: string, request: AuthPredicateUpdate, options: AuthMutationOptions): Promise<TerraScaleResult<AuthPersistenceResult>> {
    return this.#http.request({ method: "POST", path: `${authPath(database)}/updateMany`, routeName: "databases.updateMany", body: request, ...mutationOptions(options) });
  }

  deleteOne(database: string, request: AuthPredicateOnly, options: AuthMutationOptions): Promise<TerraScaleResult<AuthPersistenceResult>> {
    return this.#http.request({ method: "POST", path: `${authPath(database)}/deleteOne`, routeName: "databases.deleteOne", body: request, ...mutationOptions(options) });
  }

  deleteMany(database: string, request: AuthPredicateOnly, options: AuthMutationOptions): Promise<TerraScaleResult<AuthPersistenceResult>> {
    return this.#http.request({ method: "POST", path: `${authPath(database)}/deleteMany`, routeName: "databases.deleteMany", body: request, ...mutationOptions(options) });
  }

  incrementOne(database: string, request: AuthIncrement, options: AuthMutationOptions): Promise<TerraScaleResult<AuthPersistenceResult>> {
    return this.#http.request({ method: "POST", path: `${authPath(database)}/incrementOne`, routeName: "databases.incrementOne", body: request, ...mutationOptions(options) });
  }

  consumeMatching(database: string, request: AuthPredicateOnly, options: AuthMutationOptions): Promise<TerraScaleResult<AuthPersistenceResult>> {
    return this.#http.request({ method: "POST", path: `${authPath(database)}/consumeMatching`, routeName: "databases.consumeMatching", body: request, ...mutationOptions(options) });
  }

  count(database: string, request: AuthPredicateOnly, options: AuthQueryOptions = {}): Promise<TerraScaleResult<AuthPersistenceResult>> {
    return this.#http.request({ method: "POST", path: `${authPath(database)}/count`, routeName: "databases.count", body: request, ...queryOptions(options) });
  }

  /** Use the returned transactionId for every operation, commit, and rollback. */
  beginTransaction(database: string, request: AuthTransactionIdentity, options: AuthTransactionOptions): Promise<TerraScaleResult<AuthPersistenceResult>> {
    identity(request.transactionId, "transactionId", 100);
    return this.#http.request({ method: "POST", path: `${authPath(database)}/transactions/begin`, routeName: "databases.beginTransaction", body: request, ...transactionOptions(options) });
  }

  /** Resolve uncertain commits with the same identity; never rerun the application callback. */
  commitTransaction(database: string, request: AuthTransactionIdentity, options: AuthTransactionOptions): Promise<TerraScaleResult<AuthPersistenceResult>> {
    identity(request.transactionId, "transactionId", 128);
    return this.#http.request({ method: "POST", path: `${authPath(database)}/transactions/commit`, routeName: "databases.commitTransaction", body: request, ...transactionOptions(options) });
  }

  rollbackTransaction(database: string, request: AuthTransactionIdentity, options: AuthTransactionOptions): Promise<TerraScaleResult<AuthPersistenceResult>> {
    identity(request.transactionId, "transactionId", 128);
    return this.#http.request({ method: "POST", path: `${authPath(database)}/transactions/rollback`, routeName: "databases.rollbackTransaction", body: request, ...transactionOptions(options) });
  }
}

export function createAuthPersistenceClient(config: TerraScaleRuntimeConfig): AuthPersistenceClient {
  return new AuthPersistenceClient(config);
}

function transactionOptions(options: AuthTransactionOptions) {
  identity(options?.idempotencyKey, "idempotencyKey", 256);
  return { idempotencyKey: options.idempotencyKey, signal: options.signal };
}
function mutationOptions(options: AuthMutationOptions) {
  const transport = transactionOptions(options);
  if (options.transactionId === undefined) return transport;
  identity(options.transactionId, "transactionId", 128);
  return { ...transport, headers: { "x-terrascale-auth-transaction": options.transactionId } };
}
function queryOptions(options: AuthQueryOptions) {
  const { transactionId, idempotencyKey } = options;
  if (transactionId === undefined) {
    if (idempotencyKey !== undefined) throw new TypeError("Query idempotencyKey requires transactionId.");
    return { signal: options.signal };
  }
  if (idempotencyKey === undefined) throw new TypeError("Query transactionId requires idempotencyKey.");
  identity(transactionId, "transactionId", 128);
  identity(idempotencyKey, "idempotencyKey", 256);
  return { signal: options.signal, idempotencyKey, headers: { "x-terrascale-auth-transaction": transactionId } };
}
function authPath(database: string): string { return `/api/v1/databases/${segment(database)}/auth`; }
function segment(value: string): string {
  identity(value, "path identifier", 255);
  return encodeURIComponent(value);
}
function identity(value: string, name: string, maximum: number): void {
  if (typeof value !== "string" || value.length === 0 || /\p{Cc}/u.test(value) || new TextEncoder().encode(value).length > maximum) throw new TypeError(`${name} must be nonempty printable text of at most ${maximum} UTF-8 bytes.`);
}
