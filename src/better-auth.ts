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
  readonly idempotencyKey: string;
};

/**
 * Named Database API operations required by Better Auth.
 *
 * This is deliberately a port rather than an HTTP client. The Database API
 * owns the versioned wire contract; callers must provide its generated,
 * typed implementation. In particular, an arbitrary request function cannot
 * satisfy this interface.
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
    callback: (operations: BetterAuthDatabaseOperations) => Promise<T>,
  ) => Promise<T>;
  readonly migrate?: ((input: {
    readonly tables: unknown;
    readonly file?: string | undefined;
  }) => Promise<{ readonly code: string; readonly path: string }>) | undefined;
};

export type BetterAuthAdapterOptions = {
  /** Stable adapter id shown in Better Auth diagnostics. */
  readonly id?: string | undefined;
  /** Bounds Better Auth pagination even when it omits a limit. */
  readonly defaultPageSize?: number | undefined;
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
    readonly limit: number;
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
  readonly transaction: <T>(callback: (adapter: BetterAuthDatabaseAdapter) => Promise<T>) => Promise<T>;
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
  const defaultPageSize = options.defaultPageSize ?? 100;
  if (!Number.isSafeInteger(defaultPageSize) || defaultPageSize < 1 || defaultPageSize > 1_000) {
    throw new TypeError("defaultPageSize must be an integer between 1 and 1000.");
  }

  const id = options.id ?? "terrascale-database-api";
  if (id.trim().length === 0) throw new TypeError("The Better Auth adapter id cannot be empty.");
  const key = options.idempotencyKeyFactory ?? (() => randomUUID());
  const nextKey = (): string => {
    const value = key();
    if (value.trim().length === 0 || value.length > 128 || /[\u0000-\u001f\u007f]/u.test(value)) {
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
      return unwrap(
        await operations.findOne({
          model: input.model,
          where: input.where,
          ...(input.select === undefined ? {} : { select: input.select }),
          ...(input.join === undefined ? {} : { join: input.join }),
        }),
      ) as T | null;
    },
    async findMany<T>(input: {
      readonly model: string;
      readonly where?: readonly BetterAuthWhere[] | undefined;
      readonly limit: number;
      readonly select?: readonly string[] | undefined;
      readonly sortBy?: { readonly field: string; readonly direction: "asc" | "desc" } | undefined;
      readonly offset?: number | undefined;
      readonly join?: BetterAuthJoin | undefined;
    }) {
      return [...unwrap(
        await operations.findPage({
          model: input.model,
          where: input.where ?? [],
          limit: boundedPageSize(input.limit, defaultPageSize),
          ...(input.select === undefined ? {} : { select: input.select }),
          ...(input.sortBy === undefined ? {} : { sortBy: input.sortBy }),
          ...(input.offset === undefined ? {} : { offset: input.offset }),
          ...(input.join === undefined ? {} : { join: input.join }),
        }),
      )] as T[];
    },
    async count(input: {
      readonly model: string;
      readonly where?: readonly BetterAuthWhere[] | undefined;
    }) {
      return unwrap(await operations.count({ model: input.model, where: input.where ?? [] }));
    },
    async update<T>(input: {
      readonly model: string;
      readonly where: readonly BetterAuthWhere[];
      readonly update: BetterAuthRecord;
    }) {
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
      unwrap(
        await operations.delete({ model: input.model, where: input.where, idempotencyKey: nextKey() }),
      );
    },
    async deleteMany(input: {
      readonly model: string;
      readonly where: readonly BetterAuthWhere[];
    }) {
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
      return operations.transaction(async (transactionOperations) =>
        callback(createTerraScaleBetterAuthAdapter(transactionOperations, options)),
      );
    },
    ...(operations.migrate === undefined
      ? {}
      : {
          createSchema: operations.migrate,
        }),
    options: { adapterConfig: { adapterId: id } },
  };

  return adapter;
}

function boundedPageSize(value: number, fallback: number): number {
  if (!Number.isSafeInteger(value) || value < 1) return fallback;
  return Math.min(value, 1_000);
}

function unwrap<T>(result: TerraScaleResult<T>): T {
  if (result.ok) return result.value;
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
