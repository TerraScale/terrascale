import {
  createAdapterFactory,
  type AdapterFactory,
  type CleanedWhere,
  type CustomAdapter,
  type DBAdapter,
  type JoinConfig,
} from "@better-auth/core/db/adapter";
import type { BetterAuthOptions } from "@better-auth/core";
import {
  AuthPersistenceClient,
  type AuthConditionalUpdate,
  type AuthConsume,
  type AuthFindOneOptions,
  type AuthIncrement,
  type AuthMutationOptions,
  type AuthPageQuery,
  type AuthPersistenceResult,
  type AuthPredicateOnly,
  type AuthPredicateUpdate,
  type AuthQueryOptions,
  type AuthUniqueIndex,
  type AuthUniqueInsert,
  type AuthPersistencePredicate,
} from "./auth-persistence.js";
import { runAuthTransaction, type AuthTransactionOperations } from "./auth-transaction.js";
import type { JsonObject, JsonValue } from "./models.js";
import type { TerraScaleResult } from "./result.js";

type WirePageQuery = AuthPageQuery & {
  readonly select?: readonly string[];
  readonly sortBy?: { readonly field: string; readonly direction: "asc" | "desc" };
  readonly joins?: readonly AuthQueryJoin[];
};

type AuthQueryJoin = {
  readonly model: string;
  readonly outputField: string;
  readonly fromField: string;
  readonly toField: string;
  readonly relation: "one-to-one" | "one-to-many" | "many-to-many";
  readonly limit?: number;
};

type Port = {
  readonly insertUnique: (request: AuthUniqueInsert, options: AuthMutationOptions) => Promise<TerraScaleResult<AuthPersistenceResult>>;
  readonly updateOne: (request: AuthPredicateUpdate, options: AuthMutationOptions) => Promise<TerraScaleResult<AuthPersistenceResult>>;
  readonly updateMany: (request: AuthPredicateUpdate, options: AuthMutationOptions) => Promise<TerraScaleResult<AuthPersistenceResult>>;
  readonly deleteOne: (request: AuthPredicateOnly, options: AuthMutationOptions) => Promise<TerraScaleResult<AuthPersistenceResult>>;
  readonly deleteMany: (request: AuthPredicateOnly, options: AuthMutationOptions) => Promise<TerraScaleResult<AuthPersistenceResult>>;
  readonly consumeMatching: (request: AuthPredicateOnly, options: AuthMutationOptions) => Promise<TerraScaleResult<AuthPersistenceResult>>;
  readonly incrementOne: (request: AuthIncrement, options: AuthMutationOptions) => Promise<TerraScaleResult<AuthPersistenceResult>>;
  readonly index: (request: AuthUniqueIndex, options: AuthMutationOptions) => Promise<TerraScaleResult<AuthPersistenceResult>>;
  readonly findPage: (request: WirePageQuery, options: AuthQueryOptions) => Promise<TerraScaleResult<AuthPersistenceResult>>;
  readonly count: (request: AuthPredicateOnly, options: AuthQueryOptions) => Promise<TerraScaleResult<AuthPersistenceResult>>;
};

export type TerraScaleAuthAdapterFactory = AdapterFactory<BetterAuthOptions>;

/** Build the Better Auth 1.7.2 adapter factory over the public auth API. */
export function createTerraScaleAuthAdapter(
  client: AuthPersistenceClient,
  database: string,
): TerraScaleAuthAdapterFactory {
  return (options: BetterAuthOptions): DBAdapter<BetterAuthOptions> => {
    const factory = createAdapterFactory<BetterAuthOptions>({
      config: {
        adapterId: "terrascale-control-plane-auth",
        supportsDates: false,
        supportsJSON: true,
        supportsArrays: true,
        supportsBooleans: true,
        supportsNumericIds: false,
        supportsUUIDs: false,
        transaction: async (callback) => runAuthTransaction(
          client,
          database,
          async (transactionOperations) => callback(
            transactionAdapter(options, transactionOperations),
          ),
        ),
      },
      adapter: (factoryConfig) => customAdapter(factoryConfig, directPort(client, database)),
    });
    return factory(options);
  };
}

function transactionAdapter(
  options: BetterAuthOptions,
  operations: AuthTransactionOperations,
): Omit<DBAdapter<BetterAuthOptions>, "transaction"> {
  const factory = createAdapterFactory<BetterAuthOptions>({
    config: {
      adapterId: "terrascale-control-plane-auth-transaction",
      supportsDates: false,
      supportsJSON: true,
      supportsArrays: true,
      supportsBooleans: true,
      supportsNumericIds: false,
      supportsUUIDs: false,
      transaction: false,
    },
    adapter: (factoryConfig) => customAdapter(factoryConfig, transactionPort(operations)),
  });
  const adapter = factory(options);
  const { transaction: _transaction, ...scoped } = adapter;
  return scoped;
}

function customAdapter(
  config: Parameters<NonNullable<Parameters<typeof createAdapterFactory>[0]["adapter"]>>[0],
  port: Port,
): CustomAdapter {
  const operationKey = () => `better-auth-${globalThis.crypto.randomUUID()}`;
  const mutationOptions = (): AuthMutationOptions => ({ idempotencyKey: operationKey() });
  const queryOptions = (): AuthQueryOptions => ({});
  const where = (model: string, input: CleanedWhere[], action: Parameters<typeof config.transformWhereClause>[0]["action"]): readonly AuthPersistencePredicate[] => input.map((predicate) => ({
    field: config.getFieldName({ model, field: predicate.field }),
    value: jsonValue(predicate.value),
    ...(predicate.operator === "eq" ? {} : { operator: predicate.operator }),
    ...(predicate.connector === "AND" ? {} : { connector: predicate.connector }),
    ...(predicate.mode === "sensitive" ? {} : { mode: predicate.mode }),
  }));

  const page = async <T extends Record<string, unknown>>(
    model: string,
    input: WirePageQuery,
    options: AuthQueryOptions,
  ): Promise<T[]> => {
    const result = await port.findPage(input, options);
    const value = unwrap(result).records ?? [];
    return value.map((record) => record as T);
  };

  return {
    create: async ({ model, data, select }) => {
      const result = unwrap(await port.insertUnique({ model, record: jsonObject(data) }, mutationOptions()));
      return projectRecord(result.record, select);
    },
    update: async ({ model, where: predicates, update }) => {
      const result = unwrap(await port.updateOne({ model, predicates: where(model, predicates, "update"), update: jsonObject(update) }, mutationOptions()));
      return result.record;
    },
    updateMany: async ({ model, where: predicates, update }) => {
      const result = unwrap(await port.updateMany({ model, predicates: where(model, predicates, "updateMany"), update: jsonObject(update) }, mutationOptions()));
      return safeCount(result.count);
    },
    findOne: async ({ model, where: predicates, select, join }) => {
      const records = await page(model, {
        model,
        predicates: where(model, predicates, "findOne"),
        offset: 0,
        limit: 1,
        ...(select === undefined ? {} : { select }),
        ...(join === undefined ? {} : { joins: joins(join) }),
      }, queryOptions());
      return projectRecord(records[0] ?? null, select);
    },
    findMany: async ({ model, where: predicates, limit, offset, select, sortBy, join }) => page(model, {
      model,
      predicates: where(model, predicates ?? [], "findMany"),
      offset: offset ?? 0,
      limit,
      ...(select === undefined ? {} : { select }),
      ...(sortBy === undefined ? {} : { sortBy }),
      ...(join === undefined ? {} : { joins: joins(join) }),
    }, queryOptions()),
    delete: async ({ model, where: predicates }) => {
      unwrap(await port.deleteOne({ model, predicates: where(model, predicates, "delete") }, mutationOptions()));
    },
    deleteMany: async ({ model, where: predicates }) => {
      const result = unwrap(await port.deleteMany({ model, predicates: where(model, predicates, "deleteMany") }, mutationOptions()));
      return safeCount(result.count);
    },
    consumeOne: async ({ model, where: predicates }) => {
      const result = unwrap(await port.consumeMatching({ model, predicates: where(model, predicates, "consumeOne") }, mutationOptions()));
      return result.record;
    },
    incrementOne: async ({ model, where: predicates, increment, set }) => {
      const result = unwrap(await port.incrementOne({ model, predicates: where(model, predicates, "incrementOne"), increment, ...(set === undefined ? {} : { set: jsonObject(set) }) }, mutationOptions()));
      return result.record;
    },
    count: async ({ model, where: predicates }) => {
      const result = unwrap(await port.count({ model, predicates: where(model, predicates ?? [], "count") }, queryOptions()));
      return safeCount(result.count);
    },
  };
}

function directPort(client: AuthPersistenceClient, database: string): Port {
  return {
    insertUnique: (request, options) => client.insertUnique(database, request, options),
    updateOne: (request, options) => client.updateOne(database, request, options),
    updateMany: (request, options) => client.updateMany(database, request, options),
    deleteOne: (request, options) => client.deleteOne(database, request, options),
    deleteMany: (request, options) => client.deleteMany(database, request, options),
    consumeMatching: (request, options) => client.consumeMatching(database, request, options),
    incrementOne: (request, options) => client.incrementOne(database, request, options),
    index: (request, options) => client.index(database, request, options),
    findPage: (request, options) => client.findPage(database, request, options),
    count: (request, options) => client.count(database, request, options),
  };
}

function transactionPort(operations: AuthTransactionOperations): Port {
  return {
    insertUnique: operations.insertUnique,
    updateOne: operations.updateOne,
    updateMany: operations.updateMany,
    deleteOne: operations.deleteOne,
    deleteMany: operations.deleteMany,
    consumeMatching: operations.consumeMatching,
    incrementOne: operations.incrementOne,
    index: operations.index,
    findPage: operations.findPage,
    count: operations.count,
  };
}

function joins(input: JoinConfig): readonly AuthQueryJoin[] {
  return Object.entries(input).map(([outputField, value]) => ({
    model: outputField,
    outputField,
    fromField: value.on.from,
    toField: value.on.to,
    relation: value.relation ?? "one-to-many",
    ...(value.limit === undefined ? {} : { limit: value.limit }),
  }));
}

function unwrap(result: TerraScaleResult<AuthPersistenceResult>): AuthPersistenceResult {
  if (result.ok) return result.value;
  const error = new Error(result.error.message);
  error.name = "TerraScaleAuthAdapterError";
  Object.assign(error, { code: result.error.code, details: result.error });
  throw error;
}

function projectRecord(record: JsonObject | null, select?: readonly string[]): JsonObject | null {
  if (record === null || select === undefined || select.length === 0) return record;
  return Object.fromEntries(select.flatMap((field) => field in record ? [[field, record[field]]] : []));
}

function safeCount(value: number | bigint | undefined): number {
  if (typeof value === "bigint") {
    if (value < 0n || value > BigInt(Number.MAX_SAFE_INTEGER)) throw new RangeError("Auth count exceeds safe integer range.");
    return Number(value);
  }
  if (value === undefined || !Number.isSafeInteger(value) || value < 0) throw new TypeError("Auth operation returned an invalid count.");
  return value;
}

function jsonObject(value: Record<string, unknown>): JsonObject {
  const result: Record<string, JsonValue> = {};
  for (const [key, item] of Object.entries(value)) result[key] = jsonValue(item);
  return result;
}

function jsonValue(value: unknown): JsonValue {
  if (value === null || typeof value === "string" || typeof value === "boolean") return value;
  if (typeof value === "number" && Number.isFinite(value)) return value;
  if (value instanceof Date) return value.toISOString();
  if (Array.isArray(value)) return value.map(jsonValue);
  if (typeof value === "object") return jsonObject(value as Record<string, unknown>);
  throw new TypeError("Better Auth values must be JSON-compatible.");
}
