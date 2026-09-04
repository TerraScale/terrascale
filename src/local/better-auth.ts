import type { BetterAuthDatabaseOperations, BetterAuthWhere } from "../better-auth.js";
import { ok } from "../result.js";
import type { TerraScaleSqliteStore } from "./store.js";

type Row = Record<string, unknown>;
const headers = new Headers({ "x-terrascale-serving-region": "local" });

export function createLocalBetterAuthDatabaseOperations(
  store: TerraScaleSqliteStore,
): BetterAuthDatabaseOperations {
  const success = <T>(operation: string, value: T) => ok(`local.better-auth.${operation}`, 200, headers, value);
  const rows = (model: string): Row[] => store.list<Row>("better-auth", model);
  const save = (model: string, row: Row): Row => {
    const id = typeof row.id === "string" && row.id.length > 0 ? row.id : store.nextUlid();
    const saved = { ...row, id };
    store.put("better-auth", model, id, saved);
    return saved;
  };
  const find = (model: string, where: readonly BetterAuthWhere[]) =>
    rows(model).filter((row) => matches(row, where));

  const operations = {
    async insertUnique(input: any) {
      const candidate = { ...input.data } as Row;
      const existing = rows(input.model);
      const duplicate = existing.some((row) =>
        (candidate.id !== undefined && row.id === candidate.id) ||
        (typeof candidate.email === "string" && row.email === candidate.email) ||
        (typeof candidate.token === "string" && row.token === candidate.token) ||
        (candidate.providerId !== undefined && candidate.accountId !== undefined &&
          row.providerId === candidate.providerId && row.accountId === candidate.accountId));
      if (duplicate) {
        return {
          ok: false,
          routeName: "local.better-auth.insertUnique",
          status: 409,
          headers,
          error: { kind: "conflict", code: "unique_conflict", message: "A unique Better Auth record already exists." },
        };
      }
      return success("insertUnique", select(save(input.model, candidate), input.select));
    },
    async findOne(input: any) {
      const row = find(input.model, input.where)[0] ?? null;
      return success(
        "findOne",
        row === null ? null : withJoins(input.model, select(row, input.select), input.join),
      );
    },
    async findPage(input: any) {
      let selected = find(input.model, input.where);
      if (input.sortBy !== undefined) selected.sort((a, b) => compare(a[input.sortBy!.field], b[input.sortBy!.field]) * (input.sortBy!.direction === "desc" ? -1 : 1));
      selected = selected.slice(input.offset ?? 0, (input.offset ?? 0) + input.limit);
      return success(
        "findPage",
        selected.map((row) => withJoins(input.model, select(row, input.select), input.join)),
      );
    },
    async count(input: any) {
      return success("count", find(input.model, input.where).length);
    },
    async compareAndSwap(input: any) {
      const row = find(input.model, input.where)[0];
      if (row === undefined) return success("compareAndSwap", null);
      return success("compareAndSwap", save(input.model, { ...row, ...input.update }));
    },
    async updateMany(input: any) {
      const selected = find(input.model, input.where);
      for (const row of selected) save(input.model, { ...row, ...input.update });
      return success("updateMany", selected.length);
    },
    async delete(input: any) {
      for (const row of find(input.model, input.where)) store.delete("better-auth", input.model, String(row.id));
      return success("delete", undefined);
    },
    async deleteMany(input: any) {
      const selected = find(input.model, input.where);
      for (const row of selected) store.delete("better-auth", input.model, String(row.id));
      return success("deleteMany", selected.length);
    },
    async consumeOne(input: any) {
      const row = find(input.model, input.where)[0];
      if (row !== undefined) store.delete("better-auth", input.model, String(row.id));
      return success("consumeOne", row ?? null);
    },
    async incrementOne(input: any) {
      const row = find(input.model, input.where)[0];
      if (row === undefined) return success("incrementOne", null);
      const updated: Row = { ...row, ...(input.set ?? {}) };
      for (const [field, amount] of Object.entries(input.increment)) updated[field] = Number(row[field] ?? 0) + Number(amount);
      return success("incrementOne", save(input.model, updated));
    },
    async transaction(callback: any) {
      return callback(operationsWithoutTransaction(operations));
    },
    async migrate() {
      return { code: "local_sqlite_ready", path: "better-auth" };
    },
  } as BetterAuthDatabaseOperations;

  function withJoins(model: string, row: Row, join: Record<string, boolean | { limit?: number }> | undefined): Row {
    if (join === undefined) return row;
    const joined = { ...row };
    for (const [relatedModel, setting] of Object.entries(join)) {
      if (setting === false) continue;
      const outboundId = row[`${relatedModel}Id`];
      if (typeof outboundId === "string") {
        joined[relatedModel] = rows(relatedModel).find((candidate) => candidate.id === outboundId) ?? null;
        continue;
      }
      const foreignKey = `${model}Id`;
      const limit = typeof setting === "object" ? setting.limit : undefined;
      const related = rows(relatedModel).filter(
        (candidate) => candidate[foreignKey] === row.id,
      );
      joined[relatedModel] = limit === undefined ? related : related.slice(0, limit);
    }
    return joined;
  }

  return operations;
}

export async function handleLocalBetterAuthRequest(
  store: TerraScaleSqliteStore,
  action: string,
  input: unknown,
): Promise<unknown> {
  const operations = createLocalBetterAuthDatabaseOperations(store);
  const body = input as never;
  switch (action) {
    case "insertUnique": return operations.insertUnique(body);
    case "findOne": return operations.findOne(body);
    case "findPage": return operations.findPage(body);
    case "count": return operations.count(body);
    case "compareAndSwap": return operations.compareAndSwap(body);
    case "updateMany": return operations.updateMany(body);
    case "delete": return operations.delete(body);
    case "deleteMany": return operations.deleteMany(body);
    case "consumeOne": return operations.consumeOne(body);
    case "incrementOne": return operations.incrementOne(body);
    case "migrate": return operations.migrate?.(body);
    default: throw new TypeError(`Unsupported Better Auth operation: ${action}`);
  }
}

function operationsWithoutTransaction(operations: BetterAuthDatabaseOperations) {
  const { transaction: _transaction, ...rest } = operations;
  return rest;
}

function select(row: Row, fields: readonly string[] | undefined): Row {
  if (fields === undefined) return row;
  return Object.fromEntries(fields.filter((field) => field in row).map((field) => [field, row[field]]));
}

function matches(row: Row, where: readonly BetterAuthWhere[]): boolean {
  if (where.length === 0) return true;
  let result = test(row, where[0]!);
  for (let index = 1; index < where.length; index += 1) {
    const clause = where[index]!;
    result = clause.connector === "OR" ? result || test(row, clause) : result && test(row, clause);
  }
  return result;
}

function test(row: Row, where: BetterAuthWhere): boolean {
  const left = row[where.field];
  const right = where.value;
  const normalize = (value: unknown) => value instanceof Date ? value.toISOString() : value;
  const a = normalize(left);
  const b = normalize(right);
  switch (where.operator ?? "eq") {
    case "eq": return a === b;
    case "ne": return a !== b;
    case "lt": return compare(a, b) < 0;
    case "lte": return compare(a, b) <= 0;
    case "gt": return compare(a, b) > 0;
    case "gte": return compare(a, b) >= 0;
    case "in": return Array.isArray(b) && b.includes(a as never);
    case "not_in": return Array.isArray(b) && !b.includes(a as never);
    case "contains": return String(a ?? "").includes(String(b ?? ""));
    case "starts_with": return String(a ?? "").startsWith(String(b ?? ""));
    case "ends_with": return String(a ?? "").endsWith(String(b ?? ""));
  }
}

function compare(left: unknown, right: unknown): number {
  if (left === right) return 0;
  return String(left ?? "") < String(right ?? "") ? -1 : 1;
}
