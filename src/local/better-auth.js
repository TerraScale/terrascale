import { generateId, requireId } from "../ids.js";
import { createAdapterFactory } from "@better-auth/core/db/adapter";
/** @import { CleanedWhere, CustomAdapter, DBAdapter, JoinConfig } from "@better-auth/core/db/adapter" */
/** @import { BetterAuthOptions } from "@better-auth/core" */
/** @import { TerraScaleSqliteStore } from "./store.js" */

/** @typedef {Record<string, unknown>} Row */
/**
 * @typedef {Parameters<
 *   NonNullable<Parameters<typeof createAdapterFactory>[0]["adapter"]>
 * >[0]} FactoryConfig
 */
/**
 * @typedef {(
 *   options: BetterAuthOptions,
 * ) => DBAdapter<BetterAuthOptions>} LocalBetterAuthAdapterFactory
 */

/**
 * Better Auth 1.7.2 over explicitly enabled development SQLite only.
 *
 * @param {TerraScaleSqliteStore} store
 * @returns {LocalBetterAuthAdapterFactory}
 */
export function createLocalBetterAuthAdapter(store) {
  /**
   * @param {BetterAuthOptions} options
   * @param {boolean} scoped
   * @returns {DBAdapter<BetterAuthOptions>}
   */
  function make(options, scoped) {
    if (options.advanced?.database?.generateId === "uuid")
      throw new TypeError("Better Auth entity IDs must be ULIDs; UUID generation is unavailable.");
    /** @type {ReturnType<typeof createAdapterFactory<BetterAuthOptions>>} */
    const factory = createAdapterFactory({
      config: {
        adapterId: "terrascale-local-sqlite",
        supportsDates: false,
        supportsJSON: true,
        supportsArrays: true,
        supportsBooleans: true,
        supportsNumericIds: false,
        customIdGenerator: generateId,
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

/**
 * @param {TerraScaleSqliteStore} store
 * @param {FactoryConfig} config
 * @param {boolean} scoped
 * @returns {CustomAdapter}
 */
function customAdapter(store, config, scoped) {
  /**
   * @param {string} model
   * @returns {Row[]}
   */
  const rows = (model) => store.listNamespace("local-auth")
    .filter(entry => entry.scope === model)
    .map(entry => ({ .../** @type {Row} */ (entry.value), id: requireId(entry.id) }));
  /**
   * @param {string} model
   * @param {readonly CleanedWhere[]} clauses
   * @returns {readonly CleanedWhere[]}
   */
  const where = (model, clauses) =>
    clauses.map((clause) => ({
      ...clause,
      field: config.getFieldName({ model, field: clause.field }),
    }));
  /**
   * @param {string} model
   * @param {readonly CleanedWhere[]} [clauses]
   * @returns {Row[]}
   */
  const find = (model, clauses = []) =>
    rows(model).filter((row) => matches(row, where(model, clauses)));
  /**
   * @param {string} model
   * @returns {readonly (readonly string[])[]}
   */
  const indexes = (model) =>
    /** @type {readonly (readonly string[])[] | undefined} */ (
      store.getMeta(`local-auth-indexes:${model}`)
    ) ?? [["id"]];
  /** @returns {void} */
  const installSchema = () => {
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
  /**
   * @param {string} model
   * @param {Row} candidate
   * @param {readonly (readonly string[])[]} [constraints]
   * @returns {void}
   */
  const ensureUnique = (model, candidate, constraints = indexes(model)) => {
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
  /**
   * @param {string} model
   * @param {Row} input
   * @param {boolean} [creating]
   * @returns {Row}
   */
  const save = (model, input, creating = false) => {
    const row = jsonRow(input);
    const id = row.id === undefined ? generateId() : requireId(row.id);
    for (const [logical, table] of Object.entries(config.schema)) {
      if (config.getModelName(logical) !== model) continue;
      for (const [field, schema] of Object.entries(table.fields)) {
        if (schema.references?.field !== "id") continue;
        const value = row[config.getFieldName({ model: logical, field })];
        if (value !== undefined && value !== null) requireId(value, field);
      }
    }
    const candidate = { ...row, id };
    if (creating && store.get("local-auth", model, id) !== undefined)
      throw new LocalAuthConstraintError();
    ensureUnique(model, candidate);
    delete row.id;
    store.put("local-auth", model, id, row);
    return candidate;
  };
  /**
   * @template T
   * @param {() => T} work
   * @returns {Promise<T>}
   */
  const run = async (work) => {
    const operation = () => {
      installSchema();
      return work();
    };
    return scoped
      ? operation()
      : store.exclusive(() => store.transaction(operation));
  };
  /**
   * @param {string} model
   * @param {readonly Row[]} selected
   * @param {readonly string[] | undefined} select
   * @param {JoinConfig | undefined} join
   * @returns {Row[]}
   */
  const page = (model, selected, select, join) =>
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
    /**
     * @template {Record<string, any>} T
     * @param {Parameters<CustomAdapter["create"]>[0] & { data: T }} input
     */
    create: async (input) =>
      run(() => /** @type {T} */ (save(input.model, input.data, true))),
    /**
     * @template T
     * @param {Parameters<CustomAdapter["update"]>[0] & { update: T }} input
     */
    update: async (input) =>
      /** @type {Promise<T | null>} */ (
        run(() => {
          const row = find(input.model, input.where)[0];
          return row === undefined
            ? null
            : save(input.model, { ...row, ...jsonRow(input.update), id: row.id });
        })
      ),
    updateMany: async (input) =>
      run(() => {
        const selected = find(input.model, input.where);
        for (const row of selected)
          save(input.model, { ...row, ...input.update, id: row.id });
        return selected.length;
      }),
    /**
     * @template T
     * @param {Parameters<CustomAdapter["findOne"]>[0]} input
     */
    findOne: async (input) =>
      /** @type {Promise<T | null>} */ (
        run(
          () =>
            page(
              input.model,
              find(input.model, input.where).slice(0, 1),
              input.select,
              input.join,
            )[0] ?? null,
        )
      ),
    /**
     * @template T
     * @param {Parameters<CustomAdapter["findMany"]>[0]} input
     */
    findMany: async (input) =>
      /** @type {Promise<T[]>} */ (run(() => {
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
      })),
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
    /**
     * @template T
     * @param {Parameters<CustomAdapter["consumeOne"]>[0]} input
     */
    consumeOne: async (input) =>
      /** @type {Promise<T | null>} */ (
        run(() => {
          const row = find(input.model, input.where)[0];
          if (row !== undefined)
            store.delete("local-auth", input.model, String(row.id));
          return row ?? null;
        })
      ),
    /**
     * @template T
     * @param {Parameters<CustomAdapter["incrementOne"]>[0]} input
     */
    incrementOne: async (input) =>
      /** @type {Promise<T | null>} */ (run(() => {
        const row = find(input.model, input.where)[0];
        if (row === undefined) return null;
        /** @type {Row} */
        const updated = { ...row, ...input.set, id: row.id };
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
      })),
    count: async (input) => run(() => find(input.model, input.where).length),
    createSchema: async () =>
      run(() => ({ code: "local_sqlite_ready", path: store.databasePath })),
  };
}

/**
 * A caught operation failure still poisons the transaction; escaped calls refuse.
 *
 * @template T
 * @param {DBAdapter<BetterAuthOptions>} adapter
 * @param {(adapter: Omit<DBAdapter<BetterAuthOptions>, "transaction">) => Promise<T>} callback
 * @returns {Promise<T>}
 */
async function serializedCallback(adapter, callback) {
  let queue = Promise.resolve();
  let closed = false;
  /** @type {unknown} */
  let failure;
  let failed = false;
  let count = 0;
  /** @param {unknown} error */
  const poison = (error) => {
    if (!failed) failure = error;
    failed = true;
  };
  const scoped = new Proxy(adapter, {
    get(target, property, receiver) {
      if (property === "transaction") return undefined;
      /** @type {unknown} */
      const method = Reflect.get(target, property, receiver);
      if (typeof method !== "function") return method;
      /** @param {unknown[]} args */
      return (...args) => {
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
          return /** @type {unknown} */ (Reflect.apply(method, target, args));
        });
        queue = operation.then(() => undefined, poison);
        return operation;
      };
    },
  });
  /** @type {T | undefined} */
  let value;
  try {
    value = await callback(scoped);
  } catch (error) {
    poison(error);
  } finally {
    closed = true;
    await queue;
  }
  if (failed) throw failure;
  return /** @type {T} */ (value);
}

export class LocalAuthConstraintError extends Error {
  /** @readonly */
  code = "unique_conflict";
  constructor() {
    super("A unique Better Auth record already exists.");
    this.name = "LocalAuthConstraintError";
  }
}
/**
 * @param {number} value
 * @param {number} maximum
 * @returns {number}
 */
function bounded(value, maximum) {
  if (!Number.isSafeInteger(value) || value < 0 || value > maximum)
    throw new TypeError("Local auth pagination exceeds its bound.");
  return value;
}
/**
 * @param {unknown} input
 * @returns {Row}
 */
function jsonRow(input) {
  if (input === null || typeof input !== "object" || Array.isArray(input))
    throw new TypeError("Auth records require objects.");
  // Official factory handles Date conversion; undefined optional fields are omitted.
  return /** @type {Row} */ (JSON.parse(JSON.stringify(input)));
}
/**
 * @param {unknown} left
 * @param {unknown} right
 * @returns {boolean}
 */
function equal(left, right) {
  return JSON.stringify(left ?? null) === JSON.stringify(right ?? null);
}
/**
 * @param {unknown} left
 * @param {unknown} right
 * @returns {number}
 */
function compare(left, right) {
  if (equal(left, right)) return 0;
  if (typeof left === "number" && typeof right === "number")
    return left < right ? -1 : 1;
  return String(left ?? "") < String(right ?? "") ? -1 : 1;
}
/**
 * @param {Row} row
 * @param {readonly CleanedWhere[]} clauses
 * @returns {boolean}
 */
function matches(row, clauses) {
  let group = true;
  let result = false;
  for (const [index, clause] of clauses.entries()) {
    if (index > 0 && clause.connector === "OR") {
      result ||= group;
      group = true;
    }
    let left = row[clause.field] ?? null;
    /** @type {unknown} */
    let right = clause.value;
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
    /** @type {boolean} */
    let matched;
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
