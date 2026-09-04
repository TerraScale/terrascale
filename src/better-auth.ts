import { randomUUID } from "node:crypto";

import type { TerraScaleResult } from "./result.js";

/** The comparison operators understood by Better Auth's database adapter. */
export type BetterAuthWhere = {
  readonly field: string;
  readonly value: string | number | boolean | readonly (string | number)[] | Date | null;
  readonly operator?:
    | "eq"
    | "ne"
    | "lt"
    | "lte"
    | "gt"
    | "gte"
    | "in"
    | "not_in"
    | "contains"
    | "starts_with"
    | "ends_with";
  readonly connector?: "AND" | "OR";
  readonly mode?: "sensitive" | "insensitive";
};

export type BetterAuthJoin = Record<
  string,
  boolean | { readonly limit?: number | undefined }
>;

type BetterAuthRecord = Record<string, unknown>;

type BetterAuthOperationInput = {
  readonly model: string;
  readonly where: readonly BetterAuthWhere[];
  readonly select?: readonly string[] | undefined;
  readonly join?: BetterAuthJoin | undefined;
};

type BetterAuthPageInput = BetterAuthOperationInput & {
  readonly limit: number;
  readonly offset?: number | undefined;
  readonly sortBy?: {
    readonly field: string;
    readonly direction: "asc" | "desc";
  } | undefined;
};

type BetterAuthMutationInput = {
  readonly model: string;
  /** The operation implementation reuses this for a safe retry/replay. */
  readonly idempotencyKey: string;
};

/**
 * Named Database API operations required by Better Auth.
 *
 * This is deliberately a port rather than an HTTP client. The Database API
 * owns the versioned wire contract; callers must provide its generated,
 * typed implementation. In particular, an arbitrary request function cannot
 * satisfy this interface. The currently published TerraBase contract does
 * not publish these auth operations; until it does, construction with a
 * complete generated port is the only supported path and a missing method
 * fails closed with `contract_gap`.
 */
export type BetterAuthDatabaseOperations = {
  readonly insertUnique: <T extends BetterAuthRecord>(input: BetterAuthMutationInput & {
    readonly data: T;
    readonly select?: readonly string[] | undefined;
  }) => Promise<TerraScaleResult<T>>;
  readonly findOne: <T extends BetterAuthRecord>(
    input: BetterAuthOperationInput,
  ) => Promise<TerraScaleResult<T | null>>;
  readonly findPage: <T extends BetterAuthRecord>(
    input: BetterAuthPageInput,
  ) => Promise<TerraScaleResult<readonly T[]>>;
  readonly count: (
    input: Omit<BetterAuthOperationInput, "select" | "join">,
  ) => Promise<TerraScaleResult<number>>;
  readonly compareAndSwap: <T extends BetterAuthRecord>(input: BetterAuthMutationInput & {
    readonly where: readonly BetterAuthWhere[];
    readonly update: BetterAuthRecord;
  }) => Promise<TerraScaleResult<T | null>>;
  readonly updateMany: (
    input: BetterAuthMutationInput & {
      readonly where: readonly BetterAuthWhere[];
      readonly update: BetterAuthRecord;
    },
  ) => Promise<TerraScaleResult<number>>;
  readonly delete: (
    input: BetterAuthMutationInput & { readonly where: readonly BetterAuthWhere[] },
  ) => Promise<TerraScaleResult<void>>;
  readonly deleteMany: (
    input: BetterAuthMutationInput & { readonly where: readonly BetterAuthWhere[] },
  ) => Promise<TerraScaleResult<number>>;
  readonly consumeOne: <T extends BetterAuthRecord>(
    input: BetterAuthMutationInput & { readonly where: readonly BetterAuthWhere[] },
  ) => Promise<TerraScaleResult<T | null>>;
  readonly incrementOne: <T extends BetterAuthRecord>(input: BetterAuthMutationInput & {
    readonly where: readonly BetterAuthWhere[];
    readonly increment: Record<string, number>;
    readonly set?: Record<string, unknown> | undefined;
  }) => Promise<TerraScaleResult<T | null>>;
  readonly transaction: <T>(
    callback: (operations: Omit<BetterAuthDatabaseOperations, "transaction">) => Promise<T>,
  ) => Promise<T>;
  readonly migrate?: ((input: {
    readonly tables: unknown;
    readonly file?: string | undefined;
  }) => Promise<{ readonly code: string; readonly path: string }>) | undefined;
};

export type BetterAuthTransactionAdapter = Omit<BetterAuthDatabaseAdapter, "transaction">;

export type BetterAuthAdapterOptions = {
  /** Stable adapter id shown in Better Auth diagnostics. */
  readonly id?: string | undefined;
  /** Bounds Better Auth pagination even when it omits a limit. */
  readonly defaultPageSize?: number | undefined;
  /** Bounds the number of adapter operations in one transaction callback. */
  readonly maxTransactionOperations?: number | undefined;
  /** Supplies stable keys for an outer retry; defaults to a per-call UUID. */
  readonly idempotencyKeyFactory?: (() => string) | undefined;
};

export type BetterAuthDatabaseAdapter = {
  readonly id: string;
  readonly create: <T extends BetterAuthRecord, R = T>(input: {
    readonly model: string;
    readonly data: Omit<T, "id">;
    readonly select?: readonly string[] | undefined;
    readonly forceAllowId?: boolean | undefined;
  }) => Promise<R>;
  readonly findOne: <T>(input: {
    readonly model: string;
    readonly where: readonly BetterAuthWhere[];
    readonly select?: readonly string[] | undefined;
    readonly join?: BetterAuthJoin | undefined;
  }) => Promise<T | null>;
  readonly findMany: <T>(input: {
    readonly model: string;
    readonly where?: readonly BetterAuthWhere[] | undefined;
    readonly limit?: number | undefined;
    readonly select?: readonly string[] | undefined;
    readonly sortBy?: { readonly field: string; readonly direction: "asc" | "desc" } | undefined;
    readonly offset?: number | undefined;
    readonly join?: BetterAuthJoin | undefined;
  }) => Promise<T[]>;
  readonly count: (input: {
    readonly model: string;
    readonly where?: readonly BetterAuthWhere[] | undefined;
  }) => Promise<number>;
  readonly update: <T>(input: {
    readonly model: string;
    readonly where: readonly BetterAuthWhere[];
    readonly update: BetterAuthRecord;
  }) => Promise<T | null>;
  readonly updateMany: (input: {
    readonly model: string;
    readonly where: readonly BetterAuthWhere[];
    readonly update: BetterAuthRecord;
  }) => Promise<number>;
  readonly delete: (input: {
    readonly model: string;
    readonly where: readonly BetterAuthWhere[];
  }) => Promise<void>;
  readonly deleteMany: (input: {
    readonly model: string;
    readonly where: readonly BetterAuthWhere[];
  }) => Promise<number>;
  readonly consumeOne: <T>(input: {
    readonly model: string;
    readonly where: readonly BetterAuthWhere[];
  }) => Promise<T | null>;
  readonly incrementOne: <T>(input: {
    readonly model: string;
    readonly where: readonly BetterAuthWhere[];
    readonly increment: Record<string, number>;
    readonly set?: Record<string, unknown> | undefined;
  }) => Promise<T | null>;
  readonly transaction: <T>(callback: (adapter: BetterAuthTransactionAdapter) => Promise<T>) => Promise<T>;
  readonly createSchema?: ((input: {
    readonly tables: unknown;
    readonly file?: string | undefined;
  }) => Promise<{ readonly code: string; readonly path: string }>) | undefined;
  readonly options: { readonly adapterConfig: { readonly adapterId: string } };
};

/**
 * Creates the server-only Better Auth adapter.
 *
 * The returned object is structurally compatible with Better Auth's
 * `database` adapter. Keep this module behind the `better-auth` package
 * export: importing it in browser code is intentionally unsupported.
 */
export function createTerraScaleBetterAuthAdapter(
  operations: BetterAuthDatabaseOperations,
  options: BetterAuthAdapterOptions = {},
): BetterAuthDatabaseAdapter {
  return createAdapter(operations, options);
}

type TransactionBudget = {
  readonly limit: number;
  used: number;
};

function createAdapter(
  operations: BetterAuthDatabaseOperations,
  options: BetterAuthAdapterOptions,
  transactionBudget?: TransactionBudget,
): BetterAuthDatabaseAdapter {
  validateOperations(operations, transactionBudget === undefined);
  const defaultPageSize = options.defaultPageSize ?? 100;
  if (!Number.isSafeInteger(defaultPageSize) || defaultPageSize < 1 || defaultPageSize > 1_000) {
    throw new TypeError("defaultPageSize must be an integer between 1 and 1000.");
  }
  const maxTransactionOperations = options.maxTransactionOperations ?? 100;
  if (
    !Number.isSafeInteger(maxTransactionOperations) ||
    maxTransactionOperations < 1 ||
    maxTransactionOperations > 1_000
  ) {
    throw new TypeError("maxTransactionOperations must be an integer between 1 and 1000.");
  }

  const id = options.id ?? "terrascale-database-api";
  if (id.trim().length === 0) throw new TypeError("The Better Auth adapter id cannot be empty.");
  const key = options.idempotencyKeyFactory ?? (() => randomUUID());
  const nextKey = (): string => {
    const value = key();
    if (
      typeof value !== "string" ||
      value.trim().length === 0 ||
      value.length > 128 ||
      /[\u0000-\u001f\u007f]/u.test(value)
    ) {
      throw new TypeError("Better Auth idempotency keys must be 1-128 printable characters.");
    }
    return value;
  };

  const adapter: BetterAuthDatabaseAdapter = {
    id,
    async create<T extends BetterAuthRecord, R = T>(input: {
      readonly model: string;
      readonly data: Omit<T, "id">;
      readonly select?: readonly string[] | undefined;
      readonly forceAllowId?: boolean | undefined;
    }) {
      consumeTransactionBudget(transactionBudget);
      requireModel(input.model);
      const data = { ...input.data } as BetterAuthRecord;
      if (input.forceAllowId !== true) delete data.id;
      return unwrap(
        await operations.insertUnique({
          model: input.model,
          data,
          ...(input.select === undefined ? {} : { select: input.select }),
          idempotencyKey: nextKey(),
        }),
      ) as R;
    },
    async findOne<T>(input: {
      readonly model: string;
      readonly where: readonly BetterAuthWhere[];
      readonly select?: readonly string[] | undefined;
      readonly join?: BetterAuthJoin | undefined;
    }) {
      consumeTransactionBudget(transactionBudget);
      requireModel(input.model);
      return unwrap(
        await operations.findOne({
          model: input.model,
          where: input.where,
          ...(input.select === undefined ? {} : { select: input.select }),
          ...(input.join === undefined ? {} : { join: boundedJoin(input.join, defaultPageSize) }),
        }),
      ) as T | null;
    },
    async findMany<T>(input: {
      readonly model: string;
      readonly where?: readonly BetterAuthWhere[] | undefined;
      readonly limit?: number | undefined;
      readonly select?: readonly string[] | undefined;
      readonly sortBy?: { readonly field: string; readonly direction: "asc" | "desc" } | undefined;
      readonly offset?: number | undefined;
      readonly join?: BetterAuthJoin | undefined;
    }) {
      consumeTransactionBudget(transactionBudget);
      requireModel(input.model);
      return [...unwrap(
        await operations.findPage({
          model: input.model,
          where: input.where ?? [],
          limit: boundedPageSize(input.limit, defaultPageSize),
          ...(input.select === undefined ? {} : { select: input.select }),
          ...(input.sortBy === undefined ? {} : { sortBy: input.sortBy }),
          ...(input.offset === undefined ? {} : { offset: boundedOffset(input.offset) }),
          ...(input.join === undefined ? {} : { join: boundedJoin(input.join, defaultPageSize) }),
        }),
      )] as T[];
    },
    async count(input: {
      readonly model: string;
      readonly where?: readonly BetterAuthWhere[] | undefined;
    }) {
      consumeTransactionBudget(transactionBudget);
      requireModel(input.model);
      return unwrap(await operations.count({ model: input.model, where: input.where ?? [] }));
    },
    async update<T>(input: {
      readonly model: string;
      readonly where: readonly BetterAuthWhere[];
      readonly update: BetterAuthRecord;
    }) {
      consumeTransactionBudget(transactionBudget);
      requireModel(input.model);
      return unwrap(
        await operations.compareAndSwap({
          model: input.model,
          where: input.where,
          update: input.update,
          idempotencyKey: nextKey(),
        }),
      ) as T | null;
    },
    async updateMany(input: {
      readonly model: string;
      readonly where: readonly BetterAuthWhere[];
      readonly update: BetterAuthRecord;
    }) {
      consumeTransactionBudget(transactionBudget);
      requireModel(input.model);
      return unwrap(
        await operations.updateMany({
          model: input.model,
          where: input.where,
          update: input.update,
          idempotencyKey: nextKey(),
        }),
      );
    },
    async delete(input: {
      readonly model: string;
      readonly where: readonly BetterAuthWhere[];
    }) {
      consumeTransactionBudget(transactionBudget);
      requireModel(input.model);
      unwrap(
        await operations.delete({ model: input.model, where: input.where, idempotencyKey: nextKey() }),
      );
    },
    async deleteMany(input: {
      readonly model: string;
      readonly where: readonly BetterAuthWhere[];
    }) {
      consumeTransactionBudget(transactionBudget);
      requireModel(input.model);
      return unwrap(
        await operations.deleteMany({
          model: input.model,
          where: input.where,
          idempotencyKey: nextKey(),
        }),
      );
    },
    async consumeOne<T>(input: {
      readonly model: string;
      readonly where: readonly BetterAuthWhere[];
    }) {
      consumeTransactionBudget(transactionBudget);
      requireModel(input.model);
      return unwrap(
        await operations.consumeOne({
          model: input.model,
          where: input.where,
          idempotencyKey: nextKey(),
        }),
      ) as T | null;
    },
    async incrementOne<T>(input: {
      readonly model: string;
      readonly where: readonly BetterAuthWhere[];
      readonly increment: Record<string, number>;
      readonly set?: Record<string, unknown> | undefined;
    }) {
      consumeTransactionBudget(transactionBudget);
      requireModel(input.model);
      return unwrap(
        await operations.incrementOne({
          model: input.model,
          where: input.where,
          increment: input.increment,
          ...(input.set === undefined ? {} : { set: input.set }),
          idempotencyKey: nextKey(),
        }),
      ) as T | null;
    },
    async transaction(callback) {
      if (typeof callback !== "function") {
        throw new TypeError("Better Auth transaction callback must be a function.");
      }
      const budget = transactionBudget ?? {
        limit: maxTransactionOperations,
        used: 0,
      };
      return operations.transaction(async (transactionOperations) =>
        callback(
          createAdapter(
            transactionOperations as BetterAuthDatabaseOperations,
            options,
            budget,
          ) as BetterAuthTransactionAdapter,
        ),
      );
    },
    ...(operations.migrate === undefined
      ? {}
      : {
          createSchema: async (input: { readonly tables: unknown; readonly file?: string | undefined }) => {
            const result = await operations.migrate!(input);
            if (
              result === null ||
              typeof result !== "object" ||
              typeof result.code !== "string" ||
              typeof result.path !== "string" ||
              result.code.length > 1_048_576 ||
              result.path.length === 0 ||
              result.path.length > 4_096 ||
              /[\u0000-\u001f\u007f]/u.test(result.path)
            ) {
              throw new BetterAuthAdapterError(
                "The Better Auth schema migration operation returned an invalid result.",
                "contract_invalid_response",
                result,
              );
            }
            return result;
          },
        }),
    options: { adapterConfig: { adapterId: id } },
  };

  return adapter;
}

function validateOperations(
  operations: BetterAuthDatabaseOperations,
  requireTransaction: boolean,
): void {
  if (operations === null || typeof operations !== "object") {
    throw new BetterAuthAdapterError(
      "The Better Auth adapter requires the published Database API operation port.",
      "contract_gap",
      { missingOperations: ["all"] },
    );
  }
  const required = [
    "insertUnique",
    "findOne",
    "findPage",
    "count",
    "compareAndSwap",
    "updateMany",
    "delete",
    "deleteMany",
    "consumeOne",
    "incrementOne",
  ] as const;
  const missing: string[] = required.filter((name) => typeof operations[name] !== "function");
  if (requireTransaction && typeof operations.transaction !== "function") {
    missing.push("transaction");
  }
  if (missing.length > 0) {
    throw new BetterAuthAdapterError(
      `The published Database API contract is missing Better Auth operations: ${missing.join(", ")}.`,
      "contract_gap",
      { missingOperations: missing },
    );
  }
}

function requireModel(model: string): void {
  if (
    typeof model !== "string" ||
    model.trim().length === 0 ||
    model.length > 128 ||
    /[\u0000-\u001f\u007f]/u.test(model)
  ) {
    throw new TypeError("Better Auth model names must be 1-128 printable characters.");
  }
}

function consumeTransactionBudget(budget: TransactionBudget | undefined): void {
  if (budget === undefined) return;
  budget.used += 1;
  if (budget.used > budget.limit) {
    throw new BetterAuthAdapterError(
      `The Better Auth transaction exceeds its ${budget.limit}-operation bound.`,
      "transaction_bound_exceeded",
      { limit: budget.limit },
    );
  }
}

function boundedPageSize(value: number | undefined, fallback: number): number {
  if (value === undefined || !Number.isSafeInteger(value) || value < 1) return fallback;
  return Math.min(value, 1_000);
}

function boundedOffset(value: number): number {
  if (!Number.isSafeInteger(value) || value < 0 || value > 1_000_000) {
    throw new TypeError("Better Auth pagination offsets must be integers between 0 and 1000000.");
  }
  return value;
}

function boundedJoin(join: BetterAuthJoin, fallback: number): BetterAuthJoin {
  return Object.fromEntries(
    Object.entries(join).map(([model, option]) => [
      model,
      typeof option === "boolean"
        ? option
        : {
            ...option,
            ...(option.limit === undefined
              ? {}
              : { limit: boundedPageSize(option.limit, fallback) }),
          },
    ]),
  );
}

function unwrap<T>(result: TerraScaleResult<T>): T {
  if (result === null || typeof result !== "object" || typeof result.ok !== "boolean") {
    throw new BetterAuthAdapterError(
      "The Database API operation returned an invalid result.",
      "contract_invalid_response",
      result,
    );
  }
  if (result.ok) {
    if (!Number.isInteger(result.status) || result.status < 200 || result.status > 299) {
      throw new BetterAuthAdapterError(
        "The Database API operation returned an invalid success status.",
        "contract_invalid_response",
        result,
      );
    }
    return result.value;
  }
  if (
    result.error === null ||
    typeof result.error !== "object" ||
    typeof result.error.message !== "string" ||
    result.error.message.length === 0 ||
    typeof result.error.code !== "string" ||
    result.error.code.trim().length === 0
  ) {
    throw new BetterAuthAdapterError(
      "The Database API operation returned an invalid error.",
      "contract_invalid_response",
      result,
    );
  }
  throw new BetterAuthAdapterError(result.error.message, result.error.code, result.error);
}

export class BetterAuthAdapterError extends Error {
  readonly code: string;
  readonly details: unknown;

  constructor(message: string, code: string, details: unknown) {
    super(message);
    this.name = "BetterAuthAdapterError";
    this.code = code;
    this.details = details;
  }
}
