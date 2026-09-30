/**
 * Pure parameterized SQL construction. Listener capabilities are checked by the PostgreSQL transport.
 * This module performs no execution and grants no server SQL capabilities.
 *
 * The typed builder accepts schema field descriptors, not SQL fragments. All
 * values (including LIMIT/OFFSET values) are kept separately from the SQL
 * statement and are sent as positional parameters.
 */

import {
  FieldTypes,
  type CollectionSchema,
  type FieldDef,
  type FieldType,
} from "./schema.js";

export type SqlFieldDef = Pick<FieldDef, "name" | "type"> &
  Partial<Omit<FieldDef, "name" | "type">>;
export type SqlCollectionSchema = {
  readonly name: string;
  readonly fields: readonly SqlFieldDef[];
} & Partial<Omit<CollectionSchema, "name" | "fields">>;
export type SchemaField<S extends SqlCollectionSchema> = S["fields"][number];
export type QueryField<S extends SqlCollectionSchema> = SchemaField<S>;

export const Direction = Object.freeze({
  Asc: "ASC",
  Desc: "DESC",
} as const);

export type Direction = (typeof Direction)[keyof typeof Direction];
export type SqlScalar = boolean | bigint | number | string | Uint8Array | null;
export type SqlParameter = SqlScalar;

export class SqlValidationError extends Error {
  public override readonly name = "SqlValidationError";
  public readonly code = "invalid_sql" as const;

  public constructor(message: string) {
    super(`terrascale SQL: ${message}`);
  }
}

export type QueryDescriptor = {
  readonly statement: string;
  readonly parameters: readonly SqlParameter[];
};

function freezeParameters(
  parameters: readonly SqlParameter[],
): readonly SqlParameter[] {
  return Object.freeze(
    parameters.map((parameter) => cloneParameter(parameter)),
  );
}

function cloneParameter(parameter: SqlParameter): SqlParameter {
  return parameter instanceof Uint8Array ? parameter.slice() : parameter;
}

/** A validated, parameterized read-only SQL statement. */
export class Query {
  public readonly statement: string;
  readonly #parameters: readonly SqlParameter[];
  public get parameters(): readonly SqlParameter[] {
    return freezeParameters(this.#parameters);
  }

  private constructor(sql: string, parameters: readonly SqlParameter[]) {
    validateSql(sql, parameters);
    this.statement = sql;
    this.#parameters = freezeParameters(parameters);
    Object.freeze(this);
  }

  public static raw(
    sql: string,
    parameters: readonly SqlParameter[] = [],
  ): Query {
    return new Query(sql, parameters);
  }

  public static select(columns: readonly string[] = ["*"]): SelectBuilder {
    return SelectBuilder.fromColumns(columns);
  }

  public static selectSchema<S extends SqlCollectionSchema>(
    schema: S,
  ): SelectBuilder<S> {
    return SelectBuilder.fromSchema(schema);
  }

  public toDescriptor(): QueryDescriptor {
    return Object.freeze({
      statement: this.statement,
      parameters: this.parameters,
    });
  }
}

type AnySchema = SqlCollectionSchema;

/** Schema-bound SELECT builder. */
export class SelectBuilder<S extends SqlCollectionSchema = AnySchema> {
  readonly #columns: readonly string[];
  readonly #from: string | undefined;
  readonly #schema: S | undefined;
  readonly #predicates: readonly string[];
  readonly #order: readonly (readonly [string, Direction])[];
  readonly #limit: number | undefined;
  readonly #offset: number | undefined;
  readonly #parameters: readonly SqlParameter[];
  readonly #schemaBound: boolean;

  private constructor(input: {
    columns: readonly string[];
    from?: string | undefined;
    schema?: S | undefined;
    predicates?: readonly string[];
    order?: readonly (readonly [string, Direction])[];
    limit?: number | undefined;
    offset?: number | undefined;
    parameters?: readonly SqlParameter[];
    schemaBound?: boolean;
  }) {
    this.#columns = Object.freeze([...input.columns]);
    this.#from = input.from;
    this.#schema = input.schema;
    this.#predicates = Object.freeze([...(input.predicates ?? [])]);
    this.#order = input.order ?? [];
    this.#limit = input.limit;
    this.#offset = input.offset;
    this.#parameters = freezeParameters(input.parameters ?? []);
    this.#schemaBound = input.schemaBound ?? false;
  }

  private copy(
    changes: Partial<{
      columns: readonly string[];
      from: string | undefined;
      predicates: readonly string[];
      order: readonly (readonly [string, Direction])[];
      limit: number | undefined;
      offset: number | undefined;
      parameters: readonly SqlParameter[];
    }>,
  ): SelectBuilder<S> {
    return new SelectBuilder<S>({
      columns: changes.columns ?? this.#columns,
      from: changes.from === undefined ? this.#from : changes.from,
      schema: this.#schema,
      predicates: changes.predicates ?? this.#predicates,
      order: changes.order ?? this.#order,
      limit: changes.limit === undefined ? this.#limit : changes.limit,
      offset: changes.offset === undefined ? this.#offset : changes.offset,
      parameters: changes.parameters ?? this.#parameters,
      schemaBound: this.#schemaBound,
    });
  }

  public static fromColumns(columns: readonly string[]): SelectBuilder {
    return new SelectBuilder({ columns: [...columns] });
  }

  public static fromSchema<S extends SqlCollectionSchema>(
    schema: S,
  ): SelectBuilder<S> {
    validateIdentifier(schema.name, "collection");
    if (!Array.isArray(schema.fields))
      throw new SqlValidationError("schema fields must be an array");
    const names = new Set<string>();
    for (const field of schema.fields) {
      validateField(field, "schema field");
      if (names.has(field.name))
        throw new SqlValidationError(`duplicate schema field '${field.name}'`);
      names.add(field.name);
    }
    return new SelectBuilder<S>({
      columns: ["*"],
      from: schema.name,
      schema: Object.freeze({
        ...schema,
        fields: Object.freeze(
          schema.fields.map((field) => Object.freeze({ ...field })),
        ),
      }) as S,
      schemaBound: true,
    });
  }

  public column(field: QueryField<S>): SelectBuilder<S> {
    const name = this.checkedField(field, "column");
    return this.copy({
      columns: [
        ...(this.#columns.length === 1 && this.#columns[0] === "*"
          ? []
          : this.#columns),
        name,
      ],
    });
  }

  public columns(fields: readonly QueryField<S>[]): SelectBuilder<S> {
    let builder: SelectBuilder<S> = this;
    for (const field of fields) builder = builder.column(field);
    return builder;
  }

  public from(collection: string): SelectBuilder<S> {
    const valid = validateIdentifier(collection, "collection");
    if (this.#schemaBound && valid !== this.#from) {
      throw new SqlValidationError(
        "a schema-bound query cannot change its collection",
      );
    }
    return this.copy({ from: valid });
  }

  public whereEq<T extends SqlParameter>(
    field: QueryField<S>,
    value: T,
  ): SelectBuilder<S> {
    return this.comparison(field, "=", value);
  }

  public whereNe<T extends SqlParameter>(
    field: QueryField<S>,
    value: T,
  ): SelectBuilder<S> {
    return this.comparison(field, "<>", value);
  }

  public whereGt<T extends SqlParameter>(
    field: QueryField<S>,
    value: T,
  ): SelectBuilder<S> {
    return this.comparison(field, ">", value);
  }

  public whereGte<T extends SqlParameter>(
    field: QueryField<S>,
    value: T,
  ): SelectBuilder<S> {
    return this.comparison(field, ">=", value);
  }

  public whereLt<T extends SqlParameter>(
    field: QueryField<S>,
    value: T,
  ): SelectBuilder<S> {
    return this.comparison(field, "<", value);
  }

  public whereLte<T extends SqlParameter>(
    field: QueryField<S>,
    value: T,
  ): SelectBuilder<S> {
    return this.comparison(field, "<=", value);
  }

  public whereLike<T extends SqlParameter>(
    field: QueryField<S>,
    value: T,
  ): SelectBuilder<S> {
    return this.comparison(field, "LIKE", value);
  }

  public whereIn<T extends SqlParameter>(
    field: QueryField<S>,
    values: readonly T[],
  ): SelectBuilder<S> {
    if (values.length === 0)
      throw new SqlValidationError("IN requires at least one value");
    const name = this.checkedField(field, "predicate");
    const placeholders = Array.from(
      { length: values.length },
      (_, index) => `$${this.#parameters.length + index + 1}`,
    );
    return this.copy({
      predicates: [
        ...this.#predicates,
        `${name} IN (${placeholders.join(", ")})`,
      ],
      parameters: [...this.#parameters, ...values],
    });
  }

  public orderByField(
    field: SchemaField<S>,
    direction: Direction = Direction.Asc,
  ): SelectBuilder<S> {
    return this.orderBy(this.checkedField(field, "order_by"), direction);
  }

  public orderBy(
    column: string,
    direction: Direction = Direction.Asc,
  ): SelectBuilder<S> {
    if (direction !== Direction.Asc && direction !== Direction.Desc) {
      throw new SqlValidationError("ORDER BY direction must be ASC or DESC");
    }
    const valid = this.#schemaBound
      ? this.checkedFieldName(column, "order_by")
      : validateIdentifier(column, "order_by");
    return this.copy({ order: [...this.#order, [valid, direction]] });
  }

  public limit(value: number): SelectBuilder<S> {
    return this.paginationValue(value, "limit", { limit: value });
  }

  public offset(value: number): SelectBuilder<S> {
    return this.paginationValue(value, "offset", { offset: value });
  }

  public paginate(options: {
    readonly limit: number;
    readonly offset?: number | undefined;
  }): SelectBuilder<S> {
    const withLimit = this.limit(options.limit);
    return options.offset === undefined
      ? withLimit
      : withLimit.offset(options.offset);
  }

  public page(page: number, pageSize: number): SelectBuilder<S> {
    if (!Number.isSafeInteger(page) || page < 1)
      throw new SqlValidationError("page must be a positive integer");
    const result = this.limit(pageSize);
    return result.offset((page - 1) * pageSize);
  }

  public build(): Query {
    if (this.#from === undefined)
      throw new SqlValidationError("a query requires .from(collection)");
    if (this.#columns.length === 0)
      throw new SqlValidationError(
        "a query requires at least one selected column",
      );
    const columns = this.#columns.map(validateProjection);
    const parts = [
      `SELECT ${columns.join(", ")} FROM ${validateIdentifier(this.#from, "collection")}`,
    ];
    if (this.#predicates.length > 0)
      parts.push(`WHERE ${this.#predicates.join(" AND ")}`);
    if (this.#order.length > 0) {
      parts.push(
        `ORDER BY ${this.#order.map(([column, direction]) => `${column} ${direction}`).join(", ")}`,
      );
    }
    const parameters = [...this.#parameters];
    if (this.#limit !== undefined) {
      parts.push(`LIMIT $${parameters.length + 1}`);
      parameters.push(this.#limit);
    }
    if (this.#offset !== undefined) {
      parts.push(`OFFSET $${parameters.length + 1}`);
      parameters.push(this.#offset);
    }
    return Query.raw(parts.join(" "), parameters);
  }

  public toDescriptor(): QueryDescriptor {
    return this.build().toDescriptor();
  }

  private comparison<T extends SqlParameter>(
    field: SchemaField<S>,
    operator: ComparisonOperator,
    value: T,
  ): SelectBuilder<S> {
    return this.comparisonExpression(
      this.checkedField(field, "predicate"),
      operator,
      value,
    );
  }

  private comparisonExpression<T extends SqlParameter>(
    expression: string,
    operator: ComparisonOperator,
    value: T,
  ): SelectBuilder<S> {
    if (!COMPARISON_OPERATORS.has(operator))
      throw new SqlValidationError("unsupported SQL comparison operator");
    const placeholder = `$${this.#parameters.length + 1}`;
    return this.copy({
      predicates: [
        ...this.#predicates,
        `${expression} ${operator} ${placeholder}`,
      ],
      parameters: [...this.#parameters, value],
    });
  }

  private paginationValue(
    value: number,
    label: string,
    pagination: { readonly limit?: number; readonly offset?: number },
  ): SelectBuilder<S> {
    if (!Number.isSafeInteger(value) || value < 0)
      throw new SqlValidationError(
        `${label} must be a non-negative safe integer`,
      );
    return this.copy({
      limit: pagination.limit === undefined ? this.#limit : pagination.limit,
      offset:
        pagination.offset === undefined ? this.#offset : pagination.offset,
    });
  }

  private checkedField(field: QueryField<S>, context: string): string {
    return this.checkedFieldDef(field, context).name;
  }

  private checkedFieldDef(field: QueryField<S>, context: string): SqlFieldDef {
    if (!field || typeof field !== "object")
      throw new SqlValidationError(`${context} field is invalid`);
    const name = validateIdentifier(field.name, context);
    if (!this.#schemaBound || this.#schema === undefined)
      throw new SqlValidationError(
        "use a schema-bound SELECT before passing field descriptors",
      );
    const declared = this.#schema.fields.find(
      (candidate) => candidate.name === name,
    );
    if (declared === undefined || fieldType(declared) !== fieldType(field)) {
      throw new SqlValidationError(
        `${context} field '${name}' is not declared by this collection`,
      );
    }
    return declared;
  }

  private checkedFieldName(name: string, context: string): string {
    validateIdentifier(name, context);
    if (
      !this.#schemaBound ||
      this.#schema === undefined ||
      !this.#schema.fields.some((field) => field.name === name)
    ) {
      throw new SqlValidationError(
        `${context} field '${name}' is not declared by this collection`,
      );
    }
    return name;
  }
}

export function select<S extends SqlCollectionSchema>(
  schema: S,
): SelectBuilder<S>;
export function select(columns?: readonly string[]): SelectBuilder;
export function select(
  input: SqlCollectionSchema | readonly string[] = ["*"],
): SelectBuilder {
  if (Array.isArray(input)) return SelectBuilder.fromColumns(input);
  return SelectBuilder.fromSchema(input as CollectionSchema);
}

export function selectSchema<S extends SqlCollectionSchema>(
  schema: S,
): SelectBuilder<S> {
  return SelectBuilder.fromSchema(schema);
}

export function raw(
  sql: string,
  parameters: readonly SqlParameter[] = [],
): Query {
  return Query.raw(sql, parameters);
}

type ComparisonOperator =
  | "="
  | "<>"
  | "!="
  | ">"
  | ">="
  | "<"
  | "<="
  | "MATCH"
  | "LIKE";
const COMPARISON_OPERATORS = new Set<string>([
  "=",
  "<>",
  "!=",
  ">",
  ">=",
  "<",
  "<=",
  "MATCH",
  "LIKE",
]);

function fieldType(field: SqlFieldDef): FieldType | undefined {
  return normalizeFieldType(field.type);
}

function normalizeFieldType(value: FieldType | string): FieldType | undefined {
  return Object.values(FieldTypes).includes(value as FieldType)
    ? (value as FieldType)
    : undefined;
}

function validateField(field: SqlFieldDef, context: string): void {
  validateIdentifier(field.name, context);
  if (fieldType(field) === undefined)
    throw new SqlValidationError(
      `${context} '${field.name}' has an unknown type`,
    );
}

function isIdentifier(value: string): boolean {
  return /^[A-Za-z_][A-Za-z0-9_]*$/u.test(value);
}

function validateIdentifier(value: string, context: string): string {
  if (!isIdentifier(value))
    throw new SqlValidationError(
      `${context} must match [A-Za-z_][A-Za-z0-9_]*`,
    );
  return value;
}

function validateProjection(value: string): string {
  if (value.trim() === "*") return "*";
  return validateIdentifier(value, "column");
}

const FORBIDDEN_KEYWORDS = new Set([
  "attach",
  "alter",
  "analyze",
  "begin",
  "commit",
  "create",
  "delete",
  "detach",
  "drop",
  "end",
  "insert",
  "pragma",
  "reindex",
  "replace",
  "rollback",
  "savepoint",
  "transaction",
  "truncate",
  "update",
  "vacuum",
  "upsert",
]);
const FORBIDDEN_FUNCTIONS = new Set([
  "random",
  "randomblob",
  "changes",
  "total_changes",
  "last_insert_rowid",
  "load_extension",
  "readfile",
  "writefile",
  "datetime",
  "date",
  "time",
  "strftime",
  "julianday",
  "unixepoch",
]);

/** Validate the client-side read-only SQL allow-list and positional bindings. */
export function validateSql(
  sql: string,
  parameters: readonly SqlParameter[] = [],
): void {
  if (typeof sql !== "string" || sql.trim() === "")
    throw new SqlValidationError("SQL must not be empty");
  if (new TextEncoder().encode(sql).length > 1_048_576)
    throw new SqlValidationError("SQL exceeds the 1 MiB query text limit");
  for (const parameter of parameters) validateParameter(parameter);
  const tokens = tokenizeSql(sql);
  const first = tokens[0]?.toLowerCase();
  let bodyStart = 1;
  if (first === "select") {
    // normal SELECT
  } else if (first === "with") {
    if (tokens[1]?.toLowerCase() === "recursive")
      throw new SqlValidationError("WITH RECURSIVE is not allowed");
  } else if (first === "explain") {
    if (tokens[1]?.toLowerCase() === "query") {
      if (tokens[2]?.toLowerCase() !== "plan")
        throw new SqlValidationError(
          "EXPLAIN may only use EXPLAIN QUERY PLAN or EXPLAIN SELECT",
        );
      bodyStart = 3;
    }
    if (
      tokens[bodyStart]?.toLowerCase() !== "select" &&
      tokens[bodyStart]?.toLowerCase() !== "with"
    ) {
      throw new SqlValidationError(
        "only EXPLAIN SELECT and EXPLAIN QUERY PLAN SELECT are allowed",
      );
    }
    if (
      tokens[bodyStart]?.toLowerCase() === "with" &&
      tokens[bodyStart + 1]?.toLowerCase() === "recursive"
    ) {
      throw new SqlValidationError("WITH RECURSIVE is not allowed");
    }
  } else {
    throw new SqlValidationError(
      "only SELECT, WITH, and EXPLAIN statements are allowed",
    );
  }
  for (let index = bodyStart; index < tokens.length; index += 1) {
    const token = tokens[index]!.toLowerCase();
    if (FORBIDDEN_KEYWORDS.has(token))
      throw new SqlValidationError(
        "SQL contains a DDL, DML, or transaction keyword",
      );
    if (["current_date", "current_time", "current_timestamp"].includes(token))
      throw new SqlValidationError(
        "non-deterministic SQL functions are not allowed",
      );
    if (FORBIDDEN_FUNCTIONS.has(token) && tokens[index + 1] === "(")
      throw new SqlValidationError("unsafe SQL functions are not allowed");
    if (token.startsWith("sqlite_") || token.startsWith("pragma_"))
      throw new SqlValidationError(
        "internal SQLite names and pragma functions are not allowed",
      );
  }
  validateBindings(tokens, parameters.length);
}

export const validate_sql = validateSql;

function validateParameter(value: unknown): void {
  if (
    value === null ||
    typeof value === "string" ||
    typeof value === "boolean" ||
    value instanceof Uint8Array
  )
    return;
  if (typeof value === "bigint" && value >= -(1n << 63n) && value < 1n << 63n)
    return;
  if (typeof value === "number" && Number.isSafeInteger(value)) return;
  throw new SqlValidationError(
    "SQL parameters require exact i64 integers, strings, booleans, bytes or null; listener support is separate.",
  );
}

function tokenizeSql(sql: string): string[] {
  const tokens: string[] = [];
  const bytes = new TextEncoder().encode(sql);
  let index = 0;
  while (index < bytes.length) {
    const byte = bytes[index]!;
    if (byte === 32 || byte === 9 || byte === 10 || byte === 13) {
      index += 1;
      continue;
    }
    if (byte === 59) {
      if (
        bytes
          .slice(index + 1)
          .some(
            (next) => next !== 32 && next !== 9 && next !== 10 && next !== 13,
          )
      )
        throw new SqlValidationError("multiple SQL statements are not allowed");
      index += 1;
      continue;
    }
    if (
      (byte === 45 && bytes[index + 1] === 45) ||
      (byte === 47 && bytes[index + 1] === 42)
    )
      throw new SqlValidationError("SQL comments are not allowed");
    if (byte === 39 || byte === 34 || byte === 96 || byte === 91) {
      const end = byte === 91 ? 93 : byte;
      index += 1;
      let closed = false;
      while (index < bytes.length) {
        if (bytes[index] === end) {
          if (end !== 93 && bytes[index + 1] === end) {
            index += 2;
            continue;
          }
          index += 1;
          closed = true;
          break;
        }
        index += 1;
      }
      if (!closed)
        throw new SqlValidationError(
          "SQL contains an unterminated quoted value",
        );
      tokens.push("<quoted>");
      continue;
    }
    if (byte === 36) {
      const start = index;
      index += 1;
      while (index < bytes.length && bytes[index]! >= 48 && bytes[index]! <= 57)
        index += 1;
      if (index === start + 1)
        throw new SqlValidationError("only numbered $n parameters are allowed");
      tokens.push(new TextDecoder().decode(bytes.slice(start, index)));
      continue;
    }
    if (byte === 63) {
      const start = index;
      index += 1;
      while (index < bytes.length && bytes[index]! >= 48 && bytes[index]! <= 57)
        index += 1;
      tokens.push(new TextDecoder().decode(bytes.slice(start, index)));
      continue;
    }
    if (
      (byte >= 48 && byte <= 57) ||
      (byte >= 65 && byte <= 90) ||
      (byte >= 97 && byte <= 122) ||
      byte === 95
    ) {
      const start = index;
      index += 1;
      while (
        index < bytes.length &&
        ((bytes[index]! >= 48 && bytes[index]! <= 57) ||
          (bytes[index]! >= 65 && bytes[index]! <= 90) ||
          (bytes[index]! >= 97 && bytes[index]! <= 122) ||
          bytes[index] === 95)
      )
        index += 1;
      tokens.push(new TextDecoder().decode(bytes.slice(start, index)));
      continue;
    }
    tokens.push(new TextDecoder().decode(bytes.slice(index, index + 1)));
    index += 1;
  }
  return tokens;
}

function validateBindings(
  tokens: readonly string[],
  parameterCount: number,
): void {
  let nextUnnamed = 1;
  let highest = 0;
  const seen = new Set<number>();
  for (const token of tokens) {
    let number: number | undefined;
    if (/^\$[0-9]+$/u.test(token)) number = Number(token.slice(1));
    else if (token === "?") {
      number = nextUnnamed;
      nextUnnamed += 1;
    } else if (/^\?[0-9]+$/u.test(token)) number = Number(token.slice(1));
    if (number === undefined) continue;
    if (!Number.isSafeInteger(number) || number < 1)
      throw new SqlValidationError("parameter numbering starts at 1");
    seen.add(number);
    highest = Math.max(highest, number);
  }
  if (highest !== parameterCount || seen.size !== parameterCount)
    throw new SqlValidationError(
      `SQL has ${highest} positional parameter slots but ${parameterCount} values were supplied`,
    );
}
