import type { TerraScaleResult } from "./result.js";

export {
  AuthTransactionOutcomeUnknownError,
  AuthTransactionOperationLimitError,
  AuthTransactionPoisonedError,
  AuthTransactionRollbackError,
  runAuthTransaction,
} from "./auth-transaction.js";
export type {
  AuthTransactionMutationOptions,
  AuthTransactionCallbackOptions,
  AuthTransactionOperations,
  AuthTransactionQueryOptions,
} from "./auth-transaction.js";

export {
  AuthPersistenceClient,
  createAuthPersistenceClient,
} from "./auth-persistence.js";
export type {
  AuthConditionalUpdate,
  AuthConsume,
  AuthFindOneOptions,
  AuthIncrement,
  AuthPageQuery,
  AuthPersistencePredicate,
  AuthPersistenceResult,
  AuthPredicateOnly,
  AuthPredicateUpdate,
  AuthQueryOptions,
  AuthRequestOptions,
  AuthTransactionIdentity,
  AuthTransactionOptions,
  AuthMutationOptions,
  AuthUniqueIndex,
  AuthUniqueInsert,
  AuthQueryJoin,
} from "./auth-persistence.js";

export { createTerraScaleAuthAdapter } from "./better-auth-factory.js";
export type { TerraScaleAuthAdapterFactory } from "./better-auth-factory.js";

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

/** A named schema/index operation owned by the Database API. */
export type BetterAuthIndexInput = {
  readonly model: string;
  readonly fields: readonly string[];
  readonly unique?: boolean | undefined;
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
  /**
   * Ensure an auth index exists. This is optional while the producer's
   * generated contract is being rolled out; invoking the adapter operation
   * without it fails with `contract_gap` rather than falling back to SQLite.
   */
  readonly index?: ((input: BetterAuthIndexInput & BetterAuthMutationInput) => Promise<TerraScaleResult<void>>) | undefined;
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
  /** Number of additional attempts for documented transient mutations. */
  readonly maxRetryAttempts?: number | undefined;
  /** Delay before a retry; Retry-After from the producer takes precedence. */
  readonly retryDelayMs?: number | undefined;
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
  /** Explicit named index operation; never implemented with local storage. */
  readonly index: (input: BetterAuthIndexInput) => Promise<void>;
  readonly transaction: <T>(callback: (adapter: BetterAuthTransactionAdapter) => Promise<T>) => Promise<T>;
  readonly createSchema?: ((input: {
    readonly tables: unknown;
    readonly file?: string | undefined;
  }) => Promise<{ readonly code: string; readonly path: string }>) | undefined;
  readonly options: { readonly adapterConfig: { readonly adapterId: string } };
};

export type LocalBetterAuthHttpOptions = {
  readonly baseUrl: string;
  readonly fetch?: typeof fetch;
};

/** Development-only HTTP operation port for the loopback SQLite runtime. */
export function createLocalBetterAuthHttpOperations(
  options: LocalBetterAuthHttpOptions,
): BetterAuthDatabaseOperations {
  const url = new URL(options.baseUrl);
  if (!["127.0.0.1", "localhost", "::1"].includes(url.hostname) || url.protocol !== "http:") {
    throw new TypeError("The local Better Auth operation port must use loopback HTTP.");
  }
  if (process.env.NODE_ENV === "production" || process.env.CI) {
    throw new TypeError("The local Better Auth operation port is disabled outside development.");
  }
  const request = async <T>(action: string, input: unknown): Promise<T> => {
    const response = await (options.fetch ?? globalThis.fetch)(
      new URL(`/__terrascale/local/better-auth/${action}`, url),
      { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(input) },
    );
    if (!response.ok) throw new BetterAuthAdapterError(`Local Better Auth ${action} failed with HTTP ${response.status}.`, "local_transport", { status: response.status });
    return reviveDates(await response.json()) as T;
  };
  const operations: BetterAuthDatabaseOperations = {
    insertUnique: (input) => request("insertUnique", input),
    findOne: (input) => request("findOne", input),
    findPage: (input) => request("findPage", input),
    count: (input) => request("count", input),
    compareAndSwap: (input) => request("compareAndSwap", input),
    updateMany: (input) => request("updateMany", input),
    delete: (input) => request("delete", input),
    deleteMany: (input) => request("deleteMany", input),
    consumeOne: (input) => request("consumeOne", input),
    incrementOne: (input) => request("incrementOne", input),
    transaction: async () => {
      throw new BetterAuthAdapterError(
        "The local HTTP auth bridge cannot execute atomic transaction callbacks.",
        "transaction_unsupported",
        { atomicTransactions: false },
      );
    },
    migrate: (input) => request("migrate", input),
  };
  return operations;
}

function reviveDates(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(reviveDates);
  if (value !== null && typeof value === "object") {
    return Object.fromEntries(Object.entries(value).map(([key, entry]) => [key, reviveDates(entry)]));
  }
  if (typeof value === "string" && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}/u.test(value)) return new Date(value);
  return value;
}

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

/**
 * A transaction can be replayed by the producer after an uncertain commit.
 * Keep the generated keys by operation ordinal so replaying the callback does
 * not turn one logical mutation into several idempotency identities.
 */
type MutationKeyScope = {
  readonly keys: string[];
  cursor: number;
};

function createAdapter(
  operations: BetterAuthDatabaseOperations,
  options: BetterAuthAdapterOptions,
  transactionBudget?: TransactionBudget,
  keyScope?: MutationKeyScope,
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
  const maxRetryAttempts = options.maxRetryAttempts ?? 0;
  if (!Number.isSafeInteger(maxRetryAttempts) || maxRetryAttempts < 0 || maxRetryAttempts > 8) {
    throw new TypeError("maxRetryAttempts must be an integer between 0 and 8.");
  }
  const retryDelayMs = options.retryDelayMs ?? 250;
  if (!Number.isFinite(retryDelayMs) || retryDelayMs < 0 || retryDelayMs > 30_000) {
    throw new TypeError("retryDelayMs must be a finite number between 0 and 30000.");
  }

  const id = options.id ?? "terrascale-database-api";
  if (id.trim().length === 0) throw new TypeError("The Better Auth adapter id cannot be empty.");
  const key = options.idempotencyKeyFactory ?? (() => globalThis.crypto.randomUUID());
  const nextKey = (): string => {
    if (keyScope !== undefined) {
      const ordinal = keyScope.cursor++;
      const cached = keyScope.keys[ordinal];
      if (cached !== undefined) return cached;
      const generated = validateIdempotencyKey(key());
      keyScope.keys.push(generated);
      return generated;
    }
    return validateIdempotencyKey(key());
  };

  const invokeRead = async <T>(operation: () => Promise<TerraScaleResult<T>>): Promise<T> =>
    unwrap(await operation());

  const invokeMutation = async <T>(
    operation: () => Promise<TerraScaleResult<T>>,
    idempotencyKey: string,
  ): Promise<T> => {
    let attempt = 0;
    while (true) {
      let result: TerraScaleResult<T>;
      try {
        result = await operation();
      } catch (error) {
        if (!isRetryableThrownError(error, attempt, maxRetryAttempts)) throw error;
        await delayForRetry(retryDelayMs, undefined);
        attempt += 1;
        continue;
      }
      if (!isRetryableResult(result, attempt, maxRetryAttempts, idempotencyKey)) {
        return unwrap(result);
      }
      await delayForRetry(retryDelay(result, retryDelayMs, attempt), undefined);
      attempt += 1;
    }
  };

  const invokeMutationVoid = async (
    operation: () => Promise<TerraScaleResult<void>>,
    idempotencyKey: string,
  ): Promise<void> => {
    await invokeMutation(operation, idempotencyKey);
  };

  const indexOperation = operations.index;

  /* Keep validation in one place so a malformed factory fails before a
   * producer call and can never emit a credential-like key. */
  function validateIdempotencyKey(value: unknown): string {
    if (
      typeof value !== "string" ||
      value.trim().length === 0 ||
      value.length > 128 ||
      /[\u0000-\u001f\u007f]/u.test(value)
    ) {
      throw new TypeError("Better Auth idempotency keys must be 1-128 printable characters.");
    }
    return value.trim();
  }

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
      const idempotencyKey = nextKey();
      return (await invokeMutation(
        () => operations.insertUnique({
          model: input.model,
          data,
          ...(input.select === undefined ? {} : { select: input.select }),
          idempotencyKey,
        }),
        idempotencyKey,
      )) as R;
    },
    async findOne<T>(input: {
      readonly model: string;
      readonly where: readonly BetterAuthWhere[];
      readonly select?: readonly string[] | undefined;
      readonly join?: BetterAuthJoin | undefined;
    }) {
      consumeTransactionBudget(transactionBudget);
      requireModel(input.model);
      return (await invokeRead(() => operations.findOne({
          model: input.model,
          where: input.where,
          ...(input.select === undefined ? {} : { select: input.select }),
          ...(input.join === undefined ? {} : { join: boundedJoin(input.join, defaultPageSize) }),
        }))) as T | null;
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
      return [...(await invokeRead(() => operations.findPage({
          model: input.model,
          where: input.where ?? [],
          limit: boundedPageSize(input.limit, defaultPageSize),
          ...(input.select === undefined ? {} : { select: input.select }),
          ...(input.sortBy === undefined ? {} : { sortBy: input.sortBy }),
          ...(input.offset === undefined ? {} : { offset: boundedOffset(input.offset) }),
          ...(input.join === undefined ? {} : { join: boundedJoin(input.join, defaultPageSize) }),
        })))] as T[];
    },
    async count(input: {
      readonly model: string;
      readonly where?: readonly BetterAuthWhere[] | undefined;
    }) {
      consumeTransactionBudget(transactionBudget);
      requireModel(input.model);
      return invokeRead(() => operations.count({ model: input.model, where: input.where ?? [] }));
    },
    async update<T>(input: {
      readonly model: string;
      readonly where: readonly BetterAuthWhere[];
      readonly update: BetterAuthRecord;
    }) {
      consumeTransactionBudget(transactionBudget);
      requireModel(input.model);
      const idempotencyKey = nextKey();
      return (await invokeMutation(
        () => operations.compareAndSwap({
          model: input.model,
          where: input.where,
          update: input.update,
          idempotencyKey,
        }),
        idempotencyKey,
      )) as T | null;
    },
    async updateMany(input: {
      readonly model: string;
      readonly where: readonly BetterAuthWhere[];
      readonly update: BetterAuthRecord;
    }) {
      consumeTransactionBudget(transactionBudget);
      requireModel(input.model);
      const idempotencyKey = nextKey();
      return invokeMutation(
        () => operations.updateMany({
          model: input.model,
          where: input.where,
          update: input.update,
          idempotencyKey,
        }),
        idempotencyKey,
      );
    },
    async delete(input: {
      readonly model: string;
      readonly where: readonly BetterAuthWhere[];
    }) {
      consumeTransactionBudget(transactionBudget);
      requireModel(input.model);
      const idempotencyKey = nextKey();
      await invokeMutationVoid(
        () => operations.delete({ model: input.model, where: input.where, idempotencyKey }),
        idempotencyKey,
      );
    },
    async deleteMany(input: {
      readonly model: string;
      readonly where: readonly BetterAuthWhere[];
    }) {
      consumeTransactionBudget(transactionBudget);
      requireModel(input.model);
      const idempotencyKey = nextKey();
      return invokeMutation(
        () => operations.deleteMany({
          model: input.model,
          where: input.where,
          idempotencyKey,
        }),
        idempotencyKey,
      );
    },
    async consumeOne<T>(input: {
      readonly model: string;
      readonly where: readonly BetterAuthWhere[];
    }) {
      consumeTransactionBudget(transactionBudget);
      requireModel(input.model);
      const idempotencyKey = nextKey();
      return (await invokeMutation(
        () => operations.consumeOne({
          model: input.model,
          where: input.where,
          idempotencyKey,
        }),
        idempotencyKey,
      )) as T | null;
    },
    async incrementOne<T>(input: {
      readonly model: string;
      readonly where: readonly BetterAuthWhere[];
      readonly increment: Record<string, number>;
      readonly set?: Record<string, unknown> | undefined;
    }) {
      consumeTransactionBudget(transactionBudget);
      requireModel(input.model);
      const idempotencyKey = nextKey();
      return (await invokeMutation(
        () => operations.incrementOne({
          model: input.model,
          where: input.where,
          increment: input.increment,
          ...(input.set === undefined ? {} : { set: input.set }),
          idempotencyKey,
        }),
        idempotencyKey,
      )) as T | null;
    },
    async index(input: BetterAuthIndexInput): Promise<void> {
      requireModel(input.model);
      if (indexOperation === undefined) {
        throw new BetterAuthAdapterError(
          "The published Database API contract does not expose the Better Auth index operation.",
          "contract_gap",
          { missingOperations: ["index"] },
        );
      }
      const fields = [...input.fields];
      if (fields.length === 0 || fields.length > 64 || fields.some(field => typeof field !== "string" || field.trim().length === 0)) {
        throw new TypeError("Better Auth index fields must contain between 1 and 64 nonblank names.");
      }
      const idempotencyKey = nextKey();
      await invokeMutationVoid(
        () => indexOperation({ model: input.model, fields, ...(input.unique === undefined ? {} : { unique: input.unique }), idempotencyKey }),
        idempotencyKey,
      );
    },
    async transaction(callback) {
      if (typeof callback !== "function") {
        throw new TypeError("Better Auth transaction callback must be a function.");
      }
      const budget = transactionBudget ?? {
        limit: maxTransactionOperations,
        used: 0,
      };
      const mutationKeys = keyScope ?? { keys: [], cursor: 0 };
      return operations.transaction(async (transactionOperations) =>
        callback(
          createAdapter(
            transactionOperations as BetterAuthDatabaseOperations,
            options,
            budget,
            mutationKeys,
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

const betterAuthRetryableStatuses = new Set([408, 429, 500, 502, 503, 504]);

function isRetryableThrownError(error: unknown, attempt: number, maxAttempts: number): boolean {
  if (attempt >= maxAttempts || !error || typeof error !== "object") return false;
  const status = (error as { readonly status?: unknown }).status;
  return typeof status === "number" && betterAuthRetryableStatuses.has(status);
}

function isRetryableResult<T>(
  result: TerraScaleResult<T>,
  attempt: number,
  maxAttempts: number,
  idempotencyKey: string,
): boolean {
  if (attempt >= maxAttempts || idempotencyKey.length === 0 || result.ok) return false;
  return result.status !== undefined && betterAuthRetryableStatuses.has(result.status);
}

function retryDelay<T>(result: TerraScaleResult<T>, baseDelayMs: number, attempt: number): number {
  if (!result.ok) {
    const retryAfter = result.headers?.get("retry-after");
    if (retryAfter !== null && retryAfter !== undefined) {
      const seconds = Number(retryAfter);
      if (Number.isFinite(seconds) && seconds >= 0) return Math.min(seconds * 1_000, 30_000);
      const date = Date.parse(retryAfter);
      if (Number.isFinite(date)) return Math.min(Math.max(0, date - Date.now()), 30_000);
    }
  }
  return Math.min(baseDelayMs * (2 ** attempt), 30_000);
}

function delayForRetry(milliseconds: number, _signal?: AbortSignal): Promise<void> {
  if (milliseconds <= 0) return Promise.resolve();
  return new Promise(resolve => globalThis.setTimeout(resolve, milliseconds));
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
