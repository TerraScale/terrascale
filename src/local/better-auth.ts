import { randomUUID } from "node:crypto";
import {
  createAdapterFactory,
  type CleanedWhere,
  type CustomAdapter,
  type DBAdapter,
  type JoinConfig,
} from "@better-auth/core/db/adapter";
import type { BetterAuthOptions } from "@better-auth/core";
import type { TerraScaleSqliteStore } from "./store.js";

type Row = Record<string, unknown>;
type FactoryConfig = Parameters<
  NonNullable<Parameters<typeof createAdapterFactory>[0]["adapter"]>
>[0];
export type LocalBetterAuthAdapterFactory = (
  options: BetterAuthOptions,
) => DBAdapter<BetterAuthOptions>;

/** Better Auth 1.7.2 over explicitly enabled development SQLite only. */
export function createLocalBetterAuthAdapter(
  store: TerraScaleSqliteStore,
): LocalBetterAuthAdapterFactory {
  function make(
    options: BetterAuthOptions,
    scoped: boolean,
  ): DBAdapter<BetterAuthOptions> {
    const factory = createAdapterFactory<BetterAuthOptions>({
      config: {
        adapterId: "terrascale-local-sqlite",
        supportsDates: false,
        supportsJSON: true,
        supportsArrays: true,
        supportsBooleans: true,
        supportsNumericIds: false,
        supportsUUIDs: true,
        transaction: async (callback) => {
          if (scoped)
            throw new Error("Nested local auth transactions are unavailable.");
          return store.transactionAsync(async () =>
            serializedCallback(make(options, true), callback),
          );
        },
      },
      adapter: (config) => customAdapter(store, config, scoped),
    });
    return factory(options);
  }
  return (options) => make(options, false);
}

function customAdapter(
  store: TerraScaleSqliteStore,
  config: FactoryConfig,
  scoped: boolean,
): CustomAdapter {
  const rows = (model: string): Row[] => store.list<Row>("local-auth", model);
  const where = (
    model: string,
    clauses: readonly CleanedWhere[],
  ): readonly CleanedWhere[] =>
    clauses.map((clause) => ({
      ...clause,
      field: config.getFieldName({ model, field: clause.field }),
    }));
  const find = (model: string, clauses: readonly CleanedWhere[] = []): Row[] =>
    rows(model).filter((row) => matches(row, where(model, clauses)));
  const indexes = (model: string): readonly (readonly string[])[] =>
    store.getMeta<readonly (readonly string[])[]>(
      `local-auth-indexes:${model}`,
    ) ?? [["id"]];
  const installSchema = (): void => {
    for (const [model, table] of Object.entries(config.schema)) {
      const fields = [
        ["id"],
        ...Object.entries(table.fields)
          .filter(([, field]) => field.unique === true)
          .map(([field]) => [config.getFieldName({ model, field })]),
        ...(table.indexes ?? [])
          .filter((index) => index.unique === true)
          .map((index) =>
            index.fields.map((field) => config.getFieldName({ model, field })),
          ),
      ];
      const name = config.getModelName(model);
      const prior = indexes(name);
      const merged = [...prior];
      for (const index of fields)
        if (
          !merged.some(
            (value) => JSON.stringify(value) === JSON.stringify(index),
          )
        )
          merged.push(index);
      for (const row of rows(name)) ensureUnique(name, row, merged);
      store.setMeta(`local-auth-indexes:${name}`, merged);
    }
  };
  const ensureUnique = (
    model: string,
    candidate: Row,
    constraints = indexes(model),
  ): void => {
    for (const other of rows(model)) {
      if (other.id === candidate.id) continue;
      for (const fields of constraints) {
        if (
          fields.every(
            (field) =>
              candidate[field] !== null &&
              candidate[field] !== undefined &&
              equal(candidate[field], other[field]),
          )
        ) {
          throw new LocalAuthConstraintError();
        }
      }
    }
  };
  const save = (model: string, input: Row, creating = false): Row => {
    const row = jsonRow(input);
    const id =
      typeof row.id === "string" && row.id.length > 0 ? row.id : randomUUID();
    const candidate = { ...row, id };
    if (creating && store.get("local-auth", model, id) !== undefined)
      throw new LocalAuthConstraintError();
    ensureUnique(model, candidate);
    store.put("local-auth", model, id, candidate);
    return candidate;
  };
  const run = async <T>(work: () => T): Promise<T> => {
    const operation = () => {
      installSchema();
      return work();
    };
    return scoped
      ? operation()
      : store.exclusive(() => store.transaction(operation));
  };
  const page = (
    model: string,
    selected: readonly Row[],
    select: readonly string[] | undefined,
    join: JoinConfig | undefined,
  ): Row[] =>
    selected.map((row) => {
      const result =
        select === undefined || select.length === 0
          ? { ...row }
          : Object.fromEntries(
              select
                .map((field) => config.getFieldName({ model, field }))
                .filter((field) => Object.hasOwn(row, field))
                .map((field) => [field, row[field]]),
            );
      for (const [output, relation] of Object.entries(join ?? {})) {
        const related = rows(config.getModelName(output)).filter((candidate) =>
          equal(candidate[relation.on.to], row[relation.on.from]),
        );
        result[output] =
          relation.relation === "one-to-one"
            ? (related[0] ?? null)
            : related.slice(0, bounded(relation.limit ?? 100, 1000));
      }
      return result;
    });
  return {
    create: async <T extends Record<string, any>>(
      input: Parameters<CustomAdapter["create"]>[0] & { data: T },
    ) => run(() => save(input.model, input.data, true) as T),
    update: async <T>(
      input: Parameters<CustomAdapter["update"]>[0] & { update: T },
    ) =>
      run(() => {
        const row = find(input.model, input.where)[0];
        return row === undefined
          ? null
          : save(input.model, { ...row, ...jsonRow(input.update), id: row.id });
      }) as Promise<T | null>,
    updateMany: async (input) =>
      run(() => {
        const selected = find(input.model, input.where);
        for (const row of selected)
          save(input.model, { ...row, ...input.update, id: row.id });
        return selected.length;
      }),
    findOne: async <T>(input: Parameters<CustomAdapter["findOne"]>[0]) =>
      run(
        () =>
          page(
            input.model,
            find(input.model, input.where).slice(0, 1),
            input.select,
            input.join,
          )[0] ?? null,
      ) as Promise<T | null>,
    findMany: async <T>(input: Parameters<CustomAdapter["findMany"]>[0]) =>
      run(() => {
        let selected = find(input.model, input.where);
        if (input.sortBy !== undefined) {
          const field = config.getFieldName({
            model: input.model,
            field: input.sortBy.field,
          });
          const direction = input.sortBy.direction === "desc" ? -1 : 1;
          selected = selected.sort(
            (left, right) =>
              compare(left[field], right[field]) * direction ||
              String(left.id).localeCompare(String(right.id)),
          );
        }
        const offset = bounded(input.offset ?? 0, 1_000_000);
        return page(
          input.model,
          selected.slice(offset, offset + bounded(input.limit, 1000)),
          input.select,
          input.join,
        );
      }) as Promise<T[]>,
    delete: async (input) =>
      run(() => {
        const row = find(input.model, input.where)[0];
        if (row !== undefined)
          store.delete("local-auth", input.model, String(row.id));
      }),
    deleteMany: async (input) =>
      run(() => {
        const selected = find(input.model, input.where);
        for (const row of selected)
          store.delete("local-auth", input.model, String(row.id));
        return selected.length;
      }),
    consumeOne: async <T>(input: Parameters<CustomAdapter["consumeOne"]>[0]) =>
      run(() => {
        const row = find(input.model, input.where)[0];
        if (row !== undefined)
          store.delete("local-auth", input.model, String(row.id));
        return row ?? null;
      }) as Promise<T | null>,
    incrementOne: async <T>(
      input: Parameters<CustomAdapter["incrementOne"]>[0],
    ) =>
      run(() => {
        const row = find(input.model, input.where)[0];
        if (row === undefined) return null;
        const updated: Row = { ...row, ...input.set, id: row.id };
        for (const [field, delta] of Object.entries(input.increment)) {
          const name = config.getFieldName({ model: input.model, field });
          const old = row[name] ?? 0;
          if (
            typeof old !== "number" ||
            !Number.isSafeInteger(old) ||
            !Number.isSafeInteger(delta) ||
            !Number.isSafeInteger(old + delta)
          )
            throw new TypeError("Local auth counters require safe integers.");
          updated[name] = old + delta;
        }
        return save(input.model, updated);
      }) as Promise<T | null>,
    count: async (input) => run(() => find(input.model, input.where).length),
    createSchema: async () =>
      run(() => ({ code: "local_sqlite_ready", path: store.databasePath })),
  };
}

/** A caught operation failure still poisons the transaction; escaped calls refuse. */
async function serializedCallback<T>(
  adapter: DBAdapter<BetterAuthOptions>,
  callback: (
    adapter: Omit<DBAdapter<BetterAuthOptions>, "transaction">,
  ) => Promise<T>,
): Promise<T> {
  let queue = Promise.resolve();
  let closed = false;
  let failure: unknown;
  let failed = false;
  let count = 0;
  const poison = (error: unknown) => {
    if (!failed) failure = error;
    failed = true;
  };
  const scoped = new Proxy(adapter, {
    get(target, property, receiver) {
      if (property === "transaction") return undefined;
      const method: unknown = Reflect.get(target, property, receiver);
      if (typeof method !== "function") return method;
      return (...args: unknown[]) => {
        if (closed)
          return Promise.reject(
            new Error("Local auth transaction callback has settled."),
          );
        if (++count > 100) {
          const error = new Error(
            "Local auth transaction exceeds 100 operations.",
          );
          poison(error);
          return Promise.reject(error);
        }
        const operation = queue.then(() => {
          if (failed) throw failure;
          return Reflect.apply(method, target, args) as unknown;
        });
        queue = operation.then(() => undefined, poison);
        return operation;
      };
    },
  });
  let value: T | undefined;
  try {
    value = await callback(scoped);
  } catch (error) {
    poison(error);
  } finally {
    closed = true;
    await queue;
  }
  if (failed) throw failure;
  return value as T;
}

export class LocalAuthConstraintError extends Error {
  readonly code = "unique_conflict";
  constructor() {
    super("A unique Better Auth record already exists.");
    this.name = "LocalAuthConstraintError";
  }
}
function bounded(value: number, maximum: number): number {
  if (!Number.isSafeInteger(value) || value < 0 || value > maximum)
    throw new TypeError("Local auth pagination exceeds its bound.");
  return value;
}
function jsonRow(input: unknown): Row {
  if (input === null || typeof input !== "object" || Array.isArray(input))
    throw new TypeError("Auth records require objects.");
  // Official factory handles Date conversion; undefined optional fields are omitted.
  return JSON.parse(JSON.stringify(input)) as Row;
}
function equal(left: unknown, right: unknown): boolean {
  return JSON.stringify(left ?? null) === JSON.stringify(right ?? null);
}
function compare(left: unknown, right: unknown): number {
  if (equal(left, right)) return 0;
  if (typeof left === "number" && typeof right === "number")
    return left < right ? -1 : 1;
  return String(left ?? "") < String(right ?? "") ? -1 : 1;
}
function matches(row: Row, clauses: readonly CleanedWhere[]): boolean {
  let group = true;
  let result = false;
  for (const [index, clause] of clauses.entries()) {
    if (index > 0 && clause.connector === "OR") {
      result ||= group;
      group = true;
    }
    let left = row[clause.field] ?? null;
    let right: unknown = clause.value;
    if (clause.mode === "insensitive") {
      if (typeof left === "string") left = left.toLowerCase();
      right = Array.isArray(right)
        ? right.map((value) =>
            typeof value === "string" ? value.toLowerCase() : value,
          )
        : typeof right === "string"
          ? right.toLowerCase()
          : right;
    }
    let matched: boolean;
    switch (clause.operator ?? "eq") {
      case "eq":
        matched = equal(left, right);
        break;
      case "ne":
        matched = !equal(left, right);
        break;
      case "lt":
        matched = left !== null && right !== null && compare(left, right) < 0;
        break;
      case "lte":
        matched = left !== null && right !== null && compare(left, right) <= 0;
        break;
      case "gt":
        matched = left !== null && right !== null && compare(left, right) > 0;
        break;
      case "gte":
        matched = left !== null && right !== null && compare(left, right) >= 0;
        break;
      case "in":
        matched =
          Array.isArray(right) && right.some((value) => equal(left, value));
        break;
      case "not_in":
        matched =
          Array.isArray(right) && !right.some((value) => equal(left, value));
        break;
      case "contains":
        matched =
          typeof left === "string" &&
          typeof right === "string" &&
          left.includes(right);
        break;
      case "starts_with":
        matched =
          typeof left === "string" &&
          typeof right === "string" &&
          left.startsWith(right);
        break;
      case "ends_with":
        matched =
          typeof left === "string" &&
          typeof right === "string" &&
          left.endsWith(right);
        break;
    }
    group &&= matched;
  }
  return result || group;
}
