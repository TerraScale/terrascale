import {
  createAdapterFactory,
  type CleanedWhere,
  type CustomAdapter,
  type DBAdapter,
  type JoinConfig,
} from "@better-auth/core/db/adapter";
import type { BetterAuthOptions } from "@better-auth/core";
import {
  AuthPersistenceClient,
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
  type AuthQueryJoin,
} from "./auth-persistence.js";
import { runAuthTransaction, type AuthTransactionOperations } from "./auth-transaction.js";
import type { JsonObject, JsonValue } from "./models.js";
import type { TerraScaleResult } from "./result.js";

type WirePageQuery = AuthPageQuery;

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
type CreateInput = Parameters<CustomAdapter["create"]>[0];
type UpdateInput = Parameters<CustomAdapter["update"]>[0];
type UpdateManyInput = Parameters<CustomAdapter["updateMany"]>[0];
type FindOneInput = Parameters<CustomAdapter["findOne"]>[0];
type FindManyInput = Parameters<CustomAdapter["findMany"]>[0];
type DeleteInput = Parameters<CustomAdapter["delete"]>[0];
type DeleteManyInput = Parameters<CustomAdapter["deleteMany"]>[0];
type ConsumeInput = Parameters<CustomAdapter["consumeOne"]>[0];
type IncrementInput = Parameters<CustomAdapter["incrementOne"]>[0];
type CountInput = Parameters<CustomAdapter["count"]>[0];

export type TerraScaleAuthAdapterFactory = (options: BetterAuthOptions) => TerraScaleAuthDatabaseAdapter;
export class TerraScaleAuthAdapterError extends Error {
  readonly code: string;
  readonly details: unknown;

  constructor(message: string, code: string, details: unknown) {
    super(message);
    this.name = "TerraScaleAuthAdapterError";
    this.code = code;
    this.details = details;
  }
}
export type TerraScaleAuthIndexInput = {
  readonly model: string;
  readonly fields: readonly string[];
  readonly unique?: boolean | undefined;
};
export type TerraScaleAuthTransactionAdapter = Omit<DBAdapter<BetterAuthOptions>, "transaction"> & {
  readonly index: (input: TerraScaleAuthIndexInput) => Promise<void>;
};
export type TerraScaleAuthDatabaseAdapter = Omit<DBAdapter<BetterAuthOptions>, "transaction"> & {
  readonly index: (input: TerraScaleAuthIndexInput) => Promise<void>;
  readonly transaction: <T>(callback: (adapter: TerraScaleAuthTransactionAdapter) => Promise<T>) => Promise<T>;
};

/** Build the Better Auth 1.7.2 adapter factory over the public auth API. */
export function createTerraScaleAuthAdapter(
  client: AuthPersistenceClient,
  database: string,
): TerraScaleAuthAdapterFactory {
  return (options: BetterAuthOptions): TerraScaleAuthDatabaseAdapter => {
    const factoryOptions: BetterAuthOptions = {
      ...options,
      advanced: {
        ...options.advanced,
        database: { ...options.advanced?.database, joins: true },
      },
    };
    let ensureSchema: () => Promise<void> = async () => undefined;
    let directIndex: ((input: TerraScaleAuthIndexInput) => Promise<void>) | undefined;
    const factory = createAdapterFactory<BetterAuthOptions>({
      config: {
        adapterId: "terrascale-control-plane-auth",
        supportsDates: false,
        supportsJSON: true,
        supportsArrays: true,
        supportsBooleans: true,
        supportsNumericIds: false,
        supportsUUIDs: false,
        transaction: async (callback) => {
          await ensureSchema();
          return runAuthTransaction(client, database, async (transactionOperations) => serializedTransactionCallback(
            transactionAdapter(factoryOptions, transactionOperations), callback,
          ));
        },
      },
      adapter: (factoryConfig) => {
        const result = customAdapter(factoryConfig, directPort(client, database), true);
        ensureSchema = result.ensure;
        directIndex = result.index;
        return result.adapter;
      },
    });
    const adapter = factory(factoryOptions);
    return Object.assign(adapter, {
      index: (input: TerraScaleAuthIndexInput) => {
        if (directIndex === undefined) throw new Error("Auth adapter index operation is unavailable.");
        return directIndex(input);
      },
    }) as TerraScaleAuthDatabaseAdapter;
  };
}

async function serializedTransactionCallback<T>(
  adapter: Omit<DBAdapter<BetterAuthOptions>, "transaction">,
  callback: (adapter: Omit<DBAdapter<BetterAuthOptions>, "transaction">) => Promise<T>,
): Promise<T> {
  let queue = Promise.resolve();
  let closed = false;
  let failed = false;
  let failure: unknown;
  let accepted = 0;
  const poison = (error: unknown) => {
    if (!failed) failure = error;
    failed = true;
  };
  const scoped = new Proxy(adapter, {
    get(target, property, receiver) {
      const method: unknown = Reflect.get(target, property, receiver);
      if (typeof method !== "function") return method;
      return (...args: unknown[]) => {
        if (closed) return Promise.reject(new Error("Auth transaction callback has settled."));
        if (accepted >= 100) {
          const error = new Error("Auth transaction exceeds 100 adapter operations.");
          poison(error);
          return Promise.reject(error);
        }
        accepted += 1;
        // Include official input/output transforms in the ordered operation.
        const result = queue.then(() => Reflect.apply(method, target, args));
        queue = result.then(() => undefined, poison);
        return result;
      };
    },
  });
  let value: T;
  try {
    value = await callback(scoped);
  } catch (error) {
    poison(error);
  } finally {
    closed = true;
    await queue;
  }
  if (failed) throw failure;
  return value!;
}

function transactionAdapter(
  options: BetterAuthOptions,
  operations: AuthTransactionOperations,
): TerraScaleAuthTransactionAdapter {
  let scopedIndex: ((input: TerraScaleAuthIndexInput) => Promise<void>) | undefined;
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
    adapter: (factoryConfig) => {
      const result = customAdapter(factoryConfig, transactionPort(operations), false);
      scopedIndex = result.index;
      return Object.assign(result.adapter, { index: result.index });
    },
  });
  const adapter = factory(options);
  const { transaction: _transaction, ...scoped } = adapter;
  if (scopedIndex === undefined) throw new Error("Auth transaction index operation is unavailable.");
  return { ...scoped, index: scopedIndex };
}

function customAdapter(
  config: Parameters<NonNullable<Parameters<typeof createAdapterFactory>[0]["adapter"]>>[0],
  port: Port,
  provisionIndexes: boolean,
): {
  readonly adapter: CustomAdapter;
  readonly ensure: () => Promise<void>;
  readonly index: (input: TerraScaleAuthIndexInput) => Promise<void>;
} {
  const operationKey = () => `better-auth-${globalThis.crypto.randomUUID()}`;
  const mutationOptions = (): AuthMutationOptions => ({ idempotencyKey: operationKey() });
  const queryOptions = (): AuthQueryOptions => ({});
  const where = (model: string, input: CleanedWhere[], _action: Parameters<typeof config.transformWhereClause>[0]["action"]): readonly AuthPersistencePredicate[] => input.map((predicate) => ({
    field: config.getFieldName({ model, field: predicate.field }),
    value: jsonValue(predicate.value),
    ...(predicate.operator === "eq" || predicate.operator === undefined ? {} : { operator: predicate.operator }),
    ...(predicate.connector === "AND" || predicate.connector === undefined ? {} : { connector: predicate.connector }),
    ...(predicate.mode === "sensitive" || predicate.mode === undefined ? {} : { mode: predicate.mode }),
  }));

  const page = async (
    _model: string,
    input: WirePageQuery,
    options: AuthQueryOptions,
  ): Promise<readonly Record<string, any>[]> => {
    const value = unwrap(await port.findPage(input, options)).records;
    if (!Array.isArray(value)) throw new TypeError("Auth page operation returned no records array.");
    return value.map((record) => {
      if (record === null || typeof record !== "object" || Array.isArray(record)) throw new TypeError("Auth page operation returned an invalid record.");
      return jsonObject(record);
    });
  };

  let ready: Promise<void> | undefined;
  const ensure = async (): Promise<void> => {
    if (!provisionIndexes) return;
    if (ready === undefined) {
      const attempt = (async () => {
        const provisioned = new Set<string>();
        for (const [model, table] of Object.entries(config.schema)) {
          const indexes = [
            ...Object.entries(table.fields).filter(([, field]) => field.unique === true)
              .map(([field]) => [field]),
            ...(table.indexes ?? []).filter(index => index.unique === true)
              .map(index => index.fields),
          ];
          for (const fields of indexes) {
            const request = {
              model: config.getModelName(model),
              fields: fields.map(field => config.getFieldName({ model, field })),
            };
            const identity = JSON.stringify(request);
            if (provisioned.has(identity)) continue;
            // Provision sequentially so cold-start schema work cannot saturate
            // the same bounded consensus lane required to install the indexes.
            const value = unwrap(await port.index(request, {
              idempotencyKey: `better-auth-index-${globalThis.crypto.randomUUID()}`,
            }));
            if (value.matched !== true || value.record !== null) {
              throw new TypeError("Auth index operation returned an invalid success result.");
            }
            provisioned.add(identity);
          }
        }
      })();
      ready = attempt.catch((error: unknown) => {
        ready = undefined;
        throw error;
      });
    }
    await ready;
  };
  const namedIndex = async (input: TerraScaleAuthIndexInput): Promise<void> => {
    if (input.unique === false) throw new TypeError("TerraScale auth indexes must be unique.");
    await ensure();
    const request = {
      model: config.getModelName(input.model),
      fields: input.fields.map(field => config.getFieldName({ model: input.model, field })),
    };
    const result = unwrap(await port.index(request, { idempotencyKey: `better-auth-index-${globalThis.crypto.randomUUID()}` }));
    if (result.matched !== true || result.record !== null) {
      throw new TypeError("Auth index operation returned an invalid success result.");
    }
  };
  const adapter: CustomAdapter = {
    create: async <T extends Record<string, any>>(input: CreateInput & { data: T }) => {
      await ensure();
      const result = unwrap(await port.insertUnique({ model: input.model, record: jsonObject(input.data) }, mutationOptions()));
      return outputRecord(result.record) as T;
    },
    update: async <T>(input: UpdateInput & { update: T }) => {
      await ensure();
      const result = unwrap(await port.updateOne({ model: input.model, predicates: where(input.model, input.where, "update"), update: jsonObject(input.update as Record<string, unknown>) }, mutationOptions()));
      return outputRecord(result.record) as T | null;
    },
    updateMany: async (input: UpdateManyInput) => {
      await ensure();
      const result = unwrap(await port.updateMany({ model: input.model, predicates: where(input.model, input.where, "updateMany"), update: jsonObject(input.update) }, mutationOptions()));
      return safeCount(result.count);
    },
    findOne: async <T>(input: FindOneInput) => {
      await ensure();
      const records = await page(input.model, { model: input.model, predicates: where(input.model, input.where, "findOne"), offset: 0, limit: 1, ...(input.select === undefined || input.select.length === 0 ? {} : { select: input.select.map((field) => config.getFieldName({ model: input.model, field })) }), ...(input.join === undefined ? {} : { joins: joins(input.join) }) }, queryOptions());
      return outputRecord(records[0] ?? null) as T | null;
    },
    findMany: async <T>(input: FindManyInput) => {
      await ensure();
      const records = await page(input.model, { model: input.model, predicates: where(input.model, input.where ?? [], "findMany"), offset: input.offset ?? 0, limit: input.limit, ...(input.select === undefined || input.select.length === 0 ? {} : { select: input.select.map((field) => config.getFieldName({ model: input.model, field })) }), ...(input.sortBy === undefined ? {} : { sortBy: { ...input.sortBy, field: config.getFieldName({ model: input.model, field: input.sortBy.field }) } }), ...(input.join === undefined ? {} : { joins: joins(input.join) }) }, queryOptions());
      return [...records] as T[];
    },
    delete: async (input: DeleteInput) => { await ensure(); unwrap(await port.deleteOne({ model: input.model, predicates: where(input.model, input.where, "delete") }, mutationOptions())); },
    deleteMany: async (input: DeleteManyInput) => { await ensure(); return safeCount(unwrap(await port.deleteMany({ model: input.model, predicates: where(input.model, input.where, "deleteMany") }, mutationOptions())).count); },
    consumeOne: async <T>(input: ConsumeInput) => { await ensure(); return outputRecord(unwrap(await port.consumeMatching({ model: input.model, predicates: where(input.model, input.where, "consumeOne") }, mutationOptions())).record) as T | null; },
    incrementOne: async <T>(input: IncrementInput) => { await ensure(); return outputRecord(unwrap(await port.incrementOne({ model: input.model, predicates: where(input.model, input.where, "incrementOne"), increment: input.increment, ...(input.set === undefined ? {} : { set: jsonObject(input.set) }) }, mutationOptions())).record) as T | null; },
    count: async (input: CountInput) => { await ensure(); return safeCount(unwrap(await port.count({ model: input.model, predicates: where(input.model, input.where ?? [], "count") }, queryOptions())).count); },
  };
  return { adapter, ensure, index: namedIndex };
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
    findPage: (request, options) => operations.findPage(request, {
      ...options,
      idempotencyKey: options.idempotencyKey ?? `better-auth-query-${globalThis.crypto.randomUUID()}`,
    }),
    count: (request, options) => operations.count(request, {
      ...options,
      idempotencyKey: options.idempotencyKey ?? `better-auth-count-${globalThis.crypto.randomUUID()}`,
    }),
  };
}

function joins(input: JoinConfig): readonly AuthQueryJoin[] {
  return Object.entries(input).map(([outputField, value]) => ({
    model: outputField,
    outputField,
    fromField: value.on.from,
    toField: value.on.to,
    relation: value.relation === "one-to-one" ? "one-to-one" : "one-to-many",
    limit: value.limit ?? 100,
  }));
}

function unwrap(result: TerraScaleResult<AuthPersistenceResult>): AuthPersistenceResult {
  if (result.ok) return result.value;
  throw new TerraScaleAuthAdapterError(result.error.message, result.error.code, result.error);
}

function outputRecord(record: Record<string, unknown> | JsonObject | null, select?: readonly string[]): JsonObject | null {
  if (record === null) return null;
  if (select === undefined || select.length === 0) return jsonObject(record);
  return jsonObject(Object.fromEntries(select.flatMap((field) => field in record ? [[field, record[field]]] : [])));
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
  const result: { [key: string]: JsonValue } = {};
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
